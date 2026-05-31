# AGENTS.md

Instructions for AI coding agents working in this repository.

## Development stage

Pre-1.0, active development. Backward compatibility is not required — breaking changes to any layer including `IV.RagToolkit.Abstractions` are acceptable.

## Tool usage rules

- Do NOT run commands in parallel
- Run at most ONE shell command at a time
- Never scan the entire repository
- Never auto-run formatting or analysis tools

## Must follow

- If a request contains a question, discuss before modifying code
- Be a constructive skeptic when discussing solutions and ideas
- Follow existing patterns and conventions in the repo
- Breaking changes to `IV.RagToolkit.Abstractions` are acceptable — the project is pre-1.0
- Prefer small, focused changes
- Do not commit unless asked

## Code style

- C# only; follow existing formatting and naming conventions
- No comments unless the WHY is non-obvious
- All public APIs in `src/` require XML doc comments (`TreatWarningsAsErrors` is on)
- Never add `Version` to `<PackageReference>` — all versions are managed in `Directory.Packages.props`

## Test commands

```bash
# Unit tests (no infrastructure required)
dotnet test tests/unit/

# Integration tests (requires Docker for Postgres via Testcontainers)
dotnet test tests/integration/
```

## Workflow

- Summarize changes and list affected files after completing a task
