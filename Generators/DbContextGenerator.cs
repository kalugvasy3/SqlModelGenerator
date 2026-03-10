using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class DbContextGenerator
{
    private readonly CodeGeneratorOptions _options;

    public DbContextGenerator(CodeGeneratorOptions options)
    {
        _options = options;
    }

    public void Generate(DatabaseModel model)
    {
        var path = Path.Combine(_options.OutputRoot, _options.DbContextName + ".g.cs");
        var w = new CodeWriter();

        w.WriteLine("using Microsoft.EntityFrameworkCore;");
        w.WriteLine($"using {_options.RootNamespace}.Entities;");
        w.WriteLine($"using {_options.RootNamespace}.Views;");
        w.WriteLine($"using {_options.RootNamespace}.Procedures;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace};");
        w.WriteLine();
        w.WriteLine($"public partial class {_options.DbContextName} : DbContext");
        w.OpenBlock();
        w.WriteLine($"public {_options.DbContextName}(DbContextOptions<{_options.DbContextName}> options) : base(options)");
        w.OpenBlock();
        w.CloseBlock();
        w.WriteLine();

        foreach (var table in model.Tables.OrderBy(x => x.ClassName))
            w.WriteLine($"public DbSet<{table.ClassName}> {table.ClassName}s => Set<{table.ClassName}>();");

        foreach (var view in model.Views.OrderBy(x => x.ClassName))
            w.WriteLine($"public DbSet<{view.ClassName}> {view.ClassName}s => Set<{view.ClassName}>();");

        foreach (var proc in model.Procedures.Where(x => x.HasResultSet).OrderBy(x => x.ResultClassName))
            w.WriteLine($"public DbSet<{proc.ResultClassName}> {proc.ResultClassName}s => Set<{proc.ResultClassName}>();");

        w.WriteLine();
        w.WriteLine("protected override void OnModelCreating(ModelBuilder modelBuilder)");
        w.OpenBlock();

        foreach (var table in model.Tables.OrderBy(x => x.ClassName))
        {
            w.WriteLine($"modelBuilder.Entity<{table.ClassName}>(entity =>");
            w.OpenBlock();
            w.WriteLine($"entity.ToTable(\"{table.Name}\", \"{table.Schema}\");");

            if (table.PrimaryKeyColumns.Count > 0)
            {
                if (table.PrimaryKeyColumns.Count == 1)
                {
                    var prop = table.Columns.First(x => x.Name == table.PrimaryKeyColumns[0]).PropertyName;
                    w.WriteLine($"entity.HasKey(e => e.{prop});");
                }
                else
                {
                    var props = table.Columns
                        .Where(c => table.PrimaryKeyColumns.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
                        .OrderBy(c => c.Ordinal)
                        .Select(c => "e." + c.PropertyName);
                    w.WriteLine($"entity.HasKey(e => new {{ {string.Join(", ", props)} }});");
                }
            }
            else
            {
                w.WriteLine("entity.HasNoKey();");
            }

            foreach (var column in table.Columns.OrderBy(x => x.Ordinal))
            {
                w.WriteLine($"entity.Property(e => e.{column.PropertyName}).HasColumnName(\"{column.Name}\");");
            }

            w.CloseBlock(");");
            w.WriteLine();
        }

        foreach (var view in model.Views.OrderBy(x => x.ClassName))
        {
            w.WriteLine($"modelBuilder.Entity<{view.ClassName}>(entity =>");
            w.OpenBlock();
            w.WriteLine("entity.HasNoKey();");
            w.WriteLine($"entity.ToView(\"{view.Name}\", \"{view.Schema}\");");
            foreach (var column in view.Columns.OrderBy(x => x.Ordinal))
            {
                w.WriteLine($"entity.Property(e => e.{column.PropertyName}).HasColumnName(\"{column.Name}\");");
            }
            w.CloseBlock(");");
            w.WriteLine();
        }

        foreach (var proc in model.Procedures.Where(x => x.HasResultSet).OrderBy(x => x.ResultClassName))
        {
            w.WriteLine($"modelBuilder.Entity<{proc.ResultClassName}>(entity =>");
            w.OpenBlock();
            w.WriteLine("entity.HasNoKey();");
            w.WriteLine("entity.ToView(null);");
            foreach (var column in proc.ResultColumns.OrderBy(x => x.Ordinal))
            {
                w.WriteLine($"entity.Property(e => e.{column.PropertyName}).HasColumnName(\"{column.Name}\");");
            }
            w.CloseBlock(");");
            w.WriteLine();
        }

        w.WriteLine("base.OnModelCreating(modelBuilder);");
        w.CloseBlock();
        w.CloseBlock();

        File.WriteAllText(path, w.ToString());
    }
}
