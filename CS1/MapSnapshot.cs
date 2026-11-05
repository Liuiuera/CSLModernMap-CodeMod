using System.Collections.Generic;
using CSLModernMap.Export;

namespace CSLModernMap.Exporter
{
    // Reuse the existing in-memory content DTOs, never serialize/read XML on the
    // native path. All Unity access finishes before CMM mapping and file writing.
    public sealed class MapSnapshot : CSLExportXML
    {
        public readonly Dictionary<int, NetworkFacts> Networks = new Dictionary<int, NetworkFacts>();
        public byte[] TerrainHeight;
        public byte[] TerrainWater;
        public readonly List<string> Problems = new List<string>();
        public bool SimulationPaused;
        internal RoadGeometry.SurfaceCapture Surfaces;
    }

    public sealed class NetworkFacts
    {
        public string Mode;
        public string Kind;
        public string TransitFor;
        public int CarLaneCount;
        public int Direction;
        public bool? CarForward;
        public bool? CarBackward;
        public float CarForwardSpeedMps;
        public float CarBackwardSpeedMps;
        public bool CarSpeedReadFailed;
        public bool Highway;
        public bool Invert;
        public readonly List<string> Tracks = new List<string>();
        public readonly Dictionary<string, List<XMLVector3>> TrackGeometry = new Dictionary<string, List<XMLVector3>>();
        public readonly Dictionary<string, float> TrackWidth = new Dictionary<string, float>();
        public List<XMLVector3> Footprint;
    }
}
