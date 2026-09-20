using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using UnityEngine;

namespace CSLModernMap.Systems
{
    public sealed partial class MapExportUISystem
    {
        private ExportResult WriteCmmExport()
        {
            var outputDirectory = ExportPaths.EnsureExportDirectory();

            var now = DateTime.Now;
            var path = Path.Combine(outputDirectory, ExportPaths.BuildFileName(now));

            var exportedNodes = new HashSet<Entity>();
            var exportedNetworks = new HashSet<Entity>();
            var laneToNetworkId = new Dictionary<Entity, int>();

            var nodeJson = new StringBuilder(2 * 1024 * 1024);
            var networkJson = new StringBuilder(12 * 1024 * 1024);
            var buildingJson = new StringBuilder(12 * 1024 * 1024);
            var nodeCount = AppendNodes(nodeJson, exportedNodes);
            var networkCount = AppendNetworks(
                networkJson, exportedNodes, exportedNetworks, laneToNetworkId);
            var buildingCount = AppendBuildings(buildingJson);

            // Terrain and water are resampled to one output grid.
            var terrain = TerrainExport.Build(World);

            var transit = TransitExport.Build(
                World,
                EntityManager,
                GetPrefabName,
                exportedNodes,
                exportedNetworks,
                laneToNetworkId);

            var json = new StringBuilder(
                nodeJson.Length + networkJson.Length + buildingJson.Length
                + terrain.Json.Length + 4096);
            json.AppendLine("{");
            json.AppendLine("  \"schema_version\": \"cmm-v1\",");
            json.AppendLine("  \"metadata\": {");
            json.Append("    \"origin\": {\"game\": \"cs2\", \"exporter\": \"CSLModernMapCs2\"");
            CmmJson.AppendProperty(json, "source_name", ReadCityName());
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
                .Append(", \"building_roles\": ").Append(CmmJson.BuildCounters(m_RoleCounts))
                .Append(", \"building_services\": ").Append(CmmJson.BuildCounters(m_ServiceCounts))
                .Append(", \"building_sub_services\": ").Append(CmmJson.BuildCounters(m_SubServiceCounts))
                .Append(", \"building_links\": {\"attached\": ").Append(m_AttachedLinkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"installed_upgrade\": ").Append(m_UpgradeLinkCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"extension_entities\": ").Append(m_ExtensionCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"upgrade_entities\": ").Append(m_UpgradeCount.ToString(CultureInfo.InvariantCulture))
                .Append("}, \"stops\": ").Append(transit.StopCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"transit_lines\": ").Append(transit.LineCount.ToString(CultureInfo.InvariantCulture))
                .Append(", \"routes\": ").Append(transit.RouteCount.ToString(CultureInfo.InvariantCulture))
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
            json.AppendLine("  \"districts\": [],");
            json.AppendLine("  \"stops\": [");
            json.Append(transit.StopsJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"transit_lines\": [");
            json.Append(transit.LinesJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"routes\": [");
            json.Append(transit.RoutesJson);
            json.AppendLine("  ],");
            json.AppendLine("  \"derived\": {},");
            json.AppendLine("  \"forests\": [],");
            json.AppendLine("  \"procedural_objects\": [],");
            json.Append("  \"issues\": ").Append(CombineIssueArrays(terrain.Issues, transit.Issues));
            json.AppendLine(",");
            json.Append("  \"extensions\": {\"cs2\": {\"game_version\": ");
            CmmJson.AppendString(json, Application.version);
            json.Append(", \"world\": ");
            CmmJson.AppendString(json, World.Name);
            json.Append(", \"cargo_routes\": ")
                .Append(transit.CargoRouteCount.ToString(CultureInfo.InvariantCulture));
            // 线路实体的prefab资产名；line.source_raw是渲染端类型关键字，放不下资产名，故单独放这里。
            if (!string.IsNullOrEmpty(transit.LinePrefabsJson))
            {
                json.Append(", \"transit_line_prefabs\": ").Append(transit.LinePrefabsJson);
            }

            json.AppendLine("}}");
            json.AppendLine("}");

            var text = json.ToString();

            // Never publish malformed JSON; keep the plain text for diagnosis.
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
            return new ExportResult(path, now, nodeCount, networkCount, buildingCount,
                transit.StopCount, transit.LineCount,
                terrain.IssueCount + transit.IssueCount);
        }
    }
}
