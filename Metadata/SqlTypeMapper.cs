namespace SqlModelGenerator.Metadata;

public static class SqlTypeMapper
{
    public static string ToClrType(string sqlTypeName, bool isNullable)
    {
        var sql = sqlTypeName.ToLowerInvariant();

        var baseType = sql switch
        {
            "bigint" => "long",
            "binary" => "byte[]",
            "bit" => "bool",
            "char" => "string",
            "date" => "DateTime",
            "datetime" => "DateTime",
            "datetime2" => "DateTime",
            "datetimeoffset" => "DateTimeOffset",
            "decimal" => "decimal",
            "float" => "double",
            "image" => "byte[]",
            "int" => "int",
            "money" => "decimal",
            "nchar" => "string",
            "ntext" => "string",
            "numeric" => "decimal",
            "nvarchar" => "string",
            "real" => "float",
            "smalldatetime" => "DateTime",
            "smallint" => "short",
            "smallmoney" => "decimal",
            "sql_variant" => "object",
            "text" => "string",
            "time" => "TimeSpan",
            "timestamp" => "byte[]",
            "tinyint" => "byte",
            "uniqueidentifier" => "Guid",
            "varbinary" => "byte[]",
            "varchar" => "string",
            "xml" => "string",
            _ => "string"
        };

        // The generated code targets C# 7.3 on .NET Framework 4.8. Nullable
        // reference annotations require C# 8 and are not needed at runtime.
        if (baseType is "string" or "byte[]" or "object")
            return baseType;

        return isNullable ? baseType + "?" : baseType;
    }

    public static string ToSqlDbTypeExpression(string sqlTypeName)
    {
        return sqlTypeName.ToLowerInvariant() switch
        {
            "bigint" => "SqlDbType.BigInt",
            "binary" => "SqlDbType.Binary",
            "bit" => "SqlDbType.Bit",
            "char" => "SqlDbType.Char",
            "date" => "SqlDbType.Date",
            "datetime" => "SqlDbType.DateTime",
            "datetime2" => "SqlDbType.DateTime2",
            "datetimeoffset" => "SqlDbType.DateTimeOffset",
            "decimal" => "SqlDbType.Decimal",
            "float" => "SqlDbType.Float",
            "image" => "SqlDbType.Image",
            "int" => "SqlDbType.Int",
            "money" => "SqlDbType.Money",
            "nchar" => "SqlDbType.NChar",
            "ntext" => "SqlDbType.NText",
            "numeric" => "SqlDbType.Decimal",
            "nvarchar" => "SqlDbType.NVarChar",
            "real" => "SqlDbType.Real",
            "smalldatetime" => "SqlDbType.SmallDateTime",
            "smallint" => "SqlDbType.SmallInt",
            "smallmoney" => "SqlDbType.SmallMoney",
            "sql_variant" => "SqlDbType.Variant",
            "text" => "SqlDbType.Text",
            "time" => "SqlDbType.Time",
            "timestamp" => "SqlDbType.Timestamp",
            "tinyint" => "SqlDbType.TinyInt",
            "uniqueidentifier" => "SqlDbType.UniqueIdentifier",
            "varbinary" => "SqlDbType.VarBinary",
            "varchar" => "SqlDbType.VarChar",
            "xml" => "SqlDbType.Xml",
            _ => "SqlDbType.NVarChar"
        };
    }
}
