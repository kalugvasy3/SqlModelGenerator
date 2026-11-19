using System.Data;

namespace CommonClassLibrary;

// Metadata identities keep SQL Server's exact spelling; FK are never inferred from names.
public enum SqlCatalogObjectKind { Table, View, Column, Function, Procedure, Trigger }
public sealed record SqlCatalogObject(SqlCatalogObjectKind Kind, string Schema, string Name, string ParentName, string DataType);
public static class SqlMetadataNotice
{
    public const string RelationshipVisibility = "Only relationships visible to this database connection are included; hidden or remote relationships cannot be inferred.";
    public static bool IsInformation(string text) => string.Equals(text, RelationshipVisibility, StringComparison.Ordinal);
    public static string[] ReadWarnings(string[]? warnings) => (warnings ?? []).Where(x => !IsInformation(x)).Distinct(StringComparer.Ordinal).ToArray();
    public static string[] Information(string[]? warnings) => (warnings ?? []).Where(IsInformation).Distinct(StringComparer.Ordinal).ToArray();
}
public sealed record SqlCatalogSnapshot(string ProviderId, bool UsesAccessSyntax, string DatabaseName, string ServerVersion,
    SqlCatalogObject[] Objects, string[] Keywords, string[] Warnings, TimeSpan Elapsed)
{
    public string[] ReadWarnings => SqlMetadataNotice.ReadWarnings(Warnings);
    public string[] Information => SqlMetadataNotice.Information(Warnings);
}
public sealed record SqlRelationshipTable(string Catalog, string Schema, string Name);
public sealed record SqlRelationshipColumn(string ChildColumn, string ParentColumn, int Ordinal);
public sealed record SqlForeignKeyRelationship(string Name, SqlRelationshipTable Child, SqlRelationshipTable Parent, SqlRelationshipColumn[] Columns);
public sealed record SqlRelationshipCatalog(SqlForeignKeyRelationship[] Relationships, string[] Warnings, string? DialectId, bool Complete);

public enum PocoTarget { Ef6, EfCore }
public enum PocoNaming { AsInDatabase, Singular, Plural }
public sealed record PocoColumn(string Name, Type ClrType, string StoreType, bool? Nullable,
    int? Size, int? Precision, int? Scale, bool AutoNumber, bool Computed);
public sealed record PocoParameter(PocoColumn Column, ParameterDirection Direction, int Ordinal);
public enum PocoRoutineResultKind { NoRows, Rows, Unknown }
public sealed record PocoRoutineResult(PocoRoutineResultKind Kind, PocoColumn[] Columns, string Message)
{
    public static PocoRoutineResult NoRows { get; } = new(PocoRoutineResultKind.NoRows, [], "");
    public static PocoRoutineResult NewRows(PocoColumn[] columns) => new(PocoRoutineResultKind.Rows, columns, "");
    public static PocoRoutineResult NewUnknown(string message) => new(PocoRoutineResultKind.Unknown, [], message);
    public bool IsNoRows => Kind == PocoRoutineResultKind.NoRows;
    public bool IsRows => Kind == PocoRoutineResultKind.Rows;
    public bool IsUnknown => Kind == PocoRoutineResultKind.Unknown;
}
public sealed record PocoObject(SqlCatalogObject Item, PocoColumn[] Columns, string[] PrimaryKey, string[][] UniqueKeys,
    PocoParameter[] Parameters, bool ParametersKnown, PocoRoutineResult RoutineResult, string[] Warnings);
public sealed record PocoRelationshipDetails(string Name, SqlRelationshipTable Child, string DeleteAction, string UpdateAction, bool Disabled, bool NotTrusted);
public sealed record PocoSchemaSnapshot(string ProviderId, bool UsesAccessSyntax, PocoObject[] Objects,
    SqlForeignKeyRelationship[] Relationships, PocoRelationshipDetails[] RelationshipDetails, string[] Warnings);
public sealed record PocoObjectNames(string ClassName, string SetName, IReadOnlyDictionary<string, string> Properties);
public sealed record PocoOptions(string Namespace, string ContextName, PocoTarget Target, PocoNaming ClassNaming,
    PocoNaming SetNaming, bool NullableReferenceTypes,
    IReadOnlyDictionary<(SqlCatalogObjectKind Kind, string Schema, string Name), PocoObjectNames> Overrides);
public sealed record PocoGeneratedFile(string FileName, string Text);
public sealed record PocoGeneration(PocoGeneratedFile[] Files, string[] Warnings);
