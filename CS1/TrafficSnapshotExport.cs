using System;
using System.Collections.Generic;

namespace CSLModernMap.Traffic
{
    /// <summary>
    /// 交通快照 JSON 导出数据模型（面向分析软件，schema 稳定、字段必写）。
    ///
    /// v1.1 版本的"分析软件友好"承诺：
    ///   1. 所有字段**必定存在**，即使是默认值 0/false/空数组。分析软件不需要写"key 存在？→ 再读"的防御代码。
    ///      - JsonHelper 配置：DefaultValueHandling.Include + NullValueHandling.Include。
    ///      - 例如 laneIndex=0、speed=0 都会明确写出为 0，不会被序列化忽略。
    ///   2. 所有**坐标统一米**单位（游戏世界坐标 x/z 为平面，y 为高度），与 CSLModernMap 导出 .mmap 完全一致，可直接叠加渲染。
    ///   3. 拥堵率 congestion 已归一化至 [0,1]；laneType≠Vehicle 的车道 congestion=-1（表示该车道无交通统计意义）。
    ///   4. 新增 relativeSpeed 字段 (0~1) —— 对于分析软件的拥堵热力图来说 relativeSpeed 比原始 speed 更常用。
    ///   5. 新增 segment 级 aggregation stats (RoadSegmentAggStats) —— 分析软件无需自己向上聚合。
    ///   6. 新增 SnapshotStats.vehicleLanes / avgCongestion / cityWideCongestion 等 KPI。
    ///   7. schemaVersion 语义化版本号：主版本号破坏变更时加 1，小版本号新增字段时加 1。
    /// </summary>
    [Serializable]
    public class TrafficSnapshotExport
    {
        /// <summary>
        /// Schema 语义化版本号（分析软件第一字段就可以读这个做兼容性判断）。
        ///   "1.2" = 当前版本（新增 lights 红绿灯节点数据）；
        ///   "1.1" = 上一版本（车道级拥堵 + 聚合统计）；
        ///   "1.0" = 之前版本（有 laneIndex/speed 丢 key / speed 单位错 bug 的那版）。
        /// </summary>
        public string schemaVersion { get; set; }

        /// <summary>城市名称。</summary>
        public string cityName { get; set; }

        /// <summary>导出时间戳（UTC ISO 8601）。</summary>
        public string timestamp { get; set; }

        /// <summary>游戏模拟是否处于暂停状态（暂停时数据是旧快照，分析软件可弹"不建议做拥堵评估"提示）。</summary>
        public bool simulationPaused { get; set; }

        /// <summary>道路段列表（按 NetSegment ID 升序枚举）。</summary>
        public List<RoadSegmentExport> segments { get; set; }

        /// <summary>
        /// 红绿灯节点列表（schema v1.2 新增）。
        /// 数据来源为游戏本体 RoadBaseAI（不依赖 TM:PE），
        /// 每个节点含其所有连接道路方向的当前灯色。
        /// </summary>
        public List<NodeLightExport> lights { get; set; }

        /// <summary>全市总览统计信息摘要。</summary>
        public SnapshotStats stats { get; set; }

        public TrafficSnapshotExport()
        {
            schemaVersion = "1.2";
            cityName = string.Empty;
            timestamp = string.Empty;
            simulationPaused = false;
            segments = new List<RoadSegmentExport>();
            lights = new List<NodeLightExport>();
            stats = new SnapshotStats();
        }
    }

    /// <summary>
    /// 单个红绿灯节点导出实体（schema v1.2 新增）。
    /// 数据来源：游戏本体 NetNode.Flags.TrafficLights + RoadBaseAI.GetTrafficLightState，
    /// 不依赖 TM:PE —— 即使 TM:PE 未启用，只要导出地图/交通快照就会带上。
    /// </summary>
    [Serializable]
    public class NodeLightExport
    {
        /// <summary>NetNode ID（ushort）。</summary>
        public int nodeId { get; set; }

        /// <summary>红绿灯节点世界坐标（米，x/z 平面 + y 高度），与 .mmap 节点坐标一致。</summary>
        public Vec3 position { get; set; }

        /// <summary>红绿灯是否启用（NetNode.Flags.TrafficLights）。采集时仅遍历启用节点，恒为 true。</summary>
        public bool active { get; set; }

        /// <summary>是否自定义灯组（NetNode.Flags.CustomTrafficLights，通常表示 TM:PE 接管或玩家自定义）。</summary>
        public bool custom { get; set; }

        /// <summary>该节点每条连接道路的当前灯色（按连接顺序，与 NetNode.GetSegment 一致）。</summary>
        public List<LightStateExport> states { get; set; }

        public NodeLightExport()
        {
            nodeId = 0;
            position = new Vec3(0f, 0f, 0f);
            active = false;
            custom = false;
            states = new List<LightStateExport>();
        }
    }

    /// <summary>
    /// 单条连接道路方向的信号灯状态（schema v1.2 新增）。
    /// </summary>
    [Serializable]
    public class LightStateExport
    {
        /// <summary>连接该红绿灯节点的 NetSegment ID。</summary>
        public int segmentId { get; set; }

        /// <summary>
        /// 该方向机动车灯状态（可读字符串）：
        ///   "green" / "red" / "yellow" / "unknown"。
        ///   yellow 同时覆盖 RedToGreen 与 GreenToRed（游戏中黄灯过渡）。
        /// </summary>
        public string vehicleState { get; set; }

        /// <summary>机动车灯原始枚举值（Green=0, RedToGreen=1, Red=2, GreenToRed=3）。</summary>
        public int vehicleRaw { get; set; }

        /// <summary>该方向人行灯状态（"green"/"red"/"yellow"/"unknown"）。</summary>
        public string pedestrianState { get; set; }

        /// <summary>人行灯原始枚举值。</summary>
        public int pedestrianRaw { get; set; }

        public LightStateExport()
        {
            segmentId = 0;
            vehicleState = "unknown";
            vehicleRaw = -1;
            pedestrianState = "unknown";
            pedestrianRaw = -1;
        }
    }

    /// <summary>
    /// 单条道路段导出实体。几何/属性字段与 CSLModernMap .mmap 对齐，分析软件可直接叠加。
    /// </summary>
    [Serializable]
    public class RoadSegmentExport
    {
        /// <summary>NetSegment ID（ushort，0 ~ 36863）。分析软件可作为稳定主键。</summary>
        public int segmentId { get; set; }

        /// <summary>起点节点（游戏世界坐标，米）。对应 NetManager.m_nodes[seg.m_startNode]。</summary>
        public Vec3 nodeA { get; set; }

        /// <summary>终点节点（游戏世界坐标，米）。对应 NetManager.m_nodes[seg.m_endNode]。</summary>
        public Vec3 nodeB { get; set; }

        /// <summary>
        /// 贝塞尔中间控制点（3 点，按 0.25 / 0.5 / 0.75 参数位置取）。
        /// 直线道路（StartDirection == -EndDirection）时为 null（分析软件判定为 nodeA → nodeB 直线）。
        /// 与 .mmap 文件同算法。
        /// </summary>
        public List<Vec3> curvePoints { get; set; }

        /// <summary>道路资产名称（NetInfo.name，如 "Basic Road"、"HighwayRampElevated"）。</summary>
        public string roadName { get; set; }

        /// <summary>ItemClass 分类（Small Road / Medium Road / Large Road / Highway 等 5 大类）。</summary>
        public string itemClass { get; set; }

        /// <summary>道路总宽度（米）= NetInfo.m_halfWidth × 2。</summary>
        public float width { get; set; }

        /// <summary>
        /// 车道列表。顺序严格与 NetInfo.m_lanes 一致（0-based，从左 → 右，按 m_position 从小到大）。
        /// 分析软件无需再排序即可渲染横向 lane 条带。
        /// </summary>
        public List<LaneExport> lanes { get; set; }

        /// <summary>Segment 级聚合统计（避免分析软件自行遍历 lanes 汇总）。</summary>
        public RoadSegmentAggStats segStats { get; set; }

        public RoadSegmentExport()
        {
            segmentId = 0;
            roadName = string.Empty;
            itemClass = string.Empty;
            width = 0f;
            lanes = new List<LaneExport>();
            segStats = new RoadSegmentAggStats();
        }
    }

    /// <summary>
    /// Segment 级聚合统计（分析软件直接拿值，省掉 N 次循环聚合）。
    /// 所有平均值仅基于 "laneType==Vehicle 且 relativeSpeed >= 0" 的车道参与（人行道/停车带不参与）。
    /// </summary>
    [Serializable]
    public class RoadSegmentAggStats
    {
        /// <summary>该 segment 的车道总数（含人行道/隔离带，等于 lanes.Count）。</summary>
        public int totalLanes { get; set; }

        /// <summary>参与交通统计的机动车道数（laneType == "Vehicle"）。分析软件用此分母算拥堵分布。</summary>
        public int vehicleLanes { get; set; }

        /// <summary>weightedAvgCongestion = Σ (vehicleLane.width × congestion) / Σ vehicleLane.width。
        /// 车道越宽（通常是主路）对 segment 整体拥堵贡献越大。范围 [0,1]，无车车道时 = -1。</summary>
        public float weightedAvgCongestion { get; set; }

        /// <summary>simpleAvgCongestion = Σ congestion / vehicleLanes。范围 [0,1]，无车车道时 = -1。</summary>
        public float simpleAvgCongestion { get; set; }

        /// <summary>weightedAvgRelativeSpeed（相对速度加权平均）。范围 [0,1]，无车车道时 = -1。</summary>
        public float weightedAvgRelativeSpeed { get; set; }

        /// <summary>机动车道总车数（简单相加，不是 FTE 单位，密度指示参考）。</summary>
        public int totalVehicles { get; set; }

        /// <summary>当前 segment 的 TMPE one-way 标记（来自 ExtSegment.oneWay）。单向道路分析软件可以只画一组箭头。</summary>
        public bool oneWay { get; set; }

        /// <summary>ExtSegment.highway。</summary>
        public bool highway { get; set; }

        /// <summary>ExtSegment.buslane。</summary>
        public bool hasBuslane { get; set; }

        public RoadSegmentAggStats()
        {
            totalLanes = 0;
            vehicleLanes = 0;
            weightedAvgCongestion = -1f;
            simpleAvgCongestion = -1f;
            weightedAvgRelativeSpeed = -1f;
            totalVehicles = 0;
            oneWay = false;
            highway = false;
            hasBuslane = false;
        }
    }

    /// <summary>
    /// 单车道交通数据导出实体。所有字段必写（即使为 -1 / 0 / 空串）。
    /// 分析软件固定按顺序解析，不关心字段缺省。
    /// </summary>
    [Serializable]
    public class LaneExport
    {
        /// <summary>该车道在 NetInfo.m_lanes[laneIndex] 中的索引（0-based），恒等于 lanes 数组下标。
        /// 范围 [0, laneCount-1]，即使默认 0 也会明确写入 JSON。</summary>
        public int laneIndex { get; set; }

        /// <summary>NetInfo.LaneType 枚举的字符串化：Vehicle / Pedestrian / Parking / None / TransportVehicle。</summary>
        public string laneType { get; set; }

        /// <summary>NetInfo.VehicleType 枚举的字符串化：Car / None / Transport / Bicycle 等（按位或标志枚举，多值时用逗号连）。</summary>
        public string vehicleType { get; set; }

        /// <summary>车道横向偏移（米）。道路中心线向左为负向右为正。
        /// 分析软件叠加渲染中心线 + position ± width/2 即可得车道矩形。</summary>
        public float position { get; set; }

        /// <summary>车道宽度（米）。</summary>
        public float width { get; set; }

        /// <summary>NetInfo.Lane.Direction：Forward / Backward / None / Both。</summary>
        public string laneDirection { get; set; }

        /// <summary>
        /// relativeSpeed × maxSpeed，保留旧游戏单位；不是物理车速或 ETA 依据。
        /// 无统计时为 0，relativeSpeed 和 congestion 同时为 -1。
        /// </summary>
        public float speed { get; set; }

        /// <summary>旧字段保留道路预设游戏单位（1 = 50 km/h）。
        /// 导航使用 network.car_speed 中以 m/s 记录的实际生效限速。</summary>
        public float maxSpeed { get; set; }

        /// <summary>
        /// 相对平均车速（0.0 = 完全停止（拥堵），1.0 = 均速达到限速（畅通））。
        ///   - 计算公式：relativeSpeed = clamp(0, 1, CalcLaneRelativeMeanSpeed(raw UInt16) / 10000.0f)
        ///   - 数据来源：TMPE.ITrafficMeasurementManager.CalcLaneRelativeMeanSpeed(segId, laneIndex, laneId, laneInfo)
        ///   - laneType≠Vehicle 或 无统计时写 -1
        /// </summary>
        public float relativeSpeed { get; set; }

        /// <summary>
        /// 当前车道上的瞬时车辆数（从 NetLane 链表精确计数，不依赖 TMPE 的 maxTrafficBuffer 近似值）。
        /// 非机动车道恒写 0。
        /// </summary>
        public int vehicleCount { get; set; }

        /// <summary>
        /// 拥堵率（分析软件主色带字段，直接拿值映射颜色）：
        ///   - 定义：congestion = 1.0 - relativeSpeed
        ///   - 范围：
        ///     • laneType != Vehicle:  -1.0  （表示此车道无交通统计意义，不参与色带）
        ///     • 有 TMPE 统计的 Vehicle 车道:  [0.0, 1.0]
        ///         0.0 完全畅通（relativeSpeed=1.0）
        ///         0.5 半堵（relativeSpeed=0.5，比如平均速度仅限速的一半）
        ///         1.0 完全堵死（relativeSpeed=0.0，车辆停止）
        ///   - 推荐的分析软件色带（与 CSLModernMap 渲染端统一）：
        ///     -1 → 灰色 / 不绘制；
        ///     0 → 绿色；0.3 → 黄色；0.6 → 橙色；0.85 → 红色；1.0 → 深酒红。
        /// </summary>
        public float congestion { get; set; }

        /// <summary>行驶方向（NetInfo.Lane.Direction 字符串化，与 laneDirection 字段相同值，保留以便 JSON 搜索）。</summary>
        public string tmpeDirection { get; set; }

        /// <summary>
        /// 数据质量位：
        ///   - 0 = 正常（TMPE 提供了滑动窗口统计值）
        ///   - 1 = 降级（GetLaneTrafficData 返回 false，speed/relativeSpeed/congestion 全部走默认降级值）
        /// 分析软件可以按此字段筛掉"刚加载还没统计积累"的异常车道。
        /// </summary>
        public int dataQuality { get; set; }

        public LaneExport()
        {
            laneIndex = 0;
            laneType = string.Empty;
            vehicleType = string.Empty;
            position = 0f;
            width = 0f;
            laneDirection = string.Empty;
            speed = 0f;
            maxSpeed = 0f;
            relativeSpeed = -1f;
            vehicleCount = 0;
            congestion = -1f;
            tmpeDirection = string.Empty;
            dataQuality = 0;
        }
    }

    /// <summary>
    /// 三维坐标（不依赖 UnityEngine，分析软件端直接反序列化可用）。
    /// x/z 平面（米），y 高度（米）。
    /// </summary>
    [Serializable]
    public struct Vec3
    {
        public float x { get; set; }
        public float y { get; set; }
        public float z { get; set; }

        public Vec3(float x, float y, float z)
            : this()
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    /// <summary>
    /// 快照总览统计。新增 KPI 全部直接返回给分析软件做总览面板（无需自行聚合）。
    /// </summary>
    [Serializable]
    public class SnapshotStats
    {
        /// <summary>NetManager 枚举到的 created 道路段总数（= NetSegment.Flags.Created）。</summary>
        public int totalSegments { get; set; }

        /// <summary>成功导出的道路段数（通过 ItemClass 道路正则筛选的 + 有 NetInfo 的段）。</summary>
        public int exportedSegments { get; set; }

        /// <summary>跳过的道路段数（非道路类 ItemClass / 没创建 / Info 为空）。</summary>
        public int skippedSegments { get; set; }

        /// <summary>导出的车道总数（segments × 每条 seg 的所有车道，含人行道/隔离带）。</summary>
        public int totalLanes { get; set; }

        /// <summary>机动车道总数（laneType == Vehicle）。全市拥堵评估的分母。</summary>
        public int totalVehicleLanes { get; set; }

        /// <summary>TMPE 桥接是否可用（强依赖下始终为 true）。</summary>
        public bool tmpeAvailable { get; set; }

        /// <summary>导出耗时（毫秒）。</summary>
        public long elapsedMs { get; set; }

        /// <summary>城市整体加权平均拥堵（所有 vehicleLane.width × congestion 加权）。
        /// 范围 [0,1]，-1 表示当前没机动车道（不可能）。</summary>
        public float cityWeightedAvgCongestion { get; set; }

        /// <summary>城市整体简单平均拥堵。</summary>
        public float citySimpleAvgCongestion { get; set; }

        /// <summary>城市总车数（所有 Vehicle 车道 vehicleCount 简单相加）。</summary>
        public long cityTotalVehicles { get; set; }

        /// <summary>城市整体加权平均相对速度。</summary>
        public float cityWeightedAvgRelativeSpeed { get; set; }

        /// <summary>TMPE 返回 false（无统计 / 数据未 ready）的 Vehicle 车道数。
        /// 接近 0 说明导出时机合适，滑动窗口已积累充分。</summary>
        public int vehicleLanesNoTmpeStats { get; set; }

        /// <summary>红绿灯节点数（schema v1.2 新增，= lights.Count）。</summary>
        public int lightNodes { get; set; }

        /// <summary>红绿灯连接道路方向的灯色状态总数（= Σ lights[].states.Count）。</summary>
        public int lightStates { get; set; }

        public SnapshotStats()
        {
            totalSegments = 0;
            exportedSegments = 0;
            skippedSegments = 0;
            totalLanes = 0;
            totalVehicleLanes = 0;
            tmpeAvailable = false;
            elapsedMs = 0L;
            cityWeightedAvgCongestion = -1f;
            citySimpleAvgCongestion = -1f;
            cityTotalVehicles = 0L;
            cityWeightedAvgRelativeSpeed = -1f;
            vehicleLanesNoTmpeStats = 0;
            lightNodes = 0;
            lightStates = 0;
        }
    }
}
