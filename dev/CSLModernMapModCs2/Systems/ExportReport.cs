using System;
using System.Globalization;
using Game.SceneFlow;

namespace CSLModernMap.Systems
{
    internal enum ExportState
    {
        Idle,

        Queued,

        Running,

        Succeeded,

        SucceededWithWarnings,

        Failed,
    }

    internal readonly struct ExportResult
    {
        internal ExportResult(
            string path,
            DateTime localTime,
            int nodeCount,
            int networkCount,
            int buildingCount,
            int stopCount,
            int lineCount,
            int issueCount)
        {
            Path = path;
            LocalTime = localTime;
            NodeCount = nodeCount;
            NetworkCount = networkCount;
            BuildingCount = buildingCount;
            StopCount = stopCount;
            LineCount = lineCount;
            IssueCount = issueCount;
        }

        internal string Path { get; }

        internal DateTime LocalTime { get; }

        internal int NodeCount { get; }

        internal int NetworkCount { get; }

        internal int BuildingCount { get; }

        internal int StopCount { get; }

        internal int LineCount { get; }

        /// <summary>文件里 <c>issues</c> 数组的元素个数（error + warn合计）。</summary>
        internal int IssueCount { get; }

        internal string FileName
        {
            get
            {
                var index = Path.LastIndexOf('/');
                var index2 = Path.LastIndexOf('\\');
                var cut = Math.Max(index, index2);
                return cut >= 0 ? Path.Substring(cut + 1) : Path;
            }
        }
    }

    /// <summary>保存导出状态并生成选项页与通知文案。</summary>
    internal static class ExportReport
    {
        private static ExportState s_State = ExportState.Idle;
        private static string s_FileName = "";
        private static DateTime s_Time = DateTime.MinValue;
        private static int s_Nodes;
        private static int s_Networks;
        private static int s_Buildings;
        private static int s_Stops;
        private static int s_Lines;
        private static int s_Issues;
        private static string s_Reason = "";

        private static bool s_RecoverAttempted;

        /// <summary>导出排队或进行中。</summary>
        internal static bool IsBusy => s_State == ExportState.Queued || s_State == ExportState.Running;

        internal static void MarkQueued()
        {
            s_State = ExportState.Queued;
            s_Reason = "";
        }

        internal static void MarkRunning()
        {
            s_State = ExportState.Running;
        }

        internal static void MarkSucceeded(ExportResult result)
        {
            s_State = result.IssueCount > 0 ? ExportState.SucceededWithWarnings : ExportState.Succeeded;
            s_FileName = result.FileName;
            s_Time = result.LocalTime;
            s_Nodes = result.NodeCount;
            s_Networks = result.NetworkCount;
            s_Buildings = result.BuildingCount;
            s_Stops = result.StopCount;
            s_Lines = result.LineCount;
            s_Issues = result.IssueCount;
            s_Reason = "";
        }

        internal static void MarkFailed(string reason)
        {
            s_State = ExportState.Failed;
            s_Reason = string.IsNullOrEmpty(reason) ? UnknownReason : reason;
        }

        internal static string StatusText
        {
            get
            {
                RecoverOnce();
                switch (s_State)
                {
                    case ExportState.Queued:
                    case ExportState.Running:
                        return Chinese ? "正在导出，请勿重复点击" : "Exporting — please do not click again";
                    case ExportState.Succeeded:
                        return Chinese ? "导出成功" : "Export succeeded";
                    case ExportState.SucceededWithWarnings:
                        return string.Format(
                            CultureInfo.InvariantCulture,
                            Chinese ? "导出完成，但有警告（{0} 条）" : "Export finished with {0} warning(s)",
                            s_Issues);
                    case ExportState.Failed:
                        return (Chinese ? "导出失败：" : "Export failed: ") + s_Reason;
                    default:
                        return Chinese ? "等待导出" : "Waiting to export";
                }
            }
        }

        internal static string LastExportText
        {
            get
            {
                RecoverOnce();
                if (string.IsNullOrEmpty(s_FileName))
                {
                    return Chinese ? "（还没有导出过）" : "(no export yet)";
                }

                if (s_Time == DateTime.MinValue)
                {
                    return s_FileName;
                }

                return s_Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " · " + s_FileName;
            }
        }

        internal static string CountsText
        {
            get
            {
                RecoverOnce();
                if (string.IsNullOrEmpty(s_FileName) || s_Nodes + s_Networks + s_Buildings == 0)
                {
                    return "—";
                }

                return Chinese
                    ? string.Format(CultureInfo.InvariantCulture,
                        "节点 {0} · 路网 {1} · 建筑 {2} · 站点 {3} · 线路 {4}",
                        s_Nodes, s_Networks, s_Buildings, s_Stops, s_Lines)
                    : string.Format(CultureInfo.InvariantCulture,
                        "nodes {0} · networks {1} · buildings {2} · stops {3} · lines {4}",
                        s_Nodes, s_Networks, s_Buildings, s_Stops, s_Lines);
            }
        }

        internal static string NoticeTitle => Chinese ? "CSLModernMap地图导出" : "CSLModernMap Map Export";

        internal static string StartNotice =>
            Chinese ? "已开始导出：正在读取当前城市，请勿重复点击" : "Export started: reading the current city, please wait";

        internal static string ResultNotice(ExportResult result)
        {
            if (result.IssueCount > 0)
            {
                return Chinese
                    ? string.Format(CultureInfo.InvariantCulture,
                        "导出完成，但有警告（{0} 条）：{1}", result.IssueCount, result.FileName)
                    : string.Format(CultureInfo.InvariantCulture,
                        "Export finished with {0} warning(s): {1}", result.IssueCount, result.FileName);
            }

            return Chinese
                ? string.Format(CultureInfo.InvariantCulture,
                    "导出成功：{0}\n节点 {1} · 路网 {2} · 建筑 {3} · 站点 {4} · 线路 {5}",
                    result.FileName, result.NodeCount, result.NetworkCount,
                    result.BuildingCount, result.StopCount, result.LineCount)
                : string.Format(CultureInfo.InvariantCulture,
                    "Export succeeded: {0}\nnodes {1} · networks {2} · buildings {3} · stops {4} · lines {5}",
                    result.FileName, result.NodeCount, result.NetworkCount,
                    result.BuildingCount, result.StopCount, result.LineCount);
        }

        internal static string FailureNotice(string reason)
        {
            return (Chinese ? "导出失败：" : "Export failed: ")
                + (string.IsNullOrEmpty(reason) ? UnknownReason : reason);
        }

        internal static string NoCityReason =>
            Chinese ? "没有可导出的城市，请先载入存档" : "no city to export — load a save first";

        private static string UnknownReason =>
            Chinese ? "未知错误（详见CSLModernMap.log）" : "unknown error (see CSLModernMap.log)";

        internal static string OpenFailedCopied(string directory) =>
            Chinese
                ? "无法打开文件夹，已复制导出目录路径：" + directory
                : "Could not open the folder; the export path was copied instead: " + directory;

        internal static string OpenFailed(string directory) =>
            Chinese
                ? "无法打开文件夹，请手动前往：" + directory
                : "Could not open the folder; please open it manually: " + directory;

        internal static string PathCopied(string directory) =>
            Chinese ? "已复制导出目录路径：" + directory : "Export folder path copied: " + directory;

        internal static string CopyFailed(string directory) =>
            Chinese ? "复制失败，请手动记录：" + directory : "Copy failed; the path is: " + directory;

        /// <summary>异常通知短原因</summary>
        internal static string ShortReason(Exception e)
        {
            if (e == null)
            {
                return UnknownReason;
            }

            var message = (e.Message ?? string.Empty)
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (message.Length > 80)
            {
                message = message.Substring(0, 80) + "…";
            }

            return string.IsNullOrEmpty(message)
                ? e.GetType().Name
                : e.GetType().Name + ": " + message;
        }

        private static void RecoverOnce()
        {
            if (s_RecoverAttempted)
            {
                return;
            }

            s_RecoverAttempted = true;

            if (s_State != ExportState.Idle)
            {
                return;
            }

            var newest = ExportPaths.FindNewestExport();
            if (newest == null)
            {
                return;
            }

            s_FileName = newest.Value.FileName;
            s_Time = newest.Value.LocalTime;
        }

        /// <summary>取值时动态判断语言</summary>
        private static bool Chinese
        {
            get
            {
                try
                {
                    var manager = GameManager.instance != null ? GameManager.instance.localizationManager : null;
                    if (manager == null)
                    {
                        return false;
                    }

                    var localeId = manager.activeLocaleId;
                    return !string.IsNullOrEmpty(localeId)
                        && localeId.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
