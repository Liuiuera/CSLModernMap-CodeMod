using System;
using System.Collections;
using System.Threading;
using ColossalFramework;
using ColossalFramework.UI;
using CSLModernMap.Exporter;
using UnityEngine;

namespace CSLModernMap.UI
{
    internal delegate void ExportCompleted(ExportSummary summary, Exception error);

    internal sealed class ExportTaskRunner : MonoBehaviour
    {
        private static ExportTaskRunner instance;
        private CSLModernMapExporter exporter;
        private ExportProgress progress;
        private ExportCompleted completion;
        private SimulationManager simulation;
        private bool previousForcedPause, previousPause;
        private volatile bool ownsPause;
        private volatile bool workerDone;
        private SimulationCaptureGate captureGate;
        private Exception failure;
        private bool writing;

        internal static void Begin(CSLModernMapExporter exporter, ExportProgress progress, ExportCompleted completion)
        {
            ExportTaskRunner runner = Ensure();
            if (runner.exporter != null) throw new InvalidOperationException("An export is already running");
            runner.exporter = exporter; runner.progress = progress; runner.completion = completion;
            runner.failure = null; runner.writing = false; runner.workerDone = false;
            runner.captureGate = new SimulationCaptureGate();
            try
            {
                runner.simulation = Singleton<SimulationManager>.instance;
                runner.previousPause = runner.simulation.SimulationPaused;
                runner.previousForcedPause = runner.simulation.ForcedSimulationPaused;
                runner.ownsPause = true;
                // Paused simulations do not advance AddAction queues.
                if (runner.previousPause || runner.previousForcedPause)
                {
                    runner.captureGate.StartImmediately();
                }
                else
                {
                    // Queue while running. The barrier pauses from the simulation thread, after
                    // the active step has completed.
                    runner.simulation.AddAction(runner.SimulationBarrier(runner.captureGate));
                }
                runner.StartCoroutine(runner.Capture());
            }
            catch (Exception ex) { runner.RestorePause(); runner.failure = ex; runner.Finish(); }
        }

        internal static ExportTaskRunner Ensure()
        {
            if (instance == null)
            {
                GameObject tasks = new GameObject("CSLModernMap Export Tasks");
                DontDestroyOnLoad(tasks);
                instance = tasks.AddComponent<ExportTaskRunner>();
            }
            return instance;
        }

        private IEnumerator SimulationBarrier(SimulationCaptureGate gate)
        {
            if (!ownsPause || gate.Cancelled) yield break;
            simulation.ForcedSimulationPaused = true;
            if (!ownsPause || gate.Cancelled)
            {
                simulation.ForcedSimulationPaused = previousForcedPause;
                yield break;
            }
            gate.Hold();
        }

        internal static void CancelCapture()
        {
            SatelliteExport.Cancel();
            if (instance == null || instance.exporter == null || instance.writing) return;
            instance.failure = new InvalidOperationException("City unloaded during capture");
            instance.StopAllCoroutines();
            instance.RestorePause();
            if (instance.completion != null) instance.Finish();
        }

        private IEnumerator Capture()
        {
            IEnumerator capture = null;
            try
            {
                float started = Time.realtimeSinceStartup;
                bool waitingHintShown = false;
                while (!captureGate.Ready && CSLModernMapLoadingExtension.IsLevelLoaded)
                {
                    if (!waitingHintShown && Time.realtimeSinceStartup - started >= 15f)
                    {
                        waitingHintShown = true;
                        ExportActions.ShowExportProgressHint("EXPORT_PROGRESS_WAITING_CONTINUE");
                    }
                    yield return null;
                }
                if (failure == null && !CSLModernMapLoadingExtension.IsLevelLoaded)
                    failure = new InvalidOperationException("City unloaded before capture");
                if (failure != null) yield break;
                capture = exporter.CaptureSteps(previousPause, progress);
                float captureStarted = Time.realtimeSinceStartup;
                bool captureHintShown = false;
                while (CSLModernMapLoadingExtension.IsLevelLoaded)
                {
                    bool advanced = Advance(capture);
                    if (advanced && !captureHintShown
                        && Time.realtimeSinceStartup - captureStarted >= 30f)
                    {
                        captureHintShown = true;
                        ExportActions.ShowExportProgressHint("EXPORT_PROGRESS_CAPTURE_CONTINUE");
                    }
                    if (!advanced) break;
                    yield return null;
                }
                if (!CSLModernMapLoadingExtension.IsLevelLoaded)
                    failure = new InvalidOperationException("City unloaded during capture");
                if (failure != null) yield break;
                RestorePause();
                StartWriter();
            }
            finally
            {
                IDisposable disposable = capture as IDisposable;
                if (disposable != null) disposable.Dispose();
                RestorePause();
                if (!writing) Finish();
            }
        }

        private bool Advance(IEnumerator capture)
        {
            try { return capture.MoveNext(); }
            catch (Exception ex) { failure = ex; return false; }
        }

        private void StartWriter()
        {
            MapSnapshot captured = exporter.CapturedSnapshot;
            try
            {
                Thread worker = new Thread(delegate()
                {
                    try { exporter.WriteSnapshot(captured, progress); }
                    catch (Exception ex) { failure = ex; }
                    finally { workerDone = true; }
                });
                worker.IsBackground = true; worker.Name = "CSLModernMap CMM Writer";
                writing = true;
                worker.Start();
            }
            catch (Exception ex) { writing = false; failure = ex; }
        }

        private void RestorePause()
        {
            if (!ownsPause) return;
            ownsPause = false;
            captureGate.Release();
            simulation.ForcedSimulationPaused = previousForcedPause;
        }

        private void Finish()
        {
            ExportCompleted callback = completion; completion = null;
            callback(exporter.Summary, failure);
            exporter = null; writing = false;
        }

        public void Update()
        {
            if (writing && workerDone) Finish();
            ExportActions.RefreshProgressUI();
        }

        public void OnDestroy()
        {
            SatelliteExport.Cancel();
            RestorePause();
        }
    }
}
