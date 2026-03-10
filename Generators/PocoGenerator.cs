using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class PocoGenerator
{
    private readonly CodeGeneratorOptions _options;

    public PocoGenerator(CodeGeneratorOptions options)
    {
        _options = options;
    }

    public void GenerateTables(IEnumerable<DbTableInfo> tables)
    {
        var dir = Path.Combine(_options.OutputRoot, "Entities");
        Directory.CreateDirectory(dir);

        foreach (var table in tables)
        {
            var code = BuildClassCode(table.Schema, table.ClassName, table.Columns, _options.RootNamespace + ".Entities");
            File.WriteAllText(Path.Combine(dir, table.ClassName + ".cs"), code);
        }
    }

    public void GenerateViews(IEnumerable<DbViewInfo> views)
    {
        var dir = Path.Combine(_options.OutputRoot, "Views");
        Directory.CreateDirectory(dir);

        foreach (var view in views)
        {
            var code = BuildClassCode(view.Schema, view.ClassName, view.Columns, _options.RootNamespace + ".Views");
            File.WriteAllText(Path.Combine(dir, view.ClassName + ".cs"), code);
        }
    }

    private static string BuildClassCode(string schema, string className, IEnumerable<DbColumnInfo> columns, string ns)
    {
        var w = new CodeWriter();
        w.WriteLine("using System;");
        w.WriteLine();
        w.WriteLine($"namespace {ns};");
        w.WriteLine();
        w.WriteLine($"public partial class {className}");
        w.OpenBlock();

        w.WriteLine($"// Schema: {schema}");
        foreach (var column in columns.OrderBy(x => x.Ordinal))
        {
            w.WriteLine($"public {column.ClrTypeName} {column.PropertyName} {{ get; set; }}");
        }

        w.CloseBlock();
        return w.ToString();
    }
}
