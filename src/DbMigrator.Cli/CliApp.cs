using System.CommandLine;
using DbMigrator.Core.Execution;
using DbMigrator.Core.Planning;
using DbMigrator.Core.Providers;
using DbMigrator.Providers;

namespace DbMigrator.Cli;

public static class CliApp
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Cancelled = 130;

    private const string SourceVariable = "DBMIGRATOR_SOURCE";
    private const string TargetVariable = "DBMIGRATOR_TARGET";

    private static ProviderCatalog Providers => BuiltInProviders.Catalog;

    public static Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("Copies schema and data from one database engine to another. Run 'providers' to see which engines are supported.")
        {
            ProvidersCommand(output),
            PlanCommand(output, error),
            MigrateCommand(output, error),
        };

        return root.Parse(args).InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, cancellationToken);
    }

    private static Command ProvidersCommand(TextWriter output)
    {
        var command = new Command("providers", "List the supported database engines.");
        command.SetAction(_ =>
        {
            foreach (var provider in Providers.All)
            {
                var roles = string.Join(" and ", new[] { provider.SupportsSource ? "source" : null, provider.SupportsTarget ? "target" : null }.OfType<string>());
                output.WriteLine($"{provider.Id,-12} {provider.DisplayName} ({roles})");
            }

            return Success;
        });
        return command;
    }

    private static Command PlanCommand(TextWriter output, TextWriter error)
    {
        var options = new CommonOptions();
        var outputFile = new Option<string?>("--output", "-o")
        {
            Description = "Write the SQL script to this file, or '-' for standard output.",
        };
        var command = new Command("plan", "Read the source, show what the migration would do, and optionally write the SQL script. Changes nothing.");
        options.AddTo(command);
        command.Options.Add(outputFile);
        command.SetAction(async (parse, cancellationToken) =>
        {
            if (!options.TryResolve(parse, requireTarget: false, error, out var resolved))
            {
                return Failure;
            }

            try
            {
                await using var source = resolved.SourceProvider.OpenSource(resolved.SourceConnection);
                var schema = await source.ReadSchemaAsync(cancellationToken);
                TargetInfo targetInfo;
                if (resolved.TargetConnection is null)
                {
                    targetInfo = new TargetInfo("not connected; assuming an empty target", resolved.TargetProvider.CreateDialect(), TargetState.Unknown);
                }
                else
                {
                    await using var target = resolved.TargetProvider.OpenTarget(resolved.TargetConnection);
                    targetInfo = await target.InspectAsync(cancellationToken);
                }

                var migration = MigrationEngine.Prepare(schema, targetInfo, resolved.Options);
                var reporter = new ConsoleReporter(output);
                reporter.WritePlan(migration);
                reporter.WriteIssues(migration.Plan.Issues);

                switch (parse.GetValue(outputFile))
                {
                    case "-":
                        output.WriteLine();
                        output.Write(migration.Script.ToText(migration.Plan));
                        break;
                    case { } path:
                        await File.WriteAllTextAsync(path, migration.Script.ToText(migration.Plan), cancellationToken);
                        output.WriteLine($"Script written to {path}.");
                        break;
                }

                return migration.Plan.HasErrors ? Failure : Success;
            }
            catch (OperationCanceledException)
            {
                return Cancelled;
            }
            catch (Exception ex) when (IsDatabaseError(ex))
            {
                error.WriteLine($"error: {ex.Message}");
                return Failure;
            }
        });
        return command;
    }

    private static Command MigrateCommand(TextWriter output, TextWriter error)
    {
        var options = new CommonOptions();
        var schemaOnly = new Option<bool>("--schema-only") { Description = "Create the tables, keys and indexes without copying rows." };
        var noIndexes = new Option<bool>("--no-indexes") { Description = "Skip secondary indexes and unique constraints (primary keys are still created)." };
        var noForeignKeys = new Option<bool>("--no-foreign-keys") { Description = "Skip foreign keys." };
        var noVerify = new Option<bool>("--no-verify") { Description = "Skip counting rows on both sides after the copy." };
        var command = new Command("migrate", "Create the target tables and copy the data.");
        options.AddTo(command);
        command.Options.Add(schemaOnly);
        command.Options.Add(noIndexes);
        command.Options.Add(noForeignKeys);
        command.Options.Add(noVerify);
        command.SetAction(async (parse, cancellationToken) =>
        {
            if (!options.TryResolve(parse, requireTarget: true, error, out var resolved))
            {
                return Failure;
            }

            var migrationOptions = resolved.Options with
            {
                CopyData = !parse.GetValue(schemaOnly),
                CreateIndexes = !parse.GetValue(noIndexes),
                CreateForeignKeys = !parse.GetValue(noForeignKeys),
                VerifyRowCounts = !parse.GetValue(noVerify),
            };

            var reporter = new ConsoleReporter(output);
            try
            {
                await using var source = resolved.SourceProvider.OpenSource(resolved.SourceConnection);
                await using var target = resolved.TargetProvider.OpenTarget(resolved.TargetConnection!);
                var migration = await MigrationEngine.PrepareAsync(source, target, migrationOptions, cancellationToken);
                reporter.WritePlan(migration);
                if (migration.Plan.HasErrors)
                {
                    reporter.WriteIssues(migration.Plan.Issues);
                    error.WriteLine("The plan has errors, so nothing was changed.");
                    return Failure;
                }

                var report = await MigrationEngine.ExecuteAsync(migration, source, target, reporter, cancellationToken);
                reporter.WriteReport(report);
                return report.Succeeded ? Success : Failure;
            }
            catch (MigrationCancelledException ex)
            {
                reporter.WriteReport(ex.PartialReport);
                error.WriteLine("Cancelled. The table being copied was rolled back; tables already copied remain.");
                return Cancelled;
            }
            catch (OperationCanceledException)
            {
                // Only reading the two databases can be cancelled this way; nothing was changed yet.
                error.WriteLine("Cancelled before anything was changed.");
                return Cancelled;
            }
            catch (Exception ex) when (IsDatabaseError(ex))
            {
                error.WriteLine($"error: {ex.Message}");
                return Failure;
            }
        });
        return command;
    }

    private static bool IsDatabaseError(Exception ex) => ex is System.Data.Common.DbException or InvalidOperationException or IOException;

    private sealed record ResolvedOptions(
        IDatabaseProvider SourceProvider,
        string SourceConnection,
        IDatabaseProvider TargetProvider,
        string? TargetConnection,
        MigrationOptions Options);

    /// <summary>Options shared by plan and migrate.</summary>
    private sealed class CommonOptions
    {
        private readonly Option<string> _from = new("--from")
        {
            Description = "Source engine (see 'providers').",
            Required = true,
        };

        private readonly Option<string?> _source = new("--source", "-s")
        {
            Description = $"Source connection string. Defaults to ${SourceVariable}.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable(SourceVariable),
        };

        private readonly Option<string> _to = new("--to")
        {
            Description = "Target engine (see 'providers').",
            Required = true,
        };

        private readonly Option<string?> _target = new("--target", "-t")
        {
            Description = $"Target connection string. Defaults to ${TargetVariable}.",
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable(TargetVariable),
        };

        private readonly Option<string[]> _include = new("--include", "-i")
        {
            Description = "Only tables matching this pattern, e.g. 'sales.*' or 'Order*'. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };

        private readonly Option<string[]> _exclude = new("--exclude", "-x")
        {
            Description = "Leave out tables matching this pattern. Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };

        private readonly Option<string> _naming = new("--naming")
        {
            Description = "How target names are spelled: lower (OrderID -> orderid), snake (OrderID -> order_id) or preserve.",
            DefaultValueFactory = _ => "lower",
        };

        private readonly Option<string[]> _schemaMap = new("--schema-map")
        {
            Description = "Put a source schema's tables in a given target schema, as source=target. Repeatable. The source's default schema goes to the target's by default.",
            AllowMultipleArgumentsPerToken = true,
        };

        private readonly Option<string> _ifExists = new("--if-exists")
        {
            Description = "When a target table already exists: fail, drop (drop and recreate), truncate, append or skip.",
            DefaultValueFactory = _ => "fail",
        };

        public CommonOptions()
        {
            _naming.AcceptOnlyFromAmong("lower", "snake", "preserve");
            _ifExists.AcceptOnlyFromAmong("fail", "drop", "truncate", "append", "skip");
            _from.AcceptOnlyFromAmong(Providers.Sources.Select(p => p.Id).ToArray());
            _to.AcceptOnlyFromAmong(Providers.Targets.Select(p => p.Id).ToArray());
        }

        public void AddTo(Command command)
        {
            foreach (var option in new Option[] { _from, _source, _to, _target, _include, _exclude, _naming, _schemaMap, _ifExists })
            {
                command.Options.Add(option);
            }
        }

        public bool TryResolve(ParseResult parse, bool requireTarget, TextWriter error, out ResolvedOptions resolved)
        {
            resolved = null!;
            var source = parse.GetValue(_source);
            if (string.IsNullOrWhiteSpace(source))
            {
                error.WriteLine($"error: give a source connection string with --source or ${SourceVariable}.");
                return false;
            }

            var target = parse.GetValue(_target);
            if (requireTarget && string.IsNullOrWhiteSpace(target))
            {
                error.WriteLine($"error: give a target connection string with --target or ${TargetVariable}.");
                return false;
            }

            var schemaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in parse.GetValue(_schemaMap) ?? [])
            {
                var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                {
                    error.WriteLine($"error: --schema-map expects source=target, not '{pair}'.");
                    return false;
                }

                schemaMap[parts[0]] = parts[1];
            }

            var options = new MigrationOptions
            {
                Tables = new TableFilter(parse.GetValue(_include) ?? [], parse.GetValue(_exclude) ?? []),
                Naming = new NamingOptions
                {
                    Case = parse.GetValue(_naming) switch
                    {
                        "snake" => IdentifierCase.SnakeCase,
                        "preserve" => IdentifierCase.Preserve,
                        _ => IdentifierCase.Lower,
                    },
                    SchemaMap = schemaMap,
                },
                IfTableExists = parse.GetValue(_ifExists) switch
                {
                    "drop" => TableExistsAction.DropAndRecreate,
                    "truncate" => TableExistsAction.Truncate,
                    "append" => TableExistsAction.Append,
                    "skip" => TableExistsAction.Skip,
                    _ => TableExistsAction.Fail,
                },
            };

            resolved = new ResolvedOptions(
                Providers.Get(parse.GetValue(_from)!),
                source,
                Providers.Get(parse.GetValue(_to)!),
                string.IsNullOrWhiteSpace(target) ? null : target,
                options);
            return true;
        }
    }
}
