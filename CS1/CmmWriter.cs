using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSLModernMap.Exporter
{
    internal static class CmmWriter
    {
        public static string Write(JObject document, string path, bool compress, ExportProgress progress = null, Dictionary<string, byte[]> satelliteLayers = null, int satelliteOutputSize = 0)
        {
            bool packaged = Path.GetExtension(path).Equals(".cmm", StringComparison.OrdinalIgnoreCase);
            if (satelliteLayers != null && (!packaged || satelliteOutputSize <= 0 || !satelliteLayers.ContainsKey("land") || !satelliteLayers.ContainsKey("water")))
                throw new ArgumentException("Satellite layers require a CMM archive, land/water images and positive output size");
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = File.Create(temporary))
                {
                    if (packaged) WriteArchive(document, file, satelliteLayers, satelliteOutputSize, progress);
                    else if (compress)
                        using (GZipStream gzip = new GZipStream(file, CompressionMode.Compress))
                        {
                            WriteJson(document, gzip, progress);
                        }
                    else WriteJson(document, file, progress);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                ExportStages.Report(progress, 100, "EXPORT_PROGRESS_READY");
                return path;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void Entry(ZipOutputStream archive, string name, byte[] bytes, bool stored)
        {
            // Unity's Mono does not ship CP437; use ZIP's per-entry UTF-8 flag.
            ZipEntry entry = new ZipEntry(name) { IsUnicodeText = true };
            entry.Size = bytes.Length;
            // The seekable temporary file lets SharpZipLib fill the CRC as it writes.
            if (stored) entry.CompressionMethod = CompressionMethod.Stored;
            archive.PutNextEntry(entry); archive.Write(bytes, 0, bytes.Length); archive.CloseEntry();
        }

        private static void WriteArchive(JObject document, Stream output, Dictionary<string, byte[]> satelliteLayers, int outputSize, ExportProgress progress)
        {
            document["schema_version"] = "cmm1.1";
            if (satelliteLayers != null)
            {
                JObject members = new JObject();
                foreach (string name in satelliteLayers.Keys) members[name] = "satellite/" + name + ".png";
                document["satellite"] = new JObject { { "world_bounds_xz", new JArray(-8640, -8640, 8640, 8640) },
                    { "output_size", outputSize }, { "layers", members } };
            }
            using (ZipOutputStream archive = new ZipOutputStream(output))
            {
                archive.SetLevel(6);
                Entry(archive, "city.json", Encoding.UTF8.GetBytes(document.ToString(Formatting.None)), false);
                ExportStages.Report(progress, satelliteLayers == null ? 98 : 88, "EXPORT_PROGRESS_WRITING");
                if (satelliteLayers != null)
                {
                    int written = 0;
                    foreach (var layer in satelliteLayers)
                    {
                        Entry(archive, "satellite/" + layer.Key + ".png", layer.Value, true);
                        ExportStages.Report(progress, 88 + 10 * ++written / satelliteLayers.Count, "EXPORT_PROGRESS_WRITING");
                    }
                }
                archive.Finish();
            }
        }

        private static void WriteJson(JObject document, Stream output, ExportProgress progress)
        {
            using (StreamWriter stream = new StreamWriter(output, new UTF8Encoding(false)))
            using (JsonTextWriter writer = new JsonTextWriter(stream))
            {
                writer.Culture = System.Globalization.CultureInfo.InvariantCulture;
                writer.Formatting = Formatting.None;
                writer.WriteStartObject();
                int count = Math.Max(1, document.Count);
                int written = 0;
                foreach (JProperty property in document.Properties())
                {
                    property.WriteTo(writer);
                    ExportStages.Report(progress, 80 + 18 * ++written / count, "EXPORT_PROGRESS_WRITING");
                }
                writer.WriteEndObject();
                writer.Flush();
            }
        }
    }
}
