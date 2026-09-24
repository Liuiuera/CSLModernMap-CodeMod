using System;
using System.Collections.Generic;
using System.Globalization;
using Colossal.Mathematics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>采集当前交通状态数据</summary>
    internal sealed class TrafficExport
    {
        private readonly float[] m_Weights;
        private readonly JObject m_Snapshot;
        private readonly JArray m_Segments = new JArray();
        private readonly JArray m_Lights = new JArray();
        private readonly JObject m_Volumes = new JObject();
        private readonly JObject m_Quality = new JObject();
        private readonly JObject m_LightFacts = new JObject();
        private readonly JObject m_Metadata;
        internal int SegmentCount => m_Segments.Count;
        internal int LaneCount { get; private set; }
        internal int ValidLaneCount { get; private set; }
        internal int ValidSegmentCount { get; private set; }
        internal int LightCount => m_Lights.Count;

        internal TrafficExport(World world, string cityName, DateTime capturedAt)
        {
            var time = world.GetOrCreateSystemManaged<Game.Simulation.TimeSystem>();
            var simulation = world.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            var normalizedTime = time.normalizedTime;
            m_Weights = TrafficSnapshotMath.Weights(normalizedTime);
            m_Metadata = new JObject {
                ["source"] = "cs2-native", ["method"] = "current-game-time-interpolation-v1",
                ["normalized_time"] = normalizedTime, ["frame_index"] = simulation.frameIndex,
                ["game_time"] = time.GetCurrentDateTime().ToString("o", CultureInfo.InvariantCulture),
                ["captured_at"] = capturedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                ["simulation_paused"] = simulation.selectedSpeed == 0,
                ["weights"] = new JArray(m_Weights), ["sampling"] = "latest-completed-native-statistics",
                ["physical_speed_available"] = false, ["vehicle_count_available"] = false,
                ["lane_geometry"] = "edge-midpoint-cross-section",
                ["volume_unit"] = "native-road-section-volume", ["volumes"] = m_Volumes,
                ["road_quality"] = m_Quality, ["light_facts"] = m_LightFacts
            };
            m_Snapshot = new JObject {
                ["schema_version"] = "1.2", ["city_name"] = cityName,
                ["timestamp"] = m_Metadata["captured_at"].DeepClone(),
                ["simulation_paused"] = simulation.selectedSpeed == 0,
                ["segments"] = m_Segments, ["lights"] = m_Lights
            };
        }

        internal string SnapshotJson()
        {
            m_Snapshot["stats"] = new JObject {
                ["source"] = "cs2-native", ["segments"] = SegmentCount,
                ["valid_segments"] = ValidSegmentCount, ["lanes"] = LaneCount,
                ["valid_lanes"] = ValidLaneCount, ["lights"] = LightCount
            };
            return m_Snapshot.ToString(Formatting.None);
        }

        internal string MetadataJson() => m_Metadata.ToString(Formatting.None);

        internal string IssuesJson()
        {
            var issues = new CmmIssueCollector();
            if (SegmentCount > ValidSegmentCount)
                issues.Warn("TRAFFIC_NO_SAMPLES", (SegmentCount - ValidSegmentCount)
                    + " 条道路在导出时点缺少有效原生交通统计，已保留为无数据");
            return issues.ToJson();
        }

        internal int IssueCount => SegmentCount > ValidSegmentCount ? 1 : 0;

        internal void AddRoad(EntityManager em, Entity entity, IList<float3> points,
            float width, string name, string prefabName, string level)
        {
            if (!em.HasComponent<Game.Net.Road>(entity) || points.Count < 2) return;
            var road = em.GetComponentData<Game.Net.Road>(entity);
            var durations = Values(road.m_TrafficFlowDuration0 + road.m_TrafficFlowDuration1);
            var distances = Values(road.m_TrafficFlowDistance0 + road.m_TrafficFlowDistance1);
            float roadFlow = TrafficSnapshotMath.Flow(durations, distances, m_Weights);
            float volume = TrafficSnapshotMath.Volume(durations, distances, m_Weights);
            string key = entity.Index.ToString(CultureInfo.InvariantCulture);
            m_Volumes[key] = volume < 0 ? JValue.CreateNull() : new JValue(volume);

            var lanes = ReadLanes(em, entity);
            float congestionSum = 0, validWidth = 0;
            bool forward = false, backward = false, busLane = false;
            foreach (JObject lane in lanes)
            {
                var direction = (string)lane["direction"];
                forward |= direction == "Forward" || direction == "Both";
                backward |= direction == "Backward" || direction == "Both";
                busLane |= (bool)lane["public_only"];
                lane.Remove("public_only");
                float congestion = (float)lane["congestion"];
                if (congestion < 0) continue;
                float laneWidth = (float)lane["width"];
                congestionSum += congestion * laneWidth;
                validWidth += laneWidth;
            }
            float congestionValue = validWidth > 0 ? congestionSum / validWidth
                : (roadFlow < 0 ? -1 : 1 - roadFlow);
            if (congestionValue >= 0) ValidSegmentCount++;
            m_Quality[key] = new JObject {
                ["entity_version"] = entity.Version,
                ["source"] = validWidth > 0 ? "lane-flow" : "road-flow",
                ["valid"] = congestionValue >= 0,
                ["reason"] = congestionValue >= 0 ? "available" : "missing-or-invalid-active-time-bin"
            };
            var interior = new JArray();
            for (int i = 1; i < points.Count - 1; i++) interior.Add(Vec(points[i]));
            m_Segments.Add(new JObject {
                ["segment_id"] = entity.Index, ["road_name"] = name, ["item_class"] = prefabName,
                ["width"] = width, ["node_a"] = Vec(points[0]), ["node_b"] = Vec(points[points.Count - 1]),
                ["curve_points"] = interior, ["lanes"] = lanes,
                ["seg_stats"] = new JObject {
                    ["total_lanes"] = lanes.Count, ["vehicle_lanes"] = lanes.Count,
                    ["one_way"] = forward ^ backward, ["highway"] = level == "HIGHWAY",
                    ["has_buslane"] = busLane, ["weighted_avg_congestion"] = congestionValue,
                    ["weighted_avg_relative_speed"] = congestionValue < 0 ? -1 : 1 - congestionValue
                }
            });
        }

        /// <summary>只统计道路中点横断面上的实际车道</summary>
        private JArray ReadLanes(EntityManager em, Entity road)
        {
            var result = new JArray();
            if (!em.HasBuffer<Game.Net.SubLane>(road)) return result;
            var roadCurve = em.GetComponentData<Game.Net.Curve>(road).m_Bezier;
            float3 center = MathUtils.Position(roadCurve, 0.5f);
            float2 tangent = (roadCurve.d + roadCurve.c - roadCurve.b - roadCurve.a).xz;
            float length = math.length(tangent);
            if (length < 0.001f) return result;
            float2 normal = new float2(-tangent.y, tangent.x) / length;
            var subLanes = em.GetBuffer<Game.Net.SubLane>(road, true);
            var seen = new HashSet<Entity>();
            for (int i = 0; i < subLanes.Length; i++)
            {
                var lane = subLanes[i].m_SubLane;
                if (!seen.Add(lane) || !em.HasComponent<Game.Net.CarLane>(lane)
                    || !em.HasComponent<Game.Net.EdgeLane>(lane)
                    || !em.HasComponent<Game.Net.Curve>(lane)
                    || !em.HasComponent<Game.Prefabs.PrefabRef>(lane)
                    || em.HasComponent<Game.Net.MasterLane>(lane)
                    || em.HasComponent<Game.Net.SecondaryLane>(lane)
                    || em.HasComponent<Game.Common.Deleted>(lane) || em.HasComponent<Game.Tools.Temp>(lane)) continue;
                var car = em.GetComponentData<Game.Net.CarLane>(lane);
                if ((car.m_Flags & (Game.Net.CarLaneFlags.Runway | Game.Net.CarLaneFlags.Forbidden)) != 0) continue;
                var delta = em.GetComponentData<Game.Net.EdgeLane>(lane).m_EdgeDelta;
                if (!math.all(math.isfinite(delta)) || math.cmin(delta) > 0.5f || math.cmax(delta) <= 0.5f) continue;
                var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(lane).m_Prefab;
                if (!em.HasComponent<Game.Prefabs.NetLaneData>(prefab)) continue;
                if (!em.HasComponent<Game.Prefabs.CarLaneData>(prefab)
                    || (em.GetComponentData<Game.Prefabs.CarLaneData>(prefab).m_RoadTypes
                        & Game.Net.RoadTypes.Car) == 0) continue;
                float width = em.GetComponentData<Game.Prefabs.NetLaneData>(prefab).m_Width;
                if (!TrafficSnapshotMath.Finite(width) || width <= 0) continue;
                var curve = em.GetComponentData<Game.Net.Curve>(lane).m_Bezier;
                float laneT = (0.5f - delta.x) / (delta.y - delta.x);
                float position = math.dot((MathUtils.Position(curve, laneT) - center).xz, normal);
                if (!TrafficSnapshotMath.Finite(position)) continue;
                float flow = -1;
                if (em.HasComponent<Game.Net.LaneFlow>(lane))
                {
                    var data = em.GetComponentData<Game.Net.LaneFlow>(lane);
                    flow = TrafficSnapshotMath.Flow(Values(data.m_Duration), Values(data.m_Distance), m_Weights);
                }
                string direction = (car.m_Flags & Game.Net.CarLaneFlags.Twoway) != 0 ? "Both"
                    : (delta.y > delta.x ? "Forward" : "Backward");
                result.Add(new JObject {
                    ["lane_index"] = i, ["lane_type"] = "Vehicle", ["position"] = position,
                    ["width"] = width, ["relative_speed"] = flow,
                    ["congestion"] = flow < 0 ? -1 : 1 - flow, ["data_quality"] = flow < 0 ? 1 : 0,
                    ["direction"] = direction, ["public_only"] = (car.m_Flags & Game.Net.CarLaneFlags.PublicOnly) != 0
                });
                LaneCount++;
                if (flow >= 0) ValidLaneCount++;
            }
            return result;
        }

        /// <summary>保留铁路道口事实但不将其计为道路信号灯</summary>
        internal void AddLights(EntityManager em, HashSet<Entity> nodes)
        {
            var sorted = new List<Entity>(nodes);
            sorted.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var entity in sorted)
            {
                if (!em.HasComponent<Game.Net.TrafficLights>(entity)) continue;
                var lights = em.GetComponentData<Game.Net.TrafficLights>(entity);
                if ((lights.m_Flags & Game.Net.TrafficLightFlags.MoveableBridge) != 0) continue;
                bool crossing = (lights.m_Flags & Game.Net.TrafficLightFlags.LevelCrossing) != 0;
                m_LightFacts[entity.Index.ToString(CultureInfo.InvariantCulture)] = new JObject {
                    ["kind"] = crossing ? "level_crossing" : "traffic_light",
                    ["state"] = lights.m_State.ToString(), ["flags"] = lights.m_Flags.ToString(),
                    ["current_signal_group"] = lights.m_CurrentSignalGroup
                };
                if (crossing) continue;
                m_Lights.Add(new JObject {
                    ["node_id"] = entity.Index, ["position"] = Vec(em.GetComponentData<Game.Net.Node>(entity).m_Position),
                    ["active"] = lights.m_State != Game.Net.TrafficLightState.None,
                    ["custom"] = false, ["states"] = new JArray()
                });
            }
        }

        private static float[] Values(float4 v) => new[] { v.x, v.y, v.z, v.w };
        private static JObject Vec(float3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
    }
}
