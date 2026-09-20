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

        /// <summary>导出路网及其横断面语义，并建立lane到路段的映射。</summary>
        private int AppendNetworks(
            StringBuilder json,
            HashSet<Entity> exportedNodes,
            HashSet<Entity> exportedNetworks,
            Dictionary<Entity, int> laneToNetworkId)
        {
            m_NetCompositionCache.Clear();
            m_RoadSizeClassCache.Clear();

            var entities = m_EdgeQuery.ToEntityArray(Allocator.Temp);
            var written = 0;
            try
            {
                for (var i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!EntityManager.HasComponent<Game.Net.Curve>(entity))
                    {
                        continue;
                    }

                    var kind = GetNetworkKind(entity);
                    if (kind == null)
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

                    var width = section.Width;
                    var mode = section.Form;
                    var level = kind == "ROAD" ? GetRoadLevel(section) : null;
                    var direction = section.Forward && section.Backward
                        ? 0
                        : (section.Forward ? 1 : (section.Backward ? -1 : 0));

                    exportedNetworks.Add(entity);
                    if (EntityManager.HasComponent<Game.Net.SubLane>(entity))
                    {
                        var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(entity, true);
                        for (var laneIndex = 0; laneIndex < subLanes.Length; laneIndex++)
                        {
                            var lane = subLanes[laneIndex].m_SubLane;
                            if (lane != Entity.Null)
                            {
                                laneToNetworkId[lane] = entity.Index;
                            }
                        }
                    }

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
                    AppendCurve(json, curve);
                    json.Append("]}, \"mode\": ");
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

                    // name与source_raw都取prefab资产名；display_name留给玩家自定义名
                    json.Append(", \"transit_for\": null, \"path_segment_ids\": [], \"platform\": false");
                    CmmJson.AppendProperty(json, "name", name);
                    CmmJson.AppendProperty(json, "display_name", "");
                    CmmJson.AppendProperty(json, "source_raw", name);
                    json.Append('}');
                    written++;
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

        /// <summary>
        /// 一条路段的横断面语义。全部来自prefab的composition实体。
        /// 读不到横断面的段（堤岸、纯装饰件）：宽度记0、<c>HasLanes</c> 为false，
        /// </summary>
        private readonly struct NetCompositionInfo
        {
            internal NetCompositionInfo(
                float width,
                bool hasLanes,
                int carLanes,
                bool hasPedestrianLanes,
                bool forward,
                bool backward,
                string form,
                bool highway,
                bool taxiway,
                string sizeClass)
            {
                Width = width > 0f ? width : 0f;
                HasLanes = hasLanes;
                CarLanes = carLanes;
                HasPedestrianLanes = hasPedestrianLanes;
                Forward = forward;
                Backward = backward;
                Form = form;
                Highway = highway;
                Taxiway = taxiway;
                SizeClass = sizeClass ?? "";
            }

            /// <summary>横断面宽度（米，全宽）</summary>
            internal float Width { get; }

            internal bool HasLanes { get; }

            internal int CarLanes { get; }

            internal bool HasPedestrianLanes { get; }

            internal bool Forward { get; }

            internal bool Backward { get; }

            internal string Form { get; }

            internal bool Highway { get; }

            internal bool Taxiway { get; }

            /// <summary>游戏道路面板的尺寸档（LARGE / MEDIUM / SMALL），非原版路留空。</summary>
            internal string SizeClass { get; }
        }

        /// <summary>
        /// 读段用的横断面。路径：<c>Game.Net.Composition(段).m_Edge</c> to 横断面实体。
        /// 按 横断面实体 缓存：同一prefab在地面 / 高架 / 隧道下是三个不同横断面，
        /// 宽度本来就不同（Medium Road地面24 m / 高架20 m），缓存到prefab上会串。
        /// </summary>
        private NetCompositionInfo GetNetCompositionInfo(Entity segment, Entity prefab)
        {
            var composition = Entity.Null;
            if (EntityManager.HasComponent<Game.Net.Composition>(segment))
            {
                composition = EntityManager.GetComponentData<Game.Net.Composition>(segment).m_Edge;
            }

            // composition为Null的段没有横断面，语义全部由prefab决定，不进缓存
            // 否则第一个无横断面的prefab会把它的尺寸档污染给后面所有同类段。
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
            if (composition != Entity.Null
                && EntityManager.HasBuffer<Game.Prefabs.NetCompositionLane>(composition))
            {
                hasLanes = true;
                var lanes = EntityManager.GetBuffer<Game.Prefabs.NetCompositionLane>(composition, true);
                for (var i = 0; i < lanes.Length; i++)
                {
                    var flags = lanes[i].m_Flags;

                    // Master represents a lane group, not a physical lane.
                    if ((flags & Game.Prefabs.LaneFlags.Master) != 0)
                    {
                        continue;
                    }

                    if ((flags & Game.Prefabs.LaneFlags.Road) != 0)
                    {
                        carLanes++;
                    }
                }
            }

            var highway = false;
            var taxiway = false;
            if (composition != Entity.Null)
            {
                if (EntityManager.HasComponent<Game.Prefabs.RoadComposition>(composition))
                {
                    var road = EntityManager.GetComponentData<Game.Prefabs.RoadComposition>(composition);
                    highway = ((int)road.m_Flags & (int)Game.Prefabs.RoadFlags.UseHighwayRules) != 0;
                }

                taxiway = EntityManager.HasComponent<Game.Prefabs.TaxiwayComposition>(composition);
            }

            var info = new NetCompositionInfo(
                width,
                hasLanes,
                carLanes,
                hasPedestrianLanes,
                forward,
                backward,
                form,
                highway,
                taxiway,
                GetRoadSizeClass(prefab));
            if (composition != Entity.Null)
            {
                m_NetCompositionCache[composition] = info;
            }

            return info;
        }

        private Entity GetPrefabEntity(Entity entity)
        {
            if (!EntityManager.HasComponent<Game.Prefabs.PrefabRef>(entity))
            {
                return Entity.Null;
            }

            return EntityManager.GetComponentData<Game.Prefabs.PrefabRef>(entity).m_Prefab;
        }

        /// <summary>
        /// 游戏道路面板的尺寸档：读 <c>UIObjectData.m_Group</c> 指向的分类实体名
        /// 玩家自建或无分类的路返回空串
        /// </summary>
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

        /// <summary>
        /// 道路等级。判据全部来自横断面与prefab（高速旗标 / 车道构成 / 游戏面板尺寸档）
        /// </summary>
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

            // 步行街 / 步道
            if (section.CarLanes == 0 && section.HasPedestrianLanes)
            {
                return "BEAUTIFICATION";
            }

            if (string.Equals(section.SizeClass, "LARGE", StringComparison.Ordinal)
                || string.Equals(section.SizeClass, "MEDIUM", StringComparison.Ordinal))
            {
                return "MAIN";
            }

            // 非原版路（自建资产）没有面板分类，按宽度回退。
            if (section.SizeClass.Length == 0 && section.Width >= 24f)
            {
                return "MAIN";
            }

            return "BRANCH";
        }
    }
}
