using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using Game.Simulation;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CSLModernMap.Systems
{
    /// <summary>采集地形和水域数据</summary>
    internal static class TerrainExport
    {
        internal sealed class Result
        {
            internal string Json;
            internal string Issues = "[]";

            internal int IssueCount;

            internal string FirstIssue;

            internal float SeaLevel;
            internal int Rows;
            internal int Cols;
        }

        internal static Result Build(World world)
        {
            var issues = new CmmIssueCollector();

            var terrain = world.GetExistingSystemManaged<TerrainSystem>();
            if (terrain == null)
            {
                throw new InvalidDataException("TerrainSystem不存在");
            }

            var data = terrain.GetHeightData(true);
            var includeOutside = data.hasBackdrop && data.downscaledHeights.IsCreated
                && data.downscaledHeights.Length > 0;

            if (!data.isCreated || !data.heights.IsCreated || data.heights.Length == 0)
            {
                throw new InvalidDataException("TerrainHeightData不可用");
            }

            var srcCols = data.resolution.x;
            var srcRows = data.resolution.z;
            if ((long)srcCols * srcRows != data.heights.Length)
            {
                throw new InvalidDataException("TerrainHeightData的分辨率与高度数据长度不一致");
            }

            var srcCellX = data.scale.x != 0f ? 1f / data.scale.x : 0f;
            var srcCellZ = data.scale.z != 0f ? 1f / data.scale.z : 0f;
            if (srcCellX <= 0f || srcCellZ <= 0f || srcCols <= 0 || srcRows <= 0)
            {
                throw new InvalidDataException("TerrainHeightData的resolution/scale非法，无法推源网格世界范围");
            }

            var spanX = srcCols * srcCellX;
            var spanZ = srcRows * srcCellZ;
            var originX = -data.offset.x;
            var originZ = -data.offset.z;

            var cols = 1024;
            var rows = 1024;
            if (includeOutside)
            {
                var worldSize = terrain.worldSize;
                var worldOrigin = terrain.worldOffset;
                if (worldSize.x <= 0 || worldSize.y <= 0
                    || !math.all(math.isfinite(worldSize)) || !math.all(math.isfinite(worldOrigin)))
                    throw new InvalidDataException("Invalid surrounding terrain bounds.");
                cols = (int)Math.Round(1024d * worldSize.x / spanX);
                rows = (int)Math.Round(1024d * worldSize.y / spanZ);
                if (cols < 1 || rows < 1 || cols > 8192 || rows > 8192)
                    throw new InvalidDataException("Surrounding terrain grid is too large.");
                spanX = worldSize.x;
                spanZ = worldSize.y;
                originX = worldOrigin.x;
                originZ = worldOrigin.y;
            }
            var cellX = spanX / cols;
            var cellZ = spanZ / rows;
            var cell = Math.Min(cellX, cellZ);
            if (!(cell > 0f))
            {
                throw new InvalidDataException("目标格宽非正");
            }

            if (Math.Abs(cellX - cellZ) > 0.01f * cell)
            {
                issues.Warn("TERRAIN_GRID_NON_SQUARE",
                    "源网格X/Z世界跨度不一致（" + Num(spanX) + " vs " + Num(spanZ)
                    + " m），公共格宽取小者，另一方向覆盖会略短");
            }

            var heightValues = new float[rows * cols];
            try
            {
                for (var r = 0; r < rows; r++)
                {
                    var z = originZ + (r + 0.5f) * cell;
                    var rowBase = r * cols;
                    for (var c = 0; c < cols; c++)
                    {
                        var x = originX + (c + 0.5f) * cell;
                        heightValues[rowBase + c] = TerrainUtils.SampleHeight(ref data, new float3(x, 0f, z));
                    }
                }
            }
            catch (Exception e)
            {
                throw new InvalidDataException("TerrainUtils.SampleHeight采样失败", e);
            }

            var waterValues = new float[rows * cols];
            var water = world.GetExistingSystemManaged<WaterSystem>();
            if (water == null)
            {
                throw new InvalidDataException("WaterSystem不存在");
            }

            var seaLevel = water.SeaLevel;
            var surface = water.GetSurfaceData(out var deps);
            deps.Complete();

            if (!surface.depths.IsCreated)
            {
                throw new InvalidDataException("WaterSurfaceData不可用");
            }

            var wCols = surface.resolution.x;
            var wRows = surface.resolution.z;
            var wCellX = surface.scale.x != 0f ? 1f / surface.scale.x : 0f;
            var wCellZ = surface.scale.z != 0f ? 1f / surface.scale.z : 0f;
            if (wCols <= 0 || wRows <= 0 || wCellX <= 0f || wCellZ <= 0f
                || (long)wCols * wRows != surface.depths.Length)
            {
                throw new InvalidDataException("Water surface grid is invalid.");
            }

            ResampleWaterDepth(
                ref surface, rows, cols, originX, originZ, cell, waterValues);

            if (includeOutside)
            {
                var cityMin = terrain.playableOffset;
                var cityMax = cityMin + terrain.playableArea;
                for (var r = 0; r < rows; r++)
                {
                    var z = originZ + (r + .5f) * cell;
                    for (var c = 0; c < cols; c++)
                    {
                        var x = originX + (c + .5f) * cell;
                        if (x < cityMin.x || x >= cityMax.x || z < cityMin.y || z >= cityMax.y)
                            waterValues[r * cols + c] = Math.Max(0f, seaLevel - heightValues[r * cols + c]);
                    }
                }
            }

            var json = new StringBuilder(2 * 1024 * 1024);
            json.Append("{\"rows\": ").Append(rows.ToString(CultureInfo.InvariantCulture))
                .Append(", \"cols\": ").Append(cols.ToString(CultureInfo.InvariantCulture))
                .Append(", \"origin\": [").Append(Num(originX)).Append(", ").Append(Num(originZ)).Append("]")
                .Append(", \"cell\": ").Append(Num(cell))
                .Append(", \"source_subsampled\": ").Append(SubsampleFactor(srcCols, cols, srcRows, rows).ToString(CultureInfo.InvariantCulture))
                .AppendLine(",");
            AppendGrid(json, "height", heightValues, rows, cols, originX, originZ, cell);
            json.AppendLine(",");
            AppendGrid(json, "water_depth", waterValues, rows, cols, originX, originZ, cell);
            json.AppendLine();
            json.Append("      }");

            return new Result
            {
                Json = json.ToString(),
                Issues = issues.ToJson(),
                IssueCount = issues.Count,
                FirstIssue = issues.First,
                SeaLevel = seaLevel,
                Rows = rows,
                Cols = cols,
            };
        }

        private static int SubsampleFactor(int srcW, int cols, int srcH, int rows)
        {
            return Math.Max(1, Math.Max(srcW / cols, srcH / rows));
        }

        /// <summary>取目标网格覆盖范围内的最大水深以保留窄水域</summary>
        private static void ResampleWaterDepth(
            ref WaterSurfaceData<SurfaceWater> surface, int rows, int cols,
            float originX, float originZ, float cell, float[] output)
        {
            var depths = surface.depths;
            var wCols = surface.resolution.x;
            var wRows = surface.resolution.z;
            var wOriginX = -surface.offset.x;
            var wOriginZ = -surface.offset.z;
            var wCellX = 1f / surface.scale.x;
            var wCellZ = 1f / surface.scale.z;
            var wMaxX = wOriginX + wCols * wCellX;
            var wMaxZ = wOriginZ + wRows * wCellZ;

            for (var r = 0; r < rows; r++)
            {
                var z0 = originZ + r * cell;
                var z1 = z0 + cell;
                if (z1 <= wOriginZ || z0 >= wMaxZ)
                {
                    continue;
                }

                var i0 = Math.Max(0, (int)Math.Floor((z0 - wOriginZ) / wCellZ));
                var i1 = Math.Min(wRows, (int)Math.Ceiling((z1 - wOriginZ) / wCellZ));
                if (i1 <= i0)
                {
                    i1 = i0 + 1;
                }

                var rowBase = r * cols;
                for (var c = 0; c < cols; c++)
                {
                    var x0 = originX + c * cell;
                    var x1 = x0 + cell;
                    if (x1 <= wOriginX || x0 >= wMaxX)
                    {
                        continue;
                    }

                    var j0 = Math.Max(0, (int)Math.Floor((x0 - wOriginX) / wCellX));
                    var j1 = Math.Min(wCols, (int)Math.Ceiling((x1 - wOriginX) / wCellX));

                    var best = 0f;
                    for (var i = i0; i < i1; i++)
                    {
                        var srcRow = i * wCols;
                        for (var j = j0; j < j1; j++)
                        {
                            var d = depths[srcRow + j].m_Depth;
                            if (float.IsNaN(d) || float.IsInfinity(d) || d < 0f)
                            {
                                continue;
                            }

                            if (d > best)
                            {
                                best = d;
                            }
                        }
                    }

                    output[rowBase + c] = best;
                }
            }
        }

        private static void AppendGrid(
            StringBuilder json, string name, float[] values, int rows, int cols,
            float originX, float originZ, float cell)
        {
            json.Append("      \"").Append(name).Append("\": {")
                .Append("\"rows\": ").Append(rows.ToString(CultureInfo.InvariantCulture))
                .Append(", \"cols\": ").Append(cols.ToString(CultureInfo.InvariantCulture))
                .Append(", \"origin\": [").Append(Num(originX)).Append(", ").Append(Num(originZ)).Append("]")
                .Append(", \"cell\": ").Append(Num(cell))
                .Append(", \"axis\": \"rows=north;columns=east\"")
                .Append(", \"encoding\": \"base64+zlib+float32-row-major\"")
                .Append(", \"data\": \"");
            json.Append(Encode(values));
            json.Append("\"}");
        }

        /// <summary>将浮点网格封装为带校验值的压缩数据</summary>
        private static string Encode(float[] values)
        {
            var raw = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, raw, 0, raw.Length);

            using (var output = new MemoryStream(raw.Length / 3))
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                {
                    deflate.Write(raw, 0, raw.Length);
                }

                var adler = Adler32(raw);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return Convert.ToBase64String(output.ToArray());
            }
        }

        private static uint Adler32(byte[] data)
        {
            const uint mod = 65521;
            uint a = 1, b = 0;
            var index = 0;
            while (index < data.Length)
            {
                var block = Math.Min(5552, data.Length - index);
                for (var i = 0; i < block; i++)
                {
                    a += data[index + i];
                    b += a;
                }

                a %= mod;
                b %= mod;
                index += block;
            }

            return (b << 16) | a;
        }

        private static string Num(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }
    }
}
