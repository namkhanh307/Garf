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
                nameSet.Add(candidate.Name);
            }
        }

        var references = new List<SymbolRef>();
        foreach (var parsed in parsedFiles)
        {
            var model = compilation.GetSemanticModel(parsed.Tree);
            var spans = declarationSpans[parsed.File];

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

                var symbol = model.GetSymbolInfo(simple).Symbol
                    ?? model.GetTypeInfo(simple).Type as ISymbol;
                if (symbol is null || !symbolIds.TryGetValue(symbol, out var target))
                {
                    continue;
                }

                var line = parsed.Tree.GetLineSpan(simple.Span).StartLinePosition;
                references.Add(new SymbolRef(
                    target,
                    name,
                    parsed.File,
                    line.Line + 1,
                    line.Character + 1,
                    Snippets.FromLines(parsed.Lines, line.Line, afterLines: 2)));
            }
        }

        return new IndexResult(symbols, references);
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
