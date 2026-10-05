using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DbUp.Engine;
using FluentAssertions;
using Xunit;

namespace DbUpX.Tests
{
    public class SqlScriptHashingTests
    {
        private static readonly SqlScriptHashingMode[] NormalizedModes =
        {
            SqlScriptHashingMode.NormalizeSqlServer,
            SqlScriptHashingMode.NormalizePostgreSql
        };

        private static string RawHash(string content)
        {
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(content)));
            }
        }

        [Fact]
        public void RawModePreservesExistingHashes()
        {
            foreach (var content in new[] { "", "SELECT 1;", "\uFEFFSELECT 1;\r\n", "é\r\n漢字\r" })
            {
                NameWithHash.GenerateHash(content).Should().Be(RawHash(content));
                NameWithHash.GenerateHash(content, SqlScriptHashingMode.Raw).Should().Be(RawHash(content));
            }
        }

        [Fact]
        public void NormalizesLineEndingsAndOneLeadingBom()
        {
            const string canonical = "SELECT 1;\n-- comment\nSELECT 2;\n";
            foreach (var mode in NormalizedModes)
            {
                foreach (var eol in new[] { "\n", "\r\n", "\r" })
                {
                    foreach (var bom in new[] { "", "\uFEFF" })
                    {
                        NameWithHash.GenerateHash(bom + canonical.Replace("\n", eol), mode)
                            .Should().Be(RawHash(canonical));
                    }
                }

                NameWithHash.GenerateHash("SELECT 1;\r-- comment\r\nSELECT 2;\n", mode)
                    .Should().Be(RawHash(canonical));
                NameWithHash.GenerateHash("", mode).Should().Be(RawHash(""));
                NameWithHash.GenerateHash("\uFEFF", mode).Should().Be(RawHash(""));
                NameWithHash.GenerateHash("\uFEFF\uFEFFSELECT 1;", mode)
                    .Should().Be(RawHash("\uFEFFSELECT 1;"));
            }
        }

        [Fact]
        public void PreservesOtherWhitespaceHiddenCharactersAndSqlChanges()
        {
            const string original = "SELECT 1;\nSELECT 2;";
            var changes = new[]
            {
                "SELECT 3;\nSELECT 2;", " SELECT 1;\nSELECT 2;", "SELECT\t1;\nSELECT 2;",
                "SELECT 1; \nSELECT 2;", "SELECT 1;\n\nSELECT 2;", original + "\n",
                "SELECT 1;\n\uFEFFSELECT 2;", "SELECT\u00A01;\nSELECT 2;",
                "SELECT\u200B1;\nSELECT 2;", "SELECT\u200E1;\nSELECT 2;"
            };
            foreach (var mode in NormalizedModes)
            {
                foreach (var changed in changes)
                {
                    NameWithHash.GenerateHash(changed, mode).Should().NotBe(NameWithHash.GenerateHash(original, mode));
                }

                NameWithHash.GenerateHash("SELECT 1; -- old", mode)
                    .Should().NotBe(NameWithHash.GenerateHash("SELECT 1; -- new", mode));
            }
        }

        [Fact]
        public void PreservesQuotedContentWhileNormalizingSurroundingSql()
        {
            var quotedRegions = new[]
            {
                "'first\r\nsecond'", "N'first\r\nsecond'", "'it''s\r\nstill quoted'",
                "\"first\r\nsecond\"", "\"escaped\"\"\r\nname\"",
                "'-- /* \r\n */'", "'\uFEFF\u200B\u00A0\t'"
            };
            foreach (var mode in NormalizedModes)
            {
                foreach (var quoted in quotedRegions)
                {
                    NameWithHash.GenerateHash("SELECT\r\n" + quoted + ";\r", mode)
                        .Should().Be(RawHash("SELECT\n" + quoted + ";\n"));
                    NameWithHash.GenerateHash("SELECT " + quoted, mode)
                        .Should().Be(RawHash("SELECT " + quoted));
                }

                NameWithHash.GenerateHash("SELECT 'a\r\nb';", mode)
                    .Should().NotBe(NameWithHash.GenerateHash("SELECT 'a\nb';", mode));
            }
        }

        [Fact]
        public void PreservesSqlServerBracketIdentifiersAndBackslashesInStrings()
        {
            const string quoted = "[a]]\r\n-- /* 'b]";
            NameWithHash.GenerateHash("SELECT\r\n" + quoted + ";\r\n", SqlScriptHashingMode.NormalizeSqlServer)
                .Should().Be(RawHash("SELECT\n" + quoted + ";\n"));
            NameWithHash.GenerateHash("SELECT 'path\\';\r\nSELECT 2;", SqlScriptHashingMode.NormalizeSqlServer)
                .Should().Be(RawHash("SELECT 'path\\';\nSELECT 2;"));
        }

        [Fact]
        public void NormalizesPostgreSqlDollarQuotedBodiesLikeCode()
        {
            var mode = SqlScriptHashingMode.NormalizePostgreSql;
            foreach (var delimiter in new[] { "$$", "$body$", "$Body_2$", "$é$" })
            {
                var body = "DO\r\n" + delimiter + "BEGIN\r\nSELECT '-- /* '' $other$';\r\nEND" + delimiter + ";\r";
                NameWithHash.GenerateHash(body, mode)
                    .Should().Be(RawHash(body.Replace("\r\n", "\n").Replace("\r", "\n")));
            }

            const string quoted = "'line\r\nbreak'";
            NameWithHash.GenerateHash("DO $$\r\nBEGIN\r\nRAISE NOTICE " + quoted + ";\r\nEND $$;\r\n", mode)
                .Should().Be(RawHash("DO $$\nBEGIN\nRAISE NOTICE " + quoted + ";\nEND $$;\n"));

            // DbUp variable tokens use the same $name$ syntax as dollar-quote tags.
            foreach (var sql in new[]
            {
                "CREATE TABLE $schema$.a (id int);\r\nCREATE TABLE $schema$.b (id int);\r\n",
                "CREATE TABLE $schema$.a (id int);\r\n",
                "SELECT array[\r\n1, 2], foo$tag$, $1;\r\n"
            })
            {
                NameWithHash.GenerateHash(sql, mode).Should().Be(RawHash(sql.Replace("\r\n", "\n")));
            }
        }

        [Fact]
        public void HandlesPostgreSqlEscapeStringsAndTheirContinuations()
        {
            const string first = "E'first\\'\r\nstill a string'";
            const string continuation = "'continued\\'\r\nstring'";
            var sql = "SELECT\r\n" + first + "\r\n-- comment\r\n" + continuation + ";\r\n";
            var expected = "SELECT\n" + first + "\n-- comment\n" + continuation + ";\n";
            NameWithHash.GenerateHash(sql, SqlScriptHashingMode.NormalizePostgreSql)
                .Should().Be(RawHash(expected));

            // A block comment ends the E-string chain, so the next segment is an ordinary string.
            const string afterBlockComment = "SELECT E'x'\r\n/* c */\r\n'p\\';\r\nSELECT 2;\r\n";
            NameWithHash.GenerateHash(afterBlockComment, SqlScriptHashingMode.NormalizePostgreSql)
                .Should().Be(RawHash(afterBlockComment.Replace("\r\n", "\n")));

            const string escapedBackslash = "SELECT e'\\\\';\r\nSELECT 2;";
            NameWithHash.GenerateHash(escapedBackslash, SqlScriptHashingMode.NormalizePostgreSql)
                .Should().Be(RawHash(escapedBackslash.Replace("\r\n", "\n")));
        }

        [Fact]
        public void PreservesNewlinesBetweenPostgreSqlStringSegments()
        {
            var mode = SqlScriptHashingMode.NormalizePostgreSql;
            NameWithHash.GenerateHash("SELECT 'a'\r\n'b';", mode)
                .Should().Be(RawHash("SELECT 'a'\n'b';"));
            NameWithHash.GenerateHash("SELECT 'a'\n'b';", mode)
                .Should().NotBe(NameWithHash.GenerateHash("SELECT 'a' 'b';", mode));
        }

        [Fact]
        public void NormalizesCommentsWithoutInterpretingTheirQuotes()
        {
            const string sql = "-- ' \" [ $$ /*\r\nSELECT 1;\r/* outer '\r\n/* nested */\r\nend */\r\n";
            foreach (var mode in NormalizedModes)
            {
                NameWithHash.GenerateHash(sql, mode)
                    .Should().Be(RawHash(sql.Replace("\r\n", "\n").Replace("\r", "\n")));
            }
        }

        [Fact]
        public void FallsBackToOriginalLineEndingsForIncompleteSql()
        {
            foreach (var mode in NormalizedModes)
            {
                foreach (var sql in new[] { "SELECT 1;\r\n'open", "SELECT 1;\r\n\"open", "SELECT 1;\r\n/* open" })
                {
                    NameWithHash.GenerateHash("\uFEFF" + sql, mode).Should().Be(RawHash(sql));
                }
            }

            const string bracket = "SELECT 1;\r\n[open";
            NameWithHash.GenerateHash(bracket, SqlScriptHashingMode.NormalizeSqlServer).Should().Be(RawHash(bracket));
            foreach (var sql in new[] { "SELECT 1;\r\nE'open\\'", "SELECT 1;\r\nSELECT $$it's$$;\r\n" })
            {
                NameWithHash.GenerateHash(sql, SqlScriptHashingMode.NormalizePostgreSql).Should().Be(RawHash(sql));
            }
        }

        [Fact]
        public void TreatsPostgreSqlBackslashesAsLiteralInOrdinaryStrings()
        {
            foreach (var sql in new[]
            {
                "SELECT 1;\r\nSELECT 'path\\file';\r\n",
                "SELECT regexp_replace(x, '\\d+', '');\r\nSELECT 2;\r\n",
                "SELECT E'explicit', 'ordinary\\path';\r\n"
            })
            {
                NameWithHash.GenerateHash("\uFEFF" + sql, SqlScriptHashingMode.NormalizePostgreSql)
                    .Should().Be(RawHash(sql.Replace("\r\n", "\n")));
            }
        }

        [Fact]
        public void SqlServerModeIgnoresPostgreSqlSyntax()
        {
            const string sql = "SELECT $x$, 'a\\';\r\nSELECT E'b', $$c$$;\r\n";
            NameWithHash.GenerateHash(sql, SqlScriptHashingMode.NormalizeSqlServer)
                .Should().Be(RawHash(sql.Replace("\r\n", "\n")));
        }

        [Fact]
        public void ScriptNamesUseSelectedModeWithoutChangingContents()
        {
            const string contents = "\uFEFFSELECT 1;\r\nSELECT 2;\r";
            var script = new SqlScript("script.sql#stale", contents);
            foreach (var mode in NormalizedModes)
            {
                var expected = "script.sql#" + RawHash("SELECT 1;\nSELECT 2;\n");
                NameWithHash.FromScript(script, mode).ToString().Should().Be(expected);
                var hashed = new[] { script }.HashNames(mode).Single();
                hashed.Name.Should().Be(expected);
                hashed.Contents.Should().Be(contents);
            }

            NameWithHash.FromScript(script).ContentsHash.Should().Be(RawHash(contents));
            new[] { script }.HashNames().Single().Name.Should().Be("script.sql#" + RawHash(contents));
        }

        [Fact]
        public void RejectsInvalidModesAndNullContent()
        {
            Action invalidMode = () => NameWithHash.GenerateHash("SELECT 1;", (SqlScriptHashingMode)99);
            invalidMode.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("hashingMode");
            Action invalidSequenceMode = () => Array.Empty<SqlScript>().HashNames((SqlScriptHashingMode)99);
            invalidSequenceMode.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("hashingMode");
            foreach (var mode in NormalizedModes.Concat(new[] { SqlScriptHashingMode.Raw }))
            {
                Action nullContent = () => NameWithHash.GenerateHash(null, mode);
                nullContent.Should().Throw<ArgumentNullException>().WithParameterName("content");
            }
        }
    }
}
