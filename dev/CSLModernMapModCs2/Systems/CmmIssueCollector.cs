using System.Collections.Generic;
using System.Text;

namespace CSLModernMap.Systems
{
    /// <summary>收集导出过程中的数据问题</summary>
    internal sealed class CmmIssueCollector
    {
        private sealed class Issue
        {
            internal string Severity, Code, Message;
            internal int Occurrences;
            internal readonly List<string> Examples = new List<string>();
            internal string Text => Occurrences == 0 ? Message
                : Message + " (" + Occurrences + " occurrences). Examples: " + string.Join("; ", Examples);
        }

        private readonly List<Issue> m_Issues = new List<Issue>();
        private readonly Dictionary<string, Issue> m_Groups = new Dictionary<string, Issue>();

        internal int Count => m_Issues.Count;

        internal string First => m_Issues.Count == 0 ? string.Empty : m_Issues[0].Text;

        internal void Error(string code, string message)
        {
            Add("error", code, message);
        }

        internal void Warn(string code, string message)
        {
            Add("warn", code, message);
        }

        internal void WarnGrouped(string code, string reason, string example)
        {
            var key = code + "\n" + reason;
            if (!m_Groups.TryGetValue(key, out var issue))
            {
                issue = new Issue { Severity = "warn", Code = code, Message = reason };
                m_Groups.Add(key, issue);
                m_Issues.Add(issue);
            }
            issue.Occurrences++;
            if (issue.Examples.Count < 3) issue.Examples.Add(example);
        }

        private void Add(string severity, string code, string message)
        {
            m_Issues.Add(new Issue { Severity = severity, Code = code, Message = message });
        }

        internal string ToJson()
        {
            var json = new StringBuilder("[");
            foreach (var issue in m_Issues)
            {
                if (json.Length > 1) json.Append(", ");
                json.Append("{\"severity\": ");
                CmmJson.AppendString(json, issue.Severity);
                CmmJson.AppendProperty(json, "code", issue.Code);
                CmmJson.AppendProperty(json, "message", issue.Text);
                json.Append(", \"refs\": []}");
            }
            return json.Append(']').ToString();
        }
    }
}
