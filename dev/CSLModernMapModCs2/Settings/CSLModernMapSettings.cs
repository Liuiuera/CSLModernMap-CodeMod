using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace CSLModernMap.Settings
{
    /// <summary>模组选项页；耗时操作只在属性setter中排队。</summary>
    [FileLocation(nameof(CSLModernMap))]
    [SettingsUITabOrder(MainTab)]
    [SettingsUIGroupOrder(StatusGroup, RendererGroup, ExportGroup, AboutGroup)]
    [SettingsUIShowGroupName(StatusGroup, RendererGroup, ExportGroup, AboutGroup)]
    public sealed class CSLModernMapSettings : ModSetting
    {
        internal const string MainTab = "Main";
        internal const string StatusGroup = "Status";
        internal const string ExportGroup = "Export";

        internal const string RendererGroup = "Renderer";

        internal const string AboutGroup = "About";

        internal const string PrimaryButtons = "PrimaryButtons";

        internal const string SecondaryButtons = "SecondaryButtons";

        public CSLModernMapSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainTab, StatusGroup)]
        public string ExportStatus => Systems.ExportReport.StatusText;

        [SettingsUISection(MainTab, StatusGroup)]
        public string LastExport => Systems.ExportReport.LastExportText;

        [SettingsUISection(MainTab, StatusGroup)]
        public string LastCounts => Systems.ExportReport.CountsText;

        [SettingsUISection(MainTab, RendererGroup)]
        public string RendererStatus => Systems.MapExportUISystem.RendererStatus;

        [SettingsUISection(MainTab, RendererGroup)]
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(CSLModernMapSettings), nameof(IsRendererUnsupported))]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsRendererBusy))]
        public bool InstallRenderer
        {
            set => Systems.MapExportUISystem.RequestInstallRenderer();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(PrimaryButtons)]
        [SettingsUIHideByCondition(typeof(CSLModernMapSettings), nameof(IsRendererUnsupported))]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBlocked))]
        public bool ExportAndOpen
        {
            set => Systems.MapExportUISystem.RequestExportAndOpen();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(PrimaryButtons)]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool ExportCurrentCity
        {
            set => Systems.MapExportUISystem.RequestExport();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(SecondaryButtons)]
        public bool OpenExportFolder
        {
            set => Systems.MapExportUISystem.RequestOpenExportFolder();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(SecondaryButtons)]
        public bool CopyExportFolderPath
        {
            set => Systems.MapExportUISystem.RequestCopyExportFolder();
        }

        public bool IsExportBusy => Systems.ExportReport.IsBusy;

        public bool IsExportBlocked => Systems.ExportReport.IsBusy || Systems.MapExportUISystem.RendererBusy;

        public bool IsRendererBusy => Systems.MapExportUISystem.RendererBusy;

        public bool IsRendererUnsupported => !Systems.RendererLauncher.IsSupported;

        [SettingsUISection(MainTab, AboutGroup)]
        public string Version => "CSLModernMap " + Mod.ModVersion;

        public override void SetDefaults()
        {
        }
    }
}
