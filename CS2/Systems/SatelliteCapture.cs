using System;
using System.Collections;
using System.Diagnostics;
#if CS2_SATELLITE_TRIALS
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
#endif
using Cinemachine;
using Colossal.UI;
using Game.Rendering;
using Game.SceneFlow;
using Game.Simulation;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace CSLModernMap.Systems
{
    internal sealed class SatelliteCapture : IDisposable
    {
        internal const int ImageSize = 4096;
        private const float CaptureFov = 60f;
        private const float MinimumCaptureLod = 0.5f;
        private const int WarmupFrames = 8;
        private const int StableFrames = 6;
        private const int MaxWaitFrames = 150;
        private const double MaxWaitSeconds = 45;

        private readonly Camera camera;
        private readonly HDAdditionalCameraData hdrp;
        private readonly CameraUpdateSystem cameraSystem;
        private readonly RenderingSystem rendering;
        private readonly BatchMeshSystem batchMeshes;
        private readonly IGameCameraController controller;
        private readonly CinemachineBrain brain;
        private readonly UIView view;
        private readonly bool controllerEnabled, brainEnabled, viewEnabled, wasPhysical, wasOrtho, wasOcclusion;
        private readonly bool wasDynamic, wasCustomSettings, originalHideOverlay;
        private readonly float originalAspect, originalNear, originalFar, originalSize, originalFov, originalFocal;
        private readonly float originalLod;
        private readonly RenderTexture originalTarget;
        private readonly Vector3 originalPosition;
        private readonly Quaternion originalRotation;
        private readonly Matrix4x4 originalProjection, originalCulling;
        private readonly HDAdditionalCameraData.AntialiasingMode originalAA;
        private readonly FrameSettings originalSettings;
        private readonly FrameSettingsOverrideMask originalMask;
        private readonly float2 center;
        private readonly float span, seaLevel, terrainMin, terrainMax;
        private bool entered, customAspect, customProjection, customCulling;
        private int renderedFrames, overlayMismatchFrames;
        private RenderTexture target;
        private byte[] png;
        private float CaptureAltitude => Mathf.Max(terrainMax + 1500f, seaLevel + 3800f);

        internal sealed class Image
        {
            internal readonly float4 Bounds;
            internal readonly byte[] Png;

            internal Image(float4 bounds, byte[] png)
            {
                Bounds = bounds;
                Png = png;
            }
        }

#if CS2_SATELLITE_TRIALS
        private string root;
        private BatchDataSystem batchData;
        private BatchManagerSystem batchManager;
        private SatelliteLodTargets targets;
        private JObject report;
        private readonly JArray samples = new JArray();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        internal int FailedSamples { get; private set; }
#endif

        internal SatelliteCapture(World world, GameManager game)
        {
            cameraSystem = world.GetExistingSystemManaged<CameraUpdateSystem>();
            camera = cameraSystem.activeCamera;
            if (camera == null || !camera.isActiveAndEnabled || !camera.TryGetComponent(out hdrp))
                throw new InvalidOperationException("No active HDRP game camera.");
            rendering = world.GetExistingSystemManaged<RenderingSystem>();
            batchMeshes = world.GetExistingSystemManaged<BatchMeshSystem>();
            originalHideOverlay = rendering.hideOverlay;
            originalLod = rendering.levelOfDetail;
            controller = cameraSystem.activeCameraController;
            controllerEnabled = controller != null && controller.controllerEnabled;
            brain = camera.GetComponent<CinemachineBrain>();
            brainEnabled = brain != null && brain.enabled;
            view = game.userInterface.view;
            viewEnabled = view.enabled;

            originalPosition = camera.transform.position;
            originalRotation = camera.transform.rotation;
            originalTarget = camera.targetTexture;
            originalAspect = camera.aspect;
            originalNear = camera.nearClipPlane;
            originalFar = camera.farClipPlane;
            originalSize = camera.orthographicSize;
            originalFov = camera.fieldOfView;
            originalFocal = camera.focalLength;
            originalProjection = camera.projectionMatrix;
            originalCulling = camera.cullingMatrix;
            wasPhysical = camera.usePhysicalProperties;
            wasOrtho = camera.orthographic;
            wasOcclusion = camera.useOcclusionCulling;
            wasDynamic = hdrp.allowDynamicResolution;
            wasCustomSettings = hdrp.customRenderingSettings;
            originalAA = hdrp.antialiasing;
            originalSettings = hdrp.renderingPathCustomFrameSettings;
            originalMask = hdrp.renderingPathCustomFrameSettingsOverrideMask;

            var terrain = world.GetExistingSystemManaged<TerrainSystem>();
            var area = terrain.playableArea;
            var offset = terrain.playableOffset;
            if (!math.all(math.isfinite(area)) || !math.all(math.isfinite(offset))
                || area.x <= 0 || math.abs(area.x - area.y) > 0.01f)
                throw new InvalidOperationException("Satellite capture requires valid square playable bounds.");
            span = area.x;
            center = offset + area * 0.5f;
            var data = terrain.GetHeightData(true);
            if (!data.isCreated || data.heights.Length == 0 || data.scale.y <= 0)
                throw new InvalidOperationException("Terrain elevations are unavailable.");
            ushort min = ushort.MaxValue, max = 0;
            foreach (ushort height in data.heights)
            {
                if (height < min) min = height;
                if (height > max) max = height;
            }
            terrainMin = min / data.scale.y - data.offset.y;
            terrainMax = max / data.scale.y - data.offset.y;
            seaLevel = world.GetExistingSystemManaged<WaterSystem>().SeaLevel;
        }

        internal IEnumerator RunExport()
        {
            Enter();
            // CS2 still uses FOV for LOD selection in an orthographic view.
            return Capture(true, center, CaptureAltitude, ImageSize, CaptureFov,
                Mathf.Max(originalLod, MinimumCaptureLod), true);
        }

        internal Image GetImage() => new Image(WorldBounds(center, span), png);

        private static float4 WorldBounds(float2 at, float width) =>
            new float4(at - width / 2, at + width / 2);

#if CS2_SATELLITE_TRIALS
        internal IEnumerator RunTrials(World world, string trialDirectory, SatelliteLodTargets trialTargets)
        {
            root = trialDirectory;
            targets = trialTargets;
            batchData = world.GetExistingSystemManaged<BatchDataSystem>();
            batchManager = world.GetExistingSystemManaged<BatchManagerSystem>();
            report = new JObject
            {
                ["experiment"] = "native-orthographic-lod-v3",
                ["gameVersion"] = Application.version,
                ["unityVersion"] = Application.unityVersion,
                ["graphics"] = SystemInfo.graphicsDeviceName,
                ["graphicsMemoryMB"] = SystemInfo.graphicsMemorySize,
                ["maxTextureSize"] = SystemInfo.maxTextureSize,
                ["worldBoundsXZ"] = Bounds(center, span),
                ["top"] = "+Z",
                ["right"] = "+X",
                ["seaLevel"] = seaLevel,
                ["terrainMin"] = terrainMin,
                ["terrainMax"] = terrainMax,
                ["originalHideOverlay"] = originalHideOverlay,
                ["originalLevelOfDetail"] = originalLod,
                ["targets"] = targets.Describe(),
                ["samples"] = samples,
            };
            Enter();
            var high = CaptureAltitude;
            var wideFov = 2f * Mathf.Atan2(span / 2f, high - seaLevel) * Mathf.Rad2Deg;
            var lodContrast = originalLod >= 0.999f ? 0.5f : 1f;
            var plans = new List<Sample>
            {
                new Sample("01-baseline-8f", wideFov, originalLod, false),
                new Sample("02-baseline-steady", wideFov, originalLod, true),
                new Sample("03-fov-60", 60f, originalLod, true),
                new Sample("04-fov-restored", wideFov, originalLod, true),
                new Sample("05-lod-contrast", wideFov, lodContrast, true),
                new Sample("06-lod-restored", wideFov, originalLod, true),
            };
            foreach (var target in targets.Targets)
                if (target.InspectNear && target.Entity != Entity.Null)
                    plans.Add(new Sample("near-" + target.ReferenceId, 60f, originalLod, true, target));

            foreach (var plan in plans)
            {
                bool fullMap = plan.Focus == null;
                var sampleCenter = fullMap ? center : plan.Focus.Position.xz;
                var altitude = fullMap ? high : plan.Focus.Position.y + 750f;
                var entry = new JObject
                {
                    ["name"] = plan.Name,
                    ["requestedProjection"] = fullMap ? "Orthographic" : "Perspective",
                    ["requestedHideOverlay"] = true,
                    ["requestedFov"] = plan.Fov,
                    ["requestedLevelOfDetail"] = plan.LevelOfDetail,
                    ["requestedSteady"] = plan.WaitForSteady,
                    ["requestedPixels"] = plan.Pixels,
                    ["cameraY"] = altitude,
                    ["heightAboveSea"] = altitude - seaLevel,
                };
                if (fullMap)
                    entry["worldBoundsXZ"] = Bounds(center, span);
                else
                    entry["referenceId"] = plan.Focus.ReferenceId;
                samples.Add(entry);

                Mod.log.Info("[satellite-trials] " + plan.Name);
                var steps = Capture(fullMap, sampleCenter, altitude, plan.Pixels, plan.Fov,
                    plan.LevelOfDetail, plan.WaitForSteady, entry);
                try
                {
                    while (true)
                    {
                        bool advanced;
                        try { advanced = steps.MoveNext(); }
                        catch (Exception error)
                        {
                            FailedSamples++;
                            entry["error"] = error.Message;
                            Mod.log.Warn("[satellite-trials] " + plan.Name + ": " + error);
                            break;
                        }
                        if (!advanced)
                        {
                            string file = plan.Name + ".png";
                            File.WriteAllBytes(Path.Combine(root, file), png);
                            entry["png"] = file;
                            entry["bytes"] = png.Length;
                            break;
                        }
                        yield return steps.Current;
                    }
                }
                finally
                {
                    (steps as IDisposable)?.Dispose();
                }
                SaveReport();
            }
        }
#endif

        private void Enter()
        {
            entered = true;
            camera.ResetAspect();
            customAspect = !Mathf.Approximately(originalAspect, camera.aspect);
            camera.ResetProjectionMatrix();
            customProjection = originalProjection != camera.projectionMatrix;
            if (customProjection)
                camera.projectionMatrix = originalProjection;
            camera.ResetCullingMatrix();
            customCulling = originalCulling != camera.cullingMatrix;

            if (controller != null)
                controller.controllerEnabled = false;
            if (brainEnabled)
                brain.enabled = false;
            view.enabled = false;
            camera.useOcclusionCulling = false;
            camera.usePhysicalProperties = false;
            hdrp.allowDynamicResolution = false;
            hdrp.antialiasing = HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        private IEnumerator Capture(bool orthographic, float2 sampleCenter, float altitude, int size,
            float fov, float lod, bool waitForSteady, JObject entry = null)
        {
            var timer = Stopwatch.StartNew();
            if (size > SystemInfo.maxTextureSize)
                throw new NotSupportedException("Resolution exceeds the GPU texture size limit.");

            if (target == null || target.width != size || !target.IsCreated())
            {
                ReleaseTarget();
                target = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.sRGB);
                if (!target.Create())
                    throw new InvalidOperationException("Unable to allocate the capture render texture.");
            }

            camera.targetTexture = target;
            camera.aspect = 1f;
            var position = new Vector3(sampleCenter.x, altitude, sampleCenter.y);
            camera.transform.SetPositionAndRotation(position, Quaternion.Euler(90, 0, 0));
            camera.orthographic = orthographic;
            camera.orthographicSize = span / 2f;
            camera.nearClipPlane = 1;
            camera.farClipPlane = altitude - Mathf.Min(terrainMin, seaLevel) + 1024;
            camera.fieldOfView = fov;
            camera.ResetProjectionMatrix();
            camera.ResetCullingMatrix();
            var requestedMatrix = camera.projectionMatrix;
            var requestedCulling = camera.cullingMatrix;

            var settings = originalSettings;
            var mask = wasCustomSettings ? originalMask : default;
            Disable(ref settings, ref mask, FrameSettingsField.AtmosphericScattering);
            Disable(ref settings, ref mask, FrameSettingsField.VolumetricClouds);
            Disable(ref settings, ref mask, FrameSettingsField.DepthOfField);
            Disable(ref settings, ref mask, FrameSettingsField.MotionBlur);
            Disable(ref settings, ref mask, FrameSettingsField.PaniniProjection);
            hdrp.customRenderingSettings = true;
            hdrp.renderingPathCustomFrameSettings = settings;
            hdrp.renderingPathCustomFrameSettingsOverrideMask = mask;
            // Same render flag used by Photo Mode, without changing the active tool or input.
            rendering.hideOverlay = true;
            rendering.levelOfDetail = lod;
            renderedFrames = 0;
            overlayMismatchFrames = 0;
            var warmup = Stopwatch.StartNew();
            var frameWait = new WaitForEndOfFrame();
            var history = entry == null ? null : new JArray();
            int stableFrames = 0, previousMeshCount = -1, previousRenderedFrames = 0;
            for (int frame = 1; frame <= MaxWaitFrames; frame++)
            {
                yield return frameWait;
                int meshCount = batchMeshes.loadedMeshCount;
                if (renderedFrames > previousRenderedFrames && batchMeshes.loadingRemaining == 0
                    && meshCount == previousMeshCount
                    && Mathf.Abs(rendering.frameLod - lod) <= 0.0005f)
                    stableFrames++;
                else
                    stableFrames = 0;
                previousMeshCount = meshCount;
                previousRenderedFrames = renderedFrames;
                if (history != null && (frame == 1 || frame % WarmupFrames == 0))
                    history.Add(new JArray(frame, rendering.frameLod, rendering.levelOfDetail,
                        batchMeshes.loadingRemaining, meshCount));
                if (frame >= WarmupFrames && (!waitForSteady || stableFrames >= StableFrames
                    || warmup.Elapsed.TotalSeconds >= MaxWaitSeconds))
                    break;
            }

            var hd = HDCamera.GetOrCreate(camera);
            bool projectionPreserved = camera.projectionMatrix == requestedMatrix;
            bool cullingPreserved = camera.cullingMatrix == requestedCulling;
            bool posePreserved = camera.transform.position == position
                && camera.transform.rotation == Quaternion.Euler(90, 0, 0);
#if CS2_SATELLITE_TRIALS
            if (entry != null)
            {
                entry["cameraFrames"] = renderedFrames;
                entry["warmupMs"] = warmup.ElapsedMilliseconds;
                entry["steadyReached"] = stableFrames >= StableFrames;
                entry["warmupHistoryColumns"] = new JArray("waitFrame", "frameLod", "levelOfDetail",
                    "meshLoadingRemaining", "loadedMeshCount");
                entry["warmupHistory"] = history;
                entry["actualHideOverlay"] = rendering.hideOverlay;
                entry["overlayMismatchFrames"] = overlayMismatchFrames;
                entry["actualSize"] = new JArray(hd.actualWidth, hd.actualHeight);
                entry["actualWater"] = hd.frameSettings.IsEnabled(FrameSettingsField.Water);
                entry["actualAtmosphericScattering"] = hd.frameSettings.IsEnabled(FrameSettingsField.AtmosphericScattering);
                entry["actualVolumetricClouds"] = hd.frameSettings.IsEnabled(FrameSettingsField.VolumetricClouds);
                entry["actualLitShaderMode"] = hd.frameSettings.litShaderMode.ToString();
                entry["actualOrthographic"] = camera.orthographic;
                entry["actualCameraPosition"] = new JArray(camera.transform.position.x, camera.transform.position.y,
                    camera.transform.position.z);
                entry["actualCameraUp"] = new JArray(camera.transform.up.x, camera.transform.up.y, camera.transform.up.z);
                entry["near"] = camera.nearClipPlane;
                entry["far"] = camera.farClipPlane;
                entry["fov"] = camera.fieldOfView;
                entry["projectionMatrix"] = Matrix(camera.projectionMatrix);
                entry["worldToCameraMatrix"] = Matrix(camera.worldToCameraMatrix);
                entry["cullingMatrix"] = Matrix(camera.cullingMatrix);
                entry["projectionPreserved"] = projectionPreserved;
                entry["cullingPreserved"] = cullingPreserved;
                entry["posePreserved"] = posePreserved;
                RecordLod(entry);
            }
#endif
            if (!projectionPreserved || !cullingPreserved || !posePreserved || camera.orthographic != orthographic)
                throw new InvalidOperationException("The game changed the satellite camera projection or position.");
            if (entry == null && stableFrames < StableFrames)
                throw new InvalidOperationException("City rendering did not settle in time for satellite capture.");
            if (renderedFrames == 0 || cameraSystem.activeCamera != camera || camera.targetTexture != target
                || !target.IsCreated())
                throw new InvalidOperationException("The active camera did not render to the capture texture.");
            if (overlayMismatchFrames != 0 || !rendering.hideOverlay)
                throw new InvalidOperationException("The game changed the overlay state during the capture.");
            if (hd.actualWidth != size || hd.actualHeight != size)
                throw new InvalidOperationException("The camera rendered at a different resolution than requested.");

            var pixels = new Texture2D(size, size, TextureFormat.RGB24, false);
            try
            {
                var readback = Stopwatch.StartNew();
                var previousTarget = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
                }
                finally
                {
                    RenderTexture.active = previousTarget;
                }
                if (entry != null)
                    entry["readbackMs"] = readback.ElapsedMilliseconds;
                var encoding = Stopwatch.StartNew();
                png = ImageConversion.EncodeToPNG(pixels);
                if (entry != null)
                    entry["encodingMs"] = encoding.ElapsedMilliseconds;
                if (png == null || png.Length == 0)
                    throw new InvalidOperationException("Unity returned no PNG data.");
            }
            finally
            {
                Object.Destroy(pixels);
            }

            if (entry != null)
                entry["elapsedMs"] = timer.ElapsedMilliseconds;
        }

#if CS2_SATELLITE_TRIALS
        private void RecordLod(JObject entry)
        {
            var active = cameraSystem.activeCameraController;
            entry["activeController"] = active?.GetType().Name;
            entry["activeControllerZoom"] = active != null ? new JValue(active.zoom) : JValue.CreateNull();
            entry["levelOfDetail"] = rendering.levelOfDetail;
            entry["frameLod"] = rendering.frameLod;
            entry["meshLoadingRemaining"] = batchMeshes.loadingRemaining;
            entry["loadedMeshCount"] = batchMeshes.loadedMeshCount;
            entry["meshMemoryUsedBytes"] = batchMeshes.totalSizeInMemory;
            entry["meshMemoryBudgetBytes"] = batchMeshes.memoryBudget;
            entry["strictMeshMemoryBudget"] = batchMeshes.strictMemoryBudget;
            if (!cameraSystem.TryGetLODParameters(out var lod))
            {
                entry["lodParametersUnavailable"] = true;
                return;
            }
            var effective = batchData.GetLevelOfDetail(rendering.frameLod, active);
            var parameters = RenderingUtils.CalculateLodParameters(effective, lod);
            entry["lodParameters"] = new JObject
            {
                ["fieldOfView"] = lod.fieldOfView,
                ["isOrthographic"] = lod.isOrthographic,
                ["orthoSize"] = lod.orthoSize,
                ["cameraPosition"] = SatelliteLodTargets.Vector(lod.cameraPosition),
                ["cameraPixelHeight"] = lod.cameraPixelHeight,
                ["effectiveLevelOfDetail"] = effective,
                ["scale"] = parameters.x,
            };
            var groups = batchManager.GetNativeBatchGroups(readOnly: true, out var groupJobs);
            var loadStates = batchMeshes.GetLoadingState(out var meshJobs);
            groupJobs.Complete();
            meshJobs.Complete();
            var managed = batchManager.GetManagedBatches();
            entry["targets"] = targets.Observe(lod, cameraSystem.activeViewer.forward, parameters, ReadRoadBatches);

            JArray ReadRoadBatches(int groupIndex)
            {
                var rows = new JArray();
                if (groupIndex < 0 || groupIndex >= groups.GetGroupCount())
                    return rows;
                int count = groups.GetBatchCount(groupIndex);
                for (int i = 0; i < count; i++)
                {
                    int index = groups.GetManagedBatchIndex(groupIndex, i);
                    if (index < 0 || index >= loadStates.Length || !(managed.GetBatch(index) is CustomBatch batch))
                        continue;
                    var data = groups.GetBatchData(groupIndex, i);
                    var material = batch.material;
                    var shader = material == null ? null : material.shader;
                    rows.Add(new JObject
                    {
                        ["managedBatchIndex"] = index,
                        ["lodIndex"] = data.m_LodIndex,
                        ["minLod"] = data.m_MinLod,
                        ["virtualTextureIndices"] = new JArray(data.m_VTIndex0, data.m_VTIndex1),
                        ["meshLoadingState"] = loadStates[index].ToString(),
                        ["sourceFlags"] = batch.sourceFlags.ToString(),
                        ["mesh"] = batch.mesh == null ? null : batch.mesh.name,
                        ["material"] = material == null ? null : material.name,
                        ["shader"] = shader == null ? null : shader.name,
                        ["usesDefaultMaterial"] = material != null && material == batch.defaultMaterial,
                        ["defaultMaterial"] = batch.defaultMaterial == null ? null : batch.defaultMaterial.name,
                    });
                }
                return rows;
            }
        }

#endif

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera renderedCamera)
        {
            if (renderedCamera != camera)
                return;
            renderedFrames++;
            if (!rendering.hideOverlay)
                overlayMismatchFrames++;
        }

        private static void Disable(ref FrameSettings settings, ref FrameSettingsOverrideMask mask,
            FrameSettingsField field)
        {
            settings.SetEnabled(field, false);
            mask.mask[(uint)field] = true;
        }

        private void ReleaseTarget()
        {
            if (target == null)
                return;
            camera.targetTexture = originalTarget;
            target.Release();
            Object.Destroy(target);
            target = null;
        }

        public void Dispose()
        {
            if (entered)
            {
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                ReleaseTarget();
                camera.transform.SetPositionAndRotation(originalPosition, originalRotation);
                camera.useOcclusionCulling = wasOcclusion;
                camera.orthographic = wasOrtho;
                camera.orthographicSize = originalSize;
                camera.nearClipPlane = originalNear;
                camera.farClipPlane = originalFar;
                camera.usePhysicalProperties = wasPhysical;
                if (wasPhysical)
                    camera.focalLength = originalFocal;
                else
                    camera.fieldOfView = originalFov;
                camera.ResetAspect();
                if (customAspect)
                    camera.aspect = originalAspect;
                camera.ResetProjectionMatrix();
                if (customProjection)
                    camera.projectionMatrix = originalProjection;
                camera.ResetCullingMatrix();
                if (customCulling)
                    camera.cullingMatrix = originalCulling;
                hdrp.customRenderingSettings = wasCustomSettings;
                hdrp.renderingPathCustomFrameSettings = originalSettings;
                hdrp.renderingPathCustomFrameSettingsOverrideMask = originalMask;
                hdrp.allowDynamicResolution = wasDynamic;
                hdrp.antialiasing = originalAA;
                rendering.hideOverlay = originalHideOverlay;
                rendering.levelOfDetail = originalLod;
                if (controller != null)
                    controller.controllerEnabled = controllerEnabled;
                if (brainEnabled)
                    brain.enabled = true;
                view.enabled = viewEnabled;
                entered = false;
            }

#if CS2_SATELLITE_TRIALS
            if (report == null)
                return;
            report["elapsedMs"] = elapsed.ElapsedMilliseconds;
            report["cameraRestored"] = camera.targetTexture == originalTarget
                && camera.transform.position == originalPosition && camera.transform.rotation == originalRotation
                && camera.orthographic == wasOrtho && view.enabled == viewEnabled;
            report["overlayRestored"] = rendering.hideOverlay == originalHideOverlay;
            report["levelOfDetailRestored"] = rendering.levelOfDetail == originalLod;
            report["failedSamples"] = FailedSamples;
            SaveReport();
#endif
        }

#if CS2_SATELLITE_TRIALS
        private void SaveReport()
        {
            File.WriteAllText(Path.Combine(root, "trials.json"), report.ToString(Formatting.Indented));
        }

        private static JArray Bounds(float2 at, float width)
        {
            var bounds = WorldBounds(at, width);
            return new JArray(bounds.x, bounds.y, bounds.z, bounds.w);
        }

        private static JArray Matrix(Matrix4x4 matrix)
        {
            var result = new JArray();
            for (int row = 0; row < 4; row++)
                result.Add(new JArray(matrix[row, 0], matrix[row, 1], matrix[row, 2], matrix[row, 3]));
            return result;
        }

        private readonly struct Sample
        {
            internal readonly string Name;
            internal readonly float Fov, LevelOfDetail;
            internal readonly bool WaitForSteady;
            internal readonly SatelliteLodTargets.Target Focus;
            internal int Pixels => Focus == null ? ImageSize : 1024;

            internal Sample(string name, float fov, float levelOfDetail, bool waitForSteady,
                SatelliteLodTargets.Target focus = null)
            {
                Name = name;
                Fov = fov;
                LevelOfDetail = levelOfDetail;
                WaitForSteady = waitForSteady;
                Focus = focus;
            }
        }
#endif
    }
}
