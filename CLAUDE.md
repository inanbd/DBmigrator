# Working in this repository

DbMigrator copies schema and data between database engines (.NET 10). See README.md for the
milestone plan; milestones 1 (SQL Server → PostgreSQL, CLI) and 2 (Avalonia desktop wizard) are done.

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

## Desktop app

- **View models never reference Avalonia** (`ArchitectureTests`); neither does Platform. Views bind with
  compiled bindings; there is no DI container (`ShellServices`, built in `CompositionRoot`).
- **Nothing blocking on the UI thread.** Every engine, provider, credential-store and file call goes
  through `BackgroundWork`: Core awaits without `ConfigureAwait`, so an engine call made from the UI thread
  would run there. The view-model tests run on a fake UI thread that catches it (`UiThreadContext`).
- **Progress is pulled**, through `RunProgressHub` on a 250 ms `IUiScheduler` timer; never `Progress<T>`.
- **Secrets** are persisted only through `ICredentialStore` (the OS store, never a file). Profiles are
  created only by `ProfileFactory`, which strips them; reports and logs use `Describe`/`Redact` only.
- **No `TextBox` holds more than 64 KB** (`ScriptViewModel.MaxPreviewChars`); full text goes to Copy/Save.
- **UI tests** use `AvaloniaFixture`: never `Avalonia.Headless.XUnit` (it needs xunit.v3 3.x), never dispose
  the session, keep all Avalonia access inside `RunAsync`, and keep `Parallelization(Mode = None)`.
  `StepRenderTests` fail on any binding warning.
- **Avalonia 12 gotchas:**
  - Bind read-only values `Mode=OneWay` where the property is two-way by default: `Run.Text`, DataGrid
    column `Binding`, `ProgressBar.Value`. Otherwise a formatted value is written back (an infinite loop
    with a settable source) or fails to convert.
  - `x:DataType` on every DataGrid column; no generic types in `x:DataType`; show nullable sub-objects
    through a `ContentControl` with a typed `DataTemplate`.
  - Bound text inside a `RadioButton`/`CheckBox` goes in a `TextBlock`, or `_` becomes an access key.
  - CA1822 fires on commands and on properties returning statics.
  - `Bitmap.Save(string, BitmapEncoderOptions)`; the `int?` overload is obsolete.
  - `ShutdownRequestedEventArgs.IsOSShutdown` is internal; use `WindowClosingEventArgs.CloseReason`.
