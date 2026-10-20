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
            for (var i = 0; i < stopWaypoints.Count; i++)
            {
                if (stopWaypoints[i] < 0 || stopWaypoints[i] >= waypointCount
                    || (i > 0 && stopWaypoints[i] <= stopWaypoints[i - 1]))
                    throw new ArgumentException("Stop waypoint indices must be ordered and in range.");
            }
            var result = new List<Interval>();
            var count = Math.Max(0, stopWaypoints.Count - 1) + (closed && stopWaypoints.Count > 1 ? 1 : 0);
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % stopWaypoints.Count;
                var interval = new Interval { From = i, To = next };
                var index = stopWaypoints[i];
                while (index != stopWaypoints[next])
                {
                    interval.SegmentIndices.Add(index);
                    index = (index + 1) % waypointCount;
                }
                result.Add(interval);
            }
            return result;
        }
    }
}
