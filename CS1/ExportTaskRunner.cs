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
        private bool previousForcedPause, previousPause, ownsPause;
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
                runner.simulation.ForcedSimulationPaused = true;
                // Wait for the active simulation step to finish before reading a stable snapshot.
                runner.simulation.AddAction(SimulationBarrier(runner.captureGate));
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

        private static IEnumerator SimulationBarrier(SimulationCaptureGate gate)
        { gate.Hold(); yield break; }

        internal static void CancelCapture()
        {
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
                while (!captureGate.Ready && CSLModernMapLoadingExtension.IsLevelLoaded)
                {
                    if (Time.realtimeSinceStartup - started > 15f)
                    { failure = new TimeoutException("Simulation did not reach the export boundary"); break; }
                    yield return null;
                }
                if (failure == null && !CSLModernMapLoadingExtension.IsLevelLoaded)
                    failure = new InvalidOperationException("City unloaded before capture");
                if (failure != null) yield break;
                capture = exporter.CaptureSteps(previousPause, progress);
                while (CSLModernMapLoadingExtension.IsLevelLoaded && Advance(capture)) yield return null;
                if (captureGate.TimedOut) failure = new TimeoutException("City capture exceeded the simulation hold limit");
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
            if (captureGate.TimedOut)
            { failure = new TimeoutException("City capture exceeded the simulation hold limit"); return false; }
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
            RestorePause();
        }
    }
}
