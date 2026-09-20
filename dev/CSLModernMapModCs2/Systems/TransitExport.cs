using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>
    /// 将CS2站点、线路和路径转换为CMM。CMM引用只指向本次实际导出的节点和路段。
    /// </summary>
    internal static class TransitExport
    {
        /// <summary>Owner链最大上溯跳数；超过视作不可锚定（防环、防意外长链）。</summary>
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

            /// <summary>线路实体 → prefab资产名，写进extensions.cs2；line.source_raw是渲染端类型关键字，放不下资产名。</summary>
            internal string LinePrefabsJson = "";

            /// <summary>完整JSON数组（含方括号），与terrain.Issues合并进顶层issues。</summary>
            internal string Issues = "[]";

            internal int IssueCount;

            internal string FirstIssue = "";

            internal int StopCount;
            internal int LineCount;
            internal int RouteCount;

            /// <summary>纯货物线路数（不进transit_lines，只计数进extensions.cs2.cargo_routes）。</summary>
            internal int CargoRouteCount;
        }

        /// <summary>内存里的站点记录；serving_line_ids由线路侧回填后再序列化。</summary>
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
        }

        internal static Result Build(
            World world,
            EntityManager em,
            Func<Entity, string> getPrefabName,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            Dictionary<Entity, int> laneToNetworkId)
        {
            var issues = new CmmIssueCollector();

            Game.UI.NameSystem nameSystem = null;
            try
            {
                nameSystem = world.GetOrCreateSystemManaged<Game.UI.NameSystem>();
            }
            catch (Exception e)
            {
                Mod.log.Warn("[transit] NameSystem不可用: " + e.GetType().Name + ": " + e.Message);
            }

            var stops = CollectStops(
                em, getPrefabName, nameSystem, exportedNodes, exportedNetworks,
                out var unresolvedStops);
            var result = CollectLinesAndRoutes(
                em, getPrefabName, nameSystem, stops,
                exportedNetworks, laneToNetworkId);

            // 两次导出的确定性：站点 / 线路都按实体Index排序后再写。
            stops.Sort((a, b) => a.Id.CompareTo(b.Id));
            result.StopsJson = SerializeStops(stops);

            if (unresolvedStops > 0)
            {
                issues.Warn("TRANSIT_STOP_NODE_UNRESOLVED",
                    unresolvedStops + " 个站点无法沿Owner链锚定到已导出的Game.Net.Node"
                    + "（无Building.m_RoadEdge / Attached可导出段），已从stops剔除");
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
                        ServingLineIds = new List<int>()
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

        /// <summary>
        /// stop.network_node_id的锚定链：沿Owner向上找 (edge, curvePosition) 事实，
        /// 再按端点归属取node。全部走真实组件，不做空间最近邻。
        /// 返回Entity.Null表示不可锚定。
        /// </summary>
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
                    // curve position靠近起点端取m_Start，靠近终点端取m_End
                    // （Curve是从m_Start指向m_End的贝塞尔）。
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

        /// <summary>采集客运线路；一条CS2 TransportLine对应一条CMM line和route。</summary>
        private static Result CollectLinesAndRoutes(
            EntityManager em,
            Func<Entity, string> getPrefabName,
            Game.UI.NameSystem nameSystem,
            List<StopRecord> stops,
            HashSet<Entity> exportedNetworks,
            Dictionary<Entity, int> laneToNetworkId)
        {
            var result = new Result();
            var exportedStopIds = new HashSet<int>();
            var stopById = new Dictionary<int, StopRecord>();
            foreach (var stop in stops)
            {
                exportedStopIds.Add(stop.Id);
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
                // 按实体Index排序，两次导出之间字节级可对比。
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

                    var stopIds = new List<int>();
                    var waypoints = em.GetBuffer<Game.Routes.RouteWaypoint>(entity);
                    for (var w = 0; w < waypoints.Length; w++)
                    {
                        var waypoint = waypoints[w].m_Waypoint;
                        if (waypoint == Entity.Null || !em.HasComponent<Game.Routes.Connected>(waypoint))
                        {
                            continue;
                        }

                        var connected = em.GetComponentData<Game.Routes.Connected>(waypoint).m_Connected;
                        if (connected == Entity.Null
                            || !em.HasComponent<Game.Routes.TransportStop>(connected)
                            || em.HasComponent<Game.Routes.TaxiStand>(connected))
                        {
                            continue;
                        }

                        if (exportedStopIds.Contains(connected.Index))
                        {
                            stopIds.Add(connected.Index);
                        }
                    }

                    // is_closed用写出的站序判断：首尾同一站且多于1站
                    // （原始站序即使闭合，闭合站被剔除后也不能再声称loop，否则validate报错）。
                    var isClosed = stopIds.Count > 1 && stopIds[0] == stopIds[stopIds.Count - 1];

                    var pathSegmentIds = new List<int>();
                    var segments = em.GetBuffer<Game.Routes.RouteSegment>(entity);
                    for (var s = 0; s < segments.Length; s++)
                    {
                        var segment = segments[s].m_Segment;
                        if (segment == Entity.Null
                            || !em.HasComponent<Game.Pathfind.PathElement>(segment))
                        {
                            continue;
                        }

                        var pathElements = em.GetBuffer<Game.Pathfind.PathElement>(segment);
                        for (var p = 0; p < pathElements.Length; p++)
                        {
                            var target = pathElements[p].m_Target;
                            int networkId;
                            if (target != Entity.Null && exportedNetworks.Contains(target))
                            {
                                networkId = target.Index;
                            }
                            else if (target != Entity.Null
                                && laneToNetworkId.TryGetValue(target, out var ownerNetworkId))
                            {
                                networkId = ownerNetworkId;
                            }
                            else
                            {
                                continue;
                            }

                            // 一条路段通常包含多条lane；路径缓冲会连续列出它们。
                            // CMM引用路段而非车道，因此只去掉相邻的重复路段。
                            if (pathSegmentIds.Count == 0
                                || pathSegmentIds[pathSegmentIds.Count - 1] != networkId)
                            {
                                pathSegmentIds.Add(networkId);
                            }
                        }
                    }

                    var geometryKind = pathSegmentIds.Count > 0 ? "path_chain" : "stop_direct";

                    // serving_line_ids回填（route遍历到谁就记谁，升序写）。
                    var seenStops = new HashSet<int>();
                    for (var s = 0; s < stopIds.Count; s++)
                    {
                        if (seenStops.Add(stopIds[s]))
                        {
                            stopById[stopIds[s]].ServingLineIds.Add(entity.Index);
                        }
                    }

                    // 线名：自定义名 → 翻译键 → 空串。
                    string lineName;
                    string lineNameSource;
                    ResolveLineName(nameSystem, entity, prefabName,
                        em.GetComponentData<Game.Routes.RouteNumber>(entity).m_Number,
                        out lineName, out lineNameSource);

                    var color = em.GetComponentData<Game.Routes.Color>(entity).m_Color;

                    // 写TransitLine
                    if (lineWritten > 0)
                    {
                        linesJson.AppendLine(",");
                    }

                    linesJson.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"name\": ");
                    CmmJson.AppendString(linesJson, lineName);
                    linesJson.Append(", \"kind\": ");
                    CmmJson.AppendString(linesJson, MapStopKind(lineData.m_TransportType));
                    // Color32按RGBA字节序直读；0也原样保留，不转默认色。
                    linesJson.Append(", \"user_color\": [")
                        .Append(color.r.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.g.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.b.ToString(CultureInfo.InvariantCulture)).Append(", ")
                        .Append(color.a.ToString(CultureInfo.InvariantCulture))
                        .Append("], \"bidirectional\": null, \"branch\": ");
                    CmmJson.AppendString(linesJson, "");
                    // source_raw is a renderer type keyword; prefab names live in extensions.cs2.
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

                    // 写Route（id与line共用实体Index，两个集合各自内部唯一）
                    if (routeWritten > 0)
                    {
                        routesJson.AppendLine(",");
                    }

                    routesJson.Append("    {\"id\": ").Append(entity.Index)
                        .Append(", \"line_id\": ").Append(entity.Index)
                        .Append(", \"direction\": ");
                    CmmJson.AppendString(routesJson, isClosed ? "loop" : "outbound");
                    routesJson.Append(", \"stop_ids\": [");
                    for (var s = 0; s < stopIds.Count; s++)
                    {
                        if (s > 0)
                        {
                            routesJson.Append(", ");
                        }

                        routesJson.Append(stopIds[s].ToString(CultureInfo.InvariantCulture));
                    }

                    routesJson.Append("], \"is_closed\": ").Append(isClosed ? "true" : "false")
                        .Append(", \"geometry_kind\": ");
                    CmmJson.AppendString(routesJson, geometryKind);
                    routesJson.Append(", \"path_segment_ids\": [");
                    for (var p = 0; p < pathSegmentIds.Count; p++)
                    {
                        if (p > 0)
                        {
                            routesJson.Append(", ");
                        }

                        routesJson.Append(pathSegmentIds[p].ToString(CultureInfo.InvariantCulture));
                    }

                    routesJson.Append("]");
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

        /// <summary>
        /// 站点名：CustomName自定义名（source=custom）→ 渲染标签（source=game）→ 空。
        /// GetRenderedLabelName会同时覆盖自定义名，source要靠CustomName组件区分。
        /// </summary>
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
            }
        }

        /// <summary>线路名：自定义名 → 翻译键Assets.ROUTE_NAME[prefab名] 替换 {NUMBER} → 空串（不伪造）。</summary>
        private static void ResolveLineName(
            Game.UI.NameSystem nameSystem,
            Entity entity,
            string prefabName,
            int routeNumber,
            out string text,
            out string source)
        {
            text = "";
            source = "empty";

            if (entity != Entity.Null && nameSystem != null)
            {
                try
                {
                    string custom;
                    if (nameSystem.TryGetCustomName(entity, out custom) && !string.IsNullOrEmpty(custom))
                    {
                        text = custom;
                        source = "custom";
                        return;
                    }
                }
                catch (Exception e)
                {
                    Mod.log.Warn("[transit] 线路自定义名读取失败: " + e.GetType().Name + ": " + e.Message);
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
                        source = "game";
                    }
                }
            }
            catch (Exception e)
            {
                Mod.log.Warn("[transit] 线路名翻译读取失败: " + e.GetType().Name + ": " + e.Message);
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
                json.Append("}, \"position\": {\"x\": ").Append(FormatFloat(stop.Position.x))
                    .Append(", \"y\": ").Append(FormatFloat(stop.Position.y))
                    .Append(", \"z\": ").Append(FormatFloat(stop.Position.z))
                    .Append("}, \"platform_hint\": ");
                CmmJson.AppendString(json, "");
                CmmJson.AppendProperty(json, "source_raw", stop.SourceRaw);
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

        private static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

    }
}
