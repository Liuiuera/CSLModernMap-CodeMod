using System;
using System.Collections.Generic;

namespace CSLModernMap.Export
{
    public class XMLMapTileGrid
    {
        public int Columns { get; set; }
        public int Rows { get; set; }
        public float TileSize { get; set; }
        public float OriginX { get; set; }
        public float OriginZ { get; set; }
    }

    /// <summary>供原生 CMM 映射使用的城市采集数据。</summary>
    public class CSLExportXML
    {

        public string City { get; set; }

        public string Generated { get; set; }

        public float SeaLevel { get; set; }

        // Full 81-tile grid, independent of purchased areas and unlock mods.
        public XMLMapTileGrid MapTileGrid { get; set; }

        public List<XMLNode> NodeList { get; set; }

        public List<XMLSegment> SegmentList { get; set; }

        public List<XMLBuilding> BuildingList { get; set; }

        public List<XMLDistrict> DistrictList { get; set; }

        public List<XMLDistrictPark> ParkList { get; set; }

        public List<XMLTransport> TransportList { get; set; }

        public List<string> ForestList { get; set; }

        /// <summary>区划采样网格：cell=96;x,z,id;...，id 对应 DistrictList。</summary>
        public string DistrictGrid
        {
            get { return _districtGrid; }
            set { _districtGrid = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _districtGrid;

        /// <summary>功能区采样网格，格式同 DistrictGrid，id 对应 ParkList。</summary>
        public string ParkGrid
        {
            get { return _parkGrid; }
            set { _parkGrid = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _parkGrid;

        /// <summary>内嵌交通快照 JSON。</summary>
        public string TrafficSnapshotJson
        {
            get { return _trafficSnapshotJson; }
            set { _trafficSnapshotJson = string.IsNullOrEmpty(value) ? null : value; }
        }
        private string _trafficSnapshotJson;

        /// <summary>已采集的 Procedural Objects；无对象时为空。</summary>
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
            BuildingList = new List<XMLBuilding>();
            DistrictList = new List<XMLDistrict>();
            ParkList = new List<XMLDistrictPark>();
            TransportList = new List<XMLTransport>();
            ForestList = new List<string>();
        }
    }

    /// <summary>Procedural Object 的世界坐标、局部包围盒、旋转和染色。</summary>
    public class XMLProceduralObject
    {
        public int Id { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        /// <summary>AABB 中心 X（米）。</summary>
        public float CenterX { get; set; }

        /// <summary>AABB 中心 Z（米）</summary>
        public float CenterZ { get; set; }

        /// <summary>AABB X 半宽（米，extents.x）</summary>
        public float ExtentX { get; set; }

        /// <summary>AABB Z 半宽（米，extents.z）</summary>
        public float ExtentZ { get; set; }

        /// <summary>Y 轴旋转（弧度）。</summary>
        public float RotationY { get; set; }

        public float Scale { get; set; }

        public string Source { get; set; }

        public string PrefabName { get; set; }

        /// <summary>对象染色，RGB 分量范围为 0~255。</summary>
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }

    }

    public class XMLNode
    {
        public int Id { get; set; }

        public bool UnderGround { get; set; }

        public bool OnGround { get; set; }

        public string SubService { get; set; }

        public XMLVector3 Position { get; set; }
    }

    public class XMLSegment
    {
        public int Id { get; set; }

        public int StartNodeId { get; set; }

        public int EndNodeId { get; set; }

        public string ItemClass { get; set; }

        public float Width { get; set; }

        public string Name { get; set; }

        public string CustomName { get; set; }

        public string GameName { get; set; }

        public List<XMLVector3> Points { get; set; }

        public XMLPathUnit PathUnit { get; set; }

        public XMLSegment()
        {
            Points = new List<XMLVector3>();
        }
    }

    public class XMLBuilding
    {
        public float? HeightMeters { get; set; }

        public float? BaseElevationMeters { get; set; }

        public int ParkId { get; set; }

        public int Id { get; set; }

        public string Name { get; set; }

        public string MyName { get; set; }

        public string DisplayName { get; set; }

        public string CategoryId { get; set; }

        public string CategoryName { get; set; }

        public string CategoryLocale { get; set; }

        public string GrowthKind { get; set; }

        public string GrowthUse { get; set; }

        public string Service { get; set; }

        public string SubService { get; set; }

        public string SubBuildingId { get; set; }

        public string ParentBuildingId { get; set; }

        public string ItemClass { get; set; }

        public List<XMLVector3> Points { get; set; }

        public XMLBuilding()
        {
            Points = new List<XMLVector3>();
        }
    }

    public class XMLDistrict
    {
        public int Id { get; set; }

        public string Name { get; set; }

        /// <summary>专精类型（游戏 District.m_districtType，如 Industrial/Ore/Oil/Forestry/Farming/Commercial/Office…）。</summary>
        public string Type { get; set; }

        public XMLVector3 Position { get; set; }
    }

    public class XMLDistrictPark
    {
        public int MainBuildingId { get; set; }

        public int Id { get; set; }

        public string Name { get; set; }

        public XMLVector3 Position { get; set; }

        public string Type { get; set; }
    }

    public class XMLTransport
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public string TransportType { get; set; }

        public XMLColor UserColor { get; set; }

        public List<XMLStop> Stops { get; set; }

        public XMLTransport()
        {
            Stops = new List<XMLStop>();
        }
    }

    public class XMLStop
    {
        public int Node { get; set; }
    }

    public class XMLColor
    {
        public byte A { get; set; }

        public byte R { get; set; }

        public byte G { get; set; }

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
        public float X { get; set; }

        public float Y { get; set; }

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
        public uint Id { get; set; }

        public List<int> SegmentIdList { get; set; }

        public XMLPathUnit()
        {
            SegmentIdList = new List<int>();
        }
    }

}
