#if CS2_SATELLITE_TRIALS
using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Colossal.Serialization.Entities;
using Game;
using Game.SceneFlow;
using Game.Simulation;
using UnityEngine;

namespace CSLModernMap.Systems
{
    public sealed partial class MapExportUISystem
    {
        private const string SatelliteNoticeId = "CSLModernMap.SatelliteTrials";
        private IEnumerator m_SatelliteTrials;

        internal static void RequestSatelliteTrials()
        {
            var system = s_Instance;
            var game = GameManager.instance;
            if (system == null || game == null || ExportReport.IsBusy)
                return;
            if (game.gameMode != GameMode.Game)
            {
                system.ShowNotification(SatelliteNoticeId, "Load a city before running satellite trials.");
                return;
            }

            ExportReport.MarkQueued();
            game.onGamePreload += system.CancelSatelliteTrialsOnLoad;
            system.m_SatelliteTrials = system.RunSatelliteTrials(game);
            game.StartCoroutine(system.m_SatelliteTrials);
        }

        internal static void StopSatelliteTrials() => s_Instance?.CancelSatelliteTrials();

        private void CancelSatelliteTrialsOnLoad(Purpose purpose, GameMode mode) => CancelSatelliteTrials();

        private void CancelSatelliteTrials()
        {
            var run = m_SatelliteTrials;
            if (run == null)
                return;
            GameManager.instance?.StopCoroutine(run);
            (run as IDisposable)?.Dispose();
        }

        private IEnumerator RunSatelliteTrials(GameManager game)
        {
            var steps = CaptureSatelliteTrials(game);
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
                (steps as IDisposable)?.Dispose();
                m_SatelliteTrials = null;
                game.onGamePreload -= CancelSatelliteTrialsOnLoad;
                if (!finished)
                {
                    string reason = failure?.Message ?? "Satellite trials cancelled.";
                    ExportReport.MarkFailed(reason);
                    Mod.log.Warn("[satellite-trials] " + (failure?.ToString() ?? reason));
                    ShowNotification(SatelliteNoticeId, reason);
                }
            }
        }

        private IEnumerator CaptureSatelliteTrials(GameManager game)
        {
            var root = Path.Combine(ExportPaths.EnsureExportDirectory(), "satellite-trials",
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(root);
            ExportReport.MarkRunning();
            ShowNotification(SatelliteNoticeId, "Starting satellite capture trials.");
            Mod.log.Info("[satellite-trials] Starting " + root);
            var simulation = World.GetExistingSystemManaged<SimulationSystem>();
            var previousSpeed = simulation.selectedSpeed;
            ExportResult result;
            int failedSamples;
            string city;
            try
            {
                simulation.selectedSpeed = 0;
                yield return null;
                yield return null;
                if (simulation.selectedSpeed != 0)
                    throw new InvalidOperationException("Unable to pause simulation for satellite trials.");
                result = CaptureCmmExport(out city, root);
                var targets = new SatelliteLodTargets(EntityManager, m_BuildingQuery, m_EdgeQuery, GetPrefabName);
                using var trials = new SatelliteCapture(World, game);
                var images = trials.RunTrials(World, root, targets);
                try
                {
                    while (images.MoveNext())
                        yield return images.Current;
                }
                finally
                {
                    (images as IDisposable)?.Dispose();
                }
                failedSamples = trials.FailedSamples;
            }
            finally
            {
                simulation.selectedSpeed = previousSpeed;
            }

            var write = Task.Run(() => CmmWriter.ValidateAndWrite(result.Path, city, null));
            while (!write.IsCompleted)
                yield return null;
            write.GetAwaiter().GetResult();
            if (failedSamples > 0)
                throw new InvalidOperationException($"Satellite trials recorded {failedSamples} failed capture(s). See {root}");
            ExportReport.MarkSucceeded(result);
            ShowNotification(SatelliteNoticeId, "Satellite trials finished. " + root);
            Mod.log.Info("[satellite-trials] Finished " + root);
        }
    }
}
#endif
