using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>采集公共交通站点与线路数据</summary>
    internal static class TransitExport
    {
        private const int MaxOwnerHops = 8;

        private static readonly HashSet<Game.Prefabs.TransportType> ExportableTypes =
            new HashSet<Game.Prefabs.TransportType>
            {
                Game.Prefabs.TransportType.Bus,
                Game.Prefabs.TransportType.Ferry,
                Game.Prefabs.TransportType.Train,
                Game.Prefabs.TransportType.Tram,
                Game.Prefabs.TransportType.Ship,
                Game.Prefabs.TransportType.Subway
            };

        internal sealed class Result
        {
            internal string StopsJson = "";

            internal string LinesJson = "";

            internal string RoutesJson = "";

            internal string LinePrefabsJson = "";

            internal string Issues = "[]";

            internal int IssueCount;

            internal string FirstIssue = "";

            internal int StopCount;
            internal int LineCount;
            internal int RouteCount;

            internal int CargoRouteCount;
        }

        private struct StopRecord
        {
            internal int Id;
            internal int NetworkNodeId;
            internal string Kind;
            internal string NameText;
            internal string NameSource;
            internal float3 Position;
            internal string SourceRaw;
            internal List<int> ServingLineIds;
            internal Entity StationGroup;
            internal string StationGroupSource;
            internal string NameKind;
            internal int NameEntityId;
        }

        internal static Result Build(
            World world,
            EntityManager em,
            Func<Entity, string> getPrefabName,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            Cs2DerivedNetworkState derived)
        {
            var issues = new CmmIssueCollector();

            var nameSystem = world.GetOrCreateSystemManaged<Game.UI.NameSystem>();

            var stops = CollectStops(
                em, getPrefabName, nameSystem, exportedNodes, exportedNetworks, issues,
                out var unresolvedStops);
            var result = CollectLinesAndRoutes(
                em, getPrefabName, nameSystem, stops, derived, issues);

            stops.Sort((a, b) => a.Id.CompareTo(b.Id));
            result.StopsJson = SerializeStops(stops);

            if (unresolvedStops > 0)
            {
                issues.Warn("TRANSIT_STOP_NODE_UNRESOLVED",
                    unresolvedStops + " 个站点无法沿Owner链锚定到已导出的Game.Net.Node"
                    + "（无Building.m_RoadEdge / Attached可导出段），受影响线路不可验证");
            }

            result.Issues = issues.ToJson();
            result.IssueCount = issues.Count;
            result.FirstIssue = issues.First;
            result.StopCount = stops.Count;
            return result;
        }

        private static List<StopRecord> CollectStops(
            EntityManager em,
            Func<Entity, string> getPrefabName,
            Game.UI.NameSystem nameSystem,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            CmmIssueCollector issues,
            out int unresolvedStops)
        {
            unresolvedStops = 0;
            var records = new List<StopRecord>();

            var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Common.Deleted>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>()
                }
            });

            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                    if (prefab == Entity.Null || !em.HasComponent<Game.Prefabs.TransportStopData>(prefab))
                    {
                        continue;
                    }

                    var stopData = em.GetComponentData<Game.Prefabs.TransportStopData>(prefab);
                    var kind = MapStopKind(stopData.m_TransportType);
                    var sourceRaw = getPrefabName(prefab) ?? "";

                    var node = AnchorToNetworkNode(
                        em, entity, exportedNodes, exportedNetworks, out _);
                    if (node == Entity.Null)
                    {
                        unresolvedStops++;
                        continue;
                    }

                    string nameText;
                    string nameSource;
                    ResolveEntityName(
                        nameSystem, entity, em.HasComponent<Game.UI.CustomName>(entity),
                        out nameText, out nameSource);

                    var stationGroup = ResolveStationGroup(em, entity, out var groupSource);
                    var nameEntity = entity;
                    var nameKind = kind == "METRO" ? "template" : "unknown";
                    if (nameSource == "custom")
                    {
                        nameKind = "station";
                    }
                    else if (stationGroup != Entity.Null)
                    {
                        ResolveEntityName(nameSystem, stationGroup,
                            em.HasComponent<Game.UI.CustomName>(stationGroup),
                            out var groupName, out var groupNameSource);
                        if (!string.IsNullOrWhiteSpace(groupName))
                        {
                            nameText = groupName;
                            nameSource = groupNameSource;
                            nameKind = "station";
                            nameEntity = stationGroup;
                        }
                    }
                    if (nameKind == "template" || string.IsNullOrWhiteSpace(nameText))
                    {
                        nameText = "";
                        nameSource = "empty";
                        issues.Warn("TRANSIT_STOP_NAME_UNRESOLVED", "Stop " + entity.Index
                            + ": 无有效车站显示名，站台模板名不作为站名导出");
                    }

                    var position = em.GetComponentData<Game.Objects.Transform>(entity).m_Position;

                    records.Add(new StopRecord
                    {
                        Id = entity.Index,
                        NetworkNodeId = node.Index,
                        Kind = kind,
                        NameText = nameText,
                        NameSource = nameSource,
                        Position = position,
                        SourceRaw = sourceRaw,
                        ServingLineIds = new List<int>(),
                        StationGroup = stationGroup,
                        StationGroupSource = groupSource,
                        NameKind = nameKind,
                        NameEntityId = nameEntity.Index
                    });
                }
            }
            finally
            {
                entities.Dispose();
                query.Dispose();
            }

            return records;
        }

        private static Entity ResolveStationGroup(EntityManager em, Entity stop, out string source)
        {
            var restriction = em.GetComponentData<Game.Routes.TransportStop>(stop).m_AccessRestriction;
            if (IsStationBuilding(em, restriction))
            {
                source = "AccessRestriction";
                return restriction;
            }
            var current = stop;
            for (var hop = 0; hop < MaxOwnerHops; hop++)
            {
                var parent = em.HasComponent<Game.Objects.Attached>(current)
                    ? em.GetComponentData<Game.Objects.Attached>(current).m_Parent : Entity.Null;
                if (IsStationBuilding(em, parent))
                {
                    source = "Parent";
                    return parent;
                }
                current = em.HasComponent<Game.Common.Owner>(current)
                    ? em.GetComponentData<Game.Common.Owner>(current).m_Owner : Entity.Null;
                if (current == Entity.Null) break;
                if (IsStationBuilding(em, current))
                {
                    source = "Owner";
                    return current;
                }
            }
            source = null;
            return Entity.Null;
        }

        private static bool IsStationBuilding(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.HasComponent<Game.Prefabs.PrefabRef>(entity)
                || !em.HasComponent<Game.Buildings.Building>(entity)) return false;
            return em.HasComponent<Game.Prefabs.TransportStationData>(
                em.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab);
        }

        /// <summary>沿归属关系锚定已导出网络节点而非查找最近节点</summary>
        private static Entity AnchorToNetworkNode(
            EntityManager em,
            Entity stop,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            out string source)
        {
            source = null;
            var current = stop;

            for (var hop = 0; hop < MaxOwnerHops; hop++)
            {
                Entity edge = Entity.Null;
                var curvePosition = 0f;

                if (em.HasComponent<Game.Buildings.Building>(current))
                {
                    var building = em.GetComponentData<Game.Buildings.Building>(current);
                    if (building.m_RoadEdge != Entity.Null)
                    {
                        edge = building.m_RoadEdge;
                        curvePosition = building.m_CurvePosition;
                        source = hop == 0 ? "self_building" : "owner_building";
                    }
                }

                if (edge == Entity.Null && em.HasComponent<Game.Objects.Attached>(current))
                {
                    var attached = em.GetComponentData<Game.Objects.Attached>(current);
                    if (attached.m_Parent != Entity.Null
                        && exportedNetworks.Contains(attached.m_Parent))
                    {
                        edge = attached.m_Parent;
                        curvePosition = attached.m_CurvePosition;
                        source = "attached_edge";
                    }
                }

                if (edge != Entity.Null && exportedNetworks.Contains(edge))
                {
                    var edgeData = em.GetComponentData<Game.Net.Edge>(edge);
                    var node = curvePosition < 0.5f ? edgeData.m_Start : edgeData.m_End;
                    if (node != Entity.Null && exportedNodes.Contains(node))
                    {
                        return node;
                    }
                }

                if (!em.HasComponent<Game.Common.Owner>(current))
                {
                    break;
                }

                var owner = em.GetComponentData<Game.Common.Owner>(current).m_Owner;
                if (owner == Entity.Null || owner == current)
                {
                    break;
                }

                current = owner;
            }

            source = null;
            return Entity.Null;
        }

        private static string MapStopKind(Game.Prefabs.TransportType type)
        {
            switch (type)
            {
                case Game.Prefabs.TransportType.Bus: return "BUS";
                case Game.Prefabs.TransportType.Tram: return "TRAM";
                case Game.Prefabs.TransportType.Subway: return "METRO";
                case Game.Prefabs.TransportType.Train: return "TRAIN";
                case Game.Prefabs.TransportType.Ship: return "SHIP";
                case Game.Prefabs.TransportType.Ferry: return "FERRY";
                case Game.Prefabs.TransportType.Airplane: return "AIRPLANE";
                case Game.Prefabs.TransportType.Helicopter: return "HELICOPTER";
                default: return "OTHER";
            }
        }

        private static Result CollectLinesAndRoutes(
            EntityManager em,
            Func<Entity, string> getPrefabName,
            Game.UI.NameSystem nameSystem,
            List<StopRecord> stops,
            Cs2DerivedNetworkState derived,
            CmmIssueCollector issues)
        {
            var result = new Result();
            var stopById = new Dictionary<int, StopRecord>();
            foreach (var stop in stops)
            {
                stopById[stop.Id] = stop;
            }

            var linesJson = new StringBuilder(64 * 1024);
            var routesJson = new StringBuilder(64 * 1024);
            var linePrefabsJson = new StringBuilder(1024);
            var lineWritten = 0;
            var routeWritten = 0;

            var query = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Routes.Color>(),
                    ComponentType.ReadOnly<Game.Prefabs.PrefabRef>(),
                    ComponentType.ReadOnly<Game.Routes.Route>(),
                    ComponentType.ReadOnly<Game.Routes.RouteNumber>(),
                    ComponentType.ReadOnly<Game.Routes.RouteSegment>(),
                    ComponentType.ReadOnly<Game.Routes.RouteVehicle>(),
                    ComponentType.ReadOnly<Game.Routes.RouteWaypoint>(),
                    ComponentType.ReadOnly<Game.Routes.TransportLine>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Game.Common.Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>()
                }
            });

            var entities = query.ToEntityArray(Allocator.Temp);
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
                    var prefab = em.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
                    if (prefab == Entity.Null || !em.HasComponent<Game.Prefabs.TransportLineData>(prefab))
                    {
                        continue;
                    }

                    var lineData = em.GetComponentData<Game.Prefabs.TransportLineData>(prefab);

                    if (!lineData.m_PassengerTransport)
                    {
                        result.CargoRouteCount++;
                        continue;
                    }

                    if (!ExportableTypes.Contains(lineData.m_TransportType))
                    {
                        continue;
                    }

                    var prefabName = getPrefabName(prefab) ?? "";
                    var lineTypeKeyword = MapLineTypeKeyword(lineData.m_TransportType);
                    var transitKind = MapStopKind(lineData.m_TransportType);
                    var curveJoinDistance = TransitPathContinuity.CurveJoinDistance(
                        em.GetComponentData<Game.Prefabs.RouteData>(prefab).m_Width);

                    var stopIds = new List<int>();
                    var stopWaypoints = new List<int>();
                    var incompleteStops = false;
                    var waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(entity);
                    for (var w = 0; w < waypoints.Length; w++)
                    {
                        var waypoint = waypoints[w].m_Waypoint;
                        if (waypoint == Entity.Null || !em.HasComponent<Game.Routes.Connected>(waypoint))
                        {
                            if (waypoint == Entity.Null)
                            {
                                incompleteStops = true;
                                issues.Error("TRANSIT_WAYPOINT_REFERENCE", "Line " + entity.Index
                                    + ", waypoint " + w + ": 停靠引用损坏，此路线不可验证");
                            }
                            continue;
                        }

                        var connected = em.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
                        if (connected == Entity.Null
                            || !em.HasComponent<Game.Routes.TransportStop>(connected)
                            || em.HasComponent<Game.Routes.TaxiStand>(connected))
                        {
                            continue;
                        }

                        stopIds.Add(connected.Index);
                        stopWaypoints.Add(w);
                        if (!stopById.ContainsKey(connected.Index))
                        {
                            incompleteStops = true;
                            issues.Error("TRANSIT_ROUTE_STOP_UNRESOLVED",
                                "Line " + entity.Index + ", waypoint " + w + ": stop " + connected.Index + " 未导出，保留引用并停止验证此路线");
                        }
                    }

                    var pathSegmentIds = new List<int>();
                    var segments = em.GetBuffer<Game.Routes.RouteSegment>(entity);
                    var sourceSegments = new Dictionary<int, Entity>();
                    for (var s = 0; s < segments.Length; s++)
                    {
                        var segment = segments[s].m_Segment;
                        if (segment == Entity.Null || !em.HasComponent<Game.Routes.Segment>(segment)
                            || !em.HasComponent<Game.Common.Owner>(segment)
                            || em.GetComponentData<Game.Common.Owner>(segment).m_Owner != entity)
                        {
                            incompleteStops = true;
                            issues.Warn("TRANSIT_SEGMENT_REFERENCE", "Line " + entity.Index + ", segment buffer " + s + ": 无有效路段归属");
                            continue;
                        }
                        // 游戏 RoutePathSystem.SetupPathfind 用 Segment.m_Index 取起点，下一下标取终点并回绕。
                        var index = em.GetComponentData<Game.Routes.Segment>(segment).m_Index;
                        if (index < 0 || index >= waypoints.Length || sourceSegments.ContainsKey(index))
                        {
                            incompleteStops = true;
                            issues.Warn("TRANSIT_SEGMENT_INDEX", "Line " + entity.Index + ": 重复或越界的原始路段下标 " + index);
                            continue;
                        }
                        sourceSegments.Add(index, segment);
                    }

                    var repeatedFirst = stopIds.Count > 1 && stopIds[0] == stopIds[stopIds.Count - 1];
                    var closesRoute = stopIds.Count > 1 && sourceSegments.ContainsKey(waypoints.Length - 1);
                    var isClosed = repeatedFirst || closesRoute;
                    var intervals = TransitRouteIntervals.Build(stopWaypoints, waypoints.Length, closesRoute && !repeatedFirst);
                    var legsJson = new StringBuilder();
                    foreach (var interval in intervals)
                    {
                        var legIds = new List<int>();
                        var legPoints = new List<float3>();
                        var complete = !incompleteStops;
                        var source = "none";
                        foreach (var index in interval.SegmentIndices)
                        {
                            if (!sourceSegments.TryGetValue(index, out var segment))
                            {
                                complete = false;
                                continue;
                            }
                            var path = ReadRoutePath(em, segment, waypoints[index].m_Waypoint,
                                waypoints[(index + 1) % waypoints.Length].m_Waypoint,
                                transitKind, curveJoinDistance, derived);
                            if (!path.Complete)
                                issues.Warn("TRANSIT_SOURCE_PATH_PARTIAL", "Line " + entity.Index + ", waypoint " + index
                                    + ", segment " + segment.Index + ": " + path.Reason);
                            complete &= path.Complete;
                            if (legPoints.Count > 0 && path.Points.Count > 0
                                && math.distance(legPoints[legPoints.Count - 1], path.Points[0]) > 1f)
                                complete = false;
                            legIds.AddRange(path.Ids);
                            legPoints.AddRange(path.Points);
                            if (source == "none") source = path.Source;
                            else if (path.Source != "none" && source != path.Source) source = "mixed";
                        }
                        complete &= legIds.Count > 0;
                        pathSegmentIds.AddRange(legIds);
                        if (legsJson.Length > 0) legsJson.Append(", ");
                        legsJson.Append("{\"from_stop_index\": ").Append(interval.From)
                            .Append(", \"to_stop_index\": ").Append(interval.To)
                            .Append(", \"path_segment_ids\": [");
                        AppendIds(legsJson, legIds);
                        legsJson.Append(']');
                        CmmJson.AppendProperty(legsJson, "geometry_source", source);
                        legsJson.Append('}');
                        if (!complete)
                            issues.Warn("TRANSIT_LEG_PARTIAL", "Line " + entity.Index + ", leg " + interval.From + "->" + interval.To + ": 路径或方向未通过验证");
                        if (complete)
                        {
                            derived.AddRouteCarrier(
                                entity.Index, transitKind, lineTypeKeyword + " Line",
                                stopById[stopIds[interval.From]].NetworkNodeId,
                                stopById[stopIds[interval.To]].NetworkNodeId, legPoints, legIds);
                        }
                    }

                    var geometryKind = pathSegmentIds.Count > 0 ? "path_chain" : "stop_direct";

                    var seenStops = new HashSet<int>();
                    for (var s = 0; s < stopIds.Count; s++)
                    {
                        if (seenStops.Add(stopIds[s]) && stopById.TryGetValue(stopIds[s], out var stop))
                        {
                            stop.ServingLineIds.Add(entity.Index);
                        }
                    }

                    string lineName;
                    ResolveLineName(nameSystem, entity, prefabName,
                        em.GetComponentData<Game.Routes.RouteNumber>(entity).m_Number,
                        out lineName);

                    var color = em.GetComponentData<Game.Routes.Color>(entity).m_Color;

                    if (lineWritten > 0)
                    {
                        linesJson.AppendLine(",");
                    }

                    linesJson.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"name\": ");
                    CmmJson.AppendString(linesJson, lineName);
                    linesJson.Append(", \"kind\": ");
                    CmmJson.AppendString(linesJson, MapStopKind(lineData.m_TransportType));
                    linesJson.Append(", \"user_color\": [")
                        .Append(color.r.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.g.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.b.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.a.ToString(CultureInfo.InvariantCulture))
                        .Append("], \"bidirectional\": null, \"branch\": ");
                    CmmJson.AppendString(linesJson, "");
                    CmmJson.AppendProperty(linesJson, "source_raw", lineTypeKeyword);
                    linesJson.Append('}');
                    lineWritten++;

                    if (linePrefabsJson.Length > 0)
                    {
                        linePrefabsJson.Append(", ");
                    }

                    CmmJson.AppendString(linePrefabsJson, entity.Index.ToString(CultureInfo.InvariantCulture));
                    linePrefabsJson.Append(": ");
                    CmmJson.AppendString(linePrefabsJson, prefabName);

                    if (routeWritten > 0)
                    {
                        routesJson.AppendLine(",");
                    }

                    routesJson.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"line_id\": ").Append(entity.Index)
                        .Append(", \"direction\": ");
                    CmmJson.AppendString(routesJson, isClosed ? "loop" : "outbound");
                    routesJson.Append(", \"stop_ids\": [");
                    AppendIds(routesJson, stopIds);

                    routesJson.Append("], \"is_closed\": ").Append(isClosed ? "true" : "false")
                        .Append(", \"geometry_kind\": ");
                    CmmJson.AppendString(routesJson, geometryKind);
                    routesJson.Append(", \"path_segment_ids\": [");
                    AppendIds(routesJson, pathSegmentIds);

                    routesJson.Append("], \"legs\": [").Append(legsJson).Append(']');
                    CmmJson.AppendProperty(routesJson, "source_raw", prefabName);
                    routesJson.Append(", \"group_id\": null}");
                    routeWritten++;
                }
            }
            finally
            {
                entities.Dispose();
                query.Dispose();
            }

            if (lineWritten > 0)
            {
                linesJson.AppendLine();
            }

            if (routeWritten > 0)
            {
                routesJson.AppendLine();
            }

            result.LinesJson = linesJson.ToString();
            result.RoutesJson = routesJson.ToString();
            result.LinePrefabsJson = linePrefabsJson.Length == 0
                ? ""
                : "{" + linePrefabsJson + "}";
            result.LineCount = lineWritten;
            result.RouteCount = routeWritten;
            return result;
        }

        private sealed class RoutePath
        {
            internal readonly List<int> Ids = new List<int>();
            internal readonly List<float3> Points = new List<float3>();
            internal string Source = "none";
            internal bool Complete = true;
            internal string Reason = "";
        }

        private static RoutePath ReadRoutePath(EntityManager em, Entity segment, Entity fromWaypoint,
            Entity toWaypoint, string kind, float curveJoinDistance, Cs2DerivedNetworkState derived)
        {
            var result = new RoutePath();
            if (em.HasComponent<Game.Routes.CurveElement>(segment)
                && em.GetBuffer<Game.Routes.CurveElement>(segment).Length > 0)
            {
                result.Source = "CurveElement";
                var curves = em.GetBuffer<Game.Routes.CurveElement>(segment);
                for (var i = 0; i < curves.Length; i++)
                    AddPathPiece(result, derived, segment.Index, kind,
                        Cs2Geometry.BuildPath(curves[i].m_Curve, new float2(0f, 1f)), curveJoinDistance);
            }
            else if (em.HasComponent<Game.Pathfind.PathElement>(segment))
            {
                result.Source = "PathElement";
                var elements = em.GetBuffer<Game.Pathfind.PathElement>(segment);
                for (var i = 0; i < elements.Length; i++)
                {
                    var element = elements[i];
                    if (element.m_Target == Entity.Null || !em.HasComponent<Game.Net.Curve>(element.m_Target)
                        || em.HasComponent<Game.Objects.OutsideConnection>(element.m_Target))
                    {
                        result.Complete = false;
                        result.Reason = "PathElement 缺少可读取曲线";
                        continue;
                    }
                    AddPathPiece(result, derived, element.m_Target.Index, kind,
                        Cs2Geometry.BuildPath(em.GetComponentData<Game.Net.Curve>(element.m_Target).m_Bezier,
                            element.m_TargetDelta));
                }
            }
            if (result.Points.Count < 2)
            {
                result.Complete = false;
                result.Reason = "缺少完整曲线";
                return result;
            }
            if (!em.HasComponent<Game.Routes.PathTargets>(segment)
                || !em.HasComponent<Game.Routes.RouteLane>(fromWaypoint)
                || !em.HasComponent<Game.Routes.RouteLane>(toWaypoint))
            {
                result.Complete = false;
                result.Reason = "缺少原生路径端点关系";
                return result;
            }
            var targets = em.GetComponentData<Game.Routes.PathTargets>(segment);
            var from = em.GetComponentData<Game.Routes.RouteLane>(fromWaypoint);
            var to = em.GetComponentData<Game.Routes.RouteLane>(toWaypoint);
            // 与游戏 SetupPathfind 的端点契约相同；采样容差一米，不使用站台到锚点的距离。
            if (targets.m_StartLane != from.m_EndLane || targets.m_EndLane != to.m_StartLane
                || math.abs(targets.m_CurvePositions.x - from.m_EndCurvePos) >= 0.001f
                || math.abs(targets.m_CurvePositions.y - to.m_StartCurvePos) >= 0.001f
                || math.distance(result.Points[0], targets.m_ReadyStartPosition) > 1f
                || math.distance(result.Points[result.Points.Count - 1], targets.m_ReadyEndPosition) > 1f)
            {
                result.Complete = false;
                result.Reason = "曲线方向/端点与原生 RouteLane、PathTargets 不一致";
            }
            return result;
        }

        private static void AddPathPiece(RoutePath result, Cs2DerivedNetworkState derived,
            int sourceEntity, string kind, List<float3> points, float joinDistance = 1f)
        {
            if (!TransitPathContinuity.Join(result.Points, points, joinDistance))
            {
                result.Complete = false;
                result.Reason = "有向曲线存在缺口";
            }
            var id = derived.AddRouteSegment(sourceEntity, kind, points);
            if (id != 0) result.Ids.Add(id);
            result.Points.AddRange(points);
        }

        private static void AppendIds(StringBuilder json, List<int> ids)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append(ids[i].ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string MapLineTypeKeyword(Game.Prefabs.TransportType type)
        {
            switch (type)
            {
                case Game.Prefabs.TransportType.Bus: return "Bus";
                case Game.Prefabs.TransportType.Tram: return "Tram";
                case Game.Prefabs.TransportType.Subway: return "Metro";
                case Game.Prefabs.TransportType.Train: return "Train";
                case Game.Prefabs.TransportType.Ship: return "Ship";
                case Game.Prefabs.TransportType.Ferry: return "Ferry";
                case Game.Prefabs.TransportType.Airplane: return "Airplane";
                case Game.Prefabs.TransportType.Helicopter: return "Helicopter";
                default: return "";
            }
        }

        private static void ResolveEntityName(
            Game.UI.NameSystem nameSystem,
            Entity entity,
            bool hasCustomName,
            out string text,
            out string source)
        {
            text = "";
            source = "empty";

            if (nameSystem == null || entity == Entity.Null)
            {
                return;
            }

            try
            {
                var rendered = nameSystem.GetRenderedLabelName(entity) ?? "";
                if (rendered.Length > 0)
                {
                    text = rendered;
                    source = hasCustomName ? "custom" : "game";
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn("[transit] 站点名解析失败: " + e.GetType().Name + ": " + e.Message);
                throw;
            }
        }

        private static void ResolveLineName(
            Game.UI.NameSystem nameSystem,
            Entity entity,
            string prefabName,
            int routeNumber,
            out string text)
        {
            text = "";

            if (entity != Entity.Null && nameSystem != null)
            {
                try
                {
                    string custom;
                    if (nameSystem.TryGetCustomName(entity, out custom) && !string.IsNullOrEmpty(custom))
                    {
                        text = custom;
                        return;
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn("[transit] 线路自定义名读取失败: " + e.GetType().Name + ": " + e.Message);
                    throw;
                }
            }

            try
            {
                var dictionary = Game.SceneFlow.GameManager.instance == null
                    ? null
                    : Game.SceneFlow.GameManager.instance.localizationManager == null
                        ? null
                        : Game.SceneFlow.GameManager.instance.localizationManager.activeDictionary;
                if (dictionary != null)
                {
                    string translated;
                    if (dictionary.TryGetValue("Assets.ROUTE_NAME[" + prefabName + "]", out translated)
                        && !string.IsNullOrEmpty(translated))
                    {
                        text = translated.Replace(
                            "{NUMBER}",
                            routeNumber.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn("[transit] 线路名翻译读取失败: " + e.GetType().Name + ": " + e.Message);
                throw;
            }
        }

        private static string SerializeStops(List<StopRecord> stops)
        {
            var json = new StringBuilder(16 * 1024);
            for (var i = 0; i < stops.Count; i++)
            {
                var stop = stops[i];
                if (i > 0)
                {
                    json.AppendLine(",");
                }

                json.Append("    {\"id\": ").Append(stop.Id)
                    .Append(", \"network_node_id\": ").Append(stop.NetworkNodeId)
                    .Append(", \"kind\": ");
                CmmJson.AppendString(json, stop.Kind);
                json.Append(", \"name\": {\"text\": ");
                CmmJson.AppendString(json, stop.NameText);
                json.Append(", \"source\": ");
                CmmJson.AppendString(json, stop.NameSource);
                json.Append("}, \"position\": {\"x\": ").Append(CmmJson.FormatFloat(stop.Position.x))
                    .Append(", \"y\": ").Append(CmmJson.FormatFloat(stop.Position.y))
                    .Append(", \"z\": ").Append(CmmJson.FormatFloat(stop.Position.z))
                    .Append("}, \"platform_hint\": ");
                CmmJson.AppendString(json, "");
                CmmJson.AppendProperty(json, "source_raw", stop.SourceRaw);
                CmmJson.AppendProperty(json, "name_kind", stop.NameKind);
                json.Append(", \"name_entity_id\": ").Append(stop.NameEntityId);
                if (stop.StationGroup != Entity.Null)
                {
                    json.Append(", \"station_group\": {\"entity_id\": ").Append(stop.StationGroup.Index);
                    CmmJson.AppendProperty(json, "source", stop.StationGroupSource);
                    json.Append('}');
                }
                json.Append(", \"serving_line_ids\": [");

                stop.ServingLineIds.Sort();
                for (var s = 0; s < stop.ServingLineIds.Count; s++)
                {
                    if (s > 0)
                    {
                        json.Append(", ");
                    }

                    json.Append(stop.ServingLineIds[s].ToString(CultureInfo.InvariantCulture));
                }

                json.Append("], \"serving_group_ids\": []}");
            }

            if (stops.Count > 0)
            {
                json.AppendLine();
            }

            return json.ToString();
        }

    }
}
