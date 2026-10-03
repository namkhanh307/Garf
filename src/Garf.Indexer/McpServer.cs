using System.Text.Json;
using System.Text.Json.Nodes;

namespace Garf.Indexer;

public static class McpServer
{
    private const string ProtocolVersion = "2025-06-18";
    private const string ServerName = "garf";
    private const string ServerVersion = "0.1.0";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static IndexWatcher? _watcher;

    private static readonly JsonArray Tools =
    [
        new JsonObject
        {
            ["name"] = "index",
            ["description"] = "Scan a repository and write a garf symbol/reference index.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["root"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Absolute or relative path to the repository root to scan."
                    },
                    ["output"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Output index path. Defaults to garf-index.json."
                    },
                    ["skipTs"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Skip TypeScript/TSX/JSX indexing. Defaults to false."
                    },
                    ["tsIndexer"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Path to ts-indexer/index.mjs. Auto-detected when omitted."
                    },
                    ["watch"] = new JsonObject
                    {
                        ["type"] = "boolean",
                        ["description"] = "Keep the index fresh on file changes after indexing. Defaults to false."
                    }
                },
                ["required"] = new JsonArray("root")
            }
        },
        new JsonObject
        {
            ["name"] = "query",
            ["description"] = "Search a garf index for symbol definitions and their typed edges.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["symbol"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Symbol name to find."
                    },
                    ["index"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Index file path. Defaults to garf-index.json."
                    },
                    ["format"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray("md", "json"),
                        ["description"] = "Output format. Defaults to md."
                    },
                    ["limit"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum matching symbols. Defaults to 10."
                    },
                    ["refs"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["description"] = "Maximum edges per group. Defaults to 10."
                    }
                },
                ["required"] = new JsonArray("symbol")
            }
        }
    ];

    public static int Run()
    {
        while (true)
        {
            var line = Console.In.ReadLine();
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var id = default(JsonElement);
            try
            {
                using var message = JsonDocument.Parse(line);
                var root = message.RootElement;
                id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;
                Handle(id, root);
            }
            catch (Exception ex)
            {
                if (id.ValueKind != JsonValueKind.Undefined)
                {
                    WriteError(id, -32700, ex.Message);
                }
            }
        }

        return 0;
    }

    private static void Handle(JsonElement id, JsonElement root)
    {
        var method = root.TryGetProperty("method", out var methodElement)
            ? methodElement.GetString()
            : null;

        if (method is null)
        {
            if (id.ValueKind != JsonValueKind.Undefined)
            {
                WriteError(id, -32600, "Invalid request: missing method.");
            }

            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    WriteResult(id, InitializeResult());
                    break;
                case "notifications/initialized":
                    break;
                case "ping":
                    WriteResult(id, new JsonObject());
                    break;
                case "tools/list":
                    WriteResult(id, new JsonObject { ["tools"] = Tools });
                    break;
                case "tools/call":
                    HandleToolCall(id, root);
                    break;
                case "shutdown":
                    WriteResult(id, new JsonObject());
                    break;
                default:
                    if (id.ValueKind != JsonValueKind.Undefined)
                    {
                        WriteError(id, -32601, $"Method not found: {method}");
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            if (id.ValueKind != JsonValueKind.Undefined)
            {
                WriteError(id, -32603, ex.Message);
            }
        }
    }

    private static void HandleToolCall(JsonElement id, JsonElement root)
    {
        if (!root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String)
        {
            WriteToolError(id, "Invalid tools/call request: params.name is required.");
            return;
        }

        var name = nameElement.GetString()!;
        var arguments = parameters.TryGetProperty("arguments", out var argumentsElement)
            ? argumentsElement
            : default;

        try
        {
            switch (name)
            {
                case "index":
                    HandleIndex(id, arguments);
                    break;
                case "query":
                    HandleQuery(id, arguments);
                    break;
                default:
                    WriteToolError(id, $"Unknown tool: {name}");
                    break;
            }
        }
        catch (Exception ex)
        {
            WriteToolError(id, ex.Message);
        }
    }

    private static void HandleIndex(JsonElement id, JsonElement arguments)
    {
        if (!TryGetString(arguments, "root", out var root) || string.IsNullOrWhiteSpace(root))
        {
            WriteToolError(id, "Missing required argument: root");
            return;
        }

        var output = GetString(arguments, "output") ?? "garf-index.json";
        var skipTs = GetBoolean(arguments, "skipTs", false);
        var tsIndexer = GetString(arguments, "tsIndexer") ?? Program.FindTsIndexer();
        var watch = GetBoolean(arguments, "watch", false);
        var rootFull = Path.GetFullPath(root);
        var outputFull = Path.GetFullPath(output);
        var summary = Program.IndexRepository(rootFull, outputFull, skipTs, tsIndexer);
        IndexCache.Invalidate(outputFull);

        if (watch)
        {
            _watcher?.Dispose();
            _watcher = new IndexWatcher(rootFull, outputFull, skipTs, tsIndexer, 250);
            _watcher.Start();
        }

        WriteToolResult(
            id,
            $"indexed {summary.SymbolCount} symbols, {summary.EdgeCount} edges -> {summary.Output}");
    }

    private static void HandleQuery(JsonElement id, JsonElement arguments)
    {
        if (!TryGetString(arguments, "symbol", out var symbol) || string.IsNullOrWhiteSpace(symbol))
        {
            WriteToolError(id, "Missing required argument: symbol");
            return;
        }

        var index = GetString(arguments, "index") ?? "garf-index.json";
        var format = (GetString(arguments, "format") ?? "md").ToLowerInvariant();
        var limit = GetInt32(arguments, "limit", 10);
        var refLimit = GetInt32(arguments, "refs", 10);

        Program.EnsureFresh(index);
        var result = IndexCache.Query(index, symbol, limit);
        var text = format == "json"
            ? JsonSerializer.Serialize(result, Json)
            : Program.RenderMarkdown(result, refLimit);

        WriteToolResult(id, text);
    }

    private static JsonObject InitializeResult() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject
            {
                ["listChanged"] = false
            }
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = ServerName,
            ["version"] = ServerVersion
        }
    };

    private static void WriteResult(JsonElement id, object? result)
    {
        if (id.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }, Json));
    }

    private static void WriteError(JsonElement id, int code, string message)
    {
        if (id.ValueKind == JsonValueKind.Undefined)
        {
            return;
        }

        Console.WriteLine(JsonSerializer.Serialize(
            new { jsonrpc = "2.0", id, error = new { code, message } },
            Json));
    }

    private static void WriteToolResult(JsonElement id, string text)
        => WriteResult(id, new { content = new[] { new { type = "text", text } } });

    private static void WriteToolError(JsonElement id, string message)
        => WriteResult(id, new { isError = true, content = new[] { new { type = "text", text = message } } });

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? GetString(JsonElement obj, string name)
        => TryGetString(obj, name, out var value) ? value : null;

    private static bool GetBoolean(JsonElement obj, string name, bool fallback)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? element.GetBoolean()
                : fallback;

    private static int GetInt32(JsonElement obj, string name, int fallback)
        => obj.ValueKind == JsonValueKind.Object
            && obj.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
                ? value
                : fallback;
}
