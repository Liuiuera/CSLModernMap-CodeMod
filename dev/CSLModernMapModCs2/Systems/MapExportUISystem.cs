using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.UI.Localization;
using Game.UI.Menu;
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

        private bool m_ExportArmed;

        private bool m_OpenAfterExport;

        private static volatile bool s_RendererWorkRunning;

        private static volatile string s_RendererError = "";

        private static string s_PendingRendererNotice = "";

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
                    return IsChinese ? "正在安装查看器…" : "Installing renderer…";
                }

                if (!string.IsNullOrEmpty(s_RendererError))
                {
                    return (IsChinese ? "查看器不可用：" : "Renderer unavailable: ") + s_RendererError;
                }

                if (!RendererLauncher.IsSupported)
                {
                    return IsChinese ? "当前平台不支持查看器" : "Renderer unavailable on this platform";
                }

                if (!RendererLauncher.HasPayload)
                {
                    return IsChinese ? "随包文件缺失" : "Bundled renderer files are missing";
                }

                if (RendererLauncher.IsInstalled)
                {
                    return (IsChinese ? "已就绪 · " : "Ready · ") + RendererLauncher.PayloadVersion;
                }

                return RendererLauncher.HasOlderInstallation
                    ? (IsChinese ? "待更新至 " : "Update available: ") + RendererLauncher.PayloadVersion
                        + (IsChinese ? "，点击下方安装 / 更新" : "; use Install / Update below")
                    : (IsChinese ? "未安装，点击下方安装 / 更新" : "Not installed; use Install / Update below");
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

            if (s_OpenFolderRequested)
            {
                s_OpenFolderRequested = false;

                ShellFolder.RevealOrCopy(
                    ExportPaths.ExportDirectory,
                    message => ShowNotification(ResultNoticeId, message));
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
                    message => ShowNotification(ResultNoticeId, message));
            }

            if (m_ExportArmed)
            {
                m_ExportArmed = false;
                ProcessExport();
                return;
            }

            if (!m_ExportRequested)
            {
                return;
            }

            m_ExportRequested = false;
            m_ExportArmed = true;
            ExportReport.MarkQueued();
            ShowNotification(StartNoticeId, ExportReport.StartNotice);
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
            if (s_Instance == null)
            {
                Mod.log.Warn("[export] 当前没有已载入的城市，无法执行导出");
                return;
            }

            if (!s_Instance.m_ExportRequested && !s_Instance.m_ExportArmed)
            {
                if (openAfterwards && RendererLauncher.HasPayload
                    && !RendererLauncher.IsInstalled && RendererLauncher.HasOlderInstallation)
                {
                    s_Instance.ShowNotification(RendererStartNoticeId,
                        (IsChinese ? "查看器待更新至 " : "Renderer update available: ")
                        + RendererLauncher.PayloadVersion
                        + (IsChinese ? "，导出后将自动更新" : "; updating after export"));
                }

                s_Instance.m_OpenAfterExport = openAfterwards;
                s_Instance.m_ExportRequested = true;
                ExportReport.MarkQueued();
            }
        }

        internal static void RequestInstallRenderer()
        {
            if (!RendererLauncher.IsSupported || s_RendererWorkRunning)
            {
                return;
            }

            if (!RendererLauncher.HasPayload)
            {
                FailRenderer(IsChinese
                    ? "随包查看器文件缺失，请重新安装模组"
                    : "bundled renderer files are missing — reinstall the mod");
                return;
            }

            ShowNotificationOnGameThread(RendererStartNoticeId, InstallingRendererNotice);
            StartRendererWork(() =>
            {
                RendererLauncher.EnsureInstalled(true);
                PostRendererNotice(IsChinese
                    ? "查看器已就绪（" + RendererLauncher.PayloadVersion + "）"
                    : "Renderer ready (" + RendererLauncher.PayloadVersion + ")");
            });
        }

        private void OpenInRenderer(string exportPath)
        {
            if (!RendererLauncher.IsSupported)
            {
                return;
            }

            if (!RendererLauncher.HasPayload)
            {
                FailRenderer(IsChinese
                    ? "随包查看器文件缺失，请重新安装模组"
                    : "bundled renderer files are missing — reinstall the mod");
                return;
            }

            var installing = !RendererLauncher.IsInstalled;
            var updating = installing && RendererLauncher.HasOlderInstallation;
            if (installing)
            {
                ShowNotification(RendererStartNoticeId, updating
                    ? (IsChinese ? "正在更新查看器至 " : "Updating renderer to ")
                        + RendererLauncher.PayloadVersion
                    : InstallingRendererNotice);
            }

            StartRendererWork(() =>
            {
                RendererLauncher.EnsureInstalled();
                RendererLauncher.Launch(exportPath);
                if (installing)
                {
                    PostRendererNotice(IsChinese
                        ? "查看器已就绪并已打开导出文件"
                        : "Renderer ready and export opened");
                }
            });
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
            }

            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    work();
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
                ? (IsChinese ? "未知错误" : "unknown error")
                : reason;
            PostRendererNotice((IsChinese ? "查看器失败：" : "Renderer failed: ") + s_RendererError);
        }

        private static void PostRendererNotice(string message)
        {
            Interlocked.Exchange(ref s_PendingRendererNotice, message ?? "");
        }

        private static string InstallingRendererNotice =>
            IsChinese ? "正在安装查看器，请稍候" : "Installing renderer, please wait";

        private static bool IsChinese
        {
            get
            {
                var manager = Game.SceneFlow.GameManager.instance?.localizationManager;
                var locale = manager?.activeLocaleId;
                return !string.IsNullOrEmpty(locale)
                    && locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }

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
            ExportReport.MarkRunning();

            try
            {
                if (m_NodeQuery.IsEmptyIgnoreFilter)
                {
                    var noCity = ExportReport.NoCityReason;
                    Mod.log.Warn("[export] 已拒绝请求：当前没有道路节点");
                    ExportReport.MarkFailed(noCity);
                    ShowNotification(ResultNoticeId, ExportReport.FailureNotice(noCity));
                    return;
                }

                var result = WriteCmmExport();
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
                ShowNotification(ResultNoticeId, ExportReport.ResultNotice(result));

                if (m_OpenAfterExport)
                {
                    m_OpenAfterExport = false;
                    OpenInRenderer(result.Path);
                }
            }
            catch (Exception e)
            {
                Mod.log.Error("[export] 导出失败: " + e);
                var reason = ExportReport.ShortReason(e);
                ExportReport.MarkFailed(reason);
                ShowNotification(ResultNoticeId, ExportReport.FailureNotice(reason));
            }
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

        private Game.UI.NameSystem GetNameSystem(string context)
        {
            try
            {
                return World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            }
            catch (Exception e)
            {
                Mod.log.Warn("[" + context + "] NameSystem不可用: "
                    + e.GetType().Name + ": " + e.Message);
                return null;
            }
        }

        private static bool TryGetCustomDisplayName(
            Game.UI.NameSystem nameSystem,
            Entity entity,
            string context,
            out string name)
        {
            name = "";
            if (nameSystem == null || entity == Entity.Null)
            {
                return false;
            }

            try
            {
                string customName;
                if (!nameSystem.TryGetCustomName(entity, out customName)
                    || string.IsNullOrEmpty(customName))
                {
                    return false;
                }

                name = nameSystem.GetRenderedLabelName(entity) ?? customName;
                if (string.IsNullOrEmpty(name))
                {
                    name = customName;
                }
                return !string.IsNullOrEmpty(name);
            }
            catch (Exception e)
            {
                Mod.log.Warn("[" + context + "] 自定义名称解析失败: "
                    + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>分别读取显示名称与自定义名称来源以避免混淆自动命名</summary>
        private static string GetRenderedDisplayName(
            Game.UI.NameSystem nameSystem,
            Entity entity,
            string context,
            out bool isCustom)
        {
            isCustom = false;
            if (nameSystem == null || entity == Entity.Null)
            {
                return "";
            }

            string rendered;
            try
            {
                rendered = nameSystem.GetRenderedLabelName(entity) ?? "";
            }
            catch (Exception e)
            {
                Mod.log.Warn("[" + context + "] 显示名称解析失败: "
                    + e.GetType().Name + ": " + e.Message);
                return "";
            }

            try
            {
                string customName;
                isCustom = nameSystem.TryGetCustomName(entity, out customName)
                    && !string.IsNullOrEmpty(customName);
                return string.IsNullOrEmpty(rendered) && isCustom
                    ? customName
                    : rendered;
            }
            catch (Exception e)
            {
                Mod.log.Warn("[" + context + "] 名称来源判定失败: "
                    + e.GetType().Name + ": " + e.Message);
                return rendered;
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
            json.Append("{\"x\": ").Append(FormatFloat(value.x))
                .Append(", \"y\": ").Append(FormatFloat(value.y))
                .Append(", \"z\": ").Append(FormatFloat(value.z)).Append('}');
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

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private const string StartNoticeId = "CSLModernMap.Export.Start";

        private const string ResultNoticeId = "CSLModernMap.Export.Result";

        private const string RendererStartNoticeId = "CSLModernMap.Renderer.Start";

        private const string RendererNoticeId = "CSLModernMap.Renderer.Result";

        private void ShowNotification(string notificationId, string message)
        {
            try
            {
                var notifications = World.GetOrCreateSystemManaged<NotificationUISystem>();
                notifications.AddOrUpdateNotification(
                    notificationId,
                    LocalizedString.Value(ExportReport.NoticeTitle),
                    LocalizedString.Value(message),
                    string.Empty,
                    null,
                    null,
                    null);
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 无法显示游戏内通知: " + e.Message);
            }
        }
    }
}
