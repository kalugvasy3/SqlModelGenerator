namespace SqlModelGenerator.Metadata.Models;

public sealed class DatabaseModel
{
    public List<DbTableInfo> Tables { get; } = new();
    public List<DbViewInfo> Views { get; } = new();
    public List<DbProcedureInfo> Procedures { get; } = new();
}
