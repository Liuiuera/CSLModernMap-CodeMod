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

        private Entity[] CollectBuildingEntities(CmmIssueCollector issues)
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
                    var children = ReadStructuralChildren(current.Entity, issues);
                    for (var i = 0; i < children.Length; i++)
                    {
                        var child = children[i];
                        bool structural;
                        try
                        {
                            if (child == Entity.Null || !EntityManager.Exists(child)
                                || EntityManager.HasComponent<Game.Common.Deleted>(child)
                                || EntityManager.HasComponent<Game.Tools.Temp>(child)) continue;
                            var prefab = GetPrefabEntity(child);
                            structural = EntityManager.HasComponent<Game.Objects.Transform>(child)
                                && (EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab)
                                    || EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefab));
                        }
                        catch (Exception error) when (error is ArgumentException
                            || error is InvalidOperationException && !(error is ObjectDisposedException))
                        {
                            issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building child data could not be read", "Building " + current.Parent
                                + ", child " + child.Index + " omitted: " + error.Message);
                            continue;
                        }
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

        private Entity[] ReadStructuralChildren(Entity entity, CmmIssueCollector issues)
        {
            try
            {
                if (!EntityManager.HasComponent<Game.Objects.SubObject>(entity)) return Array.Empty<Entity>();
                var buffer = EntityManager.GetBuffer<Game.Objects.SubObject>(entity, true);
                var children = new Entity[buffer.Length];
                for (var i = 0; i < buffer.Length; i++) children[i] = buffer[i].m_SubObject;
                return children;
            }
            catch (Exception error) when (error is ArgumentException
                || error is InvalidOperationException && !(error is ObjectDisposedException))
            {
                issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building child data could not be read", "Building " + entity.Index
                    + " children omitted: " + error.Message);
                return Array.Empty<Entity>();
            }
        }

        private sealed class BuildingStructure
        {
            internal int Id;
            internal int MeshIndex;
            internal float2[] Points;
            internal float? Height;
            internal float Elevation;
        }

        private Dictionary<int, List<BuildingStructure>> CollectBuildingStructures(
            Entity[] entities, HashSet<int> included,
            Dictionary<int, List<int>> children, Dictionary<int, int> parents, CmmIssueCollector issues)
        {
            var result = new Dictionary<int, List<BuildingStructure>>();
            var nextId = int.MaxValue;
            foreach (var entity in entities)
            {
                if (!included.Contains(entity.Index)) continue;
                Unity.Entities.DynamicBuffer<Game.Prefabs.SubMesh> meshes;
                Game.Objects.Transform transform;
                Entity prefab;
                try
                {
                    prefab = GetPrefabEntity(entity);
                    if (EntityManager.HasComponent<Game.Prefabs.SpawnableBuildingData>(prefab)) continue;
                    if (!EntityManager.HasComponent<Game.Prefabs.BuildingData>(prefab)
                        && !EntityManager.HasComponent<Game.Prefabs.BuildingExtensionData>(prefab)) continue;
                    if (!EntityManager.HasComponent<Game.Prefabs.SubMesh>(prefab))
                    {
                        continue;
                    }
                    transform = EntityManager.GetComponentData<Game.Objects.Transform>(entity);
                    meshes = EntityManager.GetBuffer<Game.Prefabs.SubMesh>(prefab, true);
                }
                catch (Exception error) when (error is ArgumentException
                    || error is InvalidOperationException && !(error is ObjectDisposedException))
                {
                    issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building model data could not be read", "Building " + entity.Index
                        + " structures omitted: " + error.Message);
                    continue;
                }
                var parts = new List<BuildingStructure>();
                for (var i = 0; i < meshes.Length; i++)
                {
                    BuildingStructure part;
                    try
                    {
                        part = ReadBuildingStructure(meshes[i], i, transform, entity, prefab, issues);
                    }
                    catch (Exception error) when (error is ArgumentException
                        || error is InvalidOperationException && !(error is ObjectDisposedException))
                    {
                        issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building model data could not be read", "Building " + entity.Index
                            + ", mesh " + i + " omitted: " + error.Message);
                        continue;
                    }
                    if (part == null)
                    {
                        continue;
                    }
                    while (included.Contains(nextId) || m_ExportedBuildingIds.Contains(nextId)) nextId--;
                    var id = nextId--;
                    part.Id = id;
                    parts.Add(part);
                    AddChildLink(children, parents, entity.Index, id);
                    m_ExportedBuildingIds.Add(id);
                    m_ExportedBuildingPositions[id] = part.Points[0] * 0.5f + part.Points[2] * 0.5f;
                }
                if (parts.Count > 0) result[entity.Index] = parts;
            }
            return result;
        }

        private BuildingStructure ReadBuildingStructure(Game.Prefabs.SubMesh mesh, int index,
            Game.Objects.Transform transform, Entity building, Entity prefab, CmmIssueCollector issues)
        {
            var buildingId = building.Index;
            var normalStates = Game.Prefabs.SubMeshFlags.HasTransform
                | Game.Prefabs.SubMeshFlags.RequireClear | Game.Prefabs.SubMeshFlags.RequireSafe;
            if ((mesh.m_Flags & ~normalStates) != 0)
            {
                return null;
            }
            if (!EntityManager.HasComponent<Game.Prefabs.MeshData>(mesh.m_SubMesh))
            {
                issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Active building mesh bounds are missing", "Building " + buildingId
                    + ", mesh " + index + " omitted: missing mesh bounds");
                return null;
            }
            var data = EntityManager.GetComponentData<Game.Prefabs.MeshData>(mesh.m_SubMesh);
            // 游戏的 ObjectInitializeSystem 将零值解释为默认层。
            var layers = data.m_DefaultLayers == 0 ? Game.Prefabs.MeshLayer.Default : data.m_DefaultLayers;
            if ((data.m_AvailableTypes & Game.Prefabs.MeshType.Object) == 0
                || (layers & (Game.Prefabs.MeshLayer.Default | Game.Prefabs.MeshLayer.Moving)) == 0
                || (data.m_State & Game.Prefabs.MeshFlags.Decal) != 0)
            {
                return null;
            }
            var min = data.m_Bounds.min;
            var max = data.m_Bounds.max;
            if (!math.all(math.isfinite(min)) || !math.all(math.isfinite(max)) || math.any(max < min))
            {
                issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building mesh bounds are invalid", "Building " + buildingId
                    + ", mesh " + index + " omitted: invalid mesh bounds");
                return null;
            }
            if (max.x <= min.x || max.z <= min.z)
            {
                return null;
            }
            var corners = new[] {
                new float3(min.x, min.y, min.z), new float3(max.x, min.y, min.z),
                new float3(max.x, min.y, max.z), new float3(min.x, min.y, max.z)
            };
            var points = new float2[4];
            for (var c = 0; c < corners.Length; c++)
            {
                var local = mesh.m_Position + math.rotate(mesh.m_Rotation, corners[c]);
                var world = transform.m_Position + math.rotate(transform.m_Rotation, local);
                if (!math.all(math.isfinite(world.xz)))
                {
                    issues.WarnGrouped("BUILDING_STRUCTURE_UNAVAILABLE", "Building footprint transform is invalid", "Building " + buildingId
                        + ", mesh " + index + " omitted: invalid footprint transform");
                    return null;
                }
                points[c] = world.xz;
            }
            var part = new BuildingStructure { MeshIndex = index, Points = points };
            try
            {
                float height, elevation;
                bool participates;
                string reason;
                if (TryMeasureBuildingMesh(building, prefab, mesh, index, transform,
                    out participates, out height, out elevation, out _, out reason))
                {
                    part.Height = height;
                    part.Elevation = elevation;
                }
                else if (participates)
                    issues.WarnGrouped("BUILDING_HEIGHT_UNAVAILABLE", reason, "Building " + buildingId
                        + ", mesh " + index + " height omitted: " + reason);
            }
            catch (Exception error) when (error is ArgumentException
                || error is InvalidOperationException && !(error is ObjectDisposedException))
            {
                issues.WarnGrouped("BUILDING_HEIGHT_UNAVAILABLE", "Building height data could not be read", "Building " + buildingId
                    + ", mesh " + index + " height omitted: " + error.Message);
            }
            return part;
        }

        private void AppendObjectHeight(StringBuilder json, Entity building, Entity prefab,
            Game.Objects.Transform transform, CmmIssueCollector issues)
        {
            var buildingId = building.Index;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            var source = "cs2:mesh-bounds";
            try
            {
                if (EntityManager.HasComponent<Game.Prefabs.ObjectGeometryData>(prefab))
                {
                    var geometry = EntityManager.GetComponentData<Game.Prefabs.ObjectGeometryData>(prefab);
                    if ((geometry.m_Flags & Game.Objects.GeometryFlags.Marker) != 0)
                    {
                        return;
                    }
                }
                if (!EntityManager.HasComponent<Game.Prefabs.SubMesh>(prefab))
                {
                    return;
                }
                var meshes = EntityManager.GetBuffer<Game.Prefabs.SubMesh>(prefab, true);
                if (meshes.Length == 0)
                {
                    return;
                }
                for (var i = 0; i < meshes.Length; i++)
                {
                    string reason, meshSource;
                    bool participates;
                    float meshHeight, elevation;
                    var measured = TryMeasureBuildingMesh(building, prefab, meshes[i], i, transform,
                        out participates, out meshHeight, out elevation, out meshSource, out reason);
                    if (!participates) continue;
                    if (!measured)
                    {
                        issues.WarnGrouped("BUILDING_HEIGHT_UNAVAILABLE", reason, "Building " + buildingId
                            + " height omitted: mesh " + i + " is not fully measurable (" + reason + ")");
                        return;
                    }
                    bottom = math.min(bottom, elevation);
                    top = math.max(top, elevation + meshHeight);
                    if (meshSource == "cs2:render-bounds") source = meshSource;
                }
            }
            catch (Exception error) when (error is ArgumentException
                || error is InvalidOperationException && !(error is ObjectDisposedException))
            {
                issues.WarnGrouped("BUILDING_HEIGHT_UNAVAILABLE", "Building height data could not be read", "Building " + buildingId
                    + " height omitted: " + error.Message);
                return;
            }
            var height = top - bottom;
            if (math.isfinite(height) && math.isfinite(bottom))
                AppendHeight(json, height, bottom, source);
        }

        private static void AppendHeight(StringBuilder json, float height, float elevation, string source)
        {
            var heightText = CmmJson.FormatFloat(height);
            var elevationText = CmmJson.FormatFloat(elevation);
            json.Append(", \"height_m\": ").Append(heightText);
            json.Append(", \"base_elevation_m\": ").Append(elevationText);
            CmmJson.AppendProperty(json, "height_source", source);
        }

        private static void AppendBuildingStructure(StringBuilder json, BuildingStructure part,
            int parentId, string prefabName, (string Role, string Service, string SubService) classification,
            (string Id, string Name, string Locale) category)
        {
            json.Append("    {\"id\": ").Append(part.Id);
            CmmJson.AppendProperty(json, "name", prefabName + " mesh " + part.MeshIndex);
            AppendBuildingCategory(json, category);
            CmmJson.AppendProperty(json, "role", classification.Role);
            CmmJson.AppendProperty(json, "service", classification.Service);
            CmmJson.AppendProperty(json, "sub_service", classification.SubService);
            CmmJson.AppendProperty(json, "icls", prefabName);
            CmmJson.AppendProperty(json, "source_raw", prefabName);
            CmmJson.AppendProperty(json, "geometry_source", "cs2:mesh-bounds");
            if (part.Height.HasValue)
                AppendHeight(json, part.Height.Value, part.Elevation, "cs2:mesh-bounds");
            json.Append(", \"parent_id\": ").Append(parentId)
                .Append(", \"parent_building_id\": ").Append(parentId)
                .Append(", \"child_ids\": [], \"footprint\": {\"points\": [");
            for (var i = 0; i < part.Points.Length; i++)
            {
                if (i > 0) json.Append(", ");
                json.Append('[').Append(CmmJson.FormatFloat(part.Points[i].x)).Append(", ")
                    .Append(CmmJson.FormatFloat(part.Points[i].y)).Append(']');
            }
            json.Append("]}}");
        }
    }
}
