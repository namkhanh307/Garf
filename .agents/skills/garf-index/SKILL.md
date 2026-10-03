---
name: garf-index
description: Regenerate this repo's garf-index.json before or after changing code. Use when symbols were added, renamed, moved, or queries return stale or missing results.
---

# Garf index

Keep `garf-index.json` in the repo root current so `garf-query` reflects the
latest source. The file is gitignored and should never be committed.

## Regenerate

Run from the repo root:

```powershell
dotnet run --project src\Garf.Indexer -- index . -o garf-index.json
```

Run this after meaningful changes to `src/Garf.Indexer`, `ts-indexer`, or any
Markdown under `doc`, `docs`, or `documents`.

## When indexing fails

- If TS/TSX/JSX dependencies are missing and those files matter, install them
  first: `cd ts-indexer; npm install`.
- When changing indexer behavior, validate with:
  `dotnet run --project src\Garf.Indexer -- selftest`.
