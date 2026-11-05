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
            return new ExportSummary { Path = path, Time = time,
                Nodes = ((JArray)document["nodes"]).Count, Networks = ((JArray)document["networks"]).Count,
                Buildings = ((JArray)document["buildings"]).Count, Stops = ((JArray)document["stops"]).Count,
                Lines = ((JArray)document["transit_lines"]).Count, Issues = issues.Count,
                FirstIssue = issues.Count == 0 ? "" : (string)issues[0]["message"] };
        }
    }
}
