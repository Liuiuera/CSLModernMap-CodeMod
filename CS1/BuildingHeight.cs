namespace CSLModernMap
{
    internal static class BuildingHeight
    {
        internal static bool TryMeasure(float minY, float maxY, float positionY,
            out float height, out float elevation)
        {
            height = maxY - minY;
            elevation = positionY + minY;
            return !float.IsNaN(minY) && !float.IsInfinity(minY)
                && !float.IsNaN(maxY) && !float.IsInfinity(maxY)
                && !float.IsNaN(height) && !float.IsInfinity(height) && height >= 0f
                && !float.IsNaN(elevation) && !float.IsInfinity(elevation);
        }
    }
}
