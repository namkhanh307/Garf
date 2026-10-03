using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Garf.Indexer;

public static class CSharpIndexer
{
    public static IndexResult Index(IEnumerable<string> files, string root)
    {
        var symbols = new List<SymbolDef>();
        var references = new List<SymbolRef>();
        var nameSet = new HashSet<string>(StringComparer.Ordinal);

        var trees = new List<ParsedFile>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var rel = Normalize(Path.GetRelativePath(root, file));
            var lines = Normalize(text).Split('\n');
            trees.Add(new ParsedFile(rel, tree, lines));
        }

        foreach (var parsed in trees)
        {
            foreach (var def in Declarations(parsed))
            {
                symbols.Add(def);
                nameSet.Add(def.Name);
            }
        }

        // ponytail: name-based refs (syntax only, no semantic binding); may mix same-named symbols. Upgrade: CSharpCompilation + MetadataReferences for exact edges.
        foreach (var parsed in trees)
        {
            var rootNode = parsed.Tree.GetRoot();
            foreach (var simple in rootNode.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var name = simple.Identifier.ValueText;
                if (name.Length == 0 || !nameSet.Contains(name))
                {
                    continue;
                }

                var line = parsed.Tree.GetLineSpan(simple.Span).StartLinePosition;
                references.Add(new SymbolRef(
                    name,
                    parsed.File,
                    line.Line + 1,
                    line.Character + 1,
                    Snippets.FromLines(parsed.Lines, line.Line, afterLines: 2)));
            }
        }

        return new IndexResult(symbols, references);
    }

    private static IEnumerable<SymbolDef> Declarations(ParsedFile parsed)
    {
        var root = parsed.Tree.GetRoot();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case NamespaceDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Name.ToString(), "namespace", n.Name.Span);
                    break;
                case FileScopedNamespaceDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Name.ToString(), "namespace", n.Name.Span);
                    break;
                case ClassDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "class", n.Identifier.Span);
                    break;
                case StructDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "struct", n.Identifier.Span);
                    break;
                case InterfaceDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "interface", n.Identifier.Span);
                    break;
                case EnumDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "enum", n.Identifier.Span);
                    break;
                case RecordDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "record", n.Identifier.Span);
                    break;
                case DelegateDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "delegate", n.Identifier.Span);
                    break;
                case MethodDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "method", n.Identifier.Span);
                    break;
                case ConstructorDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "constructor", n.Identifier.Span);
                    break;
                case PropertyDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "property", n.Identifier.Span);
                    break;
                case EventDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "event", n.Identifier.Span);
                    break;
                case EnumMemberDeclarationSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "enumMember", n.Identifier.Span);
                    break;
                case LocalFunctionStatementSyntax n:
                    yield return MakeDef(parsed, n.Identifier.ValueText, "function", n.Identifier.Span);
                    break;
                case FieldDeclarationSyntax n:
                    foreach (var variable in n.Declaration.Variables)
                    {
                        yield return MakeDef(parsed, variable.Identifier.ValueText, "field", variable.Identifier.Span);
                    }
                    break;
                case EventFieldDeclarationSyntax n:
                    foreach (var variable in n.Declaration.Variables)
                    {
                        yield return MakeDef(parsed, variable.Identifier.ValueText, "event", variable.Identifier.Span);
                    }
                    break;
            }
        }
    }

    private static SymbolDef MakeDef(ParsedFile parsed, string name, string kind, TextSpan span)
    {
        var line = parsed.Tree.GetLineSpan(span).StartLinePosition;
        return new SymbolDef(
            name,
            kind,
            parsed.File,
            line.Line + 1,
            line.Character + 1,
            Snippets.FromLines(parsed.Lines, line.Line, afterLines: 5));
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private readonly record struct ParsedFile(string File, SyntaxTree Tree, string[] Lines);
}


