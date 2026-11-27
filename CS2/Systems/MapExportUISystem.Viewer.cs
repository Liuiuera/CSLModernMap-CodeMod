using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Colossal.PSI.Common;
using Colossal.Serialization.Entities;
using CSLModernMap.Settings;
using Game;
using Game.SceneFlow;
using Game.UI;
using Game.UI.Localization;

namespace CSLModernMap.Systems
{
    public sealed partial class MapExportUISystem
    {
        private object m_ExportPrompt;
        private string m_ViewerWarning;
        private static string s_ModDirectory;
        private static string s_RendererRetryMapPath;
        private static string s_RendererStatusKey;
        private static string s_RendererError;
        private static Task<bool> s_OpeningRenderer;
        private static string s_OpeningMapPath;

        internal static bool RendererSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        internal static bool IsExportBusy => ExportReport.IsBusy || s_Instance?.m_ExportPrompt != null;
        internal static string RendererStatus => s_RendererError ??
            (s_RendererStatusKey == null ? "" : ModText.Get(s_RendererStatusKey));

        internal static void InitializeRenderer(string directory)
        {
            s_ModDirectory = directory;
            CancelViewerRequests();
            if (RendererSupported) ExportPaths.RegisterExportDirectory();
            RefreshRendererStatus();
        }

        internal static void CancelViewerRequests()
        {
            if (s_Instance != null) s_Instance.m_ExportPrompt = null;
            s_OpeningRenderer = null;
            s_OpeningMapPath = null;
            s_RendererRetryMapPath = null;
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_ExportPrompt = null;
        }

        private static PackageStatus? RefreshRendererStatus()
        {
            s_RendererError = null;
            if (!RendererSupported)
            {
                s_RendererStatusKey = "CSLModernMap.UI.MapExportUISystem.RendererStatus.5FC5374073";
                return null;
            }
            try
            {
                var status = new WindowsRenderer(s_ModDirectory).Status();
                s_RendererStatusKey = status == PackageStatus.Ready ? "CSLModernMap.UI.Msix.Ready"
                    : status == PackageStatus.Update ? "CSLModernMap.UI.Msix.Update" : "CSLModernMap.UI.Msix.Missing";
                return status;
            }
            catch (Exception error)
            {
                Mod.log.Warn("[renderer] " + error);
                s_RendererError = error.Message;
                return null;
            }
        }

        private async void PrepareExport(bool openAfterwards)
        {
            var game = GameManager.instance;
            if (game == null || (game.gameMode & GameMode.GameOrEditor) == 0)
            {
                ExportReport.MarkFailed(ExportReport.NoCityReason);
                ShowNotification(ResultNoticeId, ExportReport.FailureNotice(ExportReport.NoCityReason), ProgressState.Failed, 0);
                return;
            }

            var prompt = m_ExportPrompt = new object();
            bool Current() => ReferenceEquals(s_Instance, this) && m_ExportPrompt == prompt;
            try
            {
                string warning = null;
                if (RendererSupported && !IsInstalled(RefreshRendererStatus()))
                {
                    var installLabel = Mod.Settings.GetOptionLabelLocaleID(nameof(CSLModernMapSettings.InstallRenderer));
                    int choice = await Confirm(game, installLabel, "CSLModernMap.UI.Msix.InstallPrompt",
                        installLabel, "CSLModernMap.UI.SatelliteSave.Continue");
                    if (!Current()) return;
                    string installError = null;
                    if (choice == 0)
                    {
                        installError = InstallRenderer();
                        if (installError == null)
                        {
                            await Confirm(game, installLabel, "CSLModernMap.UI.Msix.InstallDone",
                                "CSLModernMap.UI.Msix.Yes", "CSLModernMap.UI.Msix.No");
                            if (!Current()) return;
                        }
                    }

                    if (!IsInstalled(RefreshRendererStatus()))
                    {
                        warning = ModText.Get("CSLModernMap.UI.Msix.ExportWarning");
                        if (installError != null) warning += "\n" + installError;
                        if (s_RendererError != null && s_RendererError != installError) warning += "\n" + s_RendererError;
                    }
                }

                bool satellite = game.gameMode == GameMode.Game && Mod.Settings.IncludeSatelliteImage;
                if (satellite)
                {
                    int choice = await Confirm(game, "CSLModernMap.UI.SatelliteSave.Title", "CSLModernMap.UI.SatelliteSave.Message",
                        "CSLModernMap.UI.SatelliteSave.Continue", "CSLModernMap.UI.SatelliteSave.Back");
                    if (!Current() || choice != 0) return;
                }

                m_ViewerWarning = warning;
                EnqueueExport(openAfterwards, satellite);
            }
            catch (Exception error)
            {
                if (Current()) FailExport(error);
            }
            finally
            {
                if (m_ExportPrompt == prompt) m_ExportPrompt = null;
            }
        }

        private static bool IsInstalled(PackageStatus? status) =>
            status == PackageStatus.Ready || status == PackageStatus.Update;

        private static async Task<int> Confirm(GameManager game, string title, string message, string confirm, string cancel)
        {
            var dialog = new ConfirmationDialog(LocalizedString.Id(title), LocalizedString.Id(message),
                LocalizedString.Id(confirm), LocalizedString.Id(cancel), Array.Empty<LocalizedString>());
            int choice = await game.userInterface.appBindings.ShowConfirmationDialogAndWait(dialog);
            // Let the native dialog close before opening the next one.
            await Task.Yield();
            return choice;
        }

        internal static string InstallRenderer()
        {
            if (!RendererSupported) return null;
            s_RendererError = null;
            try
            {
                new WindowsRenderer(s_ModDirectory).Install();
                s_RendererStatusKey = "CSLModernMap.UI.Msix.InstallHint";
            }
            catch (Exception error)
            {
                Mod.log.Error("[renderer] MSIX installer: " + error);
                s_RendererError = error.Message;
            }
            s_Instance?.ShowNotification(RendererNoticeId, RendererStatus);
            return s_RendererError;
        }

        internal static async void OpenRenderer()
        {
            if (!RendererSupported) return;
            string mapPath = s_RendererRetryMapPath;
            if (s_OpeningRenderer != null && s_OpeningMapPath == mapPath) return;
            string directory = s_ModDirectory;
            var opening = Task.Run(() => new WindowsRenderer(directory).Open(mapPath));
            s_OpeningRenderer = opening;
            s_OpeningMapPath = mapPath;
            s_RendererError = null;
            s_RendererStatusKey = "CSLModernMap.UI.Msix.Opening";
            try
            {
                bool opened = await opening;
                if (s_OpeningRenderer != opening) return;
                s_RendererStatusKey = opened ? "CSLModernMap.UI.Msix.Opened" : "CSLModernMap.UI.Msix.Missing";
                if (opened && s_RendererRetryMapPath == mapPath) s_RendererRetryMapPath = null;
            }
            catch (Exception error)
            {
                if (s_OpeningRenderer != opening) return;
                Mod.log.Error("[renderer] MSIX activation: " + error);
                s_RendererError = error.Message;
            }
            finally
            {
                if (s_OpeningRenderer == opening)
                {
                    s_OpeningRenderer = null;
                    s_OpeningMapPath = null;
                    s_Instance?.ShowNotification(RendererNoticeId, RendererStatus);
                }
            }
        }
    }
}
