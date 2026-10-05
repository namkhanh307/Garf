---
name: using-garf
description: Use when indexing, exploring, or querying symbols, definitions, callers, callees, or references in any repository using Garf CLI or Garf MCP.
---

# Using Garf in Any Repository

## Overview

Garf is a lightweight code indexer and token-saving query engine for C#, TypeScript/TSX/JSX, and Markdown documentation. It builds a directed symbol-and-relationship index (`calls`, `instantiates`, `extends`, `implements`, `imports`, `references`) so agents can query targeted signatures, definitions, and call sites without dumping whole files or running broad regex greps.

## When to Use

- Finding definitions of functions, classes, interfaces, types, components, or methods across a codebase.
- Tracing call graphs: finding callers of a function, what a method calls, or implementations of an interface.
- Exploring unfamiliar repositories without exhausting LLM context limits.
- Connecting documentation in `docs/` or `doc/` to actual code symbol definitions.
- **When NOT to use:**
  - Repositories written purely in languages not yet supported (e.g. pure Python, Go, Rust without C# or TS/JS).
  - Editing files (Garf indexes and queries code; use standard editing tools for modifications).

## Quick Reference

| Task | CLI Command | MCP Tool Call |
|------|-------------|---------------|
| **Index repository** | `garf index . -o garf-index.json` | `index(root=".")` |
| **Query symbol (Markdown)** | `garf query <symbol> -i garf-index.json -f md` | `query(symbol="<symbol>", format="md")` |
| **Query symbol (JSON)** | `garf query <symbol> -i garf-index.json -f json` | `query(symbol="<symbol>", format="json")` |
| **Trace with limits** | `garf query <symbol> --limit 5 --refs 20` | `query(symbol="<symbol>", limit=5, refs=20)` |
| **Watch & auto-update** | `garf watch . -o garf-index.json` | `index(root=".", watch=true)` |

## Setup & Access

You do not need to install Garf inside the target repository. You run it from its built binary or source checkout pointing to the target directory.

### Executable Paths on This Machine
- **Compiled binary (Recommended)**:
  - `D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe`
- **Source CLI via dotnet**:
  - `dotnet run --project D:\Garf\src\Garf.Indexer -- <args>`

---

### Option 1: Configure Garf as an MCP Server (Instant Agent Access)

Register `garf` in your AI agent's MCP configuration so `index` and `query` are available as first-class tools in any project.

#### For Claude Code, Antigravity, Cursor, or Gemini CLI (`.mcp.json` or agent config):
```json
{
  "mcpServers": {
    "garf": {
      "command": "D:\\Garf\\src\\Garf.Indexer\\bin\\Release\\net10.0\\garf.exe",
      "args": ["mcp"]
    }
  }
}
```

#### For Codex CLI (`config.toml`):
```toml
[mcp_servers.garf]
command = "D:\\Garf\\src\\Garf.Indexer\\bin\\Release\\net10.0\\garf.exe"
args = ["mcp"]
```

#### Calling MCP Tools:
- **Scan repository**: Call `index` with `{"root": "."}` (or absolute path to the target repo).
- **Search symbol**: Call `query` with `{"symbol": "TargetSymbol", "format": "md"}`.
- MCP `query` automatically detects source file changes and regenerates the index when stale.

---

### Option 2: Direct CLI Execution (Zero Configuration)

From the root of any target repository in your terminal:

```powershell
# 1. Scan and index target repo
D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe index . -o garf-index.json

# 2. Query a symbol
D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe query ProcessOrder -i garf-index.json -f md
```

---

## Step-by-Step Workflow for Target Repos

### Step 1: Add Index to .gitignore
In the target repo's `.gitignore`, add:
```gitignore
# Garf code index
garf-index.json
```
`garf-index.json` is a local cache file and should never be committed.

### Step 2: Index the Target Repository
Run `index` from the target project root:
- **CLI**:
  ```powershell
  D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe index . -o garf-index.json
  ```
- **MCP**:
  ```json
  {
    "name": "index",
    "arguments": {
      "root": "."
    }
  }
  ```
- *Tip:* If Node.js is unavailable or TypeScript indexing is not needed, pass `--skip-ts` (CLI) or `"skipTs": true` (MCP).

### Step 3: Query Symbols and Edges
Query returns exact matching declarations and their relational edges:
- **Callers**: Who calls this method/function.
- **Callees**: What functions/methods this symbol calls.
- **Instantiations**: Where classes are instantiated.
- **Hierarchy**: Base types (`extends`), implemented interfaces (`implements`), and derived types.
- **Imports**: Modules and namespaces imported.
- **Documentation references**: Mentions in Markdown files located under `doc/`, `docs/`, or `documents/`.

Example CLI:
```powershell
D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe query OrderController -i garf-index.json -f md --limit 5 --refs 10
```

Example MCP:
```json
{
  "name": "query",
  "arguments": {
    "symbol": "OrderController",
    "format": "md",
    "limit": 5,
    "refs": 10
  }
}
```

### Step 4: Automatic Freshness & Staleness Detection
- You do NOT need to manually re-index after every file edit.
- Garf v4 records file lengths and modification timestamps in `garf-index.json`.
- Whenever you run `query` (via CLI or MCP), Garf checks if indexed files were modified, created, or deleted. If stale, it automatically regenerates the index before answering.
- During intensive multi-file editing sessions, run `watch` in the background:
  ```powershell
  D:\Garf\src\Garf.Indexer\bin\Release\net10.0\garf.exe watch . -o garf-index.json
  ```

---

## Common Mistakes & Troubleshooting

| Issue | Cause | Fix |
|-------|-------|-----|
| `Cannot find package 'typescript'` | `npm install` not run in `ts-indexer` | Run `cd D:\Garf\ts-indexer; npm install` or pass `--skip-ts`. |
| `Index lacks freshness metadata` | Older v3 index format detected | Run `index` once to generate a v4 index with file tracking. |
| Query returns no results | Exact casing or partial name issue | Use broader search term or increase `--limit` (default is 10). |
| Build artifacts being indexed | Output folders named non-standardly | Garf automatically ignores `bin`, `obj`, `node_modules`, `.git`, `dist`, `build`, `out`, `coverage`, `.next`. Keep build output in standard directories. |
| Target repo path with spaces | Unquoted paths | Always wrap paths in double quotes: `garf index "C:\My Repos\App"`. |
