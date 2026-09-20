using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CSLModernMap.Systems
{
    /// <summary>
    /// CMM 导出用的 JSON 文本工具。约定：业务代码不手写引号边界，
    /// 一律走本类；写盘前经 <see cref="IsWellFormed"/> 自检。
    /// 刻意不引用 Unity/游戏类型，可独立编译成控制台程序跑断言。
    /// </summary>
    internal static class CmmJson
    {
        /// <summary>把一个字符串转义成 JSON 字符串**内容**（不含两侧引号）。null 视作空串。</summary>
        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            // 绝大多数名字（prefab 名、服务名）里一个需要转义的字符都没有，
            // 先扫一遍再决定要不要建 StringBuilder —— 建筑是逐栋调用的，别每次都分配。
            var needsEscape = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"' || c == '\\' || c < ' ')
                {
                    needsEscape = true;
                    break;
                }
            }

            if (!needsEscape)
            {
                return value;
            }

            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            // 其余控制字符（含 0x00~0x1F 里没单列的那些）走 \uXXXX，
                            // 否则它们会被原样写进文件，让解析器在别处报错。
                            builder.Append("\\u")
                                .Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>写一个 JSON 字符串字面量（自带两侧引号与转义）。</summary>
        internal static void AppendString(StringBuilder json, string value)
        {
            json.Append('"').Append(Escape(value)).Append('"');
        }

        /// <summary>写 "name": "value"，自带前导逗号，只能接在已有内容之后。</summary>
        internal static void AppendProperty(StringBuilder json, string name, string value)
        {
            json.Append(", ");
            AppendString(json, name);
            json.Append(": ");
            AppendString(json, value);
        }

        /// <summary>可选整数：没有值时写 <c>null</c>，绝不写空字符串（CMM 语义是整数或 null）。</summary>
        internal static void AppendOptionalInt(StringBuilder json, bool hasValue, int value)
        {
            if (hasValue)
            {
                json.Append(value.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                json.Append("null");
            }
        }

        /// <summary>分布计数写成 JSON 对象，键排序固定，便于两次导出之间直接对比。</summary>
        internal static string BuildCounters(Dictionary<string, int> counts)
        {
            if (counts == null || counts.Count == 0)
            {
                return "{}";
            }

            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder();
            builder.Append('{');
            for (var i = 0; i < keys.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");
                }

                // 键来自游戏侧（prefab 名、服务名、分区名），同样不能手写引号边界。
                builder.Append('"').Append(Escape(keys[i])).Append("\": ")
                    .Append(counts[keys[i]].ToString(CultureInfo.InvariantCulture));
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// 严格校验一段文本是不是合法 JSON（RFC 8259 的字面量子集）。
        /// 通过返回 true；失败返回 false，并在 <paramref name="error"/> 里给出
        /// <c>第 N 行 第 M 列：原因</c> 形式的定位信息。
        ///
        /// 注意它**比 System.Text.Json 更严**：拒绝 NaN / Infinity / 前导零 / 尾随逗号 /
        /// 未转义控制字符 / 单引号 —— 正是 Python 的 json.loads() 会拒绝的那些。
        /// </summary>
        internal static bool IsWellFormed(string text, out string error)
        {
            if (text == null)
            {
                error = "第 1 行 第 1 列：文档为空";
                return false;
            }

            var index = 0;
            if (!TryScanValue(text, ref index, out error))
            {
                error = At(text, index) + error;
                return false;
            }

            SkipWhitespace(text, ref index);
            if (index != text.Length)
            {
                error = At(text, index) + "顶层值结束后还有多余内容";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>把下标换算成「第 N 行 第 M 列」，与 Python 解析器的报错对得上，便于对照。</summary>
        private static string At(string text, int index)
        {
            var line = 1;
            var lineStart = 0;
            var limit = index < text.Length ? index : text.Length;
            for (var i = 0; i < limit; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            return "第 " + line.ToString(CultureInfo.InvariantCulture)
                + " 行 第 " + (limit - lineStart + 1).ToString(CultureInfo.InvariantCulture) + " 列：";
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                var c = text[index];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    index++;
                }
                else
                {
                    break;
                }
            }
        }

        private static bool TryScanValue(string text, ref int index, out string error)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length)
            {
                error = "文档在值的位置提前结束";
                return false;
            }

            switch (text[index])
            {
                case '{':
                    return TryScanObject(text, ref index, out error);
                case '[':
                    return TryScanArray(text, ref index, out error);
                case '"':
                    return TryScanString(text, ref index, out error);
                case 't':
                    return TryScanLiteral(text, ref index, "true", out error);
                case 'f':
                    return TryScanLiteral(text, ref index, "false", out error);
                case 'n':
                    return TryScanLiteral(text, ref index, "null", out error);
                default:
                    return TryScanNumber(text, ref index, out error);
            }
        }

        private static bool TryScanObject(string text, ref int index, out string error)
        {
            index++; // '{'
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == '}')
            {
                index++;
                error = null;
                return true;
            }

            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != '"')
                {
                    error = "对象里期待的键名（带双引号的字符串）不在这里；常见原因是前一个字符串少了结束引号";
                    return false;
                }

                if (!TryScanString(text, ref index, out error))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                {
                    error = "键名之后缺少 ':'";
                    return false;
                }

                index++;
                if (!TryScanValue(text, ref index, out error))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    error = "对象没有结束的 '}'";
                    return false;
                }

                var c = text[index];
                if (c == ',')
                {
                    index++;
                    continue;
                }

                if (c == '}')
                {
                    index++;
                    error = null;
                    return true;
                }

                error = "对象里缺少 ',' 或 '}'（该位置的字符是 '" + c + "'）";
                return false;
            }
        }

        private static bool TryScanArray(string text, ref int index, out string error)
        {
            index++; // '['
            SkipWhitespace(text, ref index);
            if (index < text.Length && text[index] == ']')
            {
                index++;
                error = null;
                return true;
            }

            while (true)
            {
                if (!TryScanValue(text, ref index, out error))
                {
                    return false;
                }

                SkipWhitespace(text, ref index);
                if (index >= text.Length)
                {
                    error = "数组没有结束的 ']'";
                    return false;
                }

                var c = text[index];
                if (c == ',')
                {
                    index++;
                    continue;
                }

                if (c == ']')
                {
                    index++;
                    error = null;
                    return true;
                }

                error = "数组里缺少 ',' 或 ']'（该位置的字符是 '" + c + "'）";
                return false;
            }
        }

        private static bool TryScanString(string text, ref int index, out string error)
        {
            index++; // 开引号

            while (true)
            {
                if (index >= text.Length)
                {
                    error = "字符串没有结束引号";
                    return false;
                }

                var c = text[index];
                if (c == '"')
                {
                    index++;
                    error = null;
                    return true;
                }

                if (c == '\\')
                {
                    index++;
                    if (index >= text.Length)
                    {
                        error = "字符串末尾的 '\\' 之后没有转义字符";
                        return false;
                    }

                    var escape = text[index];
                    if (escape == 'u')
                    {
                        if (index + 4 >= text.Length)
                        {
                            error = "\\u 转义缺少数位";
                            return false;
                        }

                        for (var i = 1; i <= 4; i++)
                        {
                            if (!IsHex(text[index + i]))
                            {
                                error = "\\u 转义里有非十六进制字符";
                                return false;
                            }
                        }

                        index += 5;
                        continue;
                    }

                    if ("\"\\/bfnrt".IndexOf(escape) < 0)
                    {
                        error = "字符串里的非法转义 '\\" + escape + "'";
                        return false;
                    }

                    index++;
                    continue;
                }

                if (c < ' ')
                {
                    error = "字符串里有未转义的控制字符（0x"
                        + ((int)c).ToString("x2", CultureInfo.InvariantCulture)
                        + "），应该写成 \\u 转义";
                    return false;
                }

                index++;
            }
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        private static bool TryScanLiteral(string text, ref int index, string literal, out string error)
        {
            if (index + literal.Length > text.Length
                || string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
            {
                error = "不认识的常量，应该只有 true / false / null";
                return false;
            }

            index += literal.Length;
            error = null;
            return true;
        }

        private static bool TryScanNumber(string text, ref int index, out string error)
        {
            var start = index;
            if (index < text.Length && text[index] == '-')
            {
                index++;
            }

            if (index >= text.Length || !IsDigit(text[index]))
            {
                error = "不是合法的 JSON 值（数字缺少整数部分）";
                return false;
            }

            if (text[index] == '0')
            {
                index++;
            }
            else
            {
                while (index < text.Length && IsDigit(text[index]))
                {
                    index++;
                }
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                if (index >= text.Length || !IsDigit(text[index]))
                {
                    error = "小数点之后没有数字";
                    return false;
                }

                while (index < text.Length && IsDigit(text[index]))
                {
                    index++;
                }
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                if (index >= text.Length || !IsDigit(text[index]))
                {
                    error = "指数部分没有数字";
                    return false;
                }

                while (index < text.Length && IsDigit(text[index]))
                {
                    index++;
                }
            }

            if (index == start)
            {
                error = "这里不是合法的 JSON 值";
                return false;
            }

            // 前导零（01）与 .5 / 5. 之类的写法此处已被上面的分支自然排除。
            error = null;
            return true;
        }

        private static bool IsDigit(char c)
        {
            return c >= '0' && c <= '9';
        }
    }
}
