using System;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;

namespace CSLModernMap.Systems
{
    /// <summary>写入地图数据文件</summary>
    internal static class CmmWriter
    {
        internal const int ArchiveVerificationProgress = 97;

        internal static void ValidateAndWrite(string path, string text, Action<int> progress,
            byte[] satellitePng = null)
        {
            if (!CmmJson.IsWellFormed(text, out var syntaxError))
            {
                var invalidPath = ExportPaths.BuildInvalidName(path);
                WritePlain(invalidPath, text);
                throw new InvalidDataException("导出结果不是合法JSON " + syntaxError
                    + "；坏文件保留在: " + invalidPath);
            }

            WriteAtomically(path, text, progress, satellitePng);
        }

        private static void WriteArchive(Stream file, string text, byte[] satellitePng, Action<int> progress)
        {
            using var zip = new ZipOutputStream(file) { IsStreamOwner = false };
            zip.SetLevel(6);
            // Unity Mono may lack the default Windows code page.
            zip.PutNextEntry(new ZipEntry("city.json") { IsUnicodeText = true });
            WriteText(zip, text, progress);
            zip.CloseEntry();
            if (satellitePng != null)
            {
                var crc = new Crc32();
                crc.Update(satellitePng);
                zip.PutNextEntry(new ZipEntry("satellite.png")
                {
                    IsUnicodeText = true,
                    CompressionMethod = CompressionMethod.Stored,
                    Size = satellitePng.Length,
                    CompressedSize = satellitePng.Length,
                    Crc = crc.Value,
                });
                zip.Write(satellitePng, 0, satellitePng.Length);
                zip.CloseEntry();
            }
            zip.Finish();
        }

        private static void WriteText(Stream stream, string text, Action<int> progress)
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            var buffer = new char[64 * 1024];
            for (var offset = 0; offset < text.Length;)
            {
                var count = Math.Min(buffer.Length, text.Length - offset);
                text.CopyTo(offset, buffer, 0, count);
                writer.Write(buffer, 0, count);
                offset += count;
                progress?.Invoke((int)(95L * offset / text.Length));
            }
        }

        private static void WriteAtomically(string path, string text, Action<int> progress, byte[] satellitePng)
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
                {
                    progress?.Invoke(0);
                    WriteArchive(file, text, satellitePng, progress);
                }

                progress?.Invoke(ArchiveVerificationProgress);
                using (var zip = new ZipFile(temporaryPath))
                {
                    if (zip.Count != (satellitePng == null ? 1 : 2) || zip.GetEntry("city.json") == null
                        || (satellitePng != null && zip.GetEntry("satellite.png")?.Size != satellitePng.Length)
                        || !zip.TestArchive(testData: true))
                        throw new InvalidDataException("The written CMM archive failed its integrity check.");
                }
                progress?.Invoke(99);
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
