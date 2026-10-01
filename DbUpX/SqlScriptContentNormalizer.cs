using System;
using System.Text;

namespace DbUpX
{
    internal static class SqlScriptContentNormalizer
    {
        internal static void ValidateMode(SqlScriptHashingMode hashingMode)
        {
            if (hashingMode != SqlScriptHashingMode.Raw &&
                hashingMode != SqlScriptHashingMode.NormalizeSqlServer &&
                hashingMode != SqlScriptHashingMode.NormalizePostgreSql)
            {
                throw new ArgumentOutOfRangeException(nameof(hashingMode));
            }
        }

        internal static void ValidateProviderMode(
            SqlScriptHashingMode hashingMode, SqlScriptHashingMode providerMode)
        {
            ValidateMode(hashingMode);
            if (hashingMode != SqlScriptHashingMode.Raw && hashingMode != providerMode)
            {
                throw new ArgumentException(
                    $"Use Raw or {providerMode} for this database provider.", nameof(hashingMode));
            }
        }

        internal static string Normalize(string content, SqlScriptHashingMode hashingMode)
        {
            ValidateMode(hashingMode);
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            if (hashingMode == SqlScriptHashingMode.Raw)
            {
                return content;
            }

            var start = content.Length > 0 && content[0] == '\uFEFF' ? 1 : 0;
            var postgres = hashingMode == SqlScriptHashingMode.NormalizePostgreSql;
            var result = new StringBuilder(content.Length);
            var index = start;
            var continuedEscapeString = false;
            while (index < content.Length)
            {
                var current = content[index];
                if (Matches(content, index, "--"))
                {
                    while (index < content.Length && content[index] != '\r' && content[index] != '\n')
                    {
                        result.Append(content[index++]);
                    }
                }
                else if (Matches(content, index, "/*"))
                {
                    var depth = 1;
                    result.Append("/*");
                    index += 2;
                    while (index < content.Length && depth > 0)
                    {
                        if (Matches(content, index, "/*"))
                        {
                            depth++;
                            result.Append("/*");
                            index += 2;
                        }
                        else if (Matches(content, index, "*/"))
                        {
                            depth--;
                            result.Append("*/");
                            index += 2;
                        }
                        else
                        {
                            AppendNormalizedCharacter(content, result, ref index);
                        }
                    }

                    if (depth != 0)
                    {
                        return content.Substring(start);
                    }
                }
                else if (current == '\'' || current == '"' || (!postgres && current == '['))
                {
                    var escapeString = postgres && current == '\'' &&
                        (continuedEscapeString || IsEscapeString(content, index, start));
                    var end = FindQuotedEnd(content, index, current == '[' ? ']' : current,
                        escapeString, postgres && current == '\'' && !escapeString);
                    if (end < 0)
                    {
                        // Do not guess about incomplete SQL or session-dependent backslash escapes.
                        return content.Substring(start);
                    }

                    result.Append(content, index, end - index);
                    index = end;
                    continuedEscapeString = escapeString;
                }
                else if (postgres && current == '$' &&
                    (index == start || !IsIdentifierPart(content[index - 1])) &&
                    TryGetDollarDelimiter(content, index, out var delimiter))
                {
                    var closing = content.IndexOf(delimiter, index + delimiter.Length, StringComparison.Ordinal);
                    if (closing < 0)
                    {
                        return content.Substring(start);
                    }

                    var end = closing + delimiter.Length;
                    result.Append(content, index, end - index);
                    index = end;
                    continuedEscapeString = false;
                }
                else
                {
                    // PostgreSQL allows an E-string to continue in another quoted segment.
                    if (current != ' ' && current != '\t' && current != '\r' &&
                        current != '\n' && current != '\f')
                    {
                        continuedEscapeString = false;
                    }

                    AppendNormalizedCharacter(content, result, ref index);
                }
            }

            return result.ToString();
        }

        private static void AppendNormalizedCharacter(string content, StringBuilder result, ref int index)
        {
            if (content[index] == '\r')
            {
                result.Append('\n');
                index++;
                if (index < content.Length && content[index] == '\n')
                {
                    index++;
                }
            }
            else
            {
                result.Append(content[index++]);
            }
        }

        // Returns the first position after the closing delimiter, or -1 when unsafe to scan.
        private static int FindQuotedEnd(
            string content, int start, char delimiter, bool escapeString, bool ambiguousBackslash)
        {
            for (var index = start + 1; index < content.Length; index++)
            {
                if (content[index] == '\\')
                {
                    if (ambiguousBackslash)
                    {
                        return -1;
                    }

                    if (escapeString)
                    {
                        index++;
                        continue;
                    }
                }

                if (content[index] == delimiter)
                {
                    if (index + 1 < content.Length && content[index + 1] == delimiter)
                    {
                        index++;
                    }
                    else
                    {
                        return index + 1;
                    }
                }
            }

            return -1;
        }

        private static bool IsEscapeString(string content, int quote, int start)
        {
            return quote > start && (content[quote - 1] == 'E' || content[quote - 1] == 'e') &&
                (quote - 1 == start || !IsIdentifierPart(content[quote - 2]));
        }

        private static bool TryGetDollarDelimiter(string content, int start, out string delimiter)
        {
            var index = start + 1;
            if (index < content.Length && IsIdentifierStart(content[index]))
            {
                index++;
                while (index < content.Length &&
                    (IsIdentifierStart(content[index]) || (content[index] >= '0' && content[index] <= '9')))
                {
                    index++;
                }
            }

            if (index < content.Length && content[index] == '$')
            {
                delimiter = content.Substring(start, index - start + 1);
                return true;
            }

            delimiter = null;
            return false;
        }

        private static bool IsIdentifierStart(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                value == '_' || value >= 128;
        }

        private static bool IsIdentifierPart(char value)
        {
            return IsIdentifierStart(value) || (value >= '0' && value <= '9') || value == '$';
        }

        private static bool Matches(string content, int index, string value)
        {
            return index + value.Length <= content.Length &&
                string.CompareOrdinal(content, index, value, 0, value.Length) == 0;
        }
    }
}
