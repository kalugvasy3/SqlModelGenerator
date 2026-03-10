namespace SqlModelGenerator.Metadata.Models;

public sealed class DbTableInfo : DbObjectBase
{
    public List<string> PrimaryKeyColumns { get; } = new();
}
