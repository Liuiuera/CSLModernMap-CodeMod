using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Colossal.Mathematics;
using Colossal.PSI.Common;
using Game;
using Game.City;
using Game.UI.Localization;
using Game.UI.Menu;
using Game.UI;
using Game.SceneFlow;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace CSLModernMap.Systems
{
    /// <summary>协调地图导出与选项页操作</summary>
    public sealed partial class MapExportUISystem : GameSystemBase
    {
        private const float CellSizeMeters = 8f;

        private static MapExportUISystem s_Instance;

        private bool m_ExportRequested;

        private int m_ExportStartFrame = -1;

        private bool m_OpenAfterExport;

        private bool m_ExportWithSatellite;

        private Task<ExportResult> m_ExportTask;

        private int m_ExportProgress;

        private int m_ShownExportProgress;

        private static volatile bool s_RendererWorkRunning;

        private static volatile string s_RendererError = "";

        private static volatile bool s_RendererInstallationFailed;

        private static string s_PendingRendererNotice = "";

        private static int s_ManualInstallPromptRequested;
        private static string s_RendererRetryMapPath;
        private static string s_ManualHelpDirectory;
        private static string s_ManualHelpError;

        private static bool s_OpenFolderRequested;

        private static bool s_OpenRendererFolderRequested;

        private static bool s_CopyFolderRequested;

        private EntityQuery m_NodeQuery;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_BuildingQuery;
        private EntityQuery m_DistrictQuery;

        private NativeHashMap<Entity, LotSizeEntry> m_LotSizeCache;

        private readonly Dictionary<Entity, string> m_PrefabNameCache = new Dictionary<Entity, string>();

        private readonly Dictionary<string, int> m_RoleCounts = new Dictionary<string, int>();

        private readonly Dictionary<string, int> m_ServiceCounts = new Dictionary<string, int>();

        private readonly Dictionary<string, int> m_SubServiceCounts = new Dictionary<string, int>();

        private readonly Dictionary<Entity, NetCompositionInfo> m_NetCompositionCache =
            new Dictionary<Entity, NetCompositionInfo>();

        private readonly Dictionary<Entity, string> m_RoadSizeClassCache = new Dictionary<Entity, string>();

        private readonly Dictionary<string, int> m_NetworkKindCounts = new Dictionary<string, int>();

        private int m_SkippedAirspaceNetworkCount;

        private int m_SkippedMarkerNetworkCount;

        private int m_CustomRoadNameCount;

        private int m_ExcludedNonBuildingCount;

        private int m_ExcludedNoGeometryCount;

        private int m_CustomBuildingNameCount;

        private readonly HashSet<int> m_ExportedBuildingIds = new HashSet<int>();

        private readonly Dictionary<int, List<int>> m_ExportedBuildingChildren =
            new Dictionary<int, List<int>>();

        private readonly Dictionary<int, float2> m_ExportedBuildingPositions =
            new Dictionary<int, float2>();

        private Game.Prefabs.PrefabSystem m_PrefabSystem;

        private int m_ExtensionCount;

        private int m_UpgradeCount;

        private int m_AttachedLinkCount;

        private int m_UpgradeLinkCount;

        internal static bool RendererBusy => s_RendererWorkRunning;

        internal static string RendererStatus
        {
            get
            {
                if (s_RendererWorkRunning)
                {
                    return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.2CBC968E35");
                }

                if (!string.IsNullOrEmpty(s_RendererError))
                {
                    if (s_RendererInstallationFailed)
                        return string.Format(CultureInfo.InvariantCulture,
                            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.WaitingFailed"), s_RendererError);
                    return (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.A52C7E0D20")) + s_RendererError;
                }

                if (RendererLauncher.InstallationStatus == RendererInstallationStatus.Manual)
                {
                    return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.Installed");
                }

                if (RendererLauncher.InstallationStatus == RendererInstallationStatus.Unsupported)
                {
                    return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.5FC5374073");
                }

                if (RendererLauncher.InstallationStatus == RendererInstallationStatus.MissingPayload)
                {
                    return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.BCF7D8C55D");
                }

                if (RendererLauncher.InstallationStatus == RendererInstallationStatus.Installed)
                {
                    return (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.C9D3F3D2BF")) + RendererLauncher.StatusVersion;
                }

                return RendererLauncher.InstallationStatus == RendererInstallationStatus.Older
                    ? (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.E726AAB194")) + RendererLauncher.StatusVersion
                        + (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.110B58EB74"))
                    : (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RendererStatus.41D3D2CC44"));
            }
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();

            s_Instance = this;

            m_NodeQuery = CreateStableQuery(ComponentType.ReadOnly<Game.Net.Node>());
            m_EdgeQuery = CreateStableQuery(ComponentType.ReadOnly<Game.Net.Edge>());

            m_BuildingQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>()
                },
                Any = new[]
                {
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Buildings.Extension>(),
                    ComponentType.ReadOnly<Game.Buildings.ServiceUpgrade>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Common.Deleted>()
                }
            });

            m_DistrictQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                    ComponentType.ReadOnly<Game.Areas.District>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Common.Deleted>()
                }
            });

            m_PrefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();
        }

        protected override void OnDestroy()
        {
            CancelSatelliteExport();
#if CS2_SATELLITE_TRIALS
            CancelSatelliteTrials();
#endif
            if (ReferenceEquals(s_Instance, this))
            {
                s_Instance = null;
            }

            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            var rendererNotice = Interlocked.Exchange(ref s_PendingRendererNotice, "");
            if (!string.IsNullOrEmpty(rendererNotice))
            {
                ShowNotification(RendererNoticeId, rendererNotice);
            }

            if (Interlocked.Exchange(ref s_ManualInstallPromptRequested, 0) != 0)
            {
                ShowManualInstallPrompt();
            }

            if (s_OpenFolderRequested)
            {
                s_OpenFolderRequested = false;

                ShellFolder.RevealOrCopy(
                    ExportPaths.ExportDirectory,
                    message => ShowNotification(FolderNoticeId, message));
            }

            if (s_OpenRendererFolderRequested)
            {
                s_OpenRendererFolderRequested = false;
                RendererLauncher.OpenRendererDirectory();
            }

            if (s_CopyFolderRequested)
            {
                s_CopyFolderRequested = false;
                ShellFolder.CopyOrComplain(
                    ExportPaths.ExportDirectory,
                    message => ShowNotification(FolderNoticeId, message));
            }

            if (m_ExportTask != null)
            {
                UpdateExport();
                return;
            }

            if (m_ExportStartFrame >= 0)
            {
                if (UnityEngine.Time.frameCount >= m_ExportStartFrame)
                {
                    m_ExportStartFrame = -1;
                    ProcessExport();
                }
                return;
            }

            if (!m_ExportRequested)
            {
                return;
            }

            m_ExportRequested = false;
            // Let the options binding update and render before blocking on city capture.
            m_ExportStartFrame = UnityEngine.Time.frameCount + (m_ExportWithSatellite ? 1 : 2);
            if (!m_ExportWithSatellite)
                MarkExportStarted("CSLModernMap.UI.ExportProgress.CapturingCity");
        }

        private void MarkExportStarted(string statusKey)
        {
            ExportReport.MarkRunning(statusKey);
            ShowNotification(ResultNoticeId, ExportReport.StartNotice, ProgressState.Indeterminate, 0);
        }

        private EntityQuery CreateStableQuery(ComponentType component)
        {
            return GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { component },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Common.Deleted>()
                }
            });
        }

        internal static void RequestExport()
        {
            QueueExport(false);
        }

        internal static void RequestExportAndOpen()
        {
            QueueExport(true);
        }

        private static void QueueExport(bool openAfterwards)
        {
            var system = s_Instance;
            if (system == null)
            {
                Mod.log.Warn("[export] 当前没有已载入的城市，无法执行导出");
                return;
            }

            if (ExportReport.IsBusy)
                return;

            var game = GameManager.instance;
            if (game != null && game.gameMode == GameMode.Game && Mod.Settings.IncludeSatelliteImage)
            {
                system.ShowSatelliteSaveReminder(game, openAfterwards);
                return;
            }

            system.EnqueueExport(openAfterwards, withSatellite: false);
        }

        private void EnqueueExport(bool openAfterwards, bool withSatellite)
        {
            if (openAfterwards && RendererLauncher.HasPayload
                && !RendererLauncher.IsInstalled && RendererLauncher.HasOlderInstallation)
            {
                ShowNotification(RendererStartNoticeId,
                    (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.QueueExport.57CD67E80A"))
                    + RendererLauncher.PayloadVersion
                    + (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.QueueExport.606539105E")));
            }

            m_OpenAfterExport = openAfterwards;
            m_ExportWithSatellite = withSatellite;
            m_ExportRequested = true;
            ExportReport.MarkQueued();
        }

        private static void ShowRendererInstallConfirmation()
        {
            if (!RendererLauncher.IsSupported || s_RendererWorkRunning) return;
            var dialog = new ConfirmationDialog(
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.Confirm")),
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.ConfirmGuide")),
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.CheckAndOpen")),
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.OpenHelp")), Array.Empty<LocalizedString>());
            GameManager.instance.userInterface.appBindings.ShowConfirmationDialog(dialog, result =>
            {
                if (result == 0) CheckAndOpenRenderer();
                else if (result == 1) OpenManualInstallHelp();
            });
        }

        private static void CheckAndOpenRenderer()
        {
            if (s_RendererWorkRunning) return;
            if (RendererLauncher.RefreshInstallationStatus())
            {
                s_RendererError = "";
                s_RendererInstallationFailed = false;
                StartRendererWork(() =>
                {
                    RendererLauncher.Launch(s_RendererRetryMapPath);
                    PostRendererNotice(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.Opened"));
                });
                return;
            }
            PostRendererNotice(RendererStatus);
            var dialog = new ConfirmationDialog(
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.CheckTitle")),
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.NotFound")),
                LocalizedString.Value(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.OpenHelp")),
                LocalizedString.Id("Common.CLOSE"), Array.Empty<LocalizedString>());
            GameManager.instance.userInterface.appBindings.ShowConfirmationDialog(dialog, result =>
            {
                if (result != 0) return;
                OpenManualInstallHelp();
            });
        }

        internal static void RequestInstallRenderer()
        {
            if (!RendererLauncher.IsSupported || s_RendererWorkRunning)
            {
                return;
            }

            s_RendererRetryMapPath = null;

            if (RendererLauncher.HasManualInstallation)
            {
                s_RendererError = "";
                s_RendererInstallationFailed = false;
                RendererLauncher.RefreshInstallationStatus();
                PostRendererNotice(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.Installed"));
                return;
            }

            if (!RendererLauncher.HasPayload)
            {
                s_RendererInstallationFailed = true;
                FailRenderer(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RequestInstallRenderer.01A918F707"));
                Interlocked.Exchange(ref s_ManualInstallPromptRequested, 1);
                return;
            }

            ShowNotificationOnGameThread(RendererStartNoticeId, InstallingRendererNotice);
            StartRendererWork(() =>
            {
                EnsureRendererInstalled(true);
                PostRendererNotice(string.Format(System.Globalization.CultureInfo.InvariantCulture, global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.RequestInstallRenderer.AB6548D24F"), RendererLauncher.PayloadVersion));
            });
        }

        private void OpenInRenderer(string exportPath)
        {
            if (!RendererLauncher.IsSupported)
            {
                return;
            }

            s_RendererRetryMapPath = exportPath;

            if (!RendererLauncher.HasManualInstallation && !RendererLauncher.HasPayload)
            {
                s_RendererInstallationFailed = true;
                FailRenderer(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.OpenInRenderer.01A918F707"));
                Interlocked.Exchange(ref s_ManualInstallPromptRequested, 1);
                return;
            }

            var installing = !RendererLauncher.IsInstalled;
            var updating = installing && RendererLauncher.HasOlderInstallation;
            if (installing)
            {
                ShowNotification(RendererStartNoticeId, updating
                    ? (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.OpenInRenderer.8CB1C7E5AC"))
                        + RendererLauncher.PayloadVersion
                    : InstallingRendererNotice);
            }

            StartRendererWork(() =>
            {
                EnsureRendererInstalled();
                RendererLauncher.Launch(exportPath);
                if (installing)
                {
                    PostRendererNotice(global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.OpenInRenderer.B5717EE887"));
                }
            });
        }

        private static void EnsureRendererInstalled(bool reinstall = false)
        {
            try
            {
                RendererLauncher.EnsureInstalled(reinstall);
            }
            catch
            {
                s_RendererInstallationFailed = true;
                throw;
            }
        }

        private static void ShowManualInstallPrompt()
        {
            s_ManualHelpDirectory = ExportPaths.RendererDirectory;
            s_ManualHelpError = s_RendererError;
            try
            {
                s_ManualHelpDirectory = RendererLauncher.PrepareManualInstallation();
            }
            catch (Exception error)
            {
                Mod.log.Error("[renderer] " + error);
                s_ManualHelpError += "\n\n" + error;
            }
            OpenManualInstallHelp();
        }

        private static void OpenManualInstallHelp()
        {
            try
            {
                RendererLauncher.OpenInstallHelp(s_ManualHelpDirectory, s_ManualHelpError);
                ShowRendererInstallConfirmation();
            }
            catch (Exception error)
            {
                Mod.log.Error("[renderer] " + error);
                ShowNotificationOnGameThread(RendererNoticeId, error.Message);
            }
        }

        private static void StartRendererWork(Action work)
        {
            lock (typeof(MapExportUISystem))
            {
                if (s_RendererWorkRunning)
                {
                    return;
                }

                s_RendererWorkRunning = true;
                s_RendererError = "";
                s_RendererInstallationFailed = false;
            }

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    work();
                    RendererLauncher.RefreshInstallationStatus();
                }
                catch (Exception e)
                {
                    Mod.log.Error("[renderer] 查看器安装/启动失败: " + e);
                    FailRenderer(ExportReport.ShortReason(e));
                }
                finally
                {
                    lock (typeof(MapExportUISystem))
                    {
                        s_RendererWorkRunning = false;
                    }
                    if (s_RendererInstallationFailed)
                        Interlocked.Exchange(ref s_ManualInstallPromptRequested, 1);
                }
            })
            {
                IsBackground = true,
                Name = "CSLModernMap.Renderer",
            };
            thread.Start();
        }

        private static void FailRenderer(string reason)
        {
            s_RendererError = string.IsNullOrEmpty(reason)
                ? (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.FailRenderer.13A46616E1"))
                : reason;
            PostRendererNotice(s_RendererInstallationFailed
                ? string.Format(CultureInfo.InvariantCulture,
                    global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.RendererManual.WaitingFailed"), s_RendererError)
                : (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.FailRenderer.0DE817BA18")) + s_RendererError);
        }

        private static void PostRendererNotice(string message)
        {
            Interlocked.Exchange(ref s_PendingRendererNotice, message ?? "");
        }

        private static string InstallingRendererNotice =>
            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.MapExportUISystem.InstallingRendererNotice.CB8FD8567A");

        private static void ShowNotificationOnGameThread(string notificationId, string message)
        {
            var instance = s_Instance;
            if (instance != null)
            {
                instance.ShowNotification(notificationId, message);
            }
        }

        internal static void RequestOpenExportFolder()
        {
            if (s_Instance == null)
            {
                ShellFolder.RevealOrCopy(ExportPaths.ExportDirectory, null);
                return;
            }

            s_OpenFolderRequested = true;
        }

        internal static void RequestCopyExportFolder()
        {
            if (s_Instance == null)
            {
                ShellFolder.CopyOrComplain(ExportPaths.ExportDirectory, null);
                return;
            }

            s_CopyFolderRequested = true;
        }

        internal static void RequestOpenRendererFolder()
        {
            s_OpenRendererFolderRequested = true;
        }

        private void ProcessExport()
        {
            try
            {
                var game = Game.SceneFlow.GameManager.instance;
                if (game == null || (game.gameMode & GameMode.GameOrEditor) == 0)
                {
                    var noCity = ExportReport.NoCityReason;
                    Mod.log.Warn("[export] 已拒绝请求：当前没有已加载的游戏或编辑器上下文");
                    ExportReport.MarkFailed(noCity);
                    m_OpenAfterExport = false;
                    ShowNotification(ResultNoticeId, ExportReport.FailureNotice(noCity), ProgressState.Failed, 0);
                    return;
                }

                if (game.gameMode == GameMode.Game && m_ExportWithSatellite)
                {
                    m_SatelliteExport = RunSatelliteExport(game);
                    game.StartCoroutine(m_SatelliteExport);
                    return;
                }

                if (m_ExportWithSatellite)
                    MarkExportStarted("CSLModernMap.UI.ExportProgress.CapturingCity");
                var result = CaptureCmmExport(out var text);
                BeginExportWrite(result, text);
            }
            catch (Exception e)
            {
                FailExport(e);
            }
        }

        private void BeginExportWrite(ExportResult result, string text, byte[] satellitePng = null)
        {
            m_ExportProgress = -1;
            m_ShownExportProgress = -1;
            ExportReport.MarkRunning("CSLModernMap.UI.ExportProgress.Validating");

            // 后台只处理已采集的数据，不访问 ECS、游戏系统或 UI。
            m_ExportTask = Task.Run(() =>
            {
                CmmWriter.ValidateAndWrite(result.Path, text,
                    progress => Volatile.Write(ref m_ExportProgress, progress), satellitePng);
                return result;
            });
        }

        private void UpdateExport()
        {
            if (!m_ExportTask.IsCompleted)
            {
                var progress = Volatile.Read(ref m_ExportProgress);
                if (progress != m_ShownExportProgress)
                {
                    m_ShownExportProgress = progress;
                    if (progress >= CmmWriter.ArchiveVerificationProgress)
                        ExportReport.MarkRunning("CSLModernMap.UI.ExportProgress.CheckingArchive");
                    else
                        ExportReport.MarkRunning("CSLModernMap.UI.ExportProgress.Writing", progress);
                }
                return;
            }

            var task = m_ExportTask;
            m_ExportTask = null;
            try
            {
                var result = task.GetAwaiter().GetResult();
                ExportReport.MarkSucceeded(result);
                Mod.log.Info(string.Format(
                    "[export] CMM地图已写出: {0}（节点={1}，路网={2}，建筑={3}，站点={4}，线路={5}，issues={6}）",
                    result.Path,
                    result.NodeCount,
                    result.NetworkCount,
                    result.BuildingCount,
                    result.StopCount,
                    result.LineCount,
                    result.IssueCount));
                ShowNotification(ResultNoticeId, ExportReport.ResultNotice(result), ProgressState.Complete, 100);

                if (m_OpenAfterExport)
                {
                    m_OpenAfterExport = false;
                    OpenInRenderer(result.Path);
                }
            }
            catch (Exception e)
            {
                FailExport(e);
            }
        }

        private void FailExport(Exception e)
        {
            m_OpenAfterExport = false;
            Mod.log.Error("[export] 导出失败: " + e);
            var reason = ExportReport.ShortReason(e);
            ExportReport.MarkFailed(reason);
            ShowNotification(ResultNoticeId, ExportReport.FailureNotice(reason), ProgressState.Failed, 0);
        }

        private string ReadCityName()
        {
            try
            {
                var config = World.GetExistingSystemManaged<CityConfigurationSystem>();
                if (config != null)
                {
                    var name = config.cityName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = config.overrideCityName;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name.Trim();
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 读取城市名失败: " + e.Message);
            }

            return "";
        }

        private static string GetRenderedDisplayName(
            Game.UI.NameSystem nameSystem, Entity entity, string context, out bool isCustom)
        {
            isCustom = false;
            if (nameSystem == null || entity == Entity.Null)
                return "";
            try
            {
                string customName;
                isCustom = nameSystem.TryGetCustomName(entity, out customName)
                    && !string.IsNullOrEmpty(customName);
                return isCustom ? customName : nameSystem.GetRenderedLabelName(entity) ?? "";
            }
            catch (Exception e)
            {
                Mod.log.Warn("[" + context + "] 名称解析失败: " + e.GetType().Name + ": " + e.Message);
                return "";
            }
        }

        private string GetPrefabName(Entity prefab)
        {
            if (prefab == Entity.Null)
            {
                return "";
            }

            string cached;
            if (m_PrefabNameCache.TryGetValue(prefab, out cached))
            {
                return cached;
            }

            var name = "";
            if (m_PrefabSystem != null)
            {
                try
                {
                    name = m_PrefabSystem.GetPrefabName(prefab) ?? "";
                    if (name.Length == 0)
                    {
                        Game.Prefabs.PrefabBase prefabBase;
                        if (m_PrefabSystem.TryGetPrefab(prefab, out prefabBase) && prefabBase != null)
                        {
                            name = prefabBase.name ?? "";
                        }
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn("[export] 预制体名称读取失败: " + e.GetType().Name + ": " + e.Message);
                }
            }

            m_PrefabNameCache[prefab] = name;
            return name;
        }

        private string GetNetworkKind(Entity entity, NetCompositionInfo section)
        {
            if (section.AirspaceOnly) return null;
            if (section.Waterway) return null;
            if (EntityManager.HasComponent<Game.Net.Marker>(entity)
                && !section.Taxiway
                && !section.Runway) return null;
            if (section.Runway || section.Taxiway) return "ROAD";
            if (section.Pathway) return "BEAUTIFICATION";
            if (EntityManager.HasComponent<Game.Net.Road>(entity)) return "ROAD";
            if (section.TramLanes > 0
                || EntityManager.HasComponent<Game.Net.TramTrack>(entity)) return "TRAM";
            if (section.SubwayLanes > 0
                || EntityManager.HasComponent<Game.Net.SubwayTrack>(entity)) return "METRO";
            if (section.TrainLanes > 0
                || EntityManager.HasComponent<Game.Net.TrainTrack>(entity)) return "RAIL";
            return null;
        }

        private string GetNodeModeHint(Entity node)
        {
            if (!EntityManager.HasComponent<Game.Net.Elevation>(node))
            {
                return "GROUND";
            }

            var elevation = EntityManager.GetComponentData<Game.Net.Elevation>(node).m_Elevation;
            var average = (elevation.x + elevation.y) * 0.5f;
            if (average < -1f) return "TUNNEL";
            if (average > 1f) return "ELEVATED";
            return "GROUND";
        }

        private static void AppendCurve(StringBuilder json, Game.Net.Curve curve)
        {
            var samples = Math.Max(2, Math.Min(64, (int)Math.Ceiling(curve.m_Length / 24f) + 1));
            for (var i = 0; i < samples; i++)
            {
                if (i > 0) json.Append(',');
                AppendVec3(json, MathUtils.Position(curve.m_Bezier, (float)i / (samples - 1)));
            }
        }

        private static void AppendVec3(StringBuilder json, Unity.Mathematics.float3 value)
        {
            json.Append("{\"x\": ").Append(CmmJson.FormatFloat(value.x))
                .Append(", \"y\": ").Append(CmmJson.FormatFloat(value.y))
                .Append(", \"z\": ").Append(CmmJson.FormatFloat(value.z)).Append('}');
        }

        private static string CombineIssueArrays(string first, string second)
        {
            if (string.IsNullOrEmpty(first) || first == "[]")
            {
                return string.IsNullOrEmpty(second) ? "[]" : second;
            }

            if (string.IsNullOrEmpty(second) || second == "[]")
            {
                return first;
            }

            return first.Substring(0, first.Length - 1) + ", " + second.Substring(1);
        }

        private const string ResultNoticeId = "CSLModernMap.Export.Result";

        private const string FolderNoticeId = "CSLModernMap.Export.Folder";

        private const string RendererStartNoticeId = "CSLModernMap.Renderer.Start";

        private const string RendererNoticeId = "CSLModernMap.Renderer.Result";

        private void ShowNotification(string notificationId, string message,
            ProgressState? progressState = null, int? progress = null)
        {
            try
            {
                var notifications = World.GetOrCreateSystemManaged<NotificationUISystem>();
                notifications.AddOrUpdateNotification(
                    notificationId,
                    LocalizedString.Value(ExportReport.NoticeTitle),
                    LocalizedString.Value(message),
                    string.Empty,
                    progressState,
                    progress,
                    null);
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 无法显示游戏内通知: " + e.Message);
            }
        }
    }
}
