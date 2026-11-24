using Colossal.Logging;
using Colossal.IO.AssetDatabase;
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

        public const string ModVersion = "6.7.4";

        private static readonly string[] SupportedLocaleIds =
        {
            "zh-HANS",
            "zh-HANT",
            "en-US",
            "it-IT",
            "de-DE",
            "es-ES",
            "fr-FR",
            "ja-JP",
            "ko-KR",
            "pl-PL",
            "pt-BR",
            "ru-RU",
        };

        internal static CSLModernMapSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            string directory;
            if (GameManager.instance != null && GameManager.instance.modManager != null &&
                GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                directory = System.IO.Path.GetDirectoryName(asset.path);
            }

            else directory = System.IO.Path.GetDirectoryName(typeof(Mod).Assembly.Location);
            Systems.MapExportUISystem.InitializeRenderer(directory);
            updateSystem.UpdateAt<Systems.MapExportUISystem>(SystemUpdatePhase.UIUpdate);

            Settings = new CSLModernMapSettings(this);
            AssetDatabase.global.LoadSettings(nameof(CSLModernMap), Settings, new CSLModernMapSettings(this));
            foreach (string localeId in SupportedLocaleIds)
            {
                GameManager.instance.localizationManager.AddSource(
                    localeId, new SettingsLocale(Settings, localeId));
            }
            Settings.RegisterInOptionsUI();
        }

        public void OnDispose()
        {
            Systems.MapExportUISystem.StopSatelliteExport();
#if CS2_SATELLITE_TRIALS
            Systems.MapExportUISystem.StopSatelliteTrials();
#endif
            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
