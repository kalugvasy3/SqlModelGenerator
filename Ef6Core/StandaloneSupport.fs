namespace CommonClassLibrary

open System
open System.IO

/// SQL Server branches extracted from AnotherEdit SqlExecution.fs, SqlProviders.
/// The standalone tool deliberately has no provider registry or driver loader.
module SqlProviders =
    let private requireSqlServer (providerId: string) =
        if not (String.Equals(providerId, "sqlserver", StringComparison.OrdinalIgnoreCase)) then
            invalidArg "providerId" "This standalone generator supports SQL Server only."

    let QuoteIdentifier providerId (name: string) =
        requireSqlServer providerId
        if String.IsNullOrEmpty name || name.IndexOf('\000') >= 0 then
            invalidArg "name" "An SQL identifier must be non-empty and contain no NUL."
        "[" + name.Replace("]", "]]") + "]"

    let QuoteStringLiteral providerId (value: string) =
        requireSqlServer providerId
        "'" + value.Replace("'", "''") + "'"

/// The safe local project-reference resolver from AnotherEdit PocoOutput.fs.
module PocoProjectIntegration =
    let internal localPath folder (value: string) =
        if String.IsNullOrWhiteSpace value || value.IndexOfAny([| '*'; '?'; ';'; '$'; '%'; '@' |]) >= 0 then None
        else
            try Some(Path.GetFullPath(value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), folder))
            with :? ArgumentException | :? NotSupportedException -> None

/// Output-only adapter: the console owns the explicit destination directory.
/// No application scratch store, background cleaner, timer or global state is used.
/// Explicit test directories under the OS temporary folder are valid destinations.
module TemporaryStorage =
    let RequirePersistentDestination(path: string) =
        if String.IsNullOrWhiteSpace path then
            invalidArg "path" "Choose a destination folder."
        let fullPath = Path.GetFullPath path
        if File.Exists fullPath then
            invalidArg "path" "A file occupies the selected destination folder."
