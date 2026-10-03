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
            "query" => RunQuery(args[1..]),
            "selftest" => RunSelfTest(),
            _ => Usage()
        };
    }

    private static int Usage()
    {
        Console.WriteLine("""
            garf - minimal code indexer for LLM token reduction

            index <root> [--output garf-index.json] [--skip-ts] [--ts-indexer path]
              Scan a repo and write a symbol/reference index.

            query <symbol> [--index garf-index.json] [--format md|json] [--limit 10] [--refs 10]
              Return matching symbol definitions and their references.

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
        var root = Path.GetFullPath(rootArg);

        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"root not found: {root}");
            return 1;
        }

        var csFiles = Crawl(root, new[] { ".cs" });
        var result = CSharpIndexer.Index(csFiles, root);

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
                        result.References.Concat(tsResult.References).ToList());
                }
            }
        }

        var docFiles = CrawlDocs(root);
        if (docFiles.Length > 0)
        {
            var docResult = MarkdownIndexer.Index(docFiles, root, result.Symbols);
            result = new IndexResult(
                result.Symbols,
                result.References.Concat(docResult.References).ToList());
        }

        var document = new IndexDocument(result.Symbols, result.References);
        File.WriteAllText(output, JsonSerializer.Serialize(document, WriteJson));

        Console.WriteLine(
            $"indexed {result.Symbols.Count} symbols, {result.References.Count} references -> {output}");
        return 0;
    }

    private static int RunQuery(string[] args)
    {
        var query = args.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal)) ?? "";
        var indexFile = GetOption(args, "--index", "-i") ?? "garf-index.json";
        var format = (GetOption(args, "--format", "-f") ?? "md").ToLowerInvariant();
        var limit = ParseInt(GetOption(args, "--limit", "-n"), 10);
        var refLimit = ParseInt(GetOption(args, "--refs"), 10);

        if (!File.Exists(indexFile))
        {
            Console.Error.WriteLine($"index not found: {indexFile}");
            return 1;
        }

        var document = JsonSerializer.Deserialize<IndexDocument>(File.ReadAllText(indexFile), ReadJson)
            ?? new IndexDocument(new List<SymbolDef>(), new List<SymbolRef>());

        var symbols = document.Symbols ?? new List<SymbolDef>();
        var references = document.References ?? new List<SymbolRef>();

        var matches = Match(symbols, query, limit)
            .Select(symbol => new SymbolMatch(
                symbol,
                references
                    .Where(r => string.Equals(r.Name, symbol.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(r => r.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Line)
                    .Take(refLimit)
                    .ToList()))
            .ToList();

        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(new QueryResult(query, matches), WriteJson));
        }
        else
        {
            PrintMarkdown(query, matches);
        }

        return 0;
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

    private static string? FindTsIndexer()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ts-indexer", "index.mjs"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ts-indexer", "index.mjs")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "ts-indexer", "index.mjs"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string[] Crawl(string root, string[] extensions)
    {
        var wanted = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "out", "coverage", ".next"
        };

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
                .Any(seg => skipped.Contains(seg)))
            .ToArray();
    }

    private static string[] CrawlDocs(string root)
    {
        var docFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "doc", "docs", "documents"
        };
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", "dist", "build", "out", "coverage", ".next"
        };

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
                return segments.Any(docFolders.Contains)
                    && !segments.Any(skipped.Contains);
            })
            .ToArray();
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

    private static void PrintMarkdown(string query, List<SymbolMatch> matches)
    {
        Console.WriteLine($"# Results for `{query}`");
        if (matches.Count == 0)
        {
            Console.WriteLine("No symbols found.");
            return;
        }

        foreach (var match in matches)
        {
            Console.WriteLine();
            Console.WriteLine($"## {match.Symbol.Name} ({match.Symbol.Kind}) — {match.Symbol.File}:{match.Symbol.Line}");
            Console.WriteLine("```");
            Console.WriteLine(match.Symbol.Snippet);
            Console.WriteLine("```");
            Console.WriteLine($"References ({match.References.Count}):");
            foreach (var reference in match.References)
            {
                Console.WriteLine($"- {reference.File}:{reference.Line} {FirstLine(reference.Snippet)}");
            }
        }
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

                public static class Program
                {
                    public static void Main()
                    {
                        var calc = new Calculator();
                        _ = calc.Add(1, 2);
                    }
                }
                """);

            var result = CSharpIndexer.Index(new[] { file }, dir);
            var names = result.Symbols.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

            Assert(names.Contains("Calculator"), "Calculator symbol missing");
            Assert(names.Contains("Add"), "Add symbol missing");
            Assert(result.References.Any(r => r.Name == "Calculator"), "Calculator reference missing");
            Assert(result.References.Any(r => r.Name == "Add"), "Add reference missing");

            var docsDir = Path.Combine(dir, "docs");
            Directory.CreateDirectory(docsDir);
            File.WriteAllText(Path.Combine(dir, "README.md"), "# Calculator\n\nOverview.\n");
            var docFile = Path.Combine(docsDir, "Calculator.md");
            File.WriteAllText(docFile, "# Calculator\n\nHandles arithmetic; see `Add`.\n");

            var docFiles = CrawlDocs(dir);
            Assert(docFiles.Length == 1, "docs crawl should include only docs/*.md");
            Assert(Path.GetFileName(docFiles[0]) == "Calculator.md", "docs md file missing");

            var docRefs = MarkdownIndexer.Index(docFiles, dir, result.Symbols).References;
            Assert(docRefs.Any(r => r.Name == "Calculator" && r.File.Contains("Calculator.md")), "markdown Calculator reference missing");
            Assert(docRefs.Any(r => r.Name == "Add" && r.File.Contains("Calculator.md")), "markdown Add reference missing");

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

public sealed record SymbolDef(string Name, string Kind, string File, int Line, int Column, string Snippet);
public sealed record SymbolRef(string Name, string File, int Line, int Column, string Snippet);
public sealed record IndexResult(List<SymbolDef> Symbols, List<SymbolRef> References);
public sealed record IndexDocument(List<SymbolDef>? Symbols, List<SymbolRef>? References);
public sealed record SymbolMatch(SymbolDef Symbol, List<SymbolRef> References);
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



