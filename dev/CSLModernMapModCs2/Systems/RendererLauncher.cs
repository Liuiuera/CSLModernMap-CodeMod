using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CSLModernMap.Systems
{
    /// <summary>安装并启动随 Mod 分发的查看器。</summary>
    internal static class RendererLauncher
    {
        internal const string PayloadFileName = "CSLModernMapRenderer.cslmr";
        internal const string PayloadHashFileName = "CSLModernMapRenderer.cslmr.sha256";
        internal const string PayloadVersionFileName = "renderer-version.txt";

        private const string RendererExeName = "CSLModernMapRenderer.exe";

        private static string s_ModDirectory = "";

        internal static void Initialize(string modDirectory)
        {
            s_ModDirectory = modDirectory ?? "";
        }

        internal static bool IsSupported =>
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        internal static string PayloadPath => Combine(PayloadFileName);

        internal static string PayloadHashPath => Combine(PayloadHashFileName);

        internal static string PayloadVersionPath => Combine(PayloadVersionFileName);

        internal static string PayloadVersion
        {
            get
            {
                if (!File.Exists(PayloadVersionPath))
                {
                    return "";
                }

                var version = File.ReadAllText(PayloadVersionPath, Encoding.UTF8).Trim();
                return version.Length > 0
                    && version.Length <= 32
                    && string.Equals(Path.GetFileName(version), version, StringComparison.Ordinal)
                        ? version
                        : "";
            }
        }

        internal static bool HasPayload =>
            File.Exists(PayloadPath)
            && File.Exists(PayloadHashPath)
            && !string.IsNullOrEmpty(PayloadVersion);

        internal static string InstalledDirectory =>
            string.IsNullOrEmpty(PayloadVersion)
                ? ""
                : Path.Combine(ExportPaths.RendererDirectory, PayloadVersion);

        internal static string InstalledExecutable =>
            string.IsNullOrEmpty(InstalledDirectory)
                ? ""
                : Path.Combine(InstalledDirectory, RendererExeName);

        internal static bool IsInstalled => File.Exists(InstalledExecutable);

        internal static string EnsureInstalled()
        {
            if (!IsSupported)
            {
                throw new InvalidOperationException("Renderer installation is only supported on Windows.");
            }

            if (!HasPayload)
            {
                throw new FileNotFoundException("Renderer payload, hash or version file is missing.");
            }

            if (IsInstalled)
            {
                return InstalledExecutable;
            }

            var expectedHash = ReadExpectedPayloadHash();
            var actualHash = HashFile(PayloadPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Renderer payload SHA-256 does not match.");
            }

            Directory.CreateDirectory(ExportPaths.RendererDirectory);
            if (Directory.Exists(InstalledDirectory))
            {
                Directory.Delete(InstalledDirectory, true);
            }

            Directory.CreateDirectory(InstalledDirectory);
            Extract(PayloadPath, InstalledDirectory);

            if (!File.Exists(InstalledExecutable))
            {
                throw new InvalidDataException("Renderer entry executable is missing after extraction.");
            }

            Mod.log.Info("[renderer] 查看器已就绪: " + InstalledDirectory);
            return InstalledExecutable;
        }

        internal static void Launch(string exportPath)
        {
            if (!IsInstalled)
            {
                throw new FileNotFoundException("Renderer is not installed.", InstalledExecutable);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = InstalledExecutable,
                Arguments = "\"" + exportPath + "\"",
                WorkingDirectory = ExportPaths.ExportDirectory,
                UseShellExecute = false,
            });
            Mod.log.Info("[renderer] 已启动查看器: " + InstalledExecutable + " \"" + exportPath + "\"");
        }

        /// <summary>用系统文件管理器打开目录；目录不存在或系统调用被拒返回 false。</summary>
        internal static bool TryOpenInFileManager(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(directory);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start("explorer", directory);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", directory);
                }
                else
                {
                    Process.Start("xdg-open", directory);
                }

                return true;
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 无法打开导出目录: " + e.Message);
                return false;
            }
        }

        private static string Combine(string fileName) =>
            string.IsNullOrEmpty(s_ModDirectory) ? "" : Path.Combine(s_ModDirectory, fileName);

        private static string ReadExpectedPayloadHash()
        {
            var fields = File.ReadAllText(PayloadHashPath, Encoding.UTF8).Split(
                (char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || !IsSha256(fields[0]))
            {
                throw new InvalidDataException("Renderer payload SHA-256 file is invalid.");
            }

            return fields[0];
        }

        private static void Extract(string payloadPath, string destination)
        {
            var root = Path.GetFullPath(destination).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            using (var stream = File.OpenRead(payloadPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0)
                    {
                        continue;
                    }

                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Renderer payload entry escapes the install directory.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Open())
                    using (var output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static string HashFile(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
