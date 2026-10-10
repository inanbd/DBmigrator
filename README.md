# DbMigrator

A .NET 10 tool that copies a database from one engine to another — schema, data, keys and
indexes — and tells you exactly what it could not carry over.

**Status:** milestone 1 of 5. SQL Server → PostgreSQL works end to end from the command line.

| Milestone | Scope | State |
|---|---|---|
| 1 | Core engine; SQL Server → PostgreSQL tables, data, keys, indexes; CLI | **Done** |
| 2 | Cross-platform desktop app (Avalonia) wizard over the same engine | Next |
| 3 | Parallel table copies, resume after failure, deeper verification | Planned |
| 4 | Views, procedures, functions, triggers: best-effort translation with side-by-side review | Planned |
| 5 | MySQL and SQLite as sources and targets; every engine both ways | Planned |

## Quick start

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
alias dbmigrator='dotnet src/DbMigrator.Cli/bin/Release/net10.0/dbmigrator.dll'

# Connection strings can come from the environment instead of the command line (and shell history).
export DBMIGRATOR_SOURCE='Server=sql01;Database=Shop;User Id=migrator;Password=...;TrustServerCertificate=true'
export DBMIGRATOR_TARGET='Host=pg01;Database=shop;Username=migrator;Password=...'

# See what would happen, and write the DDL out for review. Changes nothing.
dbmigrator plan --from sqlserver --to postgresql --naming snake --output shop.sql

# Do it.
dbmigrator migrate --from sqlserver --to postgresql --naming snake
```

`plan` without a target connection assumes an empty target and needs no PostgreSQL server.

### Options

| Option | Meaning |
|---|---|
| `--include`, `--exclude` | Glob patterns, repeatable. `sales.*` matches a schema; `Order*` matches table names in any schema. |
| `--naming lower\|snake\|preserve` | `OrderID` becomes `orderid` (default), `order_id`, or stays `OrderID` (and must then be quoted in PostgreSQL). |
| `--schema-map dbo=app` | Where a source schema's tables go. By default `dbo` maps to `public`; other schemas keep their name. |
| `--if-exists fail\|drop\|truncate\|append\|skip` | What to do with tables that already exist in the target. The default, `fail`, lists them and changes nothing. |
| `--schema-only`, `--no-indexes`, `--no-foreign-keys`, `--no-verify` | Leave out parts of the work. |

The exit code is 0 on success, 1 if anything failed, and 130 if cancelled with Ctrl+C. The table being
copied when you cancel is rolled back; tables that were already copied stay.

## What a migration does

1. **Reads** the source catalog in one round trip: tables, columns, defaults, identities, keys, indexes,
   foreign keys and check constraints.
2. **Plans** the target: names, types, defaults and constraints. It also records every compromise as an
   issue (error, warning or note). If there is an error — for example a target table that already exists
   with no action chosen — nothing runs.
3. **Creates the tables** in one transaction, so a failure leaves the target untouched.
4. **Copies data** table by table, parents before children. Rows stream from the source into PostgreSQL's
   binary `COPY` without being held in memory. Each table copies in its own transaction, so a table that
   fails is rolled back and the run carries on with the rest.
5. **Builds primary keys, indexes and foreign keys after loading.** This is much faster than loading into
   indexed tables. Each runs on its own, so one failure does not undo the others. Identity sequences are
   then moved past the copied values.
6. **Counts rows** on both sides and flags any table whose counts differ.

### SQL Server → PostgreSQL type mapping

| SQL Server | PostgreSQL | Notes |
|---|---|---|
| `bit` | `boolean` | |
| `tinyint`, `smallint` | `smallint` | |
| `int`, `bigint` | `integer`, `bigint` | |
| `decimal(p,s)`, `numeric(p,s)` | `numeric(p,s)` | Values with more than about 28 significant digits stop that table's copy (warned at plan time). |
| `money`, `smallmoney` | `numeric(19,4)`, `numeric(10,4)` | |
| `real`, `float` | `real`, `double precision` | |
| `char(n)`, `nchar(n)` | `char(n)` | |
| `varchar(n)`, `nvarchar(n)`, `sysname` | `varchar(n)` | |
| `varchar(max)`, `nvarchar(max)`, `text`, `ntext` | `text` | NUL characters are removed (PostgreSQL text cannot hold them) and counted in a warning. |
| `binary`, `varbinary`, `image` | `bytea` | |
| `rowversion` | `bytea`, nullable | No longer set automatically. |
| `date` | `date` | |
| `time(p)`, `datetime2(p)` | `time(p)`, `timestamp(p)` | PostgreSQL keeps 6 fractional digits; the 7th is dropped. |
| `datetime`, `smalldatetime` | `timestamp(3)`, `timestamp(0)` | |
| `datetimeoffset(p)` | `timestamptz(p)` | Stored as the same instant in UTC; the original offset is not kept. |
| `uniqueidentifier` | `uuid` | |
| `xml` | `xml` | |
| `json` (SQL Server 2025) | `jsonb` | |
| `sql_variant`, `hierarchyid`, `geography`, `geometry`, CLR types | `text` | Copied as their text form (WKT for spatial types). |
| Alias types (`CREATE TYPE ... FROM`) | their base type | |

Identities become `GENERATED BY DEFAULT AS IDENTITY`, starting from the same seed with the same step.
Defaults are translated when they are literals or common functions (`GETDATE()`, `SYSUTCDATETIME()`,
`NEWID()`, `CONVERT(date, GETDATE())`…). Unique indexes keep SQL Server's one-NULL rule through
`NULLS NOT DISTINCT` (PostgreSQL 15+). Foreign keys that SQL Server never checked (`WITH NOCHECK`)
are created `NOT VALID`. Constraint names that SQL Server generated (`PK__Orders__C3905BAF…`) get
readable names like `pk_orders`. Index names are made unique per schema, as PostgreSQL requires.
Names longer than 63 bytes are shortened, with a hash suffix to keep them distinct.

### Reported, not migrated (yet)

These appear as warnings with the original SQL, so you can recreate them by hand:

- Check constraints, filtered indexes, computed-column expressions and other defaults: their source SQL
  needs translating. Computed columns are copied as regular nullable columns holding the computed values.
  The translation itself comes in milestone 4.
- Columnstore, XML and spatial indexes (no general equivalent) and disabled indexes. Full-text indexes are not read.
- Views, procedures, functions and triggers are not read yet (milestone 4).

Appending to or truncating an existing table assumes it has the shape this tool would create, for
example from an earlier run. Read from a database that nobody is writing to, or the row counts checked
at the end may legitimately differ.

## Layout

```
src/
  DbMigrator.Core                    Engine-neutral schema model, planner, script builder, copy engine
  DbMigrator.Providers.SqlServer     Catalog reader, type mapping, default parsing (Microsoft's T-SQL parser), row streaming
  DbMigrator.Providers.PostgreSql    Dialect (types, DDL, defaults), binary COPY bulk writer
  DbMigrator.Cli                     dbmigrator command (System.CommandLine)
tests/
  DbMigrator.Tests                   Unit tests: naming, planning, type and default mapping, DDL
  DbMigrator.IntegrationTests        Real SQL Server and PostgreSQL in Docker (Testcontainers)
```

Each engine implements `IDatabaseProvider`: an `ISourceDatabase` (read schema, stream rows), an
`ITargetDatabase` (inspect, execute, bulk-load) and an `ISqlDialect` (names, types, DDL). Every value
crosses between them as one CLR type per kind (`CanonicalValues`), so any source pairs with any target.
The planner is pure, and `MigrationEngine.Prepare` can re-plan with new options without reading the
source again. The desktop app will use that to ask what to do about tables that already exist.

## Tests

```bash
dotnet test
```

The integration tests need Docker: they start SQL Server 2022 and PostgreSQL 17 containers once per
run. To use servers you already have instead, set `DBMIGRATOR_TEST_SQLSERVER` and
`DBMIGRATOR_TEST_POSTGRES` to admin connection strings. Each test creates its own databases.
