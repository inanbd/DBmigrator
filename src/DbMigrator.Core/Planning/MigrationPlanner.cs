using System.Globalization;
using DbMigrator.Core.Diagnostics;
using DbMigrator.Core.Providers;
using DbMigrator.Core.Schema;

namespace DbMigrator.Core.Planning;

/// <summary>Decides what every selected source table becomes in the target. Pure: touches no database.</summary>
public static class MigrationPlanner
{
    public static MigrationPlan CreatePlan(SourceSchema source, ISqlDialect dialect, TargetState target, MigrationOptions options)
    {
        var issues = new List<MigrationIssue>(source.Issues);
        var naming = new Naming(source.DefaultSchema, dialect, options.Naming, issues);
        var tables = TableOrdering.ByDependencies(source.Tables.Where(t => options.Tables.Matches(t.Name)).ToList());
        if (tables.Count == 0)
        {
            issues.Add(new(IssueSeverity.Warning, IssueCodes.NoTables, source.DefaultSchema, "No tables matched; there is nothing to migrate."));
        }

        var targetComparer = new QualifiedNameComparer(dialect.IdentifierComparer);
        var existing = new HashSet<QualifiedName>(target.ExistingTables, targetComparer);

        var targetNames = new Dictionary<QualifiedName, QualifiedName>();
        var claimedBy = new Dictionary<QualifiedName, QualifiedName>(targetComparer);
        foreach (var table in tables)
        {
            var name = new QualifiedName(naming.Schema(table.Name.Schema), naming.Identifier(table.Name.Name, table.Name.ToString()));
            if (claimedBy.TryGetValue(name, out var other))
            {
                issues.Add(Error(IssueCodes.NameCollision, table.Name.ToString(),
                    $"{other} and {table.Name} would both become {name}. Exclude one of them or choose another naming option."));
            }
            else
            {
                claimedBy[name] = table.Name;
            }

            targetNames[table.Name] = name;
        }

        // Index and constraint names share a namespace with tables in some engines (PostgreSQL),
        // so each target schema gets one scope seeded with every table name in it.
        var scopes = new Dictionary<string, NameScope>(dialect.IdentifierComparer);
        NameScope ScopeFor(string schema)
        {
            if (!scopes.TryGetValue(schema, out var scope))
            {
                scopes[schema] = scope = new NameScope(dialect.IdentifierLimit, dialect.IdentifierComparer);
            }

            return scope;
        }

        foreach (var name in claimedBy.Keys.Concat(existing))
        {
            ScopeFor(name.Schema).Reserve(name.Name);
        }

        var plans = new List<TablePlan>(tables.Count);
        var planBySource = new Dictionary<QualifiedName, TablePlan>();
        var columnMaps = new Dictionary<QualifiedName, Dictionary<string, string>>();
        foreach (var table in tables)
        {
            var targetName = targetNames[table.Name];
            var exists = existing.Contains(targetName);
            var requested = options.TableExistsOverrides.TryGetValue(table.Name, out var chosen) ? chosen : options.IfTableExists;
            var (action, conflict) = !exists ? (TableAction.Create, false) : requested switch
            {
                TableExistsAction.Fail => (TableAction.Skip, true),
                TableExistsAction.DropAndRecreate => (TableAction.DropAndCreate, false),
                TableExistsAction.Truncate => (TableAction.Truncate, false),
                TableExistsAction.Append => (TableAction.Append, false),
                TableExistsAction.Skip => (TableAction.Skip, false),
                _ => throw new ArgumentOutOfRangeException(nameof(options), requested, "Unknown table-exists action."),
            };

            if (conflict)
            {
                issues.Add(Error(IssueCodes.TableExists, table.Name.ToString(),
                    $"{targetName} already exists in the target. Choose to drop and recreate it, truncate it, append to it, or skip it."));
            }
            else if (action == TableAction.DropAndCreate)
            {
                issues.Add(new(IssueSeverity.Warning, IssueCodes.DropCascade, table.Name.ToString(),
                    $"{targetName} will be dropped and recreated, together with any views and foreign keys in the target that depend on it."));
            }

            var tableIssues = new List<MigrationIssue>(table.Issues);
            var columnMap = new Dictionary<string, string>(StringComparer.Ordinal);
            var columns = PlanColumns(table, dialect, naming, columnMap, tableIssues, issues);
            var plan = new TablePlan
            {
                Source = table,
                Target = targetName,
                Action = action,
                TargetExists = exists,
                IsUnresolvedConflict = conflict,
                Columns = columns,
            };

            if (plan.CreatesTable)
            {
                issues.AddRange(tableIssues);
            }

            plans.Add(plan);
            planBySource[table.Name] = plan;
            columnMaps[table.Name] = columnMap;
        }

        // Constraints come second so foreign keys can resolve the tables they reference.
        for (var i = 0; i < plans.Count; i++)
        {
            var plan = plans[i];
            if (!plan.CreatesTable)
            {
                continue;
            }

            var table = plan.Source;
            var columnMap = columnMaps[table.Name];
            var scope = ScopeFor(plan.Target.Schema);

            PrimaryKeyPlan? primaryKey = null;
            if (table.PrimaryKey is { } pk)
            {
                var preferred = pk.IsNameGenerated ? $"PK_{table.Name.Name}" : pk.Name;
                primaryKey = new PrimaryKeyPlan(
                    Allocate(scope, naming, preferred, $"{table.Name}.{pk.Name}", issues),
                    pk.Columns.Select(c => columnMap[c]).ToList());
            }

            var indexes = new List<IndexPlan>();
            if (options.CreateIndexes)
            {
                foreach (var index in table.Indexes)
                {
                    var subject = $"{table.Name}.{index.Name}";
                    if (index.Filter is not null)
                    {
                        issues.Add(new(IssueSeverity.Warning, IssueCodes.FilteredIndex, subject,
                            $"Filtered index (WHERE {index.Filter}) was not migrated; its predicate is source SQL and needs translating by hand."));
                        continue;
                    }

                    var keyColumnsNullable = index.Columns.Any(c => plan.Columns.First(p => p.Source.Name == c.Name).IsNullable);
                    if (index.IsUnique && !index.NullsAreDistinct && keyColumnsNullable && !dialect.SupportsNullsNotDistinct)
                    {
                        issues.Add(new(IssueSeverity.Info, IssueCodes.NullsDistinct, subject,
                            "The source allows one NULL in this unique index; this target version allows several."));
                    }

                    indexes.Add(new IndexPlan
                    {
                        Name = Allocate(scope, naming,
                            index.IsNameGenerated ? $"UQ_{table.Name.Name}_{string.Join("_", index.Columns.Select(c => c.Name))}" : index.Name,
                            subject, issues),
                        Columns = index.Columns.Select(c => c with { Name = columnMap[c.Name] }).ToList(),
                        IncludedColumns = index.IncludedColumns.Select(c => columnMap[c]).ToList(),
                        IsUnique = index.IsUnique,
                        IsUniqueConstraint = index.IsUniqueConstraint,
                        NullsAreDistinct = index.NullsAreDistinct,
                    });
                }
            }

            var foreignKeys = new List<ForeignKeyPlan>();
            if (options.CreateForeignKeys)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    var subject = $"{table.Name}.{fk.Name}";
                    if (!planBySource.TryGetValue(fk.ReferencedTable, out var referenced))
                    {
                        issues.Add(new(IssueSeverity.Warning, IssueCodes.ForeignKeyTargetMissing, subject,
                            $"References {fk.ReferencedTable}, which is not part of this migration; the foreign key was not created."));
                        continue;
                    }

                    if (!fk.IsTrusted)
                    {
                        issues.Add(new(IssueSeverity.Warning, IssueCodes.ForeignKeyNotValidated, subject,
                            "The source never checked this foreign key against existing rows, so it is created without checking them either. Validate it once orphaned rows are fixed."));
                    }

                    var referencedColumns = columnMaps[fk.ReferencedTable];
                    foreignKeys.Add(new ForeignKeyPlan
                    {
                        Name = Allocate(scope, naming,
                            fk.IsNameGenerated ? $"FK_{table.Name.Name}_{fk.ReferencedTable.Name}" : fk.Name,
                            subject, issues),
                        Columns = fk.Columns.Select(c => columnMap[c]).ToList(),
                        ReferencedTable = referenced.Target,
                        ReferencedColumns = fk.ReferencedColumns.Select(c => referencedColumns[c]).ToList(),
                        OnDelete = fk.OnDelete,
                        OnUpdate = fk.OnUpdate,
                        IsTrusted = fk.IsTrusted,
                    });
                }
            }

            foreach (var check in table.CheckConstraints)
            {
                issues.Add(new(IssueSeverity.Warning, IssueCodes.CheckConstraint, $"{table.Name}.{check.Name}",
                    $"CHECK {check.Expression} was not migrated; its expression is source SQL and needs translating by hand."));
            }

            plans[i] = plan with { PrimaryKey = primaryKey, Indexes = indexes, ForeignKeys = foreignKeys };
        }

        var schemasToCreate = plans
            .Where(p => p.CreatesTable)
            .Select(p => p.Target.Schema)
            .Distinct(dialect.IdentifierComparer)
            .Where(s => target.ExistingSchemas is null
                ? !dialect.IdentifierComparer.Equals(s, dialect.DefaultSchema)
                : !target.ExistingSchemas.Contains(s, dialect.IdentifierComparer))
            .ToList();

        return new MigrationPlan { Tables = plans, SchemasToCreate = schemasToCreate, Issues = issues };
    }

    private static List<ColumnPlan> PlanColumns(
        TableDefinition table,
        ISqlDialect dialect,
        Naming naming,
        Dictionary<string, string> columnMap,
        List<MigrationIssue> tableIssues,
        List<MigrationIssue> errors)
    {
        var names = new HashSet<string>(dialect.IdentifierComparer);
        var columns = new List<ColumnPlan>(table.Columns.Count);
        foreach (var column in table.Columns)
        {
            var subject = $"{table.Name}.{column.Name}";
            var name = naming.Identifier(column.Name, subject);
            if (!names.Add(name))
            {
                errors.Add(Error(IssueCodes.NameCollision, subject,
                    $"Two columns of {table.Name} would both become {name}. Choose another naming option."));
            }

            columnMap[column.Name] = name;

            var mapping = dialect.MapType(column.Type);
            if (mapping.Note is { } typeNote)
            {
                tableIssues.Add(typeNote.For(subject));
            }

            var identity = column.Identity;
            if (identity is not null)
            {
                if (dialect.MapIdentityType(mapping) is { } identityMapping)
                {
                    if (identityMapping.Note is { } identityNote)
                    {
                        tableIssues.Add(identityNote.For(subject));
                    }

                    mapping = identityMapping;
                }
                else
                {
                    tableIssues.Add(new(IssueSeverity.Warning, IssueCodes.IdentityDropped, subject,
                        $"{mapping.SqlType} cannot be an identity column in the target. Values are copied, but new rows get no automatic value."));
                    identity = null;
                }
            }

            string? defaultSql = null;
            if (column.Default is { } defaultValue && identity is null)
            {
                var rendering = dialect.RenderDefault(defaultValue, mapping.Type);
                if (rendering.Problem is not null)
                {
                    tableIssues.Add(new(IssueSeverity.Warning, IssueCodes.DefaultNotTranslated, subject,
                        $"Default {Describe(defaultValue)} was not migrated: {rendering.Problem}"));
                }

                defaultSql = rendering.Sql;
            }

            if (column.ComputedExpression is { } expression)
            {
                tableIssues.Add(new(IssueSeverity.Warning, IssueCodes.ComputedColumn, subject,
                    $"Computed column {expression} becomes a regular, nullable column holding the copied values. Recreate the expression by hand if it must stay computed."));
            }

            columns.Add(new ColumnPlan
            {
                Source = column,
                Name = name,
                Type = mapping.Type,
                SqlType = mapping.SqlType,
                // A computed column turned regular has nothing to fill it on insert.
                IsNullable = column.IsNullable || column.ComputedExpression is not null,
                Identity = identity,
                DefaultSql = defaultSql,
            });
        }

        return columns;
    }

    private static string Allocate(NameScope scope, Naming naming, string name, string subject, List<MigrationIssue> issues)
    {
        var preferred = naming.Cased(name);
        var allocated = scope.Allocate(preferred);
        if (allocated != IdentifierNaming.Shorten(preferred, naming.Limit))
        {
            issues.Add(new(IssueSeverity.Info, IssueCodes.NameDeduplicated, subject,
                $"Renamed to {allocated}: the target needs index and constraint names to be unique per schema."));
        }

        return allocated;
    }

    private static string Describe(DefaultValue value) => value switch
    {
        DefaultValue.Literal { Value: null } => "NULL",
        DefaultValue.Literal { Value: string s } => $"'{s}'",
        DefaultValue.Literal { Value: byte[] b } => "0x" + Convert.ToHexString(b),
        DefaultValue.Literal literal => Convert.ToString(literal.Value, CultureInfo.InvariantCulture) ?? "",
        DefaultValue.Function function => function.Name.ToString(),
        DefaultValue.Expression expression => expression.Sql,
        _ => value.ToString(),
    };

    private static MigrationIssue Error(string code, string subject, string message) =>
        new(IssueSeverity.Error, code, subject, message);

    private sealed class Naming(string sourceDefaultSchema, ISqlDialect dialect, NamingOptions options, List<MigrationIssue> issues)
    {
        private readonly Dictionary<string, string> _schemaMap = new(options.SchemaMap, StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        public IdentifierLimit Limit => dialect.IdentifierLimit;

        public string Schema(string sourceSchema)
        {
            if (_schemaMap.TryGetValue(sourceSchema, out var mapped))
            {
                return mapped;
            }

            return string.Equals(sourceSchema, sourceDefaultSchema, StringComparison.OrdinalIgnoreCase)
                ? dialect.DefaultSchema
                : Identifier(sourceSchema, sourceSchema);
        }

        public string Cased(string name) => IdentifierNaming.ApplyCase(name, options.Case);

        public string Identifier(string name, string subject)
        {
            var cased = Cased(name);
            var fitted = IdentifierNaming.Shorten(cased, Limit);
            if (fitted != cased && _reported.Add(subject))
            {
                var unit = Limit.CountsUtf8Bytes ? "bytes" : "characters";
                issues.Add(new(IssueSeverity.Warning, IssueCodes.NameShortened, subject,
                    $"The name is longer than the target's limit of {Limit.MaxLength} {unit} and becomes {fitted}."));
            }

            return fitted;
        }
    }
}
