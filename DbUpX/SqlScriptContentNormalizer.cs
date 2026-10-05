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
            // PostgreSQL dollar quotes are scanned as ordinary SQL: DbUp also reads $name$ as a
            // variable token, and dollar-quoted bodies are usually code whose EOLs should normalize.
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

                    // PostgreSQL only continues an E-string across whitespace and -- comments.
                    continuedEscapeString = false;
                }
                else if (current == '\'' || current == '"' || (!postgres && current == '['))
                {
                    var escapeString = postgres && current == '\'' &&
                        (continuedEscapeString || IsEscapeString(content, index, start));
                    // PostgreSQL ordinary strings assume standard_conforming_strings = on.
                    var end = FindQuotedEnd(content, index, current == '[' ? ']' : current, escapeString);
                    if (end < 0)
                    {
                        // Do not guess about incomplete SQL.
                        return content.Substring(start);
                    }

                    result.Append(content, index, end - index);
                    index = end;
                    continuedEscapeString = escapeString;
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

        // Returns the first position after the closing delimiter, or -1 when it is not closed.
        private static int FindQuotedEnd(string content, int start, char delimiter, bool escapeString)
        {
            for (var index = start + 1; index < content.Length; index++)
            {
                if (escapeString && content[index] == '\\')
                {
                    index++;
                    continue;
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

        private static bool IsIdentifierPart(char value)
        {
            return (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                (value >= '0' && value <= '9') || value == '_' || value == '$' || value >= 128;
        }

        private static bool Matches(string content, int index, string value)
        {
            return index + value.Length <= content.Length &&
                string.CompareOrdinal(content, index, value, 0, value.Length) == 0;
        }
    }
}
