using SqlModelGenerator.Ef6;

namespace SqlModelGenerator;

internal static class Program
{
    // ==================== INPUT PARAMETERS ====================
    // All generation settings are located here. Command-line arguments
    // and environment variables do not override this block. 

    // SQL Server connection for reading the schema. The existing value has been preserved. 
    // The generator does not execute procedures or modify data in the database.
    private const string ConnectionString = @"
            Data Source=localhost;
            Initial Catalog = OregonLC; 
            MultipleActiveResultSets=True;
            Encrypt=false;
            TrustServerCertificate=true;
            Integrated Security = True;
            Transaction Binding = Explicit Unbind;";

    // A separate folder for the result. Replace with a full path if necessary. 
    // Do not place an existing model from the project here; there is a field below for that.
    private static readonly string OutputFolder = Path.Combine(AppContext.BaseDirectory, "Generated");

    // Namespace and DbContext names in the generated EF6 C# code.
    private const string RootNamespace = "GeneratedModel";
    private const string ContextName = "AppDbContext";

    // Timeout for each metadata request, in seconds. Empty filter = all schemas.
    private const int MetadataTimeoutSeconds = 120;
    private const string SchemaFilter = "";
    private const bool IncludeTables = true;
    private const bool IncludeViews = true;
    private const bool IncludeStoredProcedures = true;
    // FK и связи читаются всегда для выбранных таблиц.

    // Singular: Clients -> Client; Plural: Client -> Clients.
    // AsInDatabase preserves the name as it appears in the database. For .NET Framework/C# 7.3,
    // nullable reference types must be disabled; nullable value types are preserved.
    private const NamingStyle ClassNaming = NamingStyle.Singular;
    private const NamingStyle SetNaming = NamingStyle.Plural;
    private const bool NullableReferenceTypes = false;

    // Optional: folder/project of an existing EF6 model (e.g., Missouri). 
    // If a path is provided, existing names, interfaces, methods,
    // navigation properties, and procedure contracts are preserved. The original model remains the input template.
    private const string ExistingModelPath = "";

    // Optional: The .csproj file of the application that will use the result. 
    // Used to determine the permissible C# syntax; the project itself is not modified.
    private const string DestinationProjectPath = "";

    // Only previously created files that have not been manually modified will be updated or deleted. 
    // In the event of a conflict, the generator will stop and preserve the existing files.
    private const bool OverwriteGeneratedFiles = true;
    private const bool RemoveObsoleteGeneratedFiles = true;

    // Optional: specific names for individual objects/properties, for example:
    // new() { Kind = "Table", Schema = "dbo", Name = "Clients",
    //         ClassName = "Client", SetName = "Clients" }
    private static readonly ObjectNameOverride[] NameOverrides = Array.Empty<ObjectNameOverride>();
    // ================== END OF INPUT PARAMETERS ==================

    private static async Task<int> Main()
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var settings = new GeneratorSettings
            {
                ConnectionString = ConnectionString,
                OutputFolder = OutputFolder,
                RootNamespace = RootNamespace,
                ContextName = ContextName,
                MetadataTimeoutSeconds = MetadataTimeoutSeconds,
                SchemaFilter = SchemaFilter,
                IncludeTables = IncludeTables,
                IncludeViews = IncludeViews,
                IncludeStoredProcedures = IncludeStoredProcedures,
                ClassNaming = ClassNaming,
                SetNaming = SetNaming,
                NullableReferenceTypes = NullableReferenceTypes,
                ExistingModelPath = ExistingModelPath,
                DestinationProjectPath = DestinationProjectPath,
                OverwriteGeneratedFiles = OverwriteGeneratedFiles,
                RemoveObsoleteGeneratedFiles = RemoveObsoleteGeneratedFiles,
                NameOverrides = NameOverrides
            };
            var result = await Ef6ModelGenerator.GenerateAsync(settings, cancellation.Token, new ConsoleProgress());
            Console.WriteLine($"Tables     : {result.Tables}");
            Console.WriteLine($"Views      : {result.Views}");
            Console.WriteLine($"Procedures : {result.Procedures}");
            Console.WriteLine($"Foreign keys: {result.ForeignKeys}");
            Console.WriteLine($"Files      : {result.Files.Length} ({result.ChangedFiles} changes)");
            foreach (var warning in result.Warnings)
                Console.Error.WriteLine("WARNING: " + warning);
            Console.WriteLine($"Output folder: {result.OutputFolder}");
            Console.WriteLine("Done. See Report.txt for details.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Generation cancelled.");
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("ERROR: " + error.Message);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string message) => Console.WriteLine(message);
    }
}
