using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class DbContextGenerator
{
    private readonly CodeGeneratorOptions _options;

    public DbContextGenerator(CodeGeneratorOptions options) => _options = options;

    public void Generate(DatabaseModel model)
    {
        var w = new CodeWriter();
        w.WriteLine("using System;");
        w.WriteLine("using System.Collections.Generic;");
        w.WriteLine("using System.ComponentModel.DataAnnotations.Schema;");
        w.WriteLine("using System.Data.Entity;");
        w.WriteLine("using System.Threading;");
        w.WriteLine("using System.Threading.Tasks;");
        w.WriteLine($"using {_options.RootNamespace}.Entities;");
        w.WriteLine($"using {_options.RootNamespace}.Views;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace}");
        w.OpenBlock();
        w.WriteLine($"public partial class {_options.DbContextName} : DbContext");
        w.OpenBlock();
        w.WriteLine($"static {_options.DbContextName}()");
        w.OpenBlock();
        w.WriteLine($"System.Data.Entity.Database.SetInitializer<{_options.DbContextName}>(null);");
        w.CloseBlock();
        w.WriteLine();
        w.WriteLine($"public {_options.DbContextName}() : base(\"name={_options.DbContextName}\") {{ }}");
        w.WriteLine($"public {_options.DbContextName}(string connectionStringOrName) : base(connectionStringOrName) {{ }}");
        w.WriteLine();

        foreach (var table in model.Tables.Where(t => t.PrimaryKeyColumns.Count > 0).OrderBy(t => t.ClassName))
            w.WriteLine($"public DbSet<{table.ClassName}> {table.ClassName}s {{ get; set; }}");

        foreach (var view in model.Views.OrderBy(v => v.ClassName))
        {
            var columns = view.Columns.OrderBy(c => c.Ordinal)
                .Select(c => $"{SqlIdentifier(c.Name)} AS {SqlIdentifier(c.PropertyName.TrimStart('@'))}");
            var sql = $"SELECT {string.Join(", ", columns)} FROM {SqlIdentifier(view.Schema)}.{SqlIdentifier(view.Name)}";
            w.WriteLine();
            w.WriteLine($"public Task<List<Views.{view.ClassName}>> Get{view.ClassName}ViewAsync(CancellationToken cancellationToken = default(CancellationToken))");
            w.OpenBlock();
            w.WriteLine($"return Database.SqlQuery<Views.{view.ClassName}>(\"{CSharpString(sql)}\").ToListAsync(cancellationToken);");
            w.CloseBlock();
        }

        w.WriteLine();
        w.WriteLine("protected override void OnModelCreating(DbModelBuilder modelBuilder)");
        w.OpenBlock();
        foreach (var table in model.Tables.Where(t => t.PrimaryKeyColumns.Count > 0).OrderBy(t => t.ClassName))
        {
            w.WriteLine($"modelBuilder.Entity<{table.ClassName}>()");
            w.WriteLine($"    .ToTable(\"{CSharpString(table.Name)}\", \"{CSharpString(table.Schema)}\");");
            var keys = table.PrimaryKeyColumns.Select(name => table.Columns.First(c => c.Name == name).PropertyName).ToArray();
            var key = keys.Length == 1 ? $"e.{keys[0]}" : $"new {{ {string.Join(", ", keys.Select(k => "e." + k))} }}";
            w.WriteLine($"modelBuilder.Entity<{table.ClassName}>().HasKey(e => {key});");
            foreach (var column in table.Columns.OrderBy(c => c.Ordinal))
            {
                var property = $"modelBuilder.Entity<{table.ClassName}>().Property(e => e.{column.PropertyName})";
                w.WriteLine($"{property}.HasColumnName(\"{CSharpString(column.Name)}\");");
                if (!column.IsNullable && (column.ClrTypeName is "string" or "byte[]"))
                    w.WriteLine($"{property}.IsRequired();");
                if (column.IsIdentity || column.IsComputed)
                {
                    var option = column.IsIdentity ? "Identity" : "Computed";
                    w.WriteLine($"{property}.HasDatabaseGeneratedOption(DatabaseGeneratedOption.{option});");
                }
            }
        }
        w.WriteLine("base.OnModelCreating(modelBuilder);");
        w.CloseBlock();
        w.CloseBlock();
        w.CloseBlock();
        File.WriteAllText(Path.Combine(_options.OutputRoot, _options.DbContextName + ".g.cs"), w.ToString());
    }

    private static string SqlIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string CSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
