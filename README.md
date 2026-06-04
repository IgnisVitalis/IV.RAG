# IV.RAG

A composable .NET 9 toolkit for building RAG (Retrieval-Augmented Generation) pipelines. Provides infrastructure and base abstractions — every step is swappable via dependency injection without touching pipeline logic.

> **Pre-1.0 — active development. Breaking changes may occur between versions.**

## Packages

| Package | Description |
|---|---|
| `IV.RAG.Abstractions` | Core interfaces and models. No dependencies. Domain projects depend only on this. |
| `IV.RAG.Core` | Pipeline orchestrators (`RagPipeline`, `RetrievalPipeline`, `AnswerPipeline`, `HybridRetrievalPipeline`, `CachedRetrievalPipeline`). Embedding migration (`EmbeddingMigrator`). Depends only on Abstractions. |
| `IV.RAG.Ingestion` | Document types and chunkers (`PlainTextDocument`, `FixedSizeChunker`, `SentenceChunker`). |
| `IV.RAG.Ollama` | `IEmbedder` and `IGenerator` backed by Ollama (`/api/embed`, `/api/chat`). |
| `IV.RAG.Postgres` | `IVectorStore`, `IRetriever`, and `ILexicalRetriever` backed by PostgreSQL + pgvector + full-text search. Model-versioned vector storage and semantic query cache. |
| `IV.RAG.Remote.Contracts` | Public request/response DTOs (`QueryRequest`, `QueryResponse`, …) and `RemoteContract` mapping, shared by the remote client and a server. Depends only on Abstractions. |
| `IV.RAG.Remote.Http` | `IRetrievalPipeline` proxy — forwards queries to a remote retrieval server over HTTP. |

## Deployment topologies

### Full local pipeline

Everything runs in one process: ingestion, retrieval, and generation.

```csharp
services.AddRagToolkit()
    .AddSentenceChunker(o => o.MaxChunkSize = 512)
    .AddOllamaEmbedder(o =>
    {
        o.EmbeddingModel = "nomic-embed-text";
        // EmbeddingDimensions defaults to 0 — auto-detected from the first embed response.
        // Set explicitly only when a store operation must run before the first embed call.
    })
    .AddOllamaGenerator(o =>
    {
        o.GenerationModel = "llama3.2";
        o.SystemPrompt = "Answer using only the provided context.";
    })
    .AddPostgresVectorStore(o =>
    {
        o.ConnectionString = "Host=localhost;Database=rag;Username=postgres;Password=postgres";
    })
    .AddEmbeddingMigrator(); // optional — register to handle model changes at startup
```

### Hybrid search (vector + lexical)

Adds a BM25 lexical retriever alongside the vector retriever. Results are fused via Reciprocal Rank Fusion (RRF) — chunks found by both sources rank higher than chunks found by only one. Optional cross-encoder reranking can be applied after fusion.

```csharp
services.AddRagToolkit()
    .AddSentenceChunker()
    .AddOllamaEmbedder(o => o.EmbeddingModel = "nomic-embed-text")
    .AddOllamaGenerator(o => o.GenerationModel = "llama3.2")
    .AddPostgresVectorStore(o => o.ConnectionString = "...")
    .AddPostgresLexicalRetriever()        // adds ILexicalRetriever (tsvector/tsquery)
    .AddHybridRetrievalPipeline(o =>      // replaces IRetrievalPipeline with hybrid
    {
        o.RrfK = 60;                      // RRF constant (default 60)
        o.CandidateMultiplier = 3;        // fetch 3× TopK candidates per source (default 3)
    });
```

Ingestion is unaffected — `IIngestionPipeline` remains on `RetrievalPipeline`. Only queries go through `HybridRetrievalPipeline`.

#### Optional reranking

If you register an `IReranker`, it is automatically applied after RRF fusion:

```csharp
services.AddSingleton<IReranker, MyReranker>(); // your cross-encoder implementation
services.AddRagToolkit()
    ...
    .AddHybridRetrievalPipeline();             // picks up IReranker automatically
```

The reranker receives the full fused candidate list and returns the top `TopK` results re-scored by the model.

### Semantic query cache

Wraps any retrieval pipeline with a transparent semantic cache. Semantically similar queries return cached results without hitting the vector store or embedder again. Works with both vector-only and hybrid pipelines.

On a cache **miss** the query is embedded only once: the embedding computed for the cache lookup is reused for retrieval rather than re-embedded by the inner pipeline.

```csharp
services.AddRagToolkit()
    .AddSentenceChunker()
    .AddOllamaEmbedder(o => o.EmbeddingModel = "nomic-embed-text")
    .AddOllamaGenerator(o => o.GenerationModel = "llama3.2")
    .AddPostgresVectorStore(o => o.ConnectionString = "...")
    .AddInMemoryQueryCache(o =>
    {
        o.SimilarityThreshold = 0.95f; // minimum cosine similarity for a cache hit
        o.Ttl = TimeSpan.FromHours(1); // entry lifetime
        o.MaxEntries = 1000;           // max entries retained (in-memory only)
    })
    .AddCachedRetrieval();             // must be called last
```

Use `AddPostgresQueryCache()` instead of `AddInMemoryQueryCache()` to store the cache in PostgreSQL — useful when multiple instances share one cache or when cache persistence across restarts is required:

```csharp
    .AddPostgresQueryCache(o => o.SimilarityThreshold = 0.95f)
    .AddCachedRetrieval();
```

**Cache invalidation:** when a document is re-ingested, all cache entries that previously returned chunks from that document are removed automatically. New documents do not trigger invalidation (no existing entry references them); TTL handles those entries naturally.

**Model changes:** the query cache stores which embedding model produced each cached query vector. When the embedder changes, entries from the old model are removed automatically on the next write — the cache self-cleans without manual intervention.

**Empty results are not cached** — a query that finds nothing is always forwarded to the inner pipeline, so results appear as soon as matching documents are ingested.

### HTTP resilience & timeouts

The Ollama and remote HTTP clients are wired with the standard resilience handler
(`Microsoft.Extensions.Http.Resilience`): a per-attempt timeout, bounded retries on transient
failures, and a circuit breaker. Because embedding and generation have very different latencies,
they use **separate clients** with separate timeouts:

```csharp
.AddOllamaEmbedder(o =>
{
    o.EmbeddingModel = "nomic-embed-text";
    o.EmbeddingTimeoutSeconds = 100;   // per-attempt embed timeout (default 100)
})
.AddOllamaGenerator(o =>
{
    o.GenerationModel = "llama3.2";
    o.GenerationTimeoutSeconds = 600;  // generation is slower — default 600
})
.AddRemoteRetrievalPipeline(o =>
{
    o.Endpoint = "https://my-server/api";
    o.TimeoutSeconds = 100;            // per-attempt remote retrieval timeout (default 100)
});
```

Generation requests are **not retried on timeout** (re-running a slow generation only wastes work);
embedding and remote requests retry on transient failures within their timeout budget.

### Server — retrieval only

Exposes a retrieval endpoint; does not generate answers.

```csharp
services.AddRetrievalPipeline()
    .AddSentenceChunker()
    .AddOllamaEmbedder(o => o.EmbeddingModel = "nomic-embed-text")
    .AddPostgresVectorStore(o => { o.ConnectionString = "..."; });

// inject IIngestionPipeline for your ingest endpoint
```

Expose the query endpoint with the shared `IV.RAG.Remote.Contracts` package — the same DTOs and
`RemoteContract` mapping the `IV.RAG.Remote.Http` client uses, so the wire shape can't drift:

```csharp
// using IV.RAG; (QueryRequest, RemoteContract)
app.MapPost("/api/query", async (QueryRequest request, IRetrievalPipeline pipeline) =>
{
    var results = await pipeline.QueryAsync(request.Query, request.ToRetrievalOptions());
    return results.ToQueryResponse();
});
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

## Embedding model versioning

Every chunk stored in the vector table carries a reference to the embedding model that produced its vector. The model identity — provider, name, and dimensions — is tracked in a `{tableName}_models` companion table.

### Automatic mismatch detection

If you switch to a different embedder model, `PostgresVectorStore` detects the mismatch on first use and throws:

```
EmbeddingModelMismatchException: Vector table 'chunks' contains chunks embedded with
ollama/nomic-embed-text (768d), but the current embedder is ollama/mxbai-embed-large (1024d).
Call IEmbeddingMigrator.MigrateAsync() to re-embed all outdated chunks.
```

If the new model has **different dimensions**, the `embedding` column is automatically altered to the new vector size (existing vectors are cleared) before the exception is thrown. The text of each chunk is preserved — only the vectors are wiped.

### Migrating

Register `IEmbeddingMigrator` and check at startup:

```csharp
services.AddRagToolkit()
    .AddOllamaEmbedder(o => o.EmbeddingModel = "mxbai-embed-large")
    // EmbeddingDimensions auto-detected on first embed — no manual config needed
    ...
    .AddPostgresVectorStore(o => o.ConnectionString = "...")
    .AddEmbeddingMigrator();
```

```csharp
// Hosted service or startup code:
if (await migrator.IsNeededAsync(cancellationToken))
{
    logger.LogInformation("Embedding model changed — starting migration...");

    await migrator.MigrateAsync(
        progress: new Progress<EmbeddingMigrationProgress>(p =>
            logger.LogInformation(
                "[{Processed}/{Total}] re-embedded chunk from {Origin}",
                p.Processed, p.Total, p.CurrentOrigin)),
        batchSize: 32,            // chunks embedded per batch request (default 32)
        cancellationToken: ct);

    logger.LogInformation("Migration complete.");
}
```

Migration re-embeds all outdated chunks **in-place** — no data loss, no re-fetching source documents. The text stored alongside each vector is used directly. Outdated chunks are processed in batches of `batchSize`, each embedded with a single batch request where the embedder supports it (`OllamaEmbedder` does, via `/api/embed`).

`IsNeededAsync()` returns `false` when the store is empty or all chunks already match the current model — safe to call on every startup with no cost when nothing has changed.

After a complete migration the next clean startup tightens the `model_id NOT NULL` constraint automatically.

## Vector index (ANN)

By default `PostgresVectorStore` creates an **HNSW** index on the `embedding` column so similarity search uses approximate nearest-neighbor lookups instead of an exact sequential scan (whose latency grows linearly with the corpus). The index is created automatically during schema initialization and uses the `vector_cosine_ops` opclass to match the cosine distance operator used by `PostgresRetriever`.

```csharp
.AddPostgresVectorStore(o =>
{
    o.ConnectionString = "...";
    o.VectorIndex = VectorIndexType.Hnsw; // None | Hnsw (default) | IVFFlat
    o.HnswM = 16;                         // connections per layer (default 16)
    o.HnswEfConstruction = 64;            // build-time candidate list (default 64, must be ≥ 2 × HnswM)
})
```

| `VectorIndex` | When to use |
|---|---|
| `Hnsw` *(default)* | General purpose. Builds incrementally as rows are inserted, so it works well even on an initially empty table. |
| `IVFFlat` | Lower build cost / memory at large scale. Tune `IVFFlatLists` (≈ `rows / 1000`). |
| `None` | Exact search only — small corpora, or when you manage indexes via external migrations. |

**Caveats:**

- **Dimension limit.** pgvector can only index vectors of up to **2000 dimensions** on the `vector` type. For higher-dimension models the index is skipped (a warning is logged) and queries fall back to an exact scan. Set `VectorIndex = None` to silence the warning.
- **IVFFlat on an empty table.** IVFFlat derives its cluster centroids from existing rows, so an index built before data is loaded has poor recall. After a large initial load, run `REINDEX INDEX {tableName}_embedding_idx` to rebuild it against the real data. HNSW does not have this limitation.
- **Bulk loads.** Building the index is faster *after* a bulk insert than incrementally during one. When loading a large existing corpus for the first time, the initial index build runs synchronously on the first store operation.
- **Dimension changes.** When the embedding dimension changes (see above), the index is dropped and recreated automatically at the new size.

## Schema management

By default (`SchemaManagement = Auto`) the provider creates and migrates its tables and indexes on
first use. Schema DDL is serialized across application instances with a PostgreSQL transaction-scoped
advisory lock, so starting many instances concurrently against the same database is safe — only one
performs the DDL and the rest find the schema already present.

For deployments that provision schema via explicit migrations and run under least-privilege accounts,
set `SchemaManagement = None` to skip all runtime structural DDL:

```csharp
.AddPostgresVectorStore(o =>
{
    o.ConnectionString = "...";
    o.SchemaManagement = SchemaManagementMode.None; // never issue CREATE/ALTER/index DDL
})
```

Under `None` the required tables must already exist (a missing table fails fast with a clear error).
The vector store still upserts into `{TableName}_models` to resolve each chunk's model id and still
detects model mismatches, so the runtime account needs `INSERT`/`SELECT` on that table — only
`CREATE`/`ALTER`/index creation are skipped.

### Manual provisioning DDL

Run this once during provisioning (replace `768` with your embedding model's dimension, and
`english` with your `TextSearchLanguage`):

```sql
CREATE EXTENSION IF NOT EXISTS vector;

-- Model registry (the runtime upserts into this table even under SchemaManagement = None)
CREATE TABLE chunks_models (
    id         SERIAL PRIMARY KEY,
    provider   TEXT NOT NULL,
    model_name TEXT NOT NULL,
    dimensions INT  NOT NULL,
    UNIQUE (provider, model_name, dimensions)
);

CREATE TABLE chunks (
    id            TEXT PRIMARY KEY,
    text          TEXT NOT NULL,
    embedding     vector(768) NOT NULL,
    metadata      JSONB,
    source_id     UUID NOT NULL,
    document_type TEXT NOT NULL,
    document_id   TEXT NOT NULL,
    chunk_index   INT,
    model_id      INT NOT NULL REFERENCES chunks_models(id),
    text_search   TSVECTOR GENERATED ALWAYS AS (to_tsvector('english'::regconfig, text)) STORED
);
CREATE INDEX chunks_origin_idx      ON chunks (source_id, document_type, document_id);
CREATE INDEX chunks_model_id_idx    ON chunks (model_id);
CREATE INDEX chunks_text_search_idx ON chunks USING GIN (text_search);
CREATE INDEX chunks_embedding_idx   ON chunks USING hnsw (embedding vector_cosine_ops);

-- Only if you use AddPostgresQueryCache:
CREATE TABLE query_cache (
    id                  BIGSERIAL PRIMARY KEY,
    query_embedding     vector(768) NOT NULL,
    options_hash        TEXT NOT NULL,
    results             JSONB NOT NULL,
    document_origins    TEXT[] NOT NULL,
    expires_at          TIMESTAMPTZ NOT NULL,
    embedder_provider   TEXT,
    embedder_model      TEXT,
    embedder_dimensions INT
);
CREATE INDEX query_cache_expires_idx ON query_cache (expires_at);
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
    Console.WriteLine($"[{result.Score:F4}] {result.Chunk.Text}");

// Answer — retrieve + generate in one call
var answer = await pipeline.AnswerAsync("your question");
Console.WriteLine(answer);

// Answer — streamed token-by-token (for chat UIs)
await foreach (var fragment in pipeline.AnswerStreamAsync("your question"))
    Console.Write(fragment);
```

`IGenerator.GenerateStreamAsync` and `IAnswerPipeline.AnswerStreamAsync` stream the answer as
incremental fragments. `OllamaGenerator` streams real tokens from `/api/chat`; generators that
don't implement streaming fall back to yielding the whole answer as a single fragment.

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
Ingest:  Document → IChunker<T> → IEmbedder → IVectorStore (stores vector + model ref) → [IQueryCache.Invalidate]

Query (vector only):
         string → IEmbedder → [IQueryCache.Get] → hit: cached results
                                                 → miss: IRetriever → IQueryCache.Set → results

Query (hybrid):
         string → IRetriever (vector)   ─┐
               → ILexicalRetriever      ─┤ RRF fusion → [IReranker] → IReadOnlyList<SearchResult>
         (cache wraps the full hybrid pipeline at the IRetrievalPipeline level)

Answer:  string → QueryAsync → IGenerator → string
```

### Extension points

| Interface | Provided by | Purpose |
|---|---|---|
| `IChunker<TDoc>` | `IV.RAG.Ingestion` or custom | Split documents into chunks |
| `IEmbedder` | `IV.RAG.Ollama` or custom | Generate vector embeddings. Exposes `EmbedderInfo ModelInfo` (provider, model name, dimensions) used for version tracking. |
| `IGenerator` | `IV.RAG.Ollama` or custom | Generate answers from retrieved chunks |
| `IVectorStore` | `IV.RAG.Postgres` or custom | Persist and manage chunks with model tracking |
| `IRetriever` | `IV.RAG.Postgres` or custom | Vector similarity search |
| `ILexicalRetriever` | `IV.RAG.Postgres` or custom | Keyword/BM25 search |
| `IReranker` | custom | Cross-encoder reranking after fusion |
| `IQueryCache` | `IV.RAG.Core` (in-memory), `IV.RAG.Postgres` | Semantic query result cache |
| `IEmbeddingMigrator` | `IV.RAG.Core` | Re-embed outdated chunks after a model change |

Each interface lives in `IV.RAG.Abstractions`. Swap any implementation in one DI registration — no other code changes required.

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
| `Chunk.Metadata` | `IChunker<T>` | Propagated from `Document.Metadata` |

### Relevance score

`SearchResult.Score` represents relevance — higher is more relevant. The exact semantics depend on the retriever in use:

| Retriever | Score range | Meaning |
|---|---|---|
| `PostgresRetriever` (vector only) | `[-1, 1]` | Cosine similarity: 1 = identical, 0 = unrelated, -1 = opposite |
| `HybridRetrievalPipeline` (RRF) | `(0, ∞)` | Sum of `1/(k+rank)` across sources; typically `0.01`–`0.04` |
| After reranking | model-specific | Relevance score from the cross-encoder |

`RetrievalOptions.MinScore` is applied inside the vector retriever before fusion. The lexical retriever and RRF fusion scores are not filtered by `MinScore`.

## Solution structure

```
src/
  IV.RAG.Abstractions/     ← interfaces + models
  IV.RAG.Core/             ← pipeline orchestrators (incl. HybridRetrievalPipeline, EmbeddingMigrator)
  IV.RAG.Ingestion/        ← chunkers + document types
  IV.RAG.Ollama/           ← embedder + generator
  IV.RAG.Postgres/         ← vector store + vector retriever + lexical retriever + query cache
  IV.RAG.Remote.Contracts/ ← shared remote query/response DTOs + mapping (client + server)
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
        MinScore = 0.7f, // minimum vector similarity (applied inside IRetriever, before fusion)
        MetadataFilter = MetadataFilter.Eq("department", "engineering")
    });
```

## Metadata

Attach typed key-value metadata to a document — it is propagated automatically to every chunk produced from it and stored alongside the vector.

```csharp
await pipeline.IngestAsync(new PlainTextDocument
{
    Source = new Document.Origin(sourceId, "Report", "RPT-2024"),
    Text = reportText,
    Metadata = new Metadata
    {
        ["department"] = "engineering",
        ["year"]       = 2024,
        ["published"]  = true
    }
});
```

Values are typed as `MetadataFilterValue` with implicit conversions from `string`, `int`, `long`, `float`, `double`, and `bool`.

## Metadata filtering

Filter retrieved chunks by their metadata before `TopK` is applied. Build filter trees with the static factory methods on `MetadataFilter`:

```csharp
// Equality and comparison
MetadataFilter.Eq("department", "engineering")
MetadataFilter.Ne("status", "archived")
MetadataFilter.Gt("year", 2020)
MetadataFilter.Gte("year", 2020)
MetadataFilter.Lt("year", 2024)
MetadataFilter.Lte("year", 2024)

// Set membership — all values must be the same type
MetadataFilter.In("department", "engineering", "research")

// Logical combinators
MetadataFilter.And(
    MetadataFilter.Eq("department", "engineering"),
    MetadataFilter.Gte("year", 2022))

MetadataFilter.Or(
    MetadataFilter.Eq("type", "pdf"),
    MetadataFilter.Eq("type", "docx"))

MetadataFilter.Not(MetadataFilter.Eq("status", "archived"))
```

Combinators compose freely:

```csharp
var results = await pipeline.QueryAsync(
    "your question",
    new RetrievalOptions
    {
        TopK = 5,
        MetadataFilter = MetadataFilter.And(
            MetadataFilter.Eq("department", "engineering"),
            MetadataFilter.Or(
                MetadataFilter.Gte("year", 2022),
                MetadataFilter.Eq("featured", true)))
    });
```

Filters are pushed down to the database — the `TopK` limit is applied to the already-filtered result set. In hybrid search, the filter is propagated to both the vector retriever and the lexical retriever independently.
