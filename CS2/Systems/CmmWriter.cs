using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CSLModernMap.Systems
{
    /// <summary>写入地图数据文件</summary>
    internal static class CmmWriter
    {
        internal static void ValidateAndWrite(string path, string text, Action<int> progress)
        {
            if (!CmmJson.IsWellFormed(text, out var syntaxError))
            {
                var invalidPath = ExportPaths.BuildInvalidName(path);
                WritePlain(invalidPath, text);
                throw new InvalidDataException("导出结果不是合法JSON " + syntaxError
                    + "；坏文件保留在: " + invalidPath);
            }

            WriteCompressed(path, text, progress);
        }

        internal static void WriteCompressed(string path, string text, Action<int> progress = null)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidDataException("Export path has no parent directory.");
            }

            var temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(path) + "." + System.Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using (var file = new FileStream(
                    temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var gz = new GZipStream(file, CompressionMode.Compress))
                using (var writer = new StreamWriter(gz, new UTF8Encoding(false)))
                {
                    const int chunkSize = 64 * 1024;
                    var buffer = new char[chunkSize];
                    progress?.Invoke(0);
                    for (var offset = 0; offset < text.Length;)
                    {
                        var count = Math.Min(buffer.Length, text.Length - offset);
                        text.CopyTo(offset, buffer, 0, count);
                        writer.Write(buffer, 0, count);
                        offset += count;
                        // 关闭 gzip 并提交文件前，进度最多为 99%。
                        progress?.Invoke((int)(99L * offset / text.Length));
                    }
                }

                File.Move(temporaryPath, path);
                progress?.Invoke(100);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        internal static void WritePlain(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
    }
}
