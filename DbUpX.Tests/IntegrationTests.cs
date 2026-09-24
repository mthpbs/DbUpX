using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using DbUp;
using DbUp.Engine;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbUpX.Tests
{
    public class IntegrationTests
    {
        private static string RequireEnv(string name) =>
            Environment.GetEnvironmentVariable(name)
                ?? throw new InvalidOperationException(
                    $"Integration tests require environment variable '{name}'. " +
                    "See AGENTS.md > Integration Test Notes for setup.");

        private static string MakeConnectionString(string database)
        {
            return new SqlConnectionStringBuilder
            {
                DataSource = RequireEnv("DBUPX_TEST_SQLSERVER"),
                InitialCatalog = database,
                UserID = RequireEnv("DBUPX_TEST_SQLUSER"),
                Password = RequireEnv("DBUPX_TEST_SQLPASSWORD"),
                TrustServerCertificate = true,
                MultipleActiveResultSets = true
            }
            .ConnectionString;
        }

        private const string DatabaseName = "DbUpIntegrationTests";

        private readonly string ConnectionString;

        private readonly string MasterConnectionString;

        private static T WithDB<T>(string connectionString, Func<Func<IDbCommand>, T> op)
        {
            using (var db = new SqlConnection(connectionString))
            {
                db.Open();
                return op(db.CreateCommand);
            }
        }

        public IntegrationTests()
        {
            ConnectionString = MakeConnectionString(DatabaseName);
            MasterConnectionString = MakeConnectionString("master");

            WithDB(MasterConnectionString, db => db.Execute($@"
                if DB_ID('{DatabaseName}') is not null
                begin
                    alter database {DatabaseName} set SINGLE_USER with rollback immediate;
                    drop database {DatabaseName};
                end"));

            EnsureDatabase.For.SqlDatabase(ConnectionString);
        }

        [Fact]
        public void RunsScripts()
        {
            var deployer = DeployChanges.To
                    .SqlDatabase(ConnectionString)
                    .JournalToSqlWithHashing()
                    .WithScripts(
                        new SqlScript("test1", @"
                            create table Frog (Eyes int)
                        "),
                        new SqlScript("test2", @"
                            insert into Frog (Eyes) values (2)
                        "))
                    .Build();

            deployer.PerformUpgrade().Successful.Should().BeTrue();

            WithDB(ConnectionString, db => (int)db.ExecuteScalar("select Eyes from Frog"))
                .Should().Be(2);

            WithDB(ConnectionString, db => db.Query<string>(
                @"select ScriptName from SchemaVersionHash
                  where len(ContentsHash) > 0
                  order by Applied"))
                .Should().ContainInOrder(new[] {
                    "test1", "test2"
                });
        }

        private IDictionary<string, string> GetHashes()
        {
            return WithDB(ConnectionString, db => db.Query<(string name, string hash)>(
                @"select ScriptName, ContentsHash from SchemaVersionHash"))
                .ToDictionary(x => x.name, x => x.hash);
        }

        [Fact]
        public void LockedScriptSkipsChangesAndRunsAfterUnlocking()
        {
            UpgradeEngine BuildUpgrader(int value)
            {
                return DeployChanges.To.SqlDatabase(ConnectionString)
                    .JournalToSqlWithHashing(scripts => scripts.WithPrefix("prefix."))
                    .WithScripts(
                        new SqlScript("prefix.create.sql", "create table Frog (Eyes int)"),
                        new SqlScript("prefix.update.sql", $"delete from Frog; insert into Frog (Eyes) values ({value})"))
                    .Build();
            }

            BuildUpgrader(2).PerformUpgrade().Successful.Should().BeTrue();
            WithDB(ConnectionString, db => db.Execute(
                "update dbo.SchemaVersionHash set IsLocked = 1 where ScriptName = @name",
                new { name = "update.sql" })).Should().Be(1);
            var original = WithDB(ConnectionString, db => db.Query<(string, DateTime, bool)>(
                "select ContentsHash, Applied, IsLocked from dbo.SchemaVersionHash where ScriptName = 'update.sql'"))
                .Single();

            var changed = BuildUpgrader(3);
            changed.GetScriptsToExecute().Should().BeEmpty();
            changed.PerformUpgrade().Successful.Should().BeTrue();
            WithDB(ConnectionString, db => (int)db.ExecuteScalar("select Eyes from Frog")).Should().Be(2);
            WithDB(ConnectionString, db => db.Query<(string, DateTime, bool)>(
                "select ContentsHash, Applied, IsLocked from dbo.SchemaVersionHash where ScriptName = 'update.sql'"))
                .Should().Equal(original);

            WithDB(ConnectionString, db => db.Execute(
                "update dbo.SchemaVersionHash set IsLocked = 0 where ScriptName = @name",
                new { name = "update.sql" })).Should().Be(1);
            changed.GetScriptsToExecute().Should().ContainSingle();
            changed.PerformUpgrade().Successful.Should().BeTrue();
            WithDB(ConnectionString, db => (int)db.ExecuteScalar("select Eyes from Frog")).Should().Be(3);
            GetHashes()["update.sql"].Should().NotBe(original.Item1);
            changed.GetScriptsToExecute().Should().BeEmpty();
        }

        [Fact]
        public void PendingScriptCheckUpgradesLegacyJournalWithoutChangingExistingEntries()
        {
            const string contents = "SELECT 1;";
            var hash = NameWithHash.GenerateHash(contents);
            var applied = new DateTime(2020, 1, 1);
            WithDB(ConnectionString, db => db.Execute(@"
                create schema journal;
            "));
            WithDB(ConnectionString, db => db.Execute(@"
                create table journal.CustomHistory (
                    ScriptName nvarchar(255) not null,
                    ContentsHash nvarchar(255) not null,
                    Applied datetime not null
                );
                insert into journal.CustomHistory values (@name, @hash, @applied)",
                new { name = "script.sql", hash, applied }));

            var upgrader = DeployChanges.To.SqlDatabase(ConnectionString)
                .JournalToSqlWithHashing(schemaName: "journal", tableName: "CustomHistory")
                .WithScripts(new SqlScript("script.sql", contents))
                .Build();

            upgrader.GetScriptsToExecute().Should().BeEmpty();
            upgrader.GetScriptsToExecute().Should().BeEmpty();
            upgrader.PerformUpgrade().Successful.Should().BeTrue();
            WithDB(ConnectionString, db => db.Query<(string, string, DateTime, bool)>(
                "select ScriptName, ContentsHash, Applied, IsLocked from journal.CustomHistory"))
                .Should().Equal(("script.sql", hash, applied, false));

            WithDB(ConnectionString, db => db.Execute(
                "insert into journal.CustomHistory (ScriptName, ContentsHash, Applied) values ('other.sql', @hash, @applied)",
                new { hash, applied }));
            WithDB(ConnectionString, db => (bool)db.ExecuteScalar(
                "select IsLocked from journal.CustomHistory where ScriptName = 'other.sql'"))
                .Should().BeFalse();
        }

        [Fact]
        public void UpgradesOneScript()
        {
            var deployer = DeployChanges.To
                    .SqlDatabase(ConnectionString)
                    .JournalToSqlWithHashing()
                    .WithScripts(
                        new SqlScript("test1", @"
                            create table Frog (Eyes int)
                        "),
                        new SqlScript("test2", @"
                            insert into Frog (Eyes) values (2)
                        "))
                    .LogToConsole()
                    .Build();

            deployer.PerformUpgrade().Successful.Should().BeTrue();

            var hashes1 = GetHashes();
            hashes1.Count().Should().Be(2);

            deployer = DeployChanges.To
                    .SqlDatabase(ConnectionString)
                    .WithScripts(
                        new SqlScript("test1", @"
                            create table Frog (Eyes int)
                        "),
                        new SqlScript("test2", @"
                            delete from Frog
                            insert into Frog (Eyes) values (3)
                        "))
                    .LogToConsole()
                    .JournalToSqlWithHashing()
                    .Build();

            deployer.PerformUpgrade().Successful.Should().BeTrue();

            WithDB(ConnectionString, db => (int)db.ExecuteScalar("select Eyes from Frog"))
                .Should().Be(3);

            var hashes2 = GetHashes();
            hashes2.Count().Should().Be(2);

            hashes2["test1"].Should().Be(hashes1["test1"]);
            hashes2["test2"].Should().NotBe(hashes1["test2"]);
        }
    }
}
