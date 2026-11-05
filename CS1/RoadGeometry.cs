using System;
using System.Collections.Generic;
using ColossalFramework.Math;
using CSLModernMap.Export;
using UnityEngine;
using Clipping = global::CSLModernMap.Geometry.ClipperLib;

namespace CSLModernMap.Exporter
{
    internal static class RoadGeometry
    {
        internal struct Corner
        {
            internal Vector3 Position, Direction;
            internal bool Smooth;
        }
        internal sealed class Mouth
        {
            internal int SegmentId;
            internal Corner Left, Right;
            internal Vector3 Direction;
            internal float EndRadius;
            internal ItemClass.Service ConnectionService;
            internal bool OnlySameGroup;
            internal NetInfo.ConnectGroup Group;
        }
        private static XMLVector3 Xml(Vector3 p) { return new XMLVector3(p.x, p.y, p.z); }
        internal static List<XMLVector3> Centerline(NetSegment segment, NetNode[] nodes)
        {
            Vector3 a = nodes[segment.m_startNode].m_position, d = nodes[segment.m_endNode].m_position, b, c;
            NetSegment.CalculateMiddlePoints(a, segment.m_startDirection, d, segment.m_endDirection,
                (nodes[segment.m_startNode].m_flags & NetNode.Flags.Middle) != 0,
                (nodes[segment.m_endNode].m_flags & NetNode.Flags.Middle) != 0, out b, out c);
            return Sample(new Bezier3(a, b, c, d));
        }
        internal static List<XMLVector3> Sample(Bezier3 curve)
        {
            List<XMLVector3> result = new List<XMLVector3>(); result.Add(Xml(curve.a));
            Subdivide(curve.a, curve.b, curve.c, curve.d, result, 0);
            return result;
        }
        private static void Subdivide(Vector3 a, Vector3 b, Vector3 c, Vector3 d, List<XMLVector3> points, int depth)
        {
            // The convex hull bound catches inflections that a midpoint-only test
            // misses. Y uses the Bernstein control deviation from linear Y(t).
            bool flat = Flat(new Bezier3(a, b, c, d));
            if (flat) { points.Add(Xml(d)); return; }
            if (depth >= 16) throw new InvalidOperationException("Curve exceeds adaptive subdivision limit");
            Vector3 ab = (a + b) * 0.5f, bc = (b + c) * 0.5f, cd = (c + d) * 0.5f;
            Vector3 abc = (ab + bc) * 0.5f, bcd = (bc + cd) * 0.5f, middle = (abc + bcd) * 0.5f;
            Subdivide(a, ab, abc, middle, points, depth + 1); Subdivide(middle, bcd, cd, d, points, depth + 1);
        }
        private static bool Flat(Bezier3 c)
        { return PlanarDeviation(c.b, (c.a * 2 + c.d) / 3) <= 0.5f && PlanarDeviation(c.c, (c.a + c.d * 2) / 3) <= 0.5f
            && Math.Abs(c.b.y - (c.a.y * 2 + c.d.y) / 3) <= 0.25f && Math.Abs(c.c.y - (c.a.y + c.d.y * 2) / 3) <= 0.25f; }
        private static double PlanarDeviation(Vector3 p, Vector3 reference)
        { double x = p.x - reference.x, z = p.z - reference.z; return Math.Sqrt(x * x + z * z); }
        private static Bezier3 Split(Bezier3 c, bool first)
        {
            Vector3 ab = (c.a + c.b) * 0.5f, bc = (c.b + c.c) * 0.5f, cd = (c.c + c.d) * 0.5f;
            Vector3 abc = (ab + bc) * 0.5f, bcd = (bc + cd) * 0.5f, middle = (abc + bcd) * 0.5f;
            return first ? new Bezier3(c.a, ab, abc, middle) : new Bezier3(middle, bcd, cd, c.d);
        }
        private static void BodyFaces(Bezier3 left, Bezier3 right, List<List<XMLVector3>> faces, int depth)
        {
            if (Flat(left) && Flat(right))
            {
                faces.Add(new List<XMLVector3> { Xml(left.a), Xml(left.d), Xml(right.d) });
                faces.Add(new List<XMLVector3> { Xml(left.a), Xml(right.d), Xml(right.a) }); return;
            }
            if (depth >= 16) throw new InvalidOperationException("Road boundary exceeds subdivision limit");
            BodyFaces(Split(left, true), Split(right, true), faces, depth + 1);
            BodyFaces(Split(left, false), Split(right, false), faces, depth + 1);
        }
        private static void CapFaces(List<XMLVector3> cap, Vector3 center, List<List<XMLVector3>> faces)
        { for (int i = 1; i < cap.Count; i++) faces.Add(new List<XMLVector3> { Xml(center), cap[i - 1], cap[i] }); }
        internal static NetworkFacts Facts(NetSegment segment, XMLNode start, XMLNode end)
        {
            NetInfo info = segment.Info; NetAI ai = info.m_netAI;
            NetworkFacts result = new NetworkFacts();
            result.Mode = ai.IsUnderground() || ai.IsTunnel() ? "TUNNEL" : ai.IsBridge() ? "ELEVATED"
                : start.UnderGround || end.UnderGround ? "TUNNEL" : start.OnGround && end.OnGround ? "GROUND" : "ELEVATED";
            result.Invert = (segment.m_flags & NetSegment.Flags.Invert) != 0;
            RoadBaseAI road = ai as RoadBaseAI; result.Highway = road != null && road.m_highwayRules;
            bool forward = false, backward = false;
            bool airport = false, pedestrian = false, water = false;
            if (info.m_lanes != null && info.m_lanes.Length > 0)
                result.CarForward = result.CarBackward = false;
            if (info.m_lanes != null) foreach (NetInfo.Lane lane in info.m_lanes)
            {
                string vehicles = lane.m_vehicleType.ToString();
                pedestrian |= (lane.m_laneType & NetInfo.LaneType.Pedestrian) != 0 || ContainsFlag(vehicles, "Bicycle");
                if ((lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                { airport |= ContainsFlag(vehicles, "Plane"); water |= ContainsFlag(vehicles, "Ferry") || ContainsFlag(vehicles, "Ship"); }
                string[] names = { "Metro", "Train", "Tram", "Monorail", "CableCar" };
                string[] kinds = { "METRO", "RAIL", "TRAM", "MONORAIL", "CABLECAR" };
                for (int i = 0; i < names.Length; i++) if (ContainsFlag(vehicles, names[i]) && !result.Tracks.Contains(kinds[i])) result.Tracks.Add(kinds[i]);
                if ((lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) == 0
                    || (lane.m_vehicleType & VehicleInfo.VehicleType.Car) == 0) continue;
                NetInfo.Direction direction = lane.m_finalDirection;
                if ((direction & (NetInfo.Direction.Forward | NetInfo.Direction.Backward)) == 0)
                {
                    if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != 0)
                    {
                        // Unknown direction permits either side without replacing known access.
                        if (result.CarForward != true) result.CarForward = null;
                        if (result.CarBackward != true) result.CarBackward = null;
                    }
                    continue;
                }
                result.CarLaneCount++;
                forward |= (direction & NetInfo.Direction.Forward) != 0;
                backward |= (direction & NetInfo.Direction.Backward) != 0;
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != 0)
                {
                    NetInfo.Direction effective = EffectiveDirection(direction, result.Invert);
                    result.CarForward |= (effective & NetInfo.Direction.Forward) != 0;
                    result.CarBackward |= (effective & NetInfo.Direction.Backward) != 0;
                }
            }
            result.Direction = forward && !backward ? 1 : backward && !forward ? -1 : 0;
            if (result.Invert) result.Direction = -result.Direction;
            string name = (info.name ?? "").ToLowerInvariant();
            string[] lineTerms = { "cable car line", "cablecar line", "monorail line", "metro line", "train line", "tram line", "bus line", "trolleybus line", "ship line", "ferry line", "airplane line", "helicopter line" };
            string[] transits = { "CABLECAR", "CABLECAR", "MONORAIL", "METRO", "TRAIN", "TRAM", "BUS", "TROLLEYBUS", "SHIP", "FERRY", "AIRPLANE", "HELICOPTER" };
            for (int i = 0; i < lineTerms.Length; i++) if (name.Contains(lineTerms[i])) { result.Kind = "STOP_LINE"; result.TransitFor = transits[i]; return result; }
            result.Kind = result.CarLaneCount > 0 || airport || road != null ? "ROAD" : result.Tracks.Count > 0 ? result.Tracks[0]
                : (!water || ai is ShipDockAI) && (pedestrian || info.GetService() == ItemClass.Service.Beautification) ? "BEAUTIFICATION" : "OTHER";
            return result;
        }
        internal static void CaptureCarSpeeds(NetSegment segment, NetworkFacts result)
        {
            NetInfo info = segment.Info;
            uint laneId = segment.m_lanes;
            for (int i = 0; info.m_lanes != null && i < info.m_lanes.Length && laneId != 0; i++)
            {
                NetInfo.Lane lane = info.m_lanes[i];
                if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != 0
                    && (lane.m_vehicleType & VehicleInfo.VehicleType.Car) != 0)
                {
                    float limit = TrafficManager.Constants.ManagerFactory.SpeedLimitManager
                        .GetGameSpeedLimit(laneId, lane) * (50f / 3.6f);
                    NetInfo.Direction effective = EffectiveDirection(lane.m_finalDirection, result.Invert);
                    if (!float.IsNaN(limit) && !float.IsInfinity(limit) && limit > 0)
                    {
                        if ((effective & NetInfo.Direction.Forward) != 0)
                            result.CarForwardSpeedMps = System.Math.Max(result.CarForwardSpeedMps, limit);
                        if ((effective & NetInfo.Direction.Backward) != 0)
                            result.CarBackwardSpeedMps = System.Math.Max(result.CarBackwardSpeedMps, limit);
                    }
                }
                laneId = NetManager.instance.m_lanes.m_buffer[laneId].m_nextLane;
            }
        }
        private static bool ContainsFlag(string values, string value)
        { foreach (string item in values.Split(',')) if (item.Trim() == value) return true; return false; }
        internal static NetInfo.Direction EffectiveDirection(NetInfo.Direction direction, bool invert)
        {
            if (!invert) return direction;
            NetInfo.Direction result = direction & ~(NetInfo.Direction.Forward | NetInfo.Direction.Backward);
            if ((direction & NetInfo.Direction.Forward) != 0) result |= NetInfo.Direction.Backward;
            if ((direction & NetInfo.Direction.Backward) != 0) result |= NetInfo.Direction.Forward;
            return result;
        }
        // Traffic uses the (-dz, dx) normal of the exported start-to-end curve.
        // NetInfo lane positions use the opposite side unless the segment is inverted.
        internal static float EffectiveLanePosition(float position, bool invert)
        { return invert ? position : -position; }
        internal static void CaptureTracks(ushort id, NetSegment segment, NetManager manager, NetworkFacts facts)
        {
            if (facts.Kind != "ROAD" || facts.Tracks.Count == 0) return;
            string[] kinds = { "METRO", "RAIL", "TRAM", "MONORAIL", "CABLECAR" };
            string[] vehicles = { "Metro", "Train", "Tram", "Monorail", "CableCar" };
            Dictionary<string, List<Bezier3>> curves = new Dictionary<string, List<Bezier3>>();
            uint laneId = segment.m_lanes; HashSet<uint> visited = new HashSet<uint>();
            Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
            for (int i = 0; i < segment.Info.m_lanes.Length && laneId != 0; i++)
            {
                if (laneId >= manager.m_lanes.m_buffer.Length || !visited.Add(laneId)) throw new InvalidOperationException("Invalid track lane chain: " + id);
                NetLane lane = manager.m_lanes.m_buffer[laneId]; NetInfo.Lane definition = segment.Info.m_lanes[i];
                if (lane.m_segment != id) throw new InvalidOperationException("Track lane belongs to another segment: " + id);
                if ((definition.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0)
                for (int t = 0; t < kinds.Length; t++) if (ContainsFlag(definition.m_vehicleType.ToString(), vehicles[t]))
                {
                    List<Bezier3> group; if (!curves.TryGetValue(kinds[t], out group)) { group = new List<Bezier3>(); curves.Add(kinds[t], group); facts.TrackWidth[kinds[t]] = 0; }
                    Bezier3 curve = lane.m_bezier;
                    if ((curve.d - start).sqrMagnitude < (curve.a - start).sqrMagnitude) curve = new Bezier3(curve.d, curve.c, curve.b, curve.a);
                    group.Add(curve); facts.TrackWidth[kinds[t]] += definition.m_width;
                }
                laneId = lane.m_nextLane;
            }
            foreach (string kind in facts.Tracks)
            {
                List<Bezier3> group;
                if (!curves.TryGetValue(kind, out group) || group.Count == 0) throw new InvalidOperationException("No native " + kind + " lane curve: " + id);
                Vector3 a = Vector3.zero, b = Vector3.zero, c = Vector3.zero, d = Vector3.zero;
                foreach (Bezier3 curve in group) { a += curve.a; b += curve.b; c += curve.c; d += curve.d; }
                facts.TrackGeometry[kind] = Sample(new Bezier3(a / group.Count, b / group.Count, c / group.Count, d / group.Count));
            }
        }
        private static Corner GetCorner(ref NetSegment segment, ushort id, bool start, bool left)
        {
            Corner result = new Corner();
            segment.CalculateCorner(id, true, start, left, out result.Position, out result.Direction, out result.Smooth);
            return result;
        }
        private static Bezier3 Connect(Corner a, Corner d, bool node)
        {
            Vector3 b, c;
            NetSegment.CalculateMiddlePoints(a.Position, node ? -a.Direction : a.Direction,
                d.Position, node ? -d.Direction : d.Direction, node || a.Smooth, node || d.Smooth, out b, out c);
            return new Bezier3(a.Position, b, c, d.Position);
        }
        private static List<XMLVector3> Half(Bezier3 curve, bool first)
        {
            Vector3 ab = (curve.a + curve.b) * 0.5f, bc = (curve.b + curve.c) * 0.5f, cd = (curve.c + curve.d) * 0.5f;
            Vector3 abc = (ab + bc) * 0.5f, bcd = (bc + cd) * 0.5f, mid = (abc + bcd) * 0.5f;
            return Sample(first ? new Bezier3(curve.a, ab, abc, mid) : new Bezier3(mid, bcd, cd, curve.d));
        }
        private static List<XMLVector3> Cap(List<Mouth> mouths, int index, NetNode.Flags flags, Vector3 position)
        {
            Mouth own = mouths[index]; List<XMLVector3> result = new List<XMLVector3>();
            if (mouths.Count == 1)
            {
                float radius = own.EndRadius * 1.3333334f;
                return Sample(new Bezier3(own.Left.Position, own.Left.Position - own.Left.Direction * radius,
                    own.Right.Position - own.Right.Direction * radius, own.Right.Position));
            }
            if ((flags & NetNode.Flags.Middle) != 0)
            { result.Add(Xml(own.Left.Position)); result.Add(Xml(own.Right.Position)); return result; }
            Mouth next = mouths[(index + 1) % mouths.Count], previous = mouths[(index + mouths.Count - 1) % mouths.Count];
            // Native node sides join CalculateCorner mouths with reversed edge
            // tangents and both smooth flags. Split each side at the same Bezier
            // midpoint so neighboring segments own matching node sectors.
            result.AddRange(Half(Connect(own.Left, next.Right, true), true));
            result.Add(Xml(position));
            result.AddRange(Half(Connect(previous.Left, own.Right, true), false));
            return result;
        }
        internal sealed class SurfaceCapture
        {
            internal readonly Dictionary<int, List<Mouth>> Mouths = new Dictionary<int, List<Mouth>>();
            internal readonly Dictionary<int, Mouth[]> Endpoints = new Dictionary<int, Mouth[]>();
            internal readonly Dictionary<int, SurfaceNode> Nodes = new Dictionary<int, SurfaceNode>();
        }
        internal struct SurfaceNode
        { internal NetNode.Flags Flags; internal Vector3 Position; }
        internal static SurfaceCapture CaptureSurfaces(MapSnapshot snapshot, NetManager manager)
        {
            SurfaceCapture captured = new SurfaceCapture();
            Dictionary<int, List<Mouth>> mouths = captured.Mouths;
            Dictionary<int, Mouth[]> endpoints = captured.Endpoints;
            foreach (XMLSegment record in snapshot.SegmentList)
            {
                NetworkFacts facts;
                if (!snapshot.Networks.TryGetValue(record.Id, out facts)) continue;
                if (facts.Kind != "ROAD" && facts.Kind != "BEAUTIFICATION") continue;
                NetSegment segment = manager.m_segments.m_buffer[record.Id];
                try
                {
                    Mouth[] pair = new Mouth[2]; int[] nodeIds = { record.StartNodeId, record.EndNodeId };
                    for (int i = 0; i < 2; i++)
                    {
                        Mouth mouth = new Mouth { SegmentId = record.Id, Left = GetCorner(ref segment, (ushort)record.Id, i == 0, true),
                            Right = GetCorner(ref segment, (ushort)record.Id, i == 0, false), Direction = i == 0 ? segment.m_startDirection : segment.m_endDirection,
                            EndRadius = segment.Info.m_netAI.GetEndRadius() };
                        mouth.ConnectionService = segment.Info.GetConnectionClass().m_service;
                        mouth.OnlySameGroup = segment.Info.m_onlySameConnectionGroup; mouth.Group = segment.Info.m_connectGroup;
                        pair[i] = mouth;
                    }
                    // Commit both ends together so a failed segment cannot alter neighboring junctions.
                    for (int i = 0; i < 2; i++)
                    {
                        List<Mouth> list;
                        if (!captured.Nodes.ContainsKey(nodeIds[i]))
                        {
                            NetNode node = manager.m_nodes.m_buffer[nodeIds[i]];
                            captured.Nodes.Add(nodeIds[i], new SurfaceNode { Flags = node.m_flags, Position = node.m_position });
                        }
                        if (!mouths.TryGetValue(nodeIds[i], out list)) { list = new List<Mouth>(); mouths.Add(nodeIds[i], list); }
                        list.Add(pair[i]);
                    }
                    endpoints.Add(record.Id, pair);
                }
                catch (Exception ex) { snapshot.Problems.Add("Segment " + record.Id + " corners: " + ex.Message); }
            }
            foreach (List<Mouth> list in mouths.Values) list.Sort(CompareMouths);
            return captured;
        }
        internal static void BuildFootprints(MapSnapshot snapshot, ExportProgress progress = null)
        {
            SurfaceCapture captured = snapshot.Surfaces;
            if (captured == null)
            {
                snapshot.Problems.Add("Road surfaces omitted: capture unavailable; segment centerlines retained.");
                return;
            }
            Dictionary<int, List<Mouth>> mouths = captured.Mouths;
            Dictionary<int, Mouth[]> endpoints = captured.Endpoints;
            int processed = 0;
            foreach (XMLSegment record in snapshot.SegmentList)
            {
                processed++;
                if ((processed & 31) == 0) ExportStages.Report(progress, 40 + 20 * processed / snapshot.SegmentList.Count, "EXPORT_PROGRESS_SURFACES");
                Mouth[] pair; if (!endpoints.TryGetValue(record.Id, out pair)) continue;
                try
                {
                    List<Mouth> end = mouths[record.EndNodeId], start = mouths[record.StartNodeId];
                    SurfaceNode startNode = captured.Nodes[record.StartNodeId], endNode = captured.Nodes[record.EndNodeId];
                    List<XMLVector3> ring = BuildRing(pair[0], pair[1], start, end, startNode.Flags, endNode.Flags, startNode.Position, endNode.Position);
                    snapshot.Networks[record.Id].Footprint = ring;
                }
                catch (Exception ex) { snapshot.Problems.Add("Segment " + record.Id + " footprint failed; centerline fallback: " + ex.Message); }
            }
            List<string> missing = new List<string>();
            foreach (XMLSegment record in snapshot.SegmentList)
            {
                NetworkFacts facts;
                if (!snapshot.Networks.TryGetValue(record.Id, out facts)) continue;
                if ((facts.Kind == "ROAD" || facts.Kind == "BEAUTIFICATION") && facts.Footprint == null)
                    missing.Add(record.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            if (missing.Count > 0)
                snapshot.Problems.Add("No native footprint for " + missing.Count
                    + " road segments; centerline fallback used: " + string.Join(",", missing.ToArray()));
        }
        internal static List<XMLVector3> BuildRing(Mouth startMouth, Mouth endMouth, List<Mouth> start, List<Mouth> end,
            NetNode.Flags startFlags, NetNode.Flags endFlags, Vector3 startPosition, Vector3 endPosition, bool validate = true)
        {
            start = Compatible(startMouth, start); end = Compatible(endMouth, end);
            // Road bodies and node sectors overlap on short, wide segments. A
            // concatenated perimeter can fold across itself even when the native
            // surfaces are valid. Compute their planar union before tracing the
            // boundary; never replace native edges with a centerline buffer.
            List<List<XMLVector3>> faces = new List<List<XMLVector3>>();
            List<XMLVector3> body = Sample(Connect(startMouth.Left, endMouth.Right, false));
            List<XMLVector3> right = Sample(Connect(startMouth.Right, endMouth.Left, false)); right.Reverse();
            List<XMLVector3> startCap = Cap(start, start.IndexOf(startMouth), startFlags, startPosition);
            List<XMLVector3> endCap = Cap(end, end.IndexOf(endMouth), endFlags, endPosition);
            BodyFaces(Connect(startMouth.Left, endMouth.Right, false), Connect(startMouth.Right, endMouth.Left, false), faces, 0);
            CapFaces(startCap, startPosition, faces); CapFaces(endCap, endPosition, faces);
            endCap.Reverse(); body.AddRange(endCap); body.AddRange(right); startCap.Reverse(); body.AddRange(startCap); faces.Add(body);
            List<XMLVector3> ring = Union(faces);
            if (validate && !Valid(ring)) throw new InvalidOperationException("Non-simple or degenerate road footprint");
            return ring;
        }
        private static List<XMLVector3> Union(List<List<XMLVector3>> surfaces)
        {
            Clipping.Clipper clipper = new Clipping.Clipper { StrictlySimple = true };
            foreach (List<XMLVector3> surface in surfaces)
            {
                List<Clipping.IntPoint> path = new List<Clipping.IntPoint>();
                foreach (XMLVector3 p in surface)
                {
                    if (float.IsNaN(p.X) || float.IsNaN(p.Z) || float.IsInfinity(p.X) || float.IsInfinity(p.Z))
                        throw new InvalidOperationException("Non-finite native road boundary");
                    path.Add(new Clipping.IntPoint((long)Math.Round(p.X * 1000.0), (long)Math.Round(p.Z * 1000.0)));
                }
                if (Clipping.Clipper.Area(path) < 0) path.Reverse();
                clipper.AddPath(path, Clipping.PolyType.ptSubject, true);
            }
            Clipping.PolyTree tree = new Clipping.PolyTree();
            if (!clipper.Execute(Clipping.ClipType.ctUnion, tree, Clipping.PolyFillType.pftNonZero, Clipping.PolyFillType.pftNonZero))
                throw new InvalidOperationException("Native surface union failed");
            Clipping.ClipperOffset closing = new Clipping.ClipperOffset(2, 0.25);
            // Integer clipping can leave hairline cracks at shared triangle edges.
            // Close at 1 cm (well below the 0.5 m sampling error), then undo the
            // dilation. This is applied to native surfaces, never a road buffer.
            closing.AddPaths(Clipping.Clipper.PolyTreeToPaths(tree), Clipping.JoinType.jtRound, Clipping.EndType.etClosedPolygon);
            List<List<Clipping.IntPoint>> expanded = new List<List<Clipping.IntPoint>>(); closing.Execute(ref expanded, 10);
            closing.Clear(); closing.AddPaths(expanded, Clipping.JoinType.jtRound, Clipping.EndType.etClosedPolygon);
            closing.Execute(ref tree, -10);
            List<Clipping.IntPoint> boundary = null;
            foreach (Clipping.PolyNode child in tree.Childs)
            {
                // Discard only numerical dust below 0.001 m², never a road part.
                if (Math.Abs(Clipping.Clipper.Area(child.Contour)) < 1000) continue;
                if (boundary != null) throw new InvalidOperationException("Disconnected native road surface");
                boundary = Clipping.Clipper.CleanPolygon(child.Contour, 10);
            }
            if (boundary == null)
            {
                string areas = "";
                foreach (Clipping.PolyNode child in tree.Childs) { areas += " " + Clipping.Clipper.Area(child.Contour) / 1000000.0; foreach(var hole in child.Childs) areas += " hole:" + Clipping.Clipper.Area(hole.Contour)/1000000.0; }
                throw new InvalidOperationException("Native road surface has " + tree.ChildCount + " components / " + (tree.ChildCount == 1 ? tree.Childs[0].ChildCount : 0) + " holes; areas=" + areas);
            }
            // A single segment ribbon plus its two local node sectors is solid.
            // Interior rings from folded short-segment mesh cells are covered by
            // that surface. Never union an entire junction/roundabout here: its
            // island lies outside every segment's local sector and stays empty.
            List<XMLVector3> ring = new List<XMLVector3>();
            foreach (Clipping.IntPoint p in boundary) ring.Add(new XMLVector3(p.X / 1000f, 0, p.Y / 1000f));
            Clean(ring); return ring;
        }
        private static List<Mouth> Compatible(Mouth own, List<Mouth> mouths)
        {
            List<Mouth> result = new List<Mouth>();
            foreach (Mouth mouth in mouths)
                if (mouth == own || (mouth.ConnectionService == own.ConnectionService
                    && (!(mouth.OnlySameGroup || own.OnlySameGroup) || (mouth.Group & own.Group) != 0))) result.Add(mouth);
            return result;
        }
        internal static int CompareMouths(Mouth a, Mouth b)
        { int angle = Math.Atan2(a.Direction.z, a.Direction.x).CompareTo(Math.Atan2(b.Direction.z, b.Direction.x)); return angle == 0 ? a.SegmentId.CompareTo(b.SegmentId) : angle; }
        private static void Clean(List<XMLVector3> ring)
        {
            for (int i = ring.Count - 1; i >= 0; i--)
            {
                XMLVector3 a = ring[i], b = ring[(i + 1) % ring.Count];
                if (Math.Abs(a.X - b.X) < 0.001 && Math.Abs(a.Z - b.Z) < 0.001) ring.RemoveAt(i);
            }
            // A shared Middle-node corner can coincide with the center partition.
            // Remove collinear zero-area spikes before checking topology.
            bool changed = true;
            while (changed && ring.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < ring.Count; i++)
                    if (Math.Abs(Cross(ring[(i + ring.Count - 1) % ring.Count], ring[i], ring[(i + 1) % ring.Count])) < 1e-5)
                    { ring.RemoveAt(i); changed = true; break; }
            }
        }
        private static double Cross(XMLVector3 a, XMLVector3 b, XMLVector3 c)
        { return (double)(b.X - a.X) * (c.Z - a.Z) - (double)(b.Z - a.Z) * (c.X - a.X); }
        private static bool Intersect(XMLVector3 a, XMLVector3 b, XMLVector3 c, XMLVector3 d)
        {
            if (Math.Max(a.X, b.X) + 1e-4 < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) + 1e-4 < Math.Min(a.X, b.X)
                || Math.Max(a.Z, b.Z) + 1e-4 < Math.Min(c.Z, d.Z) || Math.Max(c.Z, d.Z) + 1e-4 < Math.Min(a.Z, b.Z)) return false;
            return Cross(a, b, c) * Cross(a, b, d) <= 0 && Cross(c, d, a) * Cross(c, d, b) <= 0;
        }
        internal static bool Valid(List<XMLVector3> ring)
        {
            if (ring.Count < 3) return false; double area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                XMLVector3 a = ring[i], b = ring[(i + 1) % ring.Count];
                if (float.IsNaN(a.X) || float.IsNaN(a.Z) || float.IsInfinity(a.X) || float.IsInfinity(a.Z)) return false;
                area += (double)a.X * b.Z - (double)a.Z * b.X;
                for (int j = i + 2; j < ring.Count; j++)
                {
                    if (i == 0 && j == ring.Count - 1) continue;
                    if (Intersect(a, b, ring[j], ring[(j + 1) % ring.Count])) return false;
                }
            }
            return Math.Abs(area) > 0.01;
        }
    }
}
