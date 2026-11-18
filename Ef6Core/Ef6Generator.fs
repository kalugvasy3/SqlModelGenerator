namespace SqlModelGenerator.Ef6

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open CommonClassLibrary

type NamingStyle =
    | AsInDatabase = 0
    | Singular = 1
    | Plural = 2

/// Optional explicit names use database names, not generated C# identifiers, as keys.
type ObjectNameOverride() =
    member val Kind = "Table" with get, set
    member val Schema = "dbo" with get, set
    member val Name = "" with get, set
    member val ClassName = "" with get, set
    member val SetName = "" with get, set
    member val Properties = Dictionary<string, string>(StringComparer.Ordinal) with get, set

/// C#-friendly input boundary. The generation target is deliberately fixed to EF6.
type GeneratorSettings() =
    member val ConnectionString = "" with get, set
    member val OutputFolder = "" with get, set
    member val RootNamespace = "GeneratedModel" with get, set
    member val ContextName = "AppDbContext" with get, set
    member val MetadataTimeoutSeconds = 120 with get, set
    member val SchemaFilter = "" with get, set
    member val IncludeTables = true with get, set
    member val IncludeViews = true with get, set
    member val IncludeStoredProcedures = true with get, set
    member val ClassNaming = NamingStyle.Singular with get, set
    member val SetNaming = NamingStyle.Plural with get, set
    member val NullableReferenceTypes = false with get, set
    member val ExistingModelPath = "" with get, set
    member val DestinationProjectPath = "" with get, set
    member val OverwriteGeneratedFiles = true with get, set
    member val RemoveObsoleteGeneratedFiles = true with get, set
    member val NameOverrides: ObjectNameOverride array = [||] with get, set

type GenerationReport =
    { OutputFolder: string
      Tables: int
      Views: int
      Procedures: int
      ForeignKeys: int
      Files: string array
      ChangedFiles: int
      Warnings: string array }

module private Generator =
    let naming value =
        match value with
        | NamingStyle.AsInDatabase -> PocoNaming.AsInDatabase
        | NamingStyle.Singular -> PocoNaming.Singular
        | NamingStyle.Plural -> PocoNaming.Plural
        | _ -> invalidArg "naming" "Choose AsInDatabase, Singular or Plural."

    let overrides (values: ObjectNameOverride array) =
        let mutable result = Map.empty
        for value in (if isNull values then [||] else values) do
            if isNull (box value) || String.IsNullOrWhiteSpace value.Name then
                invalidArg "NameOverrides" "Each name override needs a database object name."
            let kind =
                match value.Kind.ToUpperInvariant() with
                | "TABLE" -> SqlCatalogObjectKind.Table
                | "VIEW" -> SqlCatalogObjectKind.View
                | "PROCEDURE" -> SqlCatalogObjectKind.Procedure
                | _ -> invalidArg "NameOverrides" "An override kind must be Table, View or Procedure."
            let key = kind, value.Schema, value.Name
            if result.ContainsKey key then invalidArg "NameOverrides" "A database object has duplicate naming overrides."
            let properties =
                if isNull value.Properties then Map.empty
                else value.Properties |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
            result <- result.Add(key, { ClassName = value.ClassName; SetName = value.SetName; Properties = properties })
        result

    let inside parent child =
        let parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath parent)
        let child = Path.TrimEndingDirectorySeparator(Path.GetFullPath child)
        child.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(parent + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)

    let prepare (settings: GeneratorSettings) =
        if isNull (box settings) then nullArg "settings"
        if String.IsNullOrWhiteSpace settings.ConnectionString then invalidArg "ConnectionString" "Set the SQL Server connection string in Program.cs."
        if String.IsNullOrWhiteSpace settings.OutputFolder then invalidArg "OutputFolder" "Set the output folder in Program.cs."
        let output = Path.GetFullPath settings.OutputFolder
        if String.Equals(Path.TrimEndingDirectorySeparator output, Path.GetPathRoot output, StringComparison.OrdinalIgnoreCase) then
            invalidArg "OutputFolder" "Choose a dedicated model folder, not the filesystem root."
        if settings.MetadataTimeoutSeconds <= 0 then invalidArg "MetadataTimeoutSeconds" "The metadata timeout must be positive."
        if not (settings.IncludeTables || settings.IncludeViews || settings.IncludeStoredProcedures) then
            invalidArg "settings" "Select at least one object category in Program.cs."
        let options =
            { Namespace = settings.RootNamespace; ContextName = settings.ContextName; Target = PocoTarget.Ef6
              ClassNaming = naming settings.ClassNaming; SetNaming = naming settings.SetNaming
              NullableReferenceTypes = settings.NullableReferenceTypes; Overrides = overrides settings.NameOverrides }
        output, options

    let publish output (settings: GeneratorSettings) (generation: PocoGeneration) (token: CancellationToken) =
        token.ThrowIfCancellationRequested()
        Directory.CreateDirectory output |> ignore
        let manifest, owned = PocoOutput.OwnedFiles output
        let paths = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        for file in generation.Files do
            if not (paths.Add file.FileName) then invalidOp ("Duplicate generated file: " + file.FileName)
        // Old console output used other filenames and directories. Do not mix
        // that untracked model with the new context or silently delete it.
        let folders = Stack<string>()
        folders.Push output
        while folders.Count > 0 do
            token.ThrowIfCancellationRequested()
            let folder = folders.Pop()
            if File.GetAttributes(folder).HasFlag(FileAttributes.ReparsePoint) then
                invalidOp "Choose an output directory without symbolic links or junctions."
            for child in Directory.EnumerateDirectories folder do folders.Push child
            for file in Directory.EnumerateFiles(folder, "*.cs") do
                let relative = Path.GetRelativePath(output, file)
                if not (paths.Contains relative) && not (owned.ContainsKey relative) then
                    invalidOp ("The output contains C# files from another model: " + relative + ". Choose an empty output folder; existing files were preserved.")
        let changes = ResizeArray<PocoOutputChange>()
        for generated in generation.Files do
            token.ThrowIfCancellationRequested()
            let current = PocoOutput.Inspect output generated.FileName
            if current.Before <> Some generated.Text then
                if current.BeforeHash.IsSome then
                    if not settings.OverwriteGeneratedFiles then invalidOp ("Overwriting generated files is disabled: " + generated.FileName)
                    if not (PocoOutput.IsOwned owned current) then
                        invalidOp ("The output contains an unowned or manually edited file: " + generated.FileName + ". Choose an empty output folder or preserve that file separately.")
                changes.Add { File = current; Text = generated.Text; Generated = true }
        let removals = ResizeArray<PocoOutputFile>()
        if settings.RemoveObsoleteGeneratedFiles then
            for KeyValue(path, _) in owned do
                token.ThrowIfCancellationRequested()
                if not (paths.Contains path) then
                    let current = PocoOutput.Inspect output path
                    if current.BeforeHash.IsSome then
                        if not (PocoOutput.IsOwned owned current) then invalidOp ("An obsolete generated file was manually edited: " + path)
                        removals.Add current
        // The original atomic publisher requires a write when retiring files.
        // Every normal generation includes Report.txt, so keep one stable entry
        // in that rare removal-only case; no directory is recursively cleared.
        if changes.Count = 0 && removals.Count > 0 then
            let report = generation.Files |> Array.find (fun file -> file.FileName = "Report.txt")
            changes.Add { File = PocoOutput.Inspect output report.FileName; Text = report.Text; Generated = true }
        token.ThrowIfCancellationRequested()
        if changes.Count > 0 then
            PocoOutput.ApplyGenerated(output, output, manifest, changes.ToArray(), removals.ToArray()) |> ignore
        changes.Count + removals.Count

/// The console owns cancellation. This service closes metadata resources before publication.
type Ef6ModelGenerator private () =
    static member GenerateAsync(settings: GeneratorSettings, token: CancellationToken,
                               progress: IProgress<string>) : Task<GenerationReport> = task {
        let output, options = Generator.prepare settings
        let report message = if not (isNull progress) then progress.Report message
        let existing =
            if String.IsNullOrWhiteSpace settings.ExistingModelPath then None
            else
                token.ThrowIfCancellationRequested()
                report "Reading the existing EF6 model..."
                let scan = PocoExistingModel.Scan(settings.ExistingModelPath, token)
                if scan.Target <> Some PocoTarget.Ef6 then invalidOp "ExistingModelPath must identify one complete EF6 model."
                if Generator.inside scan.Root output || Generator.inside output scan.Root then
                    invalidOp "Generate to a separate folder outside the existing model. The input model is read-only."
                Some scan
        report "Reading SQL Server metadata (tables, views, procedures and relationships)..."
        let! snapshot =
            SqlServerSchema.LoadAsync(settings.ConnectionString, settings.MetadataTimeoutSeconds, settings.SchemaFilter,
                                      settings.IncludeTables, settings.IncludeViews, settings.IncludeStoredProcedures, token)
        token.ThrowIfCancellationRequested()
        if snapshot.Objects.Length = 0 then
            invalidOp "No selected objects were visible. Check the database, schema filter and metadata permissions; existing output was preserved."
        report "Generating the EF6 model..."
        let generation =
            match existing with
            | None -> PocoCodeGeneration.Generate snapshot options
            | Some scan ->
                let project = if String.IsNullOrWhiteSpace settings.DestinationProjectPath then None else Some settings.DestinationProjectPath
                let compatible = PocoExistingModel.GenerateCompatibleForProject(scan, snapshot, options, project, token)
                PocoExistingModel.ApplyGeneratedNames(scan.Namespace, scan.ContextName, options.Namespace, options.ContextName, compatible, token)
        let count kind = snapshot.Objects |> Array.filter (fun item -> item.Item.Kind = kind) |> Array.length
        let tables, views, procedures = count SqlCatalogObjectKind.Table, count SqlCatalogObjectKind.View, count SqlCatalogObjectKind.Procedure
        let warnings = generation.Warnings |> Array.distinct
        let text = StringBuilder()
        text.AppendLine("SqlModelGenerator - Entity Framework 6") |> ignore
        text.AppendLine("Namespace: " + options.Namespace).AppendLine("Context: " + options.ContextName) |> ignore
        text.AppendLine(sprintf "Tables: %d\nViews: %d\nProcedures: %d\nRelationships: %d" tables views procedures snapshot.Relationships.Length) |> ignore
        text.AppendLine("The database was inspected through metadata queries; procedures were not executed.") |> ignore
        for warning in warnings do text.AppendLine("WARNING: " + warning) |> ignore
        let complete = { generation with Files = Array.append generation.Files [| { FileName = "Report.txt"; Text = text.ToString() } |] }
        report "Publishing generated files..."
        let changed = Generator.publish output settings complete token
        return { OutputFolder = output; Tables = tables; Views = views; Procedures = procedures
                 ForeignKeys = snapshot.Relationships.Length
                 Files = complete.Files |> Array.map _.FileName; ChangedFiles = changed; Warnings = warnings }
    }
