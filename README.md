# garf

Minimal code indexer for LLM token reduction. It scans a repo and emits a compact
`symbols -> typed edges` JSON index, then answers `query` with only the matching
definition and its directed relationships — instead of dumping whole files.

## Installation

### Windows (PowerShell)

Run in PowerShell:

```powershell
irm https://raw.githubusercontent.com/namkhanh307/Garf/main/install.ps1 | iex
```

### Linux & macOS (Bash)

```bash
curl -fsSL https://raw.githubusercontent.com/namkhanh307/Garf/main/install.sh | bash
```

**What the installer does:**
- Downloads precompiled self-contained binaries to `~/.garf/bin` (`C:\Users\{username}\.garf\bin`).
- Injects `~/.garf/bin` into your permanent user `PATH` environment variable.
- Bundles `ts-indexer` with AST dependencies so TS/TSX/JSX works out of the box if Node.js is present.
- **Zero prerequisites**: You do **not** need the .NET SDK installed.

To uninstall:
```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

---

## Development Setup

If building or contributing from source (requires .NET 10 SDK and Node.js):

```powershell
dotnet build src\Garf.Indexer\Garf.Indexer.csproj
cd ts-indexer
npm install
cd ..
```

To package release bundles locally:
```powershell
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Runtime win-x64 -OutputDir dist
```

## Use

Once installed, `garf` is globally available from any terminal:

```powershell
# Index a repository
garf index <path-to-repo> -o garf-index.json

# Query symbols and relationships
garf query Calculate -i garf-index.json -f md

# Continuous watching
garf watch <path-to-repo> -o garf-index.json
```

- `query` supports `--format md|json`, `--limit`, `--refs`.
- `query` auto-rebuilds the index when it detects the source changed; `watch` keeps it fresh on file saves.
- Markdown files under `doc`, `docs`, or `documents` folders are indexed as references, so `query <class>` also surfaces the doc block that describes it.
- C# is indexed in-process with Roslyn; TS/TSX/JSX source files are indexed by `ts-indexer/index.mjs` (skipped automatically if Node is unavailable).

## MCP Server

Garf can run as a local Model Context Protocol (MCP) server over stdio, exposing `index` and `query` as tools for AI agents.

```powershell
garf mcp
```

Register it in an MCP client using `garf` or its full installed path:

```json
{
  "mcpServers": {
    "garf": {
      "command": "garf",
      "args": ["mcp"]
    }
  }
}
```

Or explicit executable path (`~/.garf/bin/garf.exe` on Windows):

```json
{
  "mcpServers": {
    "garf": {
      "command": "C:\\Users\\<username>\\.garf\\bin\\garf.exe",
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

- `index` — scan `root` and write an index (`output`, `skipTs`, `tsIndexer`, and `watch` are optional).
- `query` — find `symbol` in an index (`index`, `format`, `limit`, and `refs` are optional); auto-rebuilds a stale index.

## Index schema (v4)

- `garf-index.json` contains `version: 4`, `root`, `skipTs`, `tsIndexer`, `files`, `symbols`, and `edges`.
- `files` records each indexed file's relative `path`, `length`, and `lastWriteTimeUtc` so `query` can detect staleness.
- Each `symbol` has a stable `id`, `name`, `kind`, `language`, `qualifiedName`, `signature`, `file`, `line`, `column`, and `snippet`.
- Each `edge` has `source` (the containing symbol `id`, empty for file-level links), `target` (the exact symbol `id` it points to), `kind`, `name`, `file`, `line`, `column`, and `snippet`.
- `kind` is one of `calls`, `instantiates`, `extends`, `implements`, `imports`, or `references`; `query` groups these into callers, callees, instantiations, base/derived types, imports, and references.
- v4 is a breaking change. Regenerate existing indexes with `index`.

