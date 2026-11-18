namespace CommonClassLibrary

// Extracted from AnotherEdit CommonClassLibrary/PocoOutput.fs (2026-11-18).
// This standalone port retains the EF6 generation/source-compatibility path.

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

type PocoOutputFile =
    { RelativePath: string
      Before: string option
      BeforeHash: string option
      HasUtf8Bom: bool }

type PocoOutputChange =
    { File: PocoOutputFile
      Text: string
      Generated: bool }

module PocoOutput =
    let private gate = obj ()
    let private strictUtf8 = UTF8Encoding(false, true)
    let private hash (bytes: byte array) = Convert.ToHexString(SHA256.HashData bytes)
    let private maximumFileBytes = 4L * 1024L * 1024L
    let private manifestName = ".anotheredit-poco.json"

    let private rejectReparse (path: string) =
        let mutable current = path
        while not (String.IsNullOrEmpty current) do
            try
                if File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint) then
                    invalidOp ("Symbolic links and junctions are outside this output plan: " + current)
            with
            | :? FileNotFoundException | :? DirectoryNotFoundException -> ()
            current <- Path.GetDirectoryName current

    let private safePath (root: string) (relative: string) =
        let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
        if Path.IsPathRooted relative then invalidArg "relative" "Use a path relative to the selected output folder."
        let segments = relative.Split([| '/'; '\\' |])
        if segments |> Array.exists (fun segment ->
            String.IsNullOrWhiteSpace segment
            || segment <> segment.TrimEnd(' ', '.')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase ||| RegexOptions.CultureInvariant)) then
            invalidArg "relative" ("Invalid output name: " + relative)
        let target = Path.GetFullPath(Path.Combine(root, relative))
        let prefix = if Path.EndsInDirectorySeparator root then root else root + string Path.DirectorySeparatorChar
        if not (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
            invalidArg "relative" "Output path escapes the selected folder."
        rejectReparse target
        target

    let private decode (bytes: byte array) =
        let bom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble().AsSpan())
        let skip = if bom then 3 else 0
        strictUtf8.GetString(bytes, skip, bytes.Length - skip), bom

    let private readBytes path =
        if FileInfo(path).Length > maximumFileBytes then invalidOp ("Output file exceeds the review limit: " + path)
        let bytes = File.ReadAllBytes path
        if int64 bytes.Length > maximumFileBytes then invalidOp ("Output file exceeds the review limit: " + path)
        bytes

    let private inspect readText (root: string) (relative: string) =
        let path = safePath root relative
        if Directory.Exists path then invalidOp ("A folder already exists at " + relative)
        if File.Exists path then
            let bytes = readBytes path
            let text, bom =
                if readText then
                    let text, bom = decode bytes
                    Some text, bom
                else None, false
            { RelativePath = relative; Before = text; BeforeHash = Some(hash bytes); HasUtf8Bom = bom }
        else
            { RelativePath = relative; Before = None; BeforeHash = None; HasUtf8Bom = false }

    let Inspect root relative = inspect true root relative

    // Retirement reviews list paths. Apply and recovery verify and move the original bytes.
    let internal InspectRemoval root relative = inspect false root relative

    let private deserialize (file: PocoOutputFile) =
        let map = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        for body in Option.toList file.Before do
            let parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(body)
            if isNull parsed then invalidOp "The POCO ownership manifest is invalid."
            for pair in parsed do
                if String.IsNullOrEmpty pair.Value || pair.Value.Length <> 64 || not (map.TryAdd(pair.Key, pair.Value)) then
                    invalidOp "The POCO ownership manifest is invalid."
        map

    let OwnedFiles root =
        let file = Inspect root manifestName
        let map = deserialize file
        file, (map |> Seq.map (fun item -> item.Key, item.Value) |> Map.ofSeq)

    let IsOwned (ownership: Map<string, string>) (file: PocoOutputFile) =
        file.BeforeHash
        |> Option.exists (fun actual -> ownership |> Map.exists (fun name expected ->
            String.Equals(name, file.RelativePath, StringComparison.OrdinalIgnoreCase) && expected = actual))

    let private bytesFor bom (text: string) =
        let bytes = strictUtf8.GetBytes text
        if bom then Array.append (Encoding.UTF8.GetPreamble()) bytes else bytes

    let private sameVersion (file: PocoOutputFile) path =
        if File.Exists path then file.BeforeHash = Some(hash (readBytes path))
        else file.BeforeHash.IsNone && not (Directory.Exists path)

    /// Publish new code before retiring the reviewed originals, then commit the manifest.
    /// Recover earlier mutations only while their paths still match this plan.
    let ApplyWithRemovals(root: string, manifest: PocoOutputFile, changes: PocoOutputChange array,
                          removals: PocoOutputFile array) =
        lock gate (fun () ->
            if changes.Length = 0 then invalidOp "Select at least one file change."
            let root = Path.GetFullPath root
            if not (Directory.Exists root) then invalidOp "Choose an existing destination folder."
            TemporaryStorage.RequirePersistentDestination root
            let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
            let ownership = deserialize manifest
            if not (String.Equals(Path.GetFileName manifest.RelativePath, manifestName, StringComparison.OrdinalIgnoreCase)) then
                invalidArg "manifest" "Use the POCO ownership manifest for this output folder."
            let manifestPath = safePath root manifest.RelativePath
            let ownershipFolder = Path.GetDirectoryName manifestPath
            let ownershipKey path = Path.GetRelativePath(ownershipFolder, path)
            let reservePath relative =
                let path = safePath root relative
                if String.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase) || not (names.Add path) then
                    invalidOp ("Duplicate output path: " + relative)
                path
            let entries =
                changes |> Array.map (fun change ->
                    let path = reservePath change.File.RelativePath
                    let bytes = bytesFor change.File.HasUtf8Bom change.Text
                    if int64 bytes.Length > maximumFileBytes then invalidOp ("Generated file exceeds the review limit: " + change.File.RelativePath)
                    if change.Generated then ownership.[ownershipKey path] <- hash bytes
                    else ownership.Remove(ownershipKey path) |> ignore
                    change.File, path, Some bytes)
            let retired =
                removals |> Array.map (fun file ->
                    let path = reservePath file.RelativePath
                    if file.BeforeHash.IsNone then invalidOp ("Retiring a file requires a reviewed original: " + file.RelativePath)
                    ownership.Remove(ownershipKey path) |> ignore
                    file, path, None)
            let manifestBytes = bytesFor false (JsonSerializer.Serialize(ownership, JsonSerializerOptions(WriteIndented = true)))
            let all = Array.concat [ entries; retired; [| manifest, manifestPath, Some manifestBytes |] ]
            for file, path, _ in all do
                if not (sameVersion file path) then invalidOp ("File changed since preview: " + file.RelativePath)
                if not (Directory.Exists(Path.GetDirectoryName path)) then invalidOp ("Output folder is missing for " + file.RelativePath)
            let staging = Path.Combine(root, ".anotheredit-poco-" + Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory staging |> ignore
            let applied = ResizeArray<string * string option * string option>()
            let locks = ResizeArray<FileStream>()
            let published = ResizeArray<FileStream>()
            let releasePublished () =
                for guard in published do guard.Dispose()
                published.Clear()
            let mutable retainRecovery = false
            try
                try
                    for index in 0 .. all.Length - 1 do
                        let file, path, bytes = all.[index]
                        bytes |> Option.iter (fun content -> File.WriteAllBytes(Path.Combine(staging, string index + ".new"), content))
                        if file.BeforeHash.IsSome then
                            rejectReparse path
                            let guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read ||| FileShare.Delete)
                            locks.Add guard
                            if Some(Convert.ToHexString(SHA256.HashData guard)) <> file.BeforeHash then
                                invalidOp ("File changed since preview: " + file.RelativePath)
                    for index in 0 .. all.Length - 1 do
                        let file, path, bytes = all.[index]
                        rejectReparse path
                        if not (sameVersion file path) then invalidOp ("File changed since preview: " + file.RelativePath)
                        let staged = Path.Combine(staging, string index + ".new")
                        let backup =
                            if file.BeforeHash.IsSome then
                                Some(Path.Combine(staging, string index + ".backup"))
                            else None
                        let writtenHash = bytes |> Option.map hash
                        match writtenHash, backup with
                        | Some _, Some original -> File.Replace(staged, path, original)
                        | Some _, None -> File.Move(staged, path)
                        | None, Some original -> File.Move(path, original)
                        | None, None -> invalidOp "Retiring a file requires a reviewed original."
                        applied.Add(path, backup, writtenHash)
                        if removals.Length > 0 && index < entries.Length then
                            rejectReparse path
                            let guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                            published.Add guard
                            if Some(Convert.ToHexString(SHA256.HashData guard)) <> writtenHash then
                                invalidOp ("Published output could not be verified: " + file.RelativePath)
                    changes.Length + removals.Length
                with error ->
                    releasePublished ()
                    for path, backup, writtenHash in Seq.rev applied do
                        try
                            rejectReparse path
                            match writtenHash, backup with
                            | Some expected, _ ->
                                if not (File.Exists path) || hash (readBytes path) <> expected then
                                    invalidOp "The published path changed during recovery."
                                match backup with
                                // The original read guard permits rename/delete, while File.Replace
                                // also asks for write access to the guarded backup on Windows.
                                | Some original -> File.Move(original, path, true)
                                | None -> File.Delete path
                            | None, Some original -> File.Move(original, path)
                            | None, None -> invalidOp "The retired file has no recovery copy."
                        with _ -> retainRecovery <- true
                    if retainRecovery then
                        raise (IOException("Some files could not be restored. Recovery files are in " + staging, error))
                    reraise ()
            finally
                releasePublished ()
                for guard in locks do guard.Dispose()
                if not retainRecovery then
                    try
                        for file in Directory.EnumerateFiles staging do File.Delete file
                        Directory.Delete staging
                    with _ -> ())

    let Apply(root: string, manifest: PocoOutputFile, changes: PocoOutputChange array) =
        ApplyWithRemovals(root, manifest, changes, [||])

    let ApplyGenerated(root: string, outputFolder: string, manifest: PocoOutputFile,
                       changes: PocoOutputChange array, removals: PocoOutputFile array) =
        lock gate (fun () ->
            let root = Path.TrimEndingDirectorySeparator(Path.GetFullPath root)
            let outputFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath outputFolder)
            let relative = Path.GetRelativePath(root, outputFolder)
            if relative <> "." then safePath root relative |> ignore
            rejectReparse root
            rejectReparse outputFolder
            TemporaryStorage.RequirePersistentDestination outputFolder
            let folders =
                Array.append [| outputFolder; Path.GetDirectoryName(safePath root manifest.RelativePath) |]
                    (changes |> Array.map (fun change -> Path.GetDirectoryName(safePath root change.File.RelativePath)))
                |> Array.distinct
            let created = ResizeArray<string>()
            let ensureFolder path =
                let missing = ResizeArray<string>()
                let mutable current = path
                while not (Directory.Exists current) && not (String.IsNullOrEmpty current) do
                    if File.Exists current then invalidOp ("A file occupies output folder " + current)
                    missing.Add current
                    current <- Path.GetDirectoryName current
                rejectReparse path
                Directory.CreateDirectory path |> ignore
                for folder in Seq.rev missing do created.Add folder
            try
                for folder in folders do ensureFolder folder
                ApplyWithRemovals(root, manifest, changes, removals)
            with _ ->
                for folder in Seq.rev created do
                    try
                        if Directory.Exists folder && Seq.isEmpty (Directory.EnumerateFileSystemEntries folder) then
                            Directory.Delete folder
                    with _ -> ()
                reraise ())
