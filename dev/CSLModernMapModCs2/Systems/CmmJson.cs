using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CSLModernMap.Systems
{
    /// <summary>生成地图数据的序列化内容</summary>
    internal static class CmmJson
    {
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            var firstEscape = -1;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"' || c == '\\' || c < ' ')
                {
                    firstEscape = i;
                    break;
                }
            }

            if (firstEscape < 0)
            {
                return value;
            }

            var result = new StringBuilder(value.Length + 8);
            result.Append(value, 0, firstEscape);
            for (var i = firstEscape; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"':
                        result.Append("\\\"");
                        break;
                    case '\\':
                        result.Append("\\\\");
                        break;
                    case '\b':
                        result.Append("\\b");
                        break;
                    case '\f':
                        result.Append("\\f");
                        break;
                    case '\n':
                        result.Append("\\n");
                        break;
                    case '\r':
                        result.Append("\\r");
                        break;
                    case '\t':
                        result.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            result.Append("\\u")
                                .Append(((int)c).ToString(
                                    "x4",
                                    CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            result.Append(c);
                        }
                        break;
                }
            }

            return result.ToString();
        }

        internal static void AppendString(StringBuilder json, string value)
        {
            json.Append('"').Append(Escape(value)).Append('"');
        }
        internal static void AppendProperty(
            StringBuilder json,
            string name,
            string value)
        {
            json.Append(", ");
            AppendString(json, name);
            json.Append(": ");
            AppendString(json, value);
        }
        internal static void AppendOptionalInt(
            StringBuilder json,
            bool hasValue,
            int value)
        {
            json.Append(
                hasValue
                    ? value.ToString(CultureInfo.InvariantCulture)
                    : "null");
        }

        internal static string BuildCounters(
            Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return "{}";
            }
            var sorted = new SortedDictionary<string, int>(
                counts,
                StringComparer.Ordinal);

            return JsonConvert.SerializeObject(sorted, Formatting.None);
        }

        internal static bool IsWellFormed(
            string text,
            out string error)
        {
            if (text == null)
            {
                error = "文档为空";
                return false;
            }
            try
            {
                JToken.Parse(text);
                error = null;
                return true;
            }
            catch (JsonException exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
