using System.IO;
using System.IO.Compression;
using System.Text;

namespace CSLModernMap.Systems
{
    internal static class CmmWriter
    {
        /// <summary>以UTF-8无BOM写入gzip；成功关闭后才替换为正式文件。</summary>
        internal static void WriteCompressed(string path, string text)
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
                    writer.Write(text);
                }

                File.Move(temporaryPath, path);
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
