using System.Threading;

namespace CSLModernMap.Exporter
{
    internal sealed class SimulationCaptureGate
    {
        private readonly object sync = new object();
        private ManualResetEvent released = new ManualResetEvent(false);
        internal volatile bool Ready, Cancelled;

        internal void Hold()
        {
            Ready = true;
            // Capture owns the hold until it completes or is cancelled.
            try { released.WaitOne(); }
            finally { lock (sync) { released.Close(); released = null; } }
        }

        internal void StartImmediately()
        {
            Ready = true;
            lock (sync)
            {
                if (released == null) return;
                released.Close();
                released = null;
            }
        }

        internal void Release()
        {
            lock (sync)
            {
                Cancelled = true;
                if (released != null) released.Set();
            }
        }
    }
}
