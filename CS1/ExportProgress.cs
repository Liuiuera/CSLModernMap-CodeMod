namespace CSLModernMap.Exporter
{
    public delegate void ExportProgress(int percent, string stageKey);

    internal static class ExportStages
    {
        internal static void Report(ExportProgress progress, int percent, string stageKey)
        { if (progress != null) progress(percent, stageKey); }
    }
}
