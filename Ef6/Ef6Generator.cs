using System.IO;
using System.Text;
using CommonClassLibrary;

namespace SqlModelGenerator.Ef6;

public enum NamingStyle { AsInDatabase, Singular, Plural }

/// <summary>Explicit names are keyed by original database object and column names.</summary>
public sealed class ObjectNameOverride
{
    public string Kind { get; set; } = "Table";
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string SetName { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>All input settings originate in Program.cs. The generation target is fixed to EF6.</summary>
public sealed class GeneratorSettings
{
    public string ConnectionString { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string RootNamespace { get; set; } = "GeneratedModel";
    public string ContextName { get; set; } = "AppDbContext";
    public int MetadataTimeoutSeconds { get; set; } = 120;
    public string SchemaFilter { get; set; } = "";
    public bool IncludeTables { get; set; } = true;
    public bool IncludeViews { get; set; } = true;
    public bool IncludeStoredProcedures { get; set; } = true;
    public NamingStyle ClassNaming { get; set; } = NamingStyle.Singular;
    public NamingStyle SetNaming { get; set; } = NamingStyle.Plural;
    public bool NullableReferenceTypes { get; set; }
    public string ExistingModelPath { get; set; } = "";
    public string DestinationProjectPath { get; set; } = "";
    public bool OverwriteGeneratedFiles { get; set; } = true;
    public bool RemoveObsoleteGeneratedFiles { get; set; } = true;
    public ObjectNameOverride[] NameOverrides { get; set; } = [];
}

public sealed record GenerationReport(string OutputFolder, int Tables, int Views, int Procedures,
    int ForeignKeys, string[] Files, int ChangedFiles, string[] Warnings);

internal static class Generator
{
    private static PocoNaming Naming(NamingStyle value) => value switch
    {
        NamingStyle.AsInDatabase => PocoNaming.AsInDatabase,
        NamingStyle.Singular => PocoNaming.Singular,
        NamingStyle.Plural => PocoNaming.Plural,
        _ => throw new ArgumentException("Choose AsInDatabase, Singular or Plural.", "naming")
    };

    private static IReadOnlyDictionary<(SqlCatalogObjectKind, string, string), PocoObjectNames> Overrides(ObjectNameOverride[]? values)
    {
        var result = new Dictionary<(SqlCatalogObjectKind, string, string), PocoObjectNames>();
        foreach (var value in values ?? [])
        {
            if (value is null || string.IsNullOrWhiteSpace(value.Name))
                throw new ArgumentException("Each name override needs a database object name.", "NameOverrides");
            var kind = value.Kind.ToUpperInvariant() switch
            {
                "TABLE" => SqlCatalogObjectKind.Table,
                "VIEW" => SqlCatalogObjectKind.View,
                "PROCEDURE" => SqlCatalogObjectKind.Procedure,
                _ => throw new ArgumentException("An override kind must be Table, View or Procedure.", "NameOverrides")
            };
            var key = (kind, value.Schema, value.Name);
            var properties = value.Properties is null
                ? new SortedDictionary<string, string>(StringComparer.Ordinal)
                : new SortedDictionary<string, string>(value.Properties, StringComparer.Ordinal);
            if (!result.TryAdd(key, new(value.ClassName, value.SetName, properties)))
                throw new ArgumentException("A database object has duplicate naming overrides.", "NameOverrides");
        }
        return result;
    }

    internal static bool Inside(string parent, string child)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static (string Output, PocoOptions Options) Prepare(GeneratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
            throw new ArgumentException("Set the SQL Server connection string in Program.cs.", "ConnectionString");
        if (string.IsNullOrWhiteSpace(settings.OutputFolder))
            throw new ArgumentException("Set the output folder in Program.cs.", "OutputFolder");
        var output = Path.GetFullPath(settings.OutputFolder);
        if (string.Equals(Path.TrimEndingDirectorySeparator(output), Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a dedicated model folder, not the filesystem root.", "OutputFolder");
        if (settings.MetadataTimeoutSeconds <= 0)
            throw new ArgumentException("The metadata timeout must be positive.", "MetadataTimeoutSeconds");
        if (!(settings.IncludeTables || settings.IncludeViews || settings.IncludeStoredProcedures))
            throw new ArgumentException("Select at least one object category in Program.cs.", nameof(settings));
        return (output, new(settings.RootNamespace, settings.ContextName, PocoTarget.Ef6,
            Naming(settings.ClassNaming), Naming(settings.SetNaming), settings.NullableReferenceTypes, Overrides(settings.NameOverrides)));
    }

    internal static int Publish(string output, GeneratorSettings settings, PocoGeneration generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        var (manifest, owned) = PocoOutput.OwnedFiles(output);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in generation.Files)
            if (!paths.Add(file.FileName))
                throw new InvalidOperationException("Duplicate generated file: " + file.FileName);

        // A previous console used different filenames. Never mix or silently delete an untracked model.
        var folders = new Stack<string>();
        folders.Push(output);
        while (folders.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Choose an output directory without symbolic links or junctions.");
            foreach (var child in Directory.EnumerateDirectories(folder)) folders.Push(child);
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs"))
            {
                var relative = Path.GetRelativePath(output, file);
                if (!paths.Contains(relative) && !owned.ContainsKey(relative))
                    throw new InvalidOperationException("The output contains C# files from another model: " + relative
                        + ". Choose an empty output folder; existing files were preserved.");
            }
        }

        var changes = new List<PocoOutputChange>();
        foreach (var generated in generation.Files)
        {
            token.ThrowIfCancellationRequested();
            var current = PocoOutput.Inspect(output, generated.FileName);
            if (current.Before != generated.Text)
            {
                if (current.BeforeHash is not null)
                {
                    if (!settings.OverwriteGeneratedFiles)
                        throw new InvalidOperationException("Overwriting generated files is disabled: " + generated.FileName);
                    if (!PocoOutput.IsOwned(owned, current))
                        throw new InvalidOperationException("The output contains an unowned or manually edited file: " + generated.FileName
                            + ". Choose an empty output folder or preserve that file separately.");
                }
                changes.Add(new(current, generated.Text, true));
            }
        }

        var removals = new List<PocoOutputFile>();
        if (settings.RemoveObsoleteGeneratedFiles)
        {
            foreach (var (path, _) in owned)
            {
                token.ThrowIfCancellationRequested();
                if (paths.Contains(path)) continue;
                var current = PocoOutput.Inspect(output, path);
                if (current.BeforeHash is null) continue;
                if (!PocoOutput.IsOwned(owned, current))
                    throw new InvalidOperationException("An obsolete generated file was manually edited: " + path);
                removals.Add(current);
            }
        }
        // The atomic publisher requires a write for retirement. A normal generation includes Report.txt.
        if (changes.Count == 0 && removals.Count > 0)
        {
            var report = generation.Files.First(file => file.FileName == "Report.txt");
            changes.Add(new(PocoOutput.Inspect(output, report.FileName), report.Text, true));
        }
        token.ThrowIfCancellationRequested();
        if (changes.Count > 0)
            _ = PocoOutput.ApplyGenerated(output, output, manifest, changes.ToArray(), removals.ToArray());
        return changes.Count + removals.Count;
    }

    // Retained for the independent parity/publisher verification harness.
    internal static int publish(string output, GeneratorSettings settings, PocoGeneration generation, CancellationToken token) =>
        Publish(output, settings, generation, token);
}

/// <summary>The console owns cancellation. Metadata connections/readers are closed before publication.</summary>
public static class Ef6ModelGenerator
{
    public static async Task<GenerationReport> GenerateAsync(GeneratorSettings settings, CancellationToken token, IProgress<string>? progress)
    {
        var (output, options) = Generator.Prepare(settings);
        void Report(string message) => progress?.Report(message);
        PocoExistingScan? existing = null;
        if (!string.IsNullOrWhiteSpace(settings.ExistingModelPath))
        {
            token.ThrowIfCancellationRequested();
            Report("Reading the existing EF6 model...");
            existing = PocoExistingModel.Scan(settings.ExistingModelPath, token);
            if (existing.Target != PocoTarget.Ef6)
                throw new InvalidOperationException("ExistingModelPath must identify one complete EF6 model.");
            if (Generator.Inside(existing.Root, output) || Generator.Inside(output, existing.Root))
                throw new InvalidOperationException("Generate to a separate folder outside the existing model. The input model is read-only.");
        }

        Report("Reading SQL Server metadata (tables, views, procedures and relationships)...");
        var snapshot = await SqlServerSchema.LoadAsync(settings.ConnectionString, settings.MetadataTimeoutSeconds, settings.SchemaFilter,
            settings.IncludeTables, settings.IncludeViews, settings.IncludeStoredProcedures, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (snapshot.Objects.Length == 0)
            throw new InvalidOperationException("No selected objects were visible. Check the database, schema filter and metadata permissions; existing output was preserved.");
        Report("Generating the EF6 model...");
        PocoGeneration generation;
        if (existing is null)
            generation = PocoCodeGeneration.Generate(snapshot, options);
        else
        {
            var project = string.IsNullOrWhiteSpace(settings.DestinationProjectPath) ? null : settings.DestinationProjectPath;
            var compatible = PocoExistingModel.GenerateCompatibleForProject(existing, snapshot, options, project, token);
            generation = PocoExistingModel.ApplyGeneratedNames(existing.Namespace, existing.ContextName,
                options.Namespace, options.ContextName, compatible, token);
        }
        int Count(SqlCatalogObjectKind kind) => snapshot.Objects.Count(item => item.Item.Kind == kind);
        var tables = Count(SqlCatalogObjectKind.Table);
        var views = Count(SqlCatalogObjectKind.View);
        var procedures = Count(SqlCatalogObjectKind.Procedure);
        var warnings = generation.Warnings.Distinct(StringComparer.Ordinal).ToArray();
        var text = new StringBuilder();
        text.AppendLine("SqlModelGenerator - Entity Framework 6");
        text.AppendLine("Namespace: " + options.Namespace).AppendLine("Context: " + options.ContextName);
        text.AppendLine($"Tables: {tables}\nViews: {views}\nProcedures: {procedures}\nRelationships: {snapshot.Relationships.Length}");
        text.AppendLine("The database was inspected through metadata queries; procedures were not executed.");
        foreach (var warning in warnings) text.AppendLine("WARNING: " + warning);
        var complete = generation with { Files = [.. generation.Files, new PocoGeneratedFile("Report.txt", text.ToString())] };
        Report("Publishing generated files...");
        var changed = Generator.Publish(output, settings, complete, token);
        return new(output, tables, views, procedures, snapshot.Relationships.Length,
            complete.Files.Select(file => file.FileName).ToArray(), changed, warnings);
    }
}
