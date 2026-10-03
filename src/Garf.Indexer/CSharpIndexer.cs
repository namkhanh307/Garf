using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Garf.Indexer;

public static class CSharpIndexer
{
    private const string Language = "csharp";
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    private static readonly SymbolDisplayFormat FullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType
            | SymbolDisplayMemberOptions.IncludeParameters
            | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeExplicitInterface,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
            | SymbolDisplayParameterOptions.IncludeParamsRefOut
            | SymbolDisplayParameterOptions.IncludeOptionalBrackets
            | SymbolDisplayParameterOptions.IncludeExtensionThis,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static IndexResult Index(IEnumerable<string> files, string root)
    {
        var parsedFiles = files
            .Select(file => ParseFile(file, root))
            .ToList();

        var compilation = CreateCompilation(parsedFiles.Select(parsed => parsed.Tree));

        var symbols = new List<SymbolDef>();
        var symbolIds = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var nameSet = new HashSet<string>(StringComparer.Ordinal);
        var declarationSpans = new Dictionary<string, List<TextSpan>>(StringComparer.Ordinal);
        var declarationIds = new Dictionary<SyntaxNode, string>();

        foreach (var parsed in parsedFiles)
        {
            var candidates = Declarations(parsed).ToList();
            declarationSpans[parsed.File] = candidates.Select(candidate => candidate.NameSpan).ToList();

            var model = compilation.GetSemanticModel(parsed.Tree);
            foreach (var candidate in candidates)
            {
                var symbol = model.GetDeclaredSymbol(candidate.Node);
                if (symbol is null || symbolIds.ContainsKey(symbol))
                {
                    continue;
                }

                var line = parsed.Tree.GetLineSpan(candidate.NameSpan).StartLinePosition;
                var id = GetSymbolId(symbol, parsed.File, line.Line + 1, line.Character + 1);
                var def = new SymbolDef(
                    id,
                    candidate.Name,
                    candidate.Kind,
                    Language,
                    symbol.ToDisplayString(FullyQualified),
                    parsed.File,
                    line.Line + 1,
                    line.Character + 1,
                    symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Snippets.FromLines(parsed.Lines, line.Line, afterLines: 5));

                symbols.Add(def);
                symbolIds.Add(symbol, id);
                declarationIds.Add(candidate.Node, id);
                nameSet.Add(candidate.Name);
            }
        }

        var edges = new List<SymbolEdge>();
        var seenEdges = new HashSet<(string Source, string Target, string Kind, string File, int Line, int Column)>();

        foreach (var parsed in parsedFiles)
        {
            var model = compilation.GetSemanticModel(parsed.Tree);
            var spans = declarationSpans[parsed.File];
            var typedSpans = new HashSet<TextSpan>();

            void AddEdge(string kind, string target, string name, TextSpan span, string source)
            {
                var line = parsed.Tree.GetLineSpan(span).StartLinePosition;
                var file = parsed.File;
                var lineNumber = line.Line + 1;
                var column = line.Character + 1;
                var key = (source, target, kind, file, lineNumber, column);
                if (!seenEdges.Add(key))
                {
                    return;
                }

                edges.Add(new SymbolEdge(
                    source,
                    target,
                    kind,
                    name,
                    file,
                    lineNumber,
                    column,
                    Snippets.FromLines(parsed.Lines, line.Line, afterLines: 2)));
            }

            string Source(SyntaxNode node)
                => node.Ancestors().FirstOrDefault(declarationIds.ContainsKey) is { } declaration
                    ? declarationIds[declaration]
                    : "";

            foreach (var invocation in parsed.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (CalledName(invocation) is not { } called)
                {
                    continue;
                }

                var target = ResolveTarget(model.GetSymbolInfo(invocation).Symbol, symbolIds);
                if (target is null)
                {
                    continue;
                }

                typedSpans.Add(called.Span);
                AddEdge("calls", target, called.Name, called.Span, Source(invocation));
            }

            foreach (var creation in parsed.Tree.GetRoot().DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                var target = ResolveTarget(model.GetSymbolInfo(creation.Type).Symbol, symbolIds)
                    ?? ResolveTarget(model.GetTypeInfo(creation.Type).Type, symbolIds);
                if (target is null)
                {
                    continue;
                }

                typedSpans.Add(creation.Type.Span);
                AddEdge("instantiates", target, creation.Type.ToString(), creation.Type.Span, Source(creation));
            }

            foreach (var baseType in parsed.Tree.GetRoot().DescendantNodes().OfType<BaseTypeSyntax>())
            {
                var symbol = model.GetSymbolInfo(baseType.Type).Symbol
                    ?? model.GetTypeInfo(baseType.Type).Type;
                var target = ResolveTarget(symbol, symbolIds);
                if (target is null)
                {
                    continue;
                }

                var kind = symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }
                    ? "implements"
                    : "extends";
                typedSpans.Add(baseType.Type.Span);
                AddEdge(kind, target, baseType.Type.ToString(), baseType.Type.Span, Source(baseType));
            }

            foreach (var usingDirective in parsed.Tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
            {
                if (usingDirective.Name is null)
                {
                    continue;
                }

                var target = ResolveTarget(model.GetSymbolInfo(usingDirective.Name).Symbol, symbolIds);
                if (target is null)
                {
                    continue;
                }

                typedSpans.Add(usingDirective.Name.Span);
                AddEdge("imports", target, usingDirective.Name.ToString(), usingDirective.Name.Span, "");
            }

            foreach (var simple in parsed.Tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var name = simple.Identifier.ValueText;
                if (name.Length == 0 || !nameSet.Contains(name))
                {
                    continue;
                }

                if (spans.Any(span => span.Contains(simple.Span)))
                {
                    continue;
                }

                if (typedSpans.Any(span => span.Contains(simple.Span)))
                {
                    continue;
                }

                var symbol = model.GetSymbolInfo(simple).Symbol
                    ?? model.GetTypeInfo(simple).Type as ISymbol;
                var target = ResolveTarget(symbol, symbolIds);
                if (target is null)
                {
                    continue;
                }

                AddEdge("references", target, name, simple.Span, Source(simple));
            }
        }

        return new IndexResult(symbols, edges);
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<SyntaxTree> trees)
    {
        var globalUsings = CSharpSyntaxTree.ParseText(
            """
            global using System;
            global using System.Collections.Generic;
            global using System.IO;
            global using System.Linq;
            global using System.Net.Http;
            global using System.Threading;
            global using System.Threading.Tasks;
            """,
            ParseOptions,
            path: "garf-global-usings.cs");

        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: true,
            concurrentBuild: true);

        return CSharpCompilation.Create(
            "GarfIndex",
            new[] { globalUsings }.Concat(trees),
            GetMetadataReferences(),
            options);
    }

    private static List<MetadataReference> GetMetadataReferences()
    {
        var references = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
            {
                AddReference(references, seen, path);
            }
        }

        AddReference(references, seen, typeof(object).Assembly.Location);
        return references;
    }

    private static void AddReference(List<MetadataReference> references, HashSet<string> seen, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !seen.Add(path))
        {
            return;
        }

        try
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }
        catch
        {
            // Some platform assemblies (for example native binaries) are not managed metadata.
        }
    }

    private static string GetSymbolId(ISymbol symbol, string file, int line, int column)
    {
        try
        {
            var id = DocumentationCommentId.CreateDeclarationId(symbol);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }
        catch
        {
            // Fall through to the location-based fallback.
        }

        return $"{Language}:{file}:{line}:{column}";
    }

    private static string? ResolveTarget(ISymbol? symbol, Dictionary<ISymbol, string> symbolIds)
    {
        if (symbol is null)
        {
            return null;
        }

        if (symbolIds.TryGetValue(symbol, out var id))
        {
            return id;
        }

        if (symbol is IMethodSymbol method && symbolIds.TryGetValue(method.OriginalDefinition, out id))
        {
            return id;
        }

        if (symbol is INamedTypeSymbol type && symbolIds.TryGetValue(type.OriginalDefinition, out id))
        {
            return id;
        }

        return null;
    }

    private static (string Name, TextSpan Span)? CalledName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => (member.Name.Identifier.ValueText, member.Name.Span),
            SimpleNameSyntax simple => (simple.Identifier.ValueText, simple.Span),
            _ => null
        };
    }

    private static IEnumerable<DeclarationCandidate> Declarations(ParsedFile parsed)
    {
        var root = parsed.Tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case NamespaceDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Name.ToString(), "namespace", n.Name.Span);
                    break;
                case FileScopedNamespaceDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Name.ToString(), "namespace", n.Name.Span);
                    break;
                case ClassDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "class", n.Identifier.Span);
                    break;
                case StructDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "struct", n.Identifier.Span);
                    break;
                case InterfaceDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "interface", n.Identifier.Span);
                    break;
                case EnumDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "enum", n.Identifier.Span);
                    break;
                case RecordDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "record", n.Identifier.Span);
                    break;
                case DelegateDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "delegate", n.Identifier.Span);
                    break;
                case MethodDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "method", n.Identifier.Span);
                    break;
                case ConstructorDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "constructor", n.Identifier.Span);
                    break;
                case PropertyDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "property", n.Identifier.Span);
                    break;
                case EventDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "event", n.Identifier.Span);
                    break;
                case EnumMemberDeclarationSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "enumMember", n.Identifier.Span);
                    break;
                case LocalFunctionStatementSyntax n:
                    yield return new DeclarationCandidate(n, n.Identifier.ValueText, "function", n.Identifier.Span);
                    break;
                case FieldDeclarationSyntax n:
                    foreach (var variable in n.Declaration.Variables)
                    {
                        yield return new DeclarationCandidate(
                            variable,
                            variable.Identifier.ValueText,
                            "field",
                            variable.Identifier.Span);
                    }
                    break;
                case EventFieldDeclarationSyntax n:
                    foreach (var variable in n.Declaration.Variables)
                    {
                        yield return new DeclarationCandidate(
                            variable,
                            variable.Identifier.ValueText,
                            "event",
                            variable.Identifier.Span);
                    }
                    break;
            }
        }
    }

    private static ParsedFile ParseFile(string file, string root)
    {
        var text = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: file);
        var rel = Normalize(Path.GetRelativePath(root, file));
        var lines = Normalize(text).Split('\n');
        return new ParsedFile(rel, tree, lines);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private sealed record DeclarationCandidate(SyntaxNode Node, string Name, string Kind, TextSpan NameSpan);

    private readonly record struct ParsedFile(string File, SyntaxTree Tree, string[] Lines);
}
