using System;
using System.Collections.Generic;
using System.Linq;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Support;

namespace DbUpX
{
    public static class BuilderExtensions
    {
        private class DelegatedFilter : IScriptFilter
        {
            private readonly Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> _sort;
            private readonly bool _respectLocks;

            public DelegatedFilter(Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> sort, bool respectLocks = false)
            {
                _sort = sort;
                _respectLocks = respectLocks;
            }

            public IEnumerable<SqlScript> Filter(
                IEnumerable<SqlScript> sorted,
                HashSet<string> executedScriptNames,
                ScriptNameComparer comparer)
            {
                return _sort(sorted).Where(s => !executedScriptNames.Contains(s.Name) &&
                    (!_respectLocks || !executedScriptNames.Contains(
                        new NameWithHash(NameWithHash.Parse(s.Name).PlainName, HashingTableJournal.LockedHash).ToString())));
            }
        }

        /// <summary>
        /// Applies a filter to the upgrade pipeline, allowing the list of scripts
        /// to be sorted and filtered immediately before execution.
        /// 
        /// Scripts that have previously been executed will not run again.
        /// </summary>
        /// <returns>The filtered scripts.</returns>
        /// <param name="builder">Builder.</param>
        /// <param name="filter">A delegate that takes the input set of scripts
        /// and returns them sorted and filtered.</param>
        public static UpgradeEngineBuilder WithFilter(
            this UpgradeEngineBuilder builder,
            Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> filter)
        {
            builder.Configure(config => config.ScriptFilter = new DelegatedFilter(filter));
            return builder;
        }

        /// <summary>
        /// Configures hashing script contents and saving them to the journal table for SQL Server.
        /// This means that if a script is not changed, it won't be re-run, but if it is
        /// changed then it will, unless its journal entry is locked. This avoids the need to treat "run always" and
        /// "run once" scripts differently
        /// 
        /// A filter is also installed that ensures script names include the hash.
        /// You can optionally provide your own additional filtering.
        /// </summary>
        /// <returns>The to sql with hashing.</returns>
        /// <param name="builder">Builder.</param>
        /// <param name="filter">Filter.</param>
        /// <param name="schemaName">Schema name.</param>
        /// <param name="tableName">Table name.</param>
        public static UpgradeEngineBuilder JournalToSqlWithHashing(
            this UpgradeEngineBuilder builder,
            Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> filter = null,
            string schemaName = null,
            string tableName = null)
        {
            return builder.JournalToSqlWithHashing(SqlScriptHashingMode.Raw, filter, schemaName, tableName);
        }

        /// <summary>
        /// Configures SQL Server filtering and journaling with the same hashing mode.
        /// Opting into normalization can rerun unlocked scripts recorded with legacy raw hashes.
        /// </summary>
        public static UpgradeEngineBuilder JournalToSqlWithHashing(
            this UpgradeEngineBuilder builder,
            SqlScriptHashingMode hashingMode,
            Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> filter = null,
            string schemaName = null,
            string tableName = null)
        {
            SqlScriptContentNormalizer.ValidateProviderMode(hashingMode, SqlScriptHashingMode.NormalizeSqlServer);
            builder.Configure(config =>
            {
                config.Journal = new SqlHashingJournal(
                    () => config.ConnectionManager,
                    () => config.Log,
                    schemaName,
                    tableName,
                    hashingMode);

                config.ScriptFilter = new DelegatedFilter(
                    scripts => (filter != null 
                                    ? filter(scripts) 
                                    : scripts)
                                        .HashNames(hashingMode), respectLocks: true);
            });

            return builder;
        }

        /// <summary>
        /// Configures hashing script contents and saving them to the journal table for PostgreSQL.
        /// This means that if a script is not changed, it won't be re-run, but if it is
        /// changed then it will, unless its journal entry is locked. This avoids the need to treat "run always" and
        /// "run once" scripts differently
        /// 
        /// A filter is also installed that ensures script names include the hash.
        /// You can optionally provide your own additional filtering.
        /// </summary>
        /// <returns>The to PostgreSQL with hashing.</returns>
        /// <param name="builder">Builder.</param>
        /// <param name="filter">Filter.</param>
        /// <param name="schemaName">Schema name.</param>
        /// <param name="tableName">Table name.</param>
        public static UpgradeEngineBuilder JournalToPostgreSqlWithHashing(
            this UpgradeEngineBuilder builder,
            Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> filter = null,
            string schemaName = null,
            string tableName = null)
        {
            return builder.JournalToPostgreSqlWithHashing(SqlScriptHashingMode.Raw, filter, schemaName, tableName);
        }

        /// <summary>
        /// Configures PostgreSQL filtering and journaling with the same hashing mode.
        /// Opting into normalization can rerun unlocked scripts recorded with legacy raw hashes.
        /// </summary>
        public static UpgradeEngineBuilder JournalToPostgreSqlWithHashing(
            this UpgradeEngineBuilder builder,
            SqlScriptHashingMode hashingMode,
            Func<IEnumerable<SqlScript>, IEnumerable<SqlScript>> filter = null,
            string schemaName = null,
            string tableName = null)
        {
            SqlScriptContentNormalizer.ValidateProviderMode(hashingMode, SqlScriptHashingMode.NormalizePostgreSql);
            builder.Configure(config =>
            {
                config.Journal = new PostgreSqlHashingJournal(
                    () => config.ConnectionManager,
                    () => config.Log,
                    schemaName,
                    tableName,
                    hashingMode);

                config.ScriptFilter = new DelegatedFilter(
                    scripts => (filter != null 
                                    ? filter(scripts) 
                                    : scripts)
                                        .HashNames(hashingMode), respectLocks: true);
            });

            return builder;
        }
    }
}
