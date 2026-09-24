using System;

namespace CSLModernMap.Systems
{
    /// <summary>计算交通状态的统计值</summary>
    internal static class TrafficSnapshotMath
    {
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static float[] Weights(float normalizedTime)
        {
            if (!Finite(normalizedTime)) throw new ArgumentOutOfRangeException(nameof(normalizedTime));
            float h = (normalizedTime - (float)Math.Floor(normalizedTime)) * 4f;
            return new[] { Clamp(Math.Max(h - 3f, 1f - h)), Clamp(1f - Math.Abs(h - 1f)),
                Clamp(1f - Math.Abs(h - 2f)), Clamp(1f - Math.Abs(h - 3f)) };
        }

        private static float Clamp(float value) => Math.Max(0f, Math.Min(1f, value));

        internal static float Flow(float[] duration, float[] distance, float[] weights)
        {
            float result = 0;
            for (int i = 0; i < 4; i++)
            {
                if (weights[i] <= 0) continue;
                if (!Finite(duration[i]) || !Finite(distance[i]) || duration[i] <= 0 || distance[i] < 0)
                    return -1;
                result += weights[i] * (float)Math.Min(1d, (double)distance[i] / duration[i]);
            }
            return Clamp(result);
        }

        internal static float Volume(float[] duration, float[] distance, float[] weights)
        {
            if (Flow(duration, distance, weights) < 0) return -1;
            double result = 0;
            for (int i = 0; i < 4; i++)
                if (weights[i] > 0) result += weights[i] * (double)distance[i] * 8d / 3d;
            return result <= float.MaxValue ? (float)result : -1;
        }
    }
}
