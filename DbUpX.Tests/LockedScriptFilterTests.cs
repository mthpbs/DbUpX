using System;
using System.Collections.Generic;
using System.Linq;
using DbUp.Builder;
using DbUp.Engine;
using DbUp.Engine.Transactions;
using DbUp.Helpers;
using FluentAssertions;
using Moq;
using Xunit;

namespace DbUpX.Tests
{
    public class LockedScriptFilterTests
    {
        [Fact]
        public void BothProvidersRespectLocksAndResumeHashComparisonAfterUnlocking()
        {
            foreach (var config in HashingConfigurations())
            {
                var original = new SqlScript("prefix.script.sql", "SELECT 1;\r\n");
                var changed = new SqlScript("prefix.script.sql", "SELECT 2;\n");
                var originalName = Filter(config, original, new HashSet<string>()).Single().Name;
                var locked = new HashSet<string> { "script.sql#*" };

                Filter(config, original, locked).Should().BeEmpty();
                Filter(config, changed, locked).Should().BeEmpty();
                Filter(config, new SqlScript("prefix.other.sql", changed.Contents), locked)
                    .Should().ContainSingle();

                var unlocked = new HashSet<string> { originalName };
                Filter(config, original, unlocked).Should().BeEmpty();
                Filter(config, changed, unlocked).Should().ContainSingle()
                    .Which.Contents.Should().Be(changed.Contents);
            }
        }

        [Fact]
        public void LockedDependenciesRemainAvailableToTheOrderingDelegate()
        {
            foreach (var config in HashingConfigurations())
            {
                var scripts = new[]
                {
                    new SqlScript("prefix.last.sql", "-- #requires middle\nSELECT 3;"),
                    new SqlScript("prefix.middle.sql", "-- #requires first\nSELECT 2;"),
                    new SqlScript("prefix.first.sql", "SELECT 1;")
                };

                config.ScriptFilter.Filter(scripts, new HashSet<string> { "first.sql#*" }, config.ScriptNameComparer)
                    .Select(s => NameWithHash.Parse(s.Name).PlainName)
                    .Should().Equal("middle.sql", "last.sql");
            }
        }

        [Fact]
        public void LockMatchingUsesTheExecutedSetsNameComparison()
        {
            foreach (var config in HashingConfigurations())
            {
                var script = new SqlScript("prefix.script.sql", "SELECT 1;");
                Filter(config, script, new HashSet<string>(StringComparer.Ordinal) { "SCRIPT.SQL#*" })
                    .Should().ContainSingle();
                Filter(config, script, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SCRIPT.SQL#*" })
                    .Should().BeEmpty();
            }
        }

        [Fact]
        public void StandaloneFilterDoesNotInterpretLockMarkers()
        {
            var config = Configure(new UpgradeEngineBuilder().JournalTo(new NullJournal()).WithFilter(scripts => scripts));
            var script = new SqlScript("script.sql#hash", "SELECT 1;");
            Filter(config, script, new HashSet<string> { "script.sql#*" }).Should().ContainSingle();
            Filter(config, script, new HashSet<string> { script.Name }).Should().BeEmpty();
        }

        private static IEnumerable<SqlScript> Filter(UpgradeConfiguration config, SqlScript script, HashSet<string> executed)
        {
            return config.ScriptFilter.Filter(new[] { script }, executed, config.ScriptNameComparer);
        }

        private static IEnumerable<UpgradeConfiguration> HashingConfigurations()
        {
            foreach (var mode in new[] { SqlScriptHashingMode.Raw, SqlScriptHashingMode.NormalizeSqlServer })
            {
                yield return Configure(new UpgradeEngineBuilder().JournalToSqlWithHashing(mode,
                    scripts => scripts.WithPrefix("prefix.").ToArray().OrderByDependency("#requires")));
            }

            foreach (var mode in new[] { SqlScriptHashingMode.Raw, SqlScriptHashingMode.NormalizePostgreSql })
            {
                yield return Configure(new UpgradeEngineBuilder().JournalToPostgreSqlWithHashing(mode,
                    scripts => scripts.WithPrefix("prefix.").ToArray().OrderByDependency("#requires")));
            }
        }

        private static UpgradeConfiguration Configure(UpgradeEngineBuilder builder)
        {
            builder.Configure(config =>
            {
                config.ConnectionManager = Mock.Of<IConnectionManager>();
                config.ScriptExecutor = Mock.Of<IScriptExecutor>();
            });
            return builder.WithScripts(Array.Empty<SqlScript>()).BuildConfiguration();
        }
    }
}
