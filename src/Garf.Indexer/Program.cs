using System.Diagnostics;
using System.Text.Json;

namespace Garf.Indexer;

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

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "out", "coverage", ".next"
    };

    private static readonly HashSet<string> DocFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "doc", "docs", "documents"
    };

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();
            return 1;
        }

        return args[0].ToLowerInvariant() switch
        {
            "index" => RunIndex(args[1..]),
            "watch" => RunWatch(args[1..]),
            "query" => RunQuery(args[1..]),
            "mcp" => McpServer.Run(),
            "selftest" => RunSelfTest(),
            "--version" or "-v" or "version" => PrintVersion(),
            _ => Usage()
        };
    }

    private static int PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"garf {version}");
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("""
            garf - minimal code indexer for LLM token reduction

            index <root> [--output garf-index.json] [--skip-ts] [--ts-indexer path]
              Scan a repo and write a symbol/edge index.

            watch <root> [--output garf-index.json] [--skip-ts] [--ts-indexer path] [--debounce 250]
              Index a repo, then re-index when relevant files change.

            query <symbol> [--index garf-index.json] [--format md|json] [--limit 10] [--refs 10]
              Return matching symbol definitions and their typed edges.

            mcp
              Run a local Model Context Protocol server over stdio.

            selftest
              Run the built-in C# indexer check.
            """);
        return 1;
    }

    private static int RunIndex(string[] args)
    {
        var rootArg = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? ".";
        var output = GetOption(args, "--output", "-o") ?? "garf-index.json";
        var skipTs = HasFlag(args, "--skip-ts");
        var tsIndexer = GetOption(args, "--ts-indexer") ?? FindTsIndexer();

        try
        {
            var summary = IndexRepository(Path.GetFullPath(rootArg), output, skipTs, tsIndexer);
            Console.WriteLine(
                $"indexed {summary.SymbolCount} symbols, {summary.EdgeCount} edges -> {summary.Output}");
            return 0;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static IndexSummary IndexRepository(
        string root,
        string output,
        bool skipTs,
        string? tsIndexer)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"root not found: {root}");
        }

        var csFiles = Crawl(root, new[] { ".cs" });
        var result = CSharpIndexer.Index(csFiles, root);
        var files = new List<string>(csFiles);

        if (!skipTs)
        {
            var tsFiles = Crawl(root, new[] { ".ts", ".tsx", ".jsx" });
            if (tsFiles.Length > 0 && tsIndexer is not null)
            {
                var tsResult = RunTsIndexer(tsIndexer, root);
                if (tsResult is not null)
                {
                    result = new IndexResult(
                        result.Symbols.Concat(tsResult.Symbols).ToList(),
                        result.Edges.Concat(tsResult.Edges).ToList());
                    files.AddRange(tsFiles);
                }
            }
        }

        var docFiles = CrawlDocs(root);
        if (docFiles.Length > 0)
        {
            var docResult = MarkdownIndexer.Index(docFiles, root, result.Symbols);
            result = new IndexResult(
                result.Symbols,
                result.Edges.Concat(docResult.Edges).ToList());
            files.AddRange(docFiles);
        }

        var fileMeta = files
            .Select(file => new IndexFile(
                Path.GetRelativePath(root, file),
                new FileInfo(file).Length,
                new DateTimeOffset(File.GetLastWriteTimeUtc(file))))
            .ToList();

        var document = new IndexDocument(
            4,
            root,
            skipTs,
            tsIndexer,
            fileMeta,
            result.Symbols,
            result.Edges);
        File.WriteAllText(output, JsonSerializer.Serialize(document, WriteJson));

        return new IndexSummary(output, result.Symbols.Count, result.Edges.Count);
    }

    private static int RunQuery(string[] args)
    {
        var query = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? "";
        var indexFile = GetOption(args, "--index", "-i") ?? "garf-index.json";
        var format = (GetOption(args, "--format", "-f") ?? "md").ToLowerInvariant();
        var limit = ParseInt(GetOption(args, "--limit", "-n"), 10);
        var refLimit = ParseInt(GetOption(args, "--refs"), 10);

        try
        {
            EnsureFresh(indexFile);
            var result = QueryRepository(query, indexFile, limit);

            if (format == "json")
            {
                Console.WriteLine(JsonSerializer.Serialize(result, WriteJson));
            }
            else
            {
                Console.Write(RenderMarkdown(result, refLimit));
            }

            return 0;
        }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static QueryResult QueryRepository(
        string query,
        string indexFile,
        int limit)
    {
        if (!File.Exists(indexFile))
        {
            throw new FileNotFoundException($"index not found: {indexFile}", indexFile);
        }

        var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(indexFile), ReadJson)
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

        var matches = Match(symbols, query, limit)
            .Select(symbol => new SymbolMatch(symbol, ConnectedEdges(symbol, edges)))
            .ToList();

        return new QueryResult(query, matches);
    }

    private static IndexResult? RunTsIndexer(string script, string root)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"garf-ts-{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("--root");
            startInfo.ArgumentList.Add(root);
            startInfo.ArgumentList.Add("--out");
            startInfo.ArgumentList.Add(temp);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                Console.Error.WriteLine($"[garf] TS indexer failed: {error.Trim()}");
                return null;
            }

            return JsonSerializer.Deserialize<IndexResult>(File.ReadAllText(temp), ReadJson);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[garf] TS indexing skipped: {ex.Message}");
            return null;
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static bool NodeAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static string? FindTsIndexer()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var garfHome = Environment.GetEnvironmentVariable("GARF_HOME");

        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "ts-indexer", "index.mjs"),
            Path.Combine(AppContext.BaseDirectory, "..", "ts-indexer", "index.mjs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ts-indexer", "index.mjs")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "ts-indexer", "index.mjs"))
        };

        if (!string.IsNullOrWhiteSpace(garfHome))
        {
            candidates.Add(Path.Combine(garfHome, "ts-indexer", "index.mjs"));
            candidates.Add(Path.Combine(garfHome, "bin", "ts-indexer", "index.mjs"));
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".garf", "ts-indexer", "index.mjs"));
            candidates.Add(Path.Combine(userProfile, ".garf", "bin", "ts-indexer", "index.mjs"));
        }

        return candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => Path.GetFullPath(c!))
            .FirstOrDefault(File.Exists);
    }

    private static string[] Crawl(string root, string[] extensions)
    {
        var wanted = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(root, "*", options)
            .Where(f => wanted.Contains(Path.GetExtension(f)))
            .Where(f => !Path.GetRelativePath(root, f)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(seg => SkippedDirectories.Contains(seg)))
            .ToArray();
    }

    private static string[] CrawlDocs(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(root, "*.md", options)
            .Where(f =>
            {
                var segments = Path.GetRelativePath(root, f)
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return segments.Any(DocFolders.Contains)
                    && !segments.Any(SkippedDirectories.Contains);
            })
            .ToArray();
    }

    internal static bool EnsureFresh(string indexFile)
    {
        if (!File.Exists(indexFile))
        {
            throw new FileNotFoundException($"index not found: {indexFile}", indexFile);
        }

        var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(indexFile), ReadJson);
        if (document is null
            || document.Version < 4
            || string.IsNullOrWhiteSpace(document.Root)
            || document.Files is null
            || document.Files.Count == 0)
        {
            Console.Error.WriteLine(
                "[garf] index lacks freshness metadata; run `index` to migrate (answering from existing index)");
            return false;
        }

        var root = document.Root;
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine("[garf] indexed root no longer exists; answering from existing index");
            return false;
        }

        var current = ExpectedFiles(root, document.SkipTs, document.TsIndexer);
        var stored = new Dictionary<string, IndexFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in document.Files)
        {
            stored[file.Path] = file;
        }

        var stale = current.Length != stored.Count;
        if (!stale)
        {
            foreach (var file in current)
            {
                var rel = Path.GetRelativePath(root, file);
                if (!stored.TryGetValue(rel, out var meta)
                    || meta.Length != new FileInfo(file).Length
                    || meta.LastWriteTimeUtc != new DateTimeOffset(File.GetLastWriteTimeUtc(file)))
                {
                    stale = true;
                    break;
                }
            }
        }

        if (!stale)
        {
            return false;
        }

        Console.Error.WriteLine("[garf] index is stale; regenerating ...");
        var summary = IndexRepository(root, indexFile, document.SkipTs, document.TsIndexer);
        IndexCache.Invalidate(Path.GetFullPath(indexFile));
        Console.Error.WriteLine(
            $"indexed {summary.SymbolCount} symbols, {summary.EdgeCount} edges -> {summary.Output}");
        return true;
    }

    private static string[] ExpectedFiles(string root, bool skipTs, string? tsIndexer)
    {
        var files = new List<string>();
        files.AddRange(Crawl(root, new[] { ".cs" }));
        if (!skipTs && tsIndexer is not null)
        {
            files.AddRange(Crawl(root, new[] { ".ts", ".tsx", ".jsx" }));
        }

        files.AddRange(CrawlDocs(root));
        return files.ToArray();
    }

    internal static bool IsIndexedFile(string root, string fullPath)
    {
        if (HasSkippedSegment(root, fullPath))
        {
            return false;
        }

        var ext = Path.GetExtension(fullPath);
        if (ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ts", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Path.GetRelativePath(root, fullPath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => DocFolders.Contains(seg));
    }

    private static bool HasSkippedSegment(string root, string fullPath)
        => Path.GetRelativePath(root, fullPath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => SkippedDirectories.Contains(seg));

    private static int RunWatch(string[] args)
    {
        var rootArg = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? ".";
        var output = GetOption(args, "--output", "-o") ?? "garf-index.json";
        var skipTs = HasFlag(args, "--skip-ts");
        var tsIndexer = GetOption(args, "--ts-indexer") ?? FindTsIndexer();
        var debounce = ParseInt(GetOption(args, "--debounce"), 250);
        var root = Path.GetFullPath(rootArg);

        try
        {
            var summary = IndexRepository(root, output, skipTs, tsIndexer);
            Console.WriteLine(
                $"indexed {summary.SymbolCount} symbols, {summary.EdgeCount} edges -> {summary.Output}");
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        using var watcher = new IndexWatcher(root, output, skipTs, tsIndexer, debounce);
        watcher.Start();
        Console.WriteLine($"watching {root} — press Ctrl+C to exit");

        using var exit = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Set();
        };
        exit.Wait();
        Console.WriteLine("watch stopped");
        return 0;
    }

    private static List<SymbolDef> Match(List<SymbolDef> symbols, string query, int limit)
    {
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
            .ToList();
    }

    private static List<SymbolEdge> ConnectedEdges(SymbolDef symbol, List<SymbolEdge> edges)
        => edges
            .Where(e => string.Equals(e.Target, symbol.Id, StringComparison.Ordinal)
                || string.Equals(e.Source, symbol.Id, StringComparison.Ordinal))
            .ToList();

    private static List<SymbolEdge> Incoming(SymbolDef symbol, List<SymbolEdge> edges, string kind)
        => edges
            .Where(e => e.Kind == kind && string.Equals(e.Target, symbol.Id, StringComparison.Ordinal))
            .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ToList();

    private static List<SymbolEdge> Outgoing(SymbolDef symbol, List<SymbolEdge> edges, string kind)
        => edges
            .Where(e => e.Kind == kind && string.Equals(e.Source, symbol.Id, StringComparison.Ordinal))
            .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ToList();

    internal static string RenderMarkdown(QueryResult result, int refLimit)
    {
        using var writer = new StringWriter();
        writer.WriteLine($"# Results for `{result.Query}`");
        if (result.Matches.Count == 0)
        {
            writer.WriteLine("No symbols found.");
            return writer.ToString();
        }

        foreach (var match in result.Matches)
        {
            writer.WriteLine();
            writer.WriteLine($"## {match.Symbol.Name} ({match.Symbol.Kind}) — {match.Symbol.File}:{match.Symbol.Line}");
            writer.WriteLine($"- `language` {match.Symbol.Language}");
            writer.WriteLine($"- `qualifiedName` {match.Symbol.QualifiedName}");
            writer.WriteLine($"- `signature` {match.Symbol.Signature}");
            writer.WriteLine("```");
            writer.WriteLine(match.Symbol.Snippet);
            writer.WriteLine("```");

            var groups = new (string Heading, List<SymbolEdge> Edges)[]
            {
                ("Callers", Incoming(match.Symbol, match.Edges, "calls")),
                ("Callees", Outgoing(match.Symbol, match.Edges, "calls")),
                ("Instantiations", Incoming(match.Symbol, match.Edges, "instantiates")),
                ("Base types", Outgoing(match.Symbol, match.Edges, "extends")
                    .Concat(Outgoing(match.Symbol, match.Edges, "implements"))
                    .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Line)
                    .ToList()),
                ("Derived types", Incoming(match.Symbol, match.Edges, "extends")
                    .Concat(Incoming(match.Symbol, match.Edges, "implements"))
                    .OrderBy(e => e.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Line)
                    .ToList()),
                ("Imports", Incoming(match.Symbol, match.Edges, "imports")),
                ("References", Incoming(match.Symbol, match.Edges, "references"))
            };

            foreach (var group in groups)
            {
                if (group.Edges.Count == 0)
                {
                    continue;
                }

                writer.WriteLine($"**{group.Heading}** ({Math.Min(group.Edges.Count, refLimit)})");
                foreach (var edge in group.Edges.Take(refLimit))
                {
                    writer.WriteLine($"- {edge.File}:{edge.Line} {FirstLine(edge.Snippet)}");
                }
            }
        }

        return writer.ToString();
    }

    private static string FirstLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static int RunSelfTest()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"garf-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "Sample.cs");
            File.WriteAllText(file, """
                namespace Demo;

                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                }

                public static class Formatter
                {
                    public static string Add(string left, string right) => left + right;
                }

                public interface IWork
                {
                }

                public class BaseType
                {
                }

                public class Derived : BaseType, IWork
                {
                }

                public static class Program
                {
                    public static void Main()
                    {
                        Calculator calc = new Calculator();
                        _ = calc.Add(1, 2);
                        _ = Formatter.Add("a", "b");
                        _ = new Derived();
                    }
                }
                """);

            var result = CSharpIndexer.Index(new[] { file }, dir);
            var symbols = result.Symbols;
            var names = symbols.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

            Assert(names.Contains("Calculator"), "Calculator symbol missing");
            Assert(names.Contains("Add"), "Add symbol missing");
            Assert(names.Contains("BaseType"), "BaseType symbol missing");
            Assert(names.Contains("IWork"), "IWork symbol missing");
            Assert(names.Contains("Derived"), "Derived symbol missing");

            var addSymbols = symbols.Where(s => s.Name == "Add").ToList();
            Assert(addSymbols.Count >= 2, "two distinct Add symbols expected");
            Assert(
                addSymbols.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == addSymbols.Count,
                "Add symbols must have distinct ids");

            var addCalls = result.Edges.Where(e => e.Kind == "calls" && e.Name == "Add").ToList();
            Assert(addCalls.Count == 2, "two Add call edges expected");
            foreach (var addSymbol in addSymbols)
            {
                Assert(addCalls.Any(e => e.Target == addSymbol.Id), "Add call missing exact target");
            }

            Assert(
                addCalls.Select(e => e.Target).Distinct(StringComparer.Ordinal).Count() == 2,
                "Add calls should point to two different Add symbols");

            var main = symbols.First(s => s.Name == "Main" && s.Kind == "method");
            Assert(addCalls.All(e => e.Source == main.Id), "Add calls should be sourced from Main");

            var calculator = symbols.First(s => s.Name == "Calculator" && s.Kind == "class");
            Assert(calculator.Language == "csharp", "C# symbol language should be csharp");
            Assert(!string.IsNullOrWhiteSpace(calculator.QualifiedName), "C# symbol missing qualifiedName");
            Assert(!string.IsNullOrWhiteSpace(calculator.Signature), "C# symbol missing signature");

            Assert(
                result.Edges.Any(e => e.Kind == "instantiates" && e.Target == calculator.Id && e.Source == main.Id),
                "Calculator instantiation edge missing");
            Assert(
                result.Edges.Any(e => e.Kind == "references" && e.Name == "Calculator" && e.Target == calculator.Id && e.Source == main.Id),
                "Calculator reference edge missing");

            var derived = symbols.First(s => s.Name == "Derived" && s.Kind == "class");
            var baseType = symbols.First(s => s.Name == "BaseType" && s.Kind == "class");
            var work = symbols.First(s => s.Name == "IWork" && s.Kind == "interface");

            Assert(
                result.Edges.Any(e => e.Kind == "extends" && e.Source == derived.Id && e.Target == baseType.Id),
                "Derived extends edge missing");
            Assert(
                result.Edges.Any(e => e.Kind == "implements" && e.Source == derived.Id && e.Target == work.Id),
                "Derived implements edge missing");
            Assert(
                result.Edges.Any(e => e.Kind == "instantiates" && e.Target == derived.Id && e.Source == main.Id),
                "Derived instantiation edge missing");

            var docsDir = Path.Combine(dir, "docs");
            Directory.CreateDirectory(docsDir);
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Calculator\n\nOverview.\n");
            var docFile = Path.Combine(docsDir, "Calculator.md");
            File.WriteAllText(docFile, "# Calculator\n\nHandles arithmetic; see `Add`.\n");

            var docFiles = CrawlDocs(dir);
            Assert(docFiles.Length == 1, "docs crawl should include only docs/*.md");
            Assert(Path.GetFileName(docFiles[0]) == "Calculator.md", "docs md file missing");

            var docEdges = MarkdownIndexer.Index(docFiles, dir, symbols).Edges;
            Assert(
                docEdges.Any(e => e.Kind == "references" && e.Source == "" && e.Name == "Calculator"
                    && e.File.Contains("Calculator.md") && e.Target == calculator.Id),
                "markdown Calculator edge missing exact target");
            Assert(
                docEdges.Count(e => e.Kind == "references" && e.Source == "" && e.Name == "Add") >= addSymbols.Count,
                "markdown Add mention should target every matching Add symbol");

            var cacheIndex = Path.Combine(dir, "cache.json");
            File.WriteAllText(
                cacheIndex,
                JsonSerializer.Serialize(
                    new IndexDocument(4, dir, false, null, new List<IndexFile>(), result.Symbols, result.Edges),
                    WriteJson));

            var cacheLoadBefore = IndexCache.LoadCount;
            var cachedCalculator = IndexCache.Query(cacheIndex, "Calculator", 10);
            var cacheLoadAfterFirst = IndexCache.LoadCount;
            Assert(cacheLoadAfterFirst == cacheLoadBefore + 1, "cache should load on first query");
            AssertQueryEquals(QueryRepository("Calculator", cacheIndex, 10), cachedCalculator);

            var cachedAdd = IndexCache.Query(cacheIndex, "Add", 10);
            AssertQueryEquals(QueryRepository("Add", cacheIndex, 10), cachedAdd);
            Assert(IndexCache.LoadCount == cacheLoadAfterFirst, "subsequent queries should hit the cache");

            File.WriteAllText(
                cacheIndex,
                JsonSerializer.Serialize(
                    new IndexDocument(
                        4,
                        dir,
                        false,
                        null,
                        new List<IndexFile>(),
                        new List<SymbolDef>
                        {
                            new(
                                "rewritten",
                                "Rewritten",
                                "class",
                                "csharp",
                                "Demo.Rewritten",
                                "Rewritten.cs",
                                1,
                                1,
                                "public class Rewritten",
                                "public class Rewritten { }")
                        },
                        new List<SymbolEdge>()),
                    WriteJson));

            var loadBeforeRewrite = IndexCache.LoadCount;
            var rewritten = IndexCache.Query(cacheIndex, "Rewritten", 10);
            Assert(IndexCache.LoadCount == loadBeforeRewrite + 1, "cache should reload after rewrite");
            Assert(rewritten.Matches.Count == 1, "rewritten symbol should be returned");
            Assert(
                IndexCache.Query(cacheIndex, "Calculator", 10).Matches.Count == 0,
                "old symbol should no longer match after rewrite");

            var freshRoot = Path.Combine(dir, "fresh");
            Directory.CreateDirectory(freshRoot);
            var freshFile = Path.Combine(freshRoot, "One.cs");
            File.WriteAllText(freshFile, "public class One {}");
            var freshIndex = Path.Combine(dir, "fresh.json");
            IndexRepository(freshRoot, freshIndex, skipTs: true, tsIndexer: null);
            Assert(!EnsureFresh(freshIndex), "fresh index should not rebuild");

            File.AppendAllText(freshFile, "\npublic class Two {}\n");
            Assert(EnsureFresh(freshIndex), "modified file should trigger rebuild");
            Assert(QueryRepository("Two", freshIndex, 10).Matches.Count == 1, "new symbol missing after rebuild");

            var thirdFile = Path.Combine(freshRoot, "Three.cs");
            File.WriteAllText(thirdFile, "public class Three {}");
            Assert(EnsureFresh(freshIndex), "added file should trigger rebuild");
            Assert(QueryRepository("Three", freshIndex, 10).Matches.Count == 1, "added file symbol missing");

            File.Delete(thirdFile);
            Assert(EnsureFresh(freshIndex), "deleted file should trigger rebuild");
            Assert(QueryRepository("Three", freshIndex, 10).Matches.Count == 0, "deleted file symbol should be gone");

            var legacyIndex = Path.Combine(dir, "legacy.json");
            File.WriteAllText(
                legacyIndex,
                JsonSerializer.Serialize(
                    new { version = 3, symbols = result.Symbols, edges = result.Edges },
                    WriteJson));
            Assert(!EnsureFresh(legacyIndex), "legacy index should not auto-rebuild");

            var missingIndex = Path.Combine(dir, "missing.json");
            var missingThrew = false;
            try
            {
                IndexCache.Query(missingIndex, "Calculator", 10);
            }
            catch (FileNotFoundException)
            {
                missingThrew = true;
            }

            Assert(missingThrew, "querying a missing index should throw FileNotFoundException");

            var tsIndexer = FindTsIndexer();
            if (tsIndexer is not null && NodeAvailable())
            {
                File.WriteAllText(Path.Combine(dir, "BoxA.tsx"), """
                    export function Box() { return <div>A</div>; }
                    export function UseA() { return <Box />; }
                    export class WidgetA {}
                    export function MakeA() { return new WidgetA(); }
                    """);
                File.WriteAllText(Path.Combine(dir, "BoxB.tsx"), """
                    export function Box() { return <div>B</div>; }
                    export function UseB() { return <Box />; }
                    export class WidgetB {}
                    export function MakeB() { return new WidgetB(); }
                    """);

                var tsResult = RunTsIndexer(tsIndexer, dir);
                Assert(tsResult is not null, "TS indexer should return a result");

                var tsSymbols = tsResult!.Symbols;
                var boxSymbols = tsSymbols.Where(s => s.Name == "Box").ToList();
                Assert(boxSymbols.Count >= 2, "two Box symbols expected");
                Assert(
                    boxSymbols.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() == boxSymbols.Count,
                    "Box symbols must have distinct ids");
                Assert(boxSymbols.All(s => s.Language == "tsx"), "TSX symbol language should be tsx");
                Assert(boxSymbols.All(s => !string.IsNullOrWhiteSpace(s.QualifiedName)), "TSX symbol missing qualifiedName");
                Assert(boxSymbols.All(s => !string.IsNullOrWhiteSpace(s.Signature)), "TSX symbol missing signature");

                var boxCalls = tsResult.Edges.Where(e => e.Kind == "calls" && e.Name == "Box").ToList();
                Assert(boxCalls.Count >= 2, "two Box call edges expected");
                foreach (var box in boxSymbols)
                {
                    Assert(boxCalls.Any(e => e.Target == box.Id), "Box call edge missing exact target");
                }

                var useSymbols = tsSymbols.Where(s => s.Name is "UseA" or "UseB").ToList();
                Assert(useSymbols.Count == 2, "two Use* symbols expected");
                Assert(
                    boxCalls.All(e => useSymbols.Any(u => u.Id == e.Source)),
                    "Box call edge has an unknown source");

                var widgetSymbols = tsSymbols.Where(s => s.Name is "WidgetA" or "WidgetB").ToList();
                var makeSymbols = tsSymbols.Where(s => s.Name is "MakeA" or "MakeB").ToList();
                var instantiations = tsResult.Edges
                    .Where(e => e.Kind == "instantiates" && (e.Name == "WidgetA" || e.Name == "WidgetB"))
                    .ToList();

                Assert(widgetSymbols.Count == 2, "two Widget symbols expected");
                Assert(makeSymbols.Count == 2, "two Make symbols expected");
                Assert(instantiations.Count >= 2, "two Widget instantiation edges expected");
                foreach (var widget in widgetSymbols)
                {
                    Assert(
                        instantiations.Any(e => e.Target == widget.Id && makeSymbols.Any(m => m.Id == e.Source)),
                        "Widget instantiation edge missing exact target/source");
                }

                Assert(
                    tsResult.Edges.Count(e => e.Kind == "references" && e.Name == "Box") == 0,
                    "JSX Box use should be a calls edge, not a generic reference");
            }
            else
            {
                Console.WriteLine("[garf] selftest: skipping TS assertions (Node.js unavailable)");
            }

            Console.WriteLine("selftest ok");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertQueryEquals(QueryResult expected, QueryResult actual)
    {
        Assert(expected.Query == actual.Query, "query result query differs");
        Assert(expected.Matches.Count == actual.Matches.Count, "query result match count differs");
        for (var i = 0; i < expected.Matches.Count; i++)
        {
            Assert(expected.Matches[i].Symbol == actual.Matches[i].Symbol, "query result symbol differs");
            Assert(
                expected.Matches[i].Edges.SequenceEqual(actual.Matches[i].Edges),
                "query result edges differ");
        }
    }

    private static string? GetOption(string[] args, params string[] names)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (names.Contains(args[i], StringComparer.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, params string[] names)
        => args.Any(a => names.Contains(a, StringComparer.OrdinalIgnoreCase));

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;
}

public sealed record SymbolDef(
    string Id,
    string Name,
    string Kind,
    string Language,
    string QualifiedName,
    string File,
    int Line,
    int Column,
    string Signature,
    string Snippet);
public sealed record SymbolEdge(
    string Source,
    string Target,
    string Kind,
    string Name,
    string File,
    int Line,
    int Column,
    string Snippet);
public sealed record IndexResult(List<SymbolDef> Symbols, List<SymbolEdge> Edges);
public sealed record IndexFile(string Path, long Length, DateTimeOffset LastWriteTimeUtc);
public sealed record IndexDocument(
    int Version,
    string? Root,
    bool SkipTs,
    string? TsIndexer,
    List<IndexFile>? Files,
    List<SymbolDef>? Symbols,
    List<SymbolEdge>? Edges);
public sealed record IndexSummary(string Output, int SymbolCount, int EdgeCount);
public sealed record SymbolMatch(SymbolDef Symbol, List<SymbolEdge> Edges);
public sealed record QueryResult(string Query, List<SymbolMatch> Matches);

public static class Snippets
{
    public static string FromLines(string[] lines, int lineIndex, int afterLines, int maxChars = 500)
    {
        var start = Math.Max(0, lineIndex - 1);
        var end = Math.Min(lines.Length, lineIndex + afterLines + 1);
        var text = string.Join('\n', lines[start..end]).Trim();

        if (text.Length > maxChars)
        {
            text = text[..maxChars] + "…";
        }

        return text;
    }
}
