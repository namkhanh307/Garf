---
name: garf-query
description: Find symbols and their references in this repo using garf-index.json. Use before grep or reading whole files when locating definitions, call sites, or implementation details.
---

# Garf query

Use this repo's own indexer for code navigation. It returns matching symbol
definitions plus their reference sites with smaller context than reading or
grepping whole files.

## Before querying

If `garf-index.json` is missing or the code has changed since the last index,
regenerate it first:

```powershell
dotnet run --project src\Garf.Indexer -- index . -o garf-index.json
```

## Query

```powershell
dotnet run --project src\Garf.Indexer -- query <symbol> -i garf-index.json -f md --limit 10 --refs 10
```

- Query by exact or partial symbol name, for example `CSharpIndexer`,
  `RunQuery`, or `SymbolDef`.
- Use `-f json` when the result will be parsed programmatically.
- Increase `--limit` or `--refs` for broad searches; use smaller values when
  the matching set is large.

## Coverage

The index includes C# symbols indexed by Roslyn, TS/TSX/JSX symbols indexed by
the Node helper, and Markdown references under `doc`, `docs`, or `documents`.
Definitions include file, line, column, signature, and snippet; references
include their exact symbol target plus call-site location and snippet.
