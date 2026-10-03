using System.Text.Json;

namespace Garf.Indexer;

internal static class IndexCache
{
    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static CacheEntry? cached;

    internal static int LoadCount { get; private set; }

    internal static QueryResult Query(string index, string symbol, int limit)
    {
        var entry = GetEntry(index);
        var scored = new Dictionary<string, (SymbolDef Symbol, int Index, int Score)>(StringComparer.Ordinal);

        AddExact(entry, symbol, scored);
        AddPrefix(entry, symbol, scored);

        if (scored.Count < limit)
        {
            AddContains(entry, symbol, scored);
        }

        var matches = scored.Values
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Index)
            .Take(limit)
            .Select(x => new SymbolMatch(x.Symbol, EdgesFor(entry, x.Symbol.Id)))
            .ToList();

        return new QueryResult(symbol, matches);
    }

    internal static void Invalidate(string index)
    {
        var path = Path.GetFullPath(index);
        if (cached is not null && string.Equals(cached.Path, path, StringComparison.Ordinal))
        {
            cached = null;
        }
    }

    private static CacheEntry GetEntry(string index)
    {
        var path = Path.GetFullPath(index);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"index not found: {index}", index);
        }

        var info = new FileInfo(path);
        if (cached is not null
            && string.Equals(cached.Path, path, StringComparison.Ordinal)
            && cached.LastWriteTimeUtc == info.LastWriteTimeUtc
            && cached.Length == info.Length)
        {
            return cached;
        }

        return Load(path, info);
    }

    private static CacheEntry Load(string path, FileInfo info)
    {
        var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(path), ReadJson)
            ?? new IndexDocument(
                4,
                null,
                false,
                null,
                new List<IndexFile>(),
                new List<SymbolDef>(),
                new List<SymbolEdge>());

        var symbols = document.Symbols ?? new List<SymbolDef>();
        var edges = document.Edges ?? new List<SymbolEdge>();

        var entry = new CacheEntry
        {
            Path = path,
            LastWriteTimeUtc = info.LastWriteTimeUtc,
            Length = info.Length,
            Document = document,
            Symbols = symbols,
            Edges = edges,
            ByName = BuildByName(symbols),
            SortedByName = symbols
                .Select((symbol, index) => (symbol, index))
                .OrderBy(x => x.symbol.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            OutEdges = BuildEdges(edges, edge => edge.Source),
            InEdges = BuildEdges(edges, edge => edge.Target)
        };

        cached = entry;
        LoadCount++;
        return entry;
    }

    private static void AddExact(
        CacheEntry entry,
        string symbol,
        Dictionary<string, (SymbolDef Symbol, int Index, int Score)> scored)
    {
        if (!entry.ByName.TryGetValue(symbol, out var indices))
        {
            return;
        }

        foreach (var index in indices)
        {
            Add(scored, entry.Symbols[index], index, 0);
        }
    }

    private static void AddPrefix(
        CacheEntry entry,
        string query,
        Dictionary<string, (SymbolDef Symbol, int Index, int Score)> scored)
    {
        var start = LowerBound(entry.SortedByName, query);
        for (var i = start; i < entry.SortedByName.Count; i++)
        {
            var (symbol, index) = entry.SortedByName[i];
            if (!symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                Add(scored, symbol, index, 1);
            }
        }
    }

    private static void AddContains(
        CacheEntry entry,
        string query,
        Dictionary<string, (SymbolDef Symbol, int Index, int Score)> scored)
    {
        for (var i = 0; i < entry.Symbols.Count; i++)
        {
            if (entry.Symbols[i].Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Add(scored, entry.Symbols[i], i, 2);
            }
        }
    }

    private static void Add(
        Dictionary<string, (SymbolDef Symbol, int Index, int Score)> scored,
        SymbolDef symbol,
        int index,
        int score)
    {
        if (!scored.TryGetValue(symbol.Id, out var existing) || score < existing.Score)
        {
            scored[symbol.Id] = (symbol, index, score);
        }
    }

    private static List<SymbolEdge> EdgesFor(CacheEntry entry, string id)
    {
        var indices = new HashSet<int>();
        if (entry.OutEdges.TryGetValue(id, out var outIndices))
        {
            foreach (var index in outIndices)
            {
                indices.Add(index);
            }
        }

        if (entry.InEdges.TryGetValue(id, out var inIndices))
        {
            foreach (var index in inIndices)
            {
                indices.Add(index);
            }
        }

        return indices.OrderBy(i => i).Select(i => entry.Edges[i]).ToList();
    }

    private static int LowerBound(List<(SymbolDef Symbol, int Index)> sorted, string value)
    {
        var low = 0;
        var high = sorted.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (StringComparer.OrdinalIgnoreCase.Compare(sorted[mid].Symbol.Name, value) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static Dictionary<string, List<int>> BuildByName(List<SymbolDef> symbols)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < symbols.Count; i++)
        {
            if (!byName.TryGetValue(symbols[i].Name, out var indices))
            {
                indices = new List<int>();
                byName[symbols[i].Name] = indices;
            }

            indices.Add(i);
        }

        return byName;
    }

    private static Dictionary<string, List<int>> BuildEdges(
        List<SymbolEdge> edges,
        Func<SymbolEdge, string> keySelector)
    {
        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < edges.Count; i++)
        {
            var key = keySelector(edges[i]);
            if (!map.TryGetValue(key, out var indices))
            {
                indices = new List<int>();
                map[key] = indices;
            }

            indices.Add(i);
        }

        return map;
    }

    private sealed class CacheEntry
    {
        public required string Path { get; init; }
        public required DateTime LastWriteTimeUtc { get; init; }
        public required long Length { get; init; }
        public required IndexDocument Document { get; init; }
        public required List<SymbolDef> Symbols { get; init; }
        public required List<SymbolEdge> Edges { get; init; }
        public required Dictionary<string, List<int>> ByName { get; init; }
        public required List<(SymbolDef Symbol, int Index)> SortedByName { get; init; }
        public required Dictionary<string, List<int>> OutEdges { get; init; }
        public required Dictionary<string, List<int>> InEdges { get; init; }
    }
}
