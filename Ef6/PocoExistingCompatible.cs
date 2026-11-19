using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CommonClassLibrary;

public static partial class PocoExistingModel
{
    private static string[] LambdaMembers(InvocationExpressionSyntax call) => call.ArgumentList.Arguments.Count == 0 ? Array.Empty<string>() : call.ArgumentList.Arguments[0].Expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Where(x => x.Expression is IdentifierNameSyntax).Select(x => x.Name.Identifier.ValueText).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
    private static InvocationExpressionSyntax? InChain(string[] names, InvocationExpressionSyntax call) => call.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().FirstOrDefault(c => names.Contains(MethodName(c)));
    private static string? OwnerOfRelationship(InvocationExpressionSyntax call)
    {
        var genericChild = GenericArgument("HasForeignKey", call.Expression); var direct = GenericArgument("Entity", call);
        var configuration = call.Ancestors().OfType<MethodDeclarationSyntax>().SelectMany(m => m.ParameterList.Parameters).Where(p => p.Type != null).Select(p => GenericArgument("EntityTypeBuilder", p.Type!)).FirstOrDefault(x => x != null)
            ?? call.Ancestors().OfType<ClassDeclarationSyntax>().Where(c => c.BaseList != null).Select(c => GenericArgument("EntityTypeConfiguration", c.BaseList!)).FirstOrDefault(x => x != null);
        var owner = genericChild ?? direct ?? configuration; return owner == null ? null : SimpleName(owner);
    }
    private static string FkKey(string[] names) => string.Join(",", names.OrderBy(x => x, StringComparer.Ordinal));
    private static Dictionary<(string Child, string Key), (string Parent, string? Inverse)> RelationshipNames(IEnumerable<SyntaxNode> rootNodes)
    {
        var roots = rootNodes.ToArray(); var classes = roots.SelectMany(r => r.DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
        string? CollectionType(string owner, string property) => classes.Where(c => c.Identifier.ValueText == owner).SelectMany(c => c.Members.OfType<PropertyDeclarationSyntax>()).Where(p => p.Identifier.ValueText == property)
            .Select(p => new[] { "ICollection", "IEnumerable", "IList", "List", "HashSet", "ISet" }.Select(n => GenericArgument(n, p.Type)).FirstOrDefault(x => x != null)).Where(x => x != null).Select(x => SimpleName(x!)).FirstOrDefault();
        var found = new List<((string Child, string Key) Key, (string Parent, string? Inverse) Names)>();
        foreach (var call in roots.SelectMany(r => r.DescendantNodes().OfType<InvocationExpressionSyntax>()).Where(c => MethodName(c) == "HasForeignKey"))
        {
            var owner = OwnerOfRelationship(call); var parent = InChain(new[] { "HasOne", "HasRequired", "HasOptional" }, call);
            if (owner != null && parent != null && LambdaMembers(call).Length > 0 && LambdaMembers(parent).Length == 1)
            {
                var inverseCall = InChain(new[] { "WithMany", "WithOne", "WithOptional", "WithRequired" }, call);
                found.Add(((owner, FkKey(LambdaMembers(call))), (LambdaMembers(parent)[0], inverseCall == null ? null : LambdaMembers(inverseCall).FirstOrDefault())));
            }
            else
            {
                var children = InChain(new[] { "HasMany" }, call); var toParent = InChain(new[] { "WithRequired", "WithOptional", "WithOne" }, call);
                if (owner == null || children == null || toParent == null || LambdaMembers(call).Length == 0 || LambdaMembers(children).Length != 1 || LambdaMembers(toParent).Length != 1) continue;
                var inverse = LambdaMembers(children)[0]; var child = CollectionType(owner, inverse);
                if (child != null) found.Add(((child, FkKey(LambdaMembers(call))), (LambdaMembers(toParent)[0], inverse)));
            }
        }
        return found.GroupBy(x => x.Key).Select(g => (g.Key, Value: OneValue(g.Select(x => x.Names)))).Where(x => x.Value.HasValue).ToDictionary(x => x.Key, x => x.Value!.Value);
    }
    private static PocoExistingClass SelectContext(PocoExistingScan scan, PocoOptions options)
    {
        var contexts = ContextCandidates(scan.Classes); var selected = contexts.Where(c => c.Name == options.ContextName && c.Namespace == options.Namespace).ToArray();
        if (selected.Length == 1) return selected[0]; if (contexts.Length == 1) return contexts[0];
        throw new ArgumentException("Choose one concrete DbContext in Advanced.", nameof(scan));
    }
    private static void RequireCompleteContext(PocoExistingClass context, CancellationToken token)
    {
        if (context.Declaration.SyntaxTree.GetRoot(token).DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) && t.ToString().Contains("therefore only a few tables/views/stored procedures are generated.", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The selected T4 source " + context.RelativePath + " reports a partial export. Choose the last complete working C# model from Git or a saved copy before generating a replacement.");
    }
    public static PocoGeneration GenerateCompatibleForProject(PocoExistingScan scan, PocoSchemaSnapshot snapshot, PocoOptions options, string? projectFile, CancellationToken token)
    {
        var language = NullableSyntaxForProject(projectFile) ?? NullableSyntaxForProject(scan.ProjectFile);
        var fallback = (scan.Target ?? options.Target) != PocoTarget.Ef6 || scan.NullableReferenceTypes == true;
        return GenerateCompatibleCore(language ?? fallback, scan, snapshot, options, token);
    }
    public static PocoGeneration GenerateCompatible(PocoExistingScan scan, PocoSchemaSnapshot snapshot, PocoOptions options, CancellationToken token) => GenerateCompatibleForProject(scan, snapshot, options, null, token);

    private static PocoGeneration GenerateCompatibleCore(bool allowNullableSyntax, PocoExistingScan scan, PocoSchemaSnapshot snapshot, PocoOptions originalOptions, CancellationToken token)
    {
        if (originalOptions.Target != PocoTarget.Ef6 || scan.Target == PocoTarget.EfCore) throw new ArgumentException("This standalone generator supports EF6 source models only.", nameof(scan));
        var context = SelectContext(scan, originalOptions); RequireCompleteContext(context, token);
        var overrides = Overrides(scan, snapshot.Objects.Select(x => x.Item).ToArray()).ToDictionary(x => x.Key, x => x.Value);
        foreach (var item in originalOptions.Overrides) overrides[item.Key] = item.Value;
        var options = originalOptions with { Namespace = context.Namespace, ContextName = context.Name, Target = scan.Target ?? originalOptions.Target, NullableReferenceTypes = allowNullableSyntax && (scan.NullableReferenceTypes ?? originalOptions.NullableReferenceTypes), Overrides = overrides };
        var generation = FormatGenerated(scan, PocoCodeGeneration.Generate(snapshot, options), token);
        var warnings = new List<string>(generation.Warnings);
        var matches = Matches(scan, snapshot.Objects.Select(x => x.Item).ToArray());
        var filesByPath = scan.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.Ordinal);
        var roots = scan.Files.ToDictionary(f => f.RelativePath, f => { token.ThrowIfCancellationRequested(); return CSharpSyntaxTree.ParseText(f.Text, cancellationToken: token).GetCompilationUnitRoot(token); }, StringComparer.Ordinal);
        var bySimpleName = roots.OrderBy(x => x.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.DescendantNodes().Select(n => n switch { BaseTypeDeclarationSyntax b => (Name: b.Identifier.ValueText, Path: pair.Key), DelegateDeclarationSyntax d => (Name: d.Identifier.ValueText, Path: pair.Key), _ => (Name: (string?)null, Path: pair.Key) }).Where(x => x.Name != null)).GroupBy(x => x.Name!).ToDictionary(g => g.Key, g => g.Select(x => x.Path).Distinct().ToArray(), StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var pending = new Queue<string>();
        void IncludeFile(string path) { if (filesByPath.ContainsKey(path) && included.Add(path)) pending.Enqueue(path); }
        foreach (var item in scan.Classes) if (item.FullName == context.FullName || item.TableName != null || item.SetName != null) IncludeFile(item.RelativePath);
        foreach (var pair in roots.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var globalUsing = pair.Value.Usings.Any(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword));
            var extensions = pair.Value.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.ParameterList.Parameters.Count > 0 && m.ParameterList.Parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword));
            if (globalUsing || extensions) IncludeFile(pair.Key);
        }
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested(); var path = pending.Dequeue();
            foreach (var symbol in roots[path].DescendantTokens()) if (symbol.IsKind(SyntaxKind.IdentifierToken) && bySimpleName.TryGetValue(symbol.ValueText, out var dependencies)) foreach (var file in dependencies) IncludeFile(file);
        }
        var includedTypes = included.SelectMany(p => TypeDeclarations(roots[p]).Select(x => x.Name)).ToHashSet(StringComparer.Ordinal);
        var existingByFull = scan.Classes.GroupBy(x => x.FullName).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var proposedRoots = generation.Files.Select(file => { token.ThrowIfCancellationRequested(); return (File: file, Root: CSharpSyntaxTree.ParseText(file.Text, cancellationToken: token).GetCompilationUnitRoot(token)); }).ToArray();
        string? RoutineExecutor(SyntaxNode root) => root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(d => d.Identifier.ValueText.EndsWith("Procedure", StringComparison.Ordinal) && d.Modifiers.Any(SyntaxKind.StaticKeyword)).Select(d => SourceName(d, d.Identifier.ValueText).FullName).FirstOrDefault();
        HashSet<(string Class, string Member, string Type, string Parameters)> RoutineApi(SyntaxNode root)
        {
            string SyntaxText(SyntaxNode node) => node.WithoutTrivia().NormalizeWhitespace().ToString();
            return root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(d => SourceName(d, d.Identifier.ValueText).FullName != context.FullName).SelectMany(d =>
            {
                var name = SourceName(d, d.Identifier.ValueText).FullName;
                return d.Members.Select(m => m switch { MethodDeclarationSyntax method when method.Modifiers.Any(SyntaxKind.PublicKeyword) => (name, method.Identifier.ValueText, SyntaxText(method.ReturnType), SyntaxText(method.ParameterList)), PropertyDeclarationSyntax p when p.Modifiers.Any(SyntaxKind.PublicKeyword) => (name, p.Identifier.ValueText, SyntaxText(p.Type), ""), _ => ((string, string, string, string)?)null }).Where(x => x.HasValue).Select(x => x!.Value);
            }).ToHashSet();
        }
        var oldRoutines = new Dictionary<string, CompilationUnitSyntax>(StringComparer.Ordinal);
        foreach (var path in included) if (filesByPath[path].Text.Contains("// <auto-generated by AnotherEdit>", StringComparison.Ordinal) && RoutineExecutor(roots[path]) is { } name) oldRoutines[name] = roots[path];
        var generatedRoots = proposedRoots.Where(pair =>
        {
            var name = RoutineExecutor(pair.Root);
            if (name != null && oldRoutines.TryGetValue(name, out var old) && !RoutineApi(old).IsSubsetOf(RoutineApi(pair.Root))) { warnings.Add(name + ": Existing procedure API retained. Current metadata would change its signatures or result types."); return false; }
            return true;
        }).ToArray();
        var freshTypes = generatedRoots.SelectMany(p => TypeDeclarations(p.Root).Select(x => x.Name)).ToHashSet(StringComparer.Ordinal);
        var refreshedSources = included.Where(path =>
        {
            var types = TypeDeclarations(roots[path]).Select(x => x.Name).ToArray();
            return filesByPath[path].Text.Contains("// <auto-generated by AnotherEdit>", StringComparison.Ordinal) && types.Length > 0 && !types.Contains(context.FullName) && types.All(freshTypes.Contains);
        }).ToHashSet(StringComparer.Ordinal);
        var preservedTypes = new HashSet<string>(includedTypes, StringComparer.Ordinal);
        foreach (var type in refreshedSources.SelectMany(p => TypeDeclarations(roots[p]).Select(x => x.Name))) preservedTypes.Remove(type);
        var generatedClasses = generatedRoots.SelectMany(p => InspectText(p.File.FileName, p.File.FileName, p.File.Text, "", false, token).Classes).ToArray();
        var generatedByName = generatedClasses.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var edits = new Dictionary<string, List<PocoExistingEdit>>(StringComparer.OrdinalIgnoreCase);
        void AddEdit(string path, int start, int length, string replacement, string description)
        {
            var item = new PocoExistingEdit(Guid.NewGuid().ToString("N"), path, filesByPath[path].SourceHash, description, start, length, replacement, false);
            if (!edits.TryGetValue(path, out var list)) edits.Add(path, list = new()); list.Add(item);
        }
        bool DefaultAnnotations(string path)
        {
            var text = filesByPath[path].Text; var header = text[..Math.Min(text.Length, 4096)];
            return header.IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) < 0 && header.IndexOf("<autogenerated", StringComparison.OrdinalIgnoreCase) < 0 && (scan.NullableReferenceTypes ?? false);
        }
        bool AnnotationsAt(string path, int position)
        {
            var enabled = DefaultAnnotations(path);
            foreach (var trivia in roots[path].DescendantTrivia(descendIntoTrivia: true)) if (trivia.SpanStart < position && trivia.GetStructure() is NullableDirectiveTriviaSyntax d && d.TargetToken.ValueText != "warnings") enabled = d.SettingToken.ValueText switch { "enable" => true, "disable" => false, "restore" => DefaultAnnotations(path), _ => enabled };
            return enabled;
        }
        void AppendMember(PocoExistingClass owner, MemberDeclarationSyntax member)
        {
            var newline = LineEnding(owner.Text) ?? Environment.NewLine; var start = owner.Declaration.CloseBraceToken.SpanStart; var position = LineStart(owner.Text, start); var prefix = LineIndent(owner.Text, start) ?? ""; var indent = prefix + (Indentation(owner) ?? "    ");
            var body = member.NormalizeWhitespace(indentation: "    ", eol: newline).ToFullString(); var formatted = string.Join(newline, body.Replace("\r\n", "\n").Split('\n').Select(line => indent + line));
            if (allowNullableSyntax && !AnnotationsAt(owner.RelativePath, start)) formatted = indent + "#nullable enable annotations" + newline + formatted + newline + indent + "#nullable disable annotations";
            if (owner.Text.Substring(position, start - position).All(char.IsWhiteSpace)) AddEdit(owner.RelativePath, position, 0, formatted + newline, "Refresh model member");
            else AddEdit(owner.RelativePath, start, 0, newline + formatted + newline + prefix, "Refresh model member");
        }
        bool Scalar(PocoExistingProperty property) => Attributes("Column", property.Declaration.AttributeLists).Any();
        bool SameReferenceType(TypeSyntax left, TypeSyntax right) { var l = CanonicalType(left).TrimEnd('?'); var r = CanonicalType(right).TrimEnd('?'); return l == r && (l is "string" or "object" || l.EndsWith("]", StringComparison.Ordinal)); }
        void SyncProperties(PocoExistingClass existing, PocoExistingClass desired, bool includeNavigation)
        {
            var parts = existingByFull[existing.FullName]; var current = new Dictionary<string, PocoExistingProperty>(StringComparer.Ordinal);
            foreach (var p in parts.SelectMany(x => x.Properties)) current[p.Name] = p;
            foreach (var property in desired.Properties)
            {
                if (!includeNavigation && !Scalar(property)) continue;
                if (current.TryGetValue(property.Name, out var previous))
                {
                    if (CanonicalType(previous.Declaration.Type) != CanonicalType(property.Declaration.Type) && !SameReferenceType(previous.Declaration.Type, property.Declaration.Type) && Scalar(property))
                    {
                        var owner = parts.First(part => part.Properties.Any(p => ReferenceEquals(p.Declaration, previous.Declaration)));
                        AddEdit(owner.RelativePath, previous.Declaration.Type.SpanStart, previous.Declaration.Type.Span.Length, property.Declaration.Type.ToString(), "Update type " + existing.Name + "." + property.Name);
                    }
                }
                else AppendMember(existing, property.Declaration);
            }
        }
        (bool Collection, string Name) NavigationTarget(TypeSyntax syntax)
        {
            var text = syntax.ToString().TrimEnd('?'); var collection = Regex.Match(text, @"(?:ICollection|IEnumerable|IList|List|HashSet|ISet)<(.+)>");
            return collection.Success ? (true, SimpleName(collection.Groups[1].Value.TrimEnd('?'))) : (false, SimpleName(text));
        }
        var navigationNames = new Dictionary<(string Class, string Property), string>();
        var oldRelationships = RelationshipNames(included.Select(p => (SyntaxNode)roots[p]));
        foreach (var generated in generatedClasses.Where(x => x.Name == options.ContextName)) foreach (var call in generated.Declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MethodName(call) != "HasForeignKey") continue;
            var child = OwnerOfRelationship(call); var parent = InChain(new[] { "HasOne", "HasRequired", "HasOptional" }, call);
            if (child == null || parent == null) continue;
            var names = LambdaMembers(parent);
            if (names.Length != 1 || !oldRelationships.TryGetValue((child, FkKey(LambdaMembers(call))), out var old)) continue;
            navigationNames[(child, names[0])] = old.Parent;
            var inverseCall = InChain(new[] { "WithMany", "WithOne", "WithOptional", "WithRequired" }, call); var inverse = inverseCall == null ? null : LambdaMembers(inverseCall).FirstOrDefault();
            var parentProperty = generatedClasses.FirstOrDefault(x => x.Name == child)?.Properties.FirstOrDefault(p => p.Name == names[0]); var parentType = parentProperty == null ? null : NavigationTarget(parentProperty.Declaration.Type).Name;
            if (parentType != null && inverse != null && old.Inverse != null) navigationNames[(parentType, inverse)] = old.Inverse;
        }
        var tablePairs = new List<(PocoObject Source, PocoExistingClass Existing, PocoExistingClass Desired)>();
        foreach (var source in snapshot.Objects)
        {
            if (!matches.TryGetValue(Key(source.Item), out var existing) || !generatedByName.TryGetValue(PocoCodeGeneration.Names(options, source.Item).ClassName, out var desiredItems) || desiredItems.Length != 1) continue;
            var desired = desiredItems[0]; tablePairs.Add((source, existing, desired)); var existingProperties = existingByFull[existing.FullName].SelectMany(x => x.Properties).ToArray();
            foreach (var property in desired.Properties.Where(p => !Scalar(p)))
            {
                var preferred = navigationNames.TryGetValue((desired.Name, property.Name), out var preferredName) ? existingProperties.FirstOrDefault(x => x.Name == preferredName) : null;
                var exact = existingProperties.FirstOrDefault(x => x.Name == property.Name && NavigationTarget(x.Declaration.Type) == NavigationTarget(property.Declaration.Type));
                var candidates = existingProperties.Where(x => NavigationTarget(x.Declaration.Type) == NavigationTarget(property.Declaration.Type)).ToArray(); var previous = preferred ?? exact ?? (candidates.Length == 1 ? candidates[0] : null);
                if (previous != null) navigationNames[(desired.Name, property.Name)] = previous.Name;
            }
            SyncProperties(existing, desired, false);
            foreach (var property in desired.Properties.Where(p => !Scalar(p))) if (!navigationNames.ContainsKey((desired.Name, property.Name))) AppendMember(existing, property.Declaration);
        }
        string RenameNavigation(string className, string name) => navigationNames.GetValueOrDefault((className, name), name);
        StatementSyntax ReplaceNavigation(StatementSyntax statement)
        {
            var text = statement.ToString(); var entity = Regex.Match(text, @"modelBuilder\.Entity<(?<type>[^>]+)>\(\)");
            if (!entity.Success || !(text.Contains(".HasOne(") || text.Contains(".HasRequired(") || text.Contains(".HasOptional("))) return statement;
            var className = SimpleName(entity.Groups["type"].Value); var parent = Regex.Match(text, @"\.(?:HasOne|HasRequired|HasOptional)\(e => e\.(?<property>\w+)\)"); if (!parent.Success) return statement;
            var propertyName = parent.Groups["property"].Value; var parentProperty = generatedClasses.FirstOrDefault(x => x.Name == className)?.Properties.FirstOrDefault(p => p.Name == propertyName); var parentType = parentProperty == null ? null : NavigationTarget(parentProperty.Declaration.Type).Name;
            text = text.Replace("e => e." + propertyName + ")", "e => e." + RenameNavigation(className, propertyName) + ")");
            if (parentType != null) text = Regex.Replace(text, @"(\.(?:WithMany|WithOne|WithOptional)\(e => e\.)(\w+)(\))", found => found.Groups[1].Value + RenameNavigation(parentType, found.Groups[2].Value) + found.Groups[3].Value);
            return SyntaxFactory.ParseStatement(text);
        }
        var removedScalarProperties = new List<(PocoExistingClass Existing, PocoExistingProperty Property)>();
        foreach (var (_, existing, desired) in tablePairs)
        {
            var live = desired.Properties.Where(Scalar).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var property in existingByFull[existing.FullName].SelectMany(x => x.Properties))
            {
                var known = property.ColumnName != null || Regex.IsMatch(CanonicalType(property.Declaration.Type), @"^(?:string|bool|byte|short|int|long|float|double|decimal|char|Guid|DateTime|DateTimeOffset|TimeSpan|System\.(?:Guid|DateTime|DateTimeOffset|TimeSpan))(?:\?|\[\])?$");
                if (known && !live.Contains(property.Name)) { warnings.Add(existing.FullName + "." + property.Name + " no longer has a database column; its public property is retained and ignored by EF."); removedScalarProperties.Add((existing, property)); }
            }
        }
        var generatedContextParts = generatedClasses.Where(x => x.FullName == context.FullName).ToArray();
        var generatedContext = generatedContextParts.FirstOrDefault(x => x.Declaration.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "OnModelCreating")) ?? generatedContextParts.FirstOrDefault();
        if (generatedContext != null)
        {
            var contextParts = existingByFull[context.FullName]; var members = contextParts.SelectMany(x => x.Declaration.Members).ToArray(); var existingImports = roots[context.RelativePath].DescendantNodes().OfType<UsingDirectiveSyntax>().Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
            var requiredImports = generatedRoots.Where(p => p.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(d => SourceName(d, d.Identifier.ValueText).FullName == context.FullName)).SelectMany(p => p.Root.DescendantNodes().OfType<UsingDirectiveSyntax>()).Select(x => x.ToString()).Where(x => !existingImports.Contains(x)).Distinct().ToArray();
            if (requiredImports.Length > 0)
            {
                var original = roots[context.RelativePath]; var position = original.Usings.Count > 0 ? original.Usings[0].SpanStart : original.Members.Count > 0 ? original.Members[0].SpanStart : 0; var newline = LineEnding(context.Text) ?? Environment.NewLine;
                AddEdit(context.RelativePath, position, 0, string.Join(newline, requiredImports) + newline, "Required procedure extension namespaces");
            }
            var propertyNames = members.OfType<PropertyDeclarationSyntax>().Select(p => p.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            string SignatureType(TypeSyntax syntax)
            {
                switch (syntax)
                {
                    case NullableTypeSyntax nullable: var inner = SignatureType(nullable.ElementType); return new[] { "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal", "char", "Guid", "DateTime", "DateTimeOffset", "TimeSpan" }.Contains(inner) ? inner + "?" : inner;
                    case ArrayTypeSyntax array: return SignatureType(array.ElementType) + string.Concat(array.RankSpecifiers.Select(x => x.ToString()));
                    case QualifiedNameSyntax qualified: return SignatureType(qualified.Right);
                    case AliasQualifiedNameSyntax alias: return SignatureType(alias.Name);
                    case GenericNameSyntax generic: return generic.Identifier.ValueText + "<" + string.Join(",", generic.TypeArgumentList.Arguments.Select(SignatureType)) + ">";
                    default: var text = syntax.ToString(); return ClrNames.OrderBy(x => x.Key, StringComparer.Ordinal).Where(x => SimpleName(x.Key) == text).Select(x => x.Value).FirstOrDefault() ?? text;
                }
            }
            (string Name, int Arity, string Parameters) MethodSignature(MethodDeclarationSyntax method) => (method.Identifier.ValueText, method.TypeParameterList?.Parameters.Count ?? 0, string.Join("\0", method.ParameterList.Parameters.Select(p => (p.Modifiers.Any(SyntaxKind.OutKeyword) || p.Modifiers.Any(SyntaxKind.RefKeyword) || p.Modifiers.Any(SyntaxKind.InKeyword) ? "ref:" : "") + SignatureType(p.Type!))));
            var methodNames = members.OfType<MethodDeclarationSyntax>().Select(MethodSignature).ToHashSet(); var originalMethodNames = members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
            foreach (var part in generatedContextParts) foreach (var member in part.Declaration.Members)
            {
                if (member is PropertyDeclarationSyntax property && propertyNames.Add(property.Identifier.ValueText)) AppendMember(context, member);
                else if (member is MethodDeclarationSyntax method && method.Modifiers.Any(SyntaxKind.PublicKeyword) && !methodNames.Contains(MethodSignature(method)))
                {
                    var structured = method.ParameterList.Parameters.Any(p => p.Modifiers.Any(SyntaxKind.OutKeyword)); var proposed = structured && originalMethodNames.Contains(method.Identifier.ValueText) ? method.WithIdentifier(SyntaxFactory.Identifier(method.Identifier.ValueText + "WithOutputs")) : method;
                    if (methodNames.Add(MethodSignature(proposed))) AppendMember(context, proposed);
                }
            }
            var fresh = generatedContext.Declaration.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "OnModelCreating");
            var old = contextParts.SelectMany(p => p.Declaration.Members.OfType<MethodDeclarationSyntax>()).FirstOrDefault(m => m.Identifier.ValueText == "OnModelCreating");
            if (fresh != null && old?.Body != null)
            {
                var newline = LineEnding(context.Text) ?? Environment.NewLine; var source = contextParts.First(p => p.Declaration.Span.Contains(old.Span)); var indent = LineIndent(source.Text, old.SpanStart) ?? "        ";
                var statements = fresh.Body!.Statements.Where(s => !s.ToString().StartsWith("base.OnModelCreating", StringComparison.Ordinal) && !s.ToString().StartsWith("OnModelCreatingPartial", StringComparison.Ordinal)).Select(ReplaceNavigation).ToArray();
                var builder = old.ParameterList.Parameters.Count > 0 ? old.ParameterList.Parameters[0].Identifier.ValueText : "modelBuilder";
                const string begin = "// <AnotherEdit.CurrentSchema>", end = "// </AnotherEdit.CurrentSchema>";
                var body = new StringBuilder(indent + "    " + begin + newline + indent + "    // Current database schema; existing public methods and defaults are retained." + newline);
                foreach (var statement in statements)
                {
                    var text = statement.NormalizeWhitespace(indentation: "    ", eol: newline).ToFullString().Replace("modelBuilder", builder);
                    foreach (var line in text.Replace("\r\n", "\n").Split('\n')) body.Append(indent).Append("    ").AppendLine(line);
                }
                foreach (var (existing, property) in removedScalarProperties) body.Append(indent).Append("    ").Append(builder).Append(".Entity<").Append(existing.Name).Append(">().Ignore(e => e.").Append(property.Name).AppendLine(");");
                body.Append(indent).Append("    ").Append(end).Append(newline);
                var first = source.Text.IndexOf(begin, old.Body.SpanStart, old.Body.Span.Length, StringComparison.Ordinal); var last = source.Text.IndexOf(end, old.Body.SpanStart, old.Body.Span.Length, StringComparison.Ordinal);
                if (first >= 0 && last > first)
                {
                    var start = LineStart(source.Text, first); var finishLine = source.Text.IndexOf('\n', last); var finish = finishLine < 0 ? last + end.Length : finishLine + 1;
                    AddEdit(source.RelativePath, start, finish - start, body.ToString(), "Replace current EF mappings");
                }
                else
                {
                    var close = old.Body.CloseBraceToken.SpanStart; var start = LineStart(source.Text, close);
                    if (source.Text.Substring(start, close - start).All(char.IsWhiteSpace)) AddEdit(source.RelativePath, start, 0, body.ToString(), "Refresh current EF mappings");
                    else AddEdit(source.RelativePath, close, 0, newline + body + indent, "Refresh current EF mappings");
                }
            }
            else if (fresh != null && old == null)
            {
                var statements = fresh.Body!.Statements.Where(s => !s.ToString().StartsWith("OnModelCreatingPartial", StringComparison.Ordinal) && !s.ToString().StartsWith("base.OnModelCreating", StringComparison.Ordinal)).Select(ReplaceNavigation);
                var body = "{\nbase.OnModelCreating(modelBuilder);\n// <AnotherEdit.CurrentSchema>\n// Current database schema; existing public methods and defaults are retained.\n" + string.Join("\n", statements.Select(s => s.ToString())) + string.Concat(removedScalarProperties.Select(p => "\nmodelBuilder.Entity<" + p.Existing.Name + ">().Ignore(e => e." + p.Property.Name + ");")) + "\n// </AnotherEdit.CurrentSchema>\n}";
                AppendMember(context, fresh.WithBody((BlockSyntax)SyntaxFactory.ParseStatement(body)));
            }
            else if (fresh != null && old != null) warnings.Add(context.FullName + ": expression-bodied OnModelCreating was preserved; add a block body to merge current schema mappings.");
        }
        foreach (var source in snapshot.Objects.Where(x => x.Item.Kind == SqlCatalogObjectKind.Procedure))
        {
            var desiredName = PocoCodeGeneration.Names(options, source.Item).ClassName + "Row";
            if (!generatedByName.TryGetValue(desiredName, out var desiredItems) || desiredItems.Length != 1) continue;
            var desired = desiredItems[0]; var resultTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in existingByFull[context.FullName].SelectMany(p => p.Declaration.Members.OfType<MethodDeclarationSyntax>()))
            {
                var calls = method.DescendantNodes().OfType<LiteralExpressionSyntax>().Any(l => l.IsKind(SyntaxKind.StringLiteralExpression) && Regex.IsMatch(l.Token.ValueText.Replace("[", "").Replace("]", ""), @"(?<![\p{L}\p{Nd}_])" + Regex.Escape(source.Item.Name) + @"(?![\p{L}\p{Nd}_])", RegexOptions.IgnoreCase));
                if (calls) foreach (var generic in method.DescendantNodes().OfType<GenericNameSyntax>().Where(g => new[] { "Set", "SqlQuery", "ExecuteStoreQuery" }.Contains(g.Identifier.ValueText))) foreach (var argument in generic.TypeArgumentList.Arguments) resultTypes.Add(SimpleName(argument.ToString()));
            }
            foreach (var name in resultTypes)
            {
                var candidates = scan.Classes.Where(x => x.Name == name).ToArray(); if (candidates.Length != 1) continue;
                var properties = desired.Properties.Select(property =>
                {
                    var annotation = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::System.ComponentModel.DataAnnotations.Schema.ColumnAttribute")).WithArgumentList(SyntaxFactory.AttributeArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.AttributeArgument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(property.Name))))))));
                    return property with { Declaration = property.Declaration.AddAttributeLists(annotation) };
                }).ToArray();
                SyncProperties(candidates[0], desired with { Properties = properties }, false);
            }
        }
        string CanonicalMappingRegions(string text)
        {
            var root = CSharpSyntaxTree.ParseText(text, cancellationToken: token).GetCompilationUnitRoot(token); var replacements = new List<(int Start, int Length, string Replacement)>();
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.ValueText == "OnModelCreating" && m.Body != null))
            {
                const string begin = "// <AnotherEdit.CurrentSchema>", end = "// </AnotherEdit.CurrentSchema>";
                var start = text.IndexOf(begin, method.Body!.SpanStart, method.Body.Span.Length, StringComparison.Ordinal); var finish = text.IndexOf(end, method.Body.SpanStart, method.Body.Span.Length, StringComparison.Ordinal);
                if (start < 0 || finish <= start) continue;
                var region = text.Substring(start + begin.Length, finish - start - begin.Length); var block = (BlockSyntax)SyntaxFactory.ParseStatement("{" + region + "}"); var newline = LineEnding(text) ?? Environment.NewLine; var indent = (LineIndent(text, method.SpanStart) ?? "") + (Indentation(context) ?? "    ");
                var body = new StringBuilder(indent + begin + newline + indent + "// Current database schema; existing public methods and defaults are retained." + newline);
                foreach (var statement in block.Statements)
                {
                    var rendered = statement.WithoutLeadingTrivia().WithoutTrailingTrivia().NormalizeWhitespace(indentation: "    ", eol: newline).ToFullString();
                    foreach (var line in rendered.Replace("\r\n", "\n").Split('\n')) body.Append(indent).Append(line).Append(newline);
                }
                body.Append(indent).Append(end).Append(newline); var endLine = text.IndexOf('\n', finish); var after = endLine < 0 ? finish + end.Length : endLine + 1; var before = LineStart(text, start);
                replacements.Add((before, after - before, body.ToString()));
            }
            var result = new StringBuilder(text); foreach (var (start, length, replacement) in replacements.OrderByDescending(x => x.Start)) result.Remove(start, length).Insert(start, replacement); return result.ToString();
        }
        string OutputName(PocoExistingSource source)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scan.Root)); var prefix = root + Path.DirectorySeparatorChar; var folder = new DirectoryInfo(Path.GetDirectoryName(source.Path)!); string? name = null;
            while (name == null && folder != null && (folder.FullName.Equals(root, StringComparison.OrdinalIgnoreCase) || folder.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(folder.FullName, ".anotheredit-poco.json")))
                {
                    var (_, owned) = PocoOutput.OwnedFiles(folder.FullName); var relative = Path.GetRelativePath(folder.FullName, source.Path); if (owned.Any(p => p.Key.Equals(relative, StringComparison.OrdinalIgnoreCase))) name = relative;
                }
                folder = folder.Parent;
            }
            return name ?? source.RelativePath;
        }
        var output = new List<PocoGeneratedFile>();
        foreach (var path in included.Where(p => !refreshedSources.Contains(p)).OrderBy(x => x, StringComparer.Ordinal))
        {
            var source = filesByPath[path]; var text = CanonicalMappingRegions(edits.TryGetValue(path, out var list) ? ApplyEdits(source.Text, list.ToArray()) : source.Text);
            var hasDirective = roots[path].DescendantTrivia(descendIntoTrivia: true).Any(t => t.GetStructure() is NullableDirectiveTriviaSyntax d && d.TargetToken.ValueText != "warnings");
            if (!allowNullableSyntax)
            {
                if (text.Contains('#'))
                {
                    var syntax = CSharpSyntaxTree.ParseText(text, cancellationToken: token).GetRoot(token); var result = new StringBuilder(text);
                    foreach (var trivia in syntax.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsKind(SyntaxKind.NullableDirectiveTrivia)).Reverse()) result.Remove(trivia.FullSpan.Start, trivia.FullSpan.Length);
                    text = result.ToString();
                }
            }
            else if (!hasDirective)
            {
                var newline = LineEnding(source.Text) ?? Environment.NewLine; var directive = (DefaultAnnotations(path) ? "#nullable enable annotations" : "#nullable disable annotations") + newline;
                text = text.StartsWith("// <auto-generated by AnotherEdit>", StringComparison.Ordinal) && text.IndexOf('\n') >= 0 ? text.Insert(text.IndexOf('\n') + 1, directive) : directive + text;
            }
            output.Add(new(OutputName(source), text));
        }
        foreach (var (file, root) in generatedRoots)
        {
            var duplicates = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(d => { var full = SourceName(d, d.Identifier.ValueText).FullName; return full == context.FullName || preservedTypes.Contains(full); }).Where(d => !d.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()).Cast<SyntaxNode>().ToArray();
            var updated = duplicates.Length == 0 ? root : root.RemoveNodes(duplicates, SyntaxRemoveOptions.KeepNoTrivia)!;
            if (TypeDeclarations(updated).Any()) output.Add(file with { Text = updated.ToFullString() });
        }
        return new(output.ToArray(), warnings.Distinct().ToArray());
    }
}
