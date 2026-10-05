# Repository Guidelines

Guidance for contributing to **garf**, a minimal code indexer that scans a repository and emits a compact `symbol -> references` index. It makes finding code and its call sites faster and reduces LLM token use by returning only matching definitions and where they are used, instead of whole files.

## Technology Stack

- C# / .NET 10 CLI — the main executable, command parsing, JSON output, and C# indexing.
- Roslyn (`Microsoft.CodeAnalysis` / `Microsoft.CodeAnalysis.CSharp`) — parses C# into an AST for symbol and reference extraction.
- Node.js with the TypeScript compiler API (`typescript`) — indexes TS/TSX/JSX sources.
- `System.Text.Json` — reads/writes the compact `camelCase` JSON index.

## What It Does

- `index` scans a repo, extracts symbol definitions and references, and writes a compact JSON index (`garf-index.json` by default).
- `query` searches that index for a symbol and returns matching definitions plus their references as Markdown or JSON.
- `mcp` runs a local Model Context Protocol server over stdio, exposing `index` and `query` as tools for AI agents.
- C# is indexed in-process; TS/TSX/JSX are indexed by the Node helper and merged into the same index. Build assets and vendor directories are skipped.
- Markdown files under `doc`, `docs`, or `documents` folders are indexed as references to the symbols they mention, so querying a class or namespace also surfaces its documentation.

## Project Structure & Module Organization

- `src/Garf.Indexer/` — C# CLI. `Program.cs` parses commands, crawls files, runs querying, and defines the JSON records; `CSharpIndexer.cs` indexes C# using Roslyn; `MarkdownIndexer.cs` extracts symbol references from documentation Markdown; `McpServer.cs` implements the local MCP stdio server.
- `ts-indexer/` — Node.js helper. `index.mjs` indexes TS/JS/React using the TypeScript compiler API; `package.json` declares the `typescript` dependency.
- Root files: `README.md` for usage and `.gitignore` for generated paths.
- Generated `bin/`, `obj/`, and `node_modules/` directories are ignored and must not be committed.

## Index Format

- `IndexDocument` holds `symbols` and `references`, serialized with `camelCase` property names.
- `SymbolDef` records `name`, `kind`, `file`, `line`, `column`, and a source `snippet`.
- `SymbolRef` records `name`, `file`, `line`, `column`, and an optional snippet.
- Symbol kinds include C# declarations such as `class`, `interface`, `enum`, `enumMember`, `method`, `property`, `field`, `constructor`, `namespace`, `record`, `struct`, `delegate`, and `event`, plus TS/JSX-specific kinds such as `variable`, `component`, `type`, `module`, `getter`, and `setter`.

## Build, Test, and Development Commands

- `dotnet build src\Garf.Indexer\Garf.Indexer.csproj` — compiles the CLI.
- `dotnet run --project src\Garf.Indexer -- index <repo> -o index.json` — scans a repository and writes a symbol/reference index.
- `dotnet run --project src\Garf.Indexer -- query <symbol> -i index.json -f md` — returns matching definitions and their references.
- `dotnet run --project src\Garf.Indexer -- mcp` — starts the local MCP stdio server.
- `dotnet run --project src\Garf.Indexer -- selftest` — runs the built-in C# indexer check.
- `cd ts-indexer; npm install` — installs the TypeScript dependency for JS/TS indexing.
- `powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Runtime win-x64` — packages self-contained single-file release zip into `dist/`.
- `powershell -ExecutionPolicy Bypass -File install.ps1` — installs Garf to `~/.garf/bin` and adds to user PATH.

## Coding Style & Naming Conventions

- C#: four-space indentation, file-scoped namespaces, records, expression-bodied members, nullable enabled, and implicit usings.
- TypeScript: two-space indentation, ES modules, `const`, arrow functions, and semicolons.
- Names: PascalCase for C# types and methods, camelCase for locals and JS identifiers, and lower camelCase for symbol kinds such as `enumMember`.
- No formatter is configured; match the surrounding style in each file.

## Testing Guidelines

- There is no automated test project. Use the `selftest` command to validate the C# indexer.
- When changing symbol detection or reference matching, extend the self-test in `Program.cs` with a representative sample.

## Commit & Pull Request Guidelines

- This checkout has no Git history to infer conventions from; keep commits atomic with a short imperative subject and a body explaining what and why.
- Pull requests should state the change, commands run, and include sample output when CLI behavior changes.
