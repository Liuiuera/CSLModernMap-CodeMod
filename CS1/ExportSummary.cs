using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSLModernMap.Exporter
{
    internal sealed class ExportSummary
    {
        internal string Path;
        internal DateTime Time;
        internal int Nodes, Networks, Buildings, Stops, Lines, Issues;
        internal string FirstIssue;

        internal static ExportSummary FromDocument(JObject document, string path, DateTime time)
        {
            JArray issues = (JArray)document["issues"];
            int warnings = 0;
            string firstWarning = "";
            foreach (JToken issue in issues)
            {
                if (!IsWarning(issue)) continue;
                if (warnings++ == 0) firstWarning = (string)issue["message"];
            }
            return new ExportSummary { Path = path, Time = time,
                Nodes = ((JArray)document["nodes"]).Count, Networks = ((JArray)document["networks"]).Count,
                Buildings = ((JArray)document["buildings"]).Count, Stops = ((JArray)document["stops"]).Count,
                Lines = ((JArray)document["transit_lines"]).Count, Issues = warnings,
                FirstIssue = firstWarning };
        }

        internal static bool IsWarning(JToken issue)
        { return (string)issue["severity"] != "info"; }
    }
}
