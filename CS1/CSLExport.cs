using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace CSLModernMap.Export
{
    public class XMLMapTileGrid
    {
        [XmlAttribute("columns")] public int Columns { get; set; }
        [XmlAttribute("rows")] public int Rows { get; set; }
        [XmlAttribute("tileSize")] public float TileSize { get; set; }
        [XmlAttribute("originX")] public float OriginX { get; set; }
        [XmlAttribute("originZ")] public float OriginZ { get; set; }
    }

    /// <summary>CSL 地图导出 XML 数据模型（与 CSLMapView 4.x 格式兼容）。</summary>
    [XmlRoot("CSLExportXML")]
    public class CSLExportXML
    {
        public const string CURRENT_VERSION = "4.1";
        public const int TERRAIN_RESOLUTION = 1081;

        [XmlAttribute("version")]
        public string FileVersion { get; set; }

        [XmlElement("City")]
        public string City { get; set; }

        [XmlElement("Generated")]
        public string Generated { get; set; }

        [XmlElement("SeaLevel")]
        public float SeaLevel { get; set; }

        // Full 81-tile grid, independent of purchased areas and unlock mods.
        [XmlElement("MapTileGrid")]
        public XMLMapTileGrid MapTileGrid { get; set; }

        [XmlArray("Nodes")]
        [XmlArrayItem("Node")]
        public List<XMLNode> NodeList { get; set; }

        [XmlArray("Segments")]
        [XmlArrayItem("Seg")]
        public List<XMLSegment> SegmentList { get; set; }

        [XmlArray("Terrains")]
        [XmlArrayItem("Ter")]
        public List<string> TerrainLines { get; set; }

        [XmlArray("Buildings")]
        [XmlArrayItem("Buil")]
        public List<XMLBuilding> BuildingList { get; set; }

        [XmlArray("Districts")]
        [XmlArrayItem("Dist")]
        public List<XMLDistrict> DistrictList { get; set; }

        [XmlArray("Parks")]
        [XmlArrayItem("Park")]
        public List<XMLDistrictPark> ParkList { get; set; }

        [XmlArray("Transports")]
        [XmlArrayItem("Trans")]
        public List<XMLTransport> TransportList { get; set; }

        [XmlArray("Forests")]
        [XmlArrayItem("Forest")]
        public List<string> ForestList { get; set; }

        /// <summary>
        /// 区划网格（行政区划边界数据，v6.5+）。
        /// 格式：紧凑文本 "cell=96;x,z,id;x,z,id;..."，
        /// 每项为一个区划采样格点（世界坐标 x,z + 区划 id，id 与 DistrictList 的 Id 对应）。
        /// 值为 null / 空时 XmlSerializer 跳过该节点，保持与旧版 .mmap 100% 兼容。
        /// </summary>
        [XmlElement("DistrictGrid")]
        public string DistrictGrid
        {
            get { return _districtGrid; }
            set { _districtGrid = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _districtGrid;

        /// <summary>
        /// 功能区网格（公园区/产业区边界数据，v6.5+）。
        /// 格式同 DistrictGrid："cell=96;x,z,id;x,z,id;..."，
        /// id 与 Parks / Park 的 id 属性对应。
        /// 值为 null / 空时 XmlSerializer 跳过该节点，保持与旧版 .mmap 100% 兼容。
        /// </summary>
        [XmlElement("ParkGrid")]
        public string ParkGrid
        {
            get { return _parkGrid; }
            set { _parkGrid = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _parkGrid;

        /// <summary>
        /// 内嵌交通快照 JSON（紧凑字符串）。
        /// 写 XML 时由 XmlSerializer 自动将 &amp; &lt; &gt; ' " 转义为实体，
        /// Python ElementTree 读取时 .text 会自动解转义，无需额外处理。
        /// 值为 null / 空时 XmlSerializer 跳过该节点，保持与旧版 .mmap 100% 兼容。
        /// </summary>
        [XmlElement("TrafficSnapshotJson")]
        public string TrafficSnapshotJson
        {
            get { return _trafficSnapshotJson; }
            set { _trafficSnapshotJson = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _trafficSnapshotJson;

        /// <summary>
        /// Procedural Objects（魔物 MOD）对象列表。
        /// 未安装 PO mod 时导出器会置为 null，XmlSerializer 自动跳过该节点，保持与旧版 .mmap 100% 兼容。
        /// </summary>
        [XmlArray("ProceduralObjects")]
        [XmlArrayItem("PO")]
        public List<XMLProceduralObject> ProceduralObjects
        {
            get { return _proceduralObjects; }
            set { _proceduralObjects = (value != null && value.Count > 0) ? value : null; }
        }
        private List<XMLProceduralObject> _proceduralObjects;

        public CSLExportXML()
        {
            NodeList = new List<XMLNode>();
            SegmentList = new List<XMLSegment>();
            TerrainLines = new List<string>();
            BuildingList = new List<XMLBuilding>();
            DistrictList = new List<XMLDistrict>();
            ParkList = new List<XMLDistrictPark>();
            TransportList = new List<XMLTransport>();
            ForestList = new List<string>();
        }
    }

    /// <summary>
    /// 单个 Procedural Object 的 XML 描述。
    /// - position: 世界坐标 (x,y,z)，y 为高度（与 cslmap 其他实体坐标系一致）
    /// - m_position 来自 PO mod 的本地位置（已经是世界坐标，不需要相对变换）
    /// - AABB 的 cx/cz 是 mesh.bounds 中心，ex/ez 是 mesh.bounds extents（单位：米，半宽）
    ///   AABB 四角可由：cx±ex, cz±ez 得到
    /// - ry: Y 轴旋转角度（弧度，0 时 AABB 轴对齐）
    /// - scale: 等比缩放倍数（m_scale，PO 只有单浮点缩放）
    /// - src: 来源类型 "PROP" | "BUILDING"
    /// - pfn: basePrefabName（来源资产完整名，用于分类色板匹配）
    /// - cr/cg/cb: 用户染色（0~255 整数，白色 (255,255,255) 时省略该三属性，由 Python 端用分类色板 fallback）
    /// </summary>
    public class XMLProceduralObject
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("x")]
        public float X { get; set; }

        [XmlAttribute("y")]
        public float Y { get; set; }

        [XmlAttribute("z")]
        public float Z { get; set; }

        /// <summary>AABB 中心 X（米）；与 X 相同（mesh.bounds.center.x + m_position.x）</summary>
        [XmlAttribute("cx")]
        public float CenterX { get; set; }

        /// <summary>AABB 中心 Z（米）</summary>
        [XmlAttribute("cz")]
        public float CenterZ { get; set; }

        /// <summary>AABB X 半宽（米，extents.x）</summary>
        [XmlAttribute("ex")]
        public float ExtentX { get; set; }

        /// <summary>AABB Z 半宽（米，extents.z）</summary>
        [XmlAttribute("ez")]
        public float ExtentZ { get; set; }

        /// <summary>Y 轴旋转（弧度）。0 时 AABB 轴对齐；Python 端若 |ry|>epsilon 则渲染 OBB。</summary>
        [XmlAttribute("ry")]
        public float RotationY { get; set; }

        [XmlAttribute("s")]
        public float Scale { get; set; }

        [XmlAttribute("src")]
        public string Source { get; set; }

        [XmlAttribute("pfn")]
        public string PrefabName { get; set; }

        /// <summary>染色 R（0~255）。染色为纯白色 (255,255,255) 时 ShouldSerialize 返回 false 省略属性，Python 端走分类色板。</summary>
        [XmlAttribute("cr")]
        public byte ColorR { get; set; }
        [XmlAttribute("cg")]
        public byte ColorG { get; set; }
        [XmlAttribute("cb")]
        public byte ColorB { get; set; }

        public bool ShouldSerializeColorR() { return !(ColorR == 255 && ColorG == 255 && ColorB == 255); }
        public bool ShouldSerializeColorG() { return !(ColorR == 255 && ColorG == 255 && ColorB == 255); }
        public bool ShouldSerializeColorB() { return !(ColorR == 255 && ColorG == 255 && ColorB == 255); }

        /// <summary>|RotationY| 很小时省略该属性，节省 XML 体积。</summary>
        public bool ShouldSerializeRotationY() { return Math.Abs(RotationY) > 1e-4f; }
    }

    public class XMLNode
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("ug")]
        public bool UnderGround { get; set; }

        [XmlAttribute("og")]
        public bool OnGround { get; set; }

        [XmlAttribute("subsrv")]
        public string SubService { get; set; }

        [XmlElement("Pos")]
        public XMLVector3 Position { get; set; }
    }

    public class XMLSegment
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("sn")]
        public int StartNodeId { get; set; }

        [XmlAttribute("en")]
        public int EndNodeId { get; set; }

        [XmlAttribute("icls")]
        public string ItemClass { get; set; }

        [XmlAttribute("width")]
        public float Width { get; set; }

        [XmlElement("Name")]
        public string Name { get; set; }

        [XmlElement("CustomName")]
        public string CustomName { get; set; }

        [XmlElement("GameName")]
        public string GameName { get; set; }

        [XmlArray("Points")]
        [XmlArrayItem("P")]
        public List<XMLVector3> Points { get; set; }

        [XmlElement("Path")]
        public XMLPathUnit PathUnit { get; set; }

        public XMLSegment()
        {
            Points = new List<XMLVector3>();
        }
    }

    public class XMLBuilding
    {
        [XmlIgnore]
        public float? HeightMeters { get; set; }

        [XmlIgnore]
        public float? BaseElevationMeters { get; set; }

        [XmlAttribute("park_id")]
        public int ParkId { get; set; }

        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlAttribute("myname")]
        public string MyName { get; set; }

        [XmlAttribute("displayname")]
        public string DisplayName { get; set; }

        [XmlAttribute("category_id")]
        public string CategoryId { get; set; }

        [XmlAttribute("category_name")]
        public string CategoryName { get; set; }

        [XmlAttribute("category_locale")]
        public string CategoryLocale { get; set; }

        [XmlAttribute("growth_kind")]
        public string GrowthKind { get; set; }

        [XmlAttribute("growth_use")]
        public string GrowthUse { get; set; }


        [XmlAttribute("srv")]
        public string Service { get; set; }

        [XmlAttribute("subsrv")]
        public string SubService { get; set; }

        [XmlAttribute("subb")]
        public string SubBuildingId { get; set; }

        [XmlAttribute("prntb")]
        public string ParentBuildingId { get; set; }

        [XmlAttribute("icls")]
        public string ItemClass { get; set; }

        [XmlArray("Points")]
        [XmlArrayItem("P")]
        public List<XMLVector3> Points { get; set; }

        public XMLBuilding()
        {
            Points = new List<XMLVector3>();
        }
    }

    public class XMLDistrict
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("name")]
        public string Name { get; set; }

        /// <summary>专精类型（游戏 District.m_districtType，如 Industrial/Ore/Oil/Forestry/Farming/Commercial/Office…）。</summary>
        [XmlAttribute("type")]
        public string Type { get; set; }

        [XmlElement("P")]
        public XMLVector3 Position { get; set; }
    }

    public class XMLDistrictPark
    {
        [XmlAttribute("main_building_id")]
        public int MainBuildingId { get; set; }

        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlElement("P")]
        public XMLVector3 Position { get; set; }

        [XmlElement("type")]
        public string Type { get; set; }
    }

    public class XMLTransport
    {
        [XmlAttribute("id")]
        public int Id { get; set; }

        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlAttribute("type")]
        public string TransportType { get; set; }

        [XmlElement("color")]
        public XMLColor UserColor { get; set; }

        [XmlArray("Stops")]
        [XmlArrayItem("Stop")]
        public List<XMLStop> Stops { get; set; }

        public XMLTransport()
        {
            Stops = new List<XMLStop>();
        }
    }

    public class XMLStop
    {
        [XmlAttribute("node")]
        public int Node { get; set; }
    }

    public class XMLColor
    {
        [XmlAttribute("a")]
        public byte A { get; set; }

        [XmlAttribute("r")]
        public byte R { get; set; }

        [XmlAttribute("g")]
        public byte G { get; set; }

        [XmlAttribute("b")]
        public byte B { get; set; }

        public XMLColor() { }

        public XMLColor(byte a, byte r, byte g, byte b)
        {
            A = a;
            R = r;
            G = g;
            B = b;
        }
    }

    public class XMLVector3 : IEquatable<XMLVector3>
    {
        [XmlAttribute("x")]
        public float X { get; set; }

        [XmlAttribute("y")]
        public float Y { get; set; }

        [XmlAttribute("z")]
        public float Z { get; set; }

        public XMLVector3() { }

        public XMLVector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(XMLVector3 other)
        {
            if (other == null) return false;
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as XMLVector3);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() ^ Y.GetHashCode() ^ Z.GetHashCode();
        }
    }

    public class XMLPathUnit
    {
        [XmlAttribute("id")]
        public uint Id { get; set; }

        [XmlArray("Segs")]
        [XmlArrayItem("Sg")]
        public List<int> SegmentIdList { get; set; }

        public XMLPathUnit()
        {
            SegmentIdList = new List<int>();
        }
    }

}
