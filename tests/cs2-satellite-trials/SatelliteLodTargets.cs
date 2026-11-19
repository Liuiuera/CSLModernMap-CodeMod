#if CS2_SATELLITE_TRIALS
using System;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Rendering;
using ObjectTransform = Game.Objects.Transform;

namespace CSLModernMap.Systems
{
    internal sealed class SatelliteLodTargets
    {
        internal sealed class Target
        {
            internal readonly int ReferenceId;
            internal readonly string Prefab;
            internal readonly float3 ReferencePosition;
            internal readonly bool InspectNear, Road;
            internal Entity Entity;
            internal float3 Position;
            internal float DistanceSquared = 24f * 24f;

            internal Target(int referenceId, string prefab, float3 referencePosition, bool inspectNear, bool road = false)
            {
                ReferenceId = referenceId;
                Prefab = prefab;
                ReferencePosition = referencePosition;
                InspectNear = inspectNear;
                Road = road;
            }
        }

        private readonly EntityManager entities;
        internal readonly Target[] Targets =
        {
            new Target(4743537, "OfficeHigh01_L4_4x4", new float3(-5800.444f, 259.735f, 3009.932f), true),
            new Target(4743652, "OfficeHigh01_L5_3x6", new float3(-5737.458f, 156.713f, -212.980f), true),
            new Target(6931938, "EU_ResidentialHigh01_L3_4x6", new float3(3526.561f, 407.167f, 5390.097f), true),
            new Target(494841, "OfficeHighSignature03", new float3(3162.009f, 401.094f, 3693.225f), false),
            new Target(4743335, "OfficeHigh01_L3_6x6", new float3(2631.132f, 368.758f, 3479.806f), false),
            new Target(4221121, "Medium Road", new float3(-6706.286f, 225.073f, 3159.762f), true, road: true),
            new Target(4213540, "Medium Road", new float3(-889.810f, 381.581f, 5686.889f), true, road: true),
            new Target(4218360, "Medium Road", new float3(-910.896f, 375.935f, 5572.404f), false, road: true),
        };

        internal SatelliteLodTargets(EntityManager entities, EntityQuery buildings, EntityQuery edges,
            Func<Entity, string> prefabName)
        {
            this.entities = entities;
            using var records = buildings.ToEntityArray(Allocator.Temp);
            foreach (var entity in records)
            {
                var position = entities.GetComponentData<ObjectTransform>(entity).m_Position;
                Match(entity, position, false, prefabName);
            }
            using var roads = edges.ToEntityArray(Allocator.Temp);
            foreach (var entity in roads)
            {
                if (!entities.HasComponent<Curve>(entity) || !entities.HasComponent<PrefabRef>(entity))
                    continue;
                var curve = entities.GetComponentData<Curve>(entity).m_Bezier;
                Match(entity, (curve.a + curve.d) * 0.5f, true, prefabName);
            }
        }

        private void Match(Entity entity, float3 position, bool road, Func<Entity, string> prefabName)
        {
            foreach (var target in Targets)
            {
                if (target.Road != road)
                    continue;
                var distanceSquared = math.distancesq(position.xz, target.ReferencePosition.xz);
                if (distanceSquared >= target.DistanceSquared)
                    continue;
                var prefab = entities.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (!string.Equals(prefabName(prefab), target.Prefab, StringComparison.Ordinal))
                    continue;
                target.Entity = entity;
                target.Position = position;
                target.DistanceSquared = distanceSquared;
            }
        }

        internal JArray Describe()
        {
            var result = new JArray();
            foreach (var target in Targets)
            {
                var row = new JObject
                {
                    ["referenceId"] = target.ReferenceId,
                    ["prefab"] = target.Prefab,
                    ["referencePosition"] = Vector(target.ReferencePosition),
                    ["kind"] = target.Road ? "road" : "building",
                    ["inspectNear"] = target.InspectNear,
                    ["matched"] = target.Entity != Entity.Null,
                };
                if (target.Entity != Entity.Null)
                {
                    row["entityIndex"] = target.Entity.Index;
                    row["entityVersion"] = target.Entity.Version;
                    row["position"] = Vector(target.Position);
                    row["distanceFromReferenceXZMeters"] = math.sqrt(target.DistanceSquared);
                }
                result.Add(row);
            }
            return result;
        }

        internal JArray Observe(LODParameters lod, float3 forward, float4 parameters, Func<int, JArray> roadBatches)
        {
            var rows = new JArray();
            foreach (var target in Targets)
            {
                var entity = target.Entity;
                if (entity == Entity.Null)
                    continue;

                bool exists = entities.Exists(entity);
                var row = new JObject { ["referenceId"] = target.ReferenceId, ["exists"] = exists };
                rows.Add(row);
                if (!exists)
                    continue;

                row["unspawned"] = entities.HasComponent<Game.Objects.Unspawned>(entity);
                row["hidden"] = entities.HasComponent<Game.Tools.Hidden>(entity);
                row["destroyed"] = entities.HasComponent<Game.Common.Destroyed>(entity);
                row["underConstruction"] = entities.HasComponent<Game.Objects.UnderConstruction>(entity);
                if (entities.HasComponent<CullingInfo>(entity))
                {
                    var info = entities.GetComponentData<CullingInfo>(entity);
                    row["boundsMin"] = Vector(info.m_Bounds.min);
                    row["boundsMax"] = Vector(info.m_Bounds.max);
                    row["maxLod"] = RenderingUtils.CalculateMaxLod(info.m_Bounds, lod.cameraPosition, forward, parameters);
                    row["minLod"] = info.m_MinLod;
                    row["passedCulling"] = info.m_PassedCulling;
                    row["cullingIndex"] = info.m_CullingIndex;
                    row["boundsMask"] = info.m_Mask.ToString();
                }
                if (entities.HasBuffer<MeshBatch>(entity))
                {
                    var batches = entities.GetBuffer<MeshBatch>(entity, isReadOnly: true);
                    var meshInstances = new JArray();
                    foreach (var batch in batches)
                    {
                        var instance = new JObject
                        {
                            ["groupIndex"] = batch.m_GroupIndex,
                            ["instanceIndex"] = batch.m_InstanceIndex,
                            ["meshIndex"] = batch.m_MeshIndex,
                        };
                        if (target.Road)
                            instance["groupBatches"] = roadBatches(batch.m_GroupIndex);
                        meshInstances.Add(instance);
                    }
                    row["meshInstances"] = meshInstances;
                }
            }
            return rows;
        }

        internal static JArray Vector(float3 point) => new JArray(point.x, point.y, point.z);
    }
}
#endif
