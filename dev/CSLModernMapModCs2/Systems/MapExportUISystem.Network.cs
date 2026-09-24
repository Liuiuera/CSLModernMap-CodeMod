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
    /// <summary>采集道路和网络数据</summary>
    public sealed partial class MapExportUISystem
    {
        private int AppendNodes(StringBuilder json, HashSet<Entity> exportedNodes)
        {
            var entities = m_NodeQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var node = EntityManager.GetComponentData<Game.Net.Node>(entity);
                    exportedNodes.Add(entity);
                    if (i > 0)
                    {
                        json.AppendLine(",");
                    }

                    json.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"pos\": ");
                    AppendVec3(json, node.m_Position);
                    json.Append(", \"mode_hint\": ");
                    CmmJson.AppendString(json, GetNodeModeHint(entity));
                    json.Append(", \"station_service\": null, \"source_raw\": \"\"}");
                }

                if (entities.Length > 0)
                {
                    json.AppendLine();
                }

                return entities.Length;
            }
            finally
            {
                entities.Dispose();
            }
        }

        private int AppendNetworks(
            StringBuilder json,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            Cs2DerivedNetworkState derived,
            TrafficExport traffic)
        {
            m_NetCompositionCache.Clear();
            m_RoadSizeClassCache.Clear();
            m_NetworkKindCounts.Clear();
            m_SkippedAirspaceNetworkCount = 0;
            m_SkippedMarkerNetworkCount = 0;
            m_CustomRoadNameCount = 0;

            var entities = m_EdgeQuery.ToEntityArray(Allocator.Temp);
            var geometryContext = Cs2Geometry.BuildRoadGeometryContext(EntityManager);
            var nameSystem = GetNameSystem("networks");
            var roadNameCache = new Dictionary<Entity, string>();
            var written = 0;
            try
            {
                var sorted = new List<Entity>(entities.Length);
                for (var i = 0; i < entities.Length; i++)
                {
                    sorted.Add(entities[i]);
                }

                sorted.Sort((a, b) => a.Index.CompareTo(b.Index));
                for (var i = 0; i < sorted.Count; i++)
                {
                    var entity = sorted[i];
                    if (!EntityManager.HasComponent<Game.Net.Curve>(entity))
                    {
                        continue;
                    }

                    var edge = EntityManager.GetComponentData<Game.Net.Edge>(entity);
                    if (!exportedNodes.Contains(edge.m_Start)
                        || !exportedNodes.Contains(edge.m_End))
                    {
                        continue;
                    }

                    var curve = EntityManager.GetComponentData<Game.Net.Curve>(entity);
                    var prefab = GetPrefabEntity(entity);
                    var name = GetPrefabName(prefab);

                    var section = GetNetCompositionInfo(entity, prefab);
                    var kind = GetNetworkKind(entity, section);
                    if (kind == null)
                    {
                        if (section.AirspaceOnly)
                        {
                            m_SkippedAirspaceNetworkCount++;
                        }
                        else if (EntityManager.HasComponent<Game.Net.Marker>(entity))
                        {
                            m_SkippedMarkerNetworkCount++;
                        }
                        continue;
                    }

                    var geometry = Cs2Geometry.BuildRoadGeometry(
                        EntityManager,
                        geometryContext,
                        entity,
                        edge,
                        curve,
                        section.Width,
                        IsSurfaceNetworkKind(kind));
                    var points = geometry.Centerline;
                    if (points.Count < 2)
                    {
                        continue;
                    }

                    var width = section.Width;
                    var mode = section.Form;
                    var level = GetNetworkLevel(kind, section);
                    var displayName = "";
                    if (kind == "ROAD" && EntityManager.HasComponent<Game.Net.Road>(entity))
                    {
                        var nameEntity = entity;
                        if (EntityManager.HasComponent<Game.Net.Aggregated>(entity))
                        {
                            var aggregate = EntityManager
                                .GetComponentData<Game.Net.Aggregated>(entity)
                                .m_Aggregate;
                            if (aggregate != Entity.Null)
                            {
                                nameEntity = aggregate;
                            }
                        }

                        if (!roadNameCache.TryGetValue(nameEntity, out displayName))
                        {
                            if (!TryGetCustomDisplayName(
                                nameSystem, nameEntity, "networks", out displayName))
                            {
                                displayName = "";
                            }
                            roadNameCache[nameEntity] = displayName;
                        }
                    }
                    var direction = section.Forward && section.Backward
                        ? 0
                        : (section.Forward ? 1 : (section.Backward ? -1 : 0));

                    derived.AddModes(
                        entity.Index,
                        new Cs2DerivedNetworkState.ModeRecord(
                            section.CarLanes,
                            section.BicycleLanes,
                            section.BusLanes,
                            Math.Max(
                                section.TramLanes,
                                EntityManager.HasComponent<Game.Net.TramTrack>(entity) ? 1 : 0),
                            Math.Max(
                                section.TrainLanes,
                                EntityManager.HasComponent<Game.Net.TrainTrack>(entity) ? 1 : 0),
                            Math.Max(
                                section.SubwayLanes,
                                EntityManager.HasComponent<Game.Net.SubwayTrack>(entity) ? 1 : 0)));

                    exportedNetworks.Add(entity);
                    if (kind == "ROAD")
                        traffic.AddRoad(EntityManager, entity, points, width, displayName, name, level);
                    if (written > 0)
                    {
                        json.AppendLine(",");
                    }

                    json.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"kind\": ");
                    CmmJson.AppendString(json, kind);
                    json.Append(", \"start_id\": ").Append(edge.m_Start.Index)
                        .Append(", \"end_id\": ").Append(edge.m_End.Index)
                        .Append(", \"geometry\": {\"points\": [");
                    AppendPoints(json, points);
                    json.Append("]}");
                    if (geometry.Footprint.Count >= 4)
                    {
                        json.Append(", \"footprint\": {\"points\": [");
                        AppendFootprintPoints(json, geometry.Footprint);
                        json.Append("]}");
                    }
                    json.Append(", \"mode\": ");
                    CmmJson.AppendString(json, mode);
                    json.Append(", \"width\": ").Append(FormatFloat(width));
                    if (level != null)
                    {
                        json.Append(", \"level\": ");
                        CmmJson.AppendString(json, level);
                    }

                    if (section.HasLanes)
                    {
                        json.Append(", \"lane_summary\": {\"car_lane_count\": ")
                            .Append(section.CarLanes.ToString(CultureInfo.InvariantCulture))
                            .Append(", \"direction\": ")
                            .Append(direction.ToString(CultureInfo.InvariantCulture))
                            .Append('}');
                    }
                    else
                    {
                        json.Append(", \"lane_summary\": null");
                    }

                    json.Append(", \"transit_for\": null, \"path_segment_ids\": [], \"platform\": false");
                    CmmJson.AppendProperty(json, "name", name);
                    CmmJson.AppendProperty(json, "display_name", displayName);
                    CmmJson.AppendProperty(json, "source_raw", name);
                    json.Append('}');
                    written++;
                    if (!string.IsNullOrEmpty(displayName))
                    {
                        m_CustomRoadNameCount++;
                    }
                    int kindCount;
                    m_NetworkKindCounts.TryGetValue(kind, out kindCount);
                    m_NetworkKindCounts[kind] = kindCount + 1;

                    AddTrackOverlays(
                        derived,
                        entity,
                        kind,
                        edge,
                        section,
                        points);
                }

                if (written > 0)
                {
                    json.AppendLine();
                }

                return written;
            }
            finally
            {
                entities.Dispose();
            }
        }

        private readonly struct NetCompositionInfo
        {
            internal NetCompositionInfo(
                float width,
                bool hasLanes,
                int carLanes,
                int bicycleLanes,
                int busLanes,
                int tramLanes,
                int trainLanes,
                int subwayLanes,
                bool hasPedestrianLanes,
                bool forward,
                bool backward,
                string form,
                bool highway,
                bool taxiway,
                bool runway,
                bool airspaceOnly,
                bool waterway,
                bool pathway,
                string sizeClass)
            {
                Width = width > 0f ? width : 0f;
                HasLanes = hasLanes;
                CarLanes = carLanes;
                BicycleLanes = bicycleLanes;
                BusLanes = busLanes;
                TramLanes = tramLanes;
                TrainLanes = trainLanes;
                SubwayLanes = subwayLanes;
                HasPedestrianLanes = hasPedestrianLanes;
                Forward = forward;
                Backward = backward;
                Form = form;
                Highway = highway;
                Taxiway = taxiway;
                Runway = runway;
                AirspaceOnly = airspaceOnly;
                Waterway = waterway;
                Pathway = pathway;
                SizeClass = sizeClass ?? "";
            }

            internal float Width { get; }

            internal bool HasLanes { get; }

            internal int CarLanes { get; }

            internal int BicycleLanes { get; }

            internal int BusLanes { get; }

            internal int TramLanes { get; }

            internal int TrainLanes { get; }

            internal int SubwayLanes { get; }

            internal bool HasPedestrianLanes { get; }

            internal bool Forward { get; }

            internal bool Backward { get; }

            internal string Form { get; }

            internal bool Highway { get; }

            internal bool Taxiway { get; }

            internal bool Runway { get; }

            internal bool AirspaceOnly { get; }

            internal bool Waterway { get; }

            internal bool Pathway { get; }

            internal string SizeClass { get; }
        }

        private NetCompositionInfo GetNetCompositionInfo(Entity segment, Entity prefab)
        {
            var composition = Entity.Null;
            if (EntityManager.HasComponent<Game.Net.Composition>(segment))
            {
                composition = EntityManager.GetComponentData<Game.Net.Composition>(segment).m_Edge;
            }

            NetCompositionInfo cached;
            if (composition != Entity.Null
                && m_NetCompositionCache.TryGetValue(composition, out cached))
            {
                return cached;
            }

            var width = 0f;
            var forward = false;
            var backward = false;
            var hasPedestrianLanes = false;
            var waterway = false;
            var form = "GROUND";

            if (composition != Entity.Null
                && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(composition))
            {
                var data = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(composition);
                width = data.m_Width;

                var state = data.m_State;
                forward = (state & Game.Prefabs.CompositionState.HasForwardRoadLanes) != 0
                    || (state & Game.Prefabs.CompositionState.HasForwardTrackLanes) != 0;
                backward = (state & Game.Prefabs.CompositionState.HasBackwardRoadLanes) != 0
                    || (state & Game.Prefabs.CompositionState.HasBackwardTrackLanes) != 0;
                hasPedestrianLanes = (state & Game.Prefabs.CompositionState.HasPedestrianLanes) != 0;

                var general = data.m_Flags.m_General;
                if ((general & Game.Prefabs.CompositionFlags.General.Tunnel) != 0)
                {
                    form = "TUNNEL";
                }
                else if ((general & Game.Prefabs.CompositionFlags.General.Elevated) != 0)
                {
                    form = "ELEVATED";
                }
            }

            var hasLanes = false;
            var carLanes = 0;
            var bicycleLanes = 0;
            var busLanes = 0;
            var tramLanes = 0;
            var trainLanes = 0;
            var subwayLanes = 0;
            if (composition != Entity.Null
                && EntityManager.HasBuffer<Game.Prefabs.NetCompositionLane>(composition))
            {
                hasLanes = true;
                var lanes = EntityManager.GetBuffer<Game.Prefabs.NetCompositionLane>(composition, true);
                for (var i = 0; i < lanes.Length; i++)
                {
                    var flags = lanes[i].m_Flags;

                    if ((flags & Game.Prefabs.LaneFlags.Master) != 0)
                    {
                        continue;
                    }

                    var lanePrefab = lanes[i].m_Lane;
                    if (lanePrefab == Entity.Null)
                    {
                        continue;
                    }

                    if (EntityManager.HasComponent<Game.Prefabs.NetLaneData>(lanePrefab))
                    {
                        var laneData = EntityManager.GetComponentData<Game.Prefabs.NetLaneData>(lanePrefab);
                        if ((laneData.m_Flags & Game.Prefabs.LaneFlags.OnWater) != 0)
                        {
                            waterway = true;
                        }
                        if ((laneData.m_Flags & Game.Prefabs.LaneFlags.PublicOnly) != 0)
                        {
                            busLanes++;
                        }
                    }

                    if (EntityManager.HasComponent<Game.Prefabs.CarLaneData>(lanePrefab))
                    {
                        var roadTypes = EntityManager
                            .GetComponentData<Game.Prefabs.CarLaneData>(lanePrefab)
                            .m_RoadTypes;
                        if (roadTypes == Game.Net.RoadTypes.Bicycle)
                        {
                            bicycleLanes++;
                        }

                        if ((roadTypes & Game.Net.RoadTypes.Car) != 0)
                        {
                            carLanes++;
                        }

                        if ((roadTypes & Game.Net.RoadTypes.Watercraft) != 0)
                        {
                            waterway = true;
                        }
                    }

                    if (EntityManager.HasComponent<Game.Prefabs.TrackLaneData>(lanePrefab))
                    {
                        var trackTypes = EntityManager
                            .GetComponentData<Game.Prefabs.TrackLaneData>(lanePrefab)
                            .m_TrackTypes;
                        if ((trackTypes & Game.Net.TrackTypes.Tram) != 0)
                        {
                            tramLanes++;
                        }

                        if ((trackTypes & Game.Net.TrackTypes.Train) != 0)
                        {
                            trainLanes++;
                        }

                        if ((trackTypes & Game.Net.TrackTypes.Subway) != 0)
                        {
                            subwayLanes++;
                        }
                    }
                }
            }

            var highway = false;
            var taxiway = false;
            var runway = false;
            var airspaceOnly = false;
            var pathway = false;
            if (composition != Entity.Null)
            {
                if (EntityManager.HasComponent<Game.Prefabs.RoadComposition>(composition))
                {
                    var road = EntityManager.GetComponentData<Game.Prefabs.RoadComposition>(composition);
                    highway = ((int)road.m_Flags & (int)Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
                }

                if (EntityManager.HasComponent<Game.Prefabs.TaxiwayComposition>(composition))
                {
                    var flags = EntityManager
                        .GetComponentData<Game.Prefabs.TaxiwayComposition>(composition)
                        .m_Flags;
                    airspaceOnly = flags == Game.Prefabs.TaxiwayFlags.Airspace;
                    runway = (flags & Game.Prefabs.TaxiwayFlags.Runway) != 0;
                    taxiway = !airspaceOnly && !runway;
                }

                waterway = waterway
                    || EntityManager.HasComponent<Game.Prefabs.WaterwayComposition>(composition);
                pathway = EntityManager.HasComponent<Game.Prefabs.PathwayComposition>(composition);
            }

            var info = new NetCompositionInfo(
                width,
                hasLanes,
                carLanes,
                bicycleLanes,
                busLanes,
                tramLanes,
                trainLanes,
                subwayLanes,
                hasPedestrianLanes,
                forward,
                backward,
                form,
                highway,
                taxiway,
                runway,
                airspaceOnly,
                waterway,
                pathway,
                GetRoadSizeClass(prefab));
            if (composition != Entity.Null)
            {
                m_NetCompositionCache[composition] = info;
            }

            return info;
        }

        private void AddTrackOverlays(
            Cs2DerivedNetworkState derived,
            Entity entity,
            string baseKind,
            Game.Net.Edge edge,
            NetCompositionInfo section,
            List<float3> points)
        {
            var hasTram = section.TramLanes > 0
                || EntityManager.HasComponent<Game.Net.TramTrack>(entity);
            var hasTrain = section.TrainLanes > 0
                || EntityManager.HasComponent<Game.Net.TrainTrack>(entity);
            var hasSubway = section.SubwayLanes > 0
                || EntityManager.HasComponent<Game.Net.SubwayTrack>(entity);

            if (hasTram && baseKind != "TRAM")
            {
                derived.AddTrackOverlay(
                    entity.Index,
                    "TRAM",
                    section.Form,
                    edge.m_Start.Index,
                    edge.m_End.Index,
                    points,
                    section.Width);
            }

            if (hasTrain && baseKind != "RAIL")
            {
                derived.AddTrackOverlay(
                    entity.Index,
                    "RAIL",
                    section.Form,
                    edge.m_Start.Index,
                    edge.m_End.Index,
                    points,
                    section.Width);
            }

            if (hasSubway && baseKind != "METRO")
            {
                derived.AddTrackOverlay(
                    entity.Index,
                    "METRO",
                    section.Form,
                    edge.m_Start.Index,
                    edge.m_End.Index,
                    points,
                    section.Width);
            }
        }

        private static void AppendPoints(StringBuilder json, List<float3> points)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                AppendVec3(json, points[i]);
            }
        }

        private static void AppendFootprintPoints(StringBuilder json, List<float3> points)
        {
            for (var i = 0; i < points.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(',');
                }

                json.Append('[')
                    .Append(FormatFloat(points[i].x))
                    .Append(',')
                    .Append(FormatFloat(points[i].z))
                    .Append(']');
            }
        }

        private Entity GetPrefabEntity(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Prefabs.PrefabRef>(entity))
            {
                return Entity.Null;
            }

            return EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
        }

        private string GetRoadSizeClass(Entity prefab)
        {
            if (prefab == Entity.Null)
            {
                return "";
            }

            string cached;
            if (m_RoadSizeClassCache.TryGetValue(prefab, out cached))
            {
                return cached;
            }

            var sizeClass = "";
            if (EntityManager.HasComponent<Game.Prefabs.UIObjectData>(prefab))
            {
                var uiObject = EntityManager.GetComponentData<Game.Prefabs.UIObjectData>(prefab);
                var groupName = GetPrefabName(uiObject.m_Group);
                if (string.Equals(groupName, "RoadsLargeRoads", StringComparison.Ordinal))
                {
                    sizeClass = "LARGE";
                }
                else if (string.Equals(groupName, "RoadsMediumRoads", StringComparison.Ordinal))
                {
                    sizeClass = "MEDIUM";
                }
                else if (string.Equals(groupName, "RoadsSmallRoads", StringComparison.Ordinal))
                {
                    sizeClass = "SMALL";
                }
            }

            m_RoadSizeClassCache[prefab] = sizeClass;
            return sizeClass;
        }

        /// <summary>依据横断面车道和道路尺寸确定道路等级</summary>
        private static string GetRoadLevel(NetCompositionInfo section)
        {
            if (section.Highway)
            {
                return "HIGHWAY";
            }

            if (section.Taxiway)
            {
                return "SPECIAL";
            }

            if (section.CarLanes == 0 && section.HasPedestrianLanes)
            {
                return "BEAUTIFICATION";
            }

            if (string.Equals(section.SizeClass, "LARGE", StringComparison.Ordinal)
                || string.Equals(section.SizeClass, "MEDIUM", StringComparison.Ordinal))
            {
                return "MAIN";
            }

            if (section.SizeClass.Length == 0 && section.Width >= 24f)
            {
                return "MAIN";
            }

            return "BRANCH";
        }

        private static bool IsSurfaceNetworkKind(string kind)
        {
            return kind == "ROAD"
                || kind == "BEAUTIFICATION";
        }

        private static string GetNetworkLevel(string kind, NetCompositionInfo section)
        {
            if (kind == "ROAD")
            {
                return section.Runway || section.Taxiway
                    ? "SPECIAL"
                    : GetRoadLevel(section);
            }
            if (kind == "BEAUTIFICATION") return "BEAUTIFICATION";
            return null;
        }
    }
}
