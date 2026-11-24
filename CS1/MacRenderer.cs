using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml;

namespace CSLModernMap.UI
{
    internal sealed class MacRenderer
    {
        internal readonly string PathName;
        internal readonly string DisplayVersion;
        private readonly Version version;

        private MacRenderer(string app, string display)
        {
            PathName = app;
            DisplayVersion = display;
            version = ParseVersion(display);
        }

        // CMM releases use both Apple's numeric revision and the display suffix, e.g. 6.7.2f4m.
        internal static Version ParseVersion(string value)
        {
            Match match = Regex.Match(value.Trim(), @"^([0-9]+\.[0-9]+\.[0-9]+)(?:(?:\.|f)([0-9]+)|f)?m?$",
                RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string revision = match.Groups[2].Success ? match.Groups[2].Value
                : value.IndexOf("f", StringComparison.OrdinalIgnoreCase) >= 0 ? "1" : "0";
            try { return new Version(match.Groups[1].Value + "." + revision); }
            catch (OverflowException) { return null; }
        }

        internal bool NeedsUpdate(string required)
        {
            return version == null || version < ParseVersion(required);
        }

        internal static MacRenderer Find(string outputDirectory)
        {
            MacRenderer newest = null;
            string userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Applications");
            foreach (string directory in new[] { "/Applications", userApps, outputDirectory })
            {
                if (string.IsNullOrEmpty(directory)) continue;
                foreach (string name in new[] { "CSLModernMap.app", "CSLModernMap Renderer.app" })
                {
                    string app = Path.Combine(directory, name);
                    string plist = Path.Combine(Path.Combine(app, "Contents"), "Info.plist");
                    if (!File.Exists(plist)) continue;
                    var candidate = ReadBundle(app, plist);
                    if (newest == null || (candidate.version != null && (newest.version == null || candidate.version > newest.version)))
                        newest = candidate;
                }
            }
            return newest;
        }

        internal static MacRenderer ReadBundle(string app, string plist)
        {
            // Release bundles normally have an XML plist. Use the macOS reader for binary plists.
            using (var stream = File.OpenRead(plist))
            {
                if (stream.ReadByte() != 'b')
                {
                    stream.Position = 0;
                    var document = new XmlDocument { XmlResolver = null };
                    try { document.Load(stream); }
                    catch (XmlException) { return new MacRenderer(app, ""); }
                    string display = PlistValue(document, "CFBundleShortVersionString");
                    string build = PlistValue(document, "CFBundleVersion");
                    return FromVersions(app, display, build);
                }
            }
            return FromVersions(app, ReadBinaryValue(plist, "CFBundleShortVersionString"),
                ReadBinaryValue(plist, "CFBundleVersion"));
        }

        private static MacRenderer FromVersions(string app, string display, string build)
        {
            Version release = ParseVersion(display);
            Version revision = ParseVersion(build);
            if (revision != null && (release == null || (revision.Major == release.Major &&
                revision.Minor == release.Minor && revision.Build == release.Build && revision > release)))
                return new MacRenderer(app, build);
            return new MacRenderer(app, display);
        }

        private static string PlistValue(XmlDocument document, string key)
        {
            XmlNode value = document.SelectSingleNode("/plist/dict/key[.='" + key + "']/following-sibling::*[1]");
            return value == null ? "" : value.InnerText;
        }

        private static string ReadBinaryValue(string plist, string key)
        {
            var info = new ProcessStartInfo("/usr/libexec/PlistBuddy",
                "-c \"Print :" + key + "\" \"" + plist.Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using (Process process = Process.Start(info))
            {
                string value = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit();
                return process.ExitCode == 0 ? value : "";
            }
        }
    }
}
