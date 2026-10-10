# Working in this repository

DbMigrator copies schema and data between database engines (.NET 10). See README.md for the
milestone plan; milestone 1 (SQL Server → PostgreSQL, CLI) is done.

## Build and test

```bash
dotnet build                       # warnings are errors (analyzers at latest-recommended)
dotnet test                        # unit + integration; integration needs Docker
dotnet format whitespace --verify-no-changes
```

Tests run on Microsoft.Testing.Platform (opted in via `global.json`). Integration tests start SQL Server
and PostgreSQL through Testcontainers, or use `DBMIGRATOR_TEST_SQLSERVER` / `DBMIGRATOR_TEST_POSTGRES`.
If Docker is installed but not running in a cloud session, `service docker start` brings it up.

## Design rules

- **Canonical values.** Row readers produce, and bulk writers accept, exactly one CLR type per
  `DataTypeKind` (`CanonicalValues.ClrTypeOf`). A new provider converts at its edge; the engine never does.
- **The planner is pure.** Anything engine-specific goes through `ISqlDialect` (target) or the source
  provider; `MigrationPlanner` and `ScriptBuilder` touch no database.
- **Report, don't guess.** When something cannot be carried over exactly, add a `MigrationIssue` with a
  code from `IssueCodes` and the original SQL, rather than silently dropping or approximating it.
- **Dates into PostgreSQL go as raw values** (microseconds/days since 2000-01-01). Writing `DateTime`
  would let Npgsql turn `MinValue`/`MaxValue` into ±infinity.
- **Package versions** are pinned centrally in `Directory.Packages.props`.
