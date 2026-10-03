# garf

Minimal code indexer for LLM token reduction. It scans a repo and emits a compact
`symbols -> typed edges` JSON index, then answers `query` with only the matching
definition and its directed relationships — instead of dumping whole files.

## Setup

Requires .NET 10 SDK and Node.js (for TS/React indexing).

```powershell
dotnet build src\Garf.Indexer\Garf.Indexer.csproj
cd ts-indexer
npm install
```

## Use

```powershell
# build + run from source
dotnet run --project src\Garf.Indexer -- index <repo> -o index.json
dotnet run --project src\Garf.Indexer -- query Calculate -i index.json -f md

# or the built exe
.\src\Garf.Indexer\bin\Debug\net10.0\garf.exe index <repo>
.\src\Garf.Indexer\bin\Debug\net10.0\garf.exe query Calculate
```

- `query` supports `--format md|json`, `--limit`, `--refs`.
- Markdown files under `doc`, `docs`, or `documents` folders are indexed as references, so `query <class>` also surfaces the doc block that describes it.
- C# is indexed in-process with Roslyn; TS/TSX/JSX source files are indexed by
  	s-indexer\\index.mjs (skipped automatically if Node is unavailable). Plain .js/.mjs/.cjs build assets are ignored to avoid indexing minified bundles; source .ts/.tsx/.jsx files are indexed.

## MCP server

Garf can run as a local Model Context Protocol (MCP) server over stdio, exposing
`index` and `query` as tools for AI agents.

```powershell
dotnet run --project src\Garf.Indexer -- mcp
# or the built exe
.\src\Garf.Indexer\bin\Debug\net10.0\garf.exe mcp
```

Register it in an MCP client using the built exe:

```json
{
  "mcpServers": {
    "garf": {
      "command": "C:\\path\\to\\Garf\\src\\Garf.Indexer\\bin\\Debug\\net10.0\\garf.exe",
      "args": ["mcp"]
    }
  }
}
```

Codex clients can add the same process to their MCP server configuration:

```toml
[mcp_servers.garf]
command = "dotnet"
args = ["run", "--project", "C:\\path\\to\\Garf\\src\\Garf.Indexer\\Garf.Indexer.csproj", "--", "mcp"]
cwd = "C:\\path\\to\\Garf"
```

Available tools:

- `index` — scan `root` and write an index (`output`, `skipTs`, and `tsIndexer` are optional).
- `query` — find `symbol` in an index (`index`, `format`, `limit`, and `refs` are optional).

## Index schema (v3)

- `garf-index.json` contains `version: 3`, `symbols`, and `edges`.
- Each `symbol` has a stable `id`, `name`, `kind`, `language`, `qualifiedName`, `signature`, `file`, `line`, `column`, and `snippet`.
- Each `edge` has `source` (the containing symbol `id`, empty for file-level links), `target` (the exact symbol `id` it points to), `kind`, `name`, `file`, `line`, `column`, and `snippet`.
- `kind` is one of `calls`, `instantiates`, `extends`, `implements`, `imports`, or `references`; `query` groups these into callers, callees, instantiations, base/derived types, imports, and references.
- v3 is a breaking change. Regenerate existing indexes with `index`.

