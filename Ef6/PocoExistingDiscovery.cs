using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CommonClassLibrary;

public static partial class PocoExistingModel
{
    public static (string Provider, string Evidence)? DetectProvider(PocoExistingScan scan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var calls = new Dictionary<string, string>(StringComparer.Ordinal) { ["UseSqlServer"] = "sqlserver", ["UseSqlite"] = "sqlite", ["UseNpgsql"] = "postgresql", ["UseMySql"] = "mysql", ["UseMySQL"] = "mysql", ["UseMariaDb"] = "mariadb", ["UseOracle"] = "oracle", ["UseDb2"] = "db2", ["UseDB2"] = "db2", ["UseJet"] = "oledb" };
        var source = scan.Files.SelectMany(f => { token.ThrowIfCancellationRequested(); return CSharpSyntaxTree.ParseText(f.Text, cancellationToken: token).GetRoot(token).DescendantNodes().OfType<InvocationExpressionSyntax>().Select(c => calls.GetValueOrDefault(MethodName(c))).Where(x => x != null); }).Distinct().ToArray();
        string? Invariant(string name) => name.Trim().ToLowerInvariant() switch
        {
            "system.data.sqlclient" or "microsoft.data.sqlclient" => "sqlserver", "system.data.sqlite" or "system.data.sqlite.ef6" or "microsoft.data.sqlite" => "sqlite", "npgsql" => "postgresql", "mysql.data.mysqlclient" or "mysqlconnector" => "mysql", "oracle.manageddataaccess.client" or "oracle.dataaccess.client" => "oracle", "ibm.data.db2" or "ibm.data.db2.core" => "db2", "system.data.oledb" => "oledb", _ => null
        };
        XDocument? ReadXml(string path)
        {
            token.ThrowIfCancellationRequested();
            try { if (!File.Exists(path) || new FileInfo(path).Length > 4L * 1024 * 1024) return null; using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }); return XDocument.Load(reader); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or XmlException) { return null; }
        }
        var configuration = new[] { scan.Root, scan.ProjectFile == null ? scan.Root : Path.GetDirectoryName(scan.ProjectFile)! }.Distinct().SelectMany(folder => new[] { "App.config", "app.config", "Web.config", "web.config" }.SelectMany(file => ReadXml(Path.Combine(folder, file))?.Descendants().SelectMany(e => new[] { "providerName", "invariantName" }.Select(n => e.Attribute(n)?.Value)).Where(x => x != null).Select(x => Invariant(x!)).Where(x => x != null) ?? Enumerable.Empty<string?>())).Distinct().ToArray();
        var project = scan.ProjectFile == null ? null : ReadXml(scan.ProjectFile);
        var package = project?.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "Reference").Select(e => e.Attribute("Include")?.Value.Split(',')[0].Trim().ToLowerInvariant()).Select(n => n switch
        {
            "microsoft.entityframeworkcore.sqlserver" or "entityframework.sqlserver" => "sqlserver", "microsoft.entityframeworkcore.sqlite" or "system.data.sqlite.ef6" => "sqlite", "npgsql.entityframeworkcore.postgresql" or "entityframework6.npgsql" => "postgresql", "pomelo.entityframeworkcore.mysql" or "mysql.entityframeworkcore" or "mysql.data.entityframework" => "mysql", "oracle.entityframeworkcore" or "oracle.manageddataaccess.entityframework" => "oracle", "ibm.entityframeworkcore" or "ibm.entityframeworkcore-lnx" => "db2", "entityframeworkcore.jet" => "oledb", _ => null
        }).Where(x => x != null).Distinct().ToArray() ?? Array.Empty<string?>();
        var chosen = source.Length > 0 ? source : configuration.Length > 0 ? configuration : package;
        var evidence = source.Length > 0 ? "Detected from the model's provider configuration." : configuration.Length > 0 ? "Detected from the project's provider configuration." : "Detected from the project's EF provider reference.";
        return chosen.Length == 1 ? (chosen[0]!, evidence) : null;
    }
    private static bool IsLink(string path)
    {
        var current = Directory.Exists(path) ? new DirectoryInfo(path) : new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current != null) { if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true; current = current.Parent; }
        return File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    }
    private static string[]? ReadProjectReferences(string project)
    {
        using var stream = File.OpenRead(project); if (stream.Length > 1048576) return null;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader).Descendants().Where(n => n.Name.LocalName == "ProjectReference").Select(n => n.Attribute("Include")?.Value).Where(x => x != null)
            .Select(x => PocoProjectIntegration.LocalPath(Path.GetDirectoryName(project)!, x!)).Where(x => x != null).Select(x => x!).ToArray();
    }
    private static readonly Regex SolutionProjectLine = new("^\\s*Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.csproj)\",", RegexOptions.IgnoreCase);
    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        return !(Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
    private static T[] Bounded<T>(int limit, IEnumerable<T> items)
    {
        var found = items.Take(limit + 1).ToArray();
        if (found.Length > limit) throw new InvalidOperationException("The nearby project search exceeded its limit.");
        return found;
    }
    private static bool RepositoryRoot(DirectoryInfo folder) => Directory.Exists(Path.Combine(folder.FullName, ".git")) || File.Exists(Path.Combine(folder.FullName, ".git"));
    public static string? FindConsumerProject(PocoExistingScan scan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (scan.ProjectFile == null) return null;
        var sourceProject = Path.GetFullPath(scan.ProjectFile); var sourceFolder = new DirectoryInfo(Path.GetDirectoryName(sourceProject)!);
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        string[] SolutionProjects(FileInfo solution)
        {
            token.ThrowIfCancellationRequested(); using var stream = solution.OpenRead(); if (stream.Length > 1048576) throw new InvalidOperationException("The nearby solution exceeded the 1 MiB limit.");
            using var reader = new StreamReader(stream); var found = new List<string>();
            while (reader.ReadLine() is { } line) { token.ThrowIfCancellationRequested(); var match = SolutionProjectLine.Match(line); if (match.Success && PocoProjectIntegration.LocalPath(solution.DirectoryName!, match.Groups[1].Value) is { } path) found.Add(path); if (found.Count > 128) throw new InvalidOperationException("The nearby project search exceeded its limit."); }
            return found.ToArray();
        }
        string[]? FindSolution(int depth, DirectoryInfo? folder)
        {
            token.ThrowIfCancellationRequested(); if (depth == 4 || folder?.Parent == null) return null;
            var projects = Bounded(16, folder.EnumerateFiles("*.sln", options)).Select(SolutionProjects).Where(p => p.Any(x => SamePath(x, sourceProject))).ToArray();
            var local = projects.Length == 0 ? null : projects.SelectMany(x => x).Where(p => Within(folder.FullName, p)).ToArray();
            var parent = RepositoryRoot(folder) ? null : FindSolution(depth + 1, folder.Parent);
            return local != null && parent != null ? local.Concat(parent).ToArray() : local ?? parent;
        }
        IEnumerable<DirectoryInfo> NearbyFolders(int depth, DirectoryInfo folder)
        {
            token.ThrowIfCancellationRequested(); yield return folder;
            if (depth >= 2) yield break;
            foreach (var child in folder.EnumerateDirectories("*", options)) { token.ThrowIfCancellationRequested(); if (!IgnoredDirectories.Contains(child.Name) && !RepositoryRoot(child)) foreach (var nested in NearbyFolders(depth + 1, child)) yield return nested; }
        }
        string[] NearbyProjects() => sourceFolder.Parent?.Parent == null || RepositoryRoot(sourceFolder) ? Array.Empty<string>() : Bounded(128, Bounded(128, NearbyFolders(0, sourceFolder.Parent)).SelectMany(f => { token.ThrowIfCancellationRequested(); return f.EnumerateFiles("*.csproj", options).Select(p => p.FullName); }));
        try
        {
            if (!File.Exists(sourceProject) || IsLink(sourceProject)) return null;
            var candidates = Bounded(128, (FindSolution(0, sourceFolder) ?? NearbyProjects()).DistinctBy(ProjectPathKey));
            var consumers = candidates.Where(p => { token.ThrowIfCancellationRequested(); if (SamePath(p, sourceProject) || !File.Exists(p) || IsLink(p)) return false; var refs = ReadProjectReferences(p) ?? throw new InvalidOperationException("A nearby project exceeded the 1 MiB limit."); return refs.Any(r => SamePath(r, sourceProject)); }).Take(2).ToArray();
            if (consumers.Length != 1) return null;
            var consumer = consumers[0]; var folder = Path.GetDirectoryName(consumer)!;
            return Within(sourceFolder.FullName, folder) || Within(scan.Root, folder) || !SamePath(FindProject(folder, token) ?? "", consumer) ? null : consumer;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or XmlException) { return null; }
    }
    public static PocoExistingScan? FindModel(string path, CancellationToken token)
    {
        var input = Path.GetFullPath(path); var explicitSource = File.Exists(input) && (Path.GetExtension(input).Equals(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(input).Equals(".cs", StringComparison.OrdinalIgnoreCase));
        bool Usable(PocoExistingScan scan) => scan.ContextCandidates.Length == 1 && scan.Target.HasValue;
        PocoExistingScan? TryScan(string candidate)
        {
            token.ThrowIfCancellationRequested(); try { return Scan(candidate, token); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { return null; }
        }
        var explicitScan = explicitSource ? TryScan(input) : null;
        if (explicitScan != null && Usable(explicitScan) && ContextCandidates(explicitScan.Classes).Any(c => !c.Text.StartsWith("// <auto-generated by AnotherEdit>", StringComparison.Ordinal))) return explicitScan;
        if (File.Exists(input) && Path.GetExtension(input).Equals(".cs", StringComparison.OrdinalIgnoreCase)) return explicitScan;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var visited = 0;
        PocoExistingScan? Discover(int depth, string project)
        {
            token.ThrowIfCancellationRequested(); if (depth > 8 || visited >= 32 || !File.Exists(project) || IsLink(project) || !seen.Add(project)) return null;
            visited++; string[] references;
            try { references = (ReadProjectReferences(project) ?? Array.Empty<string>()).Take(32).ToArray(); } catch (XmlException) { references = Array.Empty<string>(); }
            foreach (var reference in references) if (Discover(depth + 1, reference) is { } found) return found;
            var scan = TryScan(project); return scan != null && Usable(scan) ? scan : null;
        }
        var projectFile = FindProject(input, token);
        return (projectFile == null ? null : Discover(0, projectFile)) ?? explicitScan ?? (projectFile == null ? null : TryScan(projectFile)) ?? (Directory.Exists(input) ? TryScan(input) : null);
    }
    private static (SqlCatalogObjectKind Kind, string Schema, string Name) Key(SqlCatalogObject item) => (item.Kind, item.Schema, item.Name);
    private static (Dictionary<(SqlCatalogObjectKind, string, string), PocoExistingClass> Matches, HashSet<(SqlCatalogObjectKind, string, string)> Ambiguous) ObjectMatches(PocoExistingScan scan, SqlCatalogObject[] items)
    {
        var objects = items.Where(x => x.Kind is SqlCatalogObjectKind.Table or SqlCatalogObjectKind.View).ToArray();
        var scopes = objects.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        Dictionary<string, PocoExistingClass[]> ByName(Func<PocoExistingClass, IEnumerable<string>> names) => scan.Classes.SelectMany(c => names(c).Distinct().Select(n => (Name: n, Class: c))).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Class).ToArray(), StringComparer.Ordinal);
        var explicitNames = ByName(x => x.TableName == null ? Array.Empty<string>() : new[] { x.TableName });
        var conventions = ByName(x => x.TableName != null ? Array.Empty<string>() : x.SetName == null ? new[] { x.Name } : new[] { x.Name, x.SetName });
        var candidateMap = objects.Select(item =>
        {
            var unique = scopes[item.Name] == 1;
            var exact = explicitNames.GetValueOrDefault(item.Name, Array.Empty<PocoExistingClass>()).Where(e => e.Schema == null || e.Schema == item.Schema).ToArray();
            return (Key: Key(item), Existing: exact.Length > 0 ? exact : conventions.GetValueOrDefault(item.Name, Array.Empty<PocoExistingClass>()), Ambiguous: exact.Length > 0 ? !unique && exact.Any(e => e.Schema == null) : !unique);
        }).ToArray();
        var matches = candidateMap.Where(x => x.Existing.Length == 1 && !x.Ambiguous).GroupBy(x => x.Existing[0].FullName).Where(g => g.Count() == 1).Select(g => g.Single()).ToDictionary(x => x.Key, x => x.Existing[0]);
        return (matches, candidateMap.Where(x => x.Existing.Length > 0 && !matches.ContainsKey(x.Key)).Select(x => x.Key).ToHashSet());
    }
    public static IReadOnlyDictionary<(SqlCatalogObjectKind, string, string), PocoExistingClass> Matches(PocoExistingScan scan, SqlCatalogObject[] items) => ObjectMatches(scan, items).Matches;
    public static IReadOnlySet<(SqlCatalogObjectKind, string, string)> AmbiguousMatches(PocoExistingScan scan, SqlCatalogObject[] items) => ObjectMatches(scan, items).Ambiguous;
    public static IReadOnlyDictionary<(SqlCatalogObjectKind, string, string), PocoObjectNames> Overrides(PocoExistingScan scan, SqlCatalogObject[] items) => Matches(scan, items).ToDictionary(x => x.Key, x =>
    {
        var props = x.Value.Properties.Where(p => !Attributes("NotMapped", p.Declaration.AttributeLists).Any()).GroupBy(p => p.ColumnName ?? p.Name).Select(g => (Column: g.Key, Name: One(g.Select(p => p.Name)))).Where(p => p.Name != null).ToDictionary(p => p.Column, p => p.Name!, StringComparer.Ordinal);
        return new PocoObjectNames(x.Value.Name, x.Value.SetName ?? "", props);
    });
    private static readonly IReadOnlyDictionary<string, string> ClrNames = new Dictionary<string, string>(StringComparer.Ordinal) { ["System.Boolean"] = "bool", ["System.Byte"] = "byte", ["System.SByte"] = "sbyte", ["System.Int16"] = "short", ["System.UInt16"] = "ushort", ["System.Int32"] = "int", ["System.UInt32"] = "uint", ["System.Int64"] = "long", ["System.UInt64"] = "ulong", ["System.Single"] = "float", ["System.Double"] = "double", ["System.Decimal"] = "decimal", ["System.Char"] = "char", ["System.String"] = "string", ["System.Object"] = "object" };
    private static string TypeName(TypeSyntax syntax) { var value = syntax.ToString().Replace("global::", ""); return ClrNames.GetValueOrDefault(value, value); }
    private static string CanonicalType(TypeSyntax syntax) => syntax switch { NullableTypeSyntax n => CanonicalType(n.ElementType) + "?", ArrayTypeSyntax a => CanonicalType(a.ElementType) + string.Concat(a.RankSpecifiers.Select(x => x.ToString())), _ => TypeName(syntax) };
    private static bool AutoProperty(PocoExistingProperty property)
    {
        var d = property.Declaration; return d.AccessorList != null && d.ExpressionBody == null && d.Initializer == null && !d.Modifiers.Any(SyntaxKind.OverrideKeyword) && !d.Modifiers.Any(SyntaxKind.AbstractKeyword) && d.AccessorList.Accessors.Count == 2 && d.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null && a.SemicolonToken.IsKind(SyntaxKind.SemicolonToken));
    }
    private static int LineStart(string text, int position) => position == 0 ? 0 : text.LastIndexOf('\n', position - 1) + 1;
    private static string? LineIndent(string text, int position) { var start = LineStart(text, position); var prefix = text.Substring(start, position - start); return prefix.All(c => c is ' ' or '\t') ? prefix : null; }
    private static string? Indentation(PocoExistingClass item)
    {
        var prefix = LineIndent(item.Text, item.Declaration.SpanStart); if (prefix == null) return null;
        return One(item.Declaration.Members.Select(m => LineIndent(item.Text, m.SpanStart)).Where(i => i != null && i.Length > prefix.Length && i.StartsWith(prefix, StringComparison.Ordinal)).Select(i => i![prefix.Length..]));
    }
    private static string? LineEnding(string text) { var p = text.IndexOf('\n'); return p < 0 ? null : p > 0 && text[p - 1] == '\r' ? "\r\n" : "\n"; }
    public static PocoGeneration FormatGenerated(PocoExistingScan scan, PocoGeneration generation, CancellationToken token)
    {
        var context = scan.Classes.Where(x => x.Name == scan.ContextName && x.Namespace == scan.Namespace).ToArray();
        var mapped = scan.Classes.Where(x => x.TableName != null || x.SetName != null).ToArray(); var reference = context.Length > 0 ? context : mapped.Length > 0 ? mapped : scan.Classes;
        var scoped = OneValue(reference.Select(x => x.Declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()).Where(x => x != null).Select(x => x is FileScopedNamespaceDeclarationSyntax)) ?? false;
        var indent = One(reference.Select(Indentation).Where(x => x != null)) ?? "    ";
        var newline = One(reference.DistinctBy(x => x.Path).Select(x => LineEnding(x.Text)).Where(x => x != null)) ?? Environment.NewLine;
        var files = generation.Files.Select(file =>
        {
            token.ThrowIfCancellationRequested();
            if (!file.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !scoped && indent == "    " && LineEnding(file.Text) == newline) return file;
            var original = CSharpSyntaxTree.ParseText(file.Text, cancellationToken: token).GetCompilationUnitRoot(token); if (original.ContainsDiagnostics) return file;
            var formatted = original;
            if (scoped && original.Members.Count == 1 && original.Members[0] is NamespaceDeclarationSyntax block)
                formatted = original.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(SyntaxFactory.FileScopedNamespaceDeclaration(block.Name).WithNamespaceKeyword(block.NamespaceKeyword).WithExterns(block.Externs).WithUsings(block.Usings).WithMembers(block.Members)));
            var text = formatted.NormalizeWhitespace(indent, newline, false).ToFullString() + newline;
            var verified = CSharpSyntaxTree.ParseText(text, cancellationToken: token).GetCompilationUnitRoot(token);
            return verified.ContainsDiagnostics || !SyntaxFactory.AreEquivalent(formatted, verified, false) ? file : file with { Text = text };
        }).ToArray();
        return generation with { Files = files };
    }
    private static (int Position, string Text) AddPropertyText(PocoExistingClass existing, PocoExistingProperty property)
    {
        var text = existing.Text; var newline = LineEnding(text) ?? "\n"; var brace = existing.Declaration.CloseBraceToken.SpanStart; var braceLine = LineStart(text, brace); var atLineStart = string.IsNullOrWhiteSpace(text.Substring(braceLine, brace - braceLine));
        var declarationStart = existing.Declaration.SpanStart; var start = LineStart(text, declarationStart); var indent = new string(text.Substring(start, declarationStart - start).TakeWhile(char.IsWhiteSpace).ToArray());
        var memberIndent = indent + (Indentation(existing) ?? "    "); var body = string.Join(newline, property.Declaration.ToString().Replace("\r\n", "\n").Split('\n').Select(l => memberIndent + l.Trim()));
        return (atLineStart ? braceLine : brace, (atLineStart ? "" : newline) + body + newline + (atLineStart ? "" : indent));
    }
    public static PocoExistingComparison Compare(PocoExistingScan scan, PocoGeneratedFile[] generated, bool allowManualEdits)
    {
        var edits = new List<PocoExistingEdit>(); var notices = new List<string>(); var usage = new Dictionary<string, int>(StringComparer.Ordinal);
        if (allowManualEdits) foreach (var file in scan.Classes.DistinctBy(x => x.Path)) foreach (var symbol in file.Declaration.SyntaxTree.GetRoot().DescendantTokens()) if (symbol.IsKind(SyntaxKind.IdentifierToken)) usage[symbol.ValueText] = usage.GetValueOrDefault(symbol.ValueText) + 1;
        var byFullName = scan.Classes.GroupBy(x => x.FullName).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        PocoExistingClass[] Candidates(PocoExistingClass desired)
        {
            if (byFullName.TryGetValue(desired.FullName, out var exact)) return exact;
            if (desired.TableName == null) return Array.Empty<PocoExistingClass>();
            var mapped = scan.Classes.Where(x => x.TableName == desired.TableName && (x.Schema == null || desired.Schema == null || x.Schema == desired.Schema)).ToArray();
            return mapped.Length > 0 ? mapped : scan.Classes.Where(x => x.TableName == null && x.Name == desired.Name && (desired.TableName == x.Name || x.SetName == desired.TableName)).ToArray();
        }
        void AppendEdit(PocoExistingClass existing, string description, int start, int length, string replacement, bool review) => edits.Add(new(existing.RelativePath + "|" + start + "|" + description, existing.RelativePath, existing.SourceHash, description, start, length, replacement, review));
        void Difference(string existing, string description) => notices.Add(existing + " · " + description + (allowManualEdits ? "" : " (manual source protected)"));
        void InspectChanges(PocoExistingClass existing, PocoExistingClass desired)
        {
            var mayEdit = allowManualEdits && existing.CanEdit; var mayAddGenerated = !allowManualEdits && existing.GeneratedSource && existing.CanEdit;
            var desiredColumns = desired.Properties.Where(p => p.ColumnName != null).Select(p => (Column: p.ColumnName!, Property: p)).ToArray();
            foreach (var (column, property) in desiredColumns)
            {
                var matched = existing.Properties.Where(p => (p.ColumnName ?? p.Name) == column).ToArray();
                if (matched.Length == 1)
                {
                    var old = matched[0];
                    if (CanonicalType(old.Declaration.Type) != CanonicalType(property.Declaration.Type))
                    {
                        var description = old.Name + " · type " + old.TypeName + " → " + property.TypeName;
                        if (mayEdit && AutoProperty(old)) AppendEdit(existing, description, old.Declaration.Type.Span.Start, old.Declaration.Type.Span.Length, property.TypeName, true); else Difference(existing.FullName, description);
                    }
                    else if (!Attributes("MaxLength", old.Declaration.AttributeLists).Select(x => x.ToString()).SequenceEqual(Attributes("MaxLength", property.Declaration.AttributeLists).Select(x => x.ToString()))) notices.Add(existing.FullName + " · review length mapping for " + old.Name);
                }
                else if (matched.Length == 0)
                {
                    var description = "Add " + property.Name + " (" + column + ")";
                    var conflicts = existing.Declaration.Members.Any(m => m.DescendantTokens().Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == property.Name));
                    if ((mayEdit || mayAddGenerated) && AutoProperty(property) && !conflicts) { var (position, text) = AddPropertyText(existing, property); AppendEdit(existing, description, position, 0, text, mayAddGenerated); if (mayAddGenerated) notices.Add(existing.RelativePath + " · Regenerating this source with its original generator can overwrite direct edits."); }
                    else Difference(existing.FullName, description);
                }
                else notices.Add(existing.FullName + " · ambiguous existing properties for " + column);
            }
            var columns = desiredColumns.Select(x => x.Column).ToHashSet(StringComparer.Ordinal);
            if (columns.Count > 0) foreach (var property in existing.Properties)
            {
                if (property.ColumnName == null || columns.Contains(property.ColumnName)) continue;
                var description = "Remove " + property.Name + " · mapped column absent from proposed model"; var hasUsage = usage.GetValueOrDefault(property.Name) > 1;
                var otherAttributes = property.Declaration.AttributeLists.SelectMany(x => x.Attributes).Any(a => SimpleName(a.Name.ToString()) is not ("Column" or "ColumnAttribute"));
                if (mayEdit && AutoProperty(property) && !hasUsage && !otherAttributes) AppendEdit(existing, description, property.Declaration.Span.Start, property.Declaration.Span.Length, "", true);
                else Difference(existing.FullName, description + (hasUsage ? " · references found in inspected C# sources" : ""));
            }
            foreach (var property in desired.Properties.Where(p => p.ColumnName == null && !existing.Properties.Any(x => x.Name == p.Name && CanonicalType(x.Declaration.Type) == CanonicalType(p.Declaration.Type)))) notices.Add(existing.FullName + " · review relationship member " + property.Name + ".");
        }
        foreach (var file in generated.Where(f => f.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var (_, classes, bad) = InspectText(file.FileName, file.FileName, file.Text, "", false, CancellationToken.None);
            if (bad) { notices.Add(file.FileName + " has invalid generated C# and cannot be merged."); continue; }
            foreach (var desired in classes)
            {
                var candidates = Candidates(desired);
                if (candidates.Length == 1)
                {
                    var existing = candidates[0];
                    if (desired.TableName != null && desired.Properties.Any(p => p.ColumnName != null))
                    {
                        if (existing.TableName != null && existing.TableName != desired.TableName || existing.Schema != null && desired.Schema != null && existing.Schema != desired.Schema) notices.Add(existing.FullName + " maps to a different database object. Existing class was protected."); else InspectChanges(existing, desired);
                    }
                    else notices.Add(existing.FullName + " · existing non-entity code needs separate review.");
                }
                else notices.Add(desired.FullName + (candidates.Length == 0 ? " · no existing class match." : " · several existing declarations match. Review the mapping before applying edits."));
            }
        }
        return new(edits.ToArray(), notices.Distinct().ToArray());
    }
    public static string ApplyEdits(string text, PocoExistingEdit[] edits)
    {
        var changes = edits.GroupBy(e => e.Start).OrderByDescending(g => g.Key); var result = new StringBuilder(text); var boundary = text.Length;
        foreach (var group in changes)
        {
            var start = group.Key; var items = group.ToArray();
            if (start < 0 || start > boundary || items.Length > 1 && items.Any(e => e.Length != 0)) throw new ArgumentException("The selected edits overlap or fall outside the source file.", nameof(edits));
            var length = items[0].Length; if (length < 0 || length > boundary - start) throw new ArgumentException("The selected edit falls outside the source file.", nameof(edits));
            result.Remove(start, length).Insert(start, string.Concat(items.Select(e => e.Replacement))); boundary = start;
        }
        var updated = result.ToString(); if (CSharpSyntaxTree.ParseText(updated).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error)) throw new ArgumentException("The selected edits do not produce valid C# syntax.", nameof(edits));
        return updated;
    }
}
