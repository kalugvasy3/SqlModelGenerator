namespace CommonClassLibrary;

// SQL text preserved exactly from the verified EF6 metadata reader.
internal static class SqlServerQueries
{
    internal const string Columns = @"
SELECT c.name, TYPE_NAME(c.user_type_id), c.system_type_id, c.is_nullable,
       c.max_length, c.precision, c.scale, c.is_identity,
       CASE WHEN c.is_computed=1 OR c.generated_always_type<>0 OR c.system_type_id=189 THEN 1 ELSE 0 END
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.columns c ON c.object_id=o.object_id
WHERE {scope} AND o.type IN ('U','V') AND c.is_hidden=0
ORDER BY c.column_id";

    internal const string Keys = @"
SELECT c.name
FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.indexes i ON i.object_id=o.object_id AND i.is_primary_key=1
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE {scope} AND o.type='U'
ORDER BY ic.key_ordinal";

    internal const string UniqueKeys = @"
SELECT i.index_id,c.name
FROM sys.tables o JOIN sys.schemas s ON s.schema_id=o.schema_id
JOIN sys.indexes i ON i.object_id=o.object_id
JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.key_ordinal>0
JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
WHERE {scope} AND i.is_unique=1 AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
ORDER BY i.index_id,ic.key_ordinal";

    internal const string RelationshipDetails = @"
SELECT fk.name,DB_NAME(),s.name,o.name,fk.delete_referential_action_desc,fk.update_referential_action_desc,
       fk.is_disabled,fk.is_not_trusted
FROM sys.foreign_keys fk
JOIN sys.tables o ON o.object_id=fk.parent_object_id
JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE {scopes}
ORDER BY fk.object_id";

    internal const string Relationships = @"
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
ORDER BY fk.object_id,fc.constraint_column_id";

    internal const string VerifyRoutine = @"
SELECT o.object_id FROM sys.objects o JOIN sys.schemas s ON s.schema_id=o.schema_id
WHERE {scope} AND o.type IN ('P','PC')";

    internal const string Parameters = @"
SELECT p.name, COALESCE(TYPE_NAME(p.system_type_id), TYPE_NAME(p.user_type_id)), p.system_type_id, p.is_nullable,
       p.max_length, p.precision, p.scale, p.is_output, p.parameter_id, p.is_readonly
FROM sys.parameters p
WHERE p.object_id={id} AND p.parameter_id>0
ORDER BY p.parameter_id";

    internal const string Result = @"
SELECT name, TYPE_NAME(system_type_id), system_type_id, is_nullable,
       max_length, precision, scale, error_number, error_type_desc
FROM sys.dm_exec_describe_first_result_set_for_object({id},0)
ORDER BY column_ordinal";

    internal const string Selected = @"
SELECT TOP ({maximumObjects + 1}) o.type,s.name,o.name,
       CASE WHEN o.type='U' AND t.temporal_type=1 THEN 'temporal history' ELSE '' END
FROM sys.objects AS o
JOIN sys.schemas AS s ON s.schema_id=o.schema_id
LEFT JOIN sys.tables AS t ON t.object_id=o.object_id
WHERE o.type IN ({kinds}) AND o.is_ms_shipped=0 AND s.name NOT IN ('sys','INFORMATION_SCHEMA'){schemaScope}
ORDER BY CASE WHEN o.type='V' THEN 'view' WHEN o.type='U' THEN 'table' ELSE 'procedure' END,s.name,o.name";

}
