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
        // Never expose a partially written map to the viewer. Read the entire
        // output before atomically replacing the previous successful file.
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
                ExportStages.Report(progress, 90, "EXPORT_PROGRESS_VALIDATING");
                using (FileStream file = File.OpenRead(temporary))
                {
                    if (packaged) CheckArchive(file, satelliteLayers);
                    else if (compress)
                        using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                        {
                            CheckJson(gzip);
                        }
                    else CheckJson(file);
                }
                ExportStages.Report(progress, 99, "EXPORT_PROGRESS_VALIDATING");
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
            if (stored)
            {
                entry.CompressionMethod = CompressionMethod.Stored;
                var crc = new ICSharpCode.SharpZipLib.Checksums.Crc32();
                crc.Update(bytes); entry.Crc = crc.Value;
            }
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
                ExportStages.Report(progress, 86, "EXPORT_PROGRESS_WRITING");
                if (satelliteLayers != null)
                {
                    int written = 0;
                    foreach (var layer in satelliteLayers)
                    {
                        Entry(archive, "satellite/" + layer.Key + ".png", layer.Value, true);
                        ExportStages.Report(progress, 86 + 3 * ++written / satelliteLayers.Count, "EXPORT_PROGRESS_WRITING");
                    }
                }
                archive.Finish();
            }
        }

        private static void CheckArchive(Stream input, Dictionary<string, byte[]> satelliteLayers)
        {
            HashSet<string> names = new HashSet<string>();
            var expected = new Dictionary<string, byte[]>();
            if (satelliteLayers != null)
                foreach (var layer in satelliteLayers) expected.Add("satellite/" + layer.Key + ".png", layer.Value);
            using (ZipInputStream archive = new ZipInputStream(input))
            {
                ZipEntry entry;
                while ((entry = archive.GetNextEntry()) != null)
                {
                    if (entry.IsDirectory || !names.Add(entry.Name)) throw new InvalidDataException("Invalid CMM member: " + entry.Name);
                    byte[] satellite;
                    if (expected.TryGetValue(entry.Name, out satellite))
                    {
                        if (entry.Size != satellite.Length) throw new InvalidDataException("Satellite size mismatch");
                        byte[] buffer = new byte[65536]; int count, offset = 0;
                        while ((count = archive.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (count > satellite.Length - offset) throw new InvalidDataException("Satellite size mismatch");
                            for (int i = 0; i < count; i++) if (buffer[i] != satellite[offset + i]) throw new InvalidDataException("Satellite bytes mismatch");
                            offset += count;
                        }
                        if (offset != satellite.Length) throw new InvalidDataException("Incomplete satellite image");
                    }
                    else if (entry.Name == "city.json")
                    {
                        using (MemoryStream bytes = new MemoryStream())
                        {
                            byte[] buffer = new byte[65536]; int count;
                            while ((count = archive.Read(buffer, 0, buffer.Length)) > 0) bytes.Write(buffer, 0, count);
                            bytes.Position = 0; CheckJson(bytes);
                        }
                    }
                    else throw new InvalidDataException("Unknown CMM member: " + entry.Name);
                }
            }
            if (!names.Contains("city.json") || names.Count != 1 + expected.Count)
                throw new InvalidDataException("Incomplete CMM archive");
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
                    ExportStages.Report(progress, 80 + 10 * ++written / count, "EXPORT_PROGRESS_WRITING");
                }
                writer.WriteEndObject();
                writer.Flush();
            }
        }

        private static void CheckJson(Stream input)
        {
            using (StreamReader stream = new StreamReader(input, new UTF8Encoding(false, true)))
            using (JsonTextReader reader = new JsonTextReader(stream))
            {
                // Stream the validation pass without allocating a second city DOM.
                reader.DateParseHandling = DateParseHandling.None;
                if (!reader.Read() || reader.TokenType != JsonToken.StartObject)
                    throw new InvalidDataException("CMM root must be an object");
                bool complete = false;
                while (reader.Read())
                {
                    if (complete) throw new InvalidDataException("Extra CMM content");
                    complete = reader.TokenType == JsonToken.EndObject && reader.Depth == 0;
                }
                if (!complete) throw new InvalidDataException("Incomplete CMM JSON");
            }
        }
    }
}
