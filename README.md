# IV.RAG

A composable .NET 9 toolkit for building RAG (Retrieval-Augmented Generation) pipelines. Provides infrastructure and base abstractions — every step is swappable via dependency injection without touching pipeline logic.

> **Pre-1.0 — active development. Breaking changes may occur between versions.**

## Packages

| Package | Description |
|---|---|
| `IV.RAG.Abstractions` | Core interfaces and models. No dependencies. Domain projects depend only on this. |
| `IV.RAG.Core` | Pipeline orchestrators (`RagPipeline`, `RetrievalPipeline`, `AnswerPipeline`). Depends only on Abstractions. |
| `IV.RAG.Ingestion` | Document types and chunkers (`PlainTextDocument`, `FixedSizeChunker`, `SentenceChunker`). |
| `IV.RAG.Ollama` | `IEmbedder` and `IGenerator` backed by Ollama (`/api/embed`, `/api/chat`). |
| `IV.RAG.Postgres` | `IVectorStore` and `IRetriever` backed by PostgreSQL + pgvector. |
| `IV.RAG.Remote.Http` | `IRetrievalPipeline` proxy — forwards queries to a remote retrieval server over HTTP. |

## Deployment topologies

### Full local pipeline

Everything runs in one process: ingestion, retrieval, and generation.

```csharp
services.AddRagToolkit()
    .AddSentenceChunker(o => o.MaxChunkSize = 512)
    .AddOllamaEmbedder(o => o.EmbeddingModel = "nomic-embed-text")
    .AddOllamaGenerator(o =>
    {
        o.GenerationModel = "llama3.2";
        o.SystemPrompt = "Answer using only the provided context.";
    })
    .AddPostgresVectorStore(o =>
    {
        o.ConnectionString = "Host=localhost;Database=rag;Username=postgres;Password=postgres";
        o.VectorDimension = 768;
    });
```

### Server — retrieval only

Exposes a retrieval endpoint; does not generate answers.

```csharp
services.AddRetrievalPipeline()
    .AddSentenceChunker()
    .AddOllamaEmbedder()
    .AddPostgresVectorStore(o => { ... });

// inject IIngestionPipeline for your ingest endpoint
// inject IRetrievalPipeline for your query endpoint
```

### Client — remote retrieval + local generation

Calls a remote server for retrieval, generates answers locally.

```csharp
services.AddAnswerPipeline()
    .AddRemoteRetrievalPipeline(o => o.Endpoint = "https://my-server/api")
    .AddOllamaGenerator(o => o.GenerationModel = "llama3.2");

// inject IAnswerPipeline
var answer = await answerPipeline.AnswerAsync("What is RAG?");
```

## Quick start

### Ingest and query

```csharp
var sourceId = new Guid("a34a3c8c-9a31-45f0-b5f7-d83b4ad62d11"); // stable, never changes

// Ingest
await pipeline.IngestAsync(new PlainTextDocument
{
    Source = new Document.Origin(sourceId, "Invoice", "INV-001"),
    Text = invoiceText
});

// Query — returns ranked chunks
var results = await pipeline.QueryAsync("your question");
foreach (var result in results)
    Console.WriteLine($"[{result.Score:F2}] {result.Chunk.Text}");

// Answer — retrieve + generate in one call
var answer = await pipeline.AnswerAsync("your question");
Console.WriteLine(answer);
```

### Replace a document

Re-ingesting a document atomically replaces all its chunks — stale chunks from a shorter or re-chunked document are removed automatically:

```csharp
await pipeline.IngestAsync(updatedDoc); // previous chunks for updatedDoc.Source are replaced atomically
```

To remove a document from the index entirely:

```csharp
await vectorStore.DeleteByDocumentAsync(doc.Source);
```

## Chunking strategies

Both chunkers operate on `PlainTextDocument`. Choose one per registration.

**`AddPlainTextChunker`** — fixed character-size chunks with overlap:

```csharp
.AddPlainTextChunker(o =>
{
    o.ChunkSize = 512;               // max characters per chunk
    o.Overlap = 50;                  // shared characters between consecutive chunks
    o.RespectWordBoundaries = true;  // avoid cutting mid-word (default: true)
    o.MinChunkLength = 20;           // drop trailing fragments shorter than this
})
```

**`AddSentenceChunker`** — accumulates sentences up to a character limit; paragraph breaks are always hard boundaries:

```csharp
.AddSentenceChunker(o =>
{
    o.MaxChunkSize = 512;   // max characters per chunk
    o.MinChunkLength = 20;  // drop short fragments
})
```

### Custom document types and chunkers

```csharp
// 1. Define your document type
public record InvoiceDocument : Document
{
    private static readonly Guid SourceId = new("a34a3c8c-9a31-45f0-b5f7-d83b4ad62d11");

    [SetsRequiredMembers]
    public InvoiceDocument(string text, string invoiceId)
    {
        Text = text;
        Source = new Document.Origin(SourceId, "Invoice", invoiceId);
    }

    public required string Text { get; init; }
}

// 2. Implement a chunker
public class InvoiceChunker : IChunker<InvoiceDocument>
{
    public async IAsyncEnumerable<Chunk> ChunkAsync(
        InvoiceDocument document,
        CancellationToken cancellationToken = default)
    {
        // your chunking logic
    }
}

// 3. Register
services.AddRagToolkit()
    .AddChunker<InvoiceDocument, InvoiceChunker>()
    ...
```

The dispatcher routes each document to its registered chunker automatically. If a document type has no registered chunker, an `InvalidOperationException` is thrown with the type name.

## Prerequisites

- .NET 9 SDK
- PostgreSQL with the `vector` extension installed (`CREATE EXTENSION IF NOT EXISTS vector`)
- Ollama running locally with models pulled (`ollama pull nomic-embed-text`, `ollama pull llama3.2`)
- Docker (for integration tests)

## Core concepts

### Pipeline interfaces

| Interface | Methods | Typical consumer |
|---|---|---|
| `IIngestionPipeline` | `IngestAsync` | Server ingestion endpoint |
| `IRetrievalPipeline` | `QueryAsync` | Server query endpoint, remote proxy |
| `IAnswerPipeline` | `AnswerAsync` | Client app |
| `IRagPipeline` | all three | Full local deployment |

### Pipeline flow

```
Ingest:  Document → IChunker<T> → IEmbedder → IVectorStore
Query:   string   → IEmbedder   → IRetriever → IReadOnlyList<SearchResult>
Answer:  string   → QueryAsync  → IGenerator → string
```

### Document identity

Every `Document` carries a `Source` property of type `Document.Origin`:

```csharp
public sealed record Origin(Guid SourceId, string DocumentType, string DocumentId)
```

| Field | Purpose |
|---|---|
| `SourceId` | Identifies the source system (one Guid per document class, stable forever) |
| `DocumentType` | Identifies the document category within that system (`"Invoice"`, `"Contract"`) |
| `DocumentId` | Identifies the specific document instance (`"INV-001"`) |

Origin is propagated automatically to every `Chunk` produced during ingestion and stored as dedicated columns in the vector store. Re-ingesting a document replaces all its chunks atomically via `IVectorStore.SetAsync`. To remove a document without re-ingesting, call `IVectorStore.DeleteByDocumentAsync`.

### Chunk enrichment

The pipeline automatically enriches each chunk before storage:

| Property | Set by | Value |
|---|---|---|
| `Chunk.Id` | `RetrievalPipeline` | Random `Guid` |
| `Chunk.Embedding` | `RetrievalPipeline` | Output of `IEmbedder` |
| `Chunk.Origin` | `IChunker<T>` | Copied from `Document.Source` |
| `Chunk.ChunkIndex` | `RetrievalPipeline` | Zero-based position within the document |

### Similarity score

`SearchResult.Score` is cosine similarity in `[-1, 1]`:
- `1.0` — identical meaning
- `0.0` — unrelated (orthogonal)
- `-1.0` — opposite meaning

`RetrievalOptions.MinScore` defaults to `0.0` — results with score `<= 0` are excluded.

### Adding a new provider

Create a new project referencing only `IV.RAG.Abstractions`, implement the relevant interface, and register via a `RAGBuilder` extension:

```csharp
// IV.RAG.Qdrant
public static RAGBuilder AddQdrantVectorStore(
    this RAGBuilder builder,
    Action<QdrantOptions> configure) { ... }
```

The consumer swaps one line in DI — no other code changes.

## Solution structure

```
src/
  IV.RAG.Abstractions/     ← interfaces + models
  IV.RAG.Core/             ← pipeline orchestrators
  IV.RAG.Ingestion/        ← chunkers + document types
  IV.RAG.Ollama/           ← embedder + generator
  IV.RAG.Postgres/         ← vector store + retriever
  IV.RAG.Remote.Http/      ← remote retrieval proxy
tests/
  unit/                    ← no infrastructure required
  integration/             ← Docker (Testcontainers)
  e2e/                     ← live Ollama + Postgres
automation/                ← build and publish scripts
```

## Building

```bash
dotnet build IV.RAG.sln
```

## Testing

```bash
# Unit tests — fast, no infrastructure
dotnet test IV.RAG.Unit.slnf

# Integration tests — requires Docker
dotnet test IV.RAG.Integration.slnf

# E2E tests — requires Ollama running at http://localhost:11434
dotnet test IV.RAG.E2E.slnf
```

## Retrieval options

```csharp
var results = await pipeline.QueryAsync(
    "your question",
    new RetrievalOptions
    {
        TopK = 10,       // maximum results to return
        MinScore = 0.7f  // only return highly relevant results
    });
```
