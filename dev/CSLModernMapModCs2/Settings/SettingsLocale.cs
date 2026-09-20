using System.Collections.Generic;
using Colossal;

namespace CSLModernMap.Settings
{
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
                    "本次会话的导出进度；导出目录里已有文件时，下方两行显示最近一次的结果。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastExport)), "上次导出" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastExport)), "时间与文件名。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastCounts)), "上次统计" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastCounts)),
                    "节点 / 路网 / 建筑 / 站点 / 线路的数量。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.RendererStatus)), "查看器" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.RendererStatus)),
                    "随模组一起下发的查看器是否已就绪；装好后这里显示它的版本号。安装与打开全程不联网。" },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), "安装 / 更新查看器" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.InstallRenderer)),
                    "把模组自带的查看器解压到本机的 %LOCALAPPDATA%\\CSLModernMap\\Renderer。已经装过且版本一致时会直接跳过。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.Version)), "版本" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.Version)),
                    "当前安装的模组版本号。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)), "导出并打开" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)),
                    "导出当前城市，然后用模组自带的查看器打开这份文件。导出失败不会启动查看器。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)), "导出当前城市" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)),
                    "只把当前城市写成一份 .cmm.gz，放到导出目录，不启动任何程序。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)), "打开导出目录" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)),
                    "用系统文件管理器打开导出目录。若打不开，会自动复制目录路径。" },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)), "复制导出目录" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)),
                    "把导出目录的完整路径复制到剪贴板。" },
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
                    "Progress for this session; if the export folder already has files, the two rows below show the most recent one." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastExport)), "Last export" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastExport)), "Time and file name." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastCounts)), "Last counts" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastCounts)),
                    "Node / network / building / stop / line counts." },

                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.RendererStatus)), "Renderer" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.RendererStatus)),
                    "Whether the renderer shipped with this mod is ready; once installed, its version is shown here. Nothing is downloaded." },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), "Install / Update Renderer" },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.InstallRenderer)),
                    "Unpack the bundled renderer into %LOCALAPPDATA%\\CSLModernMap\\Renderer. Skipped when the same version is already installed." },

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
