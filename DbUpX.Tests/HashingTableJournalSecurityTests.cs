using System;
using System.Collections.Generic;
using System.Linq;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.SqlServer;
using FluentAssertions;
using Xunit;

namespace DbUpX.Tests
{
    public class HashingTableJournalSecurityTests
    {
        private sealed class TestHashingTableJournal : HashingTableJournal
        {
            public TestHashingTableJournal(string schemaName, string tableName)
                : base(
                    () => null,
                    () => null,
                    new SqlServerObjectParser(),
                    schemaName,
                    tableName)
            {
            }

            public string GetDoesTableExistSql()
            {
                return DoesTableExistSql();
            }

            public object GetDoesTableExistSqlArgs()
            {
                return DoesTableExistSqlArgs();
            }

            protected override string CreateSchemaTableSql()
            {
                return string.Empty;
            }

            protected override string GetJournalEntriesSql()
            {
                return string.Empty;
            }

            protected override string GetInsertScriptSql()
            {
                return string.Empty;
            }

            protected override string GetDeleteScriptSql()
            {
                return string.Empty;
            }
        }

        [Fact]
        public void DoesTableExistQueryUsesParametersForSchemaAndTable()
        {
            var schemaName = "dbo' OR 1=1 --";
            var tableName = "SchemaVersionHash'; DROP TABLE Users;--";
            var journal = new TestHashingTableJournal(schemaName, tableName);

            var sql = journal.GetDoesTableExistSql();
            var args = ToDictionary(journal.GetDoesTableExistSqlArgs());

            sql.Should().Contain("TABLE_NAME = @tableName");
            sql.Should().Contain("TABLE_SCHEMA = @tableSchema");
            sql.Should().NotContain(tableName);
            sql.Should().NotContain(schemaName);

            args["tableName"].Should().Be(tableName);
            args["tableSchema"].Should().Be(schemaName);
        }

        [Fact]
        public void DoesTableExistQueryOmitsSchemaParameterWhenSchemaIsEmpty()
        {
            var journal = new TestHashingTableJournal(string.Empty, "SchemaVersionHash");

            var sql = journal.GetDoesTableExistSql();
            var args = ToDictionary(journal.GetDoesTableExistSqlArgs());

            sql.Should().Contain("TABLE_NAME = @tableName");
            sql.Should().NotContain("TABLE_SCHEMA = @tableSchema");

            args.Should().ContainKey("tableName");
            args.Should().NotContainKey("tableSchema");
        }

        private static IReadOnlyDictionary<string, object> ToDictionary(object args)
        {
            return args.GetType()
                       .GetProperties()
                       .ToDictionary(x => x.Name, x => x.GetValue(args));
        }
    }
}
