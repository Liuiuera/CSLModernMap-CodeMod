using System;
using System.Collections.Generic;

namespace CSLModernMap.Systems
{
    /// <summary>以原始 waypoint 下标划分停靠区间，重复停靠保留独立序号。</summary>
    internal static class TransitRouteIntervals
    {
        internal sealed class Interval
        {
            internal int From;
            internal int To;
            internal readonly List<int> SegmentIndices = new List<int>();
        }

        internal static List<Interval> Build(IReadOnlyList<int> stopWaypoints, int waypointCount, bool closed)
        {
            var result = new List<Interval>();
            var count = Math.Max(0, stopWaypoints.Count - 1) + (closed && stopWaypoints.Count > 1 ? 1 : 0);
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % stopWaypoints.Count;
                var interval = new Interval { From = i, To = next };
                var end = stopWaypoints[next] + (next == 0 ? waypointCount : 0);
                for (var index = stopWaypoints[i]; index < end; index++)
                    interval.SegmentIndices.Add(index % waypointCount);
                result.Add(interval);
            }
            return result;
        }
    }
}
