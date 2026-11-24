using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CSLModernMap
{
    internal enum ExportGame { CS1, CS2 }

    internal static class GameExportDirectories
    {
        internal static string RegistryDirectory => Path.Combine(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CSLModernMap"),
            "ExportDirectories");

        internal static void Register(string registry, ExportGame game, string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(registry);
            string entry = EntryPath(registry, game);
            string temporary = Path.Combine(registry, Path.GetRandomFileName());
            try
            {
                File.WriteAllText(temporary, fullPath);
                // A reader must see either the previous directory or the complete new one.
                if (File.Exists(entry)) File.Replace(temporary, entry, null);
                else File.Move(temporary, entry);
            }
            finally { File.Delete(temporary); }
        }

        internal static IEnumerable<FileInfo> EnumerateMaps(string directory)
        {
            return new DirectoryInfo(directory).GetFiles().Where(file =>
                file.Name.EndsWith(".cmm", StringComparison.OrdinalIgnoreCase)
                || file.Name.EndsWith(".cmm.gz", StringComparison.OrdinalIgnoreCase));
        }

        internal static IEnumerable<FileInfo> EnumerateRegisteredMaps(string registry, Action<string> log)
        {
            foreach (ExportGame game in Enum.GetValues(typeof(ExportGame)))
            {
                IEnumerable<FileInfo> maps;
                try
                {
                    string entry = EntryPath(registry, game);
                    if (!File.Exists(entry)) continue;
                    string directory;
                    using (var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                    using (var reader = new StreamReader(stream))
                        directory = reader.ReadToEnd();

                    if (directory.Length == 0 || !Path.IsPathRooted(directory)
                        || !string.Equals(Path.GetFullPath(directory), directory, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Invalid export directory: " + entry);
                    maps = EnumerateMaps(directory).ToArray();
                }
                catch (DirectoryNotFoundException) { continue; }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException
                    || error is ArgumentException || error is InvalidDataException)
                {
                    log("Cannot read " + game + " export directory: " + error.Message);
                    continue;
                }

                foreach (FileInfo map in maps) yield return map;
            }
        }

        private static string EntryPath(string registry, ExportGame game) => Path.Combine(registry, game + ".txt");
    }
}
