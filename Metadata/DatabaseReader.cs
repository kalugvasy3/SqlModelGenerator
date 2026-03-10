using Microsoft.Data.SqlClient;
using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Metadata;

public sealed class DatabaseReader
{
    private readonly string _connectionString;

    public DatabaseReader(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<DatabaseModel> ReadAsync()
    {
        var model = new DatabaseModel();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        model.Tables.AddRange(await ReadTablesAsync(connection));
        model.Views.AddRange(await ReadViewsAsync(connection));
        model.Procedures.AddRange(await ReadProceduresAsync(connection));

        return model;
    }

    private static async Task<List<DbTableInfo>> ReadTablesAsync(SqlConnection connection)
    {
        const string sql = @"
SELECT
    s.name  AS SchemaName,
    t.name  AS TableName,
    c.column_id,
    c.name  AS ColumnName,
    ty.name AS SqlTypeName,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    c.is_computed,
    CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END AS IsPrimaryKey
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN (
    SELECT ic.object_id, ic.column_id
    FROM sys.indexes i
    INNER JOIN sys.index_columns ic
        ON ic.object_id = i.object_id
       AND ic.index_id = i.index_id
    WHERE i.is_primary_key = 1
) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
WHERE t.is_ms_shipped = 0
ORDER BY s.name, t.name, c.column_id;";

        var map = new Dictionary<string, DbTableInfo>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var tableName = reader.GetString(1);
            var key = schema + "." + tableName;

            if (!map.TryGetValue(key, out var table))
            {
                table = new DbTableInfo
                {
                    Schema = schema,
                    Name = tableName,
                    ClassName = NameHelper.ToPascalCase(tableName)
                };
                map[key] = table;
            }

            var column = ReadColumn(reader, 2);
            table.Columns.Add(column);

            var isPrimaryKey = reader.GetInt32(11) == 1;
            if (isPrimaryKey)
                table.PrimaryKeyColumns.Add(column.Name);
        }

        return map.Values.OrderBy(x => x.Schema).ThenBy(x => x.Name).ToList();
    }

    private static async Task<List<DbViewInfo>> ReadViewsAsync(SqlConnection connection)
    {
        const string sql = @"
SELECT
    s.name  AS SchemaName,
    v.name  AS ViewName,
    c.column_id,
    c.name  AS ColumnName,
    ty.name AS SqlTypeName,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    c.is_computed
FROM sys.views v
INNER JOIN sys.schemas s ON s.schema_id = v.schema_id
INNER JOIN sys.columns c ON c.object_id = v.object_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE v.is_ms_shipped = 0
ORDER BY s.name, v.name, c.column_id;";

        var map = new Dictionary<string, DbViewInfo>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var viewName = reader.GetString(1);
            var key = schema + "." + viewName;

            if (!map.TryGetValue(key, out var view))
            {
                view = new DbViewInfo
                {
                    Schema = schema,
                    Name = viewName,
                    ClassName = NameHelper.ToPascalCase(viewName)
                };
                map[key] = view;
            }

            view.Columns.Add(ReadColumn(reader, 2));
        }

        return map.Values.OrderBy(x => x.Schema).ThenBy(x => x.Name).ToList();
    }

    private static async Task<List<DbProcedureInfo>> ReadProceduresAsync(SqlConnection connection)
    {
        const string sql = @"
SELECT
    s.name AS SchemaName,
    p.name AS ProcedureName
FROM sys.procedures p
INNER JOIN sys.schemas s ON s.schema_id = p.schema_id
WHERE p.is_ms_shipped = 0
ORDER BY s.name, p.name;";

        var procedures = new List<DbProcedureInfo>();

        await using (var cmd = new SqlCommand(sql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var name = reader.GetString(1);
                procedures.Add(new DbProcedureInfo
                {
                    Schema = schema,
                    Name = name,
                    MethodName = NameHelper.ToPascalCase(name)
                });
            }
        }

        foreach (var procedure in procedures)
        {
            procedure.Parameters.AddRange(await ReadProcedureParametersAsync(connection, procedure.Schema, procedure.Name));
            procedure.ResultColumns.AddRange(await ReadProcedureResultColumnsAsync(connection, procedure.Schema, procedure.Name));
        }

        return procedures;
    }

    private static async Task<List<DbParameterInfo>> ReadProcedureParametersAsync(SqlConnection connection, string schema, string procedureName)
    {
        const string sql = @"
SELECT
    p.parameter_id,
    p.name,
    ty.name AS SqlTypeName,
    p.max_length,
    p.precision,
    p.scale,
    p.is_output,
    p.has_default_value,
    p.is_nullable
FROM sys.parameters p
INNER JOIN sys.procedures sp ON sp.object_id = p.object_id
INNER JOIN sys.schemas s ON s.schema_id = sp.schema_id
INNER JOIN sys.types ty ON ty.user_type_id = p.user_type_id
WHERE s.name = @SchemaName
  AND sp.name = @ProcedureName
ORDER BY p.parameter_id;";

        var list = new List<DbParameterInfo>();

        await using var cmd = new SqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@SchemaName", schema);
        cmd.Parameters.AddWithValue("@ProcedureName", procedureName);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1);
            var isNullable = !reader.IsDBNull(8) && reader.GetBoolean(8);
            var sqlTypeName = reader.GetString(2);

            list.Add(new DbParameterInfo
            {
                Ordinal = reader.GetInt32(0),
                Name = name,
                VariableName = NameHelper.EscapeIdentifier(NameHelper.ToCamelCase(name.TrimStart('@'))),
                SqlTypeName = sqlTypeName,
                ClrTypeName = SqlTypeMapper.ToClrType(sqlTypeName, isNullable),
                MaxLength = reader.IsDBNull(3) ? null : reader.GetInt16(3),
                Precision = reader.IsDBNull(4) ? null : reader.GetByte(4),
                Scale = reader.IsDBNull(5) ? null : reader.GetByte(5),
                IsOutput = reader.GetBoolean(6),
                HasDefaultValue = !reader.IsDBNull(7) && reader.GetBoolean(7),
                IsNullable = isNullable
            });
        }

        return list;
    }

    private static async Task<List<DbResultColumnInfo>> ReadProcedureResultColumnsAsync(SqlConnection connection, string schema, string procedureName)
    {
        var list = new List<DbResultColumnInfo>();
        var fullName = $"[{schema}].[{procedureName}]";
        var tsql = "EXEC sys.sp_describe_first_result_set @tsql = N'EXEC " + fullName + "';";

        try
        {
            await using var cmd = new SqlCommand(tsql, connection);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(2))
                    continue;

                var ordinal = reader.GetInt32(0);
                var name = reader.GetString(2);
                var systemTypeName = reader.IsDBNull(5) ? "nvarchar" : reader.GetString(5);
                var sqlTypeName = NormalizeSystemTypeName(systemTypeName);
                var isNullable = !reader.IsDBNull(6) && reader.GetBoolean(6);

                list.Add(new DbResultColumnInfo
                {
                    Ordinal = ordinal,
                    Name = name,
                    PropertyName = NameHelper.EscapeIdentifier(NameHelper.ToPascalCase(name)),
                    SqlTypeName = sqlTypeName,
                    ClrTypeName = SqlTypeMapper.ToClrType(sqlTypeName, isNullable),
                    IsNullable = isNullable
                });
            }
        }
        catch
        {
            // Some procedures cannot be described automatically.
            // We intentionally keep empty result columns in this case.
        }

        return list.OrderBy(x => x.Ordinal).ToList();
    }

    private static DbColumnInfo ReadColumn(SqlDataReader reader, int ordinalOffset)
    {
        var name = reader.GetString(ordinalOffset + 1);
        var sqlTypeName = reader.GetString(ordinalOffset + 2);
        var isNullable = reader.GetBoolean(ordinalOffset + 6);

        return new DbColumnInfo
        {
            Ordinal = reader.GetInt32(ordinalOffset),
            Name = name,
            PropertyName = NameHelper.EscapeIdentifier(NameHelper.ToPascalCase(name)),
            SqlTypeName = sqlTypeName,
            ClrTypeName = SqlTypeMapper.ToClrType(sqlTypeName, isNullable),
            MaxLength = reader.IsDBNull(ordinalOffset + 3) ? null : reader.GetInt16(ordinalOffset + 3),
            Precision = reader.IsDBNull(ordinalOffset + 4) ? null : reader.GetByte(ordinalOffset + 4),
            Scale = reader.IsDBNull(ordinalOffset + 5) ? null : reader.GetByte(ordinalOffset + 5),
            IsNullable = isNullable,
            IsIdentity = reader.GetBoolean(ordinalOffset + 7),
            IsComputed = reader.GetBoolean(ordinalOffset + 8)
        };
    }

    private static string NormalizeSystemTypeName(string systemTypeName)
    {
        var s = systemTypeName.Trim().ToLowerInvariant();

        var stopAt = s.IndexOf('(');
        if (stopAt >= 0)
            s = s.Substring(0, stopAt);

        stopAt = s.IndexOf(' ');
        if (stopAt >= 0)
            s = s.Substring(0, stopAt);

        return s switch
        {
            "rowversion" => "timestamp",
            _ => s
        };
    }
}
