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
            int issueCount,
            string firstIssue)
        {
            Path = path;
            LocalTime = localTime;
            NodeCount = nodeCount;
            NetworkCount = networkCount;
            BuildingCount = buildingCount;
            StopCount = stopCount;
            LineCount = lineCount;
            IssueCount = issueCount;
            FirstIssue = firstIssue;
        }

        internal string Path { get; }

        internal DateTime LocalTime { get; }

        internal int NodeCount { get; }

        internal int NetworkCount { get; }

        internal int BuildingCount { get; }

        internal int StopCount { get; }

        internal int LineCount { get; }

        internal int IssueCount { get; }

        internal string FirstIssue { get; }

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

    /// <summary>记录导出状态并生成结果文案</summary>
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
        private static string s_FirstIssue = "";
        private static string s_Reason = "";

        private static bool s_RecoverAttempted;

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
            s_FirstIssue = result.FirstIssue;
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
                        return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StatusText.EA8EE21FF0");
                    case ExportState.Succeeded:
                        return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StatusText.5603E01058");
                    case ExportState.SucceededWithWarnings:
                        return string.Format(
                            CultureInfo.InvariantCulture,
                            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StatusText.365411E7D4"),
                            s_Issues, s_FirstIssue);
                    case ExportState.Failed:
                        return (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StatusText.C55E9D64E5")) + s_Reason;
                    default:
                        return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StatusText.82C55B1779");
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
                    return global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.LastExportText.74BA37514F");
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

                return string.Format(CultureInfo.InvariantCulture,
global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.CountsText.0DA09FCB63"),
                        s_Nodes, s_Networks, s_Buildings, s_Stops, s_Lines);
            }
        }

        internal static string NoticeTitle => global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.NoticeTitle.5E5DE0524E");

        internal static string StartNotice =>
            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.StartNotice.D8426601FD");

        internal static string ResultNotice(ExportResult result)
        {
            if (result.IssueCount > 0)
            {
                return string.Format(CultureInfo.InvariantCulture,
global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.ResultNotice.7978DA43CD"),
                        result.IssueCount, result.FirstIssue, result.FileName);
            }

            return string.Format(CultureInfo.InvariantCulture,
global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.ResultNotice.B802072C00"),
                    result.FileName, result.NodeCount, result.NetworkCount,
                    result.BuildingCount, result.StopCount, result.LineCount);
        }

        internal static string FailureNotice(string reason)
        {
            return (global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.FailureNotice.C55E9D64E5"))
                + (string.IsNullOrEmpty(reason) ? UnknownReason : reason);
        }

        internal static string NoCityReason =>
            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.NoCityReason.232E5446BE");

        private static string UnknownReason =>
            global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.UnknownReason.6189DC68F6");

        internal static string OpenFailedCopied(string directory) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.OpenFailedCopied.9C456E1F5D"), directory);

        internal static string OpenFailed(string directory) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.OpenFailed.AA6D1F5DC8"), directory);

        internal static string PathCopied(string directory) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.PathCopied.B76A2663B9"), directory);

        internal static string CopyFailed(string directory) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, global::CSLModernMap.Settings.ModText.Get("CSLModernMap.UI.ExportReport.CopyFailed.D77F1BCAED"), directory);

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

        internal static bool Chinese
        {
            get
            {
                var locale = GameManager.instance?.localizationManager?.activeLocaleId;
                return !string.IsNullOrEmpty(locale)
                    && locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
