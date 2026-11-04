using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    internal static class BuildingHeight
    {
        internal static bool TryMeasure(float3 min, float3 max,
            float3 meshPosition, quaternion meshRotation, float3 scale, float3 position, quaternion rotation,
            out float height, out float elevation)
        {
            height = 0f;
            elevation = 0f;
            if (!math.all(math.isfinite(min)) || !math.all(math.isfinite(max))
                || math.any(max < min)) return false;
            float bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            for (var i = 0; i < 8; i++)
            {
                var corner = new float3((i & 1) == 0 ? min.x : max.x,
                    (i & 2) == 0 ? min.y : max.y, (i & 4) == 0 ? min.z : max.z);
                var world = position + math.rotate(rotation, meshPosition + math.rotate(meshRotation, corner * scale));
                if (!math.all(math.isfinite(world))) return false;
                bottom = math.min(bottom, world.y);
                top = math.max(top, world.y);
            }
            height = top - bottom;
            elevation = bottom;
            return math.isfinite(height);
        }
    }
}
