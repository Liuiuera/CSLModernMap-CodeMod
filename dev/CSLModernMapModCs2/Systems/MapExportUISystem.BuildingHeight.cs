using Game.Prefabs;
using Game.Rendering;
using Unity.Entities;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    public sealed partial class MapExportUISystem
    {
        private bool TryMeasureBuildingMesh(Entity building, Entity prefab, SubMesh mesh, int meshIndex,
            Game.Objects.Transform transform, out bool participates, out float height, out float elevation,
            out string source, out string reason)
        {
            participates = true;
            height = elevation = 0f;
            source = "cs2:mesh-bounds";
            reason = "";

            if (EntityManager.HasComponent<Game.Objects.UnderConstruction>(building)
                && EntityManager.GetComponentData<Game.Objects.UnderConstruction>(building).m_NewPrefab == Entity.Null)
            {
                participates = false;
                return false;
            }
            var hasGeometry = EntityManager.HasComponent<ObjectGeometryData>(prefab);
            var geometry = hasGeometry ? EntityManager.GetComponentData<ObjectGeometryData>(prefab) : default;
            if (EntityManager.HasComponent<Game.Objects.Marker>(building)
                || hasGeometry && (geometry.m_Flags & Game.Objects.GeometryFlags.Marker) != 0)
            {
                participates = false;
                return false;
            }
            var physicalLot = Game.Objects.GeometryFlags.Physical | Game.Objects.GeometryFlags.HasLot;
            if (hasGeometry && (geometry.m_Flags & physicalLot) == physicalLot
                && EntityManager.HasComponent<Game.Common.Destroyed>(building)
                && EntityManager.GetComponentData<Game.Common.Destroyed>(building).m_Cleared >= 0f)
            {
                participates = false;
                return false;
            }

            var allowed = SubMeshFlags.DefaultMissingMesh | SubMeshFlags.HasTransform;
            var trafficFlags = SubMeshFlags.RequireLeftHandTraffic | SubMeshFlags.RequireRightHandTraffic;
            if ((mesh.m_Flags & trafficFlags) != 0)
                allowed |= World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>().leftHandTraffic
                    ? SubMeshFlags.RequireLeftHandTraffic : SubMeshFlags.RequireRightHandTraffic;
            if (EntityManager.HasComponent<Game.Objects.NetObject>(building))
                allowed |= BatchDataHelpers.CalculateNetObjectSubMeshData(
                    EntityManager.GetComponentData<Game.Objects.NetObject>(building));
            if (EntityManager.HasComponent<Game.Objects.Quantity>(building))
                allowed |= BatchDataHelpers.CalculateQuantitySubMeshData(
                    EntityManager.GetComponentData<Game.Objects.Quantity>(building),
                    EntityManager.HasComponent<QuantityObjectData>(prefab)
                        ? EntityManager.GetComponentData<QuantityObjectData>(prefab) : default, false);

            var stackFlags = SubMeshFlags.IsStackStart | SubMeshFlags.IsStackMiddle | SubMeshFlags.IsStackEnd;
            var stackData = default(StackData);
            int3 tileCounts = 0;
            float3 offsets = 0f, scales = 1f;
            if ((mesh.m_Flags & stackFlags) != 0
                && EntityManager.HasComponent<Game.Objects.Stack>(building)
                && EntityManager.HasComponent<StackData>(prefab))
            {
                stackData = EntityManager.GetComponentData<StackData>(prefab);
                allowed |= BatchDataHelpers.CalculateStackSubMeshData(
                    EntityManager.GetComponentData<Game.Objects.Stack>(building), stackData,
                    out tileCounts, out offsets, out scales);
            }
            if ((mesh.m_Flags & allowed) != mesh.m_Flags)
            {
                participates = false;
                return false;
            }
            if (mesh.m_LocaleFlags != 0)
            {
                var locale = World.GetExistingSystemManaged<LocaleMeshStateSystem>().currentLocaleFlags;
                if (EntityManager.HasComponent<LocaleMeshData>(prefab))
                    locale = BatchDataHelpers.CalculateLocaleMeshFlags(
                        EntityManager.GetComponentData<LocaleMeshData>(prefab), locale);
                if ((mesh.m_LocaleFlags & locale) != mesh.m_LocaleFlags)
                {
                    participates = false;
                    return false;
                }
            }
            if (EntityManager.HasComponent<SubMeshGroup>(prefab))
            {
                if (!EntityManager.HasComponent<MeshGroup>(building))
                {
                    reason = "missing mesh group selection";
                    return false;
                }
                var groups = EntityManager.GetBuffer<SubMeshGroup>(prefab, true);
                var selected = EntityManager.GetBuffer<MeshGroup>(building, true);
                var active = false;
                for (var i = 0; i < selected.Length; i++)
                {
                    var groupIndex = selected[i].m_SubMeshGroup;
                    if (groupIndex >= groups.Length)
                    {
                        reason = "invalid mesh group selection";
                        return false;
                    }
                    var range = groups[groupIndex].m_SubMeshRange;
                    var meshCount = EntityManager.GetBuffer<SubMesh>(prefab, true).Length;
                    if (range.x < 0 || range.y < range.x || range.y > meshCount)
                    {
                        reason = "invalid mesh group range";
                        return false;
                    }
                    active |= meshIndex >= range.x && meshIndex < range.y;
                }
                if (!active)
                {
                    participates = false;
                    return false;
                }
            }
            if (!EntityManager.HasComponent<MeshData>(mesh.m_SubMesh))
            {
                reason = "missing active mesh bounds";
                return false;
            }
            var data = EntityManager.GetComponentData<MeshData>(mesh.m_SubMesh);
            var moving = EntityManager.HasComponent<InterpolatedTransform>(building)
                || EntityManager.HasComponent<Game.Objects.Stopped>(building);
            var layers = moving ? MeshLayer.Moving : MeshLayer.Default;
            if (!moving && EntityManager.HasComponent<Game.Objects.Elevation>(building)
                && EntityManager.GetComponentData<Game.Objects.Elevation>(building).m_Elevation < 0f)
                layers = MeshLayer.Tunnel;
            if (!moving && data.m_DefaultLayers != 0) layers = data.m_DefaultLayers;
            // 按 BatchInstanceSystem 的实例层和模型层判断是否为地上实体；Tunnel 不等同于贴花。
            if ((data.m_State & MeshFlags.Decal) != 0
                || (layers & (MeshLayer.Default | MeshLayer.Moving)) == 0)
            {
                participates = false;
                return false;
            }
            if ((data.m_AvailableTypes & MeshType.Object) == 0)
            {
                reason = "unsupported active mesh type: " + data.m_AvailableTypes;
                return false;
            }
            var min = data.m_Bounds.min;
            var max = data.m_Bounds.max;
            if ((mesh.m_Flags & SubMeshFlags.DefaultMissingMesh) != 0)
            {
                if (!hasGeometry)
                {
                    reason = "missing object render bounds";
                    return false;
                }
                // BatchDataSystem 在此分支按对象边界缩放默认模型，而非使用默认模型自身尺寸。
                min = geometry.m_Bounds.min;
                max = geometry.m_Bounds.max;
                source = "cs2:render-bounds";
                if ((mesh.m_Flags & SubMeshFlags.IsStackMiddle) != 0)
                {
                    var axis = stackData.m_Direction == StackDirection.Right ? 0
                        : stackData.m_Direction == StackDirection.Up ? 1 : 2;
                    min[axis] = -1f;
                    max[axis] = 1f;
                }
            }
            var supported = MeshFlags.Base | MeshFlags.MinBounds | MeshFlags.Default | MeshFlags.Animated | MeshFlags.Skeleton
                | MeshFlags.StackX | MeshFlags.StackY | MeshFlags.StackZ;
            if (source != "cs2:render-bounds" && ((data.m_State & ~supported) != 0
                || (data.m_State & (MeshFlags.StackX | MeshFlags.StackY | MeshFlags.StackZ)) != 0
                    && (mesh.m_Flags & stackFlags) == 0))
            {
                reason = "unsupported active mesh state: " + data.m_State;
                return false;
            }
            var count = (mesh.m_Flags & SubMeshFlags.IsStackStart) != 0 ? tileCounts.x
                : (mesh.m_Flags & SubMeshFlags.IsStackMiddle) != 0 ? tileCounts.y
                : (mesh.m_Flags & SubMeshFlags.IsStackEnd) != 0 ? tileCounts.z : 1;
            if (count <= 0)
            {
                participates = false;
                return false;
            }
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            // 同一子网格的重复块沿堆叠轴线性排列，首尾块即可给出合并范围。
            for (var end = 0; end < (count > 1 ? 2 : 1); end++)
            {
                var position = mesh.m_Position;
                var rotation = mesh.m_Rotation;
                float3 scale = 1f;
                if ((mesh.m_Flags & stackFlags) != 0)
                    BatchDataHelpers.CalculateStackSubMeshData(stackData, offsets, scales,
                        end == 0 ? 0 : count - 1, mesh.m_Flags, ref position, ref scale);
                if ((mesh.m_Flags & (stackFlags | SubMeshFlags.HasTransform)) == 0)
                {
                    position = 0f;
                    rotation = quaternion.identity;
                }
                if (!BuildingHeight.TryMeasure(min, max, position, rotation, scale,
                    transform.m_Position, transform.m_Rotation, out var partHeight, out var partElevation))
                {
                    reason = "invalid active model bounds or transform";
                    return false;
                }
                bottom = math.min(bottom, partElevation);
                top = math.max(top, partElevation + partHeight);
            }
            height = top - bottom;
            elevation = bottom;
            if (math.isfinite(height)) return true;
            reason = "invalid combined active model bounds";
            return false;
        }
    }
}
