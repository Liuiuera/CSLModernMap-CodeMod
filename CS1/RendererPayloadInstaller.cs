using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace CSLModernMap.UI
{
    /// <summary>渲染器安装进度回调；stageKey 是 Mod 翻译键。</summary>
    internal delegate void RendererInstallProgress(int percent, string stageKey);

    /// <summary>
    /// 按玩家点击安装随 Mod 分发的 renderer ZIP 载荷。刻意保持为单个轻量静态类：
    /// 不联网、不调用 shell、不在游戏启动阶段执行。
    /// </summary>
    internal static class RendererPayloadInstaller
    {
        internal const string PayloadFileName = "CSLModernMapRenderer.cslmr";
        internal const string PayloadHashFileName = "CSLModernMapRenderer.cslmr.sha256";

        private const string RendererExeName = "CSLModernMapRenderer.exe";
        private const string ManifestFileName = "renderer-manifest.sha256";
        private const string InstalledMarkerName = ".installed.sha256";
        private const int MaximumFileCount = 10000;
        private const long MaximumUncompressedBytes = 512L * 1024L * 1024L;
        private const int BufferSize = 128 * 1024;

        private sealed class ManifestEntry
        {
            internal string RelativePath;
            internal long Size;
            internal string Sha256;
        }

        /// <summary>确保当前版本 renderer 已安装，返回入口 EXE 的绝对路径。</summary>
        internal static string EnsureInstalled(
            string modDirectory, string rendererInstallRoot, RendererInstallProgress progress, bool forceReinstall = false)
        {
            Report(progress, 0, "RENDERER_PROGRESS_CHECKING");

            if (string.IsNullOrEmpty(modDirectory))
            {
                throw new DirectoryNotFoundException("Mod directory is unavailable.");
            }

            string payloadPath = Path.Combine(modDirectory, PayloadFileName);
            string hashPath = Path.Combine(modDirectory, PayloadHashFileName);
            if (!File.Exists(payloadPath) || !File.Exists(hashPath))
            {
                throw new FileNotFoundException("Renderer payload or SHA-256 file is missing.");
            }

            string expectedPayloadHash = ReadExpectedHash(hashPath);
            if (string.IsNullOrEmpty(rendererInstallRoot))
            {
                throw new DirectoryNotFoundException("Renderer install directory is unavailable.");
            }
            string installRoot = Path.GetFullPath(rendererInstallRoot);
            string versionDirectory = Path.Combine(installRoot, CSLModernMap.MOD_VERSION);
            string stagingDirectory = versionDirectory + ".staging";

            Directory.CreateDirectory(installRoot);
            if (!forceReinstall && IsReusable(versionDirectory, expectedPayloadHash))
            {
                Report(progress, 100, "RENDERER_PROGRESS_READY");
                return Path.Combine(versionDirectory, RendererExeName);
            }

            Report(progress, 5, "RENDERER_PROGRESS_HASHING");
            string actualPayloadHash = HashFile(payloadPath, progress, 5, 20);
            if (!string.Equals(actualPayloadHash, expectedPayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Renderer payload SHA-256 does not match.");
            }

            DeleteDirectory(stagingDirectory, true);
            Directory.CreateDirectory(stagingDirectory);

            try
            {
                ExtractAndVerify(payloadPath, stagingDirectory, progress);
                string stagedExe = Path.Combine(stagingDirectory, RendererExeName);
                if (!File.Exists(stagedExe))
                {
                    throw new InvalidDataException("Renderer entry executable is missing after extraction.");
                }

                File.WriteAllText(
                    Path.Combine(stagingDirectory, InstalledMarkerName),
                    expectedPayloadHash + Environment.NewLine);
                Report(progress, 97, "RENDERER_PROGRESS_ACTIVATING");
                Activate(stagingDirectory, versionDirectory);
                Report(progress, 100, "RENDERER_PROGRESS_READY");
                return Path.Combine(versionDirectory, RendererExeName);
            }
            catch
            {
                DeleteDirectory(stagingDirectory, false);
                throw;
            }
        }

        private static bool IsReusable(string directory, string expectedPayloadHash)
        {
            try
            {
                string marker = Path.Combine(directory, InstalledMarkerName);
                string manifest = Path.Combine(directory, ManifestFileName);
                string executable = Path.Combine(directory, RendererExeName);
                if (!Directory.Exists(directory) || !File.Exists(marker)
                    || !File.Exists(manifest) || !File.Exists(executable))
                {
                    return false;
                }

                if (!string.Equals(File.ReadAllText(marker).Trim(), expectedPayloadHash,
                                   StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                IList<ManifestEntry> entries = ParseManifest(File.ReadAllText(manifest));
                foreach (ManifestEntry entry in entries)
                {
                    string target = ResolveSafeTarget(directory, entry.RelativePath);
                    FileInfo info = new FileInfo(target);
                    if (!info.Exists || info.Length != entry.Size)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ExtractAndVerify(
            string payloadPath, string stagingDirectory, RendererInstallProgress progress)
        {
            // SharpZipLib 0.86 默认用 CP437 解码未标 UTF-8 的 ZIP 文件名；CS1 的 Mono
            // 运行时不提供该代码页，会在打开普通 ASCII ZIP 时直接抛 NotSupportedException。
            // 仅在构造 ZipFile（读取中央目录）的极短窗口改用 UTF-8，随后立即恢复全局值。
            ZipFile openedArchive;
            int previousCodePage = ZipConstants.DefaultCodePage;
            try
            {
                ZipConstants.DefaultCodePage = 65001;
                openedArchive = new ZipFile(payloadPath);
            }
            finally
            {
                ZipConstants.DefaultCodePage = previousCodePage;
            }

            using (ZipFile archive = openedArchive)
            {
                ZipEntry manifestZipEntry = archive.GetEntry(ManifestFileName);
                if (manifestZipEntry == null || !manifestZipEntry.IsFile)
                {
                    throw new InvalidDataException("Renderer manifest is missing from the payload.");
                }

                string manifestText;
                using (Stream manifestStream = archive.GetInputStream(manifestZipEntry))
                using (StreamReader reader = new StreamReader(manifestStream))
                {
                    manifestText = reader.ReadToEnd();
                }

                IList<ManifestEntry> manifestEntries = ParseManifest(manifestText);
                Dictionary<string, ManifestEntry> manifest = new Dictionary<string, ManifestEntry>(
                    StringComparer.OrdinalIgnoreCase);
                long totalBytes = 0;
                foreach (ManifestEntry item in manifestEntries)
                {
                    string normalized = NormalizeRelativePath(item.RelativePath);
                    if (string.Equals(normalized, ManifestFileName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(normalized, InstalledMarkerName,
                                         StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            "Renderer manifest contains a reserved installer path.");
                    }
                    if (manifest.ContainsKey(normalized))
                    {
                        throw new InvalidDataException("Renderer manifest contains duplicate paths.");
                    }
                    manifest.Add(normalized, item);
                    totalBytes = CheckedAdd(totalBytes, item.Size);
                }

                if (manifest.Count == 0 || manifest.Count > MaximumFileCount
                    || totalBytes > MaximumUncompressedBytes)
                {
                    throw new InvalidDataException("Renderer payload exceeds the extraction limits.");
                }

                Dictionary<string, ZipEntry> files = new Dictionary<string, ZipEntry>(
                    StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < archive.Count; ++index)
                {
                    ZipEntry entry = archive[index];
                    if (!entry.IsFile)
                    {
                        continue;
                    }
                    string normalized = NormalizeRelativePath(entry.Name);
                    ResolveSafeTarget(stagingDirectory, normalized);
                    if (string.Equals(normalized, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (entry.IsCrypted || !entry.CanDecompress || entry.Size < 0)
                    {
                        throw new InvalidDataException("Renderer payload contains an unsupported entry.");
                    }
                    if (files.ContainsKey(normalized))
                    {
                        throw new InvalidDataException("Renderer payload contains duplicate paths.");
                    }
                    files.Add(normalized, entry);
                }

                if (files.Count != manifest.Count)
                {
                    throw new InvalidDataException("Renderer payload does not match its manifest.");
                }
                foreach (KeyValuePair<string, ManifestEntry> pair in manifest)
                {
                    ZipEntry entry;
                    if (!files.TryGetValue(pair.Key, out entry) || entry.Size != pair.Value.Size)
                    {
                        throw new InvalidDataException("Renderer payload does not match its manifest.");
                    }
                }

                File.WriteAllText(Path.Combine(stagingDirectory, ManifestFileName), manifestText);
                Report(progress, 20, "RENDERER_PROGRESS_EXTRACTING");
                long completedBytes = 0;
                byte[] buffer = new byte[BufferSize];
                foreach (ManifestEntry item in manifestEntries)
                {
                    string normalized = NormalizeRelativePath(item.RelativePath);
                    ZipEntry zipEntry = files[normalized];
                    string targetPath = ResolveSafeTarget(stagingDirectory, normalized);
                    string parent = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }

                    using (Stream input = archive.GetInputStream(zipEntry))
                    using (FileStream output = new FileStream(
                        targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (SHA256 sha = new SHA256Managed())
                    {
                        int read;
                        long fileBytes = 0;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, buffer, 0);
                            fileBytes = CheckedAdd(fileBytes, read);
                            completedBytes = CheckedAdd(completedBytes, read);
                            int percent = totalBytes == 0
                                ? 95
                                : 20 + (int)(75L * completedBytes / totalBytes);
                            Report(progress, percent, "RENDERER_PROGRESS_EXTRACTING");
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        if (fileBytes != item.Size
                            || !string.Equals(ToHex(sha.Hash), item.Sha256,
                                              StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                "Extracted renderer file failed SHA-256 verification: " + normalized);
                        }
                    }
                }
            }
        }

        private static IList<ManifestEntry> ParseManifest(string text)
        {
            List<ManifestEntry> entries = new List<ManifestEntry>();
            using (StringReader reader = new StringReader(text ?? string.Empty))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }
                    string[] fields = line.Split(new[] { '\t' }, 3);
                    long size;
                    if (fields.Length != 3 || !IsSha256(fields[0])
                        || !long.TryParse(fields[1], NumberStyles.None,
                                          CultureInfo.InvariantCulture, out size)
                        || size < 0)
                    {
                        throw new InvalidDataException("Renderer manifest is invalid.");
                    }
                    entries.Add(new ManifestEntry
                    {
                        Sha256 = fields[0].ToUpperInvariant(),
                        Size = size,
                        RelativePath = NormalizeRelativePath(fields[2]),
                    });
                }
            }
            return entries;
        }

        private static string ReadExpectedHash(string path)
        {
            string body = File.ReadAllText(path).Trim();
            string[] fields = body.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 1 || !IsSha256(fields[0]))
            {
                throw new InvalidDataException("Renderer payload SHA-256 file is invalid.");
            }
            return fields[0].ToUpperInvariant();
        }

        private static string HashFile(
            string path, RendererInstallProgress progress, int startPercent, int endPercent)
        {
            FileInfo info = new FileInfo(path);
            long total = info.Length;
            long completed = 0;
            byte[] buffer = new byte[BufferSize];
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = new SHA256Managed())
            {
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, buffer, 0);
                    completed += read;
                    int percent = total == 0
                        ? endPercent
                        : startPercent + (int)((endPercent - startPercent) * completed / total);
                    Report(progress, percent, "RENDERER_PROGRESS_HASHING");
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return ToHex(sha.Hash);
            }
        }

        private static void Activate(string stagingDirectory, string versionDirectory)
        {
            string previousDirectory = versionDirectory + ".previous-" + Guid.NewGuid().ToString("N");
            bool movedOld = false;
            try
            {
                if (Directory.Exists(versionDirectory))
                {
                    Directory.Move(versionDirectory, previousDirectory);
                    movedOld = true;
                }
                Directory.Move(stagingDirectory, versionDirectory);
            }
            catch
            {
                if (movedOld && !Directory.Exists(versionDirectory)
                    && Directory.Exists(previousDirectory))
                {
                    try
                    {
                        Directory.Move(previousDirectory, versionDirectory);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
        }

        private static string ResolveSafeTarget(string root, string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath);
            string fullRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Path.Combine(fullRoot, normalized));
            if (!target.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Renderer payload entry escapes the install directory.");
            }
            return target;
        }

        private static string NormalizeRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0 || path.IndexOf(':') >= 0
                || Path.IsPathRooted(path) || path.StartsWith("\\", StringComparison.Ordinal)
                || path.StartsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Renderer payload contains an unsafe path.");
            }
            string normalized = path.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            string[] parts = normalized.Split(Path.DirectorySeparatorChar);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == "..")
                {
                    throw new InvalidDataException("Renderer payload contains an unsafe path.");
                }
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        private static long CheckedAdd(long left, long right)
        {
            if (right < 0 || left > MaximumUncompressedBytes - right)
            {
                throw new InvalidDataException("Renderer payload exceeds the extraction limits.");
            }
            return left + right;
        }

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", string.Empty);
        }

        private static void DeleteDirectory(string path, bool throwOnFailure)
        {
            if (!Directory.Exists(path))
            {
                return;
            }
            try
            {
                Directory.Delete(path, true);
            }
            catch
            {
                if (throwOnFailure)
                {
                    throw;
                }
            }
        }

        private static void Report(RendererInstallProgress progress, int percent, string stageKey)
        {
            if (progress != null)
            {
                progress(Math.Max(0, Math.Min(100, percent)), stageKey);
            }
        }
    }
}
