using SqlModelGenerator.Ef6;

namespace SqlModelGenerator;

internal static class Program
{
    // ==================== ВХОДНЫЕ ПАРАМЕТРЫ ====================
    // Все настройки генерации находятся здесь. Аргументы командной строки
    // и переменные окружения не переопределяют этот блок.

    // Подключение SQL Server для чтения схемы. Существующее значение сохранено.
    // Генератор не выполняет процедуры и не изменяет данные в базе.
    private const string ConnectionString = @"
            Data Source=localhost;
            Initial Catalog = OregonLC; 
            MultipleActiveResultSets=True;
            Encrypt=false;
            TrustServerCertificate=true;
            Integrated Security = True;
            Transaction Binding = Explicit Unbind;";

    // Отдельная папка для результата. При необходимости замените на полный путь.
    // Существующую модель из проекта сюда не помещайте: для неё есть поле ниже.
    private static readonly string OutputFolder = Path.Combine(AppContext.BaseDirectory, "Generated");

    // Имена namespace и DbContext в генерируемом C#-коде EF6.
    private const string RootNamespace = "GeneratedModel";
    private const string ContextName = "AppDbContext";

    // Тайм-аут каждого запроса метаданных, в секундах. Пустой фильтр = все схемы.
    private const int MetadataTimeoutSeconds = 120;
    private const string SchemaFilter = "";
    private const bool IncludeTables = true;
    private const bool IncludeViews = true;
    private const bool IncludeStoredProcedures = true;
    // FK и связи читаются всегда для выбранных таблиц.

    // Singular: Clients -> Client; Plural: Client -> Clients.
    // AsInDatabase сохраняет имя из базы. Для .NET Framework/C# 7.3 nullable
    // reference types должны быть выключены; nullable value types сохраняются.
    private const NamingStyle ClassNaming = NamingStyle.Singular;
    private const NamingStyle SetNaming = NamingStyle.Plural;
    private const bool NullableReferenceTypes = false;

    // Необязательно: папка/проект существующей модели EF6 (например, Миссури).
    // Если путь указан, сохраняются существующие имена, интерфейсы, методы,
    // навигации и контракты процедур. Исходная модель остаётся входным образцом.
    private const string ExistingModelPath = "";

    // Необязательно: .csproj приложения, которое будет использовать результат.
    // Нужен для определения допустимого синтаксиса C#; проект не изменяется.
    private const string DestinationProjectPath = "";

    // Обновляются/удаляются только ранее созданные и не изменённые вручную файлы.
    // При конфликте генератор остановится и сохранит существующие файлы.
    private const bool OverwriteGeneratedFiles = true;
    private const bool RemoveObsoleteGeneratedFiles = true;

    // Необязательно: точные имена для отдельных объектов/свойств, например:
    // new() { Kind = "Table", Schema = "dbo", Name = "Clients",
    //         ClassName = "Client", SetName = "Clients" }
    private static readonly ObjectNameOverride[] NameOverrides = Array.Empty<ObjectNameOverride>();
    // ================== КОНЕЦ ВХОДНЫХ ПАРАМЕТРОВ ==================

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
