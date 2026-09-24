using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>整理建筑结构及其归属关系</summary>
    public sealed partial class MapExportUISystem
    {
        private readonly Dictionary<int, int> m_StructuralParents = new Dictionary<int, int>();

        private Entity[] CollectBuildingEntities()
        {
            m_StructuralParents.Clear();
            var result = new Dictionary<int, Entity>();
            using (var source = m_BuildingQuery.ToEntityArray(Allocator.Temp))
                foreach (var entity in source) result[entity.Index] = entity;
            var roots = new List<Entity>(result.Values);
            roots.Sort((a, b) => a.Index.CompareTo(b.Index));
            var visited = new HashSet<int>();
            foreach (var root in roots)
            {
                var pending = new Stack<(Entity Entity, int Parent)>();
                pending.Push((root, root.Index));
                while (pending.Count > 0)
                {
                    var current = pending.Pop();
                    if (!visited.Add(current.Entity.Index)) continue;
                    if (!EntityManager.HasComponent<Game.Objects.SubObject>(current.Entity)) continue;
                    var children = EntityManager.GetBuffer<Game.Objects.SubObject>(current.Entity, true);
                    for (var i = 0; i < children.Length; i++)
                    {
                        var child = children[i].m_SubObject;
                        if (child == Entity.Null || !EntityManager.Exists(child)
                            || EntityManager.HasComponent<Game.Common.Deleted>(child)
                            || EntityManager.HasComponent<Game.Tools.Temp>(child)) continue;
                        var prefab = GetPrefabEntity(child);
                        var structural = EntityManager.HasComponent<Game.Objects.Transform>(child)
                            && (EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab)
                                || EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefab));
                        if (structural)
                        {
                            result[child.Index] = child;
                            if (child.Index != current.Parent) m_StructuralParents[child.Index] = current.Parent;
                        }
                        pending.Push((child, structural ? child.Index : current.Parent));
                    }
                }
            }
            var entities = new List<Entity>(result.Values);
            entities.Sort((a, b) => a.Index.CompareTo(b.Index));
            return entities.ToArray();
        }

        private sealed class BuildingStructure
        {
            internal int Id;
            internal int MeshIndex;
            internal float2[] Points;
        }

        private Dictionary<int, List<BuildingStructure>> CollectBuildingStructures(
            Entity[] entities, HashSet<int> included,
            Dictionary<int, List<int>> children, Dictionary<int, int> parents)
        {
            var result = new Dictionary<int, List<BuildingStructure>>();
            var nextId = int.MaxValue;
            foreach (var entity in entities)
            {
                if (!included.Contains(entity.Index)) continue;
                var prefab = GetPrefabEntity(entity);
                if (EntityManager.HasComponent<Game.Prefabs.SpawnableBuildingData>(prefab)) continue;
                if (!EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab)
                    && !EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefab)) continue;
                if (!EntityManager.HasComponent<Game.Prefabs.SubMesh>(prefab)) continue;
                var transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                var meshes = EntityManager.GetBuffer<Game.Prefabs.SubMesh>(prefab, true);
                var parts = new List<BuildingStructure>();
                for (var i = 0; i < meshes.Length; i++)
                {
                    var mesh = meshes[i];
                    if ((mesh.m_Flags & ~Game.Prefabs.SubMeshFlags.HasTransform) != 0) continue;
                    if (!EntityManager.HasComponent<Game.Prefabs.MeshData>(mesh.m_SubMesh)) continue;
                    var data = EntityManager.GetComponentData<Game.Prefabs.MeshData>(mesh.m_SubMesh);
                    if ((data.m_AvailableTypes & Game.Prefabs.MeshType.Object) == 0
                        || (data.m_DefaultLayers & Game.Prefabs.MeshLayer.Default) == 0
                        || data.m_DecalLayer != 0) continue;
                    var min = data.m_Bounds.min;
                    var max = data.m_Bounds.max;
                    if (!math.all(math.isfinite(min)) || !math.all(math.isfinite(max))
                        || max.x - min.x < 0.5f || max.z - min.z < 0.5f
                        || max.y - min.y < 0.5f) continue;
                    var corners = new[] {
                        new float3(min.x, min.y, min.z), new float3(max.x, min.y, min.z),
                        new float3(max.x, min.y, max.z), new float3(min.x, min.y, max.z)
                    };
                    var points = new float2[4];
                    for (var c = 0; c < corners.Length; c++)
                    {
                        var local = mesh.m_Position + math.rotate(mesh.m_Rotation, corners[c]);
                        var world = transform.m_Position + math.rotate(transform.m_Rotation, local);
                        points[c] = world.xz;
                    }
                    while (included.Contains(nextId) || m_ExportedBuildingIds.Contains(nextId)) nextId--;
                    var id = nextId--;
                    parts.Add(new BuildingStructure { Id = id, MeshIndex = i, Points = points });
                    AddChildLink(children, parents, entity.Index, id);
                    m_ExportedBuildingIds.Add(id);
                    m_ExportedBuildingPositions[id] = (points[0] + points[2]) * 0.5f;
                }
                if (parts.Count > 0) result[entity.Index] = parts;
            }
            return result;
        }

        private static void AppendBuildingStructure(StringBuilder json, BuildingStructure part,
            int parentId, string prefabName, (string Role, string Service, string SubService) classification)
        {
            json.Append("    {\"id\": ").Append(part.Id);
            CmmJson.AppendProperty(json, "name", prefabName + " mesh " + part.MeshIndex);
            CmmJson.AppendProperty(json, "role", classification.Role);
            CmmJson.AppendProperty(json, "service", classification.Service);
            CmmJson.AppendProperty(json, "sub_service", classification.SubService);
            CmmJson.AppendProperty(json, "icls", prefabName);
            CmmJson.AppendProperty(json, "source_raw", prefabName);
            CmmJson.AppendProperty(json, "geometry_source", "cs2:mesh-bounds");
            json.Append(", \"parent_id\": ").Append(parentId)
                .Append(", \"parent_building_id\": ").Append(parentId)
                .Append(", \"child_ids\": [], \"footprint\": {\"points\": [");
            for (var i = 0; i < part.Points.Length; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append('[').Append(FormatFloat(part.Points[i].x)).Append(", ")
                    .Append(FormatFloat(part.Points[i].y)).Append(']');
            }
            json.Append("]}}");
        }
    }
}
