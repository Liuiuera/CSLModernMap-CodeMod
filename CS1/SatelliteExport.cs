using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using AlgernonCommons.Translation;
using ColossalFramework;
using CSLModernMap.Exporter;
using UnityEngine;

namespace CSLModernMap.UI
{
    // Uses the existing export task host and pause barrier; Unity access remains on the main thread.
    internal static class SatelliteExport
    {
        private const int Size = 4096;
        private const int WarmupFrames = 8;
        private const float HalfWorld = 8640f;
        private static Operation current;
        private static IEnumerator running;
        private static ExportTaskRunner host;

        private sealed class CaptureLayer
        {
            internal readonly bool HideWater, WaterOnly;
            internal readonly bool ClearBackground;
            internal readonly string Name;
            internal readonly bool Alpha;
            internal CaptureLayer(string name, bool hideWater = false, bool waterOnly = false,
                bool clearBackground = false, bool alpha = false)
            { Name = name; HideWater = hideWater; WaterOnly = waterOnly; ClearBackground = clearBackground; Alpha = alpha; }
        }

        internal static void Begin(string outputDirectory, ExportCompleted completed, ExportProgress progress)
        {
            if (current != null) throw new InvalidOperationException("Satellite capture is already running");
            Operation operation = new Operation(outputDirectory, completed, progress);
            current = operation;
            running = Run(operation);
            host = ExportTaskRunner.Ensure();
            try { host.StartCoroutine(running); }
            catch (Exception error) { operation.Complete(error); }
        }

        internal static void Cancel()
        {
            if (current == null || current.Processing) return;
            // Dispose explicitly: StopCoroutine alone must not own resource cleanup.
            Operation operation = current;
            IEnumerator routine = running;
            if (host != null) host.StopCoroutine(routine);
            ((IDisposable)routine).Dispose();
            operation.Complete(new OperationCanceledException("City unloaded during satellite capture"));
        }

        private static IEnumerator Run(Operation operation)
        {
            IEnumerator steps = operation.Capture();
            Exception failure = null;
            try
            {
                while (true)
                {
                    bool advanced;
                    try { advanced = steps.MoveNext(); }
                    catch (Exception error) { failure = error; break; }
                    if (!advanced) break;
                    yield return steps.Current;
                }
            }
            finally
            {
                ((IDisposable)steps).Dispose();
                operation.Complete(failure ?? (operation.Finished ? null :
                    new OperationCanceledException("Satellite capture stopped")));
            }
        }

        private sealed class Operation
        {
            private readonly string root;
            private readonly ExportProgress progress;
            private ExportSummary summary;
            private Dictionary<string, byte[]> layers = new Dictionary<string, byte[]>();
            private volatile bool mapWritten;
            private Exception mapWriteError;
            private readonly ExportCompleted completed;
            private readonly SimulationCaptureGate gate = new SimulationCaptureGate();
            private SimulationManager simulation;
            private bool previousForcedPause, complete;
            private volatile bool ownsPause;
            private Camera camera;
            private CameraController controller;
            private bool savedCamera, controllerEnabled, cameraEnabled, orthographic, occlusion;
            private float near, far, fieldOfView, orthographicSize, aspect, renderHeight;
            private Rect rect;
            private Vector3 position;
            private Quaternion rotation;
            private RenderingPath renderingPath;
            private Matrix4x4 originalProjection;
            private bool customProjection;
            private DepthTextureMode depthTextureMode;
            private int cullingMask, waterLayer;
            private CameraClearFlags clearFlags;
            private Color backgroundColor;
            private bool transparentWater;
            private bool savedEnvironment;
            private InfoManager.InfoMode infoMode;
            private InfoManager.SubInfoMode infoSubMode;
            private bool notifications, borders, districtNames, propMarkers, disasterMarkers, roadNames;
            private readonly List<Behaviour> effects = new List<Behaviour>();
            private readonly List<bool> effectStates = new List<bool>();
            private RenderTexture originalTarget, target;
            private Texture2D pixels;
            internal bool Finished, Processing;

            internal Operation(string outputDirectory, ExportCompleted completed, ExportProgress progress)
            {
                root = outputDirectory;
                this.completed = completed;
                this.progress = progress;
            }

            private IEnumerator Barrier()
            {
                if (!ownsPause || gate.Cancelled) yield break;
                simulation.ForcedSimulationPaused = true;
                if (!ownsPause || gate.Cancelled)
                {
                    simulation.ForcedSimulationPaused = previousForcedPause;
                    yield break;
                }
                gate.Hold();
            }

            internal IEnumerator Capture()
            {
                Directory.CreateDirectory(root);
                simulation = Singleton<SimulationManager>.instance;
                previousForcedPause = simulation.ForcedSimulationPaused;
                ownsPause = true;
                if (simulation.SimulationPaused || previousForcedPause) gate.StartImmediately();
                else simulation.AddAction(Barrier());
                while (!gate.Ready)
                {
                    if (!CSLModernMapLoadingExtension.IsLevelLoaded)
                        throw new OperationCanceledException("City unloaded before satellite capture");
                    yield return null;
                }

                // 相机与地图数据共用同一暂停窗口，桥梁合成不再配对旧地图。
                CSLModernMapExporter mapExporter = new CSLModernMapExporter { TargetDirectory = root };
                IEnumerator mapCapture = mapExporter.CaptureSteps(simulation.SimulationPaused,
                    delegate(int value, string key) { ExportStages.Report(progress, value * 30 / 100, key); });
                try
                {
                    while (mapCapture.MoveNext()) yield return mapCapture.Current;
                }
                finally { ((IDisposable)mapCapture).Dispose(); }

                IEnumerator images = CaptureImages();
                try
                {
                    while (true)
                    {
                        bool advanced;
                        try { advanced = images.MoveNext(); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            if (!CSLModernMapLoadingExtension.IsLevelLoaded) throw;
                            mapExporter.CapturedSnapshot.SatelliteWarning = Translations.Translate("SATELLITE_CAPTURE_FAILED") + " " + error.Message;
                            Debug.LogWarning("[CSLModernMap] Satellite capture failed: " + error);
                            layers = null;
                            break;
                        }
                        if (!advanced) break;
                        yield return images.Current;
                    }
                }
                finally { ((IDisposable)images).Dispose(); }
                Restore();
                Processing = true;
                mapExporter.SatelliteLayers = layers;
                mapExporter.SatelliteOutputSize = Size;
                System.Threading.Thread writer = new System.Threading.Thread(delegate()
                {
                    try
                    {
                        mapExporter.WriteSnapshot(mapExporter.CapturedSnapshot,
                            delegate(int value, string key) { ExportStages.Report(progress, 70 + value * 30 / 100, key); });
                        summary = mapExporter.Summary;
                    }
                    catch (Exception error) { mapWriteError = error; }
                    finally { mapWritten = true; }
                });
                writer.IsBackground = true; writer.Name = "CSLModernMap Satellite Archive Writer"; writer.Start();
                while (!mapWritten) yield return null;
                if (mapWriteError != null) throw new IOException("Satellite archive write failed", mapWriteError);
                Finished = true;
            }

            private IEnumerator CaptureImages()
            {
                if (SystemInfo.maxTextureSize < Size)
                    throw new NotSupportedException(Size + " capture exceeds GPU maxTextureSize=" + SystemInfo.maxTextureSize);

                camera = Camera.main;
                if (camera == null) throw new InvalidOperationException("Game main camera was not found");
                controller = camera.GetComponent<CameraController>();
                if (controller == null) throw new InvalidOperationException("Main camera has no CameraController");
                position = camera.transform.position; rotation = camera.transform.rotation;
                near = camera.nearClipPlane; far = camera.farClipPlane;
                fieldOfView = camera.fieldOfView; orthographicSize = camera.orthographicSize;
                orthographic = camera.orthographic; aspect = camera.aspect;
                rect = camera.rect; originalTarget = camera.targetTexture;
                cameraEnabled = camera.enabled; controllerEnabled = controller.enabled;
                occlusion = camera.useOcclusionCulling;
                renderingPath = camera.renderingPath;
                originalProjection = camera.projectionMatrix;
                depthTextureMode = camera.depthTextureMode;
                cullingMask = camera.cullingMask;
                clearFlags = camera.clearFlags; backgroundColor = camera.backgroundColor;
                renderHeight = Singleton<RenderManager>.instance.CameraHeight;
                savedCamera = true;
                camera.ResetProjectionMatrix();
                customProjection = !MatricesEqual(originalProjection, camera.projectionMatrix);
                if (customProjection) camera.projectionMatrix = originalProjection;
                SaveEnvironment();

                float minHeight = float.MaxValue, maxHeight = float.MinValue;
                foreach (ushort height in Singleton<TerrainManager>.instance.RawHeights2)
                {
                    float metres = height / 64f;
                    minHeight = Mathf.Min(minHeight, metres); maxHeight = Mathf.Max(maxHeight, metres);
                }
                Building[] buildings = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                foreach (Building building in buildings)
                {
                    if ((building.m_flags & Building.Flags.Created) == 0) continue;
                    BuildingInfo info = building.Info;
                    if (info != null && info.m_generatedInfo != null)
                        maxHeight = Mathf.Max(maxHeight, building.m_position.y + info.m_generatedInfo.m_max.y);
                }
                target = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32);
                if (!target.Create()) throw new InvalidOperationException("Could not allocate capture RenderTexture");
                controller.enabled = false;
                camera.rect = new Rect(0f, 0f, 1f, 1f); camera.aspect = 1f;
                camera.useOcclusionCulling = false;
                camera.targetTexture = target; camera.enabled = true;
                waterLayer = Singleton<TerrainManager>.instance.m_waterLayer;
                if (waterLayer < 0 || waterLayer > 31)
                    throw new InvalidOperationException("Invalid terrain water layer: " + waterLayer);
                if ((cullingMask & (1 << waterLayer)) == 0)
                    throw new InvalidOperationException("Original camera already excludes water layer " + waterLayer);
                var captureLayers = new List<CaptureLayer> {
                    new CaptureLayer("land", hideWater: true),
                    new CaptureLayer("water", waterOnly: true, clearBackground: true, alpha: true) };
                for (int layerIndex = 0; layerIndex < captureLayers.Count; layerIndex++)
                {
                    CaptureLayer layer = captureLayers[layerIndex];
                    for (int effect = 0; effect < effects.Count; effect++) effects[effect].enabled = false;
                    ExportStages.Report(progress, 30 + layerIndex * 40 / captureLayers.Count, "EXPORT_PROGRESS_SATELLITE_CAPTURE");
                    ApplyLayer(layer, minHeight, maxHeight);
                    Matrix4x4 requested = camera.projectionMatrix;
                    IEnumerator capture = CaptureSample(layer.Name, layer.Alpha, requested, camera.cullingMask, 30 + (layerIndex + 1) * 40 / captureLayers.Count - 1);
                    try { while (capture.MoveNext()) yield return capture.Current; }
                    finally { ((IDisposable)capture).Dispose(); }
                    // One decoded image at a time; retain only encoded PNGs until packaging.
                    UnityEngine.Object.Destroy(pixels); pixels = null;
                    yield return null;
                }
            }

            private void ApplyLayer(CaptureLayer layer, float minHeight, float maxHeight)
            {
                camera.ResetProjectionMatrix();
                camera.renderingPath = RenderingPath.DeferredLighting;
                camera.clearFlags = layer.ClearBackground ? CameraClearFlags.SolidColor : clearFlags;
                camera.backgroundColor = layer.ClearBackground ? Color.clear : backgroundColor;
                camera.cullingMask = layer.WaterOnly ? 1 << waterLayer : layer.HideWater ? cullingMask & ~(1 << waterLayer) : cullingMask;
                camera.depthTextureMode = depthTextureMode;
                Singleton<TerrainManager>.instance.TransparentWater = transparentWater;
                float altitude = maxHeight + HalfWorld + 64f;
                camera.transform.position = new Vector3(0f, altitude, 0f);
                camera.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                // Keep deferred lighting: Unity's native orthographic flag switches to Forward.
                camera.orthographic = false;
                camera.orthographicSize = HalfWorld;
                camera.fieldOfView = 90f;
                camera.nearClipPlane = 1f;
                camera.farClipPlane = altitude - minHeight + 1024f;
                Singleton<RenderManager>.instance.CameraHeight = altitude - minHeight;
                camera.projectionMatrix = Matrix4x4.Ortho(-HalfWorld, HalfWorld, -HalfWorld, HalfWorld,
                    camera.nearClipPlane, camera.farClipPlane);
            }

            private static bool MatricesEqual(Matrix4x4 first, Matrix4x4 second)
            {
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++)
                        if (!Mathf.Approximately(first[row, col], second[row, col])) return false;
                return true;
            }

            private IEnumerator CaptureSample(string name, bool alpha, Matrix4x4 requested, int mask, int encodeProgress)
            {
                for (int frame = 0; frame < WarmupFrames; frame++)
                {
                    if (!CSLModernMapLoadingExtension.IsLevelLoaded)
                        throw new OperationCanceledException("City unloaded during satellite capture");
                    yield return new WaitForEndOfFrame();
                }
                if (Singleton<InfoManager>.instance.CurrentMode != InfoManager.InfoMode.None)
                    throw new InvalidOperationException("Info view did not return to normal for satellite capture");
                if (camera.targetTexture != target || camera.cullingMask != mask ||
                    !MatricesEqual(requested, camera.projectionMatrix))
                    throw new InvalidOperationException("Camera changed during satellite layer " + name);
                pixels = new Texture2D(Size, Size, alpha ? TextureFormat.RGBA32 : TextureFormat.RGB24, false);
                RenderTexture previousActive = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
                    pixels.Apply(false, false);
                }
                finally { RenderTexture.active = previousActive; }
                ExportStages.Report(progress, encodeProgress, "EXPORT_PROGRESS_SATELLITE_ENCODE");
                yield return null;
                byte[] encoded = pixels.EncodeToPNG();
                if (encoded == null || encoded.Length == 0) throw new IOException("PNG encoding failed: " + name);
                layers.Add(name, encoded);
            }

            private void SaveEnvironment()
            {
                InfoManager info = Singleton<InfoManager>.instance;
                TerrainManager terrain = Singleton<TerrainManager>.instance;
                transparentWater = terrain.TransparentWater;
                infoMode = info.NextMode; infoSubMode = info.NextSubMode;
                notifications = Singleton<NotificationManager>.instance.NotificationsVisible;
                borders = Singleton<GameAreaManager>.instance.BordersVisible;
                districtNames = Singleton<DistrictManager>.instance.NamesVisible;
                propMarkers = Singleton<PropManager>.instance.MarkersVisible;
                disasterMarkers = Singleton<DisasterManager>.instance.MarkersVisible;
                roadNames = Singleton<NetManager>.instance.RoadNamesVisible;
                foreach (Behaviour behaviour in camera.GetComponents<Behaviour>())
                {
                    string type = behaviour.GetType().FullName;
                    if (type != "FogEffect" && type != "DayNightFogEffect" && type != "TiltShiftEffect" &&
                        type != "UnityStandardAssets.ImageEffects.DepthOfField" &&
                        type != "ColossalFramework.FilmGrainEffect")
                        continue;
                    effects.Add(behaviour); effectStates.Add(behaviour.enabled);
                }
                savedEnvironment = true;
                info.SetCurrentMode(InfoManager.InfoMode.None, InfoManager.SubInfoMode.Default);
                Singleton<NotificationManager>.instance.NotificationsVisible = false;
                Singleton<GameAreaManager>.instance.BordersVisible = false;
                Singleton<DistrictManager>.instance.NamesVisible = false;
                Singleton<PropManager>.instance.MarkersVisible = false;
                Singleton<DisasterManager>.instance.MarkersVisible = false;
                Singleton<NetManager>.instance.RoadNamesVisible = false;
            }

            private void RestoreEnvironment()
            {
                if (!savedEnvironment) return;
                for (int i = 0; i < effects.Count; i++)
                    if (effects[i] != null) effects[i].enabled = effectStates[i];
                Singleton<NotificationManager>.instance.NotificationsVisible = notifications;
                Singleton<GameAreaManager>.instance.BordersVisible = borders;
                Singleton<DistrictManager>.instance.NamesVisible = districtNames;
                Singleton<PropManager>.instance.MarkersVisible = propMarkers;
                Singleton<DisasterManager>.instance.MarkersVisible = disasterMarkers;
                Singleton<NetManager>.instance.RoadNamesVisible = roadNames;
                Singleton<InfoManager>.instance.SetCurrentMode(infoMode, infoSubMode);
                Singleton<TerrainManager>.instance.TransparentWater = transparentWater;
                savedEnvironment = false;
            }

            private void Restore()
            {
                bool restoreCamera = savedCamera && camera != null;
                try
                {
                    if (restoreCamera)
                    {
                        camera.targetTexture = originalTarget;
                        camera.transform.position = position; camera.transform.rotation = rotation;
                        camera.orthographic = orthographic; camera.orthographicSize = orthographicSize;
                        camera.renderingPath = renderingPath;
                        camera.depthTextureMode = depthTextureMode;
                        camera.cullingMask = cullingMask;
                        camera.clearFlags = clearFlags; camera.backgroundColor = backgroundColor;
                        camera.nearClipPlane = near; camera.farClipPlane = far; camera.fieldOfView = fieldOfView;
                        camera.rect = rect; camera.aspect = aspect;
                        camera.ResetProjectionMatrix();
                        if (customProjection) camera.projectionMatrix = originalProjection;
                        camera.useOcclusionCulling = occlusion; camera.enabled = cameraEnabled;
                        Singleton<RenderManager>.instance.CameraHeight = renderHeight;
                    }
                }
                finally
                {
                    savedCamera = false;
                    if (restoreCamera && controller != null) controller.enabled = controllerEnabled;
                    try { RestoreEnvironment(); }
                    finally
                    {
                        if (ownsPause)
                        {
                            ownsPause = false;
                            gate.Release();
                            simulation.ForcedSimulationPaused = previousForcedPause;
                        }
                        if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); target = null; }
                    }
                }
            }

            internal void Complete(Exception error)
            {
                if (complete) return;
                complete = true;
                try { Restore(); }
                finally
                {
                    if (pixels != null) { UnityEngine.Object.Destroy(pixels); pixels = null; }
                    current = null; running = null;
                }
                layers = null;
                completed(summary, error);
            }
        }
    }
}
