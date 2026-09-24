using System.Collections.Generic;
using Colossal;

namespace CSLModernMap.Settings
{
    /// <summary>提供模组设置的本地化文案</summary>
    internal sealed class SettingsLocale : IDictionarySource
    {
        private readonly CSLModernMapSettings m_Settings;
        private readonly bool m_Chinese;

        internal SettingsLocale(CSLModernMapSettings settings, bool chinese)
        {
            m_Settings = settings;
            m_Chinese = chinese;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return m_Chinese ? ChineseEntries() : EnglishEntries();
        }

        public void Unload()
        {
        }

        private IEnumerable<KeyValuePair<string, string>> ChineseEntries()
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "CSLModernMap地图导出" },

                { m_Settings.GetOptionTabLocaleID(CSLModernMapSettings.MainTab), "数据导出" },

                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.StatusGroup), "导出状态" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.RendererGroup), "查看器" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.ExportGroup), "当前城市" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.AboutGroup), "关于" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportStatus)), "导出状态" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportStatus)),
                    "这里直接显示导出结果和首条警告；其余问题可在导出文件的 issues 中查看。下方两行显示上次文件和统计。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastExport)), "上次导出" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastExport)), "时间与文件名。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastCounts)), "上次统计" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastCounts)),
                    "节点 / 路网 / 建筑 / 站点 / 线路的数量。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.RendererStatus)), "查看器安装状态" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.RendererStatus)),
                    "有旧版本时显示待更新至随包版本；点击安装或更新即可重装，无需联网。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), "安装 / 更新查看器" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.InstallRenderer)),
                    "校验随包载荷后清空本机的 %LOCALAPPDATA%\\CSLModernMap\\Renderer 并重新安装。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)), "打开渲染器目录" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)),
                    "用系统文件管理器打开渲染器安装目录。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.Version)), "版本" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.Version)),
                    "当前安装的模组版本号。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)), "导出并打开" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)),
                    "导出当前城市，然后用模组自带的查看器打开这份文件。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)), "导出当前城市" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)),
                    "只把当前城市导出到目录。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)), "打开导出目录" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)),
                    "用系统文件管理器打开导出目录。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)), "复制导出目录" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)),
                    "把导出目录的路径复制到剪贴板。" },
            };
        }

        private IEnumerable<KeyValuePair<string, string>> EnglishEntries()
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), "CSLModernMap Map Export" },

                { m_Settings.GetOptionTabLocaleID(CSLModernMapSettings.MainTab), "Data Export" },

                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.StatusGroup), "Export Status" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.RendererGroup), "Renderer" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.ExportGroup), "Current City" },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.AboutGroup), "About" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportStatus)), "Export status" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportStatus)),
                    "Shows the export result and first warning directly. Other issues are in the export file's issues; the rows below show the latest file and counts." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastExport)), "Last export" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastExport)), "Time and file name." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastCounts)), "Last counts" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastCounts)),
                    "Node / network / building / stop / line counts." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.RendererStatus)), "Renderer installation" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.RendererStatus)),
                    "An older installation is shown as update available. Install / Update reinstalls from the bundled payload without downloading." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), "Install / Update Renderer" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.InstallRenderer)),
                    "Verify the bundled payload, clear %LOCALAPPDATA%\\CSLModernMap\\Renderer, and reinstall the renderer." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)), "Open Renderer Folder" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)),
                    "Open the renderer installation folder in the system file manager." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.Version)), "Version" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.Version)),
                    "The version of the mod you have installed." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)), "Export and Open" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)),
                    "Export the current city, then open that file in the renderer shipped with this mod. A failed export never starts the renderer." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)), "Export Current City" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)),
                    "Write the current city to a .cmm.gz in the export folder without starting anything." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)), "Open Export Folder" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)),
                    "Open the export folder in the system file manager. If it cannot be opened, the path is copied instead." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)), "Copy Export Folder Path" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)),
                    "Copy the full export folder path to the clipboard." },
            };
        }
    }
}
