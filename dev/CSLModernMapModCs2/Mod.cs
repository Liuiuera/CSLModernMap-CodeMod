using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using CSLModernMap.Settings;

namespace CSLModernMap
{
    public class Mod : IMod
    {
        public static readonly ILog log =
            LogManager.GetLogger("CSLModernMap").SetShowsErrorsInUI(false);

        /// <summary>由release.py与其他版本落点同步，不要手改。</summary>
        public const string ModVersion = "6.7.1";

        /// <summary>中文使用中文文案，其余已知语言回退到英文。</summary>
        private static readonly string[] SupportedLocaleIds =
        {
            "zh-HANS",
            "zh-HANT",  // 繁體中文沿用简体文案：可读性优于英文
            "en-US",
            "de-DE",
            "es-ES",
            "fr-FR",
            "ja-JP",
            "ko-KR",
            "pl-PL",
            "pt-BR",
            "ru-RU",
        };

        private CSLModernMapSettings m_Settings;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info("=== CSLModernMap (CS2) 载入中 ===");

            if (GameManager.instance != null && GameManager.instance.modManager != null &&
                GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                log.Info("Mod程序集位置: " + asset.path);

                Systems.RendererLauncher.Initialize(System.IO.Path.GetDirectoryName(asset.path));
            }

            updateSystem.UpdateAt<Systems.MapExportUISystem>(SystemUpdatePhase.UIUpdate);

            m_Settings = new CSLModernMapSettings(this);
            foreach (string localeId in SupportedLocaleIds)
            {
                GameManager.instance.localizationManager.AddSource(
                    localeId, new SettingsLocale(m_Settings, localeId.StartsWith("zh")));
            }
            m_Settings.RegisterInOptionsUI();

            log.Info("=== CSLModernMap (CS2) 载入完成 ===");
        }

        public void OnDispose()
        {
            m_Settings?.UnregisterInOptionsUI();
            m_Settings = null;
            log.Info("CSLModernMap (CS2) 已卸载");
        }
    }
}
