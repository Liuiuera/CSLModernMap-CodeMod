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
    /// <summary>
    /// 将游戏地形和水深重采样到CMM的公共网格；水深不可用时保留地形并输出提示
    /// </summary>
    internal static class TerrainExport
    {
        internal sealed class Result
        {
            internal string Json = "{}";
            /// <summary>完整的JSON数组（含方括号），写进顶层 <c>issues</c>。</summary>
            internal string Issues = "[]";

            /// <summary>issue总数（error + warn）；文件写出来了但这份数据不完整时 &gt; 0。</summary>
            internal int IssueCount;

            internal int IssueErrorCount;

            internal string FirstIssue = "";
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
                return Failure(issues, "terrain-system-missing", "TerrainSystem不存在");
            }

            var data = terrain.GetHeightData(false);
            if (!data.isCreated)
            {
                data = terrain.GetHeightData(true);
            }

            if (!data.isCreated || !data.heights.IsCreated || data.heights.Length == 0)
            {
                return Failure(issues, "height-data-unavailable", "TerrainHeightData不可用");
            }

            // 地形源网格：宽resolution.x，高resolution.z。
            var srcCols = data.resolution.x;
            var srcRows = data.resolution.z;
            if ((long)srcCols * srcRows != data.heights.Length)
            {
                if ((long)data.resolution.y * srcRows == data.heights.Length)
                {
                    srcCols = data.resolution.y;
                }
                else if ((long)srcCols * data.resolution.y == data.heights.Length)
                {
                    srcRows = data.resolution.y;
                }
                else
                {
                    srcCols = data.heights.Length;
                    srcRows = 1;
                }
            }

            var srcCellX = data.scale.x != 0f ? 1f / data.scale.x : 0f;
            var srcCellZ = data.scale.z != 0f ? 1f / data.scale.z : 0f;
            if (srcCellX <= 0f || srcCellZ <= 0f || srcCols <= 0 || srcRows <= 0)
            {
                return Failure(issues, "invalid-terrain-grid",
                    "TerrainHeightData的resolution/scale非法，无法推源网格世界范围");
            }

            // 目标公共网格：范围 = 地形源网格的世界范围（world = index/scale - offset）
            var spanX = srcCols * srcCellX;
            var spanZ = srcRows * srcCellZ;
            var originX = -data.offset.x;
            var originZ = -data.offset.z;

            var cols = 1024;
            var rows = 1024;
            var cellX = spanX / cols;
            var cellZ = spanZ / rows;
            var cell = Math.Min(cellX, cellZ);
            if (!(cell > 0f))
            {
                return Failure(issues, "invalid-target-cell", "目标格宽非正");
            }

            if (Math.Abs(cellX - cellZ) > 0.01f * cell)
            {
                issues.Warn("TERRAIN_GRID_NON_SQUARE",
                    "源网格X/Z世界跨度不一致（" + Num(spanX) + " vs " + Num(spanZ)
                    + " m），公共格宽取小者，另一方向覆盖会略短");
            }

            // 高度：目标格中心按世界坐标采样（行 += 北，列 += 东）
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
                Mod.log.Warn("[terrain] 高度采样失败: " + e.GetType().Name + ": " + e.Message);
                issues.Error("HEIGHT_SAMPLE_FAILED",
                    "TerrainUtils.SampleHeight抛异常（" + e.GetType().Name + "），height为已采样部分/零值");
            }

            // 水深：CPU侧GetSurfaceData，按水网格自己的origin/cell求交取覆盖最大值
            var waterValues = new float[rows * cols];
            var water = world.GetExistingSystemManaged<WaterSystem>();
            var seaLevel = 0f;

            if (water != null)
            {
                seaLevel = water.SeaLevel;

                var surface = water.GetSurfaceData(out var deps);
                deps.Complete();

                if (surface.depths.IsCreated)
                {
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
                IssueErrorCount = issues.ErrorCount,
                FirstIssue = issues.First,
                SeaLevel = seaLevel,
                Rows = rows,
                Cols = cols,
            };
        }

        private static Result Failure(CmmIssueCollector issues, string reason, string message)
        {
            issues.Error("TERRAIN_UNAVAILABLE", message + "（" + reason + "）");
            return new Result
            {
                Json = "{}",
                Issues = issues.ToJson(),
                IssueCount = issues.Count,
                IssueErrorCount = issues.ErrorCount,
                FirstIssue = issues.First,
            };
        }

        private static int SubsampleFactor(int srcW, int cols, int srcH, int rows)
        {
            return Math.Max(1, Math.Max(srcW / cols, srcH / rows));
        }

        /// <summary>
        /// 把水网格的 <c>m_Depth</c> 投影到CMM公共网格：每个目标格取覆盖的源像元最大值
        /// （平均会把窄河道压到水域阈值以下，轮廓就断了）。下标 = z行 × resolution.x + x列，
        /// 世界坐标 = index/scale - offset（行0 = 最小Z）。
        /// </summary>
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

        /// <summary>float32小端row-major → zlib → base64，与CMM<c>Grid</c>的编码一致。</summary>
        private static string Encode(float[] values)
        {
            var raw = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, raw, 0, raw.Length);

            using (var output = new MemoryStream(raw.Length / 3))
            {
                // zlib = 2字节头 + raw deflate + adler32。.NET Framework只有raw deflate，
                // 头尾自己补（CMM侧用zlib.decompress解，两者必须严丝合缝）。
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
                // 5552是zlib文档给的「不取模也不会溢出」的分块长度。
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
