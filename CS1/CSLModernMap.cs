using System;
using System.Collections.Generic;
using System.Resources;
using AlgernonCommons;
using AlgernonCommons.Notifications;
using AlgernonCommons.XML;
using ColossalFramework.Globalization;
using CSLModernMap.Config;
using ICities;

namespace CSLModernMap
{
    /// <summary>通过 AlgernonCommons 管理设置、选项面板及更新通知。</summary>
    public class CSLModernMap : OptionsMod<global::CSLModernMap.UI.CSLModernMapOptionsPanel>, IUserMod
    {
        public const string MOD_VERSION = "6.7.3f1";

        // Content Manager cannot initialize AlgernonCommons while enumerating plugins.
        private static readonly ResourceManager ChineseMetadata = new ResourceManager(
            "CSLModernMap.Resources.ModMetadataChinese", typeof(CSLModernMap).Assembly);
        private static readonly ResourceManager EnglishMetadata = new ResourceManager(
            "CSLModernMap.Resources.ModMetadataEnglish", typeof(CSLModernMap).Assembly);
        private static readonly ResourceManager UpdateNotes = new ResourceManager(
            "CSLModernMap.Resources.UpdateNotes", typeof(CSLModernMap).Assembly);
        private static readonly Dictionary<string, ResourceManager> MetadataCatalogs =
            new Dictionary<string, ResourceManager>(StringComparer.OrdinalIgnoreCase)
            {
                { "zh", ChineseMetadata },
                { "en", EnglishMetadata },
                { "fr", new ResourceManager("CSLModernMap.Resources.ModMetadatafr", typeof(CSLModernMap).Assembly) },
                { "de", new ResourceManager("CSLModernMap.Resources.ModMetadatade", typeof(CSLModernMap).Assembly) },
                { "es", new ResourceManager("CSLModernMap.Resources.ModMetadataes", typeof(CSLModernMap).Assembly) },
                { "pl", new ResourceManager("CSLModernMap.Resources.ModMetadatapl", typeof(CSLModernMap).Assembly) },
                { "pt", new ResourceManager("CSLModernMap.Resources.ModMetadataptBR", typeof(CSLModernMap).Assembly) },
                { "ru", new ResourceManager("CSLModernMap.Resources.ModMetadataru", typeof(CSLModernMap).Assembly) },
                { "ko", new ResourceManager("CSLModernMap.Resources.ModMetadatako", typeof(CSLModernMap).Assembly) },
                { "ja", new ResourceManager("CSLModernMap.Resources.ModMetadataja", typeof(CSLModernMap).Assembly) },
            };

        private static ResourceManager MetadataCatalog(string language)
        {
            string code = language.Split('-', '_')[0];
            return MetadataCatalogs.TryGetValue(code, out var catalog) ? catalog : EnglishMetadata;
        }

        private static string Metadata(string key) =>
            MetadataCatalog(GameLanguage).GetString(key);


        /// <summary>枚举模组期间避免初始化翻译系统，以免重入 PluginManager。</summary>
        private static string GameLanguage
        {
            get
            {
                try
                {
                    return LocaleManager.exists
                        && LocaleManager.instance != null
                        && !string.IsNullOrEmpty(LocaleManager.instance.language)
                        ? LocaleManager.instance.language : "en";
                }
                catch
                {
                    return "en";
                }
            }
        }

        /// <summary>模组基础显示名（AlgernonCommons 用于通知标题与日志）。</summary>
        public override string BaseName => Metadata("DisplayName");

        /// <summary>模组完整显示名（保持原有格式，不采用 ModBase 的自动版本号拼接）。</summary>
        public override string Name => BaseName + " v" + MOD_VERSION;

        public string Description => Metadata("Description");

        /// <summary>
        /// 「更新内容」通知条目，从新到旧排列（第一条版本号决定是否触发弹窗）。
        /// 历史通知读取嵌入资源；选项页仍使用 AlgernonCommons CSV。
        /// </summary>
        public override WhatsNewMessage[] WhatsNewMessages => new WhatsNewMessage[]
        {
            new WhatsNewMessage
            {
                Version = new Version("6.7.3"),
                MessagesAreKeys = false,
                Messages = new string[]
                {
                    UpdateNotes.GetString("Changes673")
                }
            },
            new WhatsNewMessage
            {
                Version = new Version("6.7.2"),
                MessagesAreKeys = false,
                Messages = new string[]
                {
                    UpdateNotes.GetString("Changes672")
                }
            },
            new WhatsNewMessage
            {
                Version = new Version("6.6.0"),
                MessagesAreKeys = false,
                Messages = new string[]
                {
                    MetadataCatalog(AlgernonCommons.Translation.Translations.ActiveLanguageCode).GetString("Changes660")
                }
            },
            new WhatsNewMessage
            {
                Version = new Version("6.4.0"),
                MessagesAreKeys = false,
                Messages = new string[]
                {
                    MetadataCatalog(AlgernonCommons.Translation.Translations.ActiveLanguageCode).GetString("Changes640")
                }
            }
        };

        /// <summary>
        /// 加载设置，并完成旧版 LastSeenVersion 的一次性迁移。
        /// 由 ModBase.OnEnabled 基类链路调用（先于进档时的 ShowWhatsNew）。
        /// </summary>
        public override void LoadSettings()
        {
            string before = WhatsNew.LastNotifiedVersionString;
            XMLFileUtils.Load<CSLModernMapConfig>(CSLModernMapConfig.SettingsFileName);
            CSLModernMapConfig.MigrateLegacyLastSeenVersion();

            // 迁移写入了已看版本时立即持久化，避免下次启动重复迁移/重复弹窗。
            if (WhatsNew.LastNotifiedVersionString != before)
            {
                SaveSettings();
            }
        }

        /// <summary>保存设置及更新通知状态。</summary>
        public override void SaveSettings() => XMLFileUtils.Save<CSLModernMapConfig>(CSLModernMapConfig.SettingsFileName);
    }
}
