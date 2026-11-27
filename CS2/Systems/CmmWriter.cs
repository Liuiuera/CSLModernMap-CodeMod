using System;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace CSLModernMap.Systems
{
    internal static class CmmWriter
    {
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
                zip.PutNextEntry(new ZipEntry("satellite.png")
                {
                    IsUnicodeText = true,
                    CompressionMethod = CompressionMethod.Stored,
                    Size = satellitePng.Length,
                    CompressedSize = satellitePng.Length,
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
                progress?.Invoke((int)(99L * offset / text.Length));
            }
        }

        internal static void Write(string path, string text, Action<int> progress, byte[] satellitePng = null)
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
    }
}
