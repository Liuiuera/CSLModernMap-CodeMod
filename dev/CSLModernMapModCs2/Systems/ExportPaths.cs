using System;
using System.Globalization;
using System.IO;
using Colossal.PSI.Environment;

namespace CSLModernMap.Systems
{
    /// <summary>管理导出文件路径</summary>
    internal static class ExportPaths
    {
        private const string ModsDataFolderName = "ModsData";

        private const string RootFolderName = "CSLModernMap";

        internal const string RendererFolderName = "Renderer";

        internal const string FileSuffix = ".cmm.gz";

        internal static string ExportDirectory
        {
            get
            {
                var root = EnvPath.kUserDataPath;
                return Path.Combine(root, ModsDataFolderName, RootFolderName);
            }
        }

        internal static string RendererDirectory
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(local, RootFolderName, RendererFolderName);
            }
        }

        internal static string EnsureExportDirectory()
        {
            var directory = ExportDirectory;
            Directory.CreateDirectory(directory);
            return directory;
        }

        internal static string BuildFileName(string cityName, DateTime localTime)
        {
            var chars = (cityName ?? "").Trim().ToCharArray();
            var invalid = Path.GetInvalidFileNameChars();
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i]) || Array.IndexOf(invalid, chars[i]) >= 0
                    || "<>:\"/\\|?*".IndexOf(chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            var safeName = new string(chars).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = "City";
            }

            return safeName + "-"
                + localTime.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
                + FileSuffix;
        }
        internal static string BuildInvalidName(string path)
        {
            if (path != null && path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - ".gz".Length);
            }

            return path + ".invalid";
        }

        internal static ExportFileInfo? FindNewestExport()
        {
            try
            {
                var directory = new DirectoryInfo(ExportDirectory);
                if (!directory.Exists)
                {
                    return null;
                }

                FileInfo newest = null;
                foreach (var file in directory.GetFiles("*" + FileSuffix))
                {
                    if (newest == null || file.LastWriteTimeUtc > newest.LastWriteTimeUtc)
                    {
                        newest = file;
                    }
                }

                if (newest == null)
                {
                    return null;
                }

                return new ExportFileInfo(newest.Name, newest.LastWriteTime);
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 无法读取导出目录: " + e.Message);
                return null;
            }
        }

        internal readonly struct ExportFileInfo
        {
            internal ExportFileInfo(string fileName, DateTime localTime)
            {
                FileName = fileName;
                LocalTime = localTime;
            }

            internal string FileName { get; }

            internal DateTime LocalTime { get; }
        }
    }
}
