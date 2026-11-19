using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace CommonClassLibrary;

// C# translation of the tested EF6 generation policy. FK metadata is always
// used; column names alone must never invent a relationship.
public static class PocoCodeGeneration
{
    private static bool SameText(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && SyntaxFacts.IsValidIdentifier(name)
        && SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None && !name.StartsWith("@", StringComparison.Ordinal);
    private static string Identifier(string name)
    {
        var safe = Regex.Replace(name, @"[^\p{L}\p{Nd}_]+", "_").Trim('_');
        return safe == "" ? "Item" : ValidName(safe) ? safe : "_" + safe;
    }
    private static readonly (string Singular, string Plural)[] Irregular =
        [("person", "people"), ("child", "children"), ("category", "categories"), ("status", "statuses"), ("address", "addresses")];
    private static string Inflect(PocoNaming naming, string name)
    {
        if (naming == PocoNaming.AsInDatabase) return name;
        var ending = Regex.Match(name, @"[A-Z]+$|[A-Z][a-z]+$|[a-z]+$");
        if (!ending.Success) return name;
        var word = ending.Value;
        var lower = word.ToLowerInvariant();
        bool Ends(string suffix) => lower.EndsWith(suffix, StringComparison.Ordinal);
        var known = Irregular.FirstOrDefault(pair => pair.Singular == lower || pair.Plural == lower);
        string inflected;
        if (known.Singular != null) inflected = naming == PocoNaming.Singular ? known.Singular : known.Plural;
        else if (new[] { "data", "metadata", "news", "series", "species", "equipment" }.Contains(lower)) inflected = lower;
        else if (naming == PocoNaming.Singular && lower.Length > 3 && Ends("ies")) inflected = lower[..^3] + "y";
        else if (naming == PocoNaming.Singular && new[] { "sses", "ches", "shes", "xes", "zzes" }.Any(Ends)) inflected = lower[..^2];
        else if (naming == PocoNaming.Singular && lower.Length > 1 && Ends("s") && !(Ends("ss") || Ends("us") || Ends("is"))) inflected = lower[..^1];
        else if (naming == PocoNaming.Plural && Ends("y") && lower.Length > 1 && !"aeiou".Contains(lower[^2])) inflected = lower[..^1] + "ies";
        else if (naming == PocoNaming.Plural && new[] { "ss", "ch", "sh", "x", "z" }.Any(Ends)) inflected = lower + "es";
        else if (naming == PocoNaming.Plural && Ends("s") && !(Ends("us") || Ends("is"))) inflected = lower;
        else if (naming == PocoNaming.Plural) inflected = lower + "s";
        else inflected = lower;
        var result = word == word.ToUpperInvariant() ? inflected.ToUpperInvariant()
            : char.IsUpper(word[0]) ? char.ToUpperInvariant(inflected[0]) + inflected[1..] : inflected;
        return name[..ending.Index] + result;
    }
    internal static string[] NameVariants(string name) => new[] { name, Inflect(PocoNaming.Singular, name), Inflect(PocoNaming.Plural, name) }.Distinct().ToArray();
    public static PocoObjectNames Names(PocoOptions options, SqlCatalogObject item)
    {
        var name = Identifier(item.Name);
        var className = item.Kind == SqlCatalogObjectKind.Procedure ? name : Identifier(Inflect(options.ClassNaming, name));
        var setName = item.Kind == SqlCatalogObjectKind.Procedure ? name : Identifier(Inflect(options.SetNaming, name));
        return options.Overrides.TryGetValue((item.Kind, item.Schema, item.Name), out var names)
            ? names with { ClassName = string.IsNullOrWhiteSpace(names.ClassName) ? className : names.ClassName,
                SetName = string.IsNullOrWhiteSpace(names.SetName) ? setName : names.SetName }
            : new(className, setName, new Dictionary<string, string>(StringComparer.Ordinal));
    }
    private static string Literal(string value) => SyntaxFactory.Literal(value).Text;
    private static string? BaseType(Type? value)
    {
        if (value == null || value == typeof(object)) return null;
        value = Nullable.GetUnderlyingType(value) ?? value;
        var aliases = new Dictionary<Type, string>
        {
            [typeof(bool)]="bool", [typeof(byte)]="byte", [typeof(sbyte)]="sbyte", [typeof(short)]="short",
            [typeof(ushort)]="ushort", [typeof(int)]="int", [typeof(uint)]="uint", [typeof(long)]="long",
            [typeof(ulong)]="ulong", [typeof(float)]="float", [typeof(double)]="double", [typeof(decimal)]="decimal",
            [typeof(char)]="char", [typeof(string)]="string", [typeof(byte[])]="byte[]"
        };
        if (aliases.TryGetValue(value, out var alias)) return alias;
        return new[] { typeof(Guid), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(DateOnly), typeof(TimeOnly) }.Contains(value)
            ? "global::" + value.FullName : null;
    }
    private static string PropertyType(PocoOptions options, PocoColumn column, string name)
        => name + (column.Nullable != false && (column.ClrType.IsValueType || options.NullableReferenceTypes) ? "?" : "");
    private static string AddName(HashSet<string> used, string name)
    {
        var candidate = name;
        for (var number = 2; !used.Add(candidate); number++) candidate = name + "_" + number;
        return candidate;
    }
    private static string ObjectName(SqlCatalogObject item) => string.IsNullOrWhiteSpace(item.Schema) ? item.Name : item.Schema + "." + item.Name;
    private static bool IsSqlVariant(PocoColumn column) => column.ClrType == typeof(object) && SameText(column.StoreType, "sql_variant");
    private sealed record Property(PocoColumn Column, string Name, string Type, string BaseType);
    private sealed record Entity(PocoObject Source, PocoObjectNames Names, Property[] Properties, Property[] Key, bool Mapped, HashSet<string> Used);
    private sealed record Relationship(string Name, Entity Child, Entity Parent, Property[] ChildKey, Property[] ParentKey,
        string ToParent, string ToChildren, bool OneToOne, bool Required, string DeleteAction);
    private static Property[] Properties(PocoOptions options, bool allowSqlVariant, IReadOnlyDictionary<string, string> names,
        HashSet<string> used, PocoColumn[] columns, Action<string> warn)
    {
        var reserved = new HashSet<string>(used, StringComparer.Ordinal);
        foreach (var (original, replacement) in names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (columns.Any(column => column.Name == original) && (!ValidName(replacement) || !reserved.Add(replacement)))
                throw new ArgumentException("Invalid or duplicate C# member name " + replacement + ".", "options");
        foreach (var name in reserved) used.Add(name);
        var result = new List<Property>();
        foreach (var column in columns)
        {
            var baseName = allowSqlVariant && IsSqlVariant(column) ? "object" : BaseType(column.ClrType);
            if (baseName != null)
            {
                var name = names.TryGetValue(column.Name, out var replacement) ? replacement : AddName(used, Identifier(column.Name));
                result.Add(new(column, name, PropertyType(options, column, baseName), baseName));
            }
            else warn("Unsupported CLR type for " + column.Name + " (" + column.StoreType + "). The member was omitted.");
        }
        return result.ToArray();
    }
    private static string SchemaName(PocoSchemaSnapshot snapshot, SqlCatalogObject item)
        => snapshot.UsesAccessSyntax || SameText(snapshot.ProviderId, "sqlite") || SameText(snapshot.ProviderId, "sqlcipher") ? "" : item.Schema;
    private static PocoGeneratedFile FormatFile(PocoOptions options, string name, IEnumerable<string> imports, Action<StringBuilder> body)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated by AnotherEdit>");
        if (options.NullableReferenceTypes) builder.AppendLine("#nullable enable");
        foreach (var import in imports) builder.AppendLine("using " + import + ";");
        builder.AppendLine("namespace " + options.Namespace);
        builder.AppendLine("{");
        body(builder);
        builder.AppendLine("}");
        return new(name + ".g.cs", builder.ToString());
    }
    private static void Scalar(Action<string> line, bool annotation, PocoOptions options, Property property)
    {
        if (annotation)
        {
            line("        [global::System.ComponentModel.DataAnnotations.Schema.ColumnAttribute(" + Literal(property.Column.Name) + ")]");
            if (property.Column.Size is > 0 && (property.BaseType == "string" || property.BaseType == "byte[]"))
                line("        [global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(" + property.Column.Size + ")]");
        }
        var init = options.NullableReferenceTypes && property.Column.Nullable == false && !property.Column.ClrType.IsValueType ? " = null!;" : "";
        line("        public " + property.Type + " " + property.Name + " { get; set; }" + init);
    }
    private static PocoGeneratedFile EntityFile(PocoSchemaSnapshot snapshot, PocoOptions options, Relationship[] relationships, Entity entity)
        => FormatFile(options, entity.Names.ClassName, [], builder =>
        {
            void Line(string text) => builder.AppendLine(text);
            var item = entity.Source.Item;
            var name = entity.Names.ClassName;
            var schema = SchemaName(snapshot, item);
            schema = string.IsNullOrWhiteSpace(schema) ? "" : ", Schema = " + Literal(schema);
            if (item.Kind == SqlCatalogObjectKind.Table)
                Line("    [global::System.ComponentModel.DataAnnotations.Schema.TableAttribute(" + Literal(item.Name) + schema + ")]");
            Line("    public partial class " + name);
            Line("    {");
            foreach (var property in entity.Properties) Scalar(Line, true, options, property);
            foreach (var relationship in relationships)
            {
                if (name == relationship.Child.Names.ClassName)
                {
                    var nullable = options.NullableReferenceTypes && !relationship.Required ? "?" : "";
                    var init = options.NullableReferenceTypes && relationship.Required ? " = null!;" : "";
                    Line("        public " + relationship.Parent.Names.ClassName + nullable + " " + relationship.ToParent + " { get; set; }" + init);
                }
                if (name == relationship.Parent.Names.ClassName)
                {
                    var child = relationship.Child.Names.ClassName;
                    if (relationship.OneToOne)
                        Line("        public " + child + (options.NullableReferenceTypes ? "?" : "") + " " + relationship.ToChildren + " { get; set; }");
                    else Line("        public global::System.Collections.Generic.ICollection<" + child + "> " + relationship.ToChildren
                        + " { get; set; } = new global::System.Collections.Generic.List<" + child + ">();");
                }
            }
            Line("    }");
        });
    private static string KeySelector(Property[] columns) => columns.Length == 1 ? "e => e." + columns[0].Name
        : "e => new { " + string.Join(", ", columns.Select(property => "e." + property.Name)) + " }";
    private static string? ParameterDbType(PocoColumn column) => column.StoreType.ToLowerInvariant() switch
    {
        "bit" => "Boolean", "tinyint" => "Byte", "smallint" => "Int16", "int" => "Int32", "bigint" => "Int64",
        "real" => "Single", "float" => "Double", "decimal" or "numeric" => "Decimal", "money" or "smallmoney" => "Currency",
        "char" => "AnsiStringFixedLength", "nchar" => "StringFixedLength", "varchar" or "text" => "AnsiString",
        "nvarchar" or "ntext" => "String", "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => "Binary",
        "uniqueidentifier" => "Guid", "datetime" or "smalldatetime" => "DateTime", "datetime2" => "DateTime2",
        "datetimeoffset" => "DateTimeOffset", "date" => "Date", "time" => "Time", "xml" => "Xml",
        "sql_variant" when IsSqlVariant(column) => "Object", _ => null
    };
    private static string QuoteIdentifier(PocoSchemaSnapshot snapshot, string name)
        => snapshot.UsesAccessSyntax ? "[" + name.Replace("]", "]]") + "]" : SqlProviders.QuoteIdentifier(snapshot.ProviderId, name);
    private static string QuotedName(PocoSchemaSnapshot snapshot, SqlCatalogObject item)
    {
        var schema = SchemaName(snapshot, item);
        return string.IsNullOrWhiteSpace(schema) ? QuoteIdentifier(snapshot, item.Name) : QuoteIdentifier(snapshot, schema) + "." + QuoteIdentifier(snapshot, item.Name);
    }
    private static void PropertyMapping(PocoSchemaSnapshot snapshot, PocoOptions options, bool isKey, Action<string> line, Property property)
    {
        var column = property.Column;
        var sqlServer = SameText(snapshot.ProviderId, "sqlserver");
        var rowVersion = sqlServer && (SameText(column.StoreType, "rowversion") || SameText(column.StoreType, "timestamp"));
        var mapping = "            entity.Property(e => e." + property.Name + ").HasColumnName(" + Literal(column.Name) + ")";
        if (rowVersion) mapping += ".IsRowVersion()";
        else if (column.AutoNumber || column.Computed || isKey)
            mapping += ".HasDatabaseGeneratedOption(global::System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption."
                + (column.AutoNumber ? "Identity" : column.Computed ? "Computed" : "None") + ")";
        var store = column.StoreType.ToLowerInvariant();
        if (sqlServer && !rowVersion && ParameterDbType(column) != null) mapping += ".HasColumnType(" + Literal(store) + ")";
        if (property.BaseType == "decimal" && column.Precision.HasValue && column.Scale.HasValue)
            mapping += $".HasPrecision({column.Precision.Value}, {column.Scale.Value})";
        if (column.ClrType == typeof(string) || column.ClrType == typeof(byte[]))
        {
            if (column.Size is > 0) mapping += $".HasMaxLength({column.Size.Value})";
            else if (column.Size == -1) mapping += ".IsMaxLength()";
            if (sqlServer && column.ClrType == typeof(string))
            {
                if (store is "char" or "varchar" or "text") mapping += ".IsUnicode(false)";
                else if (store is "nchar" or "nvarchar" or "ntext") mapping += ".IsUnicode(true)";
            }
            if (sqlServer && !rowVersion) mapping += store is "char" or "nchar" or "binary" ? ".IsFixedLength()" : ".IsVariableLength()";
        }
        // Unknown metadata preserves configuration; explicit nullability replaces older facets.
        if (column.Nullable == false) mapping += ".IsRequired()";
        else if (column.Nullable == true) mapping += ".IsOptional()";
        line(mapping + ";");
    }
    private static string ConfiguredConnectionName(string context) => context == "CreateConfiguredConnection" ? "CreateConfiguredConnectionCore" : "CreateConfiguredConnection";
    private static PocoGeneratedFile ContextFile(PocoSchemaSnapshot snapshot, PocoOptions options, Entity[] entities, Relationship[] relationships)
        => FormatFile(options, options.ContextName, ["System.Data.Entity"], builder =>
        {
            void Line(string text) => builder.AppendLine(text);
            var context = options.ContextName;
            var configuredConnection = ConfiguredConnectionName(context);
            Line("    public partial class " + context + " : global::System.Data.Entity.DbContext");
            Line("    {");
            Line("        static " + context + "() => global::System.Data.Entity.Database.SetInitializer<" + context + ">(null);");
            Line("        public " + context + "() : base(" + configuredConnection + "(), true) { }");
            Line("        public " + context + "(global::System.Data.Common.DbConnection connection) : base(connection, false) { }");
            Line("");
            Line("        private static global::System.Data.Common.DbConnection " + configuredConnection + "()");
            Line("        {");
            Line("            var connections = global::System.Configuration.ConfigurationManager.ConnectionStrings;");
            Line("            var settings = connections[nameof(" + context + ")] ?? connections[typeof(" + context + ").FullName];");
            Line("            if (settings == null) throw new global::System.InvalidOperationException(" + Literal("Add a connection string named " + context + " to the application's App.config or Web.config.") + ");");
            Line("            var provider = settings.ProviderName;");
            Line("            var connectionString = settings.ConnectionString;");
            Line("            if (global::System.String.Equals(provider, \"System.Data.EntityClient\", global::System.StringComparison.OrdinalIgnoreCase))");
            Line("            {");
            Line("                var legacy = new global::System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };");
            Line("                provider = legacy.TryGetValue(\"provider\", out var configuredProvider) ? configuredProvider as string : null;");
            Line("                connectionString = legacy.TryGetValue(\"provider connection string\", out var providerString) ? providerString as string : null;");
            Line("            }");
            Line("            if (global::System.String.IsNullOrWhiteSpace(provider) || global::System.String.IsNullOrWhiteSpace(connectionString))");
            Line("                throw new global::System.InvalidOperationException(\"The configured EF6 connection must specify its ADO.NET provider and connection string.\");");
            Line("            global::System.Data.Common.DbProviderFactory factory =");
            Line("                global::System.String.Equals(provider, \"System.Data.SqlClient\", global::System.StringComparison.OrdinalIgnoreCase)");
            Line("                    ? global::System.Data.SqlClient.SqlClientFactory.Instance");
            Line("                    : global::System.Data.Common.DbProviderFactories.GetFactory(provider);");
            Line("            var connection = factory.CreateConnection()");
            Line("                ?? throw new global::System.InvalidOperationException(\"The configured ADO.NET provider is unavailable: \" + provider);");
            Line("            try"); Line("            {"); Line("                connection.ConnectionString = connectionString;");
            Line("                return connection;"); Line("            }"); Line("            catch"); Line("            {");
            Line("                connection.Dispose();"); Line("                throw;"); Line("            }"); Line("        }"); Line("");
            foreach (var entity in entities)
            {
                if (entity.Mapped)
                    Line("        public global::System.Data.Entity.DbSet<" + entity.Names.ClassName + "> " + entity.Names.SetName + " => base.Set<" + entity.Names.ClassName + ">();");
                else if (entity.Properties.Length > 0)
                {
                    var projection = string.Join(", ", entity.Properties.Select(property => QuoteIdentifier(snapshot, property.Column.Name) + " AS " + QuoteIdentifier(snapshot, property.Name)));
                    var sql = "SELECT " + projection + " FROM " + QuotedName(snapshot, entity.Source.Item);
                    Line("        public global::System.Data.Entity.Infrastructure.DbRawSqlQuery<" + entity.Names.ClassName + "> Query"
                        + entity.Names.SetName + "() => base.Database.SqlQuery<" + entity.Names.ClassName + ">(" + Literal(sql) + ");");
                }
            }
            Line(""); Line("        protected override void OnModelCreating(global::System.Data.Entity.DbModelBuilder modelBuilder)");
            Line("        {"); Line("            base.OnModelCreating(modelBuilder);");
            foreach (var entity in entities.Where(entity => entity.Mapped))
            {
                var item = entity.Source.Item;
                var schema = SchemaName(snapshot, item);
                var store = Literal(item.Name) + (string.IsNullOrWhiteSpace(schema) ? "" : ", " + Literal(schema));
                Line("            {"); Line("            var entity = modelBuilder.Entity<" + entity.Names.ClassName + ">();");
                Line("            entity.ToTable(" + store + ");");
                if (entity.Key.Length > 0) Line("            entity.HasKey(" + KeySelector(entity.Key) + ");");
                foreach (var property in entity.Properties)
                    PropertyMapping(snapshot, options, entity.Key.Any(key => key.Name == property.Name), Line, property);
                Line("            }");
            }
            foreach (var relationship in relationships)
            {
                var relationshipSource = "            modelBuilder.Entity<" + relationship.Child.Names.ClassName + ">()";
                var withMany = ".WithMany(e => e." + relationship.ToChildren + ").HasForeignKey(" + KeySelector(relationship.ChildKey) + ")";
                var cascade = SameText(relationship.DeleteAction, "CASCADE") ? "true" : "false";
                if (relationship.OneToOne)
                    Line(relationshipSource + ".HasRequired(e => e." + relationship.ToParent + ").WithOptional(e => e." + relationship.ToChildren + ").WillCascadeOnDelete(" + cascade + ");");
                else Line(relationshipSource + (relationship.Required ? ".HasRequired" : ".HasOptional") + "(e => e." + relationship.ToParent + ")" + withMany + ".WillCascadeOnDelete(" + cascade + ");");
            }
            Line("            OnModelCreatingPartial(modelBuilder);"); Line("        }"); Line("");
            Line("        partial void OnModelCreatingPartial(global::System.Data.Entity.DbModelBuilder modelBuilder);"); Line("    }");
        });
    private const string ProcedureRuntimeName = "AnotherEditProcedureRuntime";
    private static PocoGeneratedFile ProcedureRuntimeFile(PocoOptions options)
        => FormatFile(options, ProcedureRuntimeName, [], builder => builder.Append(ProcedureRuntimeTemplate.Replace("__RUNTIME__", ProcedureRuntimeName)
            .Replace("__EF6_TRANSACTION__", Ef6TransactionTemplate).Replace("__N__", options.NullableReferenceTypes ? "?" : "")));

    private static bool SqlServerRoutines(PocoSchemaSnapshot snapshot) => SameText(snapshot.ProviderId, "sqlserver") && !snapshot.UsesAccessSyntax;
    private static bool NeedsExplicitRows(PocoSchemaSnapshot snapshot, PocoRoutineResult result)
    {
        if (!result.IsRows || !SqlServerRoutines(snapshot)) return false;
        var names = result.Columns.Select(column => column.Name).ToArray();
        return names.Any(string.IsNullOrWhiteSpace) || names.Distinct().Count() != names.Length || result.Columns.Any(IsSqlVariant);
    }
    private static PocoGeneratedFile RoutineFile(PocoSchemaSnapshot snapshot, PocoOptions options, string methodName,
        string? explicitRowsMethod, PocoObjectNames names, PocoObject source, Action<string> warn)
    {
        var name = names.ClassName;
        var sqlServer = SqlServerRoutines(snapshot);
        var parameters = source.Parameters.OrderBy(parameter => parameter.Ordinal).ToArray();
        var arguments = new HashSet<string>([name + "Arguments"], StringComparer.Ordinal);
        var outputs = new HashSet<string>([name + "Outputs", "ReturnValue"], StringComparer.Ordinal);
        var rowNames = new HashSet<string>([name + "Row"], StringComparer.Ordinal);
        static bool Input(PocoParameter parameter) => parameter.Direction is ParameterDirection.Input or ParameterDirection.InputOutput;
        static bool Output(PocoParameter parameter) => parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput;
        SortedDictionary<int, Property> ParameterProperties(HashSet<string> used, Func<PocoParameter, bool> filter)
        {
            var result = new SortedDictionary<int, Property>();
            foreach (var parameter in parameters.Where(filter))
            {
                var property = Properties(options, sqlServer, new Dictionary<string, string>(), used, [parameter.Column], warn).FirstOrDefault();
                if (property != null) result[parameter.Ordinal] = property;
            }
            return result;
        }
        var inputProperties = ParameterProperties(arguments, Input);
        var outputProperties = ParameterProperties(outputs, Output);
        Property[] row = [];
        if (source.RoutineResult.IsRows)
        {
            var columns = source.RoutineResult.Columns;
            row = Properties(options, sqlServer, new Dictionary<string, string>(), rowNames, columns, warn);
            if (row.Length != columns.Length)
            {
                warn("The first result cannot be read as a complete typed row.");
                row = [];
            }
        }
        else if (source.RoutineResult.IsUnknown)
            warn("Result shape unavailable. " + source.RoutineResult.Message + " ExecuteDataSet/ExecuteDataSetAsync still read every result set at runtime.");
        var binding = parameters.Where(parameter => parameter.Direction != ParameterDirection.ReturnValue)
            .Select(parameter => (Parameter: parameter, DbType: ParameterDbType(parameter.Column)))
            .Where(pair => pair.DbType != null && (!Input(pair.Parameter) || inputProperties.ContainsKey(pair.Parameter.Ordinal))
                && (!Output(pair.Parameter) || outputProperties.ContainsKey(pair.Parameter.Ordinal))).ToArray();
        var canBind = sqlServer && source.ParametersKnown
            && binding.Length == parameters.Count(parameter => parameter.Direction != ParameterDirection.ReturnValue)
            && binding.Select(pair => pair.Parameter.Ordinal).Distinct().Count() == binding.Length
            && binding.All(pair => !SameText(pair.Parameter.Column.Name, "@RETURN_VALUE"));
        if (!canBind) warn("No executable command factory was generated for this provider or parameter signature.");
        var rowsType = "global::System.Collections.Generic.List<" + name + "Row>";
        var readRows = ProcedureRuntimeName + ".ReadRows(command, token, ReadRow)";
        var readRowsAsync = ProcedureRuntimeName + ".ReadRowsAsync(command, token, ReadRow)";
        const string dataSetType = "global::System.Data.DataSet";
        var readDataSet = ProcedureRuntimeName + ".ReadDataSet(command, token)";
        var readDataSetAsync = ProcedureRuntimeName + ".ReadDataSetAsync(command, token)";
        var typedDefault = row.Length > 0 && explicitRowsMethod == null;
        var (resultType, read, readAsync) = typedDefault ? (rowsType, readRows, readRowsAsync)
            : source.RoutineResult.IsNoRows ? ("int", "command.ExecuteNonQuery()", "command.ExecuteNonQueryAsync(token)")
            : (dataSetType, readDataSet, readDataSetAsync);
        var nullable = options.NullableReferenceTypes ? "?" : "";
        return FormatFile(options, name + "Procedure", [], builder =>
        {
            void Line(string text) => builder.AppendLine(text);
            void Dto(string suffix, IEnumerable<Property> members, bool returnValue)
            {
                Line("    public partial class " + name + suffix); Line("    {");
                foreach (var property in members) Scalar(Line, false, options, property);
                if (returnValue) Line("        public int ReturnValue { get; set; }");
                Line("    }"); Line("");
            }
            Dto("Arguments", inputProperties.Values, false);
            if (canBind || outputProperties.Count > 0) Dto("Outputs", outputProperties.Values, canBind);
            if (row.Length > 0) Dto("Row", row, false);
            Line("    public static partial class " + name + "Procedure"); Line("    {");
            if (canBind)
            {
                Line("        public static global::System.Data.Common.DbCommand CreateCommand(global::System.Data.Common.DbConnection connection, " + name + "Arguments arguments)");
                Line("        {");
                Line("            if (connection == null) throw new global::System.ArgumentNullException(nameof(connection));");
                Line("            if (arguments == null) throw new global::System.ArgumentNullException(nameof(arguments));");
                Line("            var command = connection.CreateCommand();"); Line("            try"); Line("            {");
                Line("                command.CommandType = global::System.Data.CommandType.StoredProcedure;");
                Line("                command.CommandText = " + Literal(QuotedName(snapshot, source.Item)) + ";");
                foreach (var (parameter, dbType) in binding)
                {
                    var variable = "p" + parameter.Ordinal;
                    void Assignment(string memberName, string value) => Line("                " + variable + "." + memberName + " = " + value + ";");
                    Line("                var " + variable + " = command.CreateParameter();");
                    Assignment("ParameterName", Literal(parameter.Column.Name));
                    Assignment("DbType", "global::System.Data.DbType." + dbType);
                    Assignment("Direction", "global::System.Data.ParameterDirection." + parameter.Direction);
                    if (parameter.Column.Size.HasValue) Assignment("Size", parameter.Column.Size.Value.ToString());
                    if (parameter.Column.Precision.HasValue) Assignment("Precision", "(byte)" + parameter.Column.Precision.Value);
                    if (parameter.Column.Scale.HasValue) Assignment("Scale", "(byte)" + parameter.Column.Scale.Value);
                    if (inputProperties.TryGetValue(parameter.Ordinal, out var property))
                        Assignment("Value", "(object" + (options.NullableReferenceTypes ? "?" : "") + ")arguments." + property.Name + " ?? global::System.DBNull.Value");
                    Line("                command.Parameters.Add(" + variable + ");");
                }
                Line("                var returnValue = command.CreateParameter();");
                Line("                returnValue.ParameterName = \"@RETURN_VALUE\";");
                Line("                returnValue.DbType = global::System.Data.DbType.Int32;");
                Line("                returnValue.Direction = global::System.Data.ParameterDirection.ReturnValue;");
                Line("                command.Parameters.Add(returnValue);"); Line("                return command;");
                Line("            }"); Line("            catch"); Line("            {"); Line("                command.Dispose();");
                Line("                throw;"); Line("            }"); Line("        }"); Line("");
                Line("        // Read outputs after execution and after closing the reader.");
                Line("        public static " + name + "Outputs ReadOutputs(global::System.Data.Common.DbCommand command)");
                Line("            => new " + name + "Outputs"); Line("            {");
                for (var index = 0; index < binding.Length; index++)
                {
                    var parameter = binding[index].Parameter;
                    if (!outputProperties.TryGetValue(parameter.Ordinal, out var property)) continue;
                    var value = $"command.Parameters[{index}].Value";
                    string converted;
                    if (property.Column.Nullable != false)
                        converted = value + " is null || " + value + " is global::System.DBNull ? default(" + property.Type + ") : (" + property.Type + ")" + value;
                    else
                    {
                        var typed = "typed" + index;
                        var present = property.BaseType == "object" ? " && !(" + typed + " is global::System.DBNull)" : "";
                        converted = value + " is " + property.BaseType + " " + typed + present + " ? " + typed
                            + " : throw new global::System.InvalidOperationException(\"Missing or incompatible output value.\")";
                    }
                    Line("                " + property.Name + " = " + converted + ",");
                }
                Line($"                ReturnValue = command.Parameters[{binding.Length}].Value is int status ? status : throw new global::System.InvalidOperationException(\"Missing return value.\")");
                Line("            };");
            }
            if (row.Length > 0)
            {
                Line(""); Line("        public static " + name + "Row ReadRow(global::System.Data.Common.DbDataReader reader)");
                Line("            => new " + name + "Row"); Line("            {");
                for (var index = 0; index < row.Length; index++)
                {
                    var property = row[index];
                    var isNullable = property.Column.Nullable != false;
                    string value;
                    if (property.BaseType == "object")
                    {
                        var missing = isNullable ? "null" : "throw new global::System.InvalidOperationException(\"Unexpected NULL result value.\")";
                        value = $"reader.IsDBNull({index}) ? {missing} : reader.GetValue({index})";
                    }
                    else
                    {
                        var getValue = $"reader.GetFieldValue<{property.BaseType}>({index})";
                        value = isNullable ? $"reader.IsDBNull({index}) ? default({property.Type}) : ({property.Type}){getValue}" : getValue;
                    }
                    Line("                " + property.Name + " = " + value + ",");
                }
                Line("            };");
            }
            if (canBind)
            {
                Line(""); Line("        private static void CopyOutputs(" + name + "Outputs source, " + name + "Outputs" + nullable + " target)");
                Line("        {"); Line("            if (target == null) return;");
                foreach (var property in outputProperties.Values) Line("            target." + property.Name + " = source." + property.Name + ";");
                Line("            target.ReturnValue = source.ReturnValue;"); Line("        }");
                void ExecuteMethods(string suffix, string returnType, string readResult, string readResultAsync)
                {
                    var signature = "global::System.Data.Common.DbConnection connection, " + name + "Arguments arguments";
                    var optional = "global::System.Data.Common.DbTransaction" + nullable + " transaction = null, int? commandTimeout = null, global::System.Threading.CancellationToken cancellationToken = default";
                    Line("");
                    if (suffix == "Rows" || suffix == "" && typedDefault)
                        Line("        // Returns every row in the described first result set. Use ExecuteDataSet for all result sets.");
                    else if (suffix == "" && source.RoutineResult.IsNoRows)
                        Line("        // Returns rows affected. SQL RETURN and output parameters are available through Outputs.");
                    else Line("        // Reads every row of every result set. Empty result tables retain their column definitions.");
                    Line("        public static " + returnType + " Execute" + suffix + "(" + signature + ", " + optional + ")");
                    Line("            => Execute" + suffix + "Core(connection, arguments, null, transaction, commandTimeout, cancellationToken);"); Line("");
                    Line("        public static " + returnType + " Execute" + suffix + "(" + signature + ", out " + name + "Outputs outputs, " + optional + ")");
                    Line("        {"); Line("            var collected = new " + name + "Outputs();");
                    Line("            var result = Execute" + suffix + "Core(connection, arguments, collected, transaction, commandTimeout, cancellationToken);");
                    Line("            outputs = collected;"); Line("            return result;"); Line("        }"); Line("");
                    Line("        private static " + returnType + " Execute" + suffix + "Core(" + signature + ", " + name + "Outputs" + nullable + " outputs, global::System.Data.Common.DbTransaction" + nullable + " transaction, int? commandTimeout, global::System.Threading.CancellationToken cancellationToken)");
                    Line("            => " + ProcedureRuntimeName + ".Execute(CreateCommand(connection, arguments), transaction, commandTimeout, cancellationToken,");
                    Line("                (command, token) => " + readResult + ", command => CopyOutputs(ReadOutputs(command), outputs));"); Line("");
                    Line("        public static global::System.Threading.Tasks.Task<" + returnType + "> Execute" + suffix + "Async(" + signature + ", " + name + "Outputs" + nullable + " outputs = null, " + optional + ")");
                    Line("            => " + ProcedureRuntimeName + ".ExecuteAsync(CreateCommand(connection, arguments), transaction, commandTimeout, cancellationToken,");
                    Line("                (command, token) => " + readResultAsync + ", command => CopyOutputs(ReadOutputs(command), outputs));");
                }
                ExecuteMethods("", resultType, read, readAsync);
                ExecuteMethods("DataSet", dataSetType, readDataSet, readDataSetAsync);
                if (explicitRowsMethod != null && row.Length > 0) ExecuteMethods("Rows", rowsType, readRows, readRowsAsync);
            }
            Line("    }");
            if (canBind)
            {
                var usedParameters = new HashSet<string>(["outputs", "cancellationToken", "arguments"], StringComparer.Ordinal);
                var inputs = parameters.Where(parameter => inputProperties.ContainsKey(parameter.Ordinal))
                    .Select(parameter => (Property: inputProperties[parameter.Ordinal], Parameter: AddName(usedParameters, Identifier(parameter.Column.Name.TrimStart('@'))))).ToArray();
                var typedParameters = string.Join(", ", inputs.Select(pair => pair.Property.Type + " " + pair.Parameter));
                static string Comma(string text) => string.IsNullOrEmpty(text) ? "" : text + ", ";
                var typedArguments = "new " + name + "Arguments { " + string.Join(", ", inputs.Select(pair => pair.Property.Name + " = " + pair.Parameter)) + " }";
                const string connection = "base.Database.Connection";
                var transaction = ProcedureRuntimeName + ".GetEf6Transaction(base.Database)";
                var timeout = ProcedureRuntimeName + ".GetEf6CommandTimeout(base.Database)";
                Line(""); Line("    public partial class " + options.ContextName); Line("    {");
                void ContextMethods(string memberBase, string suffix, string returnType)
                {
                    foreach (var withArguments in new[] { false, true })
                    {
                        var memberName = memberBase + (withArguments ? "WithArguments" : "");
                        var signature = withArguments ? name + "Arguments arguments" : typedParameters;
                        var args = withArguments ? "arguments" : typedArguments;
                        var invocation = name + "Procedure.Execute" + suffix + "(" + connection + ", " + args;
                        var syncOptions = ", transaction: " + transaction + ", commandTimeout: " + timeout + ", cancellationToken: cancellationToken)";
                        Line("        public " + returnType + " " + memberName + "(" + Comma(signature) + "global::System.Threading.CancellationToken cancellationToken = default)");
                        Line("            => " + invocation + syncOptions + ";"); Line("");
                        Line("        public " + returnType + " " + memberName + "(" + Comma(signature) + "out " + name + "Outputs outputs, global::System.Threading.CancellationToken cancellationToken = default)");
                        Line("            => " + invocation + ", out outputs" + syncOptions + ";"); Line("");
                        Line("        public global::System.Threading.Tasks.Task<" + returnType + "> " + memberName + "Async(" + Comma(signature) + "global::System.Threading.CancellationToken cancellationToken = default, " + name + "Outputs" + nullable + " outputs = null)");
                        Line("            => " + name + "Procedure.Execute" + suffix + "Async(" + connection + ", " + args + ", outputs, transaction: " + transaction + ", commandTimeout: " + timeout + ", cancellationToken: cancellationToken);"); Line("");
                    }
                }
                ContextMethods(methodName, "", resultType);
                ContextMethods(methodName + "DataSet", "DataSet", dataSetType);
                if (row.Length > 0 && explicitRowsMethod != null) ContextMethods(explicitRowsMethod, "Rows", rowsType);
                Line("    }");
            }
        });
    }

    private static bool IsReservedFileName(string name) => Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static PocoGeneration Generate(PocoSchemaSnapshot snapshot, PocoOptions options)
    {
        if (options.Target != PocoTarget.Ef6) throw new ArgumentException("This standalone generator supports EF6 only.", "options");
        if (snapshot.UsesAccessSyntax || !SameText(snapshot.ProviderId, "sqlserver"))
            throw new ArgumentException("This standalone generator supports SQL Server only.", "snapshot");
        var warnings = new List<string>(snapshot.Warnings);
        var named = snapshot.Objects.Where(source => source.Item.Kind is SqlCatalogObjectKind.Table or SqlCatalogObjectKind.View or SqlCatalogObjectKind.Procedure)
            .Select(source => (Source: source, Names: Names(options, source.Item))).ToArray();
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddType(string name)
        {
            if (!ValidName(name) || IsReservedFileName(name) || !types.Add(name))
                throw new ArgumentException("Invalid or duplicate generated type/file name " + name + ". Set a distinct object name in the preview.", "options");
        }
        if (string.IsNullOrWhiteSpace(options.Namespace) || !options.Namespace.Split('.').All(ValidName))
            throw new ArgumentException("Provide a valid C# namespace.", "options");
        AddType(options.ContextName);
        if (named.Any(pair => pair.Source.Item.Kind == SqlCatalogObjectKind.Procedure)) AddType(ProcedureRuntimeName);
        var entityList = new List<Entity>();
        var routineSources = new List<(PocoObject Source, PocoObjectNames Names)>();
        foreach (var (source, names) in named)
        {
            void Warn(string message) => warnings.Add(ObjectName(source.Item) + ": " + message);
            foreach (var warning in source.Warnings) Warn(warning);
            if (source.Item.Kind == SqlCatalogObjectKind.Procedure)
            {
                foreach (var suffix in new[] { "Arguments", "Outputs", "Row", "Procedure" }) AddType(names.ClassName + suffix);
                routineSources.Add((source, names));
            }
            else if (source.Columns.Length > 0)
            {
                AddType(names.ClassName);
                if (!ValidName(names.SetName)) throw new ArgumentException("Invalid C# set name " + names.SetName + ".", "options");
                var used = new HashSet<string>([names.ClassName], StringComparer.Ordinal);
                var properties = Properties(options, false, names.Properties, used, source.Columns, Warn);
                var key = source.PrimaryKey.Select(name => properties.FirstOrDefault(property => property.Column.Name == name)).OfType<Property>().ToArray();
                var keyKnown = key.Length == source.PrimaryKey.Length && key.Length > 0;
                if (!keyKnown) Warn("A complete primary key was not available.");
                var mapped = keyKnown && source.Item.Kind == SqlCatalogObjectKind.Table;
                if (!mapped && properties.Length > 0) Warn("EF6 emits a read query for this object.");
                if (properties.Length > 0) entityList.Add(new(source, names, properties, keyKnown ? key : [], mapped, used));
            }
            else Warn("No readable column metadata was available. No class was generated.");
        }
        var entities = entityList.ToArray();
        var contextMembers = new HashSet<string>([options.ContextName, "OnModelCreating", "OnModelCreatingPartial"], StringComparer.Ordinal);
        contextMembers.Add(ConfiguredConnectionName(options.ContextName));
        foreach (var entity in entities)
        {
            var memberName = entity.Mapped ? entity.Names.SetName : "Query" + entity.Names.SetName;
            if (!contextMembers.Add(memberName)) throw new ArgumentException("Duplicate context member " + memberName + ". Set a distinct name in the preview.", "options");
        }
        foreach (var memberName in new[] { "Database", "SaveChanges", "SaveChangesAsync", "Set", "Entry", "Dispose", "DisposeAsync" }) contextMembers.Add(memberName);
        string ReserveMethodFamily(string name, string[] suffixes)
        {
            for (var index = 0; ; index++)
            {
                var candidate = name + (index == 0 ? "" : (index + 1).ToString());
                if (suffixes.Any(suffix => contextMembers.Contains(candidate + suffix))) continue;
                foreach (var suffix in suffixes) contextMembers.Add(candidate + suffix);
                return candidate;
            }
        }
        string[] methodSuffixes = ["", "Async", "WithArguments", "WithArgumentsAsync"];
        var existingSuffixes = methodSuffixes.Concat(methodSuffixes.Select(suffix => "DataSet" + suffix)).ToArray();
        var namedRoutines = routineSources.Select(pair => (pair.Source, pair.Names, MethodName: ReserveMethodFamily(pair.Names.ClassName, existingSuffixes))).ToArray();
        var routines = namedRoutines.Select(pair => RoutineFile(snapshot, options, pair.MethodName,
            NeedsExplicitRows(snapshot, pair.Source.RoutineResult) ? ReserveMethodFamily(pair.MethodName + "Rows", methodSuffixes) : null,
            pair.Names, pair.Source, message => warnings.Add(ObjectName(pair.Source.Item) + ": " + message))).ToArray();
        var tables = new Dictionary<(string Schema, string Name), Entity>();
        foreach (var entity in entities.Where(entity => entity.Source.Item.Kind == SqlCatalogObjectKind.Table))
            tables[(SchemaName(snapshot, entity.Source.Item), entity.Source.Item.Name)] = entity;
        Entity? FindEntity(SqlRelationshipTable table)
        {
            var lookup = new SqlCatalogObject(SqlCatalogObjectKind.Table, table.Schema, table.Name, "", "");
            return tables.GetValueOrDefault((SchemaName(snapshot, lookup), table.Name));
        }
        var details = new Dictionary<(string Schema, string Table, string Name), PocoRelationshipDetails>();
        foreach (var detail in snapshot.RelationshipDetails) details[(detail.Child.Schema, detail.Child.Name, detail.Name)] = detail;
        var endpointCounts = snapshot.Relationships.GroupBy(source => (source.Child.Schema, source.Child.Name, source.Parent.Schema, source.Parent.Name))
            .ToDictionary(group => group.Key, group => group.Count());
        var relationshipList = new List<Relationship>();
        foreach (var source in snapshot.Relationships)
        {
            void Warn(string message) => warnings.Add("FK " + source.Name + ": " + message);
            var detail = details.GetValueOrDefault((source.Child.Schema, source.Child.Name, source.Name));
            var child = FindEntity(source.Child);
            var parent = FindEntity(source.Parent);
            if (child == null && parent == null) continue;
            if (child == null || parent == null)
            {
                Warn("Include " + source.Child.Name + " and " + source.Parent.Name + " to generate the relationship."); continue;
            }
            if (detail?.Disabled == true)
            {
                Warn("The constraint is disabled. The navigation was omitted."); continue;
            }
            var columns = source.Columns.OrderBy(column => column.Ordinal).ToArray();
            var parentPrimaryNames = parent.Key.Select(property => property.Column.Name).ToArray();
            var referencedNames = columns.Select(column => column.ParentColumn).ToArray();
            if (parentPrimaryNames.Length == referencedNames.Length && new HashSet<string>(parentPrimaryNames, StringComparer.Ordinal).SetEquals(referencedNames))
                columns = parentPrimaryNames.Select(name => columns.First(column => column.ParentColumn == name)).ToArray();
            Property[] Choose(Entity entity, Func<SqlRelationshipColumn, string> getName) => columns
                .Select(column => entity.Properties.FirstOrDefault(property => property.Column.Name == getName(column))).OfType<Property>().ToArray();
            var childKey = Choose(child, column => column.ChildColumn);
            var parentKey = Choose(parent, column => column.ParentColumn);
            if (columns.Length == 0 || childKey.Length != columns.Length || parentKey.Length != columns.Length)
            {
                Warn("A key column was unavailable. The navigation was omitted."); continue;
            }
            if (!childKey.Zip(parentKey).All(pair => pair.First.BaseType == pair.Second.BaseType))
            {
                Warn("The paired foreign-key columns have incompatible CLR types. The navigation was omitted."); continue;
            }
            if (!child.Mapped || !parent.Mapped || parent.Key.Length == 0 || child.Key.Length == 0)
            {
                Warn("Both ends need mapped keyed tables. The navigation was omitted."); continue;
            }
            if (parentKey.Any(property => property.Column.Nullable != false))
            {
                Warn("The principal key contains a nullable or unknown-nullability column. EF key nullability would change the model; the navigation was omitted."); continue;
            }
            if (!parentKey.Select(property => property.Name).SequenceEqual(parent.Key.Select(property => property.Name)))
            {
                Warn("The relationship refers to an alternate key. The EF6 navigation was omitted."); continue;
            }
            var sameEntity = child.Names.ClassName == parent.Names.ClassName;
            var required = childKey.All(property => property.Column.Nullable == false);
            var childColumns = childKey.Select(property => property.Column.Name).ToArray();
            var childPrimaryNames = child.Key.Select(property => property.Column.Name).ToArray();
            var sharedPrimaryKey = required && childColumns.SequenceEqual(childPrimaryNames);
            var childColumnSet = new HashSet<string>(childColumns, StringComparer.Ordinal);
            var unique = new[] { child.Source.PrimaryKey }.Concat(child.Source.UniqueKeys)
                .Any(key => key.Length > 0 && new HashSet<string>(key, StringComparer.Ordinal).IsSubsetOf(childColumnSet));
            if (unique && !sharedPrimaryKey)
                Warn("EF6 represents this unique foreign key as a reference and collection; its 1:1 mapping requires a shared primary key in the principal key order.");
            var deleteAction = detail?.DeleteAction ?? "NO_ACTION";
            if (detail?.NotTrusted == true) Warn("The constraint is not trusted; existing rows may violate it.");
            if (detail != null && !SameText(detail.UpdateAction, "NO_ACTION")) Warn("The database ON UPDATE action is retained in the database. EF does not model update actions.");
            if (SameText(deleteAction, "SET_DEFAULT")) Warn("SET DEFAULT remains database-only and is represented as NoAction in EF.");
            if (SameText(deleteAction, "SET_NULL")) Warn("SET NULL remains database-only; EF6 cascade deletion is disabled for this relationship.");
            var parentName = childColumns.Length == 1 && childColumns[0].EndsWith("Id", StringComparison.OrdinalIgnoreCase) && childColumns[0].Length > 2
                ? Identifier(childColumns[0][..^2]) : sameEntity ? "Parent" : parent.Names.ClassName;
            var toParent = AddName(child.Used, parentName);
            var childrenName = sharedPrimaryKey ? sameEntity ? "Child" : child.Names.ClassName : sameEntity ? "Children" : child.Names.SetName;
            if (endpointCounts[(source.Child.Schema, source.Child.Name, source.Parent.Schema, source.Parent.Name)] > 1) childrenName += "Via" + toParent;
            relationshipList.Add(new(source.Name, child, parent, childKey, parentKey, toParent, AddName(parent.Used, childrenName), sharedPrimaryKey, required, deleteAction));
        }
        var relationships = relationshipList.ToArray();
        var files = new List<PocoGeneratedFile>();
        foreach (var entity in entities)
            files.Add(EntityFile(snapshot, options, relationships.Where(relationship => relationship.Child.Names.ClassName == entity.Names.ClassName
                || relationship.Parent.Names.ClassName == entity.Names.ClassName).ToArray(), entity));
        if (entities.Length > 0 || routines.Length > 0) files.Add(ContextFile(snapshot, options, entities, relationships));
        if (routines.Length > 0) files.Add(ProcedureRuntimeFile(options));
        files.AddRange(routines);
        return new(files.ToArray(), warnings.Distinct().ToArray());
    }

    private const string Ef6TransactionTemplate = @"        // Older EF references may omit these Database properties.
        private static readonly global::System.Reflection.PropertyInfo__N__ EfTransaction =
            typeof(global::System.Data.Entity.Database).GetProperty(""CurrentTransaction"");
        private static readonly global::System.Reflection.PropertyInfo__N__ StoreTransaction =
            EfTransaction?.PropertyType.GetProperty(""UnderlyingTransaction"");
        private static readonly global::System.Reflection.PropertyInfo__N__ EfCommandTimeout =
            typeof(global::System.Data.Entity.Database).GetProperty(""CommandTimeout"");

        internal static global::System.Data.Common.DbTransaction__N__ GetEf6Transaction(global::System.Data.Entity.Database database)
        {
            var current = EfTransaction?.GetValue(database, null);
            if (current == null) return null;
            return StoreTransaction?.GetValue(current, null) as global::System.Data.Common.DbTransaction
                ?? throw new global::System.InvalidOperationException(""The current EF transaction did not expose its ADO.NET transaction."");
        }

        internal static int? GetEf6CommandTimeout(global::System.Data.Entity.Database database)
            => (int?)EfCommandTimeout?.GetValue(database, null);

";
private const string ProcedureRuntimeTemplate = @"    // Shared execution policy for generated procedures. Caller-owned connections and transactions remain caller-owned.
    internal static class __RUNTIME__
    {
__EF6_TRANSACTION__        private static void Configure(global::System.Data.Common.DbCommand command,
            global::System.Data.Common.DbTransaction__N__ transaction, int? commandTimeout)
        {
            if (transaction != null && !global::System.Object.ReferenceEquals(transaction.Connection, command.Connection))
                throw new global::System.ArgumentException(""The transaction belongs to a different connection."", nameof(transaction));
            command.Transaction = transaction;
            if (commandTimeout.HasValue) command.CommandTimeout = commandTimeout.Value;
        }

        private static void Cancel(global::System.Data.Common.DbCommand command)
        {
            try { command.Cancel(); }
            catch (global::System.Exception) { /* Cancellation must not mask the operation's original outcome. */ }
        }

        internal static T Execute<T>(global::System.Data.Common.DbCommand command,
            global::System.Data.Common.DbTransaction__N__ transaction, int? commandTimeout,
            global::System.Threading.CancellationToken cancellationToken,
            global::System.Func<global::System.Data.Common.DbCommand, global::System.Threading.CancellationToken, T> read,
            global::System.Action<global::System.Data.Common.DbCommand> completed)
        {
            using (command)
            {
                var connection = command.Connection ?? throw new global::System.InvalidOperationException(""The command has no connection."");
                Configure(command, transaction, commandTimeout);
                bool openedHere = connection.State == global::System.Data.ConnectionState.Closed;
                if (!openedHere && connection.State != global::System.Data.ConnectionState.Open)
                    throw new global::System.InvalidOperationException(""The connection must be open or closed before executing a procedure."");
                using (cancellationToken.Register(() => Cancel(command)))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (openedHere) connection.Open();
                        cancellationToken.ThrowIfCancellationRequested();
                        var result = read(command, cancellationToken);
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            completed(command); // Every reader has been disposed before output parameters are read.
                            return result;
                        }
                        catch
                        {
                            if (result is global::System.IDisposable disposable) disposable.Dispose();
                            throw;
                        }
                    }
                    finally
                    {
                        if (openedHere && connection.State != global::System.Data.ConnectionState.Closed) connection.Close();
                    }
                }
            }
        }

        internal static async global::System.Threading.Tasks.Task<T> ExecuteAsync<T>(global::System.Data.Common.DbCommand command,
            global::System.Data.Common.DbTransaction__N__ transaction, int? commandTimeout,
            global::System.Threading.CancellationToken cancellationToken,
            global::System.Func<global::System.Data.Common.DbCommand, global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<T>> read,
            global::System.Action<global::System.Data.Common.DbCommand> completed)
        {
            using (command)
            {
                var connection = command.Connection ?? throw new global::System.InvalidOperationException(""The command has no connection."");
                Configure(command, transaction, commandTimeout);
                bool openedHere = connection.State == global::System.Data.ConnectionState.Closed;
                if (!openedHere && connection.State != global::System.Data.ConnectionState.Open)
                    throw new global::System.InvalidOperationException(""The connection must be open or closed before executing a procedure."");
                using (cancellationToken.Register(() => Cancel(command)))
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (openedHere) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                        var result = await read(command, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            completed(command);
                            return result;
                        }
                        catch
                        {
                            if (result is global::System.IDisposable disposable) disposable.Dispose();
                            throw;
                        }
                    }
                    finally
                    {
                        if (openedHere && connection.State != global::System.Data.ConnectionState.Closed) connection.Close();
                    }
                }
            }
        }

        internal static global::System.Collections.Generic.List<T> ReadRows<T>(global::System.Data.Common.DbCommand command,
            global::System.Threading.CancellationToken cancellationToken,
            global::System.Func<global::System.Data.Common.DbDataReader, T> readRow)
        {
            var rows = new global::System.Collections.Generic.List<T>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.FieldCount == 0 && reader.NextResult()) cancellationToken.ThrowIfCancellationRequested();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add(readRow(reader));
                }
            }
            return rows;
        }

        internal static async global::System.Threading.Tasks.Task<global::System.Collections.Generic.List<T>> ReadRowsAsync<T>(
            global::System.Data.Common.DbCommand command, global::System.Threading.CancellationToken cancellationToken,
            global::System.Func<global::System.Data.Common.DbDataReader, T> readRow)
        {
            var rows = new global::System.Collections.Generic.List<T>();
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.FieldCount == 0 && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)) { }
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rows.Add(readRow(reader));
            }
            return rows;
        }

        private static global::System.Data.DataTable CreateResultTable(global::System.Data.Common.DbDataReader reader, int resultIndex)
        {
            var table = new global::System.Data.DataTable(""Table"" + resultIndex.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            var names = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                string original = reader.GetName(index);
                string stem = string.IsNullOrWhiteSpace(original) ? ""Column"" + (index + 1).ToString(global::System.Globalization.CultureInfo.InvariantCulture) : original;
                string name = stem;
                int suffix = 2;
                while (!names.Add(name)) name = stem + ""_"" + (suffix++).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                var type = reader.GetFieldType(index) ?? typeof(object);
                type = global::System.Nullable.GetUnderlyingType(type) ?? type;
                var column = table.Columns.Add(name, type);
                column.AllowDBNull = true;
                column.ExtendedProperties[""SourceColumnName""] = original;
                column.ExtendedProperties[""SourceOrdinal""] = index;
            }
            return table;
        }

        internal static global::System.Data.DataSet ReadDataSet(global::System.Data.Common.DbCommand command,
            global::System.Threading.CancellationToken cancellationToken)
        {
            var result = new global::System.Data.DataSet();
            try
            {
                using (var reader = command.ExecuteReader())
                {
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (reader.FieldCount == 0) continue;
                        var table = CreateResultTable(reader, result.Tables.Count + 1);
                        result.Tables.Add(table);
                        var values = new object[reader.FieldCount];
                        table.BeginLoadData();
                        try
                        {
                            while (reader.Read())
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                reader.GetValues(values);
                                table.Rows.Add(values); // DataRow stores its own values; this one buffer is reused.
                            }
                        }
                        finally { table.EndLoadData(); }
                    }
                    while (reader.NextResult());
                }
                result.AcceptChanges();
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal static async global::System.Threading.Tasks.Task<global::System.Data.DataSet> ReadDataSetAsync(
            global::System.Data.Common.DbCommand command, global::System.Threading.CancellationToken cancellationToken)
        {
            var result = new global::System.Data.DataSet();
            try
            {
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (reader.FieldCount == 0) continue;
                        var table = CreateResultTable(reader, result.Tables.Count + 1);
                        result.Tables.Add(table);
                        var values = new object[reader.FieldCount];
                        table.BeginLoadData();
                        try
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                reader.GetValues(values);
                                table.Rows.Add(values);
                            }
                        }
                        finally { table.EndLoadData(); }
                    }
                    while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
                }
                result.AcceptChanges();
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
";
}
