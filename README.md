# garf

Minimal code indexer for LLM token reduction. It scans a repo and emits a compact
`symbols -> references` JSON index, then answers `query` with only the matching
definition and where it is used — instead of dumping whole files.

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

## Index schema (v2)

- `garf-index.json` contains `version: 2`, `symbols`, and `references`.
- Each `symbol` has a stable `id`, `name`, `kind`, `language`, `qualifiedName`, `signature`, `file`, `line`, `column`, and `snippet`.
- Each `reference` has `target` (the exact symbol `id` it points to), `name`, `file`, `line`, `column`, and `snippet`.
- References are resolved semantically; same-named symbols get distinct ids and only link to their own references.
- v2 is a breaking change. Regenerate existing indexes with `index`.

