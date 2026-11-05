using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ColossalFramework;
using ColossalFramework.Math;
using CSLModernMap.Export;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProceduralObjects;
using ProceduralObjects.Classes;
using UnityEngine;
using Debug = UnityEngine.Debug;
using TrafficNs = global::CSLModernMap.Traffic;

namespace CSLModernMap.Exporter
{
    /// <summary>
    /// 地图导出器：从游戏各管理器采集完整快照，映射并写入原生 cmm-v1。
    ///
    /// 本模块改编自 CSLMapView 4.4.7（Cities: Skylines 社区模组，原作者 kkshoko）。
    /// 沿用内容采集 DTO，生产导出不经过 XML 序列化。
    /// 来源：https://steamcommunity.com/sharedfiles/filedetails/?id=845665815
    /// </summary>
    public class CSLModernMapExporter
    {
        // 忽略的线段类别（正则）
        private static readonly string[] IGNORE_SEGMENT_ITEM_CLASS =
        {
            "^(Ship|Airplane) Path$",
            "^Water Pipe$",
            "^Transport Connection$"
        };

        // 忽略的建筑类别（正则）
        private static readonly string[] IGNORE_BUILDING_ITEM_CLASS =
        {
            "^Water Pipe$",
            "^Electricity Wire$",
            "^Highway$",
            "^Train Track$",
            "^Pedestrian Path$",
            "^.* Road$",
            "^Monorail Track$"
        };

        // 忽略的建筑名（正则）
        private static readonly string[] IGNORE_BUILDING_NAME =
        {
            "^Cable Car Pylon.*"
        };

        public const int TERRAIN_RESOLUTION = 1081;
        public const int RESOURCES_RESOLUTION = 512;

        // 状态标志位常量（对应游戏枚举位值）
        private const int FLAG_CREATED = 1;               // NetNode/NetSegment/Building/District Created
        private const int FLAG_ON_GROUND = 16384;         // NetNode.OnGround
        private const int FLAG_UNDERGROUND = 524288;      // NetNode.Underground
        private const int FLAG_SEGMENT_CUSTOM_NAME = 134217728; // NetSegment.CustomName
        private const int FLAG_BUILDING_CUSTOM_NAME = 8;  // Building.CustomName
        private const int FLAG_LINE_ENABLED = 4;          // TransportLine.Enabled

        private readonly Dictionary<int, XMLNode> _nodeDict = new Dictionary<int, XMLNode>();

        /// <summary>输出目录。</summary>
        public string TargetDirectory { get; set; }

        /// <summary>地图与内嵌交通快照共用的导出时间。</summary>
        public DateTime? SharedTimestamp { get; set; }

        internal MapSnapshot CapturedSnapshot { get; private set; }
        internal ExportSummary Summary { get; private set; }

        internal string WriteSnapshot(MapSnapshot snapshot, ExportProgress progress)
        {
            ExportStages.Report(progress, 40, "EXPORT_PROGRESS_SURFACES");
            RoadGeometry.BuildFootprints(snapshot, progress);
            snapshot.Surfaces = null;
            ExportStages.Report(progress, 60, "EXPORT_PROGRESS_MAPPING");
            var document = CmmMapper.Map(snapshot, progress);
            StringBuilder warnings = new StringBuilder();
            foreach (JToken issue in (JArray)document["issues"])
                warnings.AppendLine((string)issue["code"] + ": " + (string)issue["message"]);
            if (warnings.Length > 0) Debug.LogWarning("[CSLModernMap] Export warnings:\n" + warnings);
            string fileName = SanitizeFileName(snapshot.City);
            fileName += SharedTimestamp.Value.ToString("-yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string path = Path.Combine(TargetDirectory, fileName + ".cmm.gz");
            string result = CmmWriter.Write(document, path, true, progress);
            Summary = ExportSummary.FromDocument(document, result, SharedTimestamp.Value);
            Debug.Log("[CSLModernMap] Native CMM exported: " + result);
            return result;
        }

        internal IEnumerator CaptureSteps(bool simulationPaused, ExportProgress progress)
        {
            string cityName = Singleton<SimulationManager>.instance.m_metaData.m_CityName;
            NetManager netManager = Singleton<NetManager>.instance;
            TerrainManager terrainManager = Singleton<TerrainManager>.instance;
            BuildingManager buildingManager = Singleton<BuildingManager>.instance;
            DistrictManager districtManager = Singleton<DistrictManager>.instance;
            TransportManager transportManager = Singleton<TransportManager>.instance;
            NaturalResourceManager naturalResourceManager = Singleton<NaturalResourceManager>.instance;
            PathManager pathManager = Singleton<PathManager>.instance;

            _nodeDict.Clear();

            DateTime sharedTs = SharedTimestamp ?? DateTime.Now;
            SharedTimestamp = sharedTs;

            MapSnapshot xml = new MapSnapshot();
            xml.SimulationPaused = simulationPaused;
            xml.City = string.IsNullOrEmpty(cityName) ? "Unknown" : cityName;
            xml.Generated = sharedTs.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            xml.MapTileGrid = new XMLMapTileGrid
            {
                Columns = 9, Rows = 9, TileSize = 1920f,
                OriginX = -8640f, OriginZ = -8640f
            };

            ExportStages.Report(progress, 2, "EXPORT_PROGRESS_NODES"); yield return null;
            IEnumerator nodes = ExportNodes(xml, netManager, progress);
            while (AdvanceLayer(xml, "Nodes", nodes)) yield return null;
            ExportStages.Report(progress, 6, "EXPORT_PROGRESS_NETWORKS"); yield return null;
            IEnumerator segments = ExportSegments(xml, netManager, pathManager, progress);
            while (AdvanceLayer(xml, "Networks", segments)) yield return null;
            ExportStages.Report(progress, 17, "EXPORT_PROGRESS_CORNERS"); yield return null;
            CaptureLayer(xml, "Road surfaces", delegate { xml.Surfaces = RoadGeometry.CaptureSurfaces(xml, netManager); });
            ExportStages.Report(progress, 20, "EXPORT_PROGRESS_TERRAIN"); yield return null;
            CaptureLayer(xml, "Terrain and water", delegate { ExportTerrain(xml, terrainManager); });
            ExportStages.Report(progress, 24, "EXPORT_PROGRESS_BUILDINGS"); yield return null;
            CaptureLayer(xml, "Buildings", delegate { ExportBuildings(xml, buildingManager); });
            ExportStages.Report(progress, 28, "EXPORT_PROGRESS_AREAS"); yield return null;
            CaptureLayer(xml, "Districts", delegate { ExportDistricts(xml, districtManager); });
            CaptureLayer(xml, "District grid", delegate { ExportDistrictGrid(xml, districtManager); });
            if (SteamHelper.IsDLCOwned((SteamHelper.DLC)715191))
            {
                CaptureLayer(xml, "Parks", delegate { ExportParks(xml, districtManager); });
                CaptureLayer(xml, "Park grid", delegate { ExportParkGrid(xml, districtManager); });
            }
            ExportStages.Report(progress, 32, "EXPORT_PROGRESS_TRANSIT"); yield return null;
            CaptureLayer(xml, "Transport lines", delegate { ExportTransports(xml, transportManager); });
            ExportStages.Report(progress, 34, "EXPORT_PROGRESS_FORESTS"); yield return null;
            CaptureLayer(xml, "Forests", delegate { ExportForests(xml, naturalResourceManager); });
            ExportStages.Report(progress, 36, "EXPORT_PROGRESS_OBJECTS"); yield return null;
            // Keep the optional PO assembly outside the distribution.
            try
            {
                ExportProceduralObjects(xml);
            }
            catch (Exception ex)
            {
                Warn(xml, "Procedural objects omitted", ex);
            }

            ExportStages.Report(progress, 38, "EXPORT_PROGRESS_TRAFFIC"); yield return null;
            try
            {
                if (TrafficNs.TMPEBridge.IsAvailable)
                {
                    var tExp = new TrafficNs.TrafficSnapshotExporter();
                    tExp.RoadGeometry = new Dictionary<int, List<XMLVector3>>();
                    foreach (XMLSegment segment in xml.SegmentList) tExp.RoadGeometry.Add(segment.Id, segment.Points);
                    tExp.SharedTimestamp = sharedTs;
                    tExp.SimulationPausedOverride = simulationPaused;
                    string err;
                    bool paused;
                    string json = tExp.ExportToJson(out err, out paused);
                    if (!string.IsNullOrEmpty(json))
                    {
                        xml.TrafficSnapshotJson = json;
                    }
                    else if (!string.IsNullOrEmpty(err))
                    {
                        xml.Problems.Add("Traffic snapshot omitted: " + err.Split('\n')[0]);
                        Debug.LogWarning(
                            "[CSLModernMapExporter] TM:PE 可用但交通快照生成失败，仍仅导出地图： "
                            + err.Split('\n')[0]);
                    }
                }
            }
            catch (Exception tex)
            {
                xml.Problems.Add("Traffic snapshot omitted: " + tex.Message);
                // 交通失败不能影响地图主导出
                Debug.LogError(
                    "[CSLModernMapExporter] 内嵌交通快照异常（忽略，继续导出地图）： "
                    + tex.GetType().Name + ": " + tex.Message);
            }

            CapturedSnapshot = xml;
        }


        private delegate void CaptureAction();

        private static void CaptureLayer(MapSnapshot snapshot, string layer, CaptureAction capture)
        {
            try { capture(); }
            catch (Exception ex) { Warn(snapshot, layer + " capture stopped; remaining content omitted", ex); }
        }

        private static bool AdvanceLayer(MapSnapshot snapshot, string layer, IEnumerator capture)
        {
            try { return capture.MoveNext(); }
            catch (Exception ex)
            {
                Warn(snapshot, layer + " capture stopped; remaining content omitted", ex);
                return false;
            }
        }

        private static void Warn(MapSnapshot snapshot, string context, Exception error)
        {
            string message = context + ": " + error.GetType().Name + ": " + error.Message;
            snapshot.Problems.Add(message);
        }

        private IEnumerator ExportNodes(MapSnapshot xml, NetManager netManager, ExportProgress progress)
        {
            NetNode[] buffer = netManager.m_nodes.m_buffer;
            Stopwatch slice = Stopwatch.StartNew();
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((i & 255) == 0 && slice.ElapsedMilliseconds >= 8)
                { ExportStages.Report(progress, 2 + 4 * i / buffer.Length, "EXPORT_PROGRESS_NODES"); yield return null; slice.Reset(); slice.Start(); }
                NetNode node = buffer[i];
                if (HasFlag((int)node.m_flags, FLAG_CREATED))
                {
                    try
                    {
                        XMLNode xn = new XMLNode();
                        xn.Id = i;
                        xn.Position = ToXML(node.m_position);
                        xn.UnderGround = HasFlag((int)node.m_flags, FLAG_UNDERGROUND);
                        xn.OnGround = HasFlag((int)node.m_flags, FLAG_ON_GROUND);
                        NetInfo info = node.Info;
                        if (info == null) xml.Problems.Add("Node " + i + " service omitted: network prefab missing; position and connectivity retained.");
                        else xn.SubService = info.GetSubService().ToString();
                        xml.NodeList.Add(xn);
                        _nodeDict.Add(i, xn);
                    }
                    catch (Exception ex) { Warn(xml, "Node " + i + " omitted; connected segments will be omitted", ex); }
                }
            }
        }


        private IEnumerator ExportSegments(MapSnapshot xml, NetManager netManager, PathManager pathManager, ExportProgress progress)
        {
            bool pathsReady = false;
            try { pathManager.WaitForAllPaths(); pathsReady = true; }
            catch (Exception ex) { Warn(xml, "Transit paths omitted; path calculation could not finish", ex); }
            NetNode[] nodes = netManager.m_nodes.m_buffer;
            NetSegment[] buffer = netManager.m_segments.m_buffer;
            Stopwatch slice = Stopwatch.StartNew();

            for (int i = 0; i < buffer.Length; i++)
            {
                if ((i & 255) == 0 && slice.ElapsedMilliseconds >= 8)
                { ExportStages.Report(progress, 6 + 11 * i / buffer.Length, "EXPORT_PROGRESS_NETWORKS"); yield return null; slice.Reset(); slice.Start(); }
                NetSegment seg = buffer[i];
                if (!HasFlag((int)seg.m_flags, FLAG_CREATED))
                {
                    continue;
                }
                try
                {
                    NetInfo info = seg.Info;
                    if (MatchesAny(IGNORE_SEGMENT_ITEM_CLASS, info.m_class.name))
                    {
                        continue;
                    }

                    XMLNode startNode;
                    XMLNode endNode;
                    if (!_nodeDict.TryGetValue(seg.m_startNode, out startNode) ||
                        !_nodeDict.TryGetValue(seg.m_endNode, out endNode))
                    {
                        xml.Problems.Add("Segment " + i + " omitted: missing endpoint " + seg.m_startNode + " or " + seg.m_endNode);
                        continue;
                    }

                    XMLSegment xs = new XMLSegment();
                    xs.Id = i;
                    xs.StartNodeId = seg.m_startNode;
                    xs.EndNodeId = seg.m_endNode;
                    xs.Name = info.name;
                    xs.ItemClass = info.m_class.name;
                    if (HasFlag((int)seg.m_flags, FLAG_SEGMENT_CUSTOM_NAME))
                    {
                        InstanceID instanceId = default(InstanceID);
                        instanceId.NetSegment = (ushort)i;
                        xs.CustomName = Singleton<InstanceManager>.instance.GetName(instanceId);
                    }
                    else if (info.m_netAI is RoadAI)
                    {
                        xs.GameName = netManager.GetSegmentName((ushort)i);
                    }
                    xs.Width = info.m_halfWidth * 2f;

                    xs.Points = RoadGeometry.Centerline(seg, nodes);
                    NetworkFacts facts = RoadGeometry.Facts(seg, startNode, endNode);
                    if (facts.Kind == "ROAD")
                    {
                        try { RoadGeometry.CaptureCarSpeeds(seg, facts); }
                        catch (Exception ex)
                        {
                            facts.CarSpeedReadFailed = true;
                            Warn(xml, "Segment " + i + " car navigation omitted: speed limit read failed; road retained", ex);
                        }
                    }
                    try { RoadGeometry.CaptureTracks((ushort)i, seg, netManager, facts); }
                    catch (Exception ex)
                    {
                        facts.Tracks.Clear(); facts.TrackGeometry.Clear(); facts.TrackWidth.Clear();
                        Warn(xml, "Segment " + i + " derived tracks omitted; road retained", ex);
                    }

                    if (seg.m_path != 0 && pathsReady)
                    {
                        try
                        {
                            XMLPathUnit pathUnit = new XMLPathUnit();
                            pathUnit.Id = seg.m_path;
                            AddPathPositions(seg.m_path, pathManager, pathUnit);
                            xs.PathUnit = pathUnit;
                        }
                        catch (Exception ex) { Warn(xml, "Segment " + i + " path " + seg.m_path + " omitted; segment geometry retained", ex); }
                    }

                    xml.Networks.Add(i, facts);
                    xml.SegmentList.Add(xs);
                }
                catch (Exception ex) { Warn(xml, "Segment " + i + " omitted", ex); }

            }



        }

        private static void AddPathPositions(uint first, PathManager pathManager, XMLPathUnit xmlPath)
        {
            uint id = first;
            HashSet<uint> visited = new HashSet<uint>();
            while (id != 0)
            {
                if (id >= pathManager.m_pathUnits.m_buffer.Length || !visited.Add(id))
                    throw new InvalidDataException("Invalid path unit chain at " + id);
                PathUnit unit = pathManager.m_pathUnits.m_buffer[id];
                for (int j = 0; j < unit.m_positionCount; j++)
                {
                    xmlPath.SegmentIdList.Add(unit.GetPosition(j).m_segment);
                }
                id = unit.m_nextPathUnit;
            }
        }


        private void ExportTerrain(MapSnapshot xml, TerrainManager terrainManager)
        {
            xml.SeaLevel = terrainManager.WaterSimulation.m_currentSeaLevel;
            ushort[] rawHeights = terrainManager.RawHeights2;
            ushort[] blockHeights = terrainManager.BlockHeights;
            var waterCells = terrainManager.WaterSimulation.BeginRead();
            try
            {

                int total = TERRAIN_RESOLUTION * TERRAIN_RESOLUTION;
                float[] heights = new float[total], depths = new float[total];
                for (int i = 0; i < total; i++)
                {
                    ushort land = rawHeights[i];
                    ushort water = waterCells[i].m_height;
                    ushort waterValue = water < 64
                        ? water
                        : (ushort)(blockHeights[i] - land + water);
                    heights[i] = land / 64f;
                    depths[i] = waterValue / 64f;
                }
                byte[] heightData = new byte[total * 4], waterData = new byte[total * 4];
                Buffer.BlockCopy(heights, 0, heightData, 0, heightData.Length);
                Buffer.BlockCopy(depths, 0, waterData, 0, waterData.Length);
                xml.TerrainHeight = heightData; xml.TerrainWater = waterData;
            }
            finally
            {
                terrainManager.WaterSimulation.EndRead();
            }
        }


        private void ExportBuildings(MapSnapshot xml, BuildingManager buildingManager)
        {
            Building[] buffer = buildingManager.m_buildings.m_buffer;
            for (int i = 0; i < buffer.Length; i++)
            {
                Building building = buffer[i];
                if (!HasFlag((int)building.m_flags, FLAG_CREATED))
                {
                    continue;
                }
                try
                {
                    BuildingInfo info = building.Info;
                    if (MatchesAny(IGNORE_BUILDING_ITEM_CLASS, info.m_class.name) ||
                        MatchesAny(IGNORE_BUILDING_NAME, info.name))
                    {
                        continue;
                    }

                    int width = building.Width;
                    int length = building.Length;
                    Vector3 position = building.m_position;
                    Vector3 dir = new Vector3(Mathf.Cos(building.m_angle), 0f, Mathf.Sin(building.m_angle)) * 8f;
                    Vector3 normal = new Vector3(dir.z, 0f, -dir.x);

                    XMLBuilding xb = new XMLBuilding();
                    xb.Id = i;
                    xb.ParkId = GetBuildingArea((ushort)i, ref building, info.m_buildingAI);
                    xb.Name = info.name;
                    var generated = info.m_generatedInfo;
                    float height, elevation;
                    if (generated != null && BuildingHeight.TryMeasure(
                        generated.m_min.y, generated.m_max.y, position.y, out height, out elevation))
                    {
                        xb.HeightMeters = height;
                        xb.BaseElevationMeters = elevation;
                    }
                    else
                    {
                        Warn(xml, "Building " + i + " height omitted: missing or invalid model bounds",
                            new InvalidOperationException("Invalid building vertical bounds"));
                    }
                    xb.Points.Add(ToXML(position - width / 2f * dir - length / 2f * normal));
                    xb.Points.Add(ToXML(position + width / 2f * dir - length / 2f * normal));
                    xb.Points.Add(ToXML(position + width / 2f * dir + length / 2f * normal));
                    xb.Points.Add(ToXML(position - width / 2f * dir + length / 2f * normal));

                    string renderedName = buildingManager.GetBuildingName((ushort)i, info.m_instanceID);
                    if (HasFlag((int)building.m_flags, FLAG_BUILDING_CUSTOM_NAME))
                    {
                        xb.MyName = renderedName;
                    }
                    else if (!String.IsNullOrEmpty(renderedName))
                    {
                        xb.DisplayName = renderedName;
                    }
                    xb.Service = info.GetService().ToString();
                    xb.GrowthKind = info.m_buildingAI is PrivateBuildingAI ? "zoned" : "placed";
                    if (xb.GrowthKind == "zoned")
                    {
                        switch (info.GetService())
                        {
                            case ItemClass.Service.Residential: xb.GrowthUse = "residential"; break;
                            case ItemClass.Service.Commercial: xb.GrowthUse = "commercial"; break;
                            case ItemClass.Service.Industrial: xb.GrowthUse = "industrial"; break;
                            case ItemClass.Service.Office: xb.GrowthUse = "office"; break;
                        }
                    }
                    xb.SubService = info.GetSubService().ToString();
                    xb.ItemClass = info.m_class.name;
                    try { ReadBuildingCategory(xb, info); }
                    catch (Exception ex) { Warn(xml, "Building " + i + " category label omitted; building retained", ex); }

                    if (building.m_subBuilding != 0)
                    {
                        xb.SubBuildingId = building.m_subBuilding.ToString();
                    }
                    if (building.m_parentBuilding != 0)
                    {
                        xb.ParentBuildingId = building.m_parentBuilding.ToString();
                    }
                    xml.BuildingList.Add(xb);
                }
                catch (Exception ex) { Warn(xml, "Building " + i + " omitted", ex); }
            }
        }


        private static byte GetBuildingArea(ushort buildingId, ref Building building, BuildingAI ai)
        {
            var campus = ai as CampusBuildingAI;
            if (campus != null)
                return campus.GetArea(buildingId, ref building);

            var airport = ai as AirportBuildingAI;
            if (airport != null)
                return airport.GetArea(buildingId, ref building);

            var airportGate = ai as AirportGateAI;
            if (airportGate != null)
                return airportGate.GetArea(buildingId, ref building);

            var cargoGate = ai as AirportCargoGateAI;
            if (cargoGate != null)
                return cargoGate.GetArea(buildingId, ref building);

            // 无公开查询接口的建筑复用游戏归属校验规则，不调用会更新状态的 Check 方法。
            var manager = Singleton<DistrictManager>.instance;
            byte areaId = manager.GetPark(building.m_position);
            if (areaId == 0)
                return 0;
            var area = manager.m_parks.m_buffer[areaId];

            if (ai is ParkBuildingAI || ai is ParkGateAI)
                return area.IsPark ? areaId : (byte)0;

            var mainCampus = ai as MainCampusBuildingAI;
            if (mainCampus != null)
                return area.IsCampus
                    && mainCampus.m_campusType != DistrictPark.ParkType.GenericCampus
                    && mainCampus.m_campusType == area.m_parkType ? areaId : (byte)0;

            if (ai is VarsitySportsArenaAI)
                return area.IsCampus && area.m_parkType != DistrictPark.ParkType.GenericCampus
                    ? areaId : (byte)0;

            var industry = ai as IndustryBuildingAI;
            if (industry != null)
                return area.IsIndustry
                    && industry.m_industryType != DistrictPark.ParkType.Industry
                    && industry.m_industryType == area.m_parkType ? areaId : (byte)0;

            var mainIndustry = ai as MainIndustryBuildingAI;
            if (mainIndustry != null)
                return area.IsIndustry
                    && mainIndustry.m_industryType != DistrictPark.ParkType.Industry
                    && mainIndustry.m_industryType == area.m_parkType ? areaId : (byte)0;

            if (ai is WarehouseAI)
                return area.IsIndustry ? areaId : (byte)0;
            if (ai is ServicePointAI)
                return area.IsPedestrianZone ? areaId : (byte)0;
            return 0;
        }


        private static void ReadBuildingCategory(XMLBuilding building, BuildingInfo info)
        {
            var zone = info.m_class.GetZone();
            string key = zone == ItemClass.Zone.None ? info.gameObject.name : zone.ToString();
            string table = zone == ItemClass.Zone.None ? "BUILDING_TITLE" : "ZONEDBUILDING_TITLE";
            // 与游戏分区建筑信息面板使用相同的专精分类键。
            switch (building.SubService)
            {
                case "CommercialTourist": key = "CommercialTourism"; break;
                case "CommercialLeisure":
                case "CommercialEco":
                case "CommercialWallToWall":
                case "ResidentialWallToWall":
                case "OfficeHightech":
                case "OfficeWallToWall":
                case "OfficeFinancial": key = building.SubService; break;
            }
            building.CategoryId = table + "[" + key + "]";
            building.CategoryLocale = ColossalFramework.Globalization.LocaleManager.instance.language;
            building.CategoryName = ColossalFramework.Globalization.Locale.Exists(table, key)
                ? ColossalFramework.Globalization.Locale.Get(table, key) : "";
            if (String.IsNullOrEmpty(building.CategoryName) || String.IsNullOrEmpty(building.CategoryLocale))
                Debug.LogWarning("[CSLModernMapExporter] Building " + building.Id
                    + " has no official category localization: " + building.CategoryId
                    + "; its category label will be omitted.");
        }


        private void ExportDistricts(MapSnapshot xml, DistrictManager districtManager)
        {
            District[] buffer = districtManager.m_districts.m_buffer;
            for (int i = 0; i < buffer.Length; i++)
            {
                District district = buffer[i];
                if (HasFlag((int)district.m_flags, FLAG_CREATED))
                {
                    try
                    {
                        XMLDistrict xd = new XMLDistrict();
                        xd.Id = i;
                        xd.Name = districtManager.GetDistrictName(i);
                        // 专精类型 = 区划策略位标志（如 IndustrialFarming/Ore/Oil/Forestry、
                        // CommercialHigh、OfficeHightech…；None=普通区划）
                        xd.Type = district.m_specializationPolicies.ToString();
                        xd.Position = ToXML(district.m_nameLocation);
                        xml.DistrictList.Add(xd);
                    }
                    catch (Exception ex) { Warn(xml, "District " + i + " omitted", ex); }
                }
            }
        }

        // 网格采样点为单元中心，归属通过游戏 API 读取。

        // 使用自定义委托，避免引入不兼容 CS1 运行时的框架类型。
        private delegate byte ZoneSampler(Vector3 pos);
        private delegate bool ZonePredicate(byte id);
        private delegate void ZoneSetter(string value);

        private void ExportDistrictGrid(MapSnapshot xml, DistrictManager districtManager)
        {
            ExportZoneGrid(
                xml, "district",
                districtManager.m_districtGrid,
                pos => districtManager.SampleDistrict(pos),
                id => IsCreatedDistrict(districtManager, id),
                s => xml.DistrictGrid = s);
        }

        /// <summary>
        /// 功能区网格导出：采样游戏公园区/产业区归属网格（同 DistrictGrid 思路，
        /// 用 DistrictManager.SamplePark），输出 <ParkGrid> 供 Python 端绘制功能区边界。
        /// </summary>
        private void ExportParkGrid(MapSnapshot xml, DistrictManager districtManager)
        {
            ExportZoneGrid(
                xml, "park",
                districtManager.m_districtGrid,
                pos => districtManager.SamplePark(pos),
                id => IsCreatedPark(districtManager, id),
                s => xml.ParkGrid = s);
        }

        /// <summary>
        /// 由网格长度推断分辨率，通过 sampler 读取归属，避免绑定游戏网格元素类型。
        /// setter 接收 cell=..;x,z,id;... 格式的采样结果。
        /// </summary>
        private void ExportZoneGrid(MapSnapshot xml, string context, Array grid,
            ZoneSampler sampler, ZonePredicate isValid,
            ZoneSetter setter)
        {
            try
            {
                if (grid == null || grid.Length == 0)
                {
                    throw new InvalidOperationException("grid is empty");
                }
                int n = (int)Math.Round(Math.Sqrt(grid.Length));
                if (n <= 1 || n * n != grid.Length)
                {
                    throw new InvalidOperationException("invalid grid length: " + grid.Length);
                }
                float cell = 17280f / n;
                StringBuilder sb = new StringBuilder();
                sb.Append("cell=").Append(cell.ToString("F1", CultureInfo.InvariantCulture)).Append(';');
                int count = 0;
                for (int row = 0; row < n; row++)
                {
                    float z = -8640f + (row + 0.5f) * cell;
                    for (int col = 0; col < n; col++)
                    {
                        float x = -8640f + (col + 0.5f) * cell;
                        byte zoneId = sampler(new Vector3(x, 0f, z));
                        if (!isValid(zoneId))
                        {
                            continue;
                        }
                        if (count > 0)
                        {
                            sb.Append(';');
                        }
                        // 坐标保留 1 位小数：F0 取整会把 19.2m 格线四舍五入成
                        // 整数（19/20 交替），渲染端按 cell 除法栅格化时每 5 格
                        // 出现像素重叠/空隙，掩膜碎成格点（"行政区图格点状"）。
                        sb.Append(x.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(z.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                          .Append(zoneId.ToString(CultureInfo.InvariantCulture));
                        count++;
                    }
                }
                setter(count > 0 ? sb.ToString() : null);
            }
            catch (Exception error)
            {
                xml.Problems.Add(context + " grid omitted: " + error.GetType().Name + ": " + error.Message);
                setter(null);
            }
        }

        /// <summary>
        /// 判断区划 id 是否为已创建的有效区划。
        /// SampleDistrict 返回 0 可能表示"无区划"，但默认区划 id 也是 0，
        /// 因此用 m_districts 缓冲区的 flags 判定，避免 0 语义歧义。
        /// </summary>
        private bool IsCreatedDistrict(DistrictManager districtManager, byte districtId)
        {
            District[] buffer = districtManager.m_districts.m_buffer;
            if (districtId >= buffer.Length)
            {
                return false;
            }
            return HasFlag((int)buffer[districtId].m_flags, FLAG_CREATED);
        }

        /// <summary>
        /// 判断公园区/产业区 id 是否为已创建的有效实体（同 IsCreatedDistrict）。
        /// </summary>
        private bool IsCreatedPark(DistrictManager districtManager, byte parkId)
        {
            DistrictPark[] buffer = districtManager.m_parks.m_buffer;
            if (parkId >= buffer.Length)
            {
                return false;
            }
            return HasFlag((int)buffer[parkId].m_flags, FLAG_CREATED);
        }

        private void ExportParks(MapSnapshot xml, DistrictManager districtManager)
        {
            DistrictPark[] buffer = districtManager.m_parks.m_buffer;
            for (int i = 0; i < buffer.Length; i++)
            {
                DistrictPark park = buffer[i];
                if (HasFlag((int)park.m_flags, FLAG_CREATED))
                {
                    try
                    {
                        XMLDistrictPark xp = new XMLDistrictPark();
                        xp.Id = i;
                        xp.MainBuildingId = park.m_mainGate;
                        xp.Name = districtManager.GetParkName(i);
                        xp.Position = ToXML(park.m_nameLocation);
                        xp.Type = park.m_parkType.ToString();
                        xml.ParkList.Add(xp);
                    }
                    catch (Exception ex) { Warn(xml, "Park " + i + " omitted", ex); }
                }
            }
        }


        private void ExportTransports(MapSnapshot xml, TransportManager transportManager)
        {
            TransportLine[] buffer = transportManager.m_lines.m_buffer;
            for (int i = 0; i < buffer.Length; i++)
            {
                TransportLine line = buffer[i];
                if (!HasFlag((int)line.m_flags, FLAG_CREATED) &&
                    !HasFlag((int)line.m_flags, FLAG_LINE_ENABLED))
                {
                    continue;
                }
                try
                {
                    XMLTransport xt = new XMLTransport();
                    xt.Id = i;
                    xt.Name = transportManager.GetLineName((ushort)i);
                    xt.TransportType = line.Info.m_transportType.ToString();
                    Color32 color = (Color32)line.GetColor();
                    xt.UserColor = new XMLColor(color.a, color.r, color.g, color.b);

                    int stopCount = line.CountStops(line.m_stops);
                    for (int j = 0; j < stopCount; j++)
                    {
                        XMLStop stop = new XMLStop();
                        stop.Node = line.GetStop(j);
                        xt.Stops.Add(stop);
                    }
                    xml.TransportList.Add(xt);
                }
                catch (Exception ex) { Warn(xml, "Transport line " + i + " omitted", ex); }
            }
        }


        private void ExportForests(CSLExportXML xml, NaturalResourceManager nrManager)
        {
            var resources = nrManager.m_naturalResources;
            List<string> rows = new List<string>();
            for (int i = 0; i < RESOURCES_RESOLUTION; i++)
            {
                StringBuilder sb = new StringBuilder(RESOURCES_RESOLUTION * 4);
                for (int j = 0; j < RESOURCES_RESOLUTION; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }
                    sb.Append(resources[i * RESOURCES_RESOLUTION + j].m_tree);
                }
                rows.Add(sb.ToString());
            }
            xml.ForestList = rows;
        }

        private void ExportProceduralObjects(MapSnapshot snapshot)
        {
            ProceduralObjectsLogic logic = ProceduralObjectsLogic.instance;
            if (logic == null || logic.proceduralObjects == null) return;
            List<XMLProceduralObject> objects = new List<XMLProceduralObject>();
            foreach (var po in logic.proceduralObjects)
            {
                if (po == null) continue;
                try
                {
                    Mesh mesh = po.overlayRenderMesh;
                    if (mesh == null) throw new InvalidDataException("Object mesh is missing");
                    Bounds bounds = mesh.bounds;
                    Vector3 position = po.m_position;
                    Color color = po.m_color;
                    float rotation = po.m_rotation.eulerAngles.y * Mathf.Deg2Rad;
                    if (rotation > Mathf.PI) rotation -= 2f * Mathf.PI;
                    objects.Add(new XMLProceduralObject {
                        Id = po.id, X = position.x, Y = position.y, Z = position.z,
                        RotationY = rotation, Scale = po.m_scale,
                        PrefabName = po.basePrefabName ?? "", Source = po.baseInfoType ?? "",
                        CenterX = position.x + bounds.center.x, CenterZ = position.z + bounds.center.z,
                        ExtentX = bounds.extents.x, ExtentZ = bounds.extents.z,
                        ColorR = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f),
                        ColorG = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f),
                        ColorB = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f) });
                }
                catch (Exception ex) { snapshot.Problems.Add("PO " + po.id + " omitted: " + ex.Message); }
            }
            snapshot.ProceduralObjects = objects;
        }

        private static string SanitizeFileName(string cityName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = cityName.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }
            return new string(chars);
        }


        private static bool HasFlag(int flags, int flag)
        {
            return (flags & flag) != 0;
        }

        private static bool MatchesAny(string[] patterns, string input)
        {
            if (input == null)
            {
                return false;
            }
            for (int i = 0; i < patterns.Length; i++)
            {
                if (Regex.IsMatch(input, patterns[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static XMLVector3 ToXML(Vector3 v)
        {
            if (float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z)
                || float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z))
                throw new InvalidDataException("Non-finite object position");
            return new XMLVector3(v.x, v.y, v.z);
        }
    }
}
