using System;
using System.Data;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.SqlServer;

namespace DbUpX
{
    /// <summary>
    /// Implements <see cref="DbUp.Engine.IJournal"/> to store hashed script
    /// contents as well as the usual ScriptName and Applied date.
    /// </summary>
    public class SqlHashingJournal : HashingTableJournal
    {
        public SqlHashingJournal(
            Func<IConnectionManager> connections, 
            Func<IUpgradeLog> logger,
            string schemaName,
            string tableName)
            : this(connections, logger, schemaName, tableName, SqlScriptHashingMode.Raw)
        {
        }

        /// <summary>Creates a SQL Server journal using raw or SQL Server normalized hashes.</summary>
        public SqlHashingJournal(
            Func<IConnectionManager> connections,
            Func<IUpgradeLog> logger,
            string schemaName,
            string tableName,
            SqlScriptHashingMode hashingMode)
            : base(connections, logger, new SqlServerObjectParser(), schemaName, tableName, hashingMode)
        {
            SqlScriptContentNormalizer.ValidateProviderMode(hashingMode, SqlScriptHashingMode.NormalizeSqlServer);
        }

        protected override string CreateSchemaTableSql()
        {
            return
                $@"create table {FqSchemaTableName} (
                    [ScriptName] nvarchar(255) not null,
                    [ContentsHash] nvarchar(255) not null,
                    [Applied] datetime not null,
                    [IsLocked] bit not null default (0)
                )";
        }

        /// <summary>Adds the lock column to an existing journal without changing its entries.</summary>
        protected override void UpgradeTableIfRequired(Func<IDbCommand> db)
        {
            var exists = db.ExecuteScalar(
                "select 1 from sys.columns where object_id = OBJECT_ID(@tableName) and name = @columnName",
                new { tableName = FqSchemaTableName, columnName = "IsLocked" });
            if (exists == null)
            {
                db.Execute($"alter table {FqSchemaTableName} add [IsLocked] bit not null default (0)");
            }
        }

        protected override string GetDeleteScriptSql()
        {
            return $"delete from {FqSchemaTableName} where [ScriptName] = @scriptName";
        }

        protected override string GetInsertScriptSql()
        {
            return $@"insert into {FqSchemaTableName} ([ScriptName], [ContentsHash], [Applied]) 
                      values (@scriptName, @contentsHash, GETUTCDATE())";
        }

        protected override string GetJournalEntriesSql()
        {
            return $"select [ScriptName], case when [IsLocked] = 1 then '{LockedHash}' else [ContentsHash] end from {FqSchemaTableName}";
        }
    }
}
