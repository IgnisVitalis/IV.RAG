# CLAUDE.md

Instructions for Claude Code when working in this repository.

## Project

`IV.RagToolkit` — a .NET 9 NuGet toolkit providing infrastructure and base classes for RAG (Retrieval-Augmented Generation) pipelines. Designed to be composable: consumers swap providers via DI without touching pipeline logic.

**Development stage:** pre-1.0, active development. Backward compatibility is not required — breaking changes to any layer including `Abstractions` are acceptable.

## Solution structure

```
IV.RagToolkit.sln            ← open this in IDE
src/
  IV.RagToolkit.Abstractions ← interfaces and models only, no implementations
  IV.RagToolkit.Core         ← pipeline orchestration, base implementations
  IV.RagToolkit.Ollama       ← IEmbedder + IRetriever backed by Ollama HTTP API
  IV.RagToolkit.Postgres     ← IVectorStore backed by pgvector via Npgsql
tests/
  unit/                      ← no infrastructure required, fast
  integration/               ← requires Docker (Postgres via Testcontainers, Ollama external)
automation/                  ← scripts (build, pack, publish)
Directory.Build.props        ← shared: TargetFramework, Nullable, TreatWarningsAsErrors
Directory.Packages.props     ← central NuGet version management
```

## Dependency rule

`Abstractions` has no project references. `Core`, `Ollama`, `Postgres` reference only `Abstractions`. Nothing in `src/` references sibling providers. Consumers wire providers together at startup.

## Common commands

```bash
# Build
dotnet build IV.RagToolkit.sln

# Unit tests (no infra needed)
dotnet test tests/unit/

# Integration tests (requires Docker)
dotnet test tests/integration/

# Pack a specific package
dotnet pack src/IV.RagToolkit.Abstractions/ -c Release
```

## Conventions

- All packages share the version defined in `Directory.Build.props`
- Never add `Version` attributes to `<PackageReference>` — versions live in `Directory.Packages.props`
- `TreatWarningsAsErrors` is on; all public APIs need XML doc comments
- Test projects set `<IsTestProject>true</IsTestProject>` to opt out of doc generation
- No comments unless the WHY is non-obvious
- No commit unless explicitly asked

## Behavior rules

- If a request contains a question, discuss before modifying code
- Be a constructive skeptic on design decisions
- Prefer small, focused changes
- Breaking changes to `Abstractions` are acceptable — the project is pre-1.0
- Use existing patterns and naming conventions in the repo
