using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CommonClassLibrary;

public static partial class PocoExistingModel
{
    // Bind names before editing: SQL literals, unrelated identifiers, and the
    // interface's own name must remain part of the original public contract.
    public static PocoGeneration ApplyGeneratedNames(string sourceNamespace, string sourceContextName,
        string namespaceText, string contextText, PocoGeneration generation, CancellationToken token)
    {
        var targetNamespace = string.IsNullOrWhiteSpace(namespaceText) ? sourceNamespace : namespaceText.Trim();
        var targetContext = string.IsNullOrWhiteSpace(contextText) ? sourceContextName : contextText.Trim();
        if (targetNamespace == sourceNamespace && targetContext == sourceContextName) return generation;
        var namespaceUnit = CSharpSyntaxTree.ParseText("namespace " + targetNamespace + " {}", cancellationToken: token).GetCompilationUnitRoot(token);
        if (namespaceUnit.ContainsDiagnostics || namespaceUnit.Members.Count != 1 || namespaceUnit.Members[0] is not NamespaceDeclarationSyntax
            || namespaceUnit.DescendantNodes().Any(n => n is GenericNameSyntax or AliasQualifiedNameSyntax))
            throw new ArgumentException("Enter a valid C# namespace, for example Company.Models.", nameof(namespaceText));
        var parsedContext = SyntaxFactory.ParseName(targetContext);
        if (parsedContext is not IdentifierNameSyntax parsedIdentifier || parsedContext.ContainsDiagnostics || !SyntaxFacts.IsValidIdentifier(targetContext))
            throw new ArgumentException("Enter a valid C# context class name.", nameof(contextText));
        var sourceFullName = string.IsNullOrWhiteSpace(sourceNamespace) ? sourceContextName : sourceNamespace + "." + sourceContextName;
        var movingGlobalTypes = string.IsNullOrWhiteSpace(sourceNamespace) && !string.IsNullOrWhiteSpace(targetNamespace);
        static string NamespaceName(INamespaceSymbol space) => space.IsGlobalNamespace ? "" : space.ToDisplayString();
        string MapNamespace(string value) => value == sourceNamespace ? targetNamespace
            : !string.IsNullOrWhiteSpace(sourceNamespace) && value.StartsWith(sourceNamespace + ".", StringComparison.Ordinal)
                ? targetNamespace + value[sourceNamespace.Length..] : value;
        var codeFiles = generation.Files.Where(f => f.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToArray();
        SyntaxTree[] ParseFiles(PocoGeneratedFile[] files) => files.Select(f => (SyntaxTree)CSharpSyntaxTree.ParseText(f.Text, path: f.FileName, cancellationToken: token)).ToArray();
        var references = new MetadataReference[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        CSharpCompilation Compile(SyntaxTree[] trees) => CSharpCompilation.Create("AnotherEdit.GeneratedNames", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        static NameSyntax WithNameTrivia(NameSyntax previous, string value)
        {
            var replacement = SyntaxFactory.ParseName(value);
            var comments = previous.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)).ToArray();
            return comments.Length == 0 ? replacement.WithTriviaFrom(previous)
                : replacement.WithLeadingTrivia(previous.GetLeadingTrivia().AddRange(comments)).WithTrailingTrivia(previous.GetTrailingTrivia());
        }
        var firstTrees = ParseFiles(codeFiles);
        var firstCompilation = Compile(firstTrees);
        var normalized = codeFiles.Select((file, index) =>
        {
            var tree = firstTrees[index];
            var root = tree.GetCompilationUnitRoot(token);
            if (!root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Parent is BaseNamespaceDeclarationSyntax)) return file;
            var semantic = firstCompilation.GetSemanticModel(tree, false);
            UsingDirectiveSyntax QualifyUsing(UsingDirectiveSyntax directive)
            {
                if (directive.Name == null) return directive;
                var symbol = semantic.GetSymbolInfo(directive.Name, token).Symbol;
                return symbol == null ? directive : directive.WithName(WithNameTrivia(directive.Name, symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
            IEnumerable<MemberDeclarationSyntax> Flatten(UsingDirectiveSyntax[] inherited, ExternAliasDirectiveSyntax[] externs, MemberDeclarationSyntax member)
            {
                if (member is BaseNamespaceDeclarationSyntax scope)
                {
                    var symbol = semantic.GetDeclaredSymbol(scope, token)!;
                    var fullName = symbol.ToDisplayString();
                    var usings = inherited.Concat(scope.Usings.Select(QualifyUsing)).ToArray();
                    var allExterns = externs.Concat(scope.Externs).ToArray();
                    var direct = scope.Members.Where(n => n is not BaseNamespaceDeclarationSyntax).ToArray();
                    if (direct.Length > 0)
                        yield return SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(fullName))
                            .WithNamespaceKeyword(scope.NamespaceKeyword).WithUsings(SyntaxFactory.List(usings))
                            .WithExterns(SyntaxFactory.List(allExterns)).WithMembers(SyntaxFactory.List(direct)).WithTriviaFrom(scope);
                    foreach (var child in scope.Members.OfType<BaseNamespaceDeclarationSyntax>())
                        foreach (var descendant in Flatten(usings, allExterns, child)) yield return descendant;
                }
                else yield return member;
            }
            return file with { Text = root.WithMembers(SyntaxFactory.List(root.Members.SelectMany(m => Flatten([], [], m)))).ToFullString() };
        }).ToArray();
        var trees = ParseFiles(normalized);
        var compilation = Compile(trees);
        var contextSymbols = trees.SelectMany(tree =>
        {
            var semantic = compilation.GetSemanticModel(tree, false);
            return tree.GetRoot(token).DescendantNodes().OfType<ClassDeclarationSyntax>().Select(d => semantic.GetDeclaredSymbol(d, token))
                .Where(s => s != null && s.ToDisplayString() == sourceFullName).Cast<ISymbol>().ToArray();
        }).ToArray();
        if (contextSymbols.Length == 0) throw new InvalidOperationException("The generated context could not be located: " + sourceFullName);
        bool IsContext(ISymbol? symbol) => symbol != null && contextSymbols.Any(c => SymbolEqualityComparer.Default.Equals(c, symbol));
        string TypePath(INamedTypeSymbol symbol) => (symbol.ContainingType == null ? "" : TypePath(symbol.ContainingType) + ".")
            + (IsContext(symbol) ? parsedIdentifier.Identifier.ValueText : symbol.Name) + "`" + symbol.Arity;
        var declaredNames = trees.SelectMany(tree =>
        {
            var semantic = compilation.GetSemanticModel(tree, false);
            return tree.GetRoot(token).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(d => semantic.GetDeclaredSymbol(d, token))
                .Where(s => s != null).Select(s => (Name: MapNamespace(NamespaceName(s!.ContainingNamespace)) + "." + TypePath(s), Original: s.ToDisplayString())).ToArray();
        });
        foreach (var group in declaredNames.GroupBy(x => x.Name, StringComparer.Ordinal))
            if (group.Select(x => x.Original).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new ArgumentException("The requested generated names would declare the same type twice: " + group.Key, nameof(contextText));
        var renamed = normalized.Select((file, index) =>
        {
            token.ThrowIfCancellationRequested();
            var tree = trees[index];
            var root = tree.GetRoot(token);
            var semantic = compilation.GetSemanticModel(tree, false);
            var edits = new List<PocoExistingEdit>();
            bool Covered(TextSpan span) => edits.Any(e => e.Start <= span.Start && span.End <= e.Start + e.Length);
            void Add(TextSpan span, string replacement)
            {
                if (!Covered(span) && file.Text.Substring(span.Start, span.Length) != replacement)
                    edits.Add(new(Guid.NewGuid().ToString("N"), file.FileName, "", "Use selected generated names", span.Start, span.Length, replacement, false));
            }
            static string NamespaceTextFor(SyntaxNode node, string value)
            {
                var comments = node.DescendantTrivia().Where(t => node.Span.Contains(t.Span) && (t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia)));
                return string.Concat(comments.Select(t => t.ToFullString() + (t.IsKind(SyntaxKind.SingleLineCommentTrivia) ? Environment.NewLine : " "))) + value;
            }
            void MappedNamespace(SyntaxNode node, ISymbol? symbol)
            {
                if (symbol is INamespaceSymbol space)
                {
                    var current = NamespaceName(space);
                    var mapped = MapNamespace(current);
                    if (mapped != current) Add(node.Span, NamespaceTextFor(node, (node.ToString().StartsWith("global::", StringComparison.Ordinal) ? "global::" : "") + mapped));
                }
            }
            foreach (var declaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                if (IsContext(semantic.GetDeclaredSymbol(declaration, token))) Add(declaration.Identifier.Span, targetContext);
            foreach (var constructor in root.DescendantNodes().OfType<ConstructorDeclarationSyntax>())
                if (semantic.GetDeclaredSymbol(constructor, token) is { } symbol && IsContext(symbol.ContainingType)) Add(constructor.Identifier.Span, targetContext);
            foreach (var destructor in root.DescendantNodes().OfType<DestructorDeclarationSyntax>())
                if (semantic.GetDeclaredSymbol(destructor, token) is { } symbol && IsContext(symbol.ContainingType)) Add(destructor.Identifier.Span, targetContext);
            foreach (var scope in root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
            {
                var current = semantic.GetDeclaredSymbol(scope, token)!.ToDisplayString();
                var mapped = MapNamespace(current);
                if (mapped != current) Add(scope.Name.Span, NamespaceTextFor(scope.Name, mapped));
            }
            if (movingGlobalTypes)
                foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
                {
                    var aliased = name is IdentifierNameSyntax identifier && semantic.GetAliasInfo(identifier, token) != null;
                    if (!aliased && semantic.GetSymbolInfo(name, token).Symbol is INamedTypeSymbol symbol
                        && symbol.ContainingType == null && symbol.ContainingNamespace.IsGlobalNamespace && symbol.OriginalDefinition.Locations.Any(l => l.IsInSource))
                    {
                        var identifierText = IsContext(symbol.OriginalDefinition) ? targetContext : name.Identifier.Text;
                        if (name.Parent is AliasQualifiedNameSyntax qualified && qualified.Alias.Identifier.ValueText == "global")
                        {
                            var prefix = TextSpan.FromBounds(qualified.SpanStart, name.SpanStart);
                            Add(prefix, file.Text.Substring(prefix.Start, prefix.Length) + targetNamespace + ".");
                            Add(name.Identifier.Span, identifierText);
                        }
                        else Add(name.Identifier.Span, "global::" + targetNamespace + "." + identifierText);
                    }
                }
            foreach (var node in root.DescendantNodes().Where(n => n is QualifiedNameSyntax or AliasQualifiedNameSyntax or MemberAccessExpressionSyntax).OrderByDescending(n => n.Span.Length))
                if (!Covered(node.Span)) MappedNamespace(node, semantic.GetSymbolInfo(node, token).Symbol);
            foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                if (!Covered(name.Span) && semantic.GetAliasInfo(name, token) == null)
                {
                    var symbol = semantic.GetSymbolInfo(name, token).Symbol;
                    if (IsContext(symbol)) Add(name.Identifier.Span, targetContext);
                    else MappedNamespace(name, symbol);
                }
            var updated = ApplyEdits(file.Text, edits.ToArray());
            if (!movingGlobalTypes) return file with { Text = updated };
            var unit = CSharpSyntaxTree.ParseText(updated, cancellationToken: token).GetCompilationUnitRoot(token);
            var members = new List<MemberDeclarationSyntax>();
            var globalTypes = new List<MemberDeclarationSyntax>();
            void FlushGlobalTypes()
            {
                if (globalTypes.Count == 0) return;
                var scope = SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(targetNamespace))
                    .WithNamespaceKeyword(SyntaxFactory.Token(SyntaxKind.NamespaceKeyword).WithTrailingTrivia(SyntaxFactory.Space))
                    .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken).WithTrailingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine)))
                    .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken).WithLeadingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine)).WithTrailingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine)))
                    .WithMembers(SyntaxFactory.List(globalTypes));
                members.Add(scope);
                globalTypes.Clear();
            }
            foreach (var member in unit.Members)
                if (member is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax) globalTypes.Add(member);
                else { FlushGlobalTypes(); members.Add(member); }
            FlushGlobalTypes();
            return file with { Text = ApplyEdits(unit.WithMembers(SyntaxFactory.List(members)).ToFullString(), []) };
        }).ToArray();
        // Match the original Map.ofArray policy: repeated paths keep the final
        // generated value while the output sequence retains its original order.
        var byPath = new Dictionary<string, PocoGeneratedFile>(StringComparer.Ordinal);
        foreach (var file in renamed) byPath[file.FileName] = file;
        return generation with { Files = generation.Files.Select(f => byPath.GetValueOrDefault(f.FileName, f)).ToArray() };
    }
}
