# IV.RagToolkit

A composable .NET 9 toolkit for building RAG (Retrieval-Augmented Generation) pipelines. Provides infrastructure and base abstractions — every step is swappable via dependency injection without touching pipeline logic.

## Packages

| Package | Description |
|---|---|
| `IV.RagToolkit.Abstractions` | Core interfaces and models. No dependencies. Domain projects depend only on this. |
| `IV.RagToolkit.Core` | Pipeline orchestration and `FixedSizeChunker`. Depends only on Abstractions. |
| `IV.RagToolkit.Ollama` | `IEmbedder` backed by the Ollama `/api/embed` endpoint. |
| `IV.RagToolkit.Postgres` | `IVectorStore` and `IRetriever` backed by PostgreSQL + pgvector. |

## Quick start

```csharp
services.AddRagToolkit()
    .AddFixedSizeChunker(o => o.ChunkSize = 512)
    .AddOllamaEmbedder(o =>
    {
        o.Endpoint = "http://localhost:11434";
        o.EmbeddingModel = "nomic-embed-text";
    })
    .AddPostgresVectorStore(o =>
    {
        o.ConnectionString = "Host=localhost;Database=rag;Username=postgres;Password=postgres";
        o.VectorDimension = 768;
    });
```

Then inject `IRagPipeline`:

```csharp
// Ingest
await pipeline.IngestAsync(new Document("your text here"));

// Query
var results = await pipeline.QueryAsync("your question");

foreach (var result in results)
    Console.WriteLine($"[{result.Score:F2}] {result.Chunk.Text}");
```

## Prerequisites

- .NET 9 SDK
- PostgreSQL with the `vector` extension installed (`CREATE EXTENSION IF NOT EXISTS vector`)
- Ollama running locally with an embedding model pulled (`ollama pull nomic-embed-text`)
- Docker (for integration tests)

## Core concepts

### Pipeline flow

```
Ingest:  Document → IChunker → IEmbedder → IVectorStore
Query:   string   → IEmbedder → IRetriever → IReadOnlyList<SearchResult>
```

### Similarity score

`SearchResult.Score` is cosine similarity in `[-1, 1]`:
- `1.0` — identical meaning
- `0.0` — unrelated (orthogonal)
- `-1.0` — opposite meaning

`RetrievalOptions.MinScore` defaults to `0.0` — results with score `<= 0` are excluded.

### Adding a new provider

Create a new project referencing only `IV.RagToolkit.Abstractions`, implement the relevant interface, and register via a `RagToolkitBuilder` extension:

```csharp
// IV.RagToolkit.Qdrant
public static RagToolkitBuilder AddQdrantVectorStore(
    this RagToolkitBuilder builder,
    Action<QdrantOptions> configure) { ... }
```

The consumer swaps one line in DI — no other code changes.

## Solution structure

```
src/
  IV.RagToolkit.Abstractions/
  IV.RagToolkit.Core/
  IV.RagToolkit.Ollama/
  IV.RagToolkit.Postgres/
tests/
  unit/                          ← no infrastructure required
  integration/                   ← Docker (Testcontainers)
  e2e/                           ← live Ollama + Postgres
automation/                      ← build and publish scripts
```

## Building

```bash
dotnet build IV.RagToolkit.sln
```

## Testing

```bash
# Unit tests — fast, no infrastructure
dotnet test IV.RagToolkit.Unit.slnf

# Integration tests — requires Docker
dotnet test IV.RagToolkit.Integration.slnf

# E2E tests — requires Ollama running at http://localhost:11434
dotnet test IV.RagToolkit.E2E.slnf
```

## Extending retrieval options

```csharp
var results = await pipeline.QueryAsync(
    "your question",
    new RetrievalOptions
    {
        TopK = 10,       // maximum results to return
        MinScore = 0.7f  // only return highly relevant results
    });
```
