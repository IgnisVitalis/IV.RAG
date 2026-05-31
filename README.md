# IV.RagToolkit

A composable .NET 9 toolkit for building RAG (Retrieval-Augmented Generation) pipelines. Provides infrastructure and base abstractions — every step is swappable via dependency injection without touching pipeline logic.

> **Pre-1.0 — active development. Breaking changes may occur between versions.**

## Packages

| Package | Description |
|---|---|
| `IV.RagToolkit.Abstractions` | Core interfaces and models. No dependencies. Domain projects depend only on this. |
| `IV.RagToolkit.Core` | Pipeline orchestration and `FixedSizeChunker`. Depends only on Abstractions. |
| `IV.RagToolkit.Ollama` | `IEmbedder` backed by the Ollama `/api/embed` endpoint. |
| `IV.RagToolkit.Postgres` | `IVectorStore` and `IRetriever` backed by PostgreSQL + pgvector. |

## Quick start

### 1. Register services

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

### 2. Define your document type

Every document type must subclass `Document` and provide a stable `Origin` that uniquely identifies where it came from.

```csharp
using System.Diagnostics.CodeAnalysis;

public record InvoiceDocument : Document
{
    // One Guid per document type — generated once, never changes
    private static readonly Guid SourceId = new("a34a3c8c-9a31-45f0-b5f7-d83b4ad62d11");

    public override Origin Source { get; }

    [SetsRequiredMembers]
    public InvoiceDocument(string text, string invoiceId)
    {
        Text = text;
        Source = new Origin(SourceId, "Invoice", invoiceId);
    }
}
```

### 3. Ingest and query

```csharp
// Ingest
await pipeline.IngestAsync(new InvoiceDocument(invoiceText, invoiceId: "INV-001"));

// Query
var results = await pipeline.QueryAsync("your question");

foreach (var result in results)
    Console.WriteLine($"[{result.Score:F2}] {result.Chunk.Text}");
```

### 4. Replace a document

When a document changes, delete its old chunks before re-ingesting:

```csharp
await vectorStore.DeleteByDocumentAsync(doc.Source);
await pipeline.IngestAsync(updatedDoc);
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

### Document identity

Every `Document` subclass carries a `Source` property of type `Document.Origin`:

```csharp
public sealed record Origin(Guid SourceId, string DocumentType, string DocumentId)
```

| Field | Purpose |
|---|---|
| `SourceId` | Identifies the source system (one Guid per document class, stable forever) |
| `DocumentType` | Identifies the document category within that system (`"Invoice"`, `"Contract"`) |
| `DocumentId` | Identifies the specific document instance (`"INV-001"`) |

Origin is propagated automatically to every `Chunk` produced during ingestion and stored as dedicated columns in the vector store. This enables `DeleteByDocumentAsync` to atomically remove all chunks belonging to a specific document.

### Chunk enrichment

The pipeline automatically enriches each chunk before storage:

| Property | Set by | Value |
|---|---|---|
| `Chunk.Id` | `RagPipeline` | Random `Guid` |
| `Chunk.Embedding` | `RagPipeline` | Output of `IEmbedder` |
| `Chunk.Origin` | `IChunker` | Copied from `Document.Source` |
| `Chunk.ChunkIndex` | `RagPipeline` | Zero-based position within the document |

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
