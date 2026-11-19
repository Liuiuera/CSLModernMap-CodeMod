using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace CSLModernMap
{
    internal static class RendererManualInstall
    {
        internal const string HelpFileName = "CSLModernMapRenderer-help.html";
        internal const string FolderName = "CSLModernMapRenderer";
        private const string HelpContextMarker = "<!--CSLMODERNMAP_INSTALL_CONTEXT-->";

        internal static string VersionedFolderName(string version)
        {
            return FolderName + "-" + version;
        }

        internal static string FindExecutable(string directory, string version)
        {
            string current = Path.Combine(directory, VersionedFolderName(version));
            if (Directory.Exists(current)) return FindInDirectory(current);
            return FindInDirectory(directory) ?? FindInDirectory(Path.Combine(directory, FolderName));
        }

        private static string FindInDirectory(string directory)
        {
            string executable = Path.Combine(directory, "CSLModernMapRenderer.exe");
            return File.Exists(executable)
                && File.Exists(Path.Combine(Path.Combine(directory, "engine"), "CSLModernMapEngine.exe"))
                    ? executable : null;
        }

        internal static string PayloadRelativePath(string entryName, string folder)
        {
            if (folder.Length == 0) return entryName;
            string prefix = folder + "/";
            if (!entryName.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("Renderer payload entry is outside its folder.");
            return entryName.Substring(prefix.Length);
        }

        internal static string PrepareDirectory(string payloadPath, string installationDirectory, string exportDirectory)
        {
            Exception installationError;
            if (TryCopy(payloadPath, installationDirectory, out installationError)) return installationDirectory;
            Exception exportError;
            if (TryCopy(payloadPath, exportDirectory, out exportError)) return exportDirectory;
            throw new IOException("Cannot prepare manual renderer installation.\n"
                + installationDirectory + ": " + installationError + "\n"
                + exportDirectory + ": " + exportError, exportError);
        }

        internal static string PrepareDirectory(string payloadPath, string installationDirectory)
        {
            if (!File.Exists(payloadPath)) throw new FileNotFoundException("Renderer ZIP is missing.", payloadPath);
            Directory.CreateDirectory(installationDirectory);
            CopyFile(payloadPath, installationDirectory);
            CopyFile(Path.Combine(Path.GetDirectoryName(payloadPath), HelpFileName), installationDirectory);
            return installationDirectory;
        }

        private static bool TryCopy(string payloadPath, string directory, out Exception failure)
        {
            try
            {
                PrepareDirectory(payloadPath, directory);
                failure = null;
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is SecurityException)
            {
                failure = error;
                return false;
            }
        }

        private static void CopyFile(string source, string directory)
        {
            string destination = Path.Combine(directory, Path.GetFileName(source));
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, destination, true);
        }

        internal static string PrepareHelp(string payloadPath, string installationDirectory,
            string game, string version, string error)
        {
            string help = Path.Combine(Path.GetDirectoryName(payloadPath), HelpFileName);
            string template = File.ReadAllText(help, Encoding.UTF8);
            if (!template.Contains(HelpContextMarker))
                throw new InvalidDataException("Renderer installation help template is outdated: " + help);
            string context = "source=" + Uri.EscapeDataString(Path.GetDirectoryName(payloadPath))
                + "&destination=" + Uri.EscapeDataString(installationDirectory)
                + "&game=" + Uri.EscapeDataString(game)
                + "&version=" + Uri.EscapeDataString(version)
                + "&error=" + Uri.EscapeDataString(error ?? "");
            string directory = Path.Combine(Path.GetTempPath(), "CSLModernMap");
            Directory.CreateDirectory(directory);
            string page = Path.Combine(directory, "renderer-install-" + game + "-" + Guid.NewGuid().ToString("N") + ".html");
            // Windows may discard a local file URL's fragment when opening the default browser.
            string metadata = "<meta name=\"cslmodernmap-install-context\" content=\"" + context.Replace("&", "&amp;") + "\">";
            File.WriteAllText(page, template.Replace(HelpContextMarker, metadata), Encoding.UTF8);
            return page;
        }

        internal static void OpenHelp(string payloadPath, string installationDirectory,
            string game, string version, string error)
        {
            Process.Start(new ProcessStartInfo(PrepareHelp(payloadPath, installationDirectory, game, version, error))
                { UseShellExecute = true });
        }
    }
}
