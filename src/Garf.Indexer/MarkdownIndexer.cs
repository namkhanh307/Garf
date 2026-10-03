namespace Garf.Indexer;

public static class MarkdownIndexer
{
    private static readonly HashSet<string> DocKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "namespace", "interface", "struct", "record", "enum", "delegate",
        "method", "constructor", "property", "type", "module", "component"
    };

    public static IndexResult Index(IEnumerable<string> files, string root, IEnumerable<SymbolDef> symbols)
    {
        var names = symbols
            .Where(s => DocKinds.Contains(s.Kind))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var references = new List<SymbolRef>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var lines = Normalize(text).Split('\n');
            var rel = Normalize(Path.GetRelativePath(root, file));

            for (var i = 0; i < lines.Length; i++)
            {
                var mentions = Mentions(lines[i], names).ToList();
                if (mentions.Count == 0)
                {
                    continue;
                }

                var snippet = Snippets.FromLines(lines, i, afterLines: 4);
                foreach (var (name, column) in mentions)
                {
                    references.Add(new SymbolRef(name, rel, i + 1, column, snippet));
                }
            }
        }

        return new IndexResult(new List<SymbolDef>(), references);
    }

    private static IEnumerable<(string Name, int Column)> Mentions(string line, IReadOnlySet<string> names)
    {
        foreach (var name in names)
        {
            var start = 0;
            while (true)
            {
                var index = line.IndexOf(name, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                if (IsBoundary(index == 0 ? '\0' : line[index - 1])
                    && IsBoundary(index + name.Length >= line.Length ? '\0' : line[index + name.Length]))
                {
                    yield return (name, index + 1);
                }

                start = index + name.Length;
            }
        }
    }

    private static bool IsBoundary(char c)
        => !char.IsLetterOrDigit(c) && c != '_';

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
}
