using System;
using System.Threading;

namespace CSLModernMap.Exporter
{
    internal sealed class SimulationCaptureGate
    {
        private readonly object sync = new object();
        private ManualResetEvent released = new ManualResetEvent(false);
        internal volatile bool Ready, TimedOut;

        internal void Hold()
        {
            Ready = true;
            try { if (!released.WaitOne(30000, false)) TimedOut = true; }
            finally { lock (sync) { released.Close(); released = null; } }
        }

        internal void Release() { lock (sync) { if (released != null) released.Set(); } }
    }
}
