using System.Collections.Generic;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    internal static class TransitPathContinuity
    {
        internal static float CurveJoinDistance(float routeWidth)
        {
            // SegmentCurveSystem: nodeDistance = width * 2.5，距离不超过其 1/4 时省略连接曲线。
            return routeWidth * 2.5f * 0.25f;
        }

        internal static bool Join(List<float3> previous, List<float3> points, float maxDistance)
        {
            if (points.Count < 2) return false;
            if (previous.Count == 0) return true;
            var end = previous[previous.Count - 1];
            if (math.distance(end, points[0]) > maxDistance) return false;
            // CMM 输出连续折线；仅衔接同一有向原生曲线序列中游戏允许省略的短连接。
            if (!end.Equals(points[0])) points.Insert(0, end);
            return true;
        }
    }
}
