using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>计算道路和路口的导出几何</summary>
    internal static class Cs2Geometry
    {
        private const float NodeInterpolationThreshold = 1f;
        private const float NodeInterpolationGap = 0.5f;
        private const float RoundaboutInterpolationThreshold = 0.5f;
        private const float RoundaboutInterpolationGap = 0.2f;
        private const float SegmentInterpolationThreshold = 1f;
        private const float SegmentInterpolationGap = 1f;

        internal sealed class RoadGeometryContext
        {
            private readonly Dictionary<Entity, float> m_AttachedRadii;

            internal RoadGeometryContext(Dictionary<Entity, float> attachedRadii)
            {
                m_AttachedRadii = attachedRadii;
            }

            internal bool TryGetAttachedRadius(Entity node, out float radius)
            {
                return m_AttachedRadii.TryGetValue(node, out radius);
            }
        }

        private readonly struct RoundaboutData
        {
            internal RoundaboutData(float3 position, float innerRadius)
            {
                Position = position;
                InnerRadius = innerRadius;
            }

            internal float3 Position { get; }
            internal float InnerRadius { get; }
        }

        internal readonly struct RoadGeometryResult
        {
            internal RoadGeometryResult(List<float3> centerline, List<float3> footprint)
            {
                Centerline = centerline;
                Footprint = footprint;
            }

            internal List<float3> Centerline { get; }
            internal List<float3> Footprint { get; }
        }

        internal static RoadGeometryResult BuildRoadGeometry(
            EntityManager em,
            RoadGeometryContext context,
            Entity entity,
            Game.Net.Edge edge,
            Game.Net.Curve curve,
            float width,
            bool includeFootprint)
        {
            return new RoadGeometryResult(
                BuildCenterline(em, entity, edge, curve),
                includeFootprint
                    ? BuildRoadFootprint(em, context, entity, edge, width)
                    : new List<float3>());
        }

        internal static RoadGeometryContext BuildRoadGeometryContext(EntityManager em)
        {
            var radii = new Dictionary<Entity, float>();
            using (var query = em.CreateEntityQuery(
                ComponentType.ReadOnly<Game.Objects.Attached>(),
                ComponentType.ReadOnly<Game.Prefabs.PrefabRef>()))
            using (var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var attachment = entities[i];
                    var parent = em.GetComponentData<Game.Objects.Attached>(attachment).m_Parent;
                    var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(attachment).m_Prefab;
                    if (parent == Entity.Null
                        || !em.HasComponent<Game.Prefabs.NetObjectData>(prefab)
                        || !em.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab))
                    {
                        continue;
                    }

                    var netObject = em.GetComponentData<Game.Prefabs.NetObjectData>(prefab);
                    if ((netObject.m_CompositionFlags.m_General
                         & Game.Prefabs.CompositionFlags.General.Roundabout) == 0)
                    {
                        continue;
                    }

                    var geometry = em.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab);
                    var circular = (geometry.m_Flags & Game.Objects.GeometryFlags.Circular) != 0;
                    var circularLeg = (geometry.m_Flags & Game.Objects.GeometryFlags.CircularLeg) != 0;
                    var radius = 0f;
                    if (circular)
                    {
                        radius = math.cmax(geometry.m_Size.xz) / 2f;
                    }
                    if (circularLeg)
                    {
                        var legRadius = math.cmax(geometry.m_LegSize.xz) / 2f;
                        radius = circular ? math.min(radius, legRadius) : legRadius;
                    }
                    if (radius > 0f)
                    {
                        radii[parent] = radius;
                    }
                }
            }
            return new RoadGeometryContext(radii);
        }

        /// <summary>将裁切后的路段中心线连接到节点并交由渲染端处理宽度过渡</summary>
        internal static List<float3> BuildCenterline(
            EntityManager em,
            Entity entity,
            Game.Net.Edge edge,
            Game.Net.Curve curve)
        {
            var points = new List<float3>();
            if (!em.HasComponent<Game.Net.StartNodeGeometry>(entity)
                || !em.HasComponent<Game.Net.EndNodeGeometry>(entity)
                || !em.HasComponent<Game.Net.Node>(edge.m_Start)
                || !em.HasComponent<Game.Net.Node>(edge.m_End))
            {
                Interpolate(curve.m_Bezier, points, 1f, 1f, true);
                return points;
            }

            var startCurve = em.GetComponentData<Game.Net.StartNodeGeometry>(entity)
                .m_Geometry.m_Middle;
            var endCurve = em.GetComponentData<Game.Net.EndNodeGeometry>(entity)
                .m_Geometry.m_Middle;
            var mainCurve = curve.m_Bezier;
            var includeStart = MathUtils.Length(startCurve) > 0f;
            var includeEnd = MathUtils.Length(endCurve) > 0f;

            IsContinuous(startCurve, mainCurve, out var startT, true);
            IsContinuous(mainCurve, endCurve, out var endT, false);
            var trimmedMain = Trim(
                mainCurve,
                includeStart ? startT : 0f,
                includeEnd ? endT : 1f);

            var startNodePosition = em.GetComponentData<Game.Net.Node>(edge.m_Start).m_Position;
            var endNodePosition = em.GetComponentData<Game.Net.Node>(edge.m_End).m_Position;

            if (includeStart)
            {
                Interpolate(
                    BuildStartConnection(startNodePosition, trimmedMain.a, trimmedMain.b),
                    points,
                    NodeInterpolationThreshold,
                    NodeInterpolationGap,
                    false);
            }

            Interpolate(trimmedMain, points, 1f, 1f, !includeEnd);

            if (includeEnd)
            {
                Interpolate(
                    BuildEndConnection(trimmedMain.d, trimmedMain.c, endNodePosition),
                    points,
                    NodeInterpolationThreshold,
                    NodeInterpolationGap,
                    true);
            }

            return points;
        }

        internal static List<float3> BuildRoadFootprint(
            EntityManager em,
            RoadGeometryContext context,
            Entity entity,
            Game.Net.Edge edge,
            float width)
        {
            var points = new List<float3>();
            if (width <= 0f
                || !em.HasComponent<Game.Net.EdgeGeometry>(entity)
                || !em.HasComponent<Game.Net.StartNodeGeometry>(entity)
                || !em.HasComponent<Game.Net.EndNodeGeometry>(entity)
                || !em.HasComponent<Game.Net.Node>(edge.m_Start)
                || !em.HasComponent<Game.Net.Node>(edge.m_End)
                || !em.HasBuffer<Game.Net.ConnectedEdge>(edge.m_Start)
                || !em.HasBuffer<Game.Net.ConnectedEdge>(edge.m_End))
            {
                return points;
            }

            var edgeGeometry = em.GetComponentData<Game.Net.EdgeGeometry>(entity);
            var startGeometry = em.GetComponentData<Game.Net.StartNodeGeometry>(entity);
            var endGeometry = em.GetComponentData<Game.Net.EndNodeGeometry>(entity);
            var startNode = em.GetComponentData<Game.Net.Node>(edge.m_Start);
            var endNode = em.GetComponentData<Game.Net.Node>(edge.m_End);
            var startRoundabout = em.HasComponent<Game.Net.Roundabout>(edge.m_Start);
            var endRoundabout = em.HasComponent<Game.Net.Roundabout>(edge.m_End);
            var startRoundaboutData = startRoundabout
                ? GetRoundaboutData(em, context, edge.m_Start, startNode.m_Position)
                : default;
            var endRoundaboutData = endRoundabout
                ? GetRoundaboutData(em, context, edge.m_End, endNode.m_Position)
                : default;
            var startTerminus = IsTerminus(em, edge.m_Start, startRoundabout);
            var endTerminus = IsTerminus(em, edge.m_End, endRoundabout);

            var scaler = width > 8f ? 1f : width / 8f;
            var nodeGap = NodeInterpolationGap * scaler;
            var nodeThreshold = NodeInterpolationThreshold * scaler;
            var roundaboutGap = RoundaboutInterpolationGap * scaler;
            var roundaboutThreshold = RoundaboutInterpolationThreshold * scaler;
            var segmentGap = SegmentInterpolationGap * scaler;
            var segmentThreshold = SegmentInterpolationThreshold * scaler;

            var mainLeftEnd = edgeGeometry.m_End.m_Left;
            var mainLeftStart = edgeGeometry.m_Start.m_Left;
            var mainRightEnd = MathUtils.Invert(edgeGeometry.m_End.m_Right);
            var mainRightStart = MathUtils.Invert(edgeGeometry.m_Start.m_Right);

            if (endRoundabout)
            {
                var leftFront = endGeometry.m_Geometry.m_Left.m_Left;
                var leftRear = endGeometry.m_Geometry.m_Right.m_Left;
                var rightFront = MathUtils.Invert(endGeometry.m_Geometry.m_Left.m_Right);
                var rightRear = MathUtils.Invert(endGeometry.m_Geometry.m_Right.m_Right);
                var leftContinuous = mainLeftEnd.d.Equals(leftFront.a);
                var rightContinuous = mainRightEnd.a.Equals(rightFront.d);

                Interpolate(mainLeftEnd, points, segmentThreshold, segmentGap, !leftContinuous);
                Interpolate(leftFront, points, roundaboutThreshold, roundaboutGap, false);
                Interpolate(leftRear, points, roundaboutThreshold, roundaboutGap, !endTerminus);
                if (!endTerminus)
                {
                    InterpolateRoundabout(
                        endRoundaboutData.Position,
                        endRoundaboutData.InnerRadius,
                        Azimuth(endRoundaboutData.Position.xz, leftRear.d.xz),
                        Azimuth(endRoundaboutData.Position.xz, rightRear.a.xz),
                        roundaboutGap,
                        points);
                }
                Interpolate(rightRear, points, roundaboutThreshold, roundaboutGap, false);
                Interpolate(rightFront, points, roundaboutThreshold, roundaboutGap, !rightContinuous);
                Interpolate(mainRightEnd, points, segmentThreshold, segmentGap, false);
            }
            else
            {
                var left = endGeometry.m_Geometry.m_Left.m_Left;
                var right = MathUtils.Invert(endGeometry.m_Geometry.m_Right.m_Right);
                var leftContinuous = mainLeftEnd.d.Equals(left.a);
                var rightContinuous = mainRightEnd.a.Equals(right.d);

                Interpolate(mainLeftEnd, points, segmentThreshold, segmentGap, !leftContinuous);
                Interpolate(left, points, nodeThreshold, nodeGap, true);
                if (!endTerminus)
                {
                    AddDistinct(points, endNode.m_Position);
                }
                Interpolate(right, points, nodeThreshold, nodeGap, !rightContinuous);
                Interpolate(mainRightEnd, points, segmentThreshold, segmentGap, false);
            }

            if (startRoundabout)
            {
                var leftFront = MathUtils.Invert(startGeometry.m_Geometry.m_Left.m_Right);
                var leftRear = MathUtils.Invert(startGeometry.m_Geometry.m_Right.m_Right);
                var rightFront = startGeometry.m_Geometry.m_Left.m_Left;
                var rightRear = startGeometry.m_Geometry.m_Right.m_Left;
                var leftContinuous = mainLeftStart.a.Equals(leftFront.d);
                var rightContinuous = mainRightStart.d.Equals(rightFront.a);

                Interpolate(mainRightStart, points, segmentThreshold, segmentGap, !rightContinuous);
                Interpolate(rightFront, points, roundaboutThreshold, roundaboutGap, false);
                Interpolate(rightRear, points, roundaboutThreshold, roundaboutGap, !startTerminus);
                if (!startTerminus)
                {
                    InterpolateRoundabout(
                        startRoundaboutData.Position,
                        startRoundaboutData.InnerRadius,
                        Azimuth(startRoundaboutData.Position.xz, rightRear.d.xz),
                        Azimuth(startRoundaboutData.Position.xz, leftRear.a.xz),
                        roundaboutGap,
                        points);
                }
                Interpolate(leftRear, points, roundaboutThreshold, roundaboutGap, false);
                Interpolate(leftFront, points, roundaboutThreshold, roundaboutGap, !leftContinuous);
                Interpolate(mainLeftStart, points, segmentThreshold, segmentGap, false);
            }
            else
            {
                var left = MathUtils.Invert(startGeometry.m_Geometry.m_Right.m_Right);
                var right = startGeometry.m_Geometry.m_Left.m_Left;
                var leftContinuous = mainLeftStart.a.Equals(left.d);
                var rightContinuous = mainRightStart.d.Equals(right.a);

                Interpolate(mainRightStart, points, segmentThreshold, segmentGap, !rightContinuous);
                Interpolate(right, points, nodeThreshold, nodeGap, true);
                if (!startTerminus)
                {
                    AddDistinct(points, startNode.m_Position);
                }
                Interpolate(left, points, nodeThreshold, nodeGap, !leftContinuous);
                Interpolate(mainLeftStart, points, segmentThreshold, segmentGap, false);
            }

            if (points.Count > 0)
            {
                AddDistinct(points, points[0]);
            }
            return points;
        }

        private static RoundaboutData GetRoundaboutData(
            EntityManager em,
            RoadGeometryContext context,
            Entity node,
            float3 position)
        {
            var roundabout = em.GetComponentData<Game.Net.Roundabout>(node);
            var width = 0f;
            var connected = em.GetBuffer<Game.Net.ConnectedEdge>(node, true);
            for (var i = 0; i < connected.Length; i++)
            {
                var network = connected[i].m_Edge;
                if (!em.HasComponent<Game.Net.Road>(network)
                    && !em.HasComponent<Game.Net.TramTrack>(network)
                    && !em.HasComponent<Game.Net.TrainTrack>(network)
                    && !em.HasComponent<Game.Net.SubwayTrack>(network))
                {
                    continue;
                }
                if (!em.HasComponent<Game.Net.Composition>(network))
                {
                    continue;
                }
                var composition = em.GetComponentData<Game.Net.Composition>(network).m_Edge;
                if (em.HasComponent<Game.Prefabs.NetCompositionData>(composition))
                {
                    var data = em.GetComponentData<Game.Prefabs.NetCompositionData>(composition);
                    width = math.max(width, data.m_Width / 2f);
                }
            }

            var innerRadius = context.TryGetAttachedRadius(node, out var attachedRadius)
                ? attachedRadius
                : roundabout.m_Radius - width;
            return new RoundaboutData(position, innerRadius);
        }

        private static bool IsTerminus(EntityManager em, Entity node, bool roundabout)
        {
            var connected = em.GetBuffer<Game.Net.ConnectedEdge>(node, true);
            var roadCount = 0;
            var pathRoadCount = 0;
            var trackCount = 0;
            for (var i = 0; i < connected.Length; i++)
            {
                var network = connected[i].m_Edge;
                if (em.HasComponent<Game.Net.Road>(network))
                {
                    roadCount++;
                    continue;
                }

                if (em.HasComponent<Game.Net.Composition>(network))
                {
                    var composition = em.GetComponentData<Game.Net.Composition>(network).m_Edge;
                    if (em.HasComponent<Game.Prefabs.RoadComposition>(composition)
                        || em.HasComponent<Game.Prefabs.TaxiwayComposition>(composition)
                        || em.HasComponent<Game.Prefabs.PathwayComposition>(composition))
                    {
                        pathRoadCount++;
                        continue;
                    }
                }

                if (em.HasComponent<Game.Net.TramTrack>(network)
                    || em.HasComponent<Game.Net.TrainTrack>(network)
                    || em.HasComponent<Game.Net.SubwayTrack>(network))
                {
                    trackCount++;
                }
            }

            var result = roadCount + pathRoadCount <= 2 && trackCount == 0;
            if (result && roundabout && roadCount > 1)
            {
                result = false;
            }
            return result;
        }

        private static double Azimuth(float2 from, float2 to)
        {
            var vector = to - from;
            var length = math.sqrt(vector.x * vector.x + vector.y * vector.y);
            if (length <= 0f)
            {
                return 0d;
            }
            var angle = math.asin(vector.x / length);
            if (vector.y < 0f)
            {
                return math.PI_DBL - angle;
            }
            return vector.x >= 0f ? angle : math.PI2_DBL + angle;
        }

        private static void InterpolateRoundabout(
            float3 center,
            float radius,
            double fromAzimuth,
            double toAzimuth,
            float threshold,
            List<float3> points)
        {
            if (radius <= 0f)
            {
                return;
            }
            if (fromAzimuth < toAzimuth)
            {
                fromAzimuth += math.PI2_DBL;
            }
            var maximumAngle = 4d * math.asin(math.sqrt(threshold / 2d / radius));
            if (maximumAngle > 60d)
            {
                maximumAngle = 60d;
            }
            var count = (int)math.ceil((fromAzimuth - toAzimuth) / maximumAngle) + 1;
            var step = (fromAzimuth - toAzimuth) / Math.Max(count - 1, 1);
            for (var i = 0; i < count; i++)
            {
                var angle = fromAzimuth - step * i;
                AddDistinct(points, new float3(
                    center.x + (float)(math.sin(angle) * radius),
                    center.y,
                    center.z + (float)(math.cos(angle) * radius)));
            }
        }

        private static Bezier4x3 BuildStartConnection(
            float3 nodePosition,
            float3 boundaryPoint,
            float3 boundaryTangent)
        {
            var span = math.distance(nodePosition, boundaryPoint);
            var handle = StartTrim(
                new Line3.Segment(boundaryPoint, boundaryTangent),
                span * 0.33f).b;
            return new Bezier4x3(nodePosition, nodePosition, handle, boundaryPoint);
        }

        private static Bezier4x3 BuildEndConnection(
            float3 boundaryPoint,
            float3 boundaryTangent,
            float3 nodePosition)
        {
            var span = math.distance(boundaryPoint, nodePosition);
            var handle = StartTrim(
                new Line3.Segment(boundaryPoint, boundaryTangent),
                span * 0.33f).b;
            return new Bezier4x3(boundaryPoint, handle, nodePosition, nodePosition);
        }

        private static Line3.Segment StartTrim(Line3.Segment line, float length)
        {
            var vector = (line.b - line.a) / MathUtils.Length(line) * length;
            return new Line3.Segment(line.a, line.a + vector);
        }

        internal static List<float3> BuildPath(Bezier4x3 curve, float2 targetDelta)
        {
            var points = new List<float3>();
            Interpolate(
                Trim(curve, targetDelta.x, targetDelta.y),
                points,
                2f,
                4f,
                true);
            return points;
        }

        private static bool IsContinuous(
            Bezier4x3 first,
            Bezier4x3 second,
            out float t,
            bool secondT)
        {
            if (secondT)
            {
                var distanceA = MathUtils.Distance(second, first.a, out var tA);
                var distanceD = MathUtils.Distance(second, first.d, out var tD);
                var continuous = distanceA > distanceD;
                t = continuous ? tD : tA;
                return continuous;
            }

            var firstDistanceA = MathUtils.Distance(first, second.a, out var firstTA);
            var firstDistanceD = MathUtils.Distance(first, second.d, out var firstTD);
            var firstContinuous = firstDistanceD > firstDistanceA;
            t = firstContinuous ? firstTA : firstTD;
            return firstContinuous;
        }

        private static Bezier4x3 Trim(Bezier4x3 curve, float t0, float t1)
        {
            var u0 = 1f - t0;
            var u1 = 1f - t1;
            var q1 = u0 * u0 * u0 * curve.a
                + 3f * t0 * u0 * u0 * curve.b
                + 3f * t0 * t0 * u0 * curve.c
                + t0 * t0 * t0 * curve.d;
            var q2 = u0 * u0 * u1 * curve.a
                + (2f * t0 * u0 * u1 + t1 * u0 * u0) * curve.b
                + (2f * t0 * t1 * u0 + t0 * t0 * u1) * curve.c
                + t0 * t0 * t1 * curve.d;
            var q3 = u0 * u1 * u1 * curve.a
                + (2f * t1 * u0 * u1 + t0 * u1 * u1) * curve.b
                + (2f * t0 * t1 * u1 + t1 * t1 * u0) * curve.c
                + t0 * t1 * t1 * curve.d;
            var q4 = u1 * u1 * u1 * curve.a
                + 3f * t1 * u1 * u1 * curve.b
                + 3f * t1 * t1 * u1 * curve.c
                + t1 * t1 * t1 * curve.d;
            return new Bezier4x3(q1, q2, q3, q4);
        }

        private static bool IsStraightLine(Bezier4x3 curve, float threshold)
        {
            var line = MathUtils.Line(curve);
            return MathUtils.Distance(line.xz, curve.b.xz, out _) <= threshold
                && MathUtils.Distance(line.xz, curve.c.xz, out _) <= threshold;
        }

        private static void Interpolate(
            Bezier4x3 curve,
            List<float3> points,
            float threshold,
            float gap,
            bool includeLast)
        {
            if (IsStraightLine(curve, threshold))
            {
                AddDistinct(points, curve.a);
                if (includeLast)
                {
                    AddDistinct(points, curve.d);
                }

                return;
            }

            var sections = (int)Math.Ceiling(MathUtils.Length(curve) / gap);
            for (var i = 0; i <= sections + 1; i++)
            {
                if (!includeLast && i == sections + 1)
                {
                    break;
                }

                AddDistinct(
                    points,
                    MathUtils.Position(curve, (float)i / (sections + 1)));
            }
        }

        private static void AddDistinct(List<float3> points, float3 point)
        {
            if (points.Count == 0 || !points[points.Count - 1].Equals(point))
            {
                points.Add(point);
            }
        }

    }
}
