using System;
using System.Collections;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using Game.Simulation;
using Game.UI;
using Game.UI.Localization;
using CSLModernMap.Settings;
using UnityEngine;

namespace CSLModernMap.Systems
{
    public sealed partial class MapExportUISystem
    {
        private const float SatelliteDialogCloseDelaySeconds = 0.5f;
        private IEnumerator m_SatelliteExport;

        private void ShowSatelliteSaveReminder(GameManager game, bool openAfterwards)
        {
            var dialog = new ConfirmationDialog(
                LocalizedString.Value(ModText.Get("CSLModernMap.UI.SatelliteSave.Title")),
                LocalizedString.Value(ModText.Get("CSLModernMap.UI.SatelliteSave.Message")),
                LocalizedString.Value(ModText.Get("CSLModernMap.UI.SatelliteSave.Continue")),
                LocalizedString.Value(ModText.Get("CSLModernMap.UI.SatelliteSave.Back")),
                Array.Empty<LocalizedString>());
            game.userInterface.appBindings.ShowConfirmationDialog(dialog, result =>
            {
                if (result == 0 && ReferenceEquals(s_Instance, this) && !ExportReport.IsBusy
                    && game == GameManager.instance && game.gameMode == GameMode.Game)
                    EnqueueExport(openAfterwards, withSatellite: true);
            });
        }

        internal static void StopSatelliteExport() => s_Instance?.CancelSatelliteExport();

        private void CancelSatelliteExportOnLoad(Purpose purpose, GameMode mode) => CancelSatelliteExport();

        private void CancelSatelliteExport()
        {
            var run = m_SatelliteExport;
            if (run == null)
                return;

            GameManager.instance?.StopCoroutine(run);
            (run as IDisposable)?.Dispose();
        }

        private IEnumerator RunSatelliteExport(GameManager game)
        {
            var steps = CaptureSatelliteExport(game);
            game.onGamePreload += CancelSatelliteExportOnLoad;
            Exception failure = null;
            bool finished = false;
            try
            {
                while (true)
                {
                    bool advanced;
                    try { advanced = steps.MoveNext(); }
                    catch (Exception error) { failure = error; break; }
                    if (!advanced)
                    {
                        finished = true;
                        break;
                    }
                    yield return steps.Current;
                }
            }
            finally
            {
                game.onGamePreload -= CancelSatelliteExportOnLoad;
                m_SatelliteExport = null;
                (steps as IDisposable)?.Dispose();
                if (!finished)
                    FailExport(failure ?? new OperationCanceledException(
                        ModText.Get("CSLModernMap.UI.ExportProgress.SatelliteCancelled")));
            }
        }

        private IEnumerator CaptureSatelliteExport(GameManager game)
        {
            yield return new WaitForSecondsRealtime(SatelliteDialogCloseDelaySeconds);
            MarkExportStarted("CSLModernMap.UI.ExportProgress.CapturingSatellite");
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            var previousSpeed = simulation.selectedSpeed;
            SatelliteCapture.Image satellite = null;
            string satelliteWarning = null;
            ExportResult result;
            string text;
            try
            {
                var images = CaptureSatelliteImage(game, simulation, image => satellite = image);
                try
                {
                    while (true)
                    {
                        bool advanced;
                        try { advanced = images.MoveNext(); }
                        catch (Exception error)
                        {
                            Mod.log.Warn("[export] Satellite capture failed. Continuing with city data: " + error);
                            satelliteWarning = ModText.Get("CSLModernMap.UI.ExportProgress.SatelliteSkipped");
                            simulation.selectedSpeed = previousSpeed;
                            break;
                        }
                        if (!advanced)
                            break;
                        yield return images.Current;
                    }
                }
                finally
                {
                    (images as IDisposable)?.Dispose();
                }

                ExportReport.MarkRunning(satelliteWarning == null
                    ? "CSLModernMap.UI.ExportProgress.CapturingCity"
                    : "CSLModernMap.UI.ExportProgress.CapturingCityWithoutSatellite");
                // Give the UI time to repaint and Unity time to release the capture textures.
                yield return null;
                yield return null;

                result = CaptureCmmExport(out text, satellite: satellite, satelliteWarning: satelliteWarning);
            }
            finally
            {
                simulation.selectedSpeed = previousSpeed;
            }

            BeginExportWrite(result, text, satellite?.Png);
        }

        private IEnumerator CaptureSatelliteImage(GameManager game, SimulationSystem simulation,
            Action<SatelliteCapture.Image> onCaptured)
        {
            simulation.selectedSpeed = 0;
            yield return null;
            yield return null;
            if (simulation.selectedSpeed != 0)
                throw new InvalidOperationException("Unable to pause the city for satellite capture.");

            SatelliteCapture.Image satellite;
            using (var capture = new SatelliteCapture(World, game))
            {
                var images = capture.RunExport();
                try
                {
                    while (images.MoveNext())
                        yield return images.Current;
                }
                finally
                {
                    (images as IDisposable)?.Dispose();
                }
                satellite = capture.GetImage();
            }
            onCaptured(satellite);
        }
    }
}
