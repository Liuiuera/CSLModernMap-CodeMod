using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Colossal;
using Newtonsoft.Json;

namespace CSLModernMap.Settings
{
    /// <summary>提供模组设置和动态消息的游戏原生本地化源</summary>
    internal sealed class SettingsLocale : IDictionarySource
    {
        private readonly CSLModernMapSettings m_Settings;
        private readonly Dictionary<string, string> m_Entries;

        internal SettingsLocale(CSLModernMapSettings settings, string localeId)
        {
            m_Settings = settings;
            string resource = "CSLModernMap.Locales." + localeId + ".json";
            using var stream = typeof(SettingsLocale).Assembly.GetManifestResourceStream(resource)
                ?? throw new FileNotFoundException("Missing mod language catalog", resource);
            using var reader = new StreamReader(stream);
            m_Entries = JsonConvert.DeserializeObject<Dictionary<string, string>>(reader.ReadToEnd());
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return SettingsEntries().Concat(m_Entries.Where(entry =>
                entry.Key.StartsWith("CSLModernMap.UI.", StringComparison.Ordinal)));
        }

        public void Unload()
        {
        }

        private IEnumerable<KeyValuePair<string, string>> SettingsEntries()
        {
            return new Dictionary<string, string>
            {
                { m_Settings.GetSettingsLocaleID(), m_Entries["Settings"] },
                { m_Settings.GetOptionTabLocaleID(CSLModernMapSettings.MainTab), m_Entries["OptionTab.MainTab"] },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.StatusGroup), m_Entries["OptionGroup.StatusGroup"] },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.RendererGroup), m_Entries["OptionGroup.RendererGroup"] },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.ExportGroup), m_Entries["OptionGroup.ExportGroup"] },
                { m_Settings.GetOptionGroupLocaleID(CSLModernMapSettings.AboutGroup), m_Entries["OptionGroup.AboutGroup"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportStatus)), m_Entries["OptionLabel.ExportStatus"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportStatus)), m_Entries["OptionDesc.ExportStatus"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastExport)), m_Entries["OptionLabel.LastExport"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastExport)), m_Entries["OptionDesc.LastExport"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.LastCounts)), m_Entries["OptionLabel.LastCounts"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.LastCounts)), m_Entries["OptionDesc.LastCounts"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.RendererStatus)), m_Entries["OptionLabel.RendererStatus"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.RendererStatus)), m_Entries["OptionDesc.RendererStatus"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), m_Entries["OptionLabel.InstallRenderer"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.InstallRenderer)), m_Entries["OptionDesc.InstallRenderer"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)), m_Entries["OptionLabel.OpenRendererFolder"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenRendererFolder)), m_Entries["OptionDesc.OpenRendererFolder"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.Version)), m_Entries["OptionLabel.Version"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.Version)), m_Entries["OptionDesc.Version"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)), m_Entries["OptionLabel.ExportAndOpen"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportAndOpen)), m_Entries["OptionDesc.ExportAndOpen"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)), m_Entries["OptionLabel.ExportCurrentCity"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.ExportCurrentCity)), m_Entries["OptionDesc.ExportCurrentCity"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)), m_Entries["OptionLabel.OpenExportFolder"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.OpenExportFolder)), m_Entries["OptionDesc.OpenExportFolder"] },
                { m_Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)), m_Entries["OptionLabel.CopyExportFolderPath"] },
                { m_Settings.GetOptionDescLocaleID(nameof(CSLModernMapSettings.CopyExportFolderPath)), m_Entries["OptionDesc.CopyExportFolderPath"] },
            };
        }
    }
}
