using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace CSLModernMap.Traffic
{
    /// <summary>读取 TM:PE 车道统计，生成内嵌 CMM 的交通快照 JSON。</summary>
    public class TrafficSnapshotExporter
    {
        private const int FLAG_CREATED = 1;

        /// <summary>与现有地图导出器一致的忽略正则（只保留道路类NetSegment导出，排除地铁轨道等）。</summary>
        private static readonly string[] IGNORE_SEGMENT_ITEM_CLASS =
        {
            "^(Ship|Airplane) Path$",
            "^Water Pipe$",
            "^Transport Connection$",
            "^Train Track$",
            "^Monorail Track$",
            "^Pedestrian Path$",
            "^Cable Car.*$"
        };

        private static readonly Regex RoadItemClassRegex = new Regex(
            @"^(.* Road|Highway|.* Road Ramp|.* Road Decor|Zoned Road)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        /// <summary>与地图导出共用的时间，写入快照时转为 UTC。</summary>
        public DateTime? SharedTimestamp { get; set; }
        internal bool? SimulationPausedOverride { get; set; }

        public Dictionary<int, List<global::CSLModernMap.Export.XMLVector3>> RoadGeometry { get; set; }

        // City-wide 聚合累加器（在遍历所有 segment/lanes 过程中持续 +=）

        private class CityAccumulator
        {
            public float sumWidthTimesCongestion;   // Σ width × congestion（仅 Vehicle 车道）
            public float sumWidth;                  // Σ width（仅 Vehicle 车道）
            public float sumCongestion;             // Σ congestion（仅 Vehicle 车道，simple avg 用）
            public int   countVehicleLanes;         // 多少条 Vehicle 车道参与了上面两个 sum
            public long  sumVehicles;               // Σ vehicleCount（所有 Vehicle 车道相加）
            public float sumWidthTimesRelSpeed;     // Σ width × relativeSpeed
            public int   countNoTmpeStats;          // 无 TM:PE 统计的 Vehicle 车道数
        }

        // 核心：构建 TrafficSnapshotExport 对象（不写文件 / 不序列化）


        /// <summary>
        /// 构建交通快照数据对象（不进行任何 IO）。
        /// 返回 null 表示失败，errorMessage 中说明原因。
        /// </summary>
        private TrafficSnapshotExport BuildSnapshot(
            out bool simulationPausedFlag,
            out string errorMessage)
        {
            simulationPausedFlag = false;
            errorMessage = null;

            if (!CSLModernMapLoadingExtension.IsLevelLoaded)
            {
                errorMessage = "尚未加载游戏存档，无法导出交通快照。";
                return null;
            }
            if (!TMPEBridge.IsAvailable)
            {
                errorMessage = "TM:PE (Traffic Manager President Edition) 不可用："
                             + TMPEBridge.StatusString;
                return null;
            }

            SimulationManager sim = Singleton<SimulationManager>.instance;
            NetManager netManager = Singleton<NetManager>.instance;

            // 暂停检测
            bool paused = SimulationPausedOverride ?? sim.SimulationPaused;
            simulationPausedFlag = paused;

            TrafficSnapshotExport snapshot = new TrafficSnapshotExport();
            snapshot.cityName = (sim.m_metaData != null && !string.IsNullOrEmpty(sim.m_metaData.m_CityName))
                ? sim.m_metaData.m_CityName
                : "Unknown";
            // snapshot.timestamp = ISO 8601 UTC；若 SharedTimestamp 已设则转 UTC 使用，
            // 保证与 cslmap 文件名时间戳来自同一时钟。
            DateTime utcTs = SharedTimestamp.HasValue
                ? SharedTimestamp.Value.ToUniversalTime()
                : DateTime.UtcNow;
            snapshot.timestamp = utcTs.ToString("o", CultureInfo.InvariantCulture);
            snapshot.simulationPaused = paused;
            snapshot.stats.tmpeAvailable = true;

            CityAccumulator cityAcc = new CityAccumulator();

            NetSegment[] segBuffer = netManager.m_segments.m_buffer;
            NetNode[] nodeBuffer = netManager.m_nodes.m_buffer;
            NetLane[] laneBuffer = netManager.m_lanes.m_buffer;
            int segCount = segBuffer.Length; // == NetManager.MAX_SEGMENT_COUNT (36864)

            for (ushort i = 0; i < segCount; i++)
            {
                NetSegment seg = segBuffer[i];
                if (!HasFlag((int)seg.m_flags, FLAG_CREATED))
                {
                    continue;
                }
                snapshot.stats.totalSegments++;

                NetInfo info = seg.Info;
                if (info == null) { snapshot.stats.skippedSegments++; continue; }
                if (!MatchesRoadClass(info.m_class.name)) { snapshot.stats.skippedSegments++; continue; }

                // 起点 & 终点节点坐标
                ushort startNid = seg.m_startNode;
                ushort endNid = seg.m_endNode;
                if (startNid >= nodeBuffer.Length || endNid >= nodeBuffer.Length)
                {
                    snapshot.stats.skippedSegments++; continue;
                }
                NetNode startNode = nodeBuffer[startNid];
                NetNode endNode = nodeBuffer[endNid];
                if (!HasFlag((int)startNode.m_flags, FLAG_CREATED) ||
                    !HasFlag((int)endNode.m_flags, FLAG_CREATED))
                {
                    snapshot.stats.skippedSegments++; continue;
                }

                // 直接读取 TM:PE 路段信息。
                uint[] tmpeLaneIds;   // 可能 null
                bool oneWay, highway, buslane;
                bool tmpeValid = TMPEBridge.TryGetSegmentInfo(i, out tmpeLaneIds, out oneWay, out highway, out buslane);

                // 车道数：取 NetInfo.m_lanes.Length (游戏定义车道) 为基准
                NetInfo.Lane[] netLanes = info.m_lanes;
                int laneNum = netLanes != null ? netLanes.Length : 0;

                if (!tmpeValid && laneNum == 0)
                {
                    snapshot.stats.skippedSegments++;
                    continue;
                }

                RoadSegmentExport rse = new RoadSegmentExport();
                rse.segmentId = i;
                rse.nodeA = new Vec3(startNode.m_position.x, startNode.m_position.y, startNode.m_position.z);
                rse.nodeB = new Vec3(endNode.m_position.x, endNode.m_position.y, endNode.m_position.z);
                rse.roadName = info.name ?? "";
                rse.itemClass = info.m_class.name ?? "";
                rse.width = info.m_halfWidth * 2f;

                List<global::CSLModernMap.Export.XMLVector3> points;
                if (RoadGeometry == null || !RoadGeometry.TryGetValue(i, out points))
                    points = global::CSLModernMap.Exporter.RoadGeometry.Centerline(seg, nodeBuffer);
                rse.curvePoints = new List<Vec3>();
                for (int p = 1; p < points.Count - 1; p++) rse.curvePoints.Add(new Vec3(points[p].X, points[p].Y, points[p].Z));

                // seg-level accumulator
                float segSumWtC = 0f;
                float segSumW = 0f;
                float segSumC = 0f;
                int   segVehLanes = 0;
                int   segTotalVeh = 0;
                float segSumWtRS = 0f;

                for (int li = 0; li < laneNum; li++)
                {
                    NetInfo.Lane lane = netLanes[li];
                    LaneExport le = new LaneExport();
                    le.laneIndex = li;
                    le.laneType = lane.m_laneType.ToString();
                    le.vehicleType = lane.m_vehicleType.ToString();
                    le.position = global::CSLModernMap.Exporter.RoadGeometry.EffectiveLanePosition(lane.m_position,
                        (seg.m_flags & NetSegment.Flags.Invert) != 0);
                    le.width = lane.m_width;
                    le.laneDirection = global::CSLModernMap.Exporter.RoadGeometry.EffectiveDirection(lane.m_finalDirection,
                        (seg.m_flags & NetSegment.Flags.Invert) != 0).ToString();
                    le.tmpeDirection = le.laneDirection;

                    le.maxSpeed = lane.m_speedLimit;

                    bool isVehicleLane = (lane.m_laneType & (NetInfo.LaneType.Vehicle | NetInfo.LaneType.TransportVehicle)) != 0;

                    if (!isVehicleLane)
                    {
                        // 非机动车道：所有交通字段写 -1 / 0，dataQuality=0
                        le.speed = 0f;
                        le.relativeSpeed = -1f;
                        le.congestion = -1f;
                        le.vehicleCount = 0;
                        le.dataQuality = 0;
                    }
                    else
                    {
                        snapshot.stats.totalVehicleLanes++;

                        float rel01;
                        int vc;
                        bool gotSnap = TMPEBridge.TryGetLaneSnapshot(i, li, out rel01, out vc);
                        if (gotSnap)
                        {
                            le.relativeSpeed = Mathf.Clamp01(rel01);
                            le.congestion = 1f - le.relativeSpeed;
                            le.speed = le.relativeSpeed * le.maxSpeed;
                            le.vehicleCount = vc;
                            le.dataQuality = 0;

                            float w = le.width > 0 ? le.width : 1f;
                            segSumWtC  += w * le.congestion;
                            segSumW    += w;
                            segSumC    += le.congestion;
                            segSumWtRS += w * le.relativeSpeed;
                            segVehLanes++;
                            segTotalVeh += vc;

                            cityAcc.sumWidthTimesCongestion += w * le.congestion;
                            cityAcc.sumWidth                 += w;
                            cityAcc.sumCongestion            += le.congestion;
                            cityAcc.countVehicleLanes       ++;
                            cityAcc.sumWidthTimesRelSpeed    += w * le.relativeSpeed;
                            cityAcc.sumVehicles              += vc;
                        }
                        else
                        {
                            le.relativeSpeed = -1f;
                            le.congestion = -1f;
                            le.speed = 0f;
                            le.vehicleCount = 0;
                            le.dataQuality = 1;
                            cityAcc.countNoTmpeStats++;
                        }
                    }

                    rse.lanes.Add(le);
                    snapshot.stats.totalLanes++;
                }

                rse.segStats.totalLanes = rse.lanes.Count;
                rse.segStats.vehicleLanes = segVehLanes;
                rse.segStats.totalVehicles = segTotalVeh;
                rse.segStats.oneWay = oneWay;
                rse.segStats.highway = highway;
                rse.segStats.hasBuslane = buslane;
                if (segVehLanes > 0)
                {
                    rse.segStats.weightedAvgCongestion    = segSumW > 0 ? (segSumWtC / segSumW) : (segSumC / segVehLanes);
                    rse.segStats.simpleAvgCongestion      = segSumC / segVehLanes;
                    rse.segStats.weightedAvgRelativeSpeed = segSumW > 0 ? (segSumWtRS / segSumW) : -1f;
                }
                else
                {
                    rse.segStats.weightedAvgCongestion    = -1f;
                    rse.segStats.simpleAvgCongestion      = -1f;
                    rse.segStats.weightedAvgRelativeSpeed = -1f;
                }

                snapshot.segments.Add(rse);
                snapshot.stats.exportedSegments++;
            }

            // 数据来源：
            //   - NetNode.Flags.TrafficLights       -> 该节点有红绿灯
            //   - NetNode.Flags.CustomTrafficLights -> 自定义灯组（通常 = TM:PE 接管）
            //   - RoadBaseAI.GetTrafficLightState(nodeId, ref seg, frame,
            //         out vehicleState, out pedestrianState, out vehicles, out pedestrians)
            //     静态方法，帧号用 SimulationManager.m_currentFrameIndex。
            uint frame = Singleton<SimulationManager>.instance.m_currentFrameIndex;
            for (ushort nodeId = 0; nodeId < nodeBuffer.Length; nodeId++)
            {
                NetNode node = nodeBuffer[nodeId];
                if (!HasFlag((int)node.m_flags, FLAG_CREATED)) continue;
                if (!HasFlag((int)node.m_flags, (int)NetNode.Flags.TrafficLights)) continue;

                NodeLightExport nle = new NodeLightExport();
                nle.nodeId = nodeId;
                nle.position = new Vec3(node.m_position.x, node.m_position.y, node.m_position.z);
                nle.active = true;
                nle.custom = HasFlag((int)node.m_flags, (int)NetNode.Flags.CustomTrafficLights);

                int connected = node.CountSegments();
                for (int si = 0; si < connected; si++)
                {
                    ushort segId = node.GetSegment(si);
                    if (segId == 0 || segId >= segBuffer.Length) continue;
                    NetSegment segData = segBuffer[segId];
                    if (!HasFlag((int)segData.m_flags, FLAG_CREATED)) continue;

                    RoadBaseAI.TrafficLightState veh;
                    RoadBaseAI.TrafficLightState ped;
                    bool vehicles, pedestrians;
                    try
                    {
                        RoadBaseAI.GetTrafficLightState(
                            nodeId, ref segData, frame,
                            out veh, out ped, out vehicles, out pedestrians);
                    }
                    catch
                    {
                        // 个别节点/段数据异常时跳过该方向，不影响整体导出
                        continue;
                    }

                    LightStateExport lse = new LightStateExport();
                    lse.segmentId = segId;
                    lse.vehicleState = LightStateToString(veh);
                    lse.vehicleRaw = (int)veh;
                    lse.pedestrianState = LightStateToString(ped);
                    lse.pedestrianRaw = (int)ped;
                    nle.states.Add(lse);
                    snapshot.stats.lightStates++;
                }

                snapshot.lights.Add(nle);
                snapshot.stats.lightNodes++;
            }

            snapshot.stats.totalVehicleLanes          = cityAcc.countVehicleLanes + cityAcc.countNoTmpeStats;
            snapshot.stats.cityTotalVehicles          = cityAcc.sumVehicles;
            snapshot.stats.vehicleLanesNoTmpeStats    = cityAcc.countNoTmpeStats;
            if (cityAcc.countVehicleLanes > 0)
            {
                snapshot.stats.cityWeightedAvgCongestion     = cityAcc.sumWidth > 0
                    ? (cityAcc.sumWidthTimesCongestion / cityAcc.sumWidth)
                    : (cityAcc.sumCongestion / cityAcc.countVehicleLanes);
                snapshot.stats.citySimpleAvgCongestion       = cityAcc.sumCongestion / cityAcc.countVehicleLanes;
                snapshot.stats.cityWeightedAvgRelativeSpeed  = cityAcc.sumWidth > 0
                    ? (cityAcc.sumWidthTimesRelSpeed / cityAcc.sumWidth)
                    : -1f;
            }
            else
            {
                snapshot.stats.cityWeightedAvgCongestion    = -1f;
                snapshot.stats.citySimpleAvgCongestion      = -1f;
                snapshot.stats.cityWeightedAvgRelativeSpeed = -1f;
            }

            return snapshot;
        }

        // 导出紧凑 JSON 字符串（供 CSLModernMapExporter 内嵌到 CMM）


        /// <summary>返回内嵌快照 JSON；失败时返回 null 并输出错误原因。</summary>
        public string ExportToJson(out string errorMessage, out bool simulationPaused)
        {
            errorMessage = null;
            simulationPaused = false;
            try
            {
                TrafficSnapshotExport snapshot = BuildSnapshot(out simulationPaused, out errorMessage);
                if (snapshot == null)
                {
                    return null;
                }
                return JsonHelper.Serialize(snapshot);
            }
            catch (Exception ex)
            {
                errorMessage = ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace;
                return null;
            }
        }


        private static bool HasFlag(int flags, int flag)
        {
            return (flags & flag) != 0;
        }

        /// <summary>游戏本体 TrafficLightState 枚举 -> 可读字符串。
        /// RedToGreen=1 / GreenToRed=3 均为过渡态，映射为 "yellow"。</summary>
        private static string LightStateToString(RoadBaseAI.TrafficLightState s)
        {
            switch (s)
            {
                case RoadBaseAI.TrafficLightState.Green:
                    return "green";
                case RoadBaseAI.TrafficLightState.Red:
                    return "red";
                case RoadBaseAI.TrafficLightState.RedToGreen:
                case RoadBaseAI.TrafficLightState.GreenToRed:
                    return "yellow";
                default:
                    return "unknown";
            }
        }

        private static bool MatchesAny(string[] patterns, string input)
        {
            if (input == null) return false;
            for (int i = 0; i < patterns.Length; i++)
            {
                if (Regex.IsMatch(input, patterns[i])) return true;
            }
            return false;
        }

        /// <summary>
        /// 判定是否为我们关心的道路类 ItemClass。
        /// </summary>
        private static bool MatchesRoadClass(string itemClassName)
        {
            if (string.IsNullOrEmpty(itemClassName)) return false;
            if (MatchesAny(IGNORE_SEGMENT_ITEM_CLASS, itemClassName)) return false;
            return RoadItemClassRegex.IsMatch(itemClassName)
                || itemClassName.IndexOf("Road", StringComparison.Ordinal) >= 0
                || itemClassName.IndexOf("Highway", StringComparison.Ordinal) >= 0;
        }

    }
}
