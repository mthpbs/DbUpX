using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Moq;
using Xunit;

namespace DbUpX.Tests
{
    public class HashingPipelineTests
    {
        [Fact]
        public void BothProvidersSkipNormalizedChangesAndRunSubstantiveChanges()
        {
            foreach (var mode in new[] { SqlScriptHashingMode.NormalizeSqlServer, SqlScriptHashingMode.NormalizePostgreSql })
            {
                var builder = new UpgradeEngineBuilder();
                if (mode == SqlScriptHashingMode.NormalizeSqlServer)
                {
                    builder.JournalToSqlWithHashing(mode, scripts => scripts.WithPrefix("prefix."));
                }
                else
                {
                    builder.JournalToPostgreSqlWithHashing(mode, scripts => scripts.WithPrefix("prefix."));
                }

                var connections = new Mock<IConnectionManager>();
                builder.Configure(c =>
                {
                    c.ConnectionManager = connections.Object;
                    c.ScriptExecutor = Mock.Of<IScriptExecutor>();
                });
                var config = builder.WithScripts(Array.Empty<SqlScript>()).BuildConfiguration();
                const string originalContents = "SELECT 1;\r\nSELECT 2;\r\n";
                var original = new SqlScript("prefix.script.sql", originalContents);
                var first = config.ScriptFilter.Filter(new[] { original }, new HashSet<string>(), config.ScriptNameComparer)
                    .Should().ContainSingle().Subject;
                first.Contents.Should().Be(originalContents);

                var stored = CaptureStoredParameters(config.Journal, first);
                stored["scriptName"].Should().Be("script.sql");
                stored["contentsHash"].Should().Be(NameWithHash.GenerateHash("SELECT 1;\nSELECT 2;\n"));
                first.Name.Should().Be($"{stored["scriptName"]}#{stored["contentsHash"]}");

                using (var rows = new DataTable())
                {
                    rows.Columns.Add("ScriptName", typeof(string));
                    rows.Columns.Add("ContentsHash", typeof(string));
                    rows.Rows.Add(stored["scriptName"], stored["contentsHash"]);
                    var command = new Mock<IDbCommand>();
                    command.SetupAllProperties();
                    using (var parameterOwner = new SqlCommand())
                    {
                        command.SetupGet(c => c.Parameters).Returns(parameterOwner.Parameters);
                        command.Setup(c => c.CreateParameter()).Returns(() => new SqlParameter());
                        command.Setup(c => c.ExecuteScalar()).Returns(1);
                        command.Setup(c => c.ExecuteReader()).Returns(() => rows.CreateDataReader());
                        Func<IDbCommand> factory = () => command.Object;
                        connections.Setup(c => c.ExecuteCommandsWithManagedConnection(It.IsAny<Func<Func<IDbCommand>, object>>()))
                            .Returns((Func<Func<IDbCommand>, object> action) => action(factory));
                        connections.Setup(c => c.ExecuteCommandsWithManagedConnection(It.IsAny<Func<Func<IDbCommand>, string[]>>()))
                            .Returns((Func<Func<IDbCommand>, string[]> action) => action(factory));

                        var executed = new HashSet<string>(config.Journal.GetExecutedScripts());
                        executed.Should().ContainSingle().Which.Should().Be(first.Name);
                        var equivalent = new SqlScript("prefix.script.sql", "\uFEFFSELECT 1;\nSELECT 2;\n");
                        config.ScriptFilter.Filter(new[] { equivalent }, executed, config.ScriptNameComparer).Should().BeEmpty();

                        const string changedContents = "\uFEFFSELECT 1;\nSELECT 3;\n";
                        var changed = new SqlScript("prefix.script.sql", changedContents);
                        var pending = config.ScriptFilter.Filter(new[] { changed }, executed, config.ScriptNameComparer)
                            .Should().ContainSingle().Subject;
                        pending.Contents.Should().Be(changedContents);
                        pending.Name.Should().NotBe(first.Name);

                        var renamed = new SqlScript("prefix.other.sql", equivalent.Contents);
                        config.ScriptFilter.Filter(new[] { renamed }, executed, config.ScriptNameComparer)
                            .Should().ContainSingle();
                    }
                }
            }
        }

        [Fact]
        public void LegacyBuildersAndJournalConstructorsKeepRawHashes()
        {
            const string contents = "\uFEFFSELECT 1;\r\n";
            foreach (var builder in new[]
            {
                new UpgradeEngineBuilder().JournalToSqlWithHashing(),
                new UpgradeEngineBuilder().JournalToPostgreSqlWithHashing()
            })
            {
                builder.Configure(c =>
                {
                    c.ConnectionManager = Mock.Of<IConnectionManager>();
                    c.ScriptExecutor = Mock.Of<IScriptExecutor>();
                });
                var config = builder.WithScripts(Array.Empty<SqlScript>()).BuildConfiguration();
                var script = new SqlScript("script.sql", contents);
                var filtered = config.ScriptFilter.Filter(new[] { script }, new HashSet<string>(), config.ScriptNameComparer).Single();
                filtered.Name.Should().Be("script.sql#" + NameWithHash.GenerateHash(contents));
                CaptureStoredParameters(config.Journal, filtered)["contentsHash"].Should().Be(NameWithHash.GenerateHash(contents));
            }

            foreach (var journal in new HashingTableJournal[]
            {
                new SqlHashingJournal(() => null, () => null, null, null),
                new PostgreSqlHashingJournal(() => null, () => null, null, null)
            })
            {
                CaptureStoredParameters(journal, new SqlScript("script.sql#stale", contents))["contentsHash"]
                    .Should().Be(NameWithHash.GenerateHash(contents));
            }
        }

        [Fact]
        public void EnablingNormalizationDoesNotSilentlyAcceptLegacyHashes()
        {
            const string contents = "SELECT 1;\r\n";
            foreach (var builder in new[]
            {
                new UpgradeEngineBuilder().JournalToSqlWithHashing(SqlScriptHashingMode.NormalizeSqlServer),
                new UpgradeEngineBuilder().JournalToPostgreSqlWithHashing(SqlScriptHashingMode.NormalizePostgreSql)
            })
            {
                builder.Configure(c =>
                {
                    c.ConnectionManager = Mock.Of<IConnectionManager>();
                    c.ScriptExecutor = Mock.Of<IScriptExecutor>();
                });
                var config = builder.WithScripts(Array.Empty<SqlScript>()).BuildConfiguration();
                var legacyNames = new HashSet<string> { "script.sql#" + NameWithHash.GenerateHash(contents) };
                config.ScriptFilter.Filter(new[] { new SqlScript("script.sql", contents) }, legacyNames, config.ScriptNameComparer)
                    .Should().ContainSingle();
            }
        }

        [Fact]
        public void RejectsModesForTheWrongProviderAndUnknownModes()
        {
            foreach (var mode in new[] { SqlScriptHashingMode.NormalizePostgreSql, (SqlScriptHashingMode)99 })
            {
                Action builder = () => new UpgradeEngineBuilder().JournalToSqlWithHashing(mode);
                builder.Should().Throw<ArgumentException>().WithParameterName("hashingMode");
                Action journal = () => new SqlHashingJournal(() => null, () => null, null, null, mode);
                journal.Should().Throw<ArgumentException>().WithParameterName("hashingMode");
            }

            foreach (var mode in new[] { SqlScriptHashingMode.NormalizeSqlServer, (SqlScriptHashingMode)99 })
            {
                Action builder = () => new UpgradeEngineBuilder().JournalToPostgreSqlWithHashing(mode);
                builder.Should().Throw<ArgumentException>().WithParameterName("hashingMode");
                Action journal = () => new PostgreSqlHashingJournal(() => null, () => null, null, null, mode);
                journal.Should().Throw<ArgumentException>().WithParameterName("hashingMode");
            }
        }

        private static Dictionary<string, object> CaptureStoredParameters(IJournal journal, SqlScript script)
        {
            var commands = new List<Dictionary<string, object>>();
            var owners = new List<SqlCommand>();
            try
            {
                journal.StoreExecutedScript(script, () =>
                {
                    var owner = new SqlCommand();
                    owners.Add(owner);
                    var command = new Mock<IDbCommand>();
                    command.SetupAllProperties();
                    command.SetupGet(c => c.Parameters).Returns(owner.Parameters);
                    command.Setup(c => c.CreateParameter()).Returns(() => new SqlParameter());
                    command.Setup(c => c.ExecuteNonQuery()).Callback(() => commands.Add(
                        owner.Parameters.Cast<SqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value)));
                    return command.Object;
                });
            }
            finally
            {
                foreach (var owner in owners)
                {
                    owner.Dispose();
                }
            }

            commands.Should().HaveCount(2);
            commands[0]["scriptName"].Should().Be(commands[1]["scriptName"]);
            return commands[1];
        }
    }
}
