using SqlModelGenerator.Metadata;
using SqlModelGenerator.Metadata.Models;

namespace SqlModelGenerator.Generators;

public sealed class ProcedureGenerator
{
    private readonly CodeGeneratorOptions _options;

    public ProcedureGenerator(CodeGeneratorOptions options) => _options = options;

    public void Generate(IEnumerable<DbProcedureInfo> procedures)
    {
        var dir = Path.Combine(_options.OutputRoot, "Procedures");
        Directory.CreateDirectory(dir);
        var ordered = procedures.OrderBy(p => p.Schema).ThenBy(p => p.Name).ToList();
        foreach (var procedure in ordered.Where(p => p.HasResultSet))
            File.WriteAllText(Path.Combine(dir, procedure.ResultClassName + ".cs"), BuildResultClassCode(procedure));
        File.WriteAllText(Path.Combine(dir, _options.DbContextName + ".Procedures.g.cs"), BuildMethodsCode(ordered));
    }

    private string BuildResultClassCode(DbProcedureInfo procedure)
    {
        var w = new CodeWriter();
        w.WriteLine("using System;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace}.Procedures");
        w.OpenBlock();
        w.WriteLine($"public partial class {procedure.ResultClassName}");
        w.OpenBlock();
        foreach (var column in procedure.ResultColumns.OrderBy(c => c.Ordinal))
            w.WriteLine($"public {column.ClrTypeName} {column.PropertyName} {{ get; set; }}");
        w.CloseBlock();
        w.CloseBlock();
        return w.ToString();
    }

    private string BuildMethodsCode(IReadOnlyList<DbProcedureInfo> procedures)
    {
        var w = new CodeWriter();
        w.WriteLine("using System;");
        w.WriteLine("using System.Collections.Generic;");
        w.WriteLine("using System.Data;");
        w.WriteLine("using System.Data.SqlClient;");
        w.WriteLine("using System.Globalization;");
        w.WriteLine("using System.Threading;");
        w.WriteLine("using System.Threading.Tasks;");
        w.WriteLine();
        w.WriteLine($"namespace {_options.RootNamespace}");
        w.OpenBlock();
        w.WriteLine($"public partial class {_options.DbContextName}");
        w.OpenBlock();
        foreach (var procedure in procedures)
        {
            WriteProcedureMethod(w, procedure);
            w.WriteLine();
        }
        w.WriteLine("private static T ReadProcedureValue<T>(SqlDataReader reader, int ordinal)");
        w.OpenBlock();
        w.WriteLine("if (reader.IsDBNull(ordinal)) return default(T);");
        w.WriteLine("object value = reader.GetValue(ordinal);");
        w.WriteLine("if (value is T) return (T)value;");
        w.WriteLine("Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);");
        w.WriteLine("return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);");
        w.CloseBlock();
        w.CloseBlock();
        w.CloseBlock();
        return w.ToString();
    }

    private static void WriteProcedureMethod(CodeWriter w, DbProcedureInfo procedure)
    {
        var parameters = string.Join(", ", procedure.Parameters.OrderBy(p => p.Ordinal)
            .Select(p => $"{p.ClrTypeName} {p.VariableName}"));
        if (parameters.Length > 0) parameters += ", ";
        parameters += "CancellationToken cancellationToken = default(CancellationToken)";
        var returnType = procedure.HasResultSet
            ? $"Task<List<Procedures.{procedure.ResultClassName}>>" : "Task<int>";
        w.WriteLine($"public async {returnType} {procedure.MethodName}Async({parameters})");
        w.OpenBlock();
        w.WriteLine("var connection = (SqlConnection)Database.Connection;");
        w.WriteLine("bool closeConnection = connection.State != ConnectionState.Open;");
        w.WriteLine("if (closeConnection) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);");
        w.WriteLine("try");
        w.OpenBlock();
        w.WriteLine("using (var command = connection.CreateCommand())");
        w.OpenBlock();
        w.WriteLine("command.CommandType = CommandType.StoredProcedure;");
        w.WriteLine($"command.CommandText = \"{CSharpString(SqlIdentifier(procedure.Schema) + "." + SqlIdentifier(procedure.Name))}\";");
        w.WriteLine("if (Database.CurrentTransaction != null)");
        w.WriteLine("    command.Transaction = (SqlTransaction)Database.CurrentTransaction.UnderlyingTransaction;");
        foreach (var p in procedure.Parameters.OrderBy(p => p.Ordinal))
        {
            var sqlDbType = SqlTypeMapper.ToSqlDbTypeExpression(p.SqlTypeName);
            w.WriteLine($"var p_{p.VariableName.TrimStart('@')} = new SqlParameter(\"{CSharpString(p.ParameterName)}\", {sqlDbType});");
            w.WriteLine($"p_{p.VariableName.TrimStart('@')}.Value = (object){p.VariableName} ?? DBNull.Value;");
            if (p.IsOutput) w.WriteLine($"p_{p.VariableName.TrimStart('@')}.Direction = ParameterDirection.InputOutput;");
            if (p.MaxLength.HasValue && p.MaxLength.Value != 0)
            {
                var size = p.SqlTypeName.ToLowerInvariant() is "nvarchar" or "nchar" && p.MaxLength.Value > 0
                    ? p.MaxLength.Value / 2 : p.MaxLength.Value;
                w.WriteLine($"p_{p.VariableName.TrimStart('@')}.Size = {size};");
            }
            if (p.Precision.HasValue && p.Precision.Value > 0)
                w.WriteLine($"p_{p.VariableName.TrimStart('@')}.Precision = {p.Precision.Value};");
            if (p.Scale.HasValue && p.Scale.Value > 0)
                w.WriteLine($"p_{p.VariableName.TrimStart('@')}.Scale = {p.Scale.Value};");
            w.WriteLine($"command.Parameters.Add(p_{p.VariableName.TrimStart('@')});");
        }
        if (procedure.HasResultSet)
        {
            w.WriteLine($"var result = new List<Procedures.{procedure.ResultClassName}>();");
            w.WriteLine("using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))");
            w.OpenBlock();
            w.WriteLine("while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))");
            w.OpenBlock();
            w.WriteLine($"result.Add(new Procedures.{procedure.ResultClassName}");
            w.OpenBlock();
            var columns = procedure.ResultColumns.OrderBy(c => c.Ordinal).ToList();
            for (var i = 0; i < columns.Count; i++)
                w.WriteLine($"{columns[i].PropertyName} = ReadProcedureValue<{columns[i].ClrTypeName}>(reader, {i}){(i + 1 == columns.Count ? "" : ",")}");
            w.CloseBlock(");");
            w.CloseBlock();
            w.CloseBlock();
            w.WriteLine("return result;");
        }
        else
        {
            w.WriteLine("return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);");
        }
        w.CloseBlock();
        w.CloseBlock();
        w.WriteLine("finally");
        w.OpenBlock();
        w.WriteLine("if (closeConnection) connection.Close();");
        w.CloseBlock();
        w.CloseBlock();
    }

    private static string SqlIdentifier(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string CSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
