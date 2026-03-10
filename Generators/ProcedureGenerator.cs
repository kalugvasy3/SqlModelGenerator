using SqlModelGenerator.Metadata;
using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class ProcedureGenerator
{
    private readonly CodeGeneratorOptions _options;

    public ProcedureGenerator(CodeGeneratorOptions options)
    {
        _options = options;
    }

    public void Generate(IEnumerable<DbProcedureInfo> procedures)
    {
        var dir = Path.Combine(_options.OutputRoot, "Procedures");
        Directory.CreateDirectory(dir);

        foreach (var procedure in procedures.Where(x => x.HasResultSet))
        {
            var resultCode = BuildResultClassCode(procedure);
            File.WriteAllText(Path.Combine(dir, procedure.ResultClassName + ".cs"), resultCode);
        }

        var methodsCode = BuildMethodsCode(procedures.OrderBy(x => x.Schema).ThenBy(x => x.Name).ToList());
        File.WriteAllText(Path.Combine(dir, _options.DbContextName + ".Procedures.g.cs"), methodsCode);
    }

    private string BuildResultClassCode(DbProcedureInfo procedure)
    {
        var w = new CodeWriter();
        w.WriteLine("using System;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace}.Procedures;");
        w.WriteLine();
        w.WriteLine($"public partial class {procedure.ResultClassName}");
        w.OpenBlock();
        foreach (var column in procedure.ResultColumns.OrderBy(x => x.Ordinal))
            w.WriteLine($"public {column.ClrTypeName} {column.PropertyName} {{ get; set; }}");
        w.CloseBlock();
        return w.ToString();
    }

    private string BuildMethodsCode(IReadOnlyList<DbProcedureInfo> procedures)
    {
        var w = new CodeWriter();
        w.WriteLine("using System.Data;");
        w.WriteLine("using Microsoft.Data.SqlClient;");
        w.WriteLine("using Microsoft.EntityFrameworkCore;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace};");
        w.WriteLine();
        w.WriteLine($"public partial class {_options.DbContextName}");
        w.OpenBlock();

        foreach (var procedure in procedures)
        {
            WriteProcedureMethod(w, procedure);
            w.WriteLine();
        }

        w.CloseBlock();
        return w.ToString();
    }

    private static void WriteProcedureMethod(CodeWriter w, DbProcedureInfo procedure)
    {
        var parameters = string.Join(", ", procedure.Parameters.Select(p => $"{p.ClrTypeName} {p.VariableName}"));
        if (parameters.Length > 0)
            parameters += ", ";
        parameters += "CancellationToken cancellationToken = default";

        var returnType = procedure.HasResultSet
            ? $"Task<List<Procedures.{procedure.ResultClassName}>>"
            : "Task<int>";

        w.WriteLine($"public async {returnType} {procedure.MethodName}Async({parameters})");
        w.OpenBlock();

        foreach (var p in procedure.Parameters)
        {
            var sqlDbType = SqlTypeMapper.ToSqlDbTypeExpression(p.SqlTypeName);
            w.WriteLine($"var p_{p.VariableName} = new SqlParameter(\"{p.ParameterName}\", {sqlDbType})");
            w.OpenBlock();
            w.WriteLine($"Value = (object?){p.VariableName} ?? DBNull.Value,");
            w.WriteLine($"Direction = {(p.IsOutput ? "ParameterDirection.InputOutput" : "ParameterDirection.Input")}");
            w.CloseBlock(";");

            if (p.MaxLength.HasValue && p.MaxLength.Value > 0)
                w.WriteLine($"p_{p.VariableName}.Size = {p.MaxLength.Value};");
            if (p.Precision.HasValue && p.Precision.Value > 0)
                w.WriteLine($"p_{p.VariableName}.Precision = {p.Precision.Value};");
            if (p.Scale.HasValue && p.Scale.Value > 0)
                w.WriteLine($"p_{p.VariableName}.Scale = {p.Scale.Value};");
        }

        var execParts = new List<string> { $"EXEC [{procedure.Schema}].[{procedure.Name}]" };
        execParts.AddRange(procedure.Parameters.Select(p => $"{p.ParameterName} = {p.ParameterName}"));
        var sql = string.Join(", ", execParts);

        if (procedure.HasResultSet)
        {
            var paramVars = procedure.Parameters.Count == 0
                ? string.Empty
                : ", " + string.Join(", ", procedure.Parameters.Select(p => "p_" + p.VariableName));
            w.WriteLine($"return await Set<Procedures.{procedure.ResultClassName}>()") ;
            w.WriteLine($"    .FromSqlRaw(@\"{sql}\"{paramVars})") ;
            w.WriteLine("    .ToListAsync(cancellationToken);");
        }
        else
        {
            var paramVars = procedure.Parameters.Count == 0
                ? string.Empty
                : ", " + string.Join(", ", procedure.Parameters.Select(p => "p_" + p.VariableName));
            w.WriteLine($"return await Database.ExecuteSqlRawAsync(@\"{sql}\"{paramVars}, cancellationToken);");
        }

        w.CloseBlock();
    }
}
