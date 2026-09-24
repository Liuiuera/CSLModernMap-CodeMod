using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using CSLModernMap.Settings;

namespace CSLModernMap
{
    /// <summary>注册模组及其导出系统和设置</summary>
    public class Mod : IMod
    {
        public static readonly ILog log =
            LogManager.GetLogger("CSLModernMap").SetShowsErrorsInUI(false);

        public const string ModVersion = "6.7.1";

        private static readonly string[] SupportedLocaleIds =
        {
            "zh-HANS",
            "zh-HANT",
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
            if (GameManager.instance != null && GameManager.instance.modManager != null &&
                GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
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
        }

        public void OnDispose()
        {
            m_Settings?.UnregisterInOptionsUI();
            m_Settings = null;
        }
    }
}
