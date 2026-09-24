using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.City;
using Game.UI.Localization;
using Game.UI.Menu;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

namespace CSLModernMap.Systems
{
    /// <summary>采集建筑及其用途数据</summary>
    public sealed partial class MapExportUISystem
    {
        private int AppendBuildings(StringBuilder json)
        {
            m_RoleCounts.Clear();
            m_ServiceCounts.Clear();
            m_SubServiceCounts.Clear();
            m_ExcludedNonBuildingCount = 0;
            m_ExcludedNoGeometryCount = 0;
            m_CustomBuildingNameCount = 0;
            m_ExtensionCount = 0;
            m_UpgradeCount = 0;
            m_AttachedLinkCount = 0;
            m_UpgradeLinkCount = 0;
            m_ExportedBuildingIds.Clear();
            m_ExportedBuildingChildren.Clear();
            m_ExportedBuildingPositions.Clear();

            var entities = CollectBuildingEntities();
            var nameSystem = GetNameSystem("buildings");
            m_LotSizeCache = new NativeHashMap<Entity, LotSizeEntry>(256, Allocator.Temp);
            m_PrefabNameCache.Clear();
            try
            {
                var included = new HashSet<int>();
                var lotByEntity = new Dictionary<int, LotSizeEntry>();
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    string excludedName;
                    if (IsNonBuildingEntity(entity, out excludedName))
                    {
                        m_ExcludedNonBuildingCount++;
                        continue;
                    }

                    var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                    LotSizeEntry lot;
                    if (!TryGetLotSize(prefab, out lot))
                    {
                        m_ExcludedNoGeometryCount++;
                        continue;
                    }

                    included.Add(entity.Index);
                    m_ExportedBuildingIds.Add(entity.Index);
                    lotByEntity.Add(entity.Index, lot);
                }

                var childrenByParent = new Dictionary<int, List<int>>();
                var parentByChild = new Dictionary<int, int>();
                foreach (var pair in m_StructuralParents)
                    if (included.Contains(pair.Key) && included.Contains(pair.Value))
                        AddChildLink(childrenByParent, parentByChild, pair.Value, pair.Key);
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!included.Contains(entity.Index))
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Game.Objects.SubObject>(entity))
                    {
                        var objects = EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                        for (var o = 0; o < objects.Length; o++)
                        {
                            var child = objects[o].m_SubObject;
                            if (child != Entity.Null && included.Contains(child.Index))
                                AddChildLink(childrenByParent, parentByChild, entity.Index, child.Index);
                        }
                    }
                    if (EntityManager.HasComponent<Game.Common.Owner>(entity))
                    {
                        var parent = EntityManager.GetComponentData<Game.Common.Owner>(entity).m_Owner;
                        if (parent != Entity.Null && included.Contains(parent.Index))
                            AddChildLink(childrenByParent, parentByChild, parent.Index, entity.Index);
                    }
                    if (EntityManager.HasComponent<Game.Buildings.Extension>(entity))
                    {
                        m_ExtensionCount++;
                    }

                    if (EntityManager.HasComponent<Game.Buildings.ServiceUpgrade>(entity))
                    {
                        m_UpgradeCount++;
                    }

                    if (EntityManager.HasComponent<Game.Buildings.InstalledUpgrade>(entity))
                    {
                        var upgrades = EntityManager.GetBuffer<Game.Buildings.InstalledUpgrade>(entity, true);
                        for (var u = 0; u < upgrades.Length; u++)
                        {
                            var child = upgrades[u].m_Upgrade;
                            if (child == Entity.Null || !included.Contains(child.Index))
                            {
                                continue;
                            }

                            if (AddChildLink(childrenByParent, parentByChild, entity.Index, child.Index))
                            {
                                m_UpgradeLinkCount++;
                            }
                        }
                    }

                    if (EntityManager.HasComponent<Game.Objects.Attached>(entity))
                    {
                        var parent = EntityManager.GetComponentData<Game.Objects.Attached>(entity).m_Parent;
                        if (parent != Entity.Null && included.Contains(parent.Index)
                            && AddChildLink(childrenByParent, parentByChild, parent.Index, entity.Index))
                        {
                            m_AttachedLinkCount++;
                        }
                    }
                }

                var structures = CollectBuildingStructures(
                    entities, included, childrenByParent, parentByChild);
                foreach (var children in childrenByParent.Values)
                {
                    children.Sort();
                }
                foreach (var pair in childrenByParent)
                {
                    m_ExportedBuildingChildren[pair.Key] = new List<int>(pair.Value);
                }

                var written = 0;
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!included.Contains(entity.Index))
                    {
                        continue;
                    }

                    var transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                    m_ExportedBuildingPositions[entity.Index] = new float2(
                        transform.m_Position.x, transform.m_Position.z);
                    var prefab = EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                    var lotMeters = lotByEntity[entity.Index].Size;
                    var prefabName = GetPrefabName(prefab);
                    bool hasCustomName;
                    var displayName = GetRenderedDisplayName(
                        nameSystem, entity, "buildings", out hasCustomName);
                    var serviceDataRaw = GetServiceDataRaw(prefab);
                    var classification = ClassifyBuilding(entity, prefab, prefabName, serviceDataRaw);

                    int parentId;
                    var hasParent = parentByChild.TryGetValue(entity.Index, out parentId);
                    List<int> childIds;
                    var hasChildren = childrenByParent.TryGetValue(entity.Index, out childIds);

                    if (written > 0)
                    {
                        json.AppendLine(",");
                    }

                    json.Append("    {\"id\": ").Append(entity.Index);
                    json.Append(", \"name\": ");
                    CmmJson.AppendString(json, prefabName);
                    json.Append(", \"display_name\": {\"text\": ");
                    CmmJson.AppendString(json, displayName);
                    json.Append(", \"source\": ");
                    CmmJson.AppendString(json, string.IsNullOrEmpty(displayName)
                        ? "empty"
                        : (hasCustomName ? "custom" : "game"));
                    json.Append('}');
                    json.Append(", \"role\": ");
                    CmmJson.AppendString(json, classification.Role);
                    json.Append(", \"footprint\": ");
                    AppendBuildingFootprint(json, transform, lotMeters);
                    CmmJson.AppendProperty(json, "geometry_source",
                        structures.ContainsKey(entity.Index) ? "cs2:structure-container" : "cs2:lot-fallback");

                    json.Append(", \"parent_id\": ");
                    CmmJson.AppendOptionalInt(json, hasParent, parentId);
                    json.Append(", \"child_ids\": [");
                    if (hasChildren)
                    {
                        for (var c = 0; c < childIds.Count; c++)
                        {
                            if (c > 0)
                            {
                                json.Append(", ");
                            }

                            json.Append(childIds[c].ToString(CultureInfo.InvariantCulture));
                        }
                    }

                    json.Append(']');
                    json.Append(", \"station_id\": null, \"build_index\": null");
                    json.Append(", \"source_raw\": ");
                    CmmJson.AppendString(json, prefabName);
                    json.Append(", \"service\": ");
                    CmmJson.AppendString(json, classification.Service);
                    json.Append(", \"sub_service\": ");
                    CmmJson.AppendString(json, classification.SubService);
                    json.Append(", \"icls\": ");
                    CmmJson.AppendString(json, prefabName);

                    json.Append(", \"sub_building_id\": ");
                    CmmJson.AppendOptionalInt(json, hasChildren, hasChildren ? childIds[0] : 0);
                    json.Append(", \"parent_building_id\": ");
                    CmmJson.AppendOptionalInt(json, hasParent, parentId);
                    json.Append('}');
                    written++;
                    List<BuildingStructure> parts;
                    if (structures.TryGetValue(entity.Index, out parts))
                    {
                        foreach (var part in parts)
                        {
                            json.AppendLine(",");
                            AppendBuildingStructure(json, part, entity.Index, prefabName, classification);
                            written++;
                        }
                    }
                    if (hasCustomName)
                    {
                        m_CustomBuildingNameCount++;
                    }

                    int roleCount;
                    m_RoleCounts.TryGetValue(classification.Role, out roleCount);
                    m_RoleCounts[classification.Role] = roleCount + 1;

                    int serviceCount;
                    m_ServiceCounts.TryGetValue(classification.Service, out serviceCount);
                    m_ServiceCounts[classification.Service] = serviceCount + 1;

                    int subServiceCount;
                    m_SubServiceCounts.TryGetValue(classification.SubService, out subServiceCount);
                    m_SubServiceCounts[classification.SubService] = subServiceCount + 1;
                }

                if (written > 0)
                {
                    json.AppendLine();
                }

                return written;
            }
            finally
            {
                m_LotSizeCache.Dispose();
            }
        }

        private bool TryGetLotSize(Entity prefab, out LotSizeEntry entry)
        {
            if (prefab == Entity.Null)
            {
                entry = default;
                return false;
            }

            LotSizeEntry cached;
            if (m_LotSizeCache.TryGetValue(prefab, out cached))
            {
                entry = cached;
                return true;
            }

            entry = default;

            if (EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab))
            {
                var lot = EntityManager.GetComponentData<Game.Prefabs.BuildingData>(prefab).m_LotSize;
                if (lot.x > 0 && lot.y > 0)
                {
                    entry = new LotSizeEntry(new float2(lot.x * CellSizeMeters, lot.y * CellSizeMeters));
                    m_LotSizeCache[prefab] = entry;
                    return true;
                }
            }

            if (EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefab))
            {
                var lot = EntityManager.GetComponentData<Game.Prefabs.BuildingExtensionData>(prefab).m_LotSize;
                if (lot.x > 0 && lot.y > 0)
                {
                    entry = new LotSizeEntry(new float2(lot.x * CellSizeMeters, lot.y * CellSizeMeters));
                    m_LotSizeCache[prefab] = entry;
                    return true;
                }
            }

            if (EntityManager.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab))
            {
                var mesh = EntityManager.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab).m_Size;
                if (mesh.x > 0.1f && mesh.z > 0.1f)
                {
                    entry = new LotSizeEntry(new float2(mesh.x, mesh.z));
                    m_LotSizeCache[prefab] = entry;
                    return true;
                }
            }

            return false;
        }

        private readonly struct LotSizeEntry
        {
            public LotSizeEntry(float2 size)
            {
                Size = size;
            }

            public float2 Size { get; }
        }

        private static void AppendBuildingFootprint(
            StringBuilder json,
            Game.Objects.Transform transform,
            float2 lotMeters)
        {
            var halfX = lotMeters.x * 0.5f;
            var halfZ = lotMeters.y * 0.5f;

            json.Append("{\"points\": [");
            AppendBuildingCorner(json, transform, -halfX, -halfZ, 0);
            AppendBuildingCorner(json, transform, halfX, -halfZ, 1);
            AppendBuildingCorner(json, transform, halfX, halfZ, 2);
            AppendBuildingCorner(json, transform, -halfX, halfZ, 3);
            json.Append("]}");
        }

        private static void AppendBuildingCorner(
            StringBuilder json,
            Game.Objects.Transform transform,
            float offsetX,
            float offsetZ,
            int index)
        {
            if (index > 0)
            {
                json.Append(", ");
            }

            var world = transform.m_Position + math.rotate(transform.m_Rotation, new float3(offsetX, 0f, offsetZ));
            json.Append("{\"x\": ").Append(FormatFloat(world.x))
                .Append(", \"z\": ").Append(FormatFloat(world.z)).Append('}');
        }

        private (string Role, string Service, string SubService) ClassifyBuilding(
            Entity entity, Entity prefab, string prefabName, string serviceDataRaw)
        {
            var zone = ClassifyByZone(prefab);
            if (zone.Role != null)
            {
                return zone;
            }

            if (EntityManager.HasComponent<Game.Buildings.TransportStation>(entity)
                || EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(entity)
                || EntityManager.HasComponent<Game.Buildings.TransportDepot>(entity))
            {
                return ("TRANSIT_STATION", "PublicTransport", TransportSubService(prefabName));
            }

            if (EntityManager.HasComponent<Game.Buildings.Hospital>(entity)
                || EntityManager.HasComponent<Game.Buildings.DeathcareFacility>(entity)
                || EntityManager.HasComponent<Game.Buildings.WelfareOffice>(entity))
            {
                return ("HEALTHCARE", "HealthCare", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.School>(entity)
                || EntityManager.HasComponent<Game.Buildings.ResearchFacility>(entity))
            {
                return ("EDUCATION", "Education", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.FireStation>(entity)
                || EntityManager.HasComponent<Game.Buildings.FirewatchTower>(entity)
                || EntityManager.HasComponent<Game.Buildings.DisasterFacility>(entity)
                || EntityManager.HasComponent<Game.Buildings.EmergencyShelter>(entity))
            {
                return ("EMERGENCY", "FireDepartment", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.PoliceStation>(entity)
                || EntityManager.HasComponent<Game.Buildings.Prison>(entity))
            {
                return ("EMERGENCY", "PoliceDepartment", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.Park>(entity)
                || EntityManager.HasComponent<Game.Buildings.ParkMaintenance>(entity))
            {
                return ("PARK_SERVICE", "Beautification", "BeautificationParks");
            }

            if (EntityManager.HasComponent<Game.Buildings.LeisureProvider>(entity))
            {
                return ("LEISURE", "Beautification", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.Transformer>(entity)
                || EntityManager.HasComponent<Game.Buildings.ElectricityProducer>(entity)
                || EntityManager.HasComponent<Game.Buildings.Battery>(entity))
            {
                return ("INFRASTRUCTURE", "Electricity", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.WaterPumpingStation>(entity)
                || EntityManager.HasComponent<Game.Buildings.WaterTower>(entity)
                || EntityManager.HasComponent<Game.Buildings.WastewaterTreatmentPlant>(entity)
                || EntityManager.HasComponent<Game.Buildings.SewageOutlet>(entity))
            {
                return ("INFRASTRUCTURE", "Water", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.GarbageFacility>(entity))
            {
                return ("INFRASTRUCTURE", "Garbage", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.PostFacility>(entity)
                || EntityManager.HasComponent<Game.Buildings.TelecomFacility>(entity)
                || EntityManager.HasComponent<Game.Buildings.MaintenanceDepot>(entity)
                || EntityManager.HasComponent<Game.Buildings.AdminBuilding>(entity)
                || EntityManager.HasComponent<Game.Buildings.CarParkingFacility>(entity)
                || EntityManager.HasComponent<Game.Buildings.BicycleParkingFacility>(entity))
            {
                return ("INFRASTRUCTURE", "None", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.ResidentialProperty>(entity))
            {
                return ("RESIDENTIAL", "Residential", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.CommercialProperty>(entity))
            {
                return ("COMMERCIAL", "Commercial", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.OfficeProperty>(entity))
            {
                return ("OFFICE", "Office", "");
            }

            if (EntityManager.HasComponent<Game.Buildings.ExtractorProperty>(entity)
                || IsIndustryAreaPlaceholder(prefabName))
            {
                var extract = ExtractClassification(prefabName);
                return ("INDUSTRIAL", extract.Service, extract.Sub);
            }

            if (EntityManager.HasComponent<Game.Buildings.IndustrialProperty>(entity)
                || EntityManager.HasComponent<Game.Buildings.StorageProperty>(entity))
            {
                return ("INDUSTRIAL", "Industrial", "");
            }

            if (HasToken(prefabName, "extractor"))
            {
                var extract = ExtractClassification(prefabName);
                return ("INDUSTRIAL", extract.Service, extract.Sub);
            }

            var keywordRole = MatchRoleByKeywords(prefabName, serviceDataRaw);
            if (keywordRole != null)
            {
                return RoleToFields(keywordRole, prefabName);
            }

            return ("OTHER", "None", "");
        }

        private static readonly (string Term, string Sub)[] TransportSubServiceTerms =
        {
            ("metro", "PublicTransportMetro"),
            ("subway", "PublicTransportMetro"),
            ("monorail", "PublicTransportMonorail"),
            ("tram", "PublicTransportTram"),
            ("trolley", "PublicTransportTrolleybus"),
            ("cable", "PublicTransportCableCar"),
            ("train", "PublicTransportTrain"),
            ("rail", "PublicTransportTrain"),
            ("bus", "PublicTransportBus"),
            ("taxi", "PublicTransportTaxi"),
            ("harbor", "PublicTransportShip"),
            ("harbour", "PublicTransportShip"),
            ("ship", "PublicTransportShip"),
            ("ferry", "PublicTransportShip"),
            ("airport", "PublicTransportPlane"),
            ("plane", "PublicTransportPlane"),
            ("post", "PublicTransportPost"),
            ("mail", "PublicTransportPost"),
        };

        private static readonly (string Role, string[] Terms)[] RoleTerms =
        {
            ("AIRPORT", new[] { "airport", "airplane", "blimp" }),
            ("TRANSIT_STATION", new[] { "publictransport", "station", "metro", "train", "tram", "monorail" }),
            ("EDUCATION", new[] { "school", "university", "education" }),
            ("HEALTHCARE", new[] { "hospital", "healthcare", "clinic", "cemetery" }),
            ("EMERGENCY", new[] { "police", "fire", "emergency" }),
            ("PARK_SERVICE", new[] { "park", "zoo", "amusement" }),
            ("RESIDENTIAL", new[] { "residential", "house", "apartment" }),
            ("COMMERCIAL", new[] { "commercial", "shop" }),
            ("INDUSTRIAL", new[] { "industrial", "industry", "factory", "warehouse" }),
            ("OFFICE", new[] { "office" }),
            ("INFRASTRUCTURE", new[] { "power", "water", "sewage", "sewer", "garbage", "transport", "wastewater" }),
            ("LEISURE", new[] { "leisure", "stadium" }),
        };

        private (string Role, string Service, string SubService) ClassifyByZone(Entity prefab)
        {
            var zone = ReadZone(prefab);
            if (!zone.HasValue)
            {
                return (null, "", "");
            }

            switch (zone.Value.m_AreaType)
            {
                case Game.Zones.AreaType.Residential:
                    return ("RESIDENTIAL", "Residential", "");
                case Game.Zones.AreaType.Commercial:
                    return ("COMMERCIAL", "Commercial", "");
                case Game.Zones.AreaType.Industrial:
                    return zone.Value.m_ZoneFlags.HasFlag(Game.Prefabs.ZoneFlags.Office)
                        ? ("OFFICE", "Office", "")
                        : ("INDUSTRIAL", "Industrial", "");
                default:
                    return (null, "", "");
            }
        }

        private Game.Prefabs.ZoneData? ReadZone(Entity prefab)
        {
            if (prefab == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.SpawnableBuildingData>(prefab))
            {
                return null;
            }

            var zonePrefab = EntityManager.GetComponentData<Game.Prefabs.SpawnableBuildingData>(prefab).m_ZonePrefab;
            if (zonePrefab == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.ZoneData>(zonePrefab))
            {
                return null;
            }

            return EntityManager.GetComponentData<Game.Prefabs.ZoneData>(zonePrefab);
        }

        private static string TransportSubService(string prefabName)
        {
            foreach (var (term, sub) in TransportSubServiceTerms)
            {
                if (HasToken(prefabName, term))
                {
                    return sub;
                }
            }

            return "";
        }

        private static (string Service, string Sub) ExtractClassification(string prefabName)
        {
            foreach (var token in Tokens(prefabName))
            {
                if (StartsWith(token, "fish") || StartsWith(token, "aquacultur"))
                {
                    return ("Fishing", "");
                }

                if (StartsWith(token, "farm") || StartsWith(token, "agricultur")
                    || StartsWith(token, "greenhouse") || StartsWith(token, "livestock")
                    || StartsWith(token, "crop") || StartsWith(token, "grain")
                    || StartsWith(token, "vegetable") || StartsWith(token, "cotton"))
                {
                    return ("PlayerIndustry", "PlayerIndustryFarming");
                }

                if (StartsWith(token, "forest") || StartsWith(token, "timber")
                    || StartsWith(token, "logging"))
                {
                    return ("PlayerIndustry", "PlayerIndustryForestry");
                }

                if (StartsWith(token, "ore") || StartsWith(token, "mine") || StartsWith(token, "mining")
                    || StartsWith(token, "coal") || StartsWith(token, "stone"))
                {
                    return ("PlayerIndustry", "PlayerIndustryOre");
                }

                if (StartsWith(token, "oil") || StartsWith(token, "petrol")
                    || StartsWith(token, "drill") || StartsWith(token, "pump"))
                {
                    return ("PlayerIndustry", "PlayerIndustryOil");
                }
            }

            return ("PlayerIndustry", "");
        }

        private static bool IsIndustryAreaPlaceholder(string prefabName)
        {
            return HasToken(prefabName, "placeholder") && HasToken(prefabName, "area");
        }

        private bool IsNonBuildingEntity(Entity entity, out string prefabName)
        {
            prefabName = GetPrefabName(
                EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab);
            return EntityManager.HasComponent<Game.Buildings.TrafficSpawner>(entity);
        }

        private static string MatchRoleByKeywords(string prefabName, string serviceDataRaw)
        {
            foreach (var (role, terms) in RoleTerms)
            {
                foreach (var term in terms)
                {
                    if (HasToken(prefabName, term) || HasToken(serviceDataRaw, term))
                    {
                        return role;
                    }
                }
            }

            return null;
        }

        private static (string Role, string Service, string SubService) RoleToFields(string role, string prefabName)
        {
            switch (role)
            {
                case "AIRPORT":
                    return ("AIRPORT", "PublicTransport", "PublicTransportPlane");
                case "TRANSIT_STATION":
                    return ("TRANSIT_STATION", "PublicTransport", TransportSubService(prefabName));
                case "EDUCATION":
                    return ("EDUCATION", "Education", "");
                case "HEALTHCARE":
                    return ("HEALTHCARE", "HealthCare", "");
                case "EMERGENCY":
                    return HasToken(prefabName, "police")
                        ? ("EMERGENCY", "PoliceDepartment", "")
                        : ("EMERGENCY", "FireDepartment", "");
                case "PARK_SERVICE":
                    return ("PARK_SERVICE", "Beautification", "BeautificationParks");
                case "RESIDENTIAL":
                    return ("RESIDENTIAL", "Residential", "");
                case "COMMERCIAL":
                    return ("COMMERCIAL", "Commercial", "");
                case "INDUSTRIAL":
                    return ("INDUSTRIAL", "Industrial", "");
                case "OFFICE":
                    return ("OFFICE", "Office", "");
                case "LEISURE":
                    return ("LEISURE", "Beautification", "");
                default:
                    if (HasToken(prefabName, "power"))
                    {
                        return ("INFRASTRUCTURE", "Electricity", "");
                    }

                    if (HasToken(prefabName, "water") || HasToken(prefabName, "wastewater")
                        || HasToken(prefabName, "sewage") || HasToken(prefabName, "sewer"))
                    {
                        return ("INFRASTRUCTURE", "Water", "");
                    }

                    if (HasToken(prefabName, "garbage"))
                    {
                        return ("INFRASTRUCTURE", "Garbage", "");
                    }

                    return ("INFRASTRUCTURE", "None", "");
            }
        }

        private static List<string> Tokens(string name)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(name))
            {
                return tokens;
            }

            var current = new StringBuilder(16);
            for (var i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                if (!char.IsLetterOrDigit(ch))
                {
                    FlushToken(tokens, current);
                    continue;
                }

                if (char.IsUpper(ch) && current.Length > 0)
                {
                    FlushToken(tokens, current);
                }

                current.Append(char.ToLowerInvariant(ch));
            }

            FlushToken(tokens, current);
            return tokens;
        }

        private static void FlushToken(List<string> tokens, StringBuilder current)
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        private static bool StartsWith(string token, string term)
        {
            return token.StartsWith(term, StringComparison.Ordinal);
        }

        private static bool HasToken(string name, string term)
        {
            foreach (var token in Tokens(name))
            {
                if (StartsWith(token, term))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetServiceDataRaw(Entity prefab)
        {
            if (prefab == Entity.Null
                || !EntityManager.HasComponent<Game.Prefabs.ServiceData>(prefab))
            {
                return "";
            }

            return EntityManager.GetComponentData<Game.Prefabs.ServiceData>(prefab).m_Service.ToString();
        }

        private static bool AddChildLink(
            Dictionary<int, List<int>> childrenByParent,
            Dictionary<int, int> parentByChild,
            int parentId,
            int childId)
        {
            if (parentId == childId || parentByChild.ContainsKey(childId))
            {
                return false;
            }

            parentByChild[childId] = parentId;
            List<int> children;
            if (!childrenByParent.TryGetValue(parentId, out children))
            {
                children = new List<int>();
                childrenByParent[parentId] = children;
            }

            children.Add(childId);

            return true;
        }
    }
}
