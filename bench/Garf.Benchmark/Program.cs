using System.Diagnostics;
using System.Text.Json;
using Garf.Indexer;
using Microsoft.Data.Sqlite;

namespace Garf.Benchmark;

public static class Program
{
    private static readonly JsonSerializerOptions WriteJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ReadJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static void Main(string[] args)
    {
        var symbolCount = ArgInt(args, "--symbols", 100_000);
        var edgesPerSymbol = ArgInt(args, "--edges-per-symbol", 3);
        var iterations = ArgInt(args, "--iterations", 40);
        var limit = ArgInt(args, "--limit", 10);

        Console.WriteLine($"generating {symbolCount} symbols, {symbolCount * edgesPerSymbol} edges");
        var doc = Generate(symbolCount, edgesPerSymbol);

        var dir = Path.Combine(Path.GetTempPath(), "garf-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var jsonPath = Path.Combine(dir, "garf-index.json");
        var dbPath = Path.Combine(dir, "garf-index.sqlite");

        var sw = Stopwatch.StartNew();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(doc, WriteJson);
        File.WriteAllBytes(jsonPath, jsonBytes);
        sw.Stop();
        var jsonBuildMs = sw.ElapsedMilliseconds;
        var jsonMb = jsonBytes.Length / 1024.0 / 1024.0;

        sw.Restart();
        var inMemory = InMemoryIndex.Build(doc);
        sw.Stop();
        var dictBuildMs = sw.ElapsedMilliseconds;

        sw.Restart();
        SqliteIndex.Build(dbPath, doc);
        sw.Stop();
        var sqliteBuildMs = sw.ElapsedMilliseconds;
        var dbMb = new FileInfo(dbPath).Length / 1024.0 / 1024.0;

        using var sqliteConn = SqliteIndex.Open(dbPath);

        Validate(doc, inMemory, sqliteConn, symbolCount, limit);

        Console.WriteLine();
        Console.WriteLine($"build: json {jsonBuildMs} ms ({jsonMb:F1} MB), dict {dictBuildMs} ms, sqlite {sqliteBuildMs} ms ({dbMb:F1} MB)");
        Console.WriteLine($"query averages over {iterations} iterations, limit={limit} (ms)");
        Console.WriteLine();
        Console.WriteLine($"{"query",-14}{"json-cold",12}{"scan-warm",12}{"dict-warm",12}{"sqlite-cold",12}{"sqlite-warm",12}");

        foreach (var query in Queries(symbolCount))
        {
            var jsonCold = Time(() => JsonQuery(jsonPath, query, limit), iterations);
            var scanWarm = Time(() => ScanQuery(doc, query, limit), iterations);
            var dictWarm = Time(() => inMemory.Query(query, limit), iterations);
            var sqliteCold = Time(() => SqliteIndex.QueryCold(dbPath, query, limit), iterations);
            var sqliteWarm = Time(() => SqliteIndex.QueryWarm(sqliteConn, query, limit), iterations);

            Console.WriteLine($"{query,-14}{jsonCold,12:F3}{scanWarm,12:F3}{dictWarm,12:F3}{sqliteCold,12:F3}{sqliteWarm,12:F3}");
        }

        Console.WriteLine();
        Console.WriteLine($"artifacts: {dir}");
    }

    private static void Validate(IndexDocument doc, InMemoryIndex inMemory, SqliteConnection sqliteConn, int symbolCount, int limit)
    {
        foreach (var query in Queries(symbolCount))
        {
            var a = ScanQuery(doc, query, 50).Select(m => m.Symbol.Id).ToArray();
            var b = inMemory.Query(query, 50).Select(m => m.Symbol.Id).ToArray();
            var c = SqliteIndex.QueryWarm(sqliteConn, query, 50).Select(m => m.Symbol.Id).ToArray();

            if (!a.SequenceEqual(b) || !a.SequenceEqual(c))
            {
                Console.WriteLine($"WARN result mismatch for {query}");
            }
        }
    }

    private static string[] Queries(int symbolCount)
    {
        var classProbe = symbolCount / 2;
        classProbe -= classProbe % 5;

        return
        [
            "Compute",
            $"Class_{classProbe:D6}",
            "Class_05",
            "_0500",
            "QzzNotFound"
        ];
    }

    private static double Time(Action action, int iterations)
    {
        action();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value))
        {
            return value;
        }

        return fallback;
    }

    private static IndexDocument Generate(int symbolCount, int edgesPerSymbol)
    {
        var kinds = new[] { "Class", "Method", "Record", "Interface", "Enum" };
        var random = new Random(42);
        var symbols = new List<SymbolDef>(symbolCount);

        for (var i = 0; i < symbolCount; i++)
        {
            var kind = kinds[i % kinds.Length];
            var name = $"{kind}_{i:D6}";
            symbols.Add(new SymbolDef(
                $"s{i}",
                name,
                kind,
                "C#",
                $"Garf.Generated.{name}",
                $"File{i % 200:D3}.cs",
                i % 900 + 1,
                4,
                $"{kind} {name}()",
                $"// snippet for {name}"));
        }

        var duplicateCount = symbolCount / 200;
        for (var d = 0; d < duplicateCount; d++)
        {
            symbols.Add(new SymbolDef(
                $"d{d}",
                "Compute",
                "method",
                "C#",
                "Garf.Generated.Compute",
                $"File{d % 200:D3}.cs",
                d % 900 + 1,
                4,
                "method Compute()",
                "// snippet for Compute"));
        }

        var edges = new List<SymbolEdge>(symbolCount * edgesPerSymbol);
        for (var source = 0; source < symbolCount; source++)
        {
            for (var j = 0; j < edgesPerSymbol; j++)
            {
                var target = random.Next(symbolCount);
                while (target == source)
                {
                    target = random.Next(symbolCount);
                }

                var targetSymbol = symbols[target];
                edges.Add(new SymbolEdge(
                    symbols[source].Id,
                    targetSymbol.Id,
                    j == 0 ? "calls" : "references",
                    targetSymbol.Name,
                    $"File{source % 200:D3}.cs",
                    (source * 7 + j) % 900 + 1,
                    8,
                    $"{targetSymbol.Name}()"));
            }
        }

        return new IndexDocument(3, symbols, edges);
    }

    private static List<SymbolMatch> JsonQuery(string path, string query, int limit)
    {
        var doc = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(path), ReadJson)
            ?? new IndexDocument(3, new List<SymbolDef>(), new List<SymbolEdge>());

        return ScanQuery(doc, query, limit);
    }

    private static List<SymbolMatch> ScanQuery(IndexDocument doc, string query, int limit)
    {
        var symbols = doc.Symbols ?? new List<SymbolDef>();
        var edges = doc.Edges ?? new List<SymbolEdge>();
        var scored = new List<(SymbolDef Symbol, int Score)>();

        foreach (var symbol in symbols)
        {
            if (string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((symbol, 0));
            }
            else if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((symbol, 1));
            }
            else if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                scored.Add((symbol, 2));
            }
        }

        return scored
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Symbol)
            .Take(limit)
            .Select(symbol => new SymbolMatch(symbol, ConnectedEdges(symbol, edges)))
            .ToList();
    }

    private static List<SymbolEdge> ConnectedEdges(SymbolDef symbol, List<SymbolEdge> edges)
        => edges
            .Where(e => string.Equals(e.Target, symbol.Id, StringComparison.Ordinal)
                || string.Equals(e.Source, symbol.Id, StringComparison.Ordinal))
            .ToList();
}

internal sealed class InMemoryIndex
{
    private readonly Dictionary<string, List<SymbolDef>> _exact;
    private readonly SymbolDef[] _sorted;
    private readonly List<SymbolDef> _all;
    private readonly Dictionary<string, List<SymbolEdge>> _out;
    private readonly Dictionary<string, List<SymbolEdge>> _in;

    private InMemoryIndex(
        Dictionary<string, List<SymbolDef>> exact,
        SymbolDef[] sorted,
        List<SymbolDef> all,
        Dictionary<string, List<SymbolEdge>> outEdges,
        Dictionary<string, List<SymbolEdge>> inEdges)
    {
        _exact = exact;
        _sorted = sorted;
        _all = all;
        _out = outEdges;
        _in = inEdges;
    }

    public static InMemoryIndex Build(IndexDocument doc)
    {
        var symbols = doc.Symbols ?? new List<SymbolDef>();
        var edges = doc.Edges ?? new List<SymbolEdge>();

        var exact = new Dictionary<string, List<SymbolDef>>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            if (!exact.TryGetValue(symbol.Name, out var list))
            {
                exact[symbol.Name] = list = new List<SymbolDef>();
            }

            list.Add(symbol);
        }

        var sorted = symbols.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        var outEdges = new Dictionary<string, List<SymbolEdge>>(StringComparer.Ordinal);
        var inEdges = new Dictionary<string, List<SymbolEdge>>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            Add(outEdges, edge.Source, edge);
            Add(inEdges, edge.Target, edge);
        }

        return new InMemoryIndex(exact, sorted, symbols, outEdges, inEdges);
    }

    public List<SymbolMatch> Query(string query, int limit)
    {
        var best = new Dictionary<string, (SymbolDef Symbol, int Score)>(StringComparer.Ordinal);

        if (_exact.TryGetValue(query, out var exactList))
        {
            foreach (var symbol in exactList)
            {
                best[symbol.Id] = (symbol, 0);
            }
        }

        foreach (var symbol in PrefixRange(query))
        {
            if (!best.TryGetValue(symbol.Id, out var existing) || 1 < existing.Score)
            {
                best[symbol.Id] = (symbol, 1);
            }
        }

        if (best.Count < limit)
        {
            foreach (var symbol in _all)
            {
                if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && (!best.TryGetValue(symbol.Id, out var existing) || 2 < existing.Score))
                {
                    best[symbol.Id] = (symbol, 2);
                }
            }
        }

        return best.Values
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new SymbolMatch(x.Symbol, EdgesFor(x.Symbol.Id)))
            .ToList();
    }

    private static void Add(Dictionary<string, List<SymbolEdge>> map, string key, SymbolEdge edge)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = new List<SymbolEdge>();
        }

        list.Add(edge);
    }

    private IEnumerable<SymbolDef> PrefixRange(string query)
    {
        var start = LowerBound(query);
        for (var i = start; i < _sorted.Length; i++)
        {
            if (!_sorted[i].Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            yield return _sorted[i];
        }
    }

    private int LowerBound(string query)
    {
        var lo = 0;
        var hi = _sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.Compare(_sorted[mid].Name, query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private List<SymbolEdge> EdgesFor(string id)
    {
        var edges = new List<SymbolEdge>();
        if (_out.TryGetValue(id, out var outList))
        {
            edges.AddRange(outList);
        }

        if (_in.TryGetValue(id, out var inList))
        {
            edges.AddRange(inList);
        }

        return edges;
    }
}

internal static class SqliteIndex
{
    private const string ExactSql = """
        SELECT Id, Name, Kind, Language, QualifiedName, "File", Line, "Column", Signature, Snippet
        FROM symbols
        WHERE Name = @pattern
        LIMIT @limit;
        """;

    private const string PrefixSql = """
        SELECT Id, Name, Kind, Language, QualifiedName, "File", Line, "Column", Signature, Snippet
        FROM symbols
        WHERE Name LIKE @pattern ESCAPE '\'
        ORDER BY Name
        LIMIT @limit;
        """;

    private const string ContainsSql = """
        SELECT Id, Name, Kind, Language, QualifiedName, "File", Line, "Column", Signature, Snippet
        FROM symbols
        WHERE Name LIKE @pattern ESCAPE '\'
        ORDER BY Name
        LIMIT @limit;
        """;

    public static void Build(string path, IndexDocument doc)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode = MEMORY; PRAGMA synchronous = OFF;";
            pragma.ExecuteNonQuery();
        }

        using (var schema = conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE symbols (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Kind TEXT,
                    Language TEXT,
                    QualifiedName TEXT,
                    "File" TEXT,
                    Line INTEGER,
                    "Column" INTEGER,
                    Signature TEXT,
                    Snippet TEXT
                );
                CREATE INDEX idx_symbols_name ON symbols(Name);
                CREATE TABLE edges (
                    Source TEXT,
                    Target TEXT,
                    Kind TEXT,
                    Name TEXT,
                    "File" TEXT,
                    Line INTEGER,
                    "Column" INTEGER,
                    Snippet TEXT
                );
                CREATE INDEX idx_edges_source ON edges(Source);
                CREATE INDEX idx_edges_target ON edges(Target);
                """;
            schema.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO symbols(Id, Name, Kind, Language, QualifiedName, "File", Line, "Column", Signature, Snippet)
                VALUES (@Id, @Name, @Kind, @Language, @QualifiedName, @File, @Line, @Column, @Signature, @Snippet);
                """;
            var pId = insert.Parameters.Add("@Id", SqliteType.Text);
            var pName = insert.Parameters.Add("@Name", SqliteType.Text);
            var pKind = insert.Parameters.Add("@Kind", SqliteType.Text);
            var pLanguage = insert.Parameters.Add("@Language", SqliteType.Text);
            var pQualifiedName = insert.Parameters.Add("@QualifiedName", SqliteType.Text);
            var pFile = insert.Parameters.Add("@File", SqliteType.Text);
            var pLine = insert.Parameters.Add("@Line", SqliteType.Integer);
            var pColumn = insert.Parameters.Add("@Column", SqliteType.Integer);
            var pSignature = insert.Parameters.Add("@Signature", SqliteType.Text);
            var pSnippet = insert.Parameters.Add("@Snippet", SqliteType.Text);

            foreach (var symbol in doc.Symbols ?? new List<SymbolDef>())
            {
                pId.Value = symbol.Id;
                pName.Value = symbol.Name;
                pKind.Value = symbol.Kind;
                pLanguage.Value = symbol.Language;
                pQualifiedName.Value = symbol.QualifiedName;
                pFile.Value = symbol.File;
                pLine.Value = symbol.Line;
                pColumn.Value = symbol.Column;
                pSignature.Value = symbol.Signature;
                pSnippet.Value = symbol.Snippet;
                insert.ExecuteNonQuery();
            }
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO edges(Source, Target, Kind, Name, "File", Line, "Column", Snippet)
                VALUES (@Source, @Target, @Kind, @Name, @File, @Line, @Column, @Snippet);
                """;
            var pSource = insert.Parameters.Add("@Source", SqliteType.Text);
            var pTarget = insert.Parameters.Add("@Target", SqliteType.Text);
            var pKind = insert.Parameters.Add("@Kind", SqliteType.Text);
            var pName = insert.Parameters.Add("@Name", SqliteType.Text);
            var pFile = insert.Parameters.Add("@File", SqliteType.Text);
            var pLine = insert.Parameters.Add("@Line", SqliteType.Integer);
            var pColumn = insert.Parameters.Add("@Column", SqliteType.Integer);
            var pSnippet = insert.Parameters.Add("@Snippet", SqliteType.Text);

            foreach (var edge in doc.Edges ?? new List<SymbolEdge>())
            {
                pSource.Value = edge.Source;
                pTarget.Value = edge.Target;
                pKind.Value = edge.Kind;
                pName.Value = edge.Name;
                pFile.Value = edge.File;
                pLine.Value = edge.Line;
                pColumn.Value = edge.Column;
                pSnippet.Value = edge.Snippet;
                insert.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();

        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA case_sensitive_like = ON;";
        pragma.ExecuteNonQuery();

        return conn;
    }

    public static List<SymbolMatch> QueryCold(string path, string query, int limit)
    {
        using var conn = Open(path);
        return QueryWarm(conn, query, limit);
    }

    public static List<SymbolMatch> QueryWarm(SqliteConnection conn, string query, int limit)
    {
        var best = new Dictionary<string, (SymbolDef Symbol, int Score)>(StringComparer.Ordinal);

        AddScored(conn, ExactSql, query, 0, limit, best);
        AddScored(conn, PrefixSql, EscapeLike(query) + "%", 1, limit - best.Count, best);
        AddScored(conn, ContainsSql, "%" + EscapeLike(query) + "%", 2, limit - best.Count, best);

        return best.Values
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Symbol.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(x => new SymbolMatch(x.Symbol, Edges(conn, x.Symbol.Id)))
            .ToList();
    }

    private static void AddScored(
        SqliteConnection conn,
        string sql,
        string pattern,
        int score,
        int remaining,
        Dictionary<string, (SymbolDef Symbol, int Score)> best)
    {
        if (remaining <= 0)
        {
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.Parameters.AddWithValue("@limit", remaining);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var symbol = new SymbolDef(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9));

            if (!best.TryGetValue(symbol.Id, out var existing) || score < existing.Score)
            {
                best[symbol.Id] = (symbol, score);
            }
        }
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static List<SymbolEdge> Edges(SqliteConnection conn, string id)
    {
        var edges = new List<SymbolEdge>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Source, Target, Kind, Name, "File", Line, "Column", Snippet
            FROM edges
            WHERE Source = @id OR Target = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new SymbolEdge(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetString(7)));
        }

        return edges;
    }
}


