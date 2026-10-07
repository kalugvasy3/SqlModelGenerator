using System.Text;
using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class ReportGenerator
{
    private readonly CodeGeneratorOptions _options;

    public ReportGenerator(CodeGeneratorOptions options)
    {
        _options = options;
    }

    public void Generate(DatabaseModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SQL Model Generator report");
        sb.AppendLine();
        sb.AppendLine($"Tables    : {model.Tables.Count}");
        sb.AppendLine($"Views     : {model.Views.Count}");
        sb.AppendLine($"Procedures: {model.Procedures.Count}");
        sb.AppendLine();

        sb.AppendLine("EF6 mapping notes:");
        foreach (var table in model.Tables.Where(t => t.PrimaryKeyColumns.Count == 0))
            sb.AppendLine($"- [{table.Schema}].[{table.Name}] has no primary key: POCO generated, DbSet omitted (EF6 requires a key).");
        sb.AppendLine("- Views are read through Get<ViewName>ViewAsync methods, not tracked DbSets.");
        sb.AppendLine("- Procedure methods use System.Data.SqlClient and materialize the first result set only.");
        sb.AppendLine();

        sb.AppendLine("Procedures:");
        foreach (var p in model.Procedures.OrderBy(x => x.Schema).ThenBy(x => x.Name))
        {
            sb.AppendLine($"- [{p.Schema}].[{p.Name}]  params={p.Parameters.Count}  resultColumns={p.ResultColumns.Count}");
        }

        File.WriteAllText(Path.Combine(_options.OutputRoot, "Report.txt"), sb.ToString());
    }
}
