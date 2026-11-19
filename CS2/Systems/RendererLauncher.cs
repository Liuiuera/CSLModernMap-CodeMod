using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CSLModernMap.Systems
{
    internal enum RendererInstallationStatus
    {
        Waiting, Unsupported, MissingPayload, Manual, Installed, Older
    }

    /// <summary>安装并启动地图查看器</summary>
    internal static class RendererLauncher
    {
        internal const string PayloadFileName = "CSLModernMapRenderer.zip";
        internal const string PayloadHashFileName = "CSLModernMapRenderer.zip.sha256";
        internal const string PayloadVersionFileName = "renderer-version.txt";

        private const string RendererExeName = "CSLModernMapRenderer.exe";

        private static string s_ModDirectory = "";
        internal static RendererInstallationStatus InstallationStatus { get; private set; }
        internal static string StatusVersion { get; private set; } = "";

        internal static void Initialize(string modDirectory)
        {
            s_ModDirectory = modDirectory ?? "";
            RefreshInstallationStatus();
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
                    && version != "."
                    && version != ".."
                    && version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
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

        // LocalLow executables inherit Low integrity and cannot host the central preview reliably.
        internal static string ManualExecutable => RendererManualInstall.FindExecutable(ExportPaths.RendererDirectory, PayloadVersion);

        internal static bool HasManualInstallation => ManualExecutable != null;

        internal static bool IsInstalled => HasManualInstallation
            || (File.Exists(InstalledExecutable)
                && File.Exists(Path.Combine(InstalledDirectory, "engine", "CSLModernMapEngine.exe")));

        internal static bool RefreshInstallationStatus()
        {
            StatusVersion = PayloadVersion;
            InstallationStatus = !IsSupported ? RendererInstallationStatus.Unsupported
                : HasManualInstallation ? RendererInstallationStatus.Manual
                : !HasPayload ? RendererInstallationStatus.MissingPayload
                : IsInstalled ? RendererInstallationStatus.Installed
                : HasOlderInstallation ? RendererInstallationStatus.Older
                : RendererInstallationStatus.Waiting;
            return InstallationStatus == RendererInstallationStatus.Manual
                || InstallationStatus == RendererInstallationStatus.Installed;
        }

        internal static bool HasOlderInstallation
        {
            get
            {
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(ExportPaths.RendererDirectory))
                    {
                        if (!string.Equals(directory, InstalledDirectory, StringComparison.OrdinalIgnoreCase)
                            && File.Exists(Path.Combine(directory, RendererExeName)))
                        {
                            return true;
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                return false;
            }
        }

        /// <summary>安装时校验查看器载荷</summary>
        internal static string EnsureInstalled(bool reinstall = false)
        {
            if (!IsSupported)
            {
                throw new InvalidOperationException("Renderer installation is only supported on Windows.");
            }

            if (HasManualInstallation)
            {
                return ManualExecutable;
            }

            if (!HasPayload)
            {
                throw new FileNotFoundException("Renderer payload, hash or version file is missing.");
            }

            if (!reinstall && IsInstalled)
            {
                return InstalledExecutable;
            }

            var expectedHash = ReadExpectedPayloadHash();
            var actualHash = HashFile(PayloadPath);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Renderer payload SHA-256 does not match.");
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var rendererRoot = Path.GetFullPath(ExportPaths.RendererDirectory);
            if (string.IsNullOrEmpty(localAppData)
                || !Path.IsPathRooted(localAppData)
                || !string.Equals(rendererRoot,
                    Path.GetFullPath(Path.Combine(localAppData, "CSLModernMap", "Renderer")),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Renderer install directory is invalid.");
            }

            if (Directory.Exists(rendererRoot))
            {
                if ((File.GetAttributes(rendererRoot) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Renderer install directory is a link.");
                }

                Directory.Delete(rendererRoot, true);
            }

            Directory.CreateDirectory(InstalledDirectory);
            Extract(PayloadPath, InstalledDirectory);

            if (!File.Exists(InstalledExecutable))
            {
                throw new InvalidDataException("Renderer entry executable is missing after extraction.");
            }

            return InstalledExecutable;
        }

        internal static void Launch(string exportPath)
        {
            if (!IsInstalled)
            {
                throw new FileNotFoundException("Renderer is not installed.", InstalledExecutable);
            }

            var executable = HasManualInstallation ? ManualExecutable : InstalledExecutable;
            var workingDirectory = ExportPaths.ExportDirectory;
            try
            {
                var processId = RendererDetachedProcess.Start(
                    executable, string.IsNullOrEmpty(exportPath) ? "" : "\"" + exportPath + "\"", workingDirectory);
                Mod.log.Info("[renderer] 查看器进程已创建，PID=" + processId);
            }
            catch (Exception error)
            {
                Mod.log.Error("[renderer] " + RendererLaunchDiagnostics.Failure("RendererDetachedProcess.Start", error)
                    + "; " + RendererLaunchDiagnostics.Context(executable, PayloadVersion, workingDirectory));
                throw;
            }
        }

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
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = directory,
                        UseShellExecute = true,
                    })?.Dispose();
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

        internal static void OpenRendererDirectory()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = HasManualInstallation ? Path.GetDirectoryName(ManualExecutable) : ExportPaths.RendererDirectory,
                    UseShellExecute = true,
                    ErrorDialog = true,
                })?.Dispose();
            }
            catch (Exception e)
            {
                Mod.log.Warn("[renderer] 无法打开渲染器目录: " + e.Message);
            }
        }

        internal static string PrepareManualInstallation()
        {
            return RendererManualInstall.PrepareDirectory(PayloadPath, ExportPaths.RendererDirectory);
        }

        internal static void OpenInstallHelp(string directory, string error)
        {
            RendererManualInstall.OpenHelp(PayloadPath, directory, "CS2", PayloadVersion, error);
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
                string folder = RendererManualInstall.VersionedFolderName(PayloadVersion);
                if (archive.GetEntry(folder + "/renderer-manifest.sha256") == null)
                    folder = archive.GetEntry(RendererManualInstall.FolderName + "/renderer-manifest.sha256") != null
                        ? RendererManualInstall.FolderName : "";
                foreach (var entry in archive.Entries)
                {
                    if (entry.Name.Length == 0)
                    {
                        continue;
                    }

                    var relativePath = RendererManualInstall.PayloadRelativePath(entry.FullName, folder);
                    var target = Path.GetFullPath(Path.Combine(root, relativePath));
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
