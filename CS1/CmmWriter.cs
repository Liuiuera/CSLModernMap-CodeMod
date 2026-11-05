using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSLModernMap.Exporter
{
    internal static class CmmWriter
    {
        // Never expose a partially written map to the viewer. Read the entire
        // compressed JSON before atomically replacing the previous successful file.
        public static string Write(JObject document, string path, bool compress, ExportProgress progress = null)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream file = File.Create(temporary))
                {
                    if (compress)
                        using (GZipStream gzip = new GZipStream(file, CompressionMode.Compress)) WriteJson(document, gzip, progress);
                    else WriteJson(document, file, progress);
                }
                ExportStages.Report(progress, 90, "EXPORT_PROGRESS_VALIDATING");
                using (FileStream file = File.OpenRead(temporary))
                {
                    if (compress)
                        using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress)) CheckJson(gzip);
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
