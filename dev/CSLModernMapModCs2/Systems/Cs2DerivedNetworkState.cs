using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>整理网络的派生状态与交通模式</summary>
    internal sealed class Cs2DerivedNetworkState
    {
        private readonly Dictionary<string, int> m_NodeIds =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<NodeRecord> m_Nodes = new List<NodeRecord>();
        private readonly List<NetworkRecord> m_Networks = new List<NetworkRecord>();
        private readonly SortedDictionary<int, ModeRecord> m_Modes =
            new SortedDictionary<int, ModeRecord>();
        private int m_NextNodeId = -1;
        private int m_NextNetworkId = -1;

        internal int NodeCount => m_Nodes.Count;

        internal int NetworkCount => m_Networks.Count;

        internal void AddModes(int networkId, ModeRecord modes)
        {
            m_Modes[networkId] = modes;
        }

        internal int AddTrackOverlay(
            int sourceEntity,
            string kind,
            string mode,
            int startId,
            int endId,
            List<float3> points,
            float width)
        {
            return AddNetwork(
                sourceEntity,
                "track_overlay",
                kind,
                mode,
                null,
                startId,
                endId,
                points,
                width);
        }

        internal int AddRouteSegment(
            int sourceEntity,
            string transitKind,
            List<float3> points)
        {
            if (points == null || points.Count < 2)
            {
                return 0;
            }

            return AddNetwork(
                sourceEntity,
                "route_path",
                "STOP_LINE",
                "GROUND",
                transitKind,
                GetNodeId(points[0]),
                GetNodeId(points[points.Count - 1]),
                points,
                0f);
        }

        internal int AddRouteCarrier(
            int sourceEntity,
            string transitKind,
            string name,
            int startId,
            int endId,
            List<float3> points,
            List<int> pathSegmentIds)
        {
            return AddNetwork(
                sourceEntity,
                "route_link",
                "STOP_LINE",
                "GROUND",
                transitKind,
                startId,
                endId,
                points,
                0f,
                pathSegmentIds,
                name);
        }

        internal void AppendNodes(StringBuilder json, int existingCount)
        {
            for (var i = 0; i < m_Nodes.Count; i++)
            {
                if (existingCount > 0 || i > 0)
                {
                    json.AppendLine(",");
                }

                var item = m_Nodes[i];
                json.Append("    {\"id\": ").Append(item.Id)
                    .Append(", \"pos\": ");
                AppendVec3(json, item.Position);
                json.Append(", \"mode_hint\": \"GROUND\", \"station_service\": null, \"source_raw\": \"derived\"}");
            }

            if (m_Nodes.Count > 0)
            {
                json.AppendLine();
            }
        }

        internal void AppendNetworks(StringBuilder json, int existingCount)
        {
            for (var i = 0; i < m_Networks.Count; i++)
            {
                if (existingCount > 0 || i > 0)
                {
                    json.AppendLine(",");
                }

                var item = m_Networks[i];
                json.Append("    {\"id\": ").Append(item.Id)
                    .Append(", \"kind\": ");
                CmmJson.AppendString(json, item.Kind);
                json.Append(", \"start_id\": ").Append(item.StartId)
                    .Append(", \"end_id\": ").Append(item.EndId)
                    .Append(", \"geometry\": {\"points\": [");
                AppendPoints(json, item.Points);
                json.Append("]}, \"mode\": ");
                CmmJson.AppendString(json, item.Mode);
                json.Append(", \"width\": ").Append(FormatFloat(item.Width))
                    .Append(", \"lane_summary\": null, \"transit_for\": ");
                if (item.TransitKind == null)
                {
                    json.Append("null");
                }
                else
                {
                    CmmJson.AppendString(json, item.TransitKind);
                }

                json.Append(", \"path_segment_ids\": [");
                for (var p = 0; p < item.PathSegmentIds.Count; p++)
                {
                    if (p > 0)
                    {
                        json.Append(", ");
                    }

                    json.Append(item.PathSegmentIds[p]);
                }

                json.Append("], \"platform\": false, \"name\": ");
                CmmJson.AppendString(json, item.Name);
                json.Append(", \"display_name\": \"\", \"source_raw\": ");
                CmmJson.AppendString(json, item.Role);
                json.Append('}');
            }

            if (m_Networks.Count > 0)
            {
                json.AppendLine();
            }
        }

        internal string BuildNetworkModesJson()
        {
            var json = new StringBuilder();
            json.Append('{');
            var index = 0;
            foreach (var pair in m_Modes)
            {
                if (index++ > 0)
                {
                    json.Append(", ");
                }

                CmmJson.AppendString(json, pair.Key.ToString(CultureInfo.InvariantCulture));
                json.Append(": {\"car\": ").Append(pair.Value.Car)
                    .Append(", \"bicycle\": ").Append(pair.Value.Bicycle)
                    .Append(", \"bus\": ").Append(pair.Value.Bus)
                    .Append(", \"tram\": ").Append(pair.Value.Tram)
                    .Append(", \"train\": ").Append(pair.Value.Train)
                    .Append(", \"subway\": ").Append(pair.Value.Subway)
                    .Append('}');
            }

            json.Append('}');
            return json.ToString();
        }

        internal string BuildDerivedNetworksJson()
        {
            var json = new StringBuilder();
            json.Append('{');
            for (var i = 0; i < m_Networks.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(", ");
                }

                var item = m_Networks[i];
                CmmJson.AppendString(json, item.Id.ToString(CultureInfo.InvariantCulture));
                json.Append(": {\"source_entity\": ").Append(item.SourceEntity)
                    .Append(", \"role\": ");
                CmmJson.AppendString(json, item.Role);
                json.Append(", \"mode\": ");
                CmmJson.AppendString(json, item.TransitKind ?? item.Kind);
                json.Append('}');
            }

            json.Append('}');
            return json.ToString();
        }

        private int AddNetwork(
            int sourceEntity,
            string role,
            string kind,
            string mode,
            string transitKind,
            int startId,
            int endId,
            List<float3> points,
            float width,
            List<int> pathSegmentIds = null,
            string name = "")
        {
            if (points == null || points.Count < 2)
            {
                return 0;
            }

            var id = m_NextNetworkId--;
            m_Networks.Add(new NetworkRecord(
                id,
                sourceEntity,
                role,
                kind,
                mode,
                transitKind,
                startId,
                endId,
                points,
                width,
                pathSegmentIds ?? new List<int>(),
                name));
            return id;
        }

        private int GetNodeId(float3 position)
        {
            var key = CoordinateKey(position);
            if (m_NodeIds.TryGetValue(key, out var id))
            {
                return id;
            }

            id = m_NextNodeId--;
            m_NodeIds.Add(key, id);
            m_Nodes.Add(new NodeRecord(id, position));
            return id;
        }

        private static string CoordinateKey(float3 value)
        {
            return FormatCoordinate(value.x) + "|"
                + FormatCoordinate(value.y) + "|"
                + FormatCoordinate(value.z);
        }

        private static string FormatCoordinate(float value)
        {
            if (math.abs(value) < 0.0005f)
            {
                value = 0f;
            }

            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static void AppendPoints(StringBuilder json, List<float3> points)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                AppendVec3(json, points[i]);
            }
        }

        private static void AppendVec3(StringBuilder json, float3 value)
        {
            json.Append("{\"x\": ").Append(FormatFloat(value.x))
                .Append(", \"y\": ").Append(FormatFloat(value.y))
                .Append(", \"z\": ").Append(FormatFloat(value.z))
                .Append('}');
        }

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        internal readonly struct ModeRecord
        {
            internal ModeRecord(
                int car,
                int bicycle,
                int bus,
                int tram,
                int train,
                int subway)
            {
                Car = car;
                Bicycle = bicycle;
                Bus = bus;
                Tram = tram;
                Train = train;
                Subway = subway;
            }

            internal int Car { get; }
            internal int Bicycle { get; }
            internal int Bus { get; }
            internal int Tram { get; }
            internal int Train { get; }
            internal int Subway { get; }
        }

        private readonly struct NodeRecord
        {
            internal NodeRecord(int id, float3 position)
            {
                Id = id;
                Position = position;
            }

            internal int Id { get; }
            internal float3 Position { get; }
        }

        private sealed class NetworkRecord
        {
            internal NetworkRecord(
                int id,
                int sourceEntity,
                string role,
                string kind,
                string mode,
                string transitKind,
                int startId,
                int endId,
                List<float3> points,
                float width,
                List<int> pathSegmentIds,
                string name)
            {
                Id = id;
                SourceEntity = sourceEntity;
                Role = role;
                Kind = kind;
                Mode = mode;
                TransitKind = transitKind;
                StartId = startId;
                EndId = endId;
                Points = points;
                Width = width;
                PathSegmentIds = pathSegmentIds;
                Name = name;
            }

            internal int Id { get; }
            internal int SourceEntity { get; }
            internal string Role { get; }
            internal string Kind { get; }
            internal string Mode { get; }
            internal string TransitKind { get; }
            internal int StartId { get; }
            internal int EndId { get; }
            internal List<float3> Points { get; }
            internal float Width { get; }
            internal List<int> PathSegmentIds { get; }
            internal string Name { get; }
        }
    }
}
