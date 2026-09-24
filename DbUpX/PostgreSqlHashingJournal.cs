using System;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Postgresql;

namespace DbUpX
{
    /// <summary>
    /// Implements <see cref="DbUp.Engine.IJournal"/> to store hashed script
    /// contents as well as the usual ScriptName and Applied date for PostgreSQL databases.
    /// </summary>
    public class PostgreSqlHashingJournal : HashingTableJournal
    {
        public PostgreSqlHashingJournal(
            Func<IConnectionManager> connections, 
            Func<IUpgradeLog> logger,
            string schemaName,
            string tableName)
            : this(connections, logger, schemaName, tableName, SqlScriptHashingMode.Raw)
        {
        }

        /// <summary>Creates a PostgreSQL journal using raw or PostgreSQL normalized hashes.</summary>
        public PostgreSqlHashingJournal(
            Func<IConnectionManager> connections,
            Func<IUpgradeLog> logger,
            string schemaName,
            string tableName,
            SqlScriptHashingMode hashingMode)
            : base(connections, logger, new PostgresqlObjectParser(), schemaName ?? "public", tableName, hashingMode)
        {
            SqlScriptContentNormalizer.ValidateProviderMode(hashingMode, SqlScriptHashingMode.NormalizePostgreSql);
        }

        protected override string CreateSchemaTableSql()
        {
            return
                $@"create table {FqSchemaTableName} (
                    ""ScriptName"" varchar(255) not null,
                    ""ContentsHash"" varchar(255) not null,
                    ""Applied"" timestamp not null
                )";
        }

        protected override string GetDeleteScriptSql()
        {
            return $"delete from {FqSchemaTableName} where \"ScriptName\" = @scriptName";
        }

        protected override string GetInsertScriptSql()
        {
            return $@"insert into {FqSchemaTableName} (""ScriptName"", ""ContentsHash"", ""Applied"") 
                      values (@scriptName, @contentsHash, CURRENT_TIMESTAMP)";
        }

        protected override string GetJournalEntriesSql()
        {
            return $"select \"ScriptName\", \"ContentsHash\" from {FqSchemaTableName}";
        }
    }
}
