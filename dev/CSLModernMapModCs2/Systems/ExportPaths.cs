using System;
using System.Globalization;
using System.IO;
using Colossal.PSI.Environment;

namespace CSLModernMap.Systems
{
    internal static class ExportPaths
    {
        private const string ModsDataFolderName = "ModsData";

        private const string RootFolderName = "CSLModernMap";

        internal const string RendererFolderName = "Renderer";

        internal const string FilePrefix = "CSLModernMap-CS2-";

        internal const string FileSuffix = ".cmm.gz";

        internal static string ExportDirectory
        {
            get
            {
                var root = EnvPath.kUserDataPath;
                return Path.Combine(root, ModsDataFolderName, RootFolderName);
            }
        }

        /// <summary>
        /// 查看器安装目录。不能放在LocalLow：其低完整性标签会阻止CLR加载查看器程序集。
        /// </summary>
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

        internal static string BuildFileName(DateTime localTime)
        {
            return FilePrefix
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

        /// <summary>目录里最新的一份成功导出，用于恢复状态栏；任何异常吞掉返回null。</summary>
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
