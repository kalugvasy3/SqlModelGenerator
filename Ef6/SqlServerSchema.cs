using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace CommonClassLibrary;

// Reads schema only, including every declared FK. Never executes a stored procedure
// or reads table data. Each command/reader/connection is owned and disposed here.
public static class SqlServerSchema
{
    private const int MaximumColumns = 1024, MaximumParameters = 256, MaximumObjects = 5000;
    private const int MaximumRelationshipRows = 50000, MaximumField = 2048;
    private const long MaximumRelationshipBytes = 16L * 1024 * 1024;
    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    private static int Integer(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static bool Boolean(object value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static T? ValueAt<T>(DbDataReader reader, int index, Func<object, T> convert) where T : struct => reader.IsDBNull(index) ? null : convert(reader.GetValue(index));
    private static string TextAt(DbDataReader reader, int index) => reader.IsDBNull(index) ? "" : Text(reader.GetValue(index));
    private static int IntAt(DbDataReader reader, int index) => ValueAt(reader, index, Integer) ?? 0;
    private static bool BoolAt(DbDataReader reader, int index) => ValueAt(reader, index, Boolean) ?? false;
    private static bool SafeName(string? name, bool allowEmpty) => name != null && (allowEmpty || !string.IsNullOrWhiteSpace(name)) && name.Length <= MaximumField && !name.Any(char.IsControl);

    private static async Task<T[]> Read<T>(SqlConnection connection, int seconds, CancellationToken token, string sql, int maximum, Func<DbDataReader,T> map)
    {
        token.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandTimeout = seconds;
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleResult, token).ConfigureAwait(false);
        if (reader == null) throw new InvalidOperationException("The provider returned no metadata reader.");
        var result = new List<T>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            if (result.Count == maximum) throw new InvalidOperationException("The selected object exceeds the retained metadata limit.");
            result.Add(map(reader));
        }
        token.ThrowIfCancellationRequested();
        return result.ToArray();
    }

    private static Type SqlServerType(int typeId) => typeId switch
    {
        34 or 165 or 173 or 189 => typeof(byte[]),
        35 or 99 or 167 or 175 or 231 or 239 or 241 => typeof(string),
        36 => typeof(Guid), 40 or 42 or 58 or 61 => typeof(DateTime),
        41 => typeof(TimeSpan), 43 => typeof(DateTimeOffset), 48 => typeof(byte),
        52 => typeof(short), 56 => typeof(int), 59 => typeof(float), 62 => typeof(double),
        60 or 106 or 108 or 122 => typeof(decimal), 104 => typeof(bool), 127 => typeof(long), _ => typeof(object)
    };
    private static PocoColumn SqlServerColumn(string name, string native, int typeId, bool? nullable, int? rawSize, int? precision, int? scale, bool identity, bool computed)
    {
        int? size = typeId switch { 231 or 239 => rawSize is null ? null : rawSize == -1 ? -1 : rawSize / 2, 165 or 167 or 173 or 175 => rawSize, _ => null };
        return new(name, SqlServerType(typeId), native, nullable, size, typeId is 106 or 108 ? precision : null,
            typeId is 106 or 108 or 41 or 42 or 43 ? scale : null, identity, computed);
    }
    private static string Literal(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Scope(SqlCatalogObject item) => "s.name=" + Literal(item.Schema) + " AND o.name=" + Literal(item.Name);
    private static PocoColumn ReadColumn(DbDataReader row) => SqlServerColumn(TextAt(row,0), TextAt(row,1), IntAt(row,2), ValueAt(row,3,Boolean), ValueAt(row,4,Integer), ValueAt(row,5,Integer), ValueAt(row,6,Integer), BoolAt(row,7), BoolAt(row,8));

    private static async Task<(PocoColumn[] Columns, string[] Keys, string[][] UniqueKeys)> Table(SqlConnection connection, int seconds, CancellationToken token, SqlCatalogObject item)
    {
        var scope = Scope(item);
        var columns = await Read(connection,seconds,token,SqlServerQueries.Columns.Replace("{scope}",scope,StringComparison.Ordinal),MaximumColumns,ReadColumn).ConfigureAwait(false);
        var keys = item.Kind != SqlCatalogObjectKind.Table ? [] : await Read(connection,seconds,token,SqlServerQueries.Keys.Replace("{scope}",scope,StringComparison.Ordinal),MaximumColumns,row => TextAt(row,0)).ConfigureAwait(false);
        var pairs = item.Kind != SqlCatalogObjectKind.Table ? [] : await Read(connection,seconds,token,SqlServerQueries.UniqueKeys.Replace("{scope}",scope,StringComparison.Ordinal),MaximumColumns*32,row => (Id:IntAt(row,0), Name:TextAt(row,1))).ConfigureAwait(false);
        var uniqueKeys = pairs.GroupBy(p => p.Id).Select(g => g.Select(p => p.Name).ToArray()).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("No columns were visible for the selected object.");
        var columnNames = new HashSet<string>(columns.Select(c => c.Name),StringComparer.Ordinal);
        if (columnNames.Count != columns.Length) throw new InvalidOperationException("SQL Server returned duplicate column metadata.");
        if (keys.Any(k => !columnNames.Contains(k))) throw new InvalidOperationException("The reported primary key did not match the column metadata.");
        if (uniqueKeys.Any(k => k.Any(c => !columnNames.Contains(c)))) throw new InvalidOperationException("The reported unique key did not match the column metadata.");
        return (columns,keys,uniqueKeys);
    }

    private static Task<PocoRelationshipDetails[]> RelationshipDetails(SqlConnection connection,int seconds,CancellationToken token,SqlCatalogObject[] selected)
    {
        var tables = selected.Where(i => i.Kind == SqlCatalogObjectKind.Table).ToArray();
        if (tables.Length == 0) return Task.FromResult(Array.Empty<PocoRelationshipDetails>());
        var scopes = string.Join(" OR ",tables.Select(i => "("+Scope(i)+")"));
        return Read(connection,seconds,token,SqlServerQueries.RelationshipDetails.Replace("{scopes}",scopes,StringComparison.Ordinal),MaximumRelationshipRows,
            row => new PocoRelationshipDetails(TextAt(row,0),new(TextAt(row,1),TextAt(row,2),TextAt(row,3)),TextAt(row,4),TextAt(row,5),BoolAt(row,6),BoolAt(row,7)));
    }
    private sealed record RelationshipRow(string Key,SqlRelationshipTable Child,SqlRelationshipTable Parent,string ChildColumn,string ParentColumn,int Ordinal);
    private static async Task<SqlForeignKeyRelationship[]> Relationships(SqlConnection connection,int seconds,CancellationToken token,SqlCatalogObject[] selected)
    {
        long retainedBytes = 0;
        var rows = await Read(connection,seconds,token,SqlServerQueries.Relationships,MaximumRelationshipRows,reader =>
        {
            var fields = Enumerable.Range(0,10).Select(i => TextAt(reader,i)).ToArray();
            if (fields.Any(v => v.Length > MaximumField || v.IndexOf('\0') >= 0)) throw new InvalidOperationException("Relationship metadata contains an oversized or invalid identifier. No JOIN was generated.");
            retainedBytes += 192L + 2L * fields.Sum(v => (long)v.Length);
            if (retainedBytes > MaximumRelationshipBytes) throw new InvalidOperationException("Relationship metadata exceeds the 50,000-row / 16 MiB limit. No partial foreign key was used.");
            return new RelationshipRow(fields[0],new(fields[1],fields[2],fields[3]),new(fields[4],fields[5],fields[6]),fields[7],fields[8],IntAt(reader,9));
        }).ConfigureAwait(false);
        var tableObjects = selected.Select(i => (i.Kind,i.Schema,i.Name)).ToHashSet();
        var relationships = new List<SqlForeignKeyRelationship>();
        foreach (var group in rows.GroupBy(r => (r.Key,r.Child,r.Parent)))
        {
            var columns = group.Distinct().OrderBy(r => r.Ordinal).ToArray();
            var (name,child,parent) = group.Key;
            bool validNames = name != "" && child.Name != "" && parent.Name != "" && columns.All(r => r.ChildColumn != "" && r.ParentColumn != "");
            bool orderKnown = columns.Select((r,i) => r.Ordinal == i+1).All(x => x) || (columns.Length == 1 && columns[0].Ordinal == 0);
            bool uniqueColumns = columns.Select(r => r.ChildColumn).Distinct(StringComparer.Ordinal).Count() == columns.Length && columns.Select(r => r.ParentColumn).Distinct(StringComparer.Ordinal).Count() == columns.Length;
            if (!(validNames && orderKnown && uniqueColumns)) throw new InvalidOperationException("A relationship with missing identities, column pairs or composite-key order was omitted; no columns were guessed.");
            if (tableObjects.Contains((SqlCatalogObjectKind.Table,child.Schema,child.Name)) || tableObjects.Contains((SqlCatalogObjectKind.Table,parent.Schema,parent.Name)))
                relationships.Add(new(name,child,parent,columns.Select((r,i) => new SqlRelationshipColumn(r.ChildColumn,r.ParentColumn,i+1)).ToArray()));
        }
        return relationships.OrderBy(r => r.Child.Schema,StringComparer.Ordinal).ThenBy(r => r.Child.Name,StringComparer.Ordinal).ThenBy(r => r.Name,StringComparer.Ordinal).ThenBy(r => r.Parent.Schema,StringComparer.Ordinal).ThenBy(r => r.Parent.Name,StringComparer.Ordinal).ToArray();
    }

    private static async Task<(PocoParameter[] Parameters,PocoRoutineResult Result)> Routine(SqlConnection connection,int seconds,CancellationToken token,SqlCatalogObject item)
    {
        var ids = await Read(connection,seconds,token,SqlServerQueries.VerifyRoutine.Replace("{scope}",Scope(item),StringComparison.Ordinal),1,row => IntAt(row,0)).ConfigureAwait(false);
        if (ids.Length != 1 || ids[0] == 0) throw new InvalidOperationException("The procedure was not visible in the SQL Server catalog.");
        var id = ids[0].ToString(CultureInfo.InvariantCulture);
        var parameters = await Read(connection,seconds,token,SqlServerQueries.Parameters.Replace("{id}",id,StringComparison.Ordinal),MaximumParameters,row =>
            new PocoParameter(SqlServerColumn(TextAt(row,0),TextAt(row,1),BoolAt(row,9)?0:IntAt(row,2),ValueAt(row,3,Boolean),ValueAt(row,4,Integer),ValueAt(row,5,Integer),ValueAt(row,6,Integer),false,false),
                BoolAt(row,7)?ParameterDirection.InputOutput:ParameterDirection.Input,IntAt(row,8))).ConfigureAwait(false);
        PocoRoutineResult result;
        try
        {
            var discovered = await Read(connection,seconds,token,SqlServerQueries.Result.Replace("{id}",id,StringComparison.Ordinal),MaximumColumns,row =>
            {
                if (!row.IsDBNull(7)) { var reason = TextAt(row,8); return (Column:(PocoColumn?)null,Error:(string?)(reason==""?"SQL Server could not describe the first result set.":reason)); }
                return (Column:(PocoColumn?)SqlServerColumn(TextAt(row,0),TextAt(row,1),IntAt(row,2),ValueAt(row,3,Boolean),ValueAt(row,4,Integer),ValueAt(row,5,Integer),ValueAt(row,6,Integer),false,false),Error:(string?)null);
            }).ConfigureAwait(false);
            var reason = discovered.Select(r => r.Error).FirstOrDefault(r => r != null);
            result = reason != null ? PocoRoutineResult.NewUnknown(reason) : discovered.Length == 0 ? PocoRoutineResult.NoRows : PocoRoutineResult.NewRows(discovered.Select(r => r.Column!).ToArray());
        }
        catch (SqlException) { token.ThrowIfCancellationRequested(); result = PocoRoutineResult.NewUnknown("SQL Server did not expose first-result metadata."); }
        return (parameters,result);
    }
    private static IEnumerable<string> DescribeColumn(bool allowsSqlVariant,PocoColumn column)
    {
        var sqlVariant = allowsSqlVariant && column.StoreType.Equals("sql_variant",StringComparison.OrdinalIgnoreCase);
        if (column.ClrType == typeof(object) && !sqlVariant) yield return column.Name + ": the provider type has no verified CLR mapping (" + column.StoreType + ").";
        if (column.Nullable == null) yield return column.Name + ": nullability was not reported.";
        if (column.ClrType == typeof(decimal) && column.Precision > 28) yield return column.Name + ": database precision exceeds the exact range of .NET decimal.";
    }
    private static string Settings(string connectionString,int seconds)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || string.IsNullOrWhiteSpace(builder.InitialCatalog)) throw new ArgumentException("Specify the intended SQL Server and database.",nameof(connectionString));
        if (!string.IsNullOrWhiteSpace(builder.AttachDBFilename) || builder.UserInstance) throw new ArgumentException("Database diagnostics does not attach files or create SQL Server user instances.",nameof(connectionString));
        builder.ConnectTimeout = Math.Max(1,Math.Min(Math.Min(30,seconds),builder.ConnectTimeout));
        builder.Pooling=false; builder.Enlist=false; builder.PersistSecurityInfo=false; builder.ApplicationName="SqlModelGenerator metadata";
        return builder.ConnectionString;
    }
    private static Task<SqlCatalogObject[]> SelectedObjects(SqlConnection connection,int seconds,CancellationToken token,string schemaFilter,bool includeTables,bool includeViews,bool includeProcedures)
    {
        var kinds = new List<string>();
        if(includeTables) kinds.Add("'U'"); if(includeViews) kinds.Add("'V'"); if(includeProcedures) kinds.Add("'P','PC'");
        var schemaScope = string.IsNullOrEmpty(schemaFilter)?"":" AND s.name="+Literal(schemaFilter);
        var sql = SqlServerQueries.Selected.Replace("{maximumObjects + 1}",(MaximumObjects+1).ToString(CultureInfo.InvariantCulture),StringComparison.Ordinal).Replace("{kinds}",string.Join(",",kinds),StringComparison.Ordinal).Replace("{schemaScope}",schemaScope,StringComparison.Ordinal);
        return Read(connection,seconds,token,sql,MaximumObjects,row =>
        {
            var kind = TextAt(row,0).Trim() switch { "U"=>SqlCatalogObjectKind.Table,"V"=>SqlCatalogObjectKind.View,"P" or "PC"=>SqlCatalogObjectKind.Procedure,_=>throw new InvalidOperationException("SQL Server returned an unsupported catalog object.") };
            var item = new SqlCatalogObject(kind,TextAt(row,1),TextAt(row,2),"",TextAt(row,3));
            if(!SafeName(item.Name,false) || !SafeName(item.Schema,true)) throw new InvalidOperationException("Only named tables, views and procedures from the loaded catalog can be inspected.");
            return item;
        });
    }

    public static async Task<PocoSchemaSnapshot> LoadAsync(string connectionString,int timeoutSeconds,string? schemaFilter,bool includeTables,bool includeViews,bool includeProcedures,CancellationToken token)
    {
        if(!(includeTables||includeViews||includeProcedures)) throw new ArgumentException("Choose at least one of tables, views or procedures.",nameof(includeTables));
        var schema = schemaFilter??"";
        if(!SafeName(schema,true)) throw new ArgumentException("The schema name is invalid.",nameof(schemaFilter));
        var seconds = Math.Clamp(timeoutSeconds,1,3600);
        token.ThrowIfCancellationRequested();
        using var connection = new SqlConnection(Settings(connectionString,seconds));
        await connection.OpenAsync(token).ConfigureAwait(false);
        var selected = await SelectedObjects(connection,seconds,token,schema,includeTables,includeViews,includeProcedures).ConfigureAwait(false);
        if(selected.Length==0) throw new InvalidOperationException("No visible user tables, views or procedures matched the selection.");
        var details = await RelationshipDetails(connection,seconds,token,selected).ConfigureAwait(false);
        var hasTables = selected.Any(i=>i.Kind==SqlCatalogObjectKind.Table);
        var relationships = hasTables ? await Relationships(connection,seconds,token,selected).ConfigureAwait(false) : [];
        var objects = new List<PocoObject>();
        foreach(var item in selected.Distinct())
        {
            token.ThrowIfCancellationRequested();
            if(item.Kind==SqlCatalogObjectKind.Procedure)
            {
                var (parameters,result) = await Routine(connection,seconds,token,item).ConfigureAwait(false);
                var warnings = new List<string>();
                if(result.IsRows) { warnings.Add("Only the first result set can be described."); warnings.AddRange(result.Columns.SelectMany(c=>DescribeColumn(true,c))); }
                else if(result.IsUnknown) warnings.Add("Result shape unavailable: "+result.Message);
                warnings.AddRange(parameters.SelectMany(p=>DescribeColumn(true,p.Column)));
                objects.Add(new(item,[],[],[],parameters,true,result,warnings.ToArray()));
            }
            else
            {
                var (columns,keys,uniqueKeys) = await Table(connection,seconds,token,item).ConfigureAwait(false);
                objects.Add(new(item,columns,keys,uniqueKeys,[],false,PocoRoutineResult.NoRows,columns.SelectMany(c=>DescribeColumn(false,c)).ToArray()));
            }
        }
        token.ThrowIfCancellationRequested();
        return new("sqlserver",false,objects.ToArray(),relationships,details,hasTables?[SqlMetadataNotice.RelationshipVisibility]:[]);
    }
}
