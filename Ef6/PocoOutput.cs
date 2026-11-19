using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommonClassLibrary;

public sealed record PocoOutputFile(string RelativePath, string? Before, string? BeforeHash, bool HasUtf8Bom);
public sealed record PocoOutputChange(PocoOutputFile File, string Text, bool Generated);

/// <summary>
/// Publishes reviewed files atomically and retires only reviewed originals. The ownership manifest
/// and byte-level conflict checks are compatible with the tested AnotherEdit EF6 generator.
/// </summary>
public static class PocoOutput
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const long MaximumFileBytes = 4L * 1024L * 1024L;
    private const string ManifestName = ".anotheredit-poco.json";

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void RejectReparse(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Symbolic links and junctions are outside this output plan: " + current);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing leaf is expected for new generated files. Existing ancestors are checked too.
            }
        }
    }

    private static string SafePath(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.IsPathRooted(relative))
            throw new ArgumentException("Use a path relative to the selected output folder.", nameof(relative));
        var segments = relative.Split(['/', '\\']);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment)
            || segment != segment.TrimEnd(' ', '.')
            || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || Regex.IsMatch(segment, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw new ArgumentException("Invalid output name: " + relative, nameof(relative));
        var target = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path escapes the selected folder.", nameof(relative));
        RejectReparse(target);
        return target;
    }

    private static (string Text, bool Bom) Decode(byte[] bytes)
    {
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble().AsSpan());
        var skip = bom ? 3 : 0;
        return (StrictUtf8.GetString(bytes, skip, bytes.Length - skip), bom);
    }

    private static byte[] ReadBytes(string path)
    {
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidOperationException("Output file exceeds the review limit: " + path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > MaximumFileBytes)
            throw new InvalidOperationException("Output file exceeds the review limit: " + path);
        return bytes;
    }

    private static PocoOutputFile Inspect(bool readText, string root, string relative)
    {
        var path = SafePath(root, relative);
        if (Directory.Exists(path))
            throw new InvalidOperationException("A folder already exists at " + relative);
        if (!File.Exists(path))
            return new(relative, null, null, false);
        var bytes = ReadBytes(path);
        var decoded = readText ? Decode(bytes) : ((string?)null, false);
        return new(relative, decoded.Item1, Hash(bytes), decoded.Item2);
    }

    public static PocoOutputFile Inspect(string root, string relative) => Inspect(true, root, relative);

    // Retirement reviews paths; apply/recovery verify and move the original bytes without decoding.
    internal static PocoOutputFile InspectRemoval(string root, string relative) => Inspect(false, root, relative);

    private static Dictionary<string, string> Deserialize(PocoOutputFile file)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (file.Before is null)
            return map;
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(file.Before);
        if (parsed is null)
            throw new InvalidOperationException("The POCO ownership manifest is invalid.");
        foreach (var pair in parsed)
            if (string.IsNullOrEmpty(pair.Value) || pair.Value.Length != 64 || !map.TryAdd(pair.Key, pair.Value))
                throw new InvalidOperationException("The POCO ownership manifest is invalid.");
        return map;
    }

    public static (PocoOutputFile Manifest, IReadOnlyDictionary<string, string> Owned) OwnedFiles(string root)
    {
        var file = Inspect(root, ManifestName);
        // F# Map enumeration was ordinal and sorted; preserve that observable ordering.
        var sorted = new SortedDictionary<string, string>(Deserialize(file), StringComparer.Ordinal);
        return (file, sorted);
    }

    public static bool IsOwned(IReadOnlyDictionary<string, string> ownership, PocoOutputFile file) =>
        file.BeforeHash is not null && ownership.Any(pair =>
            string.Equals(pair.Key, file.RelativePath, StringComparison.OrdinalIgnoreCase) && pair.Value == file.BeforeHash);

    private static byte[] BytesFor(bool bom, string text)
    {
        var bytes = StrictUtf8.GetBytes(text);
        return bom ? [.. Encoding.UTF8.GetPreamble(), .. bytes] : bytes;
    }

    private static bool SameVersion(PocoOutputFile file, string path) => File.Exists(path)
        ? file.BeforeHash == Hash(ReadBytes(path))
        : file.BeforeHash is null && !Directory.Exists(path);

    /// <summary>Publish code, retire reviewed originals, then commit the manifest; recover only unchanged paths.</summary>
    public static int ApplyWithRemovals(string root, PocoOutputFile manifest, PocoOutputChange[] changes, PocoOutputFile[] removals)
    {
        lock (Gate)
        {
            if (changes.Length == 0)
                throw new InvalidOperationException("Select at least one file change.");
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root))
                throw new InvalidOperationException("Choose an existing destination folder.");
            TemporaryStorage.RequirePersistentDestination(root);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ownership = Deserialize(manifest);
            if (!string.Equals(Path.GetFileName(manifest.RelativePath), ManifestName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use the POCO ownership manifest for this output folder.", nameof(manifest));
            var manifestPath = SafePath(root, manifest.RelativePath);
            var ownershipFolder = Path.GetDirectoryName(manifestPath)!;
            string OwnershipKey(string path) => Path.GetRelativePath(ownershipFolder, path);
            string ReservePath(string relative)
            {
                var path = SafePath(root, relative);
                if (string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase) || !names.Add(path))
                    throw new InvalidOperationException("Duplicate output path: " + relative);
                return path;
            }

            var entries = changes.Select(change =>
            {
                var path = ReservePath(change.File.RelativePath);
                var bytes = BytesFor(change.File.HasUtf8Bom, change.Text);
                if (bytes.LongLength > MaximumFileBytes)
                    throw new InvalidOperationException("Generated file exceeds the review limit: " + change.File.RelativePath);
                if (change.Generated)
                    ownership[OwnershipKey(path)] = Hash(bytes);
                else
                    ownership.Remove(OwnershipKey(path));
                return (File: change.File, Path: path, Bytes: (byte[]?)bytes);
            }).ToArray();
            var retired = removals.Select(file =>
            {
                var path = ReservePath(file.RelativePath);
                if (file.BeforeHash is null)
                    throw new InvalidOperationException("Retiring a file requires a reviewed original: " + file.RelativePath);
                ownership.Remove(OwnershipKey(path));
                return (File: file, Path: path, Bytes: (byte[]?)null);
            }).ToArray();
            var manifestBytes = BytesFor(false, JsonSerializer.Serialize(ownership, new JsonSerializerOptions { WriteIndented = true }));
            var all = entries.Concat(retired).Append((manifest, manifestPath, (byte[]?)manifestBytes)).ToArray();
            foreach (var (file, path, _) in all)
            {
                if (!SameVersion(file, path))
                    throw new InvalidOperationException("File changed since preview: " + file.RelativePath);
                if (!Directory.Exists(Path.GetDirectoryName(path)))
                    throw new InvalidOperationException("Output folder is missing for " + file.RelativePath);
            }

            var staging = Path.Combine(root, ".anotheredit-poco-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var applied = new List<(string Path, string? Backup, string? WrittenHash)>();
            var locks = new List<FileStream>();
            var published = new List<FileStream>();
            void ReleasePublished()
            {
                foreach (var guard in published) guard.Dispose();
                published.Clear();
            }
            var retainRecovery = false;
            try
            {
                try
                {
                    for (var index = 0; index < all.Length; index++)
                    {
                        var (file, path, bytes) = all[index];
                        if (bytes is not null)
                            File.WriteAllBytes(Path.Combine(staging, index + ".new"), bytes);
                        if (file.BeforeHash is not null)
                        {
                            RejectReparse(path);
                            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                            locks.Add(guard);
                            if (Convert.ToHexString(SHA256.HashData(guard)) != file.BeforeHash)
                                throw new InvalidOperationException("File changed since preview: " + file.RelativePath);
                        }
                    }
                    for (var index = 0; index < all.Length; index++)
                    {
                        var (file, path, bytes) = all[index];
                        RejectReparse(path);
                        if (!SameVersion(file, path))
                            throw new InvalidOperationException("File changed since preview: " + file.RelativePath);
                        var staged = Path.Combine(staging, index + ".new");
                        var backup = file.BeforeHash is not null ? Path.Combine(staging, index + ".backup") : null;
                        var writtenHash = bytes is not null ? Hash(bytes) : null;
                        if (writtenHash is not null && backup is not null)
                            File.Replace(staged, path, backup);
                        else if (writtenHash is not null)
                            File.Move(staged, path);
                        else if (backup is not null)
                            File.Move(path, backup);
                        else
                            throw new InvalidOperationException("Retiring a file requires a reviewed original.");
                        applied.Add((path, backup, writtenHash));
                        if (removals.Length > 0 && index < entries.Length)
                        {
                            RejectReparse(path);
                            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            published.Add(guard);
                            if (Convert.ToHexString(SHA256.HashData(guard)) != writtenHash)
                                throw new InvalidOperationException("Published output could not be verified: " + file.RelativePath);
                        }
                    }
                    return changes.Length + removals.Length;
                }
                catch (Exception error)
                {
                    ReleasePublished();
                    foreach (var (path, backup, writtenHash) in applied.AsEnumerable().Reverse())
                    {
                        try
                        {
                            RejectReparse(path);
                            if (writtenHash is not null)
                            {
                                if (!File.Exists(path) || Hash(ReadBytes(path)) != writtenHash)
                                    throw new InvalidOperationException("The published path changed during recovery.");
                                // File.Move permits the original read guard's rename/delete sharing.
                                if (backup is not null) File.Move(backup, path, true);
                                else File.Delete(path);
                            }
                            else if (backup is not null)
                                File.Move(backup, path);
                            else
                                throw new InvalidOperationException("The retired file has no recovery copy.");
                        }
                        catch
                        {
                            retainRecovery = true;
                        }
                    }
                    if (retainRecovery)
                        throw new IOException("Some files could not be restored. Recovery files are in " + staging, error);
                    throw;
                }
            }
            finally
            {
                ReleasePublished();
                foreach (var guard in locks) guard.Dispose();
                if (!retainRecovery)
                {
                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                        Directory.Delete(staging);
                    }
                    catch
                    {
                        // Publication/recovery has completed; a locked staging file is left intact.
                    }
                }
            }
        }
    }

    public static int Apply(string root, PocoOutputFile manifest, PocoOutputChange[] changes) =>
        ApplyWithRemovals(root, manifest, changes, []);

    public static int ApplyGenerated(string root, string outputFolder, PocoOutputFile manifest,
        PocoOutputChange[] changes, PocoOutputFile[] removals)
    {
        lock (Gate)
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            outputFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputFolder));
            var relative = Path.GetRelativePath(root, outputFolder);
            if (relative != ".") _ = SafePath(root, relative);
            RejectReparse(root);
            RejectReparse(outputFolder);
            TemporaryStorage.RequirePersistentDestination(outputFolder);
            var folders = new[] { outputFolder, Path.GetDirectoryName(SafePath(root, manifest.RelativePath))! }
                .Concat(changes.Select(change => Path.GetDirectoryName(SafePath(root, change.File.RelativePath))!))
                .Distinct(StringComparer.Ordinal).ToArray();
            var created = new List<string>();
            void EnsureFolder(string path)
            {
                var missing = new List<string>();
                for (string? current = path; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current))
                {
                    if (File.Exists(current)) throw new InvalidOperationException("A file occupies output folder " + current);
                    missing.Add(current);
                }
                RejectReparse(path);
                Directory.CreateDirectory(path);
                created.AddRange(missing.AsEnumerable().Reverse());
            }
            try
            {
                foreach (var folder in folders) EnsureFolder(folder);
                return ApplyWithRemovals(root, manifest, changes, removals);
            }
            catch
            {
                foreach (var folder in created.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                            Directory.Delete(folder);
                    }
                    catch { }
                }
                throw;
            }
        }
    }
}
