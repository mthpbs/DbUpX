using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Postgresql;
using DbUp.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Moq;
using Xunit;

namespace DbUpX.Tests
{
    public class JournalLockMigrationTests
    {
        [Fact]
        public void UpgradesLegacyJournalBeforeReadingAndOnlyOnce()
        {
            foreach (var postgres in new[] { false, true })
            {
                using (var database = new JournalDatabase())
                {
                    database.Rows.Rows.Add("script.sql", "hash");
                    var journal = database.CreateJournal(postgres);
                    journal.GetExecutedScripts().Should().Equal("script.sql#hash");
                    journal.GetExecutedScripts().Should().Equal("script.sql#hash");
                    journal.EnsureTableExistsAndIsLatestVersion(database.CreateCommand);

                    database.Writes.Should().ContainSingle().Which.Should().StartWith("alter table ");
                    AssertLockDefinition(database.Writes.Single(), postgres);
                    database.Reads.Should().HaveCount(2);
                    database.Reads.Should().OnlyContain(sql => sql.Contains("case when") &&
                        sql.Contains("IsLocked") && sql.Contains("then '*' else"));
                }
            }
        }

        [Fact]
        public void SchemaVerificationAlsoUpgradesLegacyJournals()
        {
            foreach (var postgres in new[] { false, true })
            {
                using (var database = new JournalDatabase())
                {
                    database.CreateJournal(postgres).EnsureTableExistsAndIsLatestVersion(database.CreateCommand);
                    database.Writes.Should().ContainSingle().Which.Should().StartWith("alter table ");
                }
            }
        }

        [Fact]
        public void MissingJournalReadDoesNotCreateTableAndCreationIncludesUnlockedDefault()
        {
            foreach (var postgres in new[] { false, true })
            {
                using (var database = new JournalDatabase { TableExists = false })
                {
                    var journal = database.CreateJournal(postgres);
                    journal.GetExecutedScripts().Should().BeEmpty();
                    database.Writes.Should().BeEmpty();
                    database.Reads.Should().BeEmpty();

                    journal.EnsureTableExistsAndIsLatestVersion(database.CreateCommand);
                    database.Writes.Should().ContainSingle().Which.Should().StartWith("create table ");
                    AssertLockDefinition(database.Writes.Single(), postgres);
                }
            }
        }

        [Fact]
        public void LatestJournalRequiresNoSchemaWritesAndPassesThroughLockMarkers()
        {
            foreach (var postgres in new[] { false, true })
            {
                using (var database = new JournalDatabase { HasLockColumn = true })
                {
                    database.Rows.Rows.Add("locked.sql", "*");
                    database.Rows.Rows.Add("unlocked.sql", "hash");
                    var journal = database.CreateJournal(postgres);
                    journal.GetExecutedScripts().Should().Equal("locked.sql#*", "unlocked.sql#hash");
                    journal.EnsureTableExistsAndIsLatestVersion(database.CreateCommand);
                    database.Writes.Should().BeEmpty();
                }
            }
        }

        [Fact]
        public void MigrationFailureStopsTheJournalRead()
        {
            foreach (var postgres in new[] { false, true })
            {
                using (var database = new JournalDatabase { FailMigration = true })
                {
                    Action read = () => database.CreateJournal(postgres).GetExecutedScripts();
                    read.Should().Throw<InvalidOperationException>().WithMessage("Schema change denied");
                    database.Reads.Should().BeEmpty();
                }
            }
        }

        [Fact]
        public void MigrationParameterizesMetadataAndQuotesCustomIdentifiers()
        {
            foreach (var postgres in new[] { false, true })
            {
                foreach (var schema in new[] { "tenant's schema", string.Empty })
                {
                    using (var database = new JournalDatabase())
                    {
                        const string table = "Journal'; DROP TABLE Users;--";
                        ISqlObjectParser parser = postgres
                            ? (ISqlObjectParser)new PostgresqlObjectParser()
                            : new SqlServerObjectParser();
                        var quotedTable = (schema.Length == 0 ? string.Empty : parser.QuoteIdentifier(schema) + ".") +
                            parser.QuoteIdentifier(table);

                        database.CreateJournal(postgres, schema, table).GetExecutedScripts();

                        var metadata = database.MetadataQueries.Should().ContainSingle().Subject;
                        metadata.Sql.Should().Contain("@tableName").And.Contain("@columnName").And.NotContain(table);
                        metadata.Parameters["tableName"].Should().Be(quotedTable);
                        metadata.Parameters["columnName"].Should().Be("IsLocked");
                        database.Writes.Single().Should().StartWith("alter table " + quotedTable + " ");
                        database.Reads.Single().Should().EndWith("from " + quotedTable);
                    }
                }
            }
        }

        [Fact]
        public void CustomJournalWithoutMigrationOverrideKeepsItsExistingBehavior()
        {
            using (var database = new JournalDatabase())
            {
                database.Rows.Rows.Add("script.sql", "hash");
                var journal = new LegacyJournal(database.Connections.Object);
                journal.GetExecutedScripts().Should().Equal("script.sql#hash");
                journal.EnsureTableExistsAndIsLatestVersion(database.CreateCommand);
                database.Writes.Should().BeEmpty();
                database.MetadataQueries.Should().BeEmpty();
            }
        }

        private static void AssertLockDefinition(string sql, bool postgres)
        {
            sql.Should().Contain(postgres
                ? "\"IsLocked\" boolean not null default false"
                : "[IsLocked] bit not null default (0)");
        }

        private sealed class LegacyJournal : HashingTableJournal
        {
            public LegacyJournal(IConnectionManager connections)
                : base(() => connections, () => Mock.Of<IUpgradeLog>(), new SqlServerObjectParser(), null, null)
            {
            }

            protected override string CreateSchemaTableSql() => "create legacy table";
            protected override string GetJournalEntriesSql() => "select ScriptName, ContentsHash from legacy";
            protected override string GetInsertScriptSql() => "insert legacy";
            protected override string GetDeleteScriptSql() => "delete legacy";
        }

        private sealed class JournalDatabase : IDisposable
        {
            private readonly List<SqlCommand> _parameterOwners = new List<SqlCommand>();
            public readonly Mock<IConnectionManager> Connections = new Mock<IConnectionManager>();
            public readonly DataTable Rows = new DataTable();
            public readonly List<string> Writes = new List<string>();
            public readonly List<string> Reads = new List<string>();
            public readonly List<(string Sql, Dictionary<string, object> Parameters)> MetadataQueries =
                new List<(string, Dictionary<string, object>)>();
            public bool TableExists = true;
            public bool HasLockColumn;
            public bool FailMigration;

            public JournalDatabase()
            {
                Rows.Columns.Add("ScriptName", typeof(string));
                Rows.Columns.Add("ContentsHash", typeof(string));
                Connections.Setup(c => c.ExecuteCommandsWithManagedConnection(It.IsAny<Func<Func<IDbCommand>, object>>()))
                    .Returns((Func<Func<IDbCommand>, object> action) => action(CreateCommand));
                Connections.Setup(c => c.ExecuteCommandsWithManagedConnection(It.IsAny<Func<Func<IDbCommand>, string[]>>()))
                    .Returns((Func<Func<IDbCommand>, string[]> action) => action(CreateCommand));
            }

            public HashingTableJournal CreateJournal(bool postgres, string schema = null, string table = null)
            {
                return postgres
                    ? (HashingTableJournal)new PostgreSqlHashingJournal(() => Connections.Object, () => Mock.Of<IUpgradeLog>(), schema, table)
                    : new SqlHashingJournal(() => Connections.Object, () => Mock.Of<IUpgradeLog>(), schema, table);
            }

            public IDbCommand CreateCommand()
            {
                var owner = new SqlCommand();
                _parameterOwners.Add(owner);
                var command = new Mock<IDbCommand>();
                command.SetupAllProperties();
                command.SetupGet(c => c.Parameters).Returns(owner.Parameters);
                command.Setup(c => c.CreateParameter()).Returns(() => new SqlParameter());
                command.Setup(c => c.ExecuteScalar()).Returns(() =>
                {
                    if (command.Object.CommandText.Contains("INFORMATION_SCHEMA.TABLES"))
                    {
                        return TableExists ? (object)1 : null;
                    }

                    MetadataQueries.Add((command.Object.CommandText,
                        owner.Parameters.Cast<SqlParameter>().ToDictionary(p => p.ParameterName, p => p.Value)));
                    return HasLockColumn ? (object)1 : null;
                });
                command.Setup(c => c.ExecuteNonQuery()).Returns(() =>
                {
                    if (FailMigration)
                    {
                        throw new InvalidOperationException("Schema change denied");
                    }

                    Writes.Add(command.Object.CommandText);
                    HasLockColumn = true;
                    return 0;
                });
                command.Setup(c => c.ExecuteReader()).Returns(() =>
                {
                    if (command.Object.CommandText.Contains("IsLocked"))
                    {
                        HasLockColumn.Should().BeTrue("migration must precede a query that uses IsLocked");
                    }

                    Reads.Add(command.Object.CommandText);
                    return Rows.CreateDataReader();
                });
                return command.Object;
            }

            public void Dispose()
            {
                Rows.Dispose();
                foreach (var owner in _parameterOwners)
                {
                    owner.Dispose();
                }
            }
        }
    }
}
