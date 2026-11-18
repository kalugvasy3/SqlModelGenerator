namespace CommonClassLibrary

// Extracted from AnotherEdit CommonClassLibrary/PocoCodeGeneration.fs (2026-11-18).
// This standalone port retains the EF6 generation/source-compatibility path.

open System
open System.Collections.Generic
open System.Data
open System.Text
open System.Text.RegularExpressions
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.CSharp.Syntax

module PocoCodeGeneration =
    let private sameText left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let private validName (name: string) =
        not (String.IsNullOrWhiteSpace name)
        && SyntaxFacts.IsValidIdentifier name
        && SyntaxFacts.GetKeywordKind name = SyntaxKind.None
        && not (name.StartsWith("@", StringComparison.Ordinal))

    let private identifier (name: string) =
        let safe = Regex.Replace(name, @"[^\p{L}\p{Nd}_]+", "_").Trim('_')
        if safe = "" then "Item"
        elif validName safe then safe
        else "_" + safe

    let private irregular = [ "person", "people"; "child", "children"; "category", "categories"; "status", "statuses"; "address", "addresses" ]

    let private inflect naming (name: string) =
        if naming = PocoNaming.AsInDatabase then name
        else
            let ending = Regex.Match(name, @"[A-Z]+$|[A-Z][a-z]+$|[a-z]+$")
            if not ending.Success then name
            else
                let word = ending.Value
                let lower = word.ToLowerInvariant()
                let ends (suffix: string) = lower.EndsWith(suffix, StringComparison.Ordinal)
                let known = irregular |> List.tryFind (fun (singular, plural) -> singular = lower || plural = lower)
                let inflected =
                    match naming, known with
                    | PocoNaming.Singular, Some(singular, _) -> singular
                    | PocoNaming.Plural, Some(_, plural) -> plural
                    | _, _ when [ "data"; "metadata"; "news"; "series"; "species"; "equipment" ] |> List.contains lower -> lower
                    | PocoNaming.Singular, _ when lower.Length > 3 && ends "ies" -> lower[..lower.Length - 4] + "y"
                    | PocoNaming.Singular, _ when [ "sses"; "ches"; "shes"; "xes"; "zzes" ] |> List.exists ends -> lower[..lower.Length - 3]
                    | PocoNaming.Singular, _ when lower.Length > 1 && ends "s" && not (ends "ss" || ends "us" || ends "is") -> lower[..lower.Length - 2]
                    | PocoNaming.Plural, _ when ends "y" && lower.Length > 1 && not ("aeiou".Contains(lower.[lower.Length - 2])) -> lower[..lower.Length - 2] + "ies"
                    | PocoNaming.Plural, _ when [ "ss"; "ch"; "sh"; "x"; "z" ] |> List.exists ends -> lower + "es"
                    | PocoNaming.Plural, _ when ends "s" && not (ends "us" || ends "is") -> lower
                    | PocoNaming.Plural, _ -> lower + "s"
                    | _ -> lower
                let result =
                    if word = word.ToUpperInvariant() then inflected.ToUpperInvariant()
                    elif Char.IsUpper word.[0] then string (Char.ToUpperInvariant inflected.[0]) + inflected.[1..]
                    else inflected
                name[..ending.Index - 1] + result

    let internal NameVariants(name: string) =
        [| name; inflect PocoNaming.Singular name; inflect PocoNaming.Plural name |] |> Array.distinct

    let Names (options: PocoOptions) (item: SqlCatalogObject) =
        let name = identifier item.Name
        let className, setName =
            if item.Kind = SqlCatalogObjectKind.Procedure then name, name
            else identifier (inflect options.ClassNaming name), identifier (inflect options.SetNaming name)
        let defaults = { ClassName = className; SetName = setName; Properties = Map.empty }
        match options.Overrides |> Map.tryFind (item.Kind, item.Schema, item.Name) with
        | Some names ->
            { names with
                ClassName = if String.IsNullOrWhiteSpace names.ClassName then className else names.ClassName
                SetName = if String.IsNullOrWhiteSpace names.SetName then setName else names.SetName }
        | None -> defaults

    let private literal (value: string) = SyntaxFactory.Literal(value).Text

    let private baseType (value: Type) =
        if isNull value || value = typeof<obj> then None
        else
            let value = defaultArg (Option.ofObj (Nullable.GetUnderlyingType value)) value
            let aliases =
                [ typeof<bool>, "bool"; typeof<byte>, "byte"; typeof<sbyte>, "sbyte"; typeof<int16>, "short"
                  typeof<uint16>, "ushort"; typeof<int>, "int"; typeof<uint32>, "uint"; typeof<int64>, "long"
                  typeof<uint64>, "ulong"; typeof<single>, "float"; typeof<double>, "double"; typeof<decimal>, "decimal"
                  typeof<char>, "char"; typeof<string>, "string"; typeof<byte[]>, "byte[]" ]
            match aliases |> List.tryPick (fun (key, name) -> if key = value then Some name else None) with
            | Some name -> Some name
            | None when [ typeof<Guid>; typeof<DateTime>; typeof<DateTimeOffset>; typeof<TimeSpan>; typeof<DateOnly>; typeof<TimeOnly> ] |> List.contains value ->
                Some("global::" + value.FullName)
            | _ -> None

    let private propertyType options (column: PocoColumn) name =
        let nullable = column.Nullable <> Some false
        let value = column.ClrType.IsValueType
        name + (if nullable && (value || options.NullableReferenceTypes) then "?" else "")

    let private addName (used: HashSet<string>) name =
        let mutable candidate = name
        let mutable number = 2
        while not (used.Add candidate) do
            candidate <- name + "_" + string number
            number <- number + 1
        candidate

    let private objectName (item: SqlCatalogObject) =
        if String.IsNullOrWhiteSpace item.Schema then item.Name else item.Schema + "." + item.Name

    let private isSqlVariant (column: PocoColumn) =
        column.ClrType = typeof<obj> && sameText column.StoreType "sql_variant"

    type private Property = { Column: PocoColumn; Name: string; Type: string; BaseType: string }

    type private Entity =
        { Source: PocoObject
          Names: PocoObjectNames
          Properties: Property array
          Key: Property array
          Mapped: bool
          Used: HashSet<string> }

    type private Relationship =
        { Name: string
          Child: Entity
          Parent: Entity
          ChildKey: Property array
          ParentKey: Property array
          ToParent: string
          ToChildren: string
          OneToOne: bool
          Required: bool
          DeleteAction: string }

    let private properties options allowSqlVariant (names: Map<string, string>) (used: HashSet<string>) (columns: PocoColumn array) warn =
        let reserved = HashSet<string>(used, StringComparer.Ordinal)
        for KeyValue(original, replacement) in names do
            if columns |> Array.exists (fun column -> column.Name = original) then
                if not (validName replacement) || not (reserved.Add replacement) then
                    invalidArg "options" ("Invalid or duplicate C# member name " + replacement + ".")
        for name in reserved do used.Add name |> ignore
        columns
        |> Array.choose (fun column ->
            let clrType = if allowSqlVariant && isSqlVariant column then Some "object" else baseType column.ClrType
            match clrType with
            | Some baseName ->
                let name =
                    match names |> Map.tryFind column.Name with
                    | Some name -> name
                    | None -> addName used (identifier column.Name)
                Some { Column = column; Name = name; Type = propertyType options column baseName; BaseType = baseName }
            | None ->
                warn ("Unsupported CLR type for " + column.Name + " (" + column.StoreType + "). The member was omitted.")
                None)

    let private schemaName snapshot (item: SqlCatalogObject) =
        if snapshot.UsesAccessSyntax || sameText snapshot.ProviderId "sqlite" || sameText snapshot.ProviderId "sqlcipher" then ""
        else item.Schema

    let private formatFile options name imports (body: StringBuilder -> unit) =
        let builder = StringBuilder()
        let line (text: string) = builder.AppendLine(text) |> ignore
        line "// <auto-generated by AnotherEdit>"
        if options.NullableReferenceTypes then line "#nullable enable"
        for import in imports do line ("using " + import + ";")
        line ("namespace " + options.Namespace)
        line "{"
        body builder
        line "}"
        { FileName = name + ".g.cs"; Text = builder.ToString() }

    let private appendLine (builder: StringBuilder) (text: string) = builder.AppendLine(text) |> ignore

    let private scalar (line: string -> unit) annotation options (property: Property) =
        if annotation then
            line ("        [global::System.ComponentModel.DataAnnotations.Schema.ColumnAttribute(" + literal property.Column.Name + ")]")
            match property.Column.Size with
            | Some size when size > 0 && (property.BaseType = "string" || property.BaseType = "byte[]") ->
                line ("        [global::System.ComponentModel.DataAnnotations.MaxLengthAttribute(" + string size + ")]")
            | _ -> ()
        let init =
            if options.NullableReferenceTypes && property.Column.Nullable = Some false && not property.Column.ClrType.IsValueType then " = null!;"
            else ""
        line ("        public " + property.Type + " " + property.Name + " { get; set; }" + init)

    let private entityFile snapshot options relationships entity =
        let item = entity.Source.Item
        let name = entity.Names.ClassName
        formatFile options name [] (fun builder ->
            let line = appendLine builder
            let schema = schemaName snapshot item
            let schema = if String.IsNullOrWhiteSpace schema then "" else ", Schema = " + literal schema
            if item.Kind = SqlCatalogObjectKind.Table then
                line ("    [global::System.ComponentModel.DataAnnotations.Schema.TableAttribute(" + literal item.Name + schema + ")]")
            line ("    public partial class " + name)
            line "    {"
            for property in entity.Properties do scalar line true options property
            for relationship in relationships do
                if name = relationship.Child.Names.ClassName then
                    let nullable = if options.NullableReferenceTypes && not relationship.Required then "?" else ""
                    let init = if options.NullableReferenceTypes && relationship.Required then " = null!;" else ""
                    line ("        public " + relationship.Parent.Names.ClassName + nullable + " " + relationship.ToParent + " { get; set; }" + init)
                if name = relationship.Parent.Names.ClassName then
                    let child = relationship.Child.Names.ClassName
                    if relationship.OneToOne then
                        let nullable = if options.NullableReferenceTypes then "?" else ""
                        line ("        public " + child + nullable + " " + relationship.ToChildren + " { get; set; }")
                    else
                        line ("        public global::System.Collections.Generic.ICollection<" + child + "> " + relationship.ToChildren
                              + " { get; set; } = new global::System.Collections.Generic.List<" + child + ">();")
            line "    }")

    let private keySelector (columns: Property array) =
        if columns.Length = 1 then "e => e." + columns.[0].Name
        else "e => new { " + String.Join(", ", columns |> Array.map (fun property -> "e." + property.Name)) + " }"

    let private parameterDbType (column: PocoColumn) =
        match column.StoreType.ToLowerInvariant() with
        | "bit" -> Some "Boolean"
        | "tinyint" -> Some "Byte"
        | "smallint" -> Some "Int16"
        | "int" -> Some "Int32"
        | "bigint" -> Some "Int64"
        | "real" -> Some "Single"
        | "float" -> Some "Double"
        | "decimal" | "numeric" -> Some "Decimal"
        | "money" | "smallmoney" -> Some "Currency"
        | "char" -> Some "AnsiStringFixedLength"
        | "nchar" -> Some "StringFixedLength"
        | "varchar" | "text" -> Some "AnsiString"
        | "nvarchar" | "ntext" -> Some "String"
        | "binary" | "varbinary" | "image" | "timestamp" | "rowversion" -> Some "Binary"
        | "uniqueidentifier" -> Some "Guid"
        | "datetime" | "smalldatetime" -> Some "DateTime"
        | "datetime2" -> Some "DateTime2"
        | "datetimeoffset" -> Some "DateTimeOffset"
        | "date" -> Some "Date"
        | "time" -> Some "Time"
        | "xml" -> Some "Xml"
        | "sql_variant" when isSqlVariant column -> Some "Object"
        | _ -> None

    let private quoteIdentifier snapshot (name: string) =
        if snapshot.UsesAccessSyntax then "[" + name.Replace("]", "]]") + "]"
        else SqlProviders.QuoteIdentifier snapshot.ProviderId name

    let private quotedName snapshot (item: SqlCatalogObject) =
        let quote = quoteIdentifier snapshot
        let schema = schemaName snapshot item
        if String.IsNullOrWhiteSpace schema then quote item.Name
        else quote schema + "." + quote item.Name

    let private propertyMapping snapshot options isKey (line: string -> unit) (property: Property) =
        let column = property.Column
        let sqlServer = sameText snapshot.ProviderId "sqlserver"
        let rowVersion = sqlServer && (sameText column.StoreType "rowversion" || sameText column.StoreType "timestamp")
        let baseMapping = "            entity.Property(e => e." + property.Name + ").HasColumnName(" + literal column.Name + ")"
        let mutable mapping = baseMapping
        let append value = mapping <- mapping + value
        if rowVersion then append ".IsRowVersion()"
        elif column.AutoNumber || column.Computed || isKey then
            append (".HasDatabaseGeneratedOption(global::System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption."
                    + (if column.AutoNumber then "Identity" elif column.Computed then "Computed" else "None") + ")")
        let store = column.StoreType.ToLowerInvariant()
        if sqlServer && not rowVersion && (parameterDbType column).IsSome then
            append (".HasColumnType(" + literal store + ")")
        if property.BaseType = "decimal" then
            match column.Precision, column.Scale with
            | Some precision, Some scale -> append (sprintf ".HasPrecision(%d, %d)" precision scale)
            | _ -> ()
        if column.ClrType = typeof<string> || column.ClrType = typeof<byte[]> then
            match column.Size with
            | Some size when size > 0 -> append (sprintf ".HasMaxLength(%d)" size)
            | Some -1 -> append ".IsMaxLength()"
            | _ -> ()
            if sqlServer && column.ClrType = typeof<string> then
                match store with
                | "char" | "varchar" | "text" -> append ".IsUnicode(false)"
                | "nchar" | "nvarchar" | "ntext" -> append ".IsUnicode(true)"
                | _ -> ()
            if sqlServer && not rowVersion then
                let fixedLength = store = "char" || store = "nchar" || store = "binary"
                if fixedLength then append ".IsFixedLength()"
                else append ".IsVariableLength()"
        // Explicit nullable facets replace older required mappings during compatible
        // generation. Unknown provider metadata retains the existing configuration.
        match column.Nullable with
        | Some false -> append ".IsRequired()"
        | Some true -> append ".IsOptional()"
        | None -> ()
        line (mapping + ";")

    let private configuredConnectionName context =
        if context = "CreateConfiguredConnection" then "CreateConfiguredConnectionCore" else "CreateConfiguredConnection"

    let private contextFile snapshot options (entities: Entity array) (relationships: Relationship array) =
        let framework = "System.Data.Entity"
        let builderType = "DbModelBuilder"
        let context = options.ContextName
        let configuredConnection = configuredConnectionName context
        formatFile options context [ framework ] (fun builder ->
            let line = appendLine builder
            line ("    public partial class " + context + " : global::" + framework + ".DbContext")
            line "    {"
            line ("        static " + context + "() => global::System.Data.Entity.Database.SetInitializer<" + context + ">(null);")
            line ("        public " + context + "() : base(" + configuredConnection + "(), true) { }")
            line ("        public " + context + "(global::System.Data.Common.DbConnection connection) : base(connection, false) { }")
            line ""
            line ("        private static global::System.Data.Common.DbConnection " + configuredConnection + "()")
            line "        {"
            line "            var connections = global::System.Configuration.ConfigurationManager.ConnectionStrings;"
            line ("            var settings = connections[nameof(" + context + ")] ?? connections[typeof(" + context + ").FullName];")
            line ("            if (settings == null) throw new global::System.InvalidOperationException("
                  + literal ("Add a connection string named " + context + " to the application's App.config or Web.config.") + ");")
            line "            var provider = settings.ProviderName;"
            line "            var connectionString = settings.ConnectionString;"
            line "            if (global::System.String.Equals(provider, \"System.Data.EntityClient\", global::System.StringComparison.OrdinalIgnoreCase))"
            line "            {"
            line "                var legacy = new global::System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };"
            line "                provider = legacy.TryGetValue(\"provider\", out var configuredProvider) ? configuredProvider as string : null;"
            line "                connectionString = legacy.TryGetValue(\"provider connection string\", out var providerString) ? providerString as string : null;"
            line "            }"
            line "            if (global::System.String.IsNullOrWhiteSpace(provider) || global::System.String.IsNullOrWhiteSpace(connectionString))"
            line "                throw new global::System.InvalidOperationException(\"The configured EF6 connection must specify its ADO.NET provider and connection string.\");"
            line "            global::System.Data.Common.DbProviderFactory factory ="
            line "                global::System.String.Equals(provider, \"System.Data.SqlClient\", global::System.StringComparison.OrdinalIgnoreCase)"
            line "                    ? global::System.Data.SqlClient.SqlClientFactory.Instance"
            line "                    : global::System.Data.Common.DbProviderFactories.GetFactory(provider);"
            line "            var connection = factory.CreateConnection()"
            line "                ?? throw new global::System.InvalidOperationException(\"The configured ADO.NET provider is unavailable: \" + provider);"
            line "            try"
            line "            {"
            line "                connection.ConnectionString = connectionString;"
            line "                return connection;"
            line "            }"
            line "            catch"
            line "            {"
            line "                connection.Dispose();"
            line "                throw;"
            line "            }"
            line "        }"
            line ""
            for entity in entities do
                if entity.Mapped then
                    line ("        public global::" + framework + ".DbSet<" + entity.Names.ClassName + "> " + entity.Names.SetName
                          + " => base.Set<" + entity.Names.ClassName + ">();")
                elif entity.Properties.Length > 0 then
                    let quote = quoteIdentifier snapshot
                    let projection =
                        entity.Properties
                        |> Array.map (fun property -> quote property.Column.Name + " AS " + quote property.Name)
                        |> String.concat ", "
                    let sql = "SELECT " + projection + " FROM " + quotedName snapshot entity.Source.Item
                    line ("        public global::System.Data.Entity.Infrastructure.DbRawSqlQuery<" + entity.Names.ClassName + "> Query"
                          + entity.Names.SetName + "() => base.Database.SqlQuery<" + entity.Names.ClassName + ">(" + literal sql + ");")
            line ""
            line ("        protected override void OnModelCreating(global::" + framework + "." + builderType + " modelBuilder)")
            line "        {"
            line "            base.OnModelCreating(modelBuilder);"
            for entity in entities do
                if entity.Mapped then
                    let item = entity.Source.Item
                    let schema = schemaName snapshot item
                    let store = literal item.Name + (if String.IsNullOrWhiteSpace schema then "" else ", " + literal schema)
                    let mapper = "ToTable"
                    line "            {"
                    line ("            var entity = modelBuilder.Entity<" + entity.Names.ClassName + ">();")
                    line ("            entity." + mapper + "(" + store + ");")
                    if entity.Key.Length > 0 then line ("            entity.HasKey(" + keySelector entity.Key + ");")
                    for property in entity.Properties do
                        let isKey = entity.Key |> Array.exists (fun column -> column.Name = property.Name)
                        propertyMapping snapshot options isKey line property
                    line "            }"
            for relationship in relationships do
                let relationshipSource = "            modelBuilder.Entity<" + relationship.Child.Names.ClassName + ">()"
                let withMany = ".WithMany(e => e." + relationship.ToChildren + ").HasForeignKey(" + keySelector relationship.ChildKey + ")"
                let cascade = sameText relationship.DeleteAction "CASCADE"
                if relationship.OneToOne then
                    line (relationshipSource + ".HasRequired(e => e." + relationship.ToParent + ").WithOptional(e => e."
                          + relationship.ToChildren + ").WillCascadeOnDelete(" + (if cascade then "true" else "false") + ");")
                else
                    let hasParent = if relationship.Required then ".HasRequired" else ".HasOptional"
                    line (relationshipSource + hasParent + "(e => e." + relationship.ToParent + ")" + withMany
                          + ".WillCascadeOnDelete(" + (if cascade then "true" else "false") + ");")
            line "            OnModelCreatingPartial(modelBuilder);"
            line "        }"
            line ""
            line ("        partial void OnModelCreatingPartial(global::" + framework + "." + builderType + " modelBuilder);")
            line "    }")

    let private procedureRuntimeName = "AnotherEditProcedureRuntime"

    let private procedureRuntimeFile options =
        formatFile options procedureRuntimeName [] (fun builder ->
            let ef6Transaction =
                """        // Older EF references may omit these Database properties.
        private static readonly global::System.Reflection.PropertyInfo__N__ EfTransaction =
            typeof(global::System.Data.Entity.Database).GetProperty("CurrentTransaction");
        private static readonly global::System.Reflection.PropertyInfo__N__ StoreTransaction =
            EfTransaction?.PropertyType.GetProperty("UnderlyingTransaction");
        private static readonly global::System.Reflection.PropertyInfo__N__ EfCommandTimeout =
            typeof(global::System.Data.Entity.Database).GetProperty("CommandTimeout");

        internal static global::System.Data.Common.DbTransaction__N__ GetEf6Transaction(global::System.Data.Entity.Database database)
        {
            var current = EfTransaction?.GetValue(database, null);
            if (current == null) return null;
            return StoreTransaction?.GetValue(current, null) as global::System.Data.Common.DbTransaction
                ?? throw new global::System.InvalidOperationException("The current EF transaction did not expose its ADO.NET transaction.");
        }

        internal static int? GetEf6CommandTimeout(global::System.Data.Entity.Database database)
            => (int?)EfCommandTimeout?.GetValue(database, null);

"""
            let runtime = """    // Shared execution policy for generated procedures. Caller-owned connections and transactions remain caller-owned.
    internal static class __RUNTIME__
    {
__EF6_TRANSACTION__        private static void Configure(global::System.Data.Common.DbCommand command,
            global::System.Data.Common.DbTransaction__N__ transaction, int? commandTimeout)
        {
            if (transaction != null && !global::System.Object.ReferenceEquals(transaction.Connection, command.Connection))
                throw new global::System.ArgumentException("The transaction belongs to a different connection.", nameof(transaction));
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
                var connection = command.Connection ?? throw new global::System.InvalidOperationException("The command has no connection.");
                Configure(command, transaction, commandTimeout);
                bool openedHere = connection.State == global::System.Data.ConnectionState.Closed;
                if (!openedHere && connection.State != global::System.Data.ConnectionState.Open)
                    throw new global::System.InvalidOperationException("The connection must be open or closed before executing a procedure.");
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
                var connection = command.Connection ?? throw new global::System.InvalidOperationException("The command has no connection.");
                Configure(command, transaction, commandTimeout);
                bool openedHere = connection.State == global::System.Data.ConnectionState.Closed;
                if (!openedHere && connection.State != global::System.Data.ConnectionState.Open)
                    throw new global::System.InvalidOperationException("The connection must be open or closed before executing a procedure.");
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
            var table = new global::System.Data.DataTable("Table" + resultIndex.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
            var names = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < reader.FieldCount; index++)
            {
                string original = reader.GetName(index);
                string stem = string.IsNullOrWhiteSpace(original) ? "Column" + (index + 1).ToString(global::System.Globalization.CultureInfo.InvariantCulture) : original;
                string name = stem;
                int suffix = 2;
                while (!names.Add(name)) name = stem + "_" + (suffix++).ToString(global::System.Globalization.CultureInfo.InvariantCulture);
                var type = reader.GetFieldType(index) ?? typeof(object);
                type = global::System.Nullable.GetUnderlyingType(type) ?? type;
                var column = table.Columns.Add(name, type);
                column.AllowDBNull = true;
                column.ExtendedProperties["SourceColumnName"] = original;
                column.ExtendedProperties["SourceOrdinal"] = index;
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
"""
            builder.Append(runtime.Replace("__RUNTIME__", procedureRuntimeName).Replace("__EF6_TRANSACTION__", ef6Transaction)
                                  .Replace("__N__", if options.NullableReferenceTypes then "?" else "")) |> ignore)

    let private sqlServerRoutines snapshot =
        sameText snapshot.ProviderId "sqlserver" && not snapshot.UsesAccessSyntax

    let private needsExplicitRows snapshot result =
        match result with
        | PocoRoutineResult.Rows columns when sqlServerRoutines snapshot ->
            let names = columns |> Array.map _.Name
            (names |> Array.exists String.IsNullOrWhiteSpace)
            || (Array.distinct names).Length <> names.Length
            || (columns |> Array.exists isSqlVariant)
        | _ -> false

    let private routineFile snapshot options methodName explicitRowsMethod (names: PocoObjectNames) (source: PocoObject) warn =
        let name = names.ClassName
        let sqlServer = sqlServerRoutines snapshot
        let parameters = source.Parameters |> Array.sortBy _.Ordinal
        let arguments = HashSet<string>([ name + "Arguments" ], StringComparer.Ordinal)
        let outputs = HashSet<string>([ name + "Outputs"; "ReturnValue" ], StringComparer.Ordinal)
        let rowNames = HashSet<string>([ name + "Row" ], StringComparer.Ordinal)
        let input (parameter: PocoParameter) =
            parameter.Direction = ParameterDirection.Input || parameter.Direction = ParameterDirection.InputOutput
        let output (parameter: PocoParameter) =
            parameter.Direction = ParameterDirection.Output || parameter.Direction = ParameterDirection.InputOutput
        let parameterProperties used filter =
            parameters
            |> Array.filter filter
            |> Array.choose (fun parameter ->
                properties options sqlServer Map.empty used [| parameter.Column |] warn
                |> Array.tryHead
                |> Option.map (fun property -> parameter.Ordinal, property))
        let inputProperties = parameterProperties arguments input |> Map.ofArray
        let outputProperties = parameterProperties outputs output |> Map.ofArray
        let row =
            match source.RoutineResult with
            | PocoRoutineResult.Rows columns ->
                let known = properties options sqlServer Map.empty rowNames columns warn
                if known.Length = columns.Length then known
                else
                    warn "The first result cannot be read as a complete typed row."
                    [||]
            | PocoRoutineResult.Unknown reason ->
                warn ("Result shape unavailable. " + reason + " ExecuteDataSet/ExecuteDataSetAsync still read every result set at runtime.")
                [||]
            | PocoRoutineResult.NoRows -> [||]
        let binding =
            parameters
            |> Array.filter (fun parameter -> parameter.Direction <> ParameterDirection.ReturnValue)
            |> Array.choose (fun parameter ->
                match parameterDbType parameter.Column with
                | Some dbType when (not (input parameter) || inputProperties.ContainsKey parameter.Ordinal)
                                   && (not (output parameter) || outputProperties.ContainsKey parameter.Ordinal) -> Some(parameter, dbType)
                | _ -> None)
        let canBind =
            sqlServer && source.ParametersKnown
            && binding.Length = (parameters |> Array.filter (fun parameter -> parameter.Direction <> ParameterDirection.ReturnValue)).Length
            && (binding |> Array.map (fun (parameter, _) -> parameter.Ordinal) |> Array.distinct |> Array.length) = binding.Length
            && (binding |> Array.forall (fun (parameter, _) -> not (sameText parameter.Column.Name "@RETURN_VALUE")))
        if not canBind then warn "No executable command factory was generated for this provider or parameter signature."
        let rowsType = "global::System.Collections.Generic.List<" + name + "Row>"
        let readRows = procedureRuntimeName + ".ReadRows(command, token, ReadRow)"
        let readRowsAsync = procedureRuntimeName + ".ReadRowsAsync(command, token, ReadRow)"
        let dataSetType = "global::System.Data.DataSet"
        let readDataSet = procedureRuntimeName + ".ReadDataSet(command, token)"
        let readDataSetAsync = procedureRuntimeName + ".ReadDataSetAsync(command, token)"
        let typedDefault = row.Length > 0 && Option.isNone explicitRowsMethod
        let resultType, read, readAsync =
            if typedDefault then rowsType, readRows, readRowsAsync
            elif source.RoutineResult = PocoRoutineResult.NoRows then
                "int", "command.ExecuteNonQuery()", "command.ExecuteNonQueryAsync(token)"
            else dataSetType, readDataSet, readDataSetAsync
        let nullable = if options.NullableReferenceTypes then "?" else ""
        let imports = []
        formatFile options (name + "Procedure") imports (fun builder ->
            let line = appendLine builder
            let dto suffix (members: Property seq) returnValue =
                line ("    public partial class " + name + suffix)
                line "    {"
                for property in members do scalar line false options property
                if returnValue then line "        public int ReturnValue { get; set; }"
                line "    }"
                line ""
            dto "Arguments" (inputProperties |> Map.values) false
            if canBind || not outputProperties.IsEmpty then dto "Outputs" (outputProperties |> Map.values) canBind
            if row.Length > 0 then dto "Row" row false
            line ("    public static partial class " + name + "Procedure")
            line "    {"
            if canBind then
                line ("        public static global::System.Data.Common.DbCommand CreateCommand(global::System.Data.Common.DbConnection connection, "
                      + name + "Arguments arguments)")
                line "        {"
                line "            if (connection == null) throw new global::System.ArgumentNullException(nameof(connection));"
                line "            if (arguments == null) throw new global::System.ArgumentNullException(nameof(arguments));"
                line "            var command = connection.CreateCommand();"
                line "            try"
                line "            {"
                line "                command.CommandType = global::System.Data.CommandType.StoredProcedure;"
                line ("                command.CommandText = " + literal (quotedName snapshot source.Item) + ";")
                for parameter, dbType in binding do
                    let variable = "p" + string parameter.Ordinal
                    let assignment memberName value = line ("                " + variable + "." + memberName + " = " + value + ";")
                    line ("                var " + variable + " = command.CreateParameter();")
                    assignment "ParameterName" (literal parameter.Column.Name)
                    assignment "DbType" ("global::System.Data.DbType." + dbType)
                    assignment "Direction" ("global::System.Data.ParameterDirection." + string parameter.Direction)
                    parameter.Column.Size |> Option.iter (string >> assignment "Size")
                    parameter.Column.Precision |> Option.iter (fun value -> assignment "Precision" ("(byte)" + string value))
                    parameter.Column.Scale |> Option.iter (fun value -> assignment "Scale" ("(byte)" + string value))
                    inputProperties |> Map.tryFind parameter.Ordinal |> Option.iter (fun property ->
                        assignment "Value" ("(object" + (if options.NullableReferenceTypes then "?" else "") + ")arguments."
                                            + property.Name + " ?? global::System.DBNull.Value"))
                    line ("                command.Parameters.Add(" + variable + ");")
                line "                var returnValue = command.CreateParameter();"
                line "                returnValue.ParameterName = \"@RETURN_VALUE\";"
                line "                returnValue.DbType = global::System.Data.DbType.Int32;"
                line "                returnValue.Direction = global::System.Data.ParameterDirection.ReturnValue;"
                line "                command.Parameters.Add(returnValue);"
                line "                return command;"
                line "            }"
                line "            catch"
                line "            {"
                line "                command.Dispose();"
                line "                throw;"
                line "            }"
                line "        }"
                line ""
                line "        // Read outputs after execution and after closing the reader."
                line ("        public static " + name + "Outputs ReadOutputs(global::System.Data.Common.DbCommand command)")
                line ("            => new " + name + "Outputs")
                line "            {"
                for index in 0..binding.Length - 1 do
                    let parameter, _ = binding.[index]
                    outputProperties |> Map.tryFind parameter.Ordinal |> Option.iter (fun property ->
                        let value = sprintf "command.Parameters[%d].Value" index
                        let converted =
                            if property.Column.Nullable <> Some false then
                                value + " is null || " + value + " is global::System.DBNull ? default(" + property.Type
                                + ") : (" + property.Type + ")" + value
                            else
                                let typed = "typed" + string index
                                let present = if property.BaseType = "object" then " && !(" + typed + " is global::System.DBNull)" else ""
                                value + " is " + property.BaseType + " " + typed + present + " ? " + typed
                                + " : throw new global::System.InvalidOperationException(\"Missing or incompatible output value.\")"
                        line ("                " + property.Name + " = " + converted + ","))
                line (sprintf "                ReturnValue = command.Parameters[%d].Value is int status ? status : throw new global::System.InvalidOperationException(\"Missing return value.\")" binding.Length)
                line "            };"
            if row.Length > 0 then
                line ""
                line ("        public static " + name + "Row ReadRow(global::System.Data.Common.DbDataReader reader)")
                line ("            => new " + name + "Row")
                line "            {"
                for index in 0..row.Length - 1 do
                    let property = row.[index]
                    let value =
                        let isNullable = property.Column.Nullable <> Some false
                        if property.BaseType = "object" then
                            let missing =
                                if isNullable then "null"
                                else "throw new global::System.InvalidOperationException(\"Unexpected NULL result value.\")"
                            sprintf "reader.IsDBNull(%d) ? %s : reader.GetValue(%d)" index missing index
                        else
                            let getValue = sprintf "reader.GetFieldValue<%s>(%d)" property.BaseType index
                            if isNullable then sprintf "reader.IsDBNull(%d) ? default(%s) : (%s)%s" index property.Type property.Type getValue
                            else getValue
                    line ("                " + property.Name + " = " + value + ",")
                line "            };"
            if canBind then
                line ""
                line ("        private static void CopyOutputs(" + name + "Outputs source, " + name + "Outputs" + nullable + " target)")
                line "        {"
                line "            if (target == null) return;"
                for property in outputProperties |> Map.values do
                    line ("            target." + property.Name + " = source." + property.Name + ";")
                line "            target.ReturnValue = source.ReturnValue;"
                line "        }"
                let executeMethods suffix returnType readResult readResultAsync =
                    let signature = "global::System.Data.Common.DbConnection connection, " + name + "Arguments arguments"
                    let optional = "global::System.Data.Common.DbTransaction" + nullable + " transaction = null, int? commandTimeout = null, global::System.Threading.CancellationToken cancellationToken = default"
                    line ""
                    if suffix = "Rows" || (suffix = "" && typedDefault) then
                        line "        // Returns every row in the described first result set. Use ExecuteDataSet for all result sets."
                    elif suffix = "" && source.RoutineResult = PocoRoutineResult.NoRows then
                        line "        // Returns rows affected. SQL RETURN and output parameters are available through Outputs."
                    else
                        line "        // Reads every row of every result set. Empty result tables retain their column definitions."
                    line ("        public static " + returnType + " Execute" + suffix + "(" + signature + ", " + optional + ")")
                    line ("            => Execute" + suffix + "Core(connection, arguments, null, transaction, commandTimeout, cancellationToken);")
                    line ""
                    line ("        public static " + returnType + " Execute" + suffix + "(" + signature + ", out " + name + "Outputs outputs, " + optional + ")")
                    line "        {"
                    line ("            var collected = new " + name + "Outputs();")
                    line ("            var result = Execute" + suffix + "Core(connection, arguments, collected, transaction, commandTimeout, cancellationToken);")
                    line "            outputs = collected;"
                    line "            return result;"
                    line "        }"
                    line ""
                    line ("        private static " + returnType + " Execute" + suffix + "Core(" + signature + ", " + name + "Outputs" + nullable + " outputs, global::System.Data.Common.DbTransaction" + nullable + " transaction, int? commandTimeout, global::System.Threading.CancellationToken cancellationToken)")
                    line ("            => " + procedureRuntimeName + ".Execute(CreateCommand(connection, arguments), transaction, commandTimeout, cancellationToken,")
                    line ("                (command, token) => " + readResult + ", command => CopyOutputs(ReadOutputs(command), outputs));")
                    line ""
                    line ("        public static global::System.Threading.Tasks.Task<" + returnType + "> Execute" + suffix + "Async(" + signature + ", " + name + "Outputs" + nullable + " outputs = null, " + optional + ")")
                    line ("            => " + procedureRuntimeName + ".ExecuteAsync(CreateCommand(connection, arguments), transaction, commandTimeout, cancellationToken,")
                    line ("                (command, token) => " + readResultAsync + ", command => CopyOutputs(ReadOutputs(command), outputs));")
                executeMethods "" resultType read readAsync
                executeMethods "DataSet" dataSetType readDataSet readDataSetAsync
                if explicitRowsMethod.IsSome && row.Length > 0 then executeMethods "Rows" rowsType readRows readRowsAsync
            line "    }"
            if canBind then
                let usedParameters = HashSet<string>([ "outputs"; "cancellationToken"; "arguments" ], StringComparer.Ordinal)
                let inputs =
                    parameters
                    |> Array.choose (fun parameter ->
                        inputProperties |> Map.tryFind parameter.Ordinal
                        |> Option.map (fun property -> property, addName usedParameters (identifier (parameter.Column.Name.TrimStart('@')))))
                let typedParameters = inputs |> Array.map (fun (property, parameter) -> property.Type + " " + parameter) |> String.concat ", "
                let comma text = if String.IsNullOrEmpty text then "" else text + ", "
                let typedArguments =
                    "new " + name + "Arguments { " + (inputs |> Array.map (fun (property, parameter) -> property.Name + " = " + parameter) |> String.concat ", ") + " }"
                let connection, transaction, timeout =
                    "base.Database.Connection", procedureRuntimeName + ".GetEf6Transaction(base.Database)",
                    procedureRuntimeName + ".GetEf6CommandTimeout(base.Database)"
                line ""
                line ("    public partial class " + options.ContextName)
                line "    {"
                let contextMethods memberBase suffix returnType =
                    for withArguments in [ false; true ] do
                        let memberName = memberBase + (if withArguments then "WithArguments" else "")
                        let signature = if withArguments then name + "Arguments arguments" else typedParameters
                        let args = if withArguments then "arguments" else typedArguments
                        let invocation = name + "Procedure.Execute" + suffix + "(" + connection + ", " + args
                        let syncOptions = ", transaction: " + transaction + ", commandTimeout: " + timeout + ", cancellationToken: cancellationToken)"
                        line ("        public " + returnType + " " + memberName + "(" + comma signature + "global::System.Threading.CancellationToken cancellationToken = default)")
                        line ("            => " + invocation + syncOptions + ";")
                        line ""
                        line ("        public " + returnType + " " + memberName + "(" + comma signature + "out " + name + "Outputs outputs, global::System.Threading.CancellationToken cancellationToken = default)")
                        line ("            => " + invocation + ", out outputs" + syncOptions + ";")
                        line ""
                        line ("        public global::System.Threading.Tasks.Task<" + returnType + "> " + memberName + "Async(" + comma signature + "global::System.Threading.CancellationToken cancellationToken = default, " + name + "Outputs" + nullable + " outputs = null)")
                        line ("            => " + name + "Procedure.Execute" + suffix + "Async(" + connection + ", " + args + ", outputs, transaction: " + transaction + ", commandTimeout: " + timeout + ", cancellationToken: cancellationToken);")
                        line ""
                contextMethods methodName "" resultType
                contextMethods (methodName + "DataSet") "DataSet" dataSetType
                if row.Length > 0 then explicitRowsMethod |> Option.iter (fun memberName -> contextMethods memberName "Rows" rowsType)
                line "    }")

    let private isReservedFileName (name: string) =
        Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)

    let Generate (snapshot: PocoSchemaSnapshot) (options: PocoOptions) =
        if options.Target <> PocoTarget.Ef6 then
            invalidArg "options" "This standalone generator supports EF6 only."
        if snapshot.UsesAccessSyntax || not (sameText snapshot.ProviderId "sqlserver") then
            invalidArg "snapshot" "This standalone generator supports SQL Server only."
        let warnings = ResizeArray<string>(snapshot.Warnings)
        let known = snapshot.Objects |> Array.filter (fun source ->
            source.Item.Kind = SqlCatalogObjectKind.Table || source.Item.Kind = SqlCatalogObjectKind.View || source.Item.Kind = SqlCatalogObjectKind.Procedure)
        let named = known |> Array.map (fun source -> source, Names options source.Item)
        let types = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let addType name =
            if not (validName name) || isReservedFileName name || not (types.Add name) then
                invalidArg "options" ("Invalid or duplicate generated type/file name " + name + ". Set a distinct object name in the preview.")
        if String.IsNullOrWhiteSpace options.Namespace || not (options.Namespace.Split('.') |> Array.forall validName) then
            invalidArg "options" "Provide a valid C# namespace."
        addType options.ContextName
        if named |> Array.exists (fun (source, _) -> source.Item.Kind = SqlCatalogObjectKind.Procedure) then addType procedureRuntimeName
        let entities = ResizeArray<Entity>()
        let routineSources = ResizeArray<PocoObject * PocoObjectNames>()
        for source, names in named do
            let warn message = warnings.Add(objectName source.Item + ": " + message)
            for warning in source.Warnings do warn warning
            if source.Item.Kind = SqlCatalogObjectKind.Procedure then
                for suffix in [ "Arguments"; "Outputs"; "Row"; "Procedure" ] do addType (names.ClassName + suffix)
                routineSources.Add(source, names)
            elif source.Columns.Length > 0 then
                addType names.ClassName
                if not (validName names.SetName) then invalidArg "options" ("Invalid C# set name " + names.SetName + ".")
                let used = HashSet<string>([ names.ClassName ], StringComparer.Ordinal)
                let properties = properties options false names.Properties used source.Columns warn
                let key =
                    source.PrimaryKey
                    |> Array.choose (fun name -> properties |> Array.tryFind (fun property -> property.Column.Name = name))
                let keyKnown = key.Length = source.PrimaryKey.Length && key.Length > 0
                if not keyKnown then warn "A complete primary key was not available."
                let mapped = keyKnown && source.Item.Kind = SqlCatalogObjectKind.Table
                if not mapped && properties.Length > 0 then
                    warn "EF6 emits a read query for this object."
                if properties.Length > 0 then
                    entities.Add { Source = source; Names = names; Properties = properties; Key = (if keyKnown then key else [||]); Mapped = mapped; Used = used }
            else
                warn "No readable column metadata was available. No class was generated."
        let entities = entities.ToArray()
        let contextMembers = HashSet<string>([ options.ContextName; "OnModelCreating"; "OnModelCreatingPartial" ], StringComparer.Ordinal)
        contextMembers.Add(configuredConnectionName options.ContextName) |> ignore
        for entity in entities do
            let memberName = if entity.Mapped then entity.Names.SetName else "Query" + entity.Names.SetName
            if not (contextMembers.Add memberName) then invalidArg "options" ("Duplicate context member " + memberName + ". Set a distinct name in the preview.")
        for memberName in [ "Database"; "SaveChanges"; "SaveChangesAsync"; "Set"; "Entry"; "Dispose"; "DisposeAsync" ] do
            contextMembers.Add memberName |> ignore
        let reserveMethodFamily name suffixes =
            let rec chooseMethod index =
                let candidate = name + (if index = 0 then "" else string (index + 1))
                if suffixes |> List.exists (fun suffix -> contextMembers.Contains(candidate + suffix)) then chooseMethod (index + 1)
                else
                    for suffix in suffixes do contextMembers.Add(candidate + suffix) |> ignore
                    candidate
            chooseMethod 0
        let methodSuffixes = [ ""; "Async"; "WithArguments"; "WithArgumentsAsync" ]
        let existingSuffixes = methodSuffixes @ List.map ((+) "DataSet") methodSuffixes
        let namedRoutines =
            routineSources |> Seq.map (fun (source, names) -> source, names, reserveMethodFamily names.ClassName existingSuffixes)
            |> Seq.toArray
        let routines =
            namedRoutines |> Array.map (fun (source, names, methodName) ->
                // Reserve every existing entry point before allocating additive typed readers.
                let rowsMethod =
                    if needsExplicitRows snapshot source.RoutineResult then Some(reserveMethodFamily (methodName + "Rows") methodSuffixes)
                    else None
                routineFile snapshot options methodName rowsMethod names source (fun message -> warnings.Add(objectName source.Item + ": " + message)))
        let tables =
            entities
            |> Array.choose (fun entity ->
                let item = entity.Source.Item
                if item.Kind = SqlCatalogObjectKind.Table then Some((schemaName snapshot item, item.Name), entity) else None)
            |> Map.ofArray
        let findEntity (table: SqlRelationshipTable) =
            let lookup = { Kind = SqlCatalogObjectKind.Table; Schema = table.Schema; Name = table.Name; ParentName = ""; DataType = "" }
            tables |> Map.tryFind (schemaName snapshot lookup, table.Name)
        let details =
            snapshot.RelationshipDetails
            |> Array.map (fun value -> (value.Child.Schema, value.Child.Name, value.Name), value)
            |> Map.ofArray
        let endpointCounts =
            snapshot.Relationships
            |> Array.countBy (fun value -> value.Child.Schema, value.Child.Name, value.Parent.Schema, value.Parent.Name)
            |> Map.ofArray
        let relationships =
            snapshot.Relationships
            |> Array.choose (fun source ->
                let warn detail = warnings.Add("FK " + source.Name + ": " + detail)
                let detail = details |> Map.tryFind (source.Child.Schema, source.Child.Name, source.Name)
                match findEntity source.Child, findEntity source.Parent with
                | Some _, Some _ when detail |> Option.exists _.Disabled ->
                    warn "The constraint is disabled. The navigation was omitted."
                    None
                | Some child, Some parent ->
                    let columns = source.Columns |> Array.sortBy _.Ordinal
                    let parentPrimaryNames = parent.Key |> Array.map _.Column.Name
                    let referencedNames = columns |> Array.map _.ParentColumn
                    let sameColumns (left: string array) (right: string array) =
                        left.Length = right.Length && Set.ofArray left = Set.ofArray right
                    let columns =
                        if sameColumns parentPrimaryNames referencedNames then
                            parentPrimaryNames |> Array.map (fun name -> columns |> Array.find (fun column -> column.ParentColumn = name))
                        else columns
                    let choose (entity: Entity) getName =
                        columns |> Array.choose (fun column -> entity.Properties |> Array.tryFind (fun property -> property.Column.Name = getName column))
                    let childKey = choose child (fun column -> column.ChildColumn)
                    let parentKey = choose parent (fun column -> column.ParentColumn)
                    if columns.Length = 0 || childKey.Length <> columns.Length || parentKey.Length <> columns.Length then
                        warn "A key column was unavailable. The navigation was omitted."
                        None
                    elif not (Array.forall2 (fun childColumn parentColumn -> childColumn.BaseType = parentColumn.BaseType) childKey parentKey) then
                        warn "The paired foreign-key columns have incompatible CLR types. The navigation was omitted."
                        None
                    elif not child.Mapped || not parent.Mapped || parent.Key.Length = 0 || child.Key.Length = 0 then
                        warn "Both ends need mapped keyed tables. The navigation was omitted."
                        None
                    elif parentKey |> Array.exists (fun property -> property.Column.Nullable <> Some false) then
                        warn "The principal key contains a nullable or unknown-nullability column. EF key nullability would change the model; the navigation was omitted."
                        None
                    elif (parentKey |> Array.map _.Name) <> (parent.Key |> Array.map _.Name) then
                        warn "The relationship refers to an alternate key. The EF6 navigation was omitted."
                        None
                    else
                        let sameEntity = child.Names.ClassName = parent.Names.ClassName
                        let required = childKey |> Array.forall (fun property -> property.Column.Nullable = Some false)
                        let childColumns = childKey |> Array.map _.Column.Name
                        let childPrimaryNames = child.Key |> Array.map _.Column.Name
                        let sharedPrimaryKey = required && childColumns = childPrimaryNames
                        let childColumnSet = Set.ofArray childColumns
                        let unique =
                            Array.append [| child.Source.PrimaryKey |] child.Source.UniqueKeys
                            |> Array.exists (fun key -> key.Length > 0 && Set.isSubset (Set.ofArray key) childColumnSet)
                        let oneToOne = sharedPrimaryKey
                        if unique && not sharedPrimaryKey then
                            warn "EF6 represents this unique foreign key as a reference and collection; its 1:1 mapping requires a shared primary key in the principal key order."
                        let deleteAction = detail |> Option.map _.DeleteAction |> Option.defaultValue "NO_ACTION"
                        if detail |> Option.exists _.NotTrusted then warn "The constraint is not trusted; existing rows may violate it."
                        if detail |> Option.exists (fun value -> not (sameText value.UpdateAction "NO_ACTION")) then
                            warn "The database ON UPDATE action is retained in the database. EF does not model update actions."
                        if sameText deleteAction "SET_DEFAULT" then warn "SET DEFAULT remains database-only and is represented as NoAction in EF."
                        if sameText deleteAction "SET_NULL" then
                            warn "SET NULL remains database-only; EF6 cascade deletion is disabled for this relationship."
                        let parentName =
                            match childColumns with
                            | [| name |] when name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && name.Length > 2 ->
                                identifier (name.Substring(0, name.Length - 2))
                            | _ -> if sameEntity then "Parent" else parent.Names.ClassName
                        let toParent = addName child.Used parentName
                        let childrenName =
                            if oneToOne then
                                if sameEntity then "Child" else child.Names.ClassName
                            elif sameEntity then "Children" else child.Names.SetName
                        let roleCount = endpointCounts.[source.Child.Schema, source.Child.Name, source.Parent.Schema, source.Parent.Name]
                        let childrenName = if roleCount > 1 then childrenName + "Via" + toParent else childrenName
                        Some { Name = source.Name; Child = child; Parent = parent; ChildKey = childKey; ParentKey = parentKey
                               ToParent = toParent; ToChildren = addName parent.Used childrenName
                               OneToOne = oneToOne
                               Required = required; DeleteAction = deleteAction }
                | Some _, None | None, Some _ ->
                    warn ("Include " + source.Child.Name + " and " + source.Parent.Name + " to generate the relationship.")
                    None
                | _ -> None)
        let related =
            relationships
            |> Array.collect (fun relationship ->
                let child, parent = relationship.Child.Names.ClassName, relationship.Parent.Names.ClassName
                if child = parent then [| child, relationship |] else [| child, relationship; parent, relationship |])
            |> Array.groupBy fst
            |> Array.map (fun (name, entries) -> name, Array.map snd entries)
            |> Map.ofArray
        let files =
            [| for entity in entities do
                   let links = related |> Map.tryFind entity.Names.ClassName |> Option.defaultValue [||]
                   yield entityFile snapshot options links entity
               if entities.Length > 0 || routines.Length > 0 then yield contextFile snapshot options entities relationships
               if routines.Length > 0 then yield procedureRuntimeFile options
               yield! routines |]
        { Files = files; Warnings = warnings |> Seq.distinct |> Seq.toArray }
