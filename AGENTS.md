# AGENTS.md

## Purpose
This document guides autonomous coding agents working in this repository.
It captures build/test commands and repository-specific coding conventions.
Follow these rules before introducing new patterns.

## Repository Snapshot
- Solution: `DbUpX.sln`
- Library project: `DbUpX/DbUpX.csproj` (`netstandard2.0`)
- Test project: `DbUpX.Tests/DbUpX.Tests.csproj` (`net10.0`)
- Main language: C#
- Test framework: xUnit + FluentAssertions + Moq
- Domain: DbUp extensions for script filtering, dependency ordering, and hash-based journaling

## Architecture

The one idea that ties the library together: DbUp's `IJournal.GetExecutedScripts()`
returns only `string[]`, with no place to carry a content hash. DbUpX works around
this by **encoding the hash into the script name** as `"plainName#hash"`
(`NameWithHash`, SHA256 base64 of the UTF-8 contents). Everything else follows:

- **`BuilderExtensions`** (entry point) — `JournalToSqlWithHashing` /
  `JournalToPostgreSqlWithHashing` call DbUp's `builder.Configure(...)` to install
  (a) a hashing `Journal` and (b) a `ScriptFilter` that runs the caller's optional
  filter delegate and then appends `#hash` to every name via `HashNames()`.
  `WithFilter` installs just a filter with no hashing.
- **The filter loop** — the installed `DelegatedFilter` (an `IScriptFilter`) applies
  the sort/filter, then drops scripts whose (already-hashed) name is in DbUp's
  executed set. So a script re-runs automatically iff its contents changed (its hash,
  hence its name, changed). This replaces DbUp's "run once vs run always" distinction:
  unchanged scripts are skipped, changed ones re-run.
- **`HashingTableJournal`** (abstract `IJournal`) — stores `ScriptName` +
  `ContentsHash` + `Applied` in `SchemaVersionHash` (default schema `dbo`/`public`).
  Vendor SQL lives only in the subclasses `SqlHashingJournal` /
  `PostgreSqlHashingJournal`; identifier quoting goes through DbUp's `ISqlObjectParser`,
  and the table-existence check is parameterized (`@tableName`/`@tableSchema`) to avoid
  SQL injection — see `HashingTableJournalSecurityTests`.
- **`SqlScriptEnumerableExtensions`** (note: the class is `SqlScriptDependencyExtensions`)
  — the LINQ toolkit callers compose inside their filter delegate: `WithPrefix` (keep
  scripts matching a namespace prefix *and strip that prefix* so journal names survive
  code moves), `HashNames`, and `OrderByDependency` (topological sort driven by a
  `-- #requires A, B` comment; throws on missing, ambiguous, or cyclic dependencies).
- **`DatabaseExtensions`** — a minimal Dapper-like helper
  (`Execute`/`ExecuteScalar`/`Query<T>`) built over DbUp's `Func<IDbCommand>` command
  factory instead of an `IDbConnection`. `Query<T>` casts directly for single-column
  results and otherwise matches a constructor to the column types (works with
  `ValueTuple`). Used by the journals and the tests.

Typical caller usage is a fluent DbUp chain whose filter argument composes the LINQ
extensions above; `README.md` has full SQL Server and PostgreSQL examples.

## Rule Sources (Cursor / Copilot)
- Checked `.cursorrules`: not present
- Checked `.cursor/rules/`: not present
- Checked `.github/copilot-instructions.md`: not present
- Therefore, no extra editor/assistant rule files apply right now

## Environment And Tooling
- Use .NET SDK compatible with test target `net10.0`
- Use `dotnet` CLI from repository root
- Docker is needed for SQL Server integration tests
- Ignore build artifacts (`bin/`, `obj/`) per `.gitignore`

## Build Commands
Run from repo root unless noted.

- Restore all projects:
  - `dotnet restore DbUpX.sln`
- Build all projects:
  - `dotnet build DbUpX.sln -c Release`
- Build library only:
  - `dotnet build DbUpX/DbUpX.csproj -c Release`
- Build tests only:
  - `dotnet build DbUpX.Tests/DbUpX.Tests.csproj -c Release`
- Create NuGet package (library has `PackOnBuild=true`):
  - `dotnet pack DbUpX/DbUpX.csproj -c Release`

## Lint / Formatting / Static Analysis
There is no dedicated linter/analyzer config file in this repo
(no `.editorconfig`, no ruleset, no `Directory.Build.*` style settings).

Use these practical checks:
- Format check (non-destructive validation):
  - `dotnet format DbUpX.sln --verify-no-changes`
- If formatting is needed:
  - `dotnet format DbUpX.sln`

Agents should not introduce a new formatter/analyzer config unless requested.

## Test Commands

### Run all tests
- `dotnet test DbUpX.sln -c Release`

### Run test project only
- `dotnet test DbUpX.Tests/DbUpX.Tests.csproj -c Release`

### Run a single test class
- `dotnet test DbUpX.Tests/DbUpX.Tests.csproj -c Release --filter "FullyQualifiedName~DbUpX.Tests.NameWithHashTests"`

### Run a single test method (important)
- `dotnet test DbUpX.Tests/DbUpX.Tests.csproj -c Release --filter "FullyQualifiedName=DbUpX.Tests.NameWithHashTests.AddsHash"`

### Run tests by method name contains
- `dotnet test DbUpX.Tests/DbUpX.Tests.csproj -c Release --filter "Name~AddsHash"`

### Exclude integration tests when local SQL Server is unavailable
- `dotnet test DbUpX.Tests/DbUpX.Tests.csproj -c Release --filter "FullyQualifiedName!~IntegrationTests"`

## Integration Test Notes
`IntegrationTests` use a real SQL Server. Connection credentials are read from
environment variables (no credentials are hard-coded in the test source):

| Variable | Purpose |
| --- | --- |
| `DBUPX_TEST_SQLSERVER` | SQL Server data source (host/instance) |
| `DBUPX_TEST_SQLUSER` | SQL login user |
| `DBUPX_TEST_SQLPASSWORD` | SQL login password |

The database name is fixed at `DbUpIntegrationTests` (not a credential, not
configurable). If any of the three variables is unset, the integration tests
**fail fast** with an `InvalidOperationException` naming the missing variable —
they do not fall back to defaults.

Each integration test **drops and recreates** the `DbUpIntegrationTests` database,
so it is destructive to anything under that name on the target server.

Spin up a matching server with `start-sql.sh` (Docker; SQL Server 2017, port 1433,
SA password `P@ssw0rd`), then set the variables to match it before running:

```powershell
# PowerShell
$env:DBUPX_TEST_SQLSERVER = "localhost"
$env:DBUPX_TEST_SQLUSER = "sa"
$env:DBUPX_TEST_SQLPASSWORD = "P@ssw0rd"
```

```bash
# bash
export DBUPX_TEST_SQLSERVER=localhost
export DBUPX_TEST_SQLUSER=sa
export DBUPX_TEST_SQLPASSWORD='P@ssw0rd'
```

If integration tests fail in CI/local, run the non-integration tests only via
filter (`--filter "FullyQualifiedName!~IntegrationTests"`) and treat integration
runs as environment-dependent.

## CI / Publishing
`.github/workflows/publish-nuget.yml` builds, runs the non-integration tests, packs,
and pushes the package to NuGet.

- Triggered by pushing a `vX.Y.Z` tag, or manually via `workflow_dispatch` with a
  `version` input.
- The package version comes from the tag/input (regex-validated), **not** from
  `PackageVersion` in `DbUpX/DbUpX.csproj`.
- Publishing runs in the `production` GitHub environment and authenticates via NuGet
  OIDC login, with an API-key fallback.

## Code Style Guidelines

### Imports / Usings
- Keep `using` directives at file top.
- Order `System.*` usings first, then third-party, then project namespaces.
- Avoid unused usings.
- Prefer explicit usings over global-usings introduction unless requested.

### Formatting
- Use 4 spaces for indentation.
- Use Allman braces (`{` on next line) consistently.
- Keep line lengths readable; wrap fluent chains across lines when needed.
- Preserve existing whitespace style in touched files.
- Avoid trailing whitespace.

### Naming Conventions
- Public types/members: PascalCase (`NameWithHash`, `GetExecutedScripts`).
- Private fields: `_camelCase` (`_prefix`, `_sort`).
- Local variables: camelCase.
- Constants: PascalCase (`DatabaseName`).
- Test method names: behavior-focused PascalCase sentences
  (`ComplainsAboutCycle`, `FilterControlsOrderAndWithPrefixRemovesPrefix`).

### Types And Language Features
- Prefer explicit return types on public APIs.
- Use `var` when RHS makes type obvious; otherwise prefer explicit type.
- Use expression-bodied members for trivial one-liners only.
- Prefer immutable/readonly fields when values do not change.
- Keep extension method signatures clear and minimal.
- Respect target framework compatibility of library (`netstandard2.0`).

### Nullability And Defensive Coding
- This repo does not enable nullable reference types globally.
- Validate assumptions at boundaries and throw clear exceptions.
- Use specific exception types (`ArgumentException`, `InvalidOperationException`).
- Include actionable exception messages, especially for dependency resolution.

### LINQ And Collections
- Use LINQ for script sequence transformations (`Where`, `Select`, `Concat`).
- Use query syntax only when it improves readability.
- Materialize (`ToList`, `ToArray`) only when required.
- Avoid multiple expensive enumerations unless intentional.

### Error Handling
- Fail fast on invalid states (missing/ambiguous/cyclic dependencies).
- Prefer explicit checks with descriptive messages over silent fallback.
- Preserve exception semantics in public extension methods.

### SQL / Database Practices
- Keep SQL statements parameterized when injecting values.
- Keep DB-vendor-specific SQL confined to journal implementations.
- Prefer explicit schema/table quoting through parser utilities.
- Preserve idempotent/upgrade-safe migration behavior.

### Public API And Documentation
- Public extension points should include XML doc comments.
- Keep docs focused on behavior and side effects.
- When changing API behavior, update README usage examples if needed.

### Test Conventions
- Use xUnit `[Fact]` for unit tests.
- Use FluentAssertions for readable assertions.
- Use Moq for collaborator boundaries (for example, executor and connection manager).
- Keep tests deterministic and isolated unless explicitly integration tests.
- Prefer small test scripts inline for clarity.

## Agent Workflow Expectations
- Make minimal, focused changes.
- Do not refactor unrelated code while implementing a task.
- Preserve backward compatibility unless task explicitly changes API behavior.
- Update/add tests for behavior changes.
- Run targeted tests first, then broader tests.
- Document any environment assumptions in PR notes.

## Pre-PR Checklist For Agents
- Build succeeds:
  - `dotnet build DbUpX.sln -c Release`
- Relevant tests pass (single test/class plus full intended scope).
- Formatting is clean (`dotnet format ... --verify-no-changes` if used).
- No accidental edits to unrelated files.
- No credentials/secrets added.
- Docs updated when public behavior changes.
