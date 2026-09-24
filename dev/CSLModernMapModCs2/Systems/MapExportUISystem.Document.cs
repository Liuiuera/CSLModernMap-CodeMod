using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;

namespace CSLModernMap.Systems
{
    /// <summary>组装地图导出文档</summary>
    public sealed partial class MapExportUISystem
    {
        private ExportResult WriteCmmExport()
        {
            var outputDirectory = ExportPaths.EnsureExportDirectory();

            EntityManager.CompleteAllTrackedJobs();
            var now = DateTime.Now;
            var cityName = ReadCityName();
            var traffic = new TrafficExport(World, cityName, now);
            var path = Path.Combine(outputDirectory, ExportPaths.BuildFileName(cityName, now));

            var exportedNodes = new HashSet<Entity>();
            var exportedNetworks = new HashSet<Entity>();
            var derived = new Cs2DerivedNetworkState();

            var nodeJson = new StringBuilder(2 * 1024 * 1024);
            var networkJson = new StringBuilder(12 * 1024 * 1024);
            var buildingJson = new StringBuilder(12 * 1024 * 1024);
            var areaJson = new StringBuilder(1024 * 1024);
            var nodeCount = AppendNodes(nodeJson, exportedNodes);
            var networkCount = AppendNetworks(
                networkJson, exportedNodes, exportedNetworks, derived, traffic);
            traffic.AddLights(EntityManager, exportedNodes);
            var buildingCount = AppendBuildings(buildingJson);
            var areas = AppendAreas(areaJson);

            var terrain = TerrainExport.Build(World);
            var mapTileGrid = "{\"columns\":23,\"rows\":23,\"tile_size_m\":"
                + (14336d / 23d).ToString("R", CultureInfo.InvariantCulture)
                + ",\"origin\":[-7168,-7168],\"source\":\"standard\"}";

            var transit = TransitExport.Build(
                World,
                EntityManager,
                GetPrefabName,
                exportedNodes,
                exportedNetworks,
                derived);

            derived.AppendNodes(nodeJson, nodeCount);
            derived.AppendNetworks(networkJson, networkCount);
            nodeCount += derived.NodeCount;
            networkCount += derived.NetworkCount;

            var json = new StringBuilder(
                nodeJson.Length + networkJson.Length + buildingJson.Length + areaJson.Length
                + terrain.Json.Length + 4096);
            json.AppendLine("{");
            json.AppendLine("  \"schema_version\": \"cmm-v1\",");
            json.AppendLine("  \"metadata\": {");
            json.Append("    \"origin\": {\"game\": \"cs2\", \"exporter\": \"CSLModernMapCs2\"");
            CmmJson.AppendProperty(json, "source_name", cityName);
            json.AppendLine("},");
            json.Append("    \"exported_at\": ");
            CmmJson.AppendString(json, now.ToString("o", CultureInfo.InvariantCulture));
            json.AppendLine(",");
            json.Append("    \"counters\": {\"nodes\": ").Append(nodeCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"networks\": ").Append(networkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"buildings\": ").Append(buildingCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"buildings_excluded_non_building\": ").Append(m_ExcludedNonBuildingCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"buildings_excluded_no_geometry\": ").Append(m_ExcludedNoGeometryCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"buildings_extension\": ").Append(m_ExtensionCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"buildings_custom_named\": ").Append(m_CustomBuildingNameCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"network_kinds\": ").Append(CmmJson.BuildCounters(m_NetworkKindCounts))
                .Append(", \"networks_skipped_airspace\": ").Append(m_SkippedAirspaceNetworkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"networks_skipped_marker\": ").Append(m_SkippedMarkerNetworkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"road_segments_custom_named\": ").Append(m_CustomRoadNameCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"building_roles\": ").Append(CmmJson.BuildCounters(m_RoleCounts))
                .Append(", \"building_services\": ").Append(CmmJson.BuildCounters(m_ServiceCounts))
                .Append(", \"building_sub_services\": ").Append(CmmJson.BuildCounters(m_SubServiceCounts))
                .Append(", \"districts\": ").Append(areas.AdministrativeDistrictCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"park_zones\": ").Append(areas.ParkZoneCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"facility_groups\": ").Append(areas.FacilityGroupCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"areas_total\": ").Append(areas.AreaCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"area_owners\": ").Append(areas.OwnerCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"area_nodes\": ").Append(areas.NodeCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"area_kinds\": ").Append(CmmJson.BuildCounters(areas.KindCounts))
                .Append(", \"areas_missing_geometry\": ").Append(areas.MissingGeometryCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"areas_missing_nodes\": ").Append(areas.MissingNodeCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"area_duplicate_refs\": ").Append(areas.DuplicateReferenceCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"building_links\": {\"attached\": ").Append(m_AttachedLinkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"installed_upgrade\": ").Append(m_UpgradeLinkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"extension_entities\": ").Append(m_ExtensionCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"upgrade_entities\": ").Append(m_UpgradeCount.ToString(CultureInfo.InvariantCulture))
                .Append("}, \"stops\": ").Append(transit.StopCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"transit_lines\": ").Append(transit.LineCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"routes\": ").Append(transit.RouteCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"traffic_segments\": ").Append(traffic.SegmentCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"traffic_valid_segments\": ").Append(traffic.ValidSegmentCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"traffic_lanes\": ").Append(traffic.LaneCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"traffic_lights\": ").Append(traffic.LightCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine("},");
            json.AppendLine("    \"anonymized\": false,");
            json.AppendLine("    \"crs\": \"world-meters-y-up; rows=north\",");
            json.AppendLine("    \"source_path\": \"\"");
            json.AppendLine("  },");
            json.Append("  \"sea_level\": ")
                .Append(terrain.SeaLevel.ToString("0.####", CultureInfo.InvariantCulture))
                .AppendLine(",");
            json.Append("  \"terrain\": ");
            json.Append(terrain.Json);
            json.AppendLine(",");
            json.AppendLine("  \"nodes\": [");
            json.Append(nodeJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"networks\": [");
            json.Append(networkJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"buildings\": [");
            json.Append(buildingJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"districts\": [");
            json.Append(areaJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"stops\": [");
            json.Append(transit.StopsJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"transit_lines\": [");
            json.Append(transit.LinesJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"routes\": [");
            json.Append(transit.RoutesJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"derived\": {");
            json.AppendLine("    \"facility_groups\": [");
            json.Append(areas.FacilityGroupsJson);
            json.AppendLine("    ]");
            json.AppendLine("  },");
            json.AppendLine("  \"forests\": [],");
            json.AppendLine("  \"procedural_objects\": [],");
            json.Append("  \"traffic\": ").Append(traffic.SnapshotJson()).AppendLine(",");
            json.Append("  \"issues\": ").Append(CombineIssueArrays(
                CombineIssueArrays(CombineIssueArrays(terrain.Issues, transit.Issues), areas.Issues),
                traffic.IssuesJson()));
            json.AppendLine(",");
            json.Append("  \"extensions\": {\"map_tile_grid\": ").Append(mapTileGrid);
            json.Append(", \"cs2\": {\"game_version\": ");
            CmmJson.AppendString(json, Application.version);
            json.Append(", \"world\": ");
            CmmJson.AppendString(json, World.Name);
            json.Append(", \"traffic_snapshot\": ").Append(traffic.MetadataJson());
            json.Append(", \"cargo_routes\": ")
                .Append(transit.CargoRouteCount.ToString(CultureInfo.InvariantCulture));
            json.Append(", \"network_modes\": ")
                .Append(derived.BuildNetworkModesJson());
            json.Append(", \"derived_networks\": ")
                .Append(derived.BuildDerivedNetworksJson());
            if (!string.IsNullOrEmpty(transit.LinePrefabsJson))
            {
                json.Append(", \"transit_line_prefabs\": ").Append(transit.LinePrefabsJson);
            }

            json.AppendLine("}}");
            json.AppendLine("}");

            var text = json.ToString();

            string syntaxError;
            if (!CmmJson.IsWellFormed(text, out syntaxError))
            {
                var invalidPath = ExportPaths.BuildInvalidName(path);
                CmmWriter.WritePlain(invalidPath, text);
                Mod.log.Error("[export] 导出结果不是合法JSON：" + syntaxError);
                Mod.log.Error("[export] 坏文件保留在: " + invalidPath);
                throw new InvalidOperationException("导出结果不是合法JSON " + syntaxError);
            }

            CmmWriter.WriteCompressed(path, text);
            var firstIssue = terrain.IssueCount > 0 ? terrain.FirstIssue
                : transit.IssueCount > 0 ? transit.FirstIssue
                : areas.IssueCount > 0 ? "部分区域缺少完整几何数据"
                : traffic.IssueCount > 0
                    ? (traffic.SegmentCount - traffic.ValidSegmentCount)
                        + " 条道路缺少有效交通统计"
                    : "";
            return new ExportResult(path, now, nodeCount, networkCount, buildingCount,
                transit.StopCount, transit.LineCount,
                terrain.IssueCount + transit.IssueCount + areas.IssueCount + traffic.IssueCount,
                firstIssue);
        }
    }
}
