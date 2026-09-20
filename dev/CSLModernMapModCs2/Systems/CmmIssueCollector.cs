using System.Text;

namespace CSLModernMap.Systems
{
    internal sealed class CmmIssueCollector
    {
        private readonly StringBuilder m_Json = new StringBuilder(256);
        private int m_Count;
        private int m_ErrorCount;
        private string m_First;

        internal int Count => m_Count;

        internal int ErrorCount => m_ErrorCount;

        internal string First => m_First ?? string.Empty;

        internal void Error(string code, string message)
        {
            m_ErrorCount++;
            Add("error", code, message);
        }

        internal void Warn(string code, string message)
        {
            Add("warn", code, message);
        }

        private void Add(string severity, string code, string message)
        {
            if (m_Count > 0)
            {
                m_Json.Append(", ");
            }

            if (m_First == null)
            {
                m_First = message;
            }

            m_Json.Append("{\"severity\": ");
            CmmJson.AppendString(m_Json, severity);
            CmmJson.AppendProperty(m_Json, "code", code);
            CmmJson.AppendProperty(m_Json, "message", message);
            m_Json.Append(", \"refs\": []}");
            m_Count++;
        }

        internal string ToJson()
        {
            return m_Count == 0 ? "[]" : "[" + m_Json + "]";
        }
    }
}
