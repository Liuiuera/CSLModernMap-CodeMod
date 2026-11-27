using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace CSLModernMap.Settings
{
    [FileLocation(nameof(CSLModernMap))]
    [SettingsUITabOrder(MainTab, RendererTab)]
    [SettingsUIGroupOrder(ExportGroup, StatusGroup, RendererGroup, AboutGroup)]
    [SettingsUIShowGroupName(ExportGroup, StatusGroup, AboutGroup)]
    /// <summary>定义模组设置及选项页操作</summary>
    public sealed class CSLModernMapSettings : ModSetting
    {
        internal const string MainTab = "Main";
        internal const string RendererTab = "Renderer";
        internal const string ExportGroup = "Export";
        internal const string StatusGroup = "Status";
        internal const string RendererGroup = "Renderer";
        // 真实 3D 导出设置接通后，在渲染器与关于之间注册此组。
        internal const string Render3DGroup = "Render3D";
        internal const string AboutGroup = "About";

        internal const string ExportButtons = "ExportButtons";
        internal const string RendererButtons = "RendererButtons";
        internal const string FolderButtons = "FolderButtons";

        public CSLModernMapSettings(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIMultilineText]
        // 原生多行说明显示 OptionLabel 文案，不读取属性值。
        public string ExportGuide => string.Empty;

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool IncludeOutsideTerrain { get; set; }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool IncludeSatelliteImage { get; set; }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIHideByCondition(typeof(CSLModernMapSettings), nameof(IsRendererUnsupported))]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool ExportAndOpen
        {
            set => Systems.MapExportUISystem.RequestExportAndOpen();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(ExportButtons)]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool ExportCurrentCity
        {
            set => Systems.MapExportUISystem.RequestExport();
        }

        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(ExportButtons)]
        [SettingsUIHideByCondition(typeof(CSLModernMapSettings), nameof(IsRendererUnsupported))]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool OpenRenderer
        {
            set => Systems.MapExportUISystem.OpenRenderer();
        }

#if CS2_SATELLITE_TRIALS
        [SettingsUISection(MainTab, ExportGroup)]
        [SettingsUIButton]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool RunSatelliteTrials
        {
            set => Systems.MapExportUISystem.RequestSatelliteTrials();
        }
#endif

        [SettingsUISection(MainTab, StatusGroup)]
        public string ExportStatus => Systems.ExportReport.StatusText;

        [SettingsUISection(MainTab, StatusGroup)]
        public string LastExport => Systems.ExportReport.LastExportText;

        [SettingsUISection(MainTab, StatusGroup)]
        public string LastCounts => Systems.ExportReport.CountsText;

        [SettingsUISection(RendererTab, RendererGroup)]
        public string RendererStatus => Systems.MapExportUISystem.RendererStatus;

        [SettingsUISection(RendererTab, RendererGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(RendererButtons)]
        [SettingsUIHideByCondition(typeof(CSLModernMapSettings), nameof(IsRendererUnsupported))]
        [SettingsUIDisableByCondition(typeof(CSLModernMapSettings), nameof(IsExportBusy))]
        public bool InstallRenderer
        {
            set => Systems.MapExportUISystem.InstallRenderer();
        }

        [SettingsUISection(RendererTab, RendererGroup)]
        [SettingsUIButton]
        [SettingsUIButtonGroup(FolderButtons)]
        public bool OpenExportFolder
        {
            set => Systems.MapExportUISystem.RequestOpenExportFolder();
        }

        [SettingsUISection(RendererTab, RendererGroup)]
        [SettingsUIButton]
        public bool CopyExportFolderPath
        {
            set => Systems.MapExportUISystem.RequestCopyExportFolder();
        }

        public bool IsExportBusy => Systems.MapExportUISystem.IsExportBusy;

        public bool IsRendererUnsupported => !Systems.MapExportUISystem.RendererSupported;

        [SettingsUISection(RendererTab, AboutGroup)]
        public string Version => "CSLModernMap " + Mod.ModVersion;

        public override void SetDefaults()
        {
            IncludeOutsideTerrain = false;
            IncludeSatelliteImage = false;
        }
    }
}
