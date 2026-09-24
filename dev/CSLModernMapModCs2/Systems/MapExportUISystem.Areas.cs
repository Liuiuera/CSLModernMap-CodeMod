using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>采集区域和行政区数据</summary>
    public sealed partial class MapExportUISystem
    {
        private sealed class AreaExportResult
        {
            internal int AreaCount;
            internal int AdministrativeDistrictCount;
            internal int ParkZoneCount;
            internal int FacilityGroupCount;
            internal int OwnerCount;
            internal int NodeCount;
            internal int MissingGeometryCount;
            internal int MissingNodeCount;
            internal int DuplicateReferenceCount;
            internal readonly Dictionary<string, int> KindCounts =
                new Dictionary<string, int>();
            internal readonly StringBuilder FacilityGroupsJson = new StringBuilder();

            internal int IssueCount =>
                MissingGeometryCount > 0 || MissingNodeCount > 0 ? 1 : 0;

            internal string Issues => IssueCount == 0
                ? "[]"
                : "[\"CS2_AREA_GEOMETRY_PARTIAL\"]";
        }

        private sealed class AreaEntry
        {
            internal int Id;
            internal int? OwnerBuildingId;
            internal string Name;
            internal string NameSource;
            internal string Kind;
            internal string ZoneKind;
            internal string SourceRaw;
            internal float2 Position;
            internal float2 Min;
            internal float2 Max;
            internal List<float2> Points;
        }

        private AreaExportResult AppendAreas(StringBuilder json)
        {
            var result = new AreaExportResult();
            var entries = new List<AreaEntry>();
            var seenAreas = new HashSet<int>();
            var owners = new HashSet<int>();
            var nameSystem = GetNameSystem("areas");

            var parentByChild = new Dictionary<int, int>();
            foreach (var pair in m_ExportedBuildingChildren)
                foreach (var child in pair.Value) parentByChild[child] = pair.Key;
            using (var entities = m_BuildingQuery.ToEntityArray(Allocator.Temp))
            {
                var byId = new Dictionary<int, Entity>();
                for (var i = 0; i < entities.Length; i++) byId[entities[i].Index] = entities[i];
                for (var i = 0; i < entities.Length; i++)
                {
                    var sourceOwner = entities[i];
                    var owner = sourceOwner;
                    var visited = new HashSet<int>();
                    int parentId;
                    Entity parent;
                    while (visited.Add(owner.Index)
                        && parentByChild.TryGetValue(owner.Index, out parentId)
                        && byId.TryGetValue(parentId, out parent)) owner = parent;
                    if (!EntityManager.HasComponent<Game.Areas.SubArea>(sourceOwner))
                    {
                        continue;
                    }

                    var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(owner).m_Prefab;
                    var prefabName = GetPrefabName(prefab);
                    var classification = ClassifyBuilding(
                        owner, prefab, prefabName, GetServiceDataRaw(prefab));
                    var zoneKind = ClassifyAreaKind(owner, prefabName, classification);
                    if (string.IsNullOrEmpty(zoneKind))
                    {
                        continue;
                    }

                    var subAreas = EntityManager.GetBuffer<Game.Areas.SubArea>(sourceOwner, true);
                    for (var areaIndex = 0; areaIndex < subAreas.Length; areaIndex++)
                    {
                        var area = subAreas[areaIndex].m_Area;
                        if (area == Entity.Null)
                        {
                            continue;
                        }

                        if (!seenAreas.Add(area.Index))
                        {
                            result.DuplicateReferenceCount++;
                            continue;
                        }

                        if (!EntityManager.HasComponent<Game.Areas.Geometry>(area))
                        {
                            result.MissingGeometryCount++;
                            continue;
                        }

                        if (!EntityManager.HasComponent<Game.Areas.Node>(area))
                        {
                            result.MissingNodeCount++;
                            continue;
                        }

                        var nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                        if (nodes.Length < 3)
                        {
                            result.MissingNodeCount++;
                            continue;
                        }

                        var points = new List<float2>(nodes.Length);
                        for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                        {
                            var position = nodes[nodeIndex].m_Position;
                            points.Add(new float2(position.x, position.z));
                        }

                        var geometry = EntityManager.GetComponentData<Game.Areas.Geometry>(area);
                        string areaName;
                        string nameSource;
                        ResolveAreaName(nameSystem, owner, prefabName, out areaName, out nameSource);
                        entries.Add(new AreaEntry
                        {
                            Id = area.Index + 1,
                            OwnerBuildingId = owner.Index,
                            Name = areaName,
                            NameSource = nameSource,
                            Kind = "PARK_ZONE",
                            ZoneKind = zoneKind,
                            SourceRaw = zoneKind,
                            Position = new float2(
                                geometry.m_CenterPosition.x,
                                geometry.m_CenterPosition.z),
                            Min = PolygonMin(points),
                            Max = PolygonMax(points),
                            Points = points
                        });
                        result.ParkZoneCount++;
                        if (owners.Add(owner.Index))
                        {
                            int kindCount;
                            result.KindCounts.TryGetValue(zoneKind, out kindCount);
                            result.KindCounts[zoneKind] = kindCount + 1;
                        }
                        result.NodeCount += points.Count;
                    }
                }
            }

            CollectAdministrativeDistricts(
                entries, seenAreas, nameSystem, result);

            AppendFacilityGroups(entries, result);

            entries.Sort((left, right) => left.Id.CompareTo(right.Id));
            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    json.AppendLine(",");
                }

                AppendArea(json, entries[i]);
            }
            if (entries.Count > 0)
            {
                json.AppendLine();
            }

            result.AreaCount = entries.Count;
            result.OwnerCount = owners.Count;
            return result;
        }

        private void AppendFacilityGroups(
            List<AreaEntry> entries,
            AreaExportResult result)
        {
            var byOwner = new Dictionary<int, List<AreaEntry>>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Kind != "PARK_ZONE" || !entry.OwnerBuildingId.HasValue)
                {
                    continue;
                }

                List<AreaEntry> owned;
                if (!byOwner.TryGetValue(entry.OwnerBuildingId.Value, out owned))
                {
                    owned = new List<AreaEntry>();
                    byOwner[entry.OwnerBuildingId.Value] = owned;
                }
                owned.Add(entry);
            }

            var ownerIds = new List<int>(byOwner.Keys);
            ownerIds.Sort();
            for (var groupIndex = 0; groupIndex < ownerIds.Count; groupIndex++)
            {
                var ownerId = ownerIds[groupIndex];
                var owned = byOwner[ownerId];
                owned.Sort((left, right) => left.Id.CompareTo(right.Id));
                var primary = owned[0];
                var boundary = primary;
                var largestArea = PolygonArea(primary.Points);
                for (var i = 1; i < owned.Count; i++)
                {
                    var area = PolygonArea(owned[i].Points);
                    if (area > largestArea)
                    {
                        boundary = owned[i];
                        largestArea = area;
                    }
                }

                var named = primary;
                for (var i = 0; i < owned.Count; i++)
                {
                    if (!string.IsNullOrEmpty(owned[i].Name))
                    {
                        named = owned[i];
                        break;
                    }
                }

                var members = CollectFacilityBuildingIds(ownerId, owned);
                if (groupIndex > 0)
                {
                    result.FacilityGroupsJson.AppendLine(",");
                }
                result.FacilityGroupsJson.Append("      {\"id\": ");
                CmmJson.AppendString(result.FacilityGroupsJson,
                    FacilityGroupId(primary.ZoneKind, ownerId));
                result.FacilityGroupsJson.Append(", \"semantic_level\": ");
                CmmJson.AppendString(result.FacilityGroupsJson,
                    primary.ZoneKind == "industry" ? "AREA" : "FACILITY");
                result.FacilityGroupsJson.Append(", \"kind\": ");
                CmmJson.AppendString(result.FacilityGroupsJson,
                    primary.ZoneKind == "campus" ? "university" : primary.ZoneKind);
                result.FacilityGroupsJson.Append(", \"name\": {\"text\": ");
                CmmJson.AppendString(result.FacilityGroupsJson, named.Name);
                result.FacilityGroupsJson.Append(", \"source\": ");
                CmmJson.AppendString(result.FacilityGroupsJson, named.NameSource);
                result.FacilityGroupsJson.Append("}, \"anchor\": [")
                    .Append(FormatFloat(primary.Position.x)).Append(", 0, ")
                    .Append(FormatFloat(primary.Position.y)).Append(']');
                result.FacilityGroupsJson.Append(", \"boundary\": {\"points\": [");
                for (var pointIndex = 0; pointIndex < boundary.Points.Count; pointIndex++)
                {
                    if (pointIndex > 0) result.FacilityGroupsJson.Append(", ");
                    result.FacilityGroupsJson.Append('[')
                        .Append(FormatFloat(boundary.Points[pointIndex].x)).Append(", ")
                        .Append(FormatFloat(boundary.Points[pointIndex].y)).Append(']');
                }
                result.FacilityGroupsJson.Append("]}, \"source_zone_id\": ")
                    .Append(primary.Id.ToString(CultureInfo.InvariantCulture));
                result.FacilityGroupsJson.Append(", \"source_zone_ids\": [");
                for (var z = 0; z < owned.Count; z++)
                {
                    if (z > 0) result.FacilityGroupsJson.Append(", ");
                    result.FacilityGroupsJson.Append(owned[z].Id);
                }
                result.FacilityGroupsJson.Append(']');
                result.FacilityGroupsJson.Append(", \"member_building_ids\": [");
                for (var memberIndex = 0; memberIndex < members.Count; memberIndex++)
                {
                    if (memberIndex > 0) result.FacilityGroupsJson.Append(", ");
                    result.FacilityGroupsJson.Append(
                        members[memberIndex].ToString(CultureInfo.InvariantCulture));
                }
                result.FacilityGroupsJson.Append("], \"root_building_ids\": [");
                if (m_ExportedBuildingIds.Contains(ownerId))
                {
                    result.FacilityGroupsJson.Append(
                        ownerId.ToString(CultureInfo.InvariantCulture));
                }
                result.FacilityGroupsJson.Append("], \"parent_group_id\": \"\"}");
                result.FacilityGroupCount++;
            }
            if (result.FacilityGroupCount > 0)
            {
                result.FacilityGroupsJson.AppendLine();
            }
        }

        private List<int> CollectFacilityBuildingIds(
            int ownerId,
            List<AreaEntry> boundaries)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();
            var pending = new Stack<int>();
            pending.Push(ownerId);
            while (pending.Count > 0)
            {
                var buildingId = pending.Pop();
                if (!seen.Add(buildingId))
                {
                    continue;
                }
                if (m_ExportedBuildingIds.Contains(buildingId))
                {
                    result.Add(buildingId);
                }
                List<int> children;
                if (!m_ExportedBuildingChildren.TryGetValue(buildingId, out children))
                {
                    continue;
                }
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push(children[i]);
                }
            }

            foreach (var pair in m_ExportedBuildingPositions)
            {
                if (seen.Contains(pair.Key))
                {
                    continue;
                }
                for (var boundaryIndex = 0; boundaryIndex < boundaries.Count;
                    boundaryIndex++)
                {
                    if (PointInPolygon(pair.Value, boundaries[boundaryIndex]))
                    {
                        seen.Add(pair.Key);
                        result.Add(pair.Key);
                        break;
                    }
                }
            }
            result.Sort();
            return result;
        }

        private static bool PointInPolygon(float2 point, AreaEntry area)
        {
            if (point.x < area.Min.x || point.x > area.Max.x
                || point.y < area.Min.y || point.y > area.Max.y)
            {
                return false;
            }
            var polygon = area.Points;
            if (polygon == null || polygon.Count < 3)
            {
                return false;
            }
            var inside = false;
            var previous = polygon.Count - 1;
            for (var current = 0; current < polygon.Count; current++)
            {
                var a = polygon[current];
                var b = polygon[previous];
                if ((a.y > point.y) != (b.y > point.y)
                    && point.x < (b.x - a.x) * (point.y - a.y)
                        / ((b.y - a.y) == 0f ? 1e-6f : (b.y - a.y)) + a.x)
                {
                    inside = !inside;
                }
                previous = current;
            }
            return inside;
        }

        private static float2 PolygonMin(List<float2> points)
        {
            var result = new float2(float.MaxValue, float.MaxValue);
            for (var i = 0; i < points.Count; i++)
            {
                result = math.min(result, points[i]);
            }
            return result;
        }

        private static float2 PolygonMax(List<float2> points)
        {
            var result = new float2(float.MinValue, float.MinValue);
            for (var i = 0; i < points.Count; i++)
            {
                result = math.max(result, points[i]);
            }
            return result;
        }

        private static string FacilityGroupId(string zoneKind, int ownerId)
        {
            return (zoneKind == "industry" ? "area:building:" : "facility:building:")
                + ownerId.ToString(CultureInfo.InvariantCulture);
        }

        private static double PolygonArea(List<float2> points)
        {
            if (points == null || points.Count < 3)
            {
                return 0.0;
            }
            double area = 0.0;
            for (var i = 0; i < points.Count; i++)
            {
                var next = points[(i + 1) % points.Count];
                area += (double)points[i].x * next.y - (double)next.x * points[i].y;
            }
            return Math.Abs(area) * 0.5;
        }

        private void CollectAdministrativeDistricts(
            List<AreaEntry> entries,
            HashSet<int> seenAreas,
            Game.UI.NameSystem nameSystem,
            AreaExportResult result)
        {
            using (var entities = m_DistrictQuery.ToEntityArray(Allocator.Temp))
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var area = entities[i];
                    if (!seenAreas.Add(area.Index))
                    {
                        result.DuplicateReferenceCount++;
                        continue;
                    }

                    if (!EntityManager.HasComponent<Game.Areas.Geometry>(area))
                    {
                        result.MissingGeometryCount++;
                        continue;
                    }
                    if (!EntityManager.HasComponent<Game.Areas.Node>(area))
                    {
                        result.MissingNodeCount++;
                        continue;
                    }

                    var nodes = EntityManager.GetBuffer<Game.Areas.Node>(area, true);
                    if (nodes.Length < 3)
                    {
                        result.MissingNodeCount++;
                        continue;
                    }

                    var points = new List<float2>(nodes.Length);
                    for (var nodeIndex = 0; nodeIndex < nodes.Length; nodeIndex++)
                    {
                        var position = nodes[nodeIndex].m_Position;
                        points.Add(new float2(position.x, position.z));
                    }

                    var geometry = EntityManager.GetComponentData<Game.Areas.Geometry>(area);
                    string districtName;
                    string nameSource;
                    ResolveDistrictName(
                        nameSystem, area, out districtName, out nameSource);
                    entries.Add(new AreaEntry
                    {
                        Id = area.Index + 1,
                        OwnerBuildingId = null,
                        Name = districtName,
                        NameSource = nameSource,
                        Kind = "DISTRICT",
                        ZoneKind = "OTHER",
                        SourceRaw = "",
                        Position = new float2(
                            geometry.m_CenterPosition.x,
                            geometry.m_CenterPosition.z),
                        Min = PolygonMin(points),
                        Max = PolygonMax(points),
                        Points = points
                    });
                    result.AdministrativeDistrictCount++;
                    result.NodeCount += points.Count;
                }
            }
        }

        private static void ResolveDistrictName(
            Game.UI.NameSystem nameSystem,
            Entity district,
            out string name,
            out string source)
        {
            name = "";
            source = "empty";
            if (nameSystem == null)
            {
                return;
            }

            try
            {
                name = nameSystem.GetRenderedLabelName(district) ?? "";
                if (name.Length == 0)
                {
                    return;
                }

                string customName;
                source = nameSystem.TryGetCustomName(district, out customName)
                    && !string.IsNullOrEmpty(customName)
                    ? "custom"
                    : "game";
            }
            catch (Exception e)
            {
                name = "";
                source = "empty";
                Mod.log.Warn("[areas] 行政区名称解析失败: "
                    + e.GetType().Name + ": " + e.Message);
            }
        }

        private string ClassifyAreaKind(
            Entity owner,
            string prefabName,
            (string Role, string Service, string SubService) classification)
        {
            if (EntityManager.HasComponent<Game.Buildings.TransportStation>(owner))
            {
                var station = EntityManager.GetComponentData<Game.Buildings.TransportStation>(owner);
                if (HasAnyValue(station.m_AircraftRefuelTypes))
                {
                    return "airport";
                }
                if (HasAnyValue(station.m_WatercraftRefuelTypes))
                {
                    return EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(owner)
                        ? "cargo_harbor"
                        : "ship";
                }
            }

            if (classification.SubService == "PublicTransportPlane")
            {
                return "airport";
            }
            if (classification.SubService == "PublicTransportShip")
            {
                return EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(owner)
                    ? "cargo_harbor"
                    : "ship";
            }
            if (classification.Role == "EDUCATION")
            {
                var prefab = GetPrefabEntity(owner);
                if (EntityManager.HasComponent<Game.Prefabs.SchoolData>(prefab)
                    && EntityManager.GetComponentData<Game.Prefabs.SchoolData>(prefab).m_EducationLevel == 4)
                    return "campus";
                return "";
            }
            if (classification.Role == "INDUSTRIAL"
                || classification.Service == "Industrial"
                || classification.Service == "PlayerIndustry")
            {
                return "industry";
            }
            if (classification.Role == "PARK_SERVICE" || classification.Role == "LEISURE")
            {
                return "park";
            }

            return "";
        }

        private static bool HasAnyValue<T>(T value)
        {
            return !EqualityComparer<T>.Default.Equals(value, default(T));
        }

        private static void ResolveAreaName(
            Game.UI.NameSystem nameSystem,
            Entity owner,
            string prefabName,
            out string name,
            out string source)
        {
            name = "";
            source = "empty";
            if (nameSystem != null)
            {
                try
                {
                    name = nameSystem.GetRenderedLabelName(owner) ?? "";
                    if (name.Length > 0)
                    {
                        source = "game";
                        return;
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn("[areas] 设施名解析失败: " + e.GetType().Name + ": " + e.Message);
                }
            }

            name = prefabName ?? "";
            source = name.Length > 0 ? "asset" : "empty";
        }

        private static void AppendArea(StringBuilder json, AreaEntry entry)
        {
            json.Append("    {\"id\": ").Append(entry.Id.ToString(CultureInfo.InvariantCulture));
            json.Append(", \"name\": {\"text\": ");
            CmmJson.AppendString(json, entry.Name);
            json.Append(", \"source\": ");
            CmmJson.AppendString(json, entry.NameSource);
            json.Append("}, \"kind\": ");
            CmmJson.AppendString(json, entry.Kind);
            json.Append(", \"zone_kind\": ");
            CmmJson.AppendString(json, entry.ZoneKind);
            json.Append(", \"source_raw\": ");
            CmmJson.AppendString(json, entry.SourceRaw);
            json.Append(", \"grid_points\": [], \"grid_cell\": 0, \"boundary\": {\"points\": [");
            for (var i = 0; i < entry.Points.Count; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append('[').Append(FormatFloat(entry.Points[i].x)).Append(", ")
                    .Append(FormatFloat(entry.Points[i].y)).Append(']');
            }
            json.Append("]}, \"position\": [")
                .Append(FormatFloat(entry.Position.x)).Append(", ")
                .Append(FormatFloat(entry.Position.y)).Append(']');
            json.Append(", \"owner_building_id\": ")
                .Append(entry.OwnerBuildingId.HasValue
                    ? entry.OwnerBuildingId.Value.ToString(CultureInfo.InvariantCulture)
                    : "null")
                .Append('}');
        }
    }
}
