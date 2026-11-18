namespace CommonClassLibrary

open System
open System.Data
open System.Data.Common
open System.Globalization
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient

/// SQL Server metadata adapter extracted from AnotherEdit PocoSchema and
/// SqlRelationshipMetadata. It never executes procedures or reads table rows.
module SqlServerSchema =
    let private maximumColumns = 1024
    let private maximumParameters = 256
    let private maximumObjects = 5000
    let private maximumRelationshipRows = 50000
    let private maximumRelationshipBytes = 16L * 1024L * 1024L
    let private maximumField = 2048

    let private text (value: obj) = Convert.ToString(value, CultureInfo.InvariantCulture)
    let private integer (value: obj) = Convert.ToInt32(value, CultureInfo.InvariantCulture)
    let private boolean (value: obj) = Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    let private valueAt convert (reader: DbDataReader) index =
        if reader.IsDBNull index then None else Some(convert (reader.GetValue index))
    let private textAt reader index = valueAt text reader index |> Option.defaultValue ""
    let private intAt reader index = valueAt integer reader index |> Option.defaultValue 0
    let private boolAt reader index = valueAt boolean reader index |> Option.defaultValue false

    let private safeName (name: string) allowEmpty =
        not (isNull name) && (allowEmpty || not (String.IsNullOrWhiteSpace name))
        && name.Length <= maximumField && not (name |> Seq.exists Char.IsControl)

    let private read (connection: SqlConnection) seconds (token: CancellationToken) sql maximum map =
        task {
            token.ThrowIfCancellationRequested()
            use command = connection.CreateCommand()
            command.CommandTimeout <- seconds
            command.CommandText <- sql
            // SqlClient owns cancellation for its asynchronous calls. The reader
            // drains or is disposed before its command and connection are released.
            use! reader = command.ExecuteReaderAsync(CommandBehavior.SingleResult, token)
            if isNull reader then invalidOp "The provider returned no metadata reader."
            let result = ResizeArray<_>()
            let mutable reading = true
            while reading do
                let! available = reader.ReadAsync token
                reading <- available
                if available then
                    token.ThrowIfCancellationRequested()
                    if result.Count = maximum then invalidOp "The selected object exceeds the retained metadata limit."
                    result.Add(map (reader :> DbDataReader))
            token.ThrowIfCancellationRequested()
            return result.ToArray()
        }

    // The native type mapping and facet rules are unchanged from PocoSchema.
    let private sqlServerType typeId =
        match typeId with
        | 34 | 165 | 173 | 189 -> typeof<byte[]>
        | 35 | 99 | 167 | 175 | 231 | 239 | 241 -> typeof<string>
        | 36 -> typeof<Guid>
        | 40 | 42 | 58 | 61 -> typeof<DateTime>
        | 41 -> typeof<TimeSpan>
        | 43 -> typeof<DateTimeOffset>
        | 48 -> typeof<byte>
        | 52 -> typeof<int16>
        | 56 -> typeof<int>
        | 59 -> typeof<single>
        | 62 -> typeof<float>
        | 60 | 106 | 108 | 122 -> typeof<decimal>
        | 104 -> typeof<bool>
        | 127 -> typeof<int64>
        | _ -> typeof<obj>

    let private sqlServerColumn name native typeId nullable rawSize precision scale identity computed =
        let size =
            match typeId with
            | 231 | 239 -> rawSize |> Option.map (fun value -> if value = -1 then -1 else value / 2)
            | 165 | 167 | 173 | 175 -> rawSize
            | _ -> None
        let numericPrecision, numericScale =
            match typeId with
            | 106 | 108 -> precision, scale
            | 41 | 42 | 43 -> None, scale
            | _ -> None, None
        { Name = name; ClrType = sqlServerType typeId; StoreType = native
          Nullable = nullable; Size = size; Precision = numericPrecision; Scale = numericScale
          AutoNumber = identity; Computed = computed }

    let private literal (value: string) = "N'" + value.Replace("'", "''") + "'"

    let private sqlServerScope (item: SqlCatalogObject) =
        "s.name=" + literal item.Schema + " AND o.name=" + literal item.Name

    let private sqlServerTable connection seconds token (item: SqlCatalogObject) =
        task {
            let scope = sqlServerScope item
            let columnsSql = $"""
SELECT c.name, TYPE_NAME(c.user_type_id), c.system_type_id, c.is_nullable,
       c.max_length, c.precision, c.scale, c.is_identity,
       CASE WHEN c.is_computed=1 OR c.generated_always_type<>0 OR c.system_type_id=189 THEN 1 ELSE 0 END
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.columns c ON c.object_id=o.object_id
WHERE {scope} AND o.type IN ('U','V') AND c.is_hidden=0
ORDER BY c.column_id"""
            let! columns =
                read connection seconds token columnsSql maximumColumns (fun row ->
                    sqlServerColumn (textAt row 0) (textAt row 1) (intAt row 2) (valueAt boolean row 3)
                        (valueAt integer row 4) (valueAt integer row 5) (valueAt integer row 6) (boolAt row 7) (boolAt row 8))
            let! keys =
                if item.Kind <> SqlCatalogObjectKind.Table then Task.FromResult [||]
                else
                    let keysSql = $"""
SELECT c.name
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.indexes i ON i.object_id=o.object_id AND i.is_primary_key=1
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE {scope} AND o.type='U'
ORDER BY ic.key_ordinal"""
                    read connection seconds token keysSql maximumColumns (fun row -> textAt row 0)
            let! uniqueKeys =
                if item.Kind <> SqlCatalogObjectKind.Table then Task.FromResult [||]
                else
                    task {
                        let sql = $"""
SELECT i.index_id,c.name
FROM sys.tables o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.indexes i ON i.object_id=o.object_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE {scope} AND i.is_unique=1 AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
ORDER BY i.index_id,ic.key_ordinal"""
                        let! pairs =
                            read connection seconds token sql (maximumColumns * 32) (fun row -> intAt row 0, textAt row 1)
                        return pairs |> Array.groupBy fst |> Array.map (snd >> Array.map snd)
                    }
            if columns.Length = 0 then invalidOp "No columns were visible for the selected object."
            let columnNames = HashSet<string>((columns |> Array.map _.Name), StringComparer.Ordinal)
            if columnNames.Count <> columns.Length then invalidOp "SQL Server returned duplicate column metadata."
            if keys |> Array.exists (columnNames.Contains >> not) then
                invalidOp "The reported primary key did not match the column metadata."
            if uniqueKeys |> Array.exists (Array.exists (columnNames.Contains >> not)) then
                invalidOp "The reported unique key did not match the column metadata."
            return columns, keys, uniqueKeys
        }

    let private sqlServerRelationshipDetails connection seconds token (selected: SqlCatalogObject array) =
        let tables = selected |> Array.filter (fun item -> item.Kind = SqlCatalogObjectKind.Table)
        if tables.Length = 0 then Task.FromResult [||]
        else
            let scopes = tables |> Array.map (fun item -> "(" + sqlServerScope item + ")") |> String.concat " OR "
            let sql = $"""
SELECT fk.name,DB_NAME(),s.name,o.name,fk.delete_referential_action_desc,fk.update_referential_action_desc,
       fk.is_disabled,fk.is_not_trusted
FROM sys.foreign_keys fk
JOIN sys.tables o ON o.object_id=fk.parent_object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE {scopes}
ORDER BY fk.object_id"""
            read connection seconds token sql maximumRelationshipRows (fun row ->
                { Name = textAt row 0
                  Child = { Catalog = textAt row 1; Schema = textAt row 2; Name = textAt row 3 }
                  DeleteAction = textAt row 4; UpdateAction = textAt row 5
                  Disabled = boolAt row 6; NotTrusted = boolAt row 7 })

    type private RelationshipRow =
        { Key: string
          Child: SqlRelationshipTable
          Parent: SqlRelationshipTable
          ChildColumn: string
          ParentColumn: string
          Ordinal: int }

    // Keep disabled and untrusted constraints, just as AnotherEdit does. Their
    // actions and state are read separately above for generator policy decisions.
    let private relationshipsSql = """
SELECT fk.name,DB_NAME(),cs.name,ct.name,DB_NAME(),ps.name,pt.name,
       cc.name,pc.name,fc.constraint_column_id
FROM sys.foreign_keys AS fk
LEFT JOIN sys.tables AS ct ON ct.object_id=fk.parent_object_id
LEFT JOIN sys.schemas AS cs ON cs.schema_id=ct.schema_id
LEFT JOIN sys.tables AS pt ON pt.object_id=fk.referenced_object_id
LEFT JOIN sys.schemas AS ps ON ps.schema_id=pt.schema_id
LEFT JOIN sys.foreign_key_columns AS fc ON fc.constraint_object_id=fk.object_id
LEFT JOIN sys.columns AS cc ON cc.object_id=fc.parent_object_id AND cc.column_id=fc.parent_column_id
LEFT JOIN sys.columns AS pc ON pc.object_id=fc.referenced_object_id AND pc.column_id=fc.referenced_column_id
ORDER BY fk.object_id,fc.constraint_column_id"""

    let private sqlServerRelationships connection seconds token (selected: SqlCatalogObject array) =
        task {
            let mutable retainedBytes = 0L
            let! rows =
                read connection seconds token relationshipsSql maximumRelationshipRows (fun reader ->
                    let fields = Array.init 10 (fun index -> textAt reader index)
                    if fields |> Array.exists (fun value -> value.Length > maximumField || value.IndexOf('\000') >= 0) then
                        invalidOp "Relationship metadata contains an oversized or invalid identifier. No JOIN was generated."
                    retainedBytes <- retainedBytes + 192L + 2L * (fields |> Array.sumBy (fun value -> int64 value.Length))
                    if retainedBytes > maximumRelationshipBytes then
                        invalidOp "Relationship metadata exceeds the 50,000-row / 16 MiB limit. No partial foreign key was used."
                    { Key = fields.[0]
                      Child = { Catalog = fields.[1]; Schema = fields.[2]; Name = fields.[3] }
                      Parent = { Catalog = fields.[4]; Schema = fields.[5]; Name = fields.[6] }
                      ChildColumn = fields.[7]; ParentColumn = fields.[8]; Ordinal = intAt reader 9 })
            let tableObjects = HashSet<_>(selected |> Array.map (fun item -> item.Kind, item.Schema, item.Name))
            let relationships = ResizeArray<SqlForeignKeyRelationship>()
            for (_, child, parent), values in rows |> Array.groupBy (fun row -> row.Key, row.Child, row.Parent) do
                let columns = values |> Array.distinct |> Array.sortBy _.Ordinal
                let name = columns.[0].Key
                let validNames =
                    name <> "" && child.Name <> "" && parent.Name <> ""
                    && (columns |> Array.forall (fun row -> row.ChildColumn <> "" && row.ParentColumn <> ""))
                let ordered = columns |> Array.mapi (fun index row -> row.Ordinal = index + 1) |> Array.forall id
                let orderKnown = ordered || (columns.Length = 1 && columns.[0].Ordinal = 0)
                let uniqueColumns =
                    (columns |> Array.map _.ChildColumn |> Array.distinct |> Array.length) = columns.Length
                    && (columns |> Array.map _.ParentColumn |> Array.distinct |> Array.length) = columns.Length
                // A console export has no preview acknowledgement step: required
                // catalog failures abort instead of returning a partial model.
                if not (validNames && orderKnown && uniqueColumns) then
                    invalidOp "A relationship with missing identities, column pairs or composite-key order was omitted; no columns were guessed."
                if tableObjects.Contains(SqlCatalogObjectKind.Table, child.Schema, child.Name)
                   || tableObjects.Contains(SqlCatalogObjectKind.Table, parent.Schema, parent.Name) then
                    relationships.Add
                        { Name = name; Child = child; Parent = parent
                          Columns = columns |> Array.mapi (fun index row ->
                              { ChildColumn = row.ChildColumn; ParentColumn = row.ParentColumn; Ordinal = index + 1 }) }
            return
                relationships
                |> Seq.sortBy (fun value -> value.Child.Schema, value.Child.Name, value.Name, value.Parent.Schema, value.Parent.Name)
                |> Seq.toArray
        }

    let private sqlServerRoutine connection seconds token (item: SqlCatalogObject) =
        task {
            let scope = sqlServerScope item
            let verified = $"""
SELECT o.object_id FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE {scope} AND o.type IN ('P','PC')"""
            let! ids = read connection seconds token verified 1 (fun row -> intAt row 0)
            if ids.Length <> 1 || ids.[0] = 0 then invalidOp "The procedure was not visible in the SQL Server catalog."
            let id = ids.[0].ToString(CultureInfo.InvariantCulture)
            let parameterSql = $"""
SELECT p.name, COALESCE(TYPE_NAME(p.system_type_id), TYPE_NAME(p.user_type_id)), p.system_type_id, p.is_nullable,
       p.max_length, p.precision, p.scale, p.is_output, p.parameter_id, p.is_readonly
FROM sys.parameters p
WHERE p.object_id={id} AND p.parameter_id>0
ORDER BY p.parameter_id"""
            let! parameters =
                read connection seconds token parameterSql maximumParameters (fun row ->
                    { Column = sqlServerColumn (textAt row 0) (textAt row 1) (if boolAt row 9 then 0 else intAt row 2)
                                   (valueAt boolean row 3) (valueAt integer row 4) (valueAt integer row 5) (valueAt integer row 6) false false
                      Direction = if boolAt row 7 then ParameterDirection.InputOutput else ParameterDirection.Input
                      Ordinal = intAt row 8 })
            let resultSql = $"""
SELECT name, TYPE_NAME(system_type_id), system_type_id, is_nullable,
       max_length, precision, scale, error_number, error_type_desc
FROM sys.dm_exec_describe_first_result_set_for_object({id},0)
ORDER BY column_ordinal"""
            let! result =
                task {
                    try
                        let! discovered =
                            read connection seconds token resultSql maximumColumns (fun row ->
                                if not (row.IsDBNull 7) then
                                    let reason = textAt row 8
                                    Error (if reason = "" then "SQL Server could not describe the first result set." else reason)
                                else
                                    Ok (sqlServerColumn (textAt row 0) (textAt row 1) (intAt row 2) (valueAt boolean row 3)
                                            (valueAt integer row 4) (valueAt integer row 5) (valueAt integer row 6) false false))
                        return
                            match discovered |> Array.tryPick (function Error reason -> Some reason | Ok _ -> None) with
                            | Some reason -> PocoRoutineResult.Unknown reason
                            | None when discovered.Length = 0 -> PocoRoutineResult.NoRows
                            | None ->
                                // Browse mode zero retains visible columns in ordinal
                                // order, including unnamed and repeated SQL aliases.
                                discovered |> Array.choose (function Ok column -> Some column | Error _ -> None) |> PocoRoutineResult.Rows
                    with
                    | :? SqlException ->
                        token.ThrowIfCancellationRequested()
                        return PocoRoutineResult.Unknown "SQL Server did not expose first-result metadata."
                }
            return parameters, result
        }

    let private describeColumn allowsSqlVariant (column: PocoColumn) =
        let sqlVariant = allowsSqlVariant && column.StoreType.Equals("sql_variant", StringComparison.OrdinalIgnoreCase)
        [| if column.ClrType = typeof<obj> && not sqlVariant then
               yield column.Name + ": the provider type has no verified CLR mapping (" + column.StoreType + ")."
           if column.Nullable.IsNone then
               yield column.Name + ": nullability was not reported."
           if column.ClrType = typeof<decimal> && (column.Precision |> Option.exists (fun value -> value > 28)) then
               yield column.Name + ": database precision exceeds the exact range of .NET decimal." |]

    let private settings connectionString seconds =
        let builder = SqlConnectionStringBuilder(connectionString)
        if String.IsNullOrWhiteSpace builder.DataSource || String.IsNullOrWhiteSpace builder.InitialCatalog then
            invalidArg "connectionString" "Specify the intended SQL Server and database."
        if not (String.IsNullOrWhiteSpace builder.AttachDBFilename) || builder.UserInstance then
            invalidArg "connectionString" "Database diagnostics does not attach files or create SQL Server user instances."
        builder.ConnectTimeout <- max 1 (min (min 30 seconds) builder.ConnectTimeout)
        builder.Pooling <- false
        builder.Enlist <- false
        builder.PersistSecurityInfo <- false
        builder.ApplicationName <- "SqlModelGenerator metadata"
        builder.ConnectionString

    let private selectedObjects connection seconds token schemaFilter includeTables includeViews includeProcedures =
        let kinds =
            [| if includeTables then yield "'U'"
               if includeViews then yield "'V'"
               if includeProcedures then yield "'P','PC'" |]
            |> String.concat ","
        let schemaScope = if String.IsNullOrEmpty schemaFilter then "" else " AND s.name=" + literal schemaFilter
        let sql = $"""
SELECT TOP ({maximumObjects + 1}) o.type,s.name,o.name,
       CASE WHEN o.type='U' AND t.temporal_type=1 THEN 'temporal history' ELSE '' END
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id=o.schema_id
LEFT JOIN sys.tables AS t ON t.object_id=o.object_id
WHERE o.type IN ({kinds}) AND o.is_ms_shipped=0 AND s.name NOT IN ('sys','INFORMATION_SCHEMA'){schemaScope}
ORDER BY CASE WHEN o.type='V' THEN 'view' WHEN o.type='U' THEN 'table' ELSE 'procedure' END,s.name,o.name"""
        read connection seconds token sql maximumObjects (fun row ->
            let kind =
                match (textAt row 0).Trim() with
                | "U" -> SqlCatalogObjectKind.Table
                | "V" -> SqlCatalogObjectKind.View
                | "P" | "PC" -> SqlCatalogObjectKind.Procedure
                | _ -> invalidOp "SQL Server returned an unsupported catalog object."
            let item = { Kind = kind; Schema = textAt row 1; Name = textAt row 2; ParentName = ""; DataType = textAt row 3 }
            if not (safeName item.Name false && safeName item.Schema true) then
                invalidOp "Only named tables, views and procedures from the loaded catalog can be inspected."
            item)

    /// Reads one owned connection using fixed SELECT metadata commands only.
    /// Empty schemaFilter selects every visible user schema; other values match
    /// one exact SQL Server schema name under the database collation.
    let LoadAsync(connectionString: string, timeoutSeconds: int, schemaFilter: string,
                  includeTables: bool, includeViews: bool, includeProcedures: bool,
                  token: CancellationToken) : Task<PocoSchemaSnapshot> =
        task {
            if not (includeTables || includeViews || includeProcedures) then
                invalidArg "includeTables" "Choose at least one of tables, views or procedures."
            let schema = if isNull schemaFilter then "" else schemaFilter
            if not (safeName schema true) then invalidArg "schemaFilter" "The schema name is invalid."
            let seconds = max 1 (min 3600 timeoutSeconds)
            token.ThrowIfCancellationRequested()
            use connection = new SqlConnection(settings connectionString seconds)
            do! connection.OpenAsync token
            let! selected = selectedObjects connection seconds token schema includeTables includeViews includeProcedures
            if selected.Length = 0 then invalidOp "No visible user tables, views or procedures matched the selection."
            let! relationshipDetails = sqlServerRelationshipDetails connection seconds token selected
            let! relationships =
                if selected |> Array.exists (fun item -> item.Kind = SqlCatalogObjectKind.Table) then
                    sqlServerRelationships connection seconds token selected
                else Task.FromResult [||]
            let objects = ResizeArray<PocoObject>()
            for item in selected |> Array.distinct do
                token.ThrowIfCancellationRequested()
                let empty =
                    { Item = item; Columns = [||]; PrimaryKey = [||]; UniqueKeys = [||]; Parameters = [||]
                      ParametersKnown = false; RoutineResult = PocoRoutineResult.NoRows; Warnings = [||] }
                if item.Kind = SqlCatalogObjectKind.Procedure then
                    let! parameters, result = sqlServerRoutine connection seconds token item
                    let routineWarnings =
                        [| match result with
                           | PocoRoutineResult.Rows columns ->
                               yield "Only the first result set can be described."
                               yield! columns |> Array.collect (describeColumn true)
                           | PocoRoutineResult.Unknown reason -> yield "Result shape unavailable: " + reason
                           | _ -> ()
                           for parameter in parameters do yield! describeColumn true parameter.Column |]
                    objects.Add { empty with Parameters = parameters; ParametersKnown = true; RoutineResult = result; Warnings = routineWarnings }
                else
                    let! columns, keys, uniqueKeys = sqlServerTable connection seconds token item
                    objects.Add
                        { empty with Columns = columns; PrimaryKey = keys; UniqueKeys = uniqueKeys
                                     Warnings = columns |> Array.collect (describeColumn false) }
            token.ThrowIfCancellationRequested()
            return
                { ProviderId = "sqlserver"; UsesAccessSyntax = false; Objects = objects.ToArray()
                  Relationships = relationships; RelationshipDetails = relationshipDetails
                  Warnings = if selected |> Array.exists (fun item -> item.Kind = SqlCatalogObjectKind.Table) then
                                 [| SqlMetadataNotice.RelationshipVisibility |]
                             else [||] }
        }
