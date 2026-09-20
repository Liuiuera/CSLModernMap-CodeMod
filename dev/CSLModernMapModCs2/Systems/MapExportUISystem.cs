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
    /// <summary>模组选项页与CMM导出流程之间的边界；各数据域的采集由独立导出器完成。</summary>
    public sealed partial class MapExportUISystem : GameSystemBase
    {
        /// <summary>CS2一个分区单元格 = 8x8米（官方资产规范的Lot Size换算基准）。</summary>
        private const float CellSizeMeters = 8f;

        private static MapExportUISystem s_Instance;

        private bool m_ExportRequested;

        private bool m_ExportArmed;

        private bool m_OpenAfterExport;

        private static volatile bool s_RendererWorkRunning;

        private static volatile string s_RendererError = "";

        private static string s_PendingRendererNotice = "";

        private static bool s_OpenFolderRequested;

        private static bool s_CopyFolderRequested;

        private EntityQuery m_NodeQuery;
        private EntityQuery m_EdgeQuery;
        private EntityQuery m_BuildingQuery;

        private NativeHashMap<Entity, LotSizeEntry> m_LotSizeCache;

        private readonly Dictionary<Entity, string> m_PrefabNameCache = new Dictionary<Entity, string>();

        private readonly Dictionary<string, int> m_RoleCounts = new Dictionary<string, int>();

        private readonly Dictionary<string, int> m_ServiceCounts = new Dictionary<string, int>();

        private readonly Dictionary<string, int> m_SubServiceCounts = new Dictionary<string, int>();

        // Network semantics are cached by composition because ground, elevated and tunnel
        // variants of one prefab can have different widths and lanes.

        private readonly Dictionary<Entity, NetCompositionInfo> m_NetCompositionCache =
            new Dictionary<Entity, NetCompositionInfo>();

        private readonly Dictionary<Entity, string> m_RoadSizeClassCache = new Dictionary<Entity, string>();

        private int m_ExcludedNonBuildingCount;

        private int m_ExcludedNoGeometryCount;

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

                return RendererLauncher.IsInstalled
                    ? (IsChinese ? "已就绪 · " : "Ready · ") + RendererLauncher.PayloadVersion
                    : (IsChinese ? "未安装" : "Not installed");
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
                    ComponentType.ReadOnly<Game.Buildings.Building>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Common.Deleted>()
                }
            });

            // prefab名只能经PrefabSystem解析。
            m_PrefabSystem = World.GetOrCreateSystemManaged<Game.Prefabs.PrefabSystem>();

            Mod.log.Info("[export] 模组选项页导出服务已就绪");
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

            if (s_CopyFolderRequested)
            {
                s_CopyFolderRequested = false;
                ShellFolder.CopyOrComplain(
                    ExportPaths.ExportDirectory,
                    message => ShowNotification(ResultNoticeId, message));
            }

            // Delay the heavy export by one frame so the queued notification can render.
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
            Mod.log.Info("[export] 已排队：下一帧开始导出当前城市");
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

        /// <summary>「出当前城市按钮只置位。</summary>
        internal static void RequestExport()
        {
            QueueExport(false, "[export] 收到模组选项页导出请求");
        }

        /// <summary>导出成功后才启动查看器</summary>
        internal static void RequestExportAndOpen()
        {
            QueueExport(true, "[export] 收到“导出并打开”请求");
        }

        private static void QueueExport(bool openAfterwards, string logMessage)
        {
            if (s_Instance == null)
            {
                Mod.log.Warn("[export] 当前没有已载入的城市，无法执行导出");
                return;
            }

            if (!s_Instance.m_ExportRequested && !s_Instance.m_ExportArmed)
            {
                s_Instance.m_OpenAfterExport = openAfterwards;
                s_Instance.m_ExportRequested = true;
                ExportReport.MarkQueued();
                Mod.log.Info(logMessage);
            }
        }

        /// <summary>
        /// 「安装 / 更新查看器」按钮：把随包载荷解压到 <c>%LOCALAPPDATA%\CSLModernMap\Renderer</c>。
        /// 后台线程解压，通知由 OnUpdate 转回游戏线程。
        /// </summary>
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
                RendererLauncher.EnsureInstalled();
                PostRendererNotice(IsChinese
                    ? "查看器已就绪（" + RendererLauncher.PayloadVersion + "）"
                    : "Renderer ready (" + RendererLauncher.PayloadVersion + ")");
            });
        }

        /// <summary>
        /// 导出成功后的收尾：启动查看器打开文件；还没装就先装再启动，全在后台线程。
        /// </summary>
        private void OpenInRenderer(string exportPath)
        {
            if (!RendererLauncher.IsSupported)
            {
                Mod.log.Info("[renderer] 当前平台不支持查看器，跳过启动");
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
            if (installing)
            {
                ShowNotification(RendererStartNoticeId, InstallingRendererNotice);
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

        /// <summary>
        /// 在后台线程跑一次查看器工作。
        /// </summary>
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

        /// <summary>给静态入口用的通知出口：主菜单里没有系统实例，就没有通知，只有日志。</summary>
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

        private void ProcessExport()
        {
            ExportReport.MarkRunning();

            try
            {
                if (m_NodeQuery.IsEmptyIgnoreFilter)
                {
                    var noCity = ExportReport.NoCityReason;
                    Mod.log.Warn("[export] 已拒绝请求：当前没有道路节点");
                    m_OpenAfterExport = false;
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
                m_OpenAfterExport = false;
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

            return "Cities: Skylines II";
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
                // UI分类实体（RoadsSmallRoads这类）只有PrefabSystem.GetPrefabName认得，
                // TryGetPrefab对它拿不到。
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

        private string GetNetworkKind(Entity entity)
        {
            if (EntityManager.HasComponent<Game.Net.TramTrack>(entity)) return "TRAM";
            if (EntityManager.HasComponent<Game.Net.SubwayTrack>(entity)) return "METRO";
            if (EntityManager.HasComponent<Game.Net.TrainTrack>(entity)) return "RAIL";
            if (EntityManager.HasComponent<Game.Net.Road>(entity)) return "ROAD";
            return null;
        }

        /// <summary>
        /// 节点的路面模式提示。节点是连接点、没有横断面，只能退回到
        /// <c>Game.Net.Elevation.m_Elevation</c>（相对地形的高程，贴地接近0）。
        /// 这只是 <c>mode_hint</c> 一个提示字段；路段的真实模式取自横断面旗标，与这个阈值无关。
        /// </summary>
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

        /// <summary>合并两段完整的JSON数组文本；任一为空数组时直接返回另一段。</summary>
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

        // Start and result use distinct IDs so one notification cannot replace the other.
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
