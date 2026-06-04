# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.21.0] - 2026-06-03

### Added

- `AnswerResult` (`Abstractions`) — `record AnswerResult(string Text, IReadOnlyList<SearchResult> Sources)`: a generated answer together with the chunks it was grounded in, for source attribution / citations.
- `IAnswerPipeline.AnswerWithSourcesAsync(query, options, ct)` — returns an `AnswerResult`. Added as a default interface method that returns the `AnswerAsync` text with an empty source list (so existing implementations keep working); `AnswerPipeline` and `RagPipeline` override it to populate the retrieved sources. The plain `AnswerAsync` string path is unchanged and now delegates to it (removing the duplicated retrieve→generate loop between the two pipelines).

## [0.20.0] - 2026-06-03

### Added

- **Tracing & metrics** — `RagDiagnostics` (`Core`) exposes a public `ActivitySource` and `Meter`, both named `IV.RAG`. The pipelines emit spans (`rag.ingest`, `rag.retrieve`, `rag.retrieve.cached`, `rag.answer`) and metrics (`rag.chunks_ingested`, `rag.retrieval.duration`, `rag.cache.hits`, `rag.cache.misses`). Subscribe via OpenTelemetry with `.AddSource("IV.RAG")` and `.AddMeter("IV.RAG")`. Instrumentation is a no-op (no allocations) when nothing is listening.
- `AddRagObservability()` (`Core`) — opt-in decorators on the registered `IEmbedder`/`IGenerator` that add `rag.embed` / `rag.generate` spans and the `rag.embed_calls` counter, capturing every embed and generate call regardless of provider (including embeds made during retrieval).
- `PostgresHealthCheck` + `AddPostgresHealthCheck()` (`Postgres`) — verifies the data source is reachable with `SELECT 1`. Adds a `Microsoft.Extensions.Diagnostics.HealthChecks` dependency.
- `OllamaHealthCheck` + `AddOllamaHealthCheck()` (`Ollama`) — pings the Ollama endpoint via a dedicated short-timeout client (no resilience handler, so the probe fails fast). Adds a `Microsoft.Extensions.Diagnostics.HealthChecks` dependency.

## [0.19.0] - 2026-06-03

### Added

- **`IV.RAG.Remote.Contracts`** — new package with the public remote wire DTOs (`QueryRequest`, `QueryResponse`, `SearchResultDto`, `ChunkDto`, `OriginDto`) and a `RemoteContract` mapping helper (`ToQueryRequest` / `ToRetrievalOptions` / `ToQueryResponse` / `ToSearchResults` / `ToDto`). Depends only on `Abstractions`, so a server can produce exactly the wire shape the `IV.RAG.Remote.Http` client consumes without re-implementing it by hand. The README's server-only topology now shows a minimal-API `/api/query` endpoint built from it.

### Changed

- The remote DTOs moved from `internal` types in `IV.RAG.Remote.Http` to the public `IV.RAG.Remote.Contracts` package; `RemoteRetrievalPipeline` references them and maps via `RemoteContract`. `Remote.Http` now references `Remote.Contracts` — a deliberate, documented exception to the "providers reference only Abstractions" rule, since the client and server need one source of truth for the wire contract.

## [0.18.0] - 2026-06-03

### Added

- `IGenerator.GenerateStreamAsync(query, chunks, ct)` — streams the answer as incremental text fragments (`IAsyncEnumerable<string>`). Added as a default interface method that yields the whole `GenerateAsync` result as one fragment, so existing generators keep working; `OllamaGenerator` overrides it with real token streaming (newline-delimited JSON from `/api/chat` with `stream: true`, read with `ResponseHeadersRead`).
- `IAnswerPipeline.AnswerStreamAsync(query, options, ct)` — retrieve-then-stream variant. Default interface method yields the whole `AnswerAsync` result as one fragment; `AnswerPipeline` and `RagPipeline` override it to stream the generator's fragments.

## [0.17.0] - 2026-06-03

### Changed

- Provider options are now validated at startup (`ValidateOnStart`), matching the chunkers: `PostgresOptions.ConnectionString` must be non-empty, and `OllamaOptions.Endpoint` / `RemoteOptions.Endpoint` must parse as absolute URIs. Misconfiguration fails fast with an `OptionsValidationException` instead of surfacing late and cryptically on first use. Applied in `AddPostgresVectorStore`, `AddOllamaEmbedder`, `AddOllamaGenerator`, and `AddRemoteRetrievalPipeline`.

## [0.16.0] - 2026-06-03

### Changed

- `PostgresOptions.TableName` and `PostgresOptions.QueryCacheTableName` are now validated as SQL identifiers — letters, digits, and underscores (not starting with a digit), optionally qualified as `schema.table` — when any Postgres component is constructed. Table names are interpolated into SQL (they cannot be parameterized), so this closes a latent injection vector and matches the existing validation of `TextSearchLanguage` and metadata field names. An invalid name throws `ArgumentException` at construction.

## [0.15.0] - 2026-06-03

### Added

- `OllamaOptions.EmbeddingTimeoutSeconds` (default 100) and `OllamaOptions.GenerationTimeoutSeconds` (default 600) — per-attempt HTTP timeouts for the embedder and generator clients (generation is far slower than embedding).
- `RemoteOptions.TimeoutSeconds` (default 100) — per-attempt HTTP timeout for remote retrieval.

### Changed

- The Ollama and remote provider HTTP clients now apply the standard resilience handler (`Microsoft.Extensions.Http.Resilience`) — per-attempt timeout, bounded retries on transient failures, and a circuit breaker — replacing the bare clients that had only a base address (a hung request previously blocked the caller indefinitely). `HttpClient.Timeout` is set to infinite so the resilience pipeline governs timeouts.
- Generation requests are **not** retried on timeout (re-running a slow generation only wastes work); embedding and remote requests retry transient failures within their timeout budget.
- **Breaking:** Ollama embedding and generation now use separate named HTTP clients (`IV.RAG.Ollama.Embedder` and `IV.RAG.Ollama.Generator`) instead of a shared `IV.RAG.Ollama` client, so they can carry independent timeouts. Only affects code that configured the named HTTP client directly.

## [0.14.0] - 2026-06-03

### Changed

- `PostgresVectorStore.SetAsync` now bulk-inserts chunks with a single binary `COPY` (`NpgsqlBinaryImporter`) inside the existing delete-then-insert transaction, replacing the previous one-`INSERT`-per-chunk loop — large documents ingest in a single round-trip instead of N. Up-front validation (origin match, non-null Id/Embedding) is unchanged, and an empty chunk set still just clears the document's existing rows. No public API change.

## [0.13.0] - 2026-06-03

### Added

- `IEmbedder.EmbedAsync(IReadOnlyList<string>, CancellationToken)` — batch embedding. Added as a **default interface method** that calls the scalar overload sequentially, so existing custom embedders keep working unchanged; providers override it for native batch support.
- `OllamaOptions.EmbeddingBatchSize` (default 32) — maximum number of texts per `/api/embed` request; larger batches are split into multiple requests automatically.

### Changed

- `OllamaEmbedder` implements native batch embedding against `/api/embed` (whose `input` accepts an array), splitting batches larger than `EmbeddingBatchSize`. The scalar `EmbedAsync(string)` now delegates to the batch path. A response whose embedding count does not match the request count throws a descriptive `InvalidOperationException` naming the model and endpoint.
- `RetrievalPipeline.IngestAsync` now embeds all of a document's chunks in one batch call instead of one HTTP round-trip per chunk (a 500-chunk document went from 500 serial calls to a handful).
- **Breaking:** `IEmbeddingMigrator.MigrateAsync` / `EmbeddingMigrator.MigrateAsync` parameter `maxConcurrency` (default 4) renamed to `batchSize` (default 32). Each flush now issues a single batch embed call per batch rather than N parallel scalar calls.

## [0.12.0] - 2026-06-03

### Added

- `IVectorRetriever` (`Abstractions`) — optional capability extending `IRetriever` with `RetrieveByVectorAsync(float[] embedding, RetrievalOptions, CT)`, for vector retrievers that can search from a precomputed query embedding.
- `IVectorQueryPipeline` (`Abstractions`) — optional capability exposing `QueryByVectorAsync(float[] embedding, string query, RetrievalOptions, CT)`, so a retrieval pipeline can accept a precomputed query embedding instead of re-embedding.

### Changed

- A cached cold query now embeds the query **once** instead of twice (vector-only and hybrid alike). `CachedRetrievalPipeline` reuses the embedding it computes for the cache probe, passing it through `IVectorQueryPipeline.QueryByVectorAsync` to the inner pipeline and on to `IVectorRetriever.RetrieveByVectorAsync`. Retrievers/pipelines that do not implement the seam fall back to the existing string-based path (re-embed), so behavior is unchanged for custom implementations.
- `PostgresRetriever` now implements `IVectorRetriever`; `RetrievalPipeline` and `HybridRetrievalPipeline` now implement `IVectorQueryPipeline`. The public `IRetriever.RetrieveAsync(string, …)` and `IRetrievalPipeline.QueryAsync(string, …)` contracts are unchanged and remain the default extension points. In hybrid retrieval only the vector arm reuses the embedding; the lexical arm and reranker still use the query string.

## [0.11.0] - 2026-06-03

### Added

- `SchemaManagementMode` enum (`IV.RAG.Postgres`) — `Auto`, `None`.
- `PostgresOptions.SchemaManagement` (default `Auto`). `None` skips all runtime structural DDL for explicit-migration / least-privilege deployments; the required tables must be provisioned manually (a missing table fails fast with a clear error — see the README "Manual provisioning DDL"). Under `None` the vector store still upserts into `{TableName}_models` to resolve each chunk's model id and still detects model mismatches, so the runtime account needs `INSERT`/`SELECT` on that table.

### Changed

- Schema DDL in `PostgresVectorStore.EnsureSchemaAsync` now runs in a single transaction guarded by a PostgreSQL transaction-scoped advisory lock (`pg_advisory_xact_lock`, keyed on the table name), so concurrent application instances starting against the same database serialize their DDL instead of racing on the destructive dimension-change path (`ALTER COLUMN … USING NULL`). The model-mismatch exception is thrown *after* the transaction commits, so a dimension change still persists for `IEmbeddingMigrator`.
- `PostgresQueryCache.EnsureSchemaAsync` is now guarded by a `SemaphoreSlim(1,1)` in-process lock and the same cross-process advisory lock (previously only a `Volatile` flag, which let concurrent first calls both run the `TRUNCATE`/`ALTER` adaptation). `PostgresQueryCache` now implements `IDisposable` (disposes the semaphore).

## [0.10.0] - 2026-06-03

### Added

- **ANN vector index** — `PostgresVectorStore` now creates an approximate-nearest-neighbor index on the `embedding` column during schema initialization, so similarity search uses an index scan instead of an exact sequential scan (whose latency grows linearly with corpus size). The index uses the `vector_cosine_ops` opclass to match the cosine distance operator (`<=>`) used by `PostgresRetriever`.
- `VectorIndexType` enum (`IV.RAG.Postgres`) — `None`, `Hnsw`, `IVFFlat`.
- `PostgresOptions.VectorIndex` — index strategy, defaults to `VectorIndexType.Hnsw`.
- `PostgresOptions.HnswM` (default 16) and `PostgresOptions.HnswEfConstruction` (default 64) — HNSW build parameters. Validated at schema init: `HnswM >= 2`, `HnswEfConstruction >= 2 × HnswM`.
- `PostgresOptions.IVFFlatLists` (default 100) — IVFFlat list count. Validated at schema init: `>= 1`.

### Changed

- `PostgresVectorStore` creates the `{tableName}_embedding_idx` index by default (HNSW, built incrementally as rows are inserted). Set `PostgresOptions.VectorIndex = None` to opt out (exact search only). Note: when upgrading an existing deployment, the index is built on the first store operation — for a large existing table this initial build runs synchronously.
- Embedding dimensions above pgvector's 2000-dimension index limit (for the `vector` type) skip index creation with a logged warning; queries fall back to an exact scan.
- The embedding-dimension-change path now drops and recreates the vector index at the new dimension (the index depends on the column type, so it is dropped before the `ALTER COLUMN` and recreated afterward).

## [0.9.0] - 2026-06-03

### Added

- **Embedding model versioning** — `IEmbedder` now exposes `EmbedderInfo ModelInfo { get; }` with `Provider`, `ModelName`, and `Dimensions`. `OllamaEmbedder` populates it from `OllamaOptions.EmbeddingDimensions` (default 768).
- `EmbeddingModelMismatchException` (`Abstractions`) — thrown on first use when the vector table's stored model differs from the current embedder. Carries `StoredModel`, `CurrentModel`, and `TableName`.
- `IEmbeddingMigrator` (`Abstractions`) — `IsNeededAsync()` and `MigrateAsync(progress, maxConcurrency, ct)`. `EmbeddingMigrator` (`Core`) implements it: counts outdated chunks first, then streams and processes them in concurrent batches of `maxConcurrency` (default 4). Reports `EmbeddingMigrationProgress(Total, Processed, CurrentOrigin)` per chunk.
- `AddEmbeddingMigrator()` (`Core`) — registers `EmbeddingMigrator` as `IEmbeddingMigrator`. Requires `IVectorStore` and `IEmbedder` to be registered.
- `IVectorStore.CountOutdatedAsync()` — returns the count of chunks needing re-embedding without loading their text.
- `IVectorStore.GetOutdatedAsync()` — streams outdated chunks as `IAsyncEnumerable<Chunk>` for memory-efficient migration of large stores.
- `IVectorStore.UpdateEmbeddingAsync(id, embedding, ct)` — updates a single chunk's vector and model tracking in place.
- `{tableName}_models` table — companion table tracking every `(provider, model_name, dimensions)` tuple seen. Each chunk row carries a `model_id` FK to this table; a partial index on `model_id` accelerates mismatch queries.
- `PostgresQueryCache` now tracks `embedder_provider`, `embedder_model`, `embedder_dimensions` per cache row. Reads filter by current model; writes purge entries from other models. Dimension changes truncate the cache and retype the column automatically.

### Changed

- **Breaking:** `PostgresOptions.VectorDimension` removed. Dimensions now come from `IEmbedder.ModelInfo.Dimensions`. Set `OllamaOptions.EmbeddingDimensions` to match your model (default 768).
- **Breaking:** `PostgresVectorStore` constructor gains a required `IEmbedder` parameter. `PostgresQueryCache` constructor gains a required `IEmbedder` parameter.
- **Breaking:** `IVectorStore.GetOutdatedAsync` return type changed from `Task<IReadOnlyList<Chunk>>` to `IAsyncEnumerable<Chunk>`. Callers must switch to `await foreach`.
- `PostgresVectorStore` implements `IDisposable` (disposes the internal `SemaphoreSlim`).
- `PostgresVectorStore.EnsureSchemaAsync` is now concurrency-safe: a `SemaphoreSlim(1,1)` guard replaces the previous `Interlocked.Exchange` pattern, ensuring `_currentModelId` is always valid before concurrent callers proceed.
- When an embedding dimension change is detected, the `embedding` column is silently altered to the new type (existing vectors set to `NULL`). A subsequent `IEmbeddingMigrator.MigrateAsync()` re-embeds all affected chunks.
- After a successful migration, `PostgresVectorStore` tightens the `model_id NOT NULL` constraint on the next startup.
- `EmbedderInfo.ToString()` returns `"provider/model (Nd)"` — used in log messages and exception text.
- `PostgresVectorStore` and `EmbeddingMigrator` accept an optional `ILogger<T>` and emit structured log events for dimension changes, mismatch detection, migration start/end, and constraint tightening.

## [0.8.0] - 2026-06-03

### Added

- `IQueryCache` (`Abstractions`) — semantic query cache interface. Lookups are similarity-based: a stored result is returned when a cached query embedding is within the configured cosine similarity threshold of the incoming embedding, combined with exact `RetrievalOptions` equality. Results are invalidated automatically when a document is re-ingested via `RetrievalPipeline`.
- `QueryCacheOptions` (`Abstractions`) — `SimilarityThreshold` (default 0.95), `Ttl` (default 1 hour), `MaxEntries` (default 1000, in-memory only).
- `CachedRetrievalPipeline` (`Core`) — transparent decorator for any `IRetrievalPipeline`. Embeds the query once, checks the cache by cosine similarity, and falls through to the inner pipeline on a miss. Empty results are never cached, preventing stale "no results" entries that document ingestion cannot invalidate.
- `InMemoryQueryCache` (`Core`) — thread-safe in-memory `IQueryCache`. Options key is the JSON-serialized `RetrievalOptions` (handles all `MetadataFilter` subtypes). TTL expiry on every read and write; expired entries are pruned before the `MaxEntries` capacity check to avoid premature eviction of valid entries.
- `PostgresQueryCache` (`IV.RAG.Postgres`) — pgvector-backed `IQueryCache`. Stores query embeddings and results in a dedicated table (`query_cache` by default). Similarity lookup via the `<=>` cosine distance operator. Expired rows cleaned up on every write. Schema auto-created on first use.
- `PostgresOptions.QueryCacheTableName` — name of the query cache table (default `"query_cache"`).
- `AddInMemoryQueryCache()` (`Core`) — registers `InMemoryQueryCache` as `IQueryCache`. Accepts an optional `Action<QueryCacheOptions>` configure delegate.
- `AddPostgresQueryCache()` (`IV.RAG.Postgres`) — registers `PostgresQueryCache` as `IQueryCache`. Requires `AddPostgresVectorStore()` to be called first. Accepts an optional `Action<QueryCacheOptions>` configure delegate.
- `AddCachedRetrieval()` (`Core`) — wraps the currently registered `IRetrievalPipeline` with `CachedRetrievalPipeline`. Must be called last, after all pipeline and cache registrations. Works with both `RetrievalPipeline` (vector-only) and `HybridRetrievalPipeline`.

### Changed

- **Breaking:** `RetrievalOptions` changed from `sealed class` to `sealed record`. Structural equality is now the default. Note: `MetadataFilter` subtypes that hold `IReadOnlyList<T>` fields (`InMetadataFilter`, `AndMetadataFilter`, `OrMetadataFilter`) use reference equality for those collections — two independently-constructed identical filters will not compare equal via `==`. The cache itself is unaffected (uses JSON comparison), but callers that compare `RetrievalOptions` instances directly should be aware.
- `RetrievalPipeline.IngestAsync` now calls `IQueryCache.InvalidateByDocumentAsync` after storing chunks, when `IQueryCache` is registered in DI. The invalidation is transparent — no changes to existing ingest code.
- `AddHybridRetrievalPipeline()` now registers `HybridRetrievalPipeline` as a concrete singleton in addition to the `IRetrievalPipeline` binding, required for the keyed-service decorator pattern used by `AddCachedRetrieval()`.

## [0.7.0] - 2026-06-02

### Added

- `ILexicalRetriever` (`Abstractions`) — keyword/BM25 search interface. Same signature as `IRetriever` (`RetrieveAsync(string query, RetrievalOptions, CancellationToken)`) but semantically distinct: implementations use full-text matching rather than vector similarity. `MinScore` is not applied — the full-text match predicate already ensures only matching chunks are returned.
- `IReranker` (`Abstractions`) — optional cross-encoder reranking interface. `RerankAsync(query, candidates, topK)` re-scores a candidate list and returns the top `topK` results. Register an implementation to enable post-fusion reranking in `HybridRetrievalPipeline`. No implementation is provided in this release — wire your own cross-encoder (e.g. an Ollama reranker model).
- `HybridRetrievalPipeline` (`Core`) — implements `IRetrievalPipeline` by combining `IRetriever` (vector search) and `ILexicalRetriever` (keyword search) via Reciprocal Rank Fusion (RRF). Both sub-retrievers are queried in parallel with an expanded candidate count (`CandidateMultiplier × TopK`). RRF score for a chunk is the sum of `1 / (k + rank)` across all lists it appears in — chunks found by both retrievers rank higher than those found by only one. If an `IReranker` is registered, the full fused list is passed to it before trimming to `TopK`.
- `HybridRetrievalOptions` (`Core`) — `RrfK` (RRF constant, default 60) and `CandidateMultiplier` (candidates fetched per source as a multiple of `TopK`, default 3).
- `AddHybridRetrievalPipeline()` (`Core`) — overrides `IRetrievalPipeline` with `HybridRetrievalPipeline`. Ingestion remains on `RetrievalPipeline` — only queries are affected. Accepts an optional `Action<HybridRetrievalOptions>` configure delegate.
- `PostgresLexicalRetriever` (`IV.RAG.Postgres`) — implements `ILexicalRetriever` using PostgreSQL full-text search (`text_search @@ plainto_tsquery`). Results are ordered by `ts_rank`. Supports `MetadataFilter` via the same SQL builder used by `PostgresRetriever`.
- `AddPostgresLexicalRetriever()` (`IV.RAG.Postgres`) — registers `PostgresLexicalRetriever` as `ILexicalRetriever`. Requires `AddPostgresVectorStore()` to be called first.
- `PostgresOptions.TextSearchLanguage` — PostgreSQL text search configuration name used for the `text_search` generated column and for `plainto_tsquery`. Defaults to `"english"`. Use `"simple"` for language-agnostic matching without stemming. Must be set before first ingestion — the value is baked into the `GENERATED ALWAYS AS` column definition.

### Changed

- **Breaking:** `IRetriever.RetrieveAsync` signature changed from `(float[] embedding, RetrievalOptions, CancellationToken)` to `(string query, RetrievalOptions, CancellationToken)`. Vector embeddings are now computed inside `PostgresRetriever` — any custom `IRetriever` implementation must be updated to accept a query string and embed internally.
- **Breaking:** `PostgresRetriever` now requires an `IEmbedder` constructor argument. Existing manual construction sites must add the embedder.
- **Breaking:** `PostgresVectorStore` schema now includes a `text_search TSVECTOR GENERATED ALWAYS AS (to_tsvector(...)) STORED` column and a GIN index. Existing tables must be dropped and recreated. Run `DROP TABLE chunks` before starting the application after upgrading.
- **Breaking:** Options properties changed from `init` to `set` across `PostgresOptions`, `OllamaOptions`, and `RemoteOptions`. Configure-lambda DI registration (e.g. `AddPostgresVectorStore(o => { o.ConnectionString = "..."; })`) now works correctly. Object-initializer construction is unchanged.
- `RetrievalPipeline.QueryAsync` no longer embeds the query string — embedding is delegated to the `IRetriever` implementation. The embedder is still required by `RetrievalPipeline` for ingestion (`IngestAsync`).
- `SearchResult.Score` semantics broadened: cosine similarity in `[-1, 1]` for vector-only retrieval; RRF fusion score (sum of `1/(k+rank)` terms, typically `0.01`–`0.04`) for hybrid retrieval; model-specific score after reranking.

## [0.6.0] - 2026-06-02

### Added

- `MetadataFilterValue` (`Abstractions`) — discriminated union for metadata scalar values: `Text(string)`, `Number(double)`, `Boolean(bool)`. Implicit conversions from `string`, `int`, `long`, `float`, `double`, and `bool` allow natural construction syntax.
- `Metadata` (`Abstractions`) — typed key-value class for document and chunk metadata. Implements `IReadOnlyDictionary<string, MetadataFilterValue>` with a settable indexer and `Add` for collection/index-initializer syntax. Provides structural value equality (`Equals`, `GetHashCode`, `==`, `!=`) and transparent JSON serialization.
- `MetadataFilter` (`Abstractions`) — composable predicate tree for filtering chunks by metadata during retrieval. Node types: `Eq`, `Ne`, `Gt`, `Gte`, `Lt`, `Lte` (field comparisons against a scalar), `In` (set membership), `And`, `Or`, `Not` (logical combinators). Built via static factory methods (`MetadataFilter.Eq(...)`, `MetadataFilter.And(...)`, etc.). Annotated with `[JsonPolymorphic]` so filters survive `Remote.Http` transport without additional configuration.
- `RetrievalOptions.MetadataFilter` (`Abstractions`) — optional `MetadataFilter` applied during retrieval. Only chunks whose metadata satisfies the filter are returned; applied before `TopK` so the result count reflects the filter.
- `MetadataFilterSqlBuilder` (`IV.RAG.Postgres`) — translates a `MetadataFilter` tree to a JSONB SQL fragment pushed down into the `PostgresRetriever` query. Field names are validated against `[a-zA-Z_][a-zA-Z0-9_]*` to prevent injection. `In` values must be homogeneous (all `Text`, all `Number`, or all `Boolean`); mixed types throw `ArgumentException`.

### Changed

- **Breaking:** `Document.Metadata` type changed from `IReadOnlyDictionary<string, object>?` to `Metadata?`.
- **Breaking:** `Chunk.Metadata` type changed from `IReadOnlyDictionary<string, object>?` to `Metadata?`.
- `PostgresRetriever` applies `RetrievalOptions.MetadataFilter` as an additional `AND` clause in the similarity search query when set.
- `Remote.Http` `QueryRequest` now includes the `MetadataFilter` field; `ChunkDto.Metadata` uses `Metadata` instead of `IReadOnlyDictionary<string, JsonElement>`.

## [0.5.0] - 2026-06-01

### Added

- `IVectorStore.SetAsync(Document.Origin, IEnumerable<Chunk>)` (`Abstractions`) — atomically replaces all chunks for a document origin in a single transaction: deletes existing chunks for the origin, then inserts the new set. Validates that all chunk origins match the target origin and that each chunk has a non-null, non-empty `Id` and non-null `Embedding` before touching the database.
- `RetrievalPipeline.IngestAsync` now uses `SetAsync` — re-ingesting a document atomically replaces its chunks. Stale chunks from a shorter or re-chunked document are removed automatically; no manual delete step required.

### Removed

- **Breaking:** `IVectorStore.UpsertAsync` removed. Use `SetAsync` for full document replacement. `DeleteAsync` (by chunk IDs) and `DeleteByDocumentAsync` (by origin) remain for targeted deletions.

## [0.4.0] - 2026-06-01

### Added

- `IGenerator` (`Abstractions`) — `GenerateAsync(query, chunks)`: takes a query and retrieved chunks, returns a generated answer string.
- `IIngestionPipeline` (`Abstractions`) — `IngestAsync`: dedicated interface for the ingestion half of the pipeline.
- `IRetrievalPipeline` (`Abstractions`) — `QueryAsync`: dedicated interface for the retrieval half of the pipeline.
- `IAnswerPipeline` (`Abstractions`) — `AnswerAsync`: dedicated interface for the retrieve → generate loop. Designed for client apps that proxy retrieval remotely and generate locally.
- `RetrievalPipeline` (`Core`) — local implementation of `IIngestionPipeline + IRetrievalPipeline`; owns chunker, embedder, vector store, and retriever.
- `AnswerPipeline` (`Core`) — client-side implementation of `IAnswerPipeline`; delegates to `IRetrievalPipeline + IGenerator`. Does not handle ingestion.
- `AddRetrievalPipeline()` DI entry point — registers `RetrievalPipeline` for server-only deployments (no `IGenerator` needed).
- `AddAnswerPipeline()` DI entry point — registers `AnswerPipeline` for client deployments.
- **`IV.RAG.Ingestion`** — new package. Chunking infrastructure extracted from `Core`: `PlainTextDocument`, `FixedSizeChunker`, `SentenceChunker`, `ChunkerDispatcher`, and all related DI extensions (`AddPlainTextChunker`, `AddSentenceChunker`, `AddChunker<>`).
- **`IV.RAG.Remote.Http`** — new package. `RemoteRetrievalPipeline` implements `IRetrievalPipeline` by forwarding queries to a remote server over HTTP. `AddRemoteRetrievalPipeline()` DI extension. `RemoteOptions` — `Endpoint`, `QueryPath`.
- `OllamaGenerator` (`IV.RAG.Ollama`) — implements `IGenerator` via the Ollama `/api/chat` endpoint.
- `OllamaOptions.GenerationModel` — model used for generation (default `llama3.2`).
- `OllamaOptions.SystemPrompt` — configurable system prompt sent before the user message (default instructs the model to answer using only the provided context).
- `AddOllamaGenerator()` DI extension.
- `OllamaGeneratorTests` (unit, 6 tests) — response content, endpoint path, model config, system prompt config, chunk inclusion, error handling.

### Changed

- **Breaking:** All namespaces renamed from `IV.RagToolkit` to `IV.RAG`.
- **Breaking:** `RagToolkitBuilder` renamed to `RAGBuilder`.
- **Breaking:** `IRagPipeline` is now a marker interface combining `IIngestionPipeline`, `IRetrievalPipeline`, and `IAnswerPipeline`. No members defined directly.
- **Breaking:** `RagPipeline` constructor signature changed from `(IChunker, IEmbedder, IVectorStore, IRetriever, IGenerator, ILogger<RagPipeline>)` to `(IIngestionPipeline, IRetrievalPipeline, IGenerator, ILogger<RagPipeline>)`. It is now a thin delegator.
- **Breaking:** `AddRagToolkit()` no longer registers `IChunker`. Call a chunker extension from `IV.RAG.Ingestion` (e.g. `AddSentenceChunker()`) to register `ChunkerDispatcher` and the chosen chunker.
- **Breaking:** Chunking types (`PlainTextDocument`, `FixedSizeChunker`, `SentenceChunker`, `ChunkerDispatcher`, options classes, DI extensions) moved from `IV.RAG.Core` to the new `IV.RAG.Ingestion` package.
- `RagPipeline.AnswerAsync` now logs at debug level.

## [0.3.0] - 2026-05-31

### Added

- `IChunker<TDocument>` typed interface in `Abstractions` — each chunker declares the exact document type it handles, enabling compile-time safety.
- `ChunkerDispatcher` — implements `IChunker` (pipeline-facing), routes each `Document` to the `IChunker<T>` registered for that runtime type. Walks the inheritance chain, so a subclass of a known document type is handled automatically.
- `PlainTextDocument` (`Core`) — concrete document for plain text (`required string Text`).
- `SentenceChunker` (`Core`) — accumulates sentences into chunks up to `MaxChunkSize` characters. Paragraph breaks (`\n\n`) are hard boundaries; a single sentence that exceeds `MaxChunkSize` is yielded as-is.
- `SentenceChunkerOptions` — `MaxChunkSize` (default 512), `MinChunkLength` (default 0).
- `FixedSizeChunkerOptions.RespectWordBoundaries` (default `true`) — ends each chunk at the last whitespace before the size limit to avoid mid-word cuts.
- `FixedSizeChunkerOptions.MinChunkLength` (default 0) — drops chunks shorter than this value before yielding (e.g. a short trailing fragment).
- `AddPlainTextChunker()` DI extension — registers `FixedSizeChunker` for `PlainTextDocument` with startup-time options validation.
- `AddSentenceChunker()` DI extension — registers `SentenceChunker` for `PlainTextDocument` with startup-time options validation.
- `AddChunker<TDocument, TChunker>()` and `AddChunker<TDocument, TChunker, TOptions>()` DI extensions — for custom document types and chunkers.
- `ChunkerDispatcherTests` (unit) — routing, inheritance-chain walk, unregistered-type error.
- `SentenceChunkerTests` (unit) — sentence accumulation, paragraph hard boundaries, oversized single sentence, `MinChunkLength` filtering, metadata propagation.
- `IngestAndQuery_ViaDI_DispatcherRoutesPlainTextDocument` (integration) — full pipeline wired through DI including the dispatcher.

### Changed

- **Breaking:** `Document.Source` is now `required Origin Source { get; init; }` (non-abstract). Subclasses set it via a `[SetsRequiredMembers]` constructor or object initializer; the `override` is no longer required.
- **Breaking:** `Document` no longer declares `Text`. Content properties live on each concrete document type (`PlainTextDocument.Text`, etc.).
- **Breaking:** `AddFixedSizeChunker()` removed — replaced by `AddPlainTextChunker()`.
- **Breaking:** `AddRagToolkit()` now also registers `ChunkerDispatcher` as `IChunker`. Passing a typed chunker directly to `RagPipeline` still works for test/manual wiring.
- `FixedSizeChunker` now implements `IChunker<PlainTextDocument>` instead of `IChunker`.
- `FixedSizeChunkerOptions` properties changed from `init` to `set` to support the `Microsoft.Extensions.Options` configuration pattern.
- Options validation moved to startup (`ValidateOnStart()`) with `[Range]` attributes on options classes.
- `RagPipeline` log message changed from character count to document type name (since `Document` no longer exposes `Text`).

## [0.2.0] - 2026-05-31

### Added

- `Document.Origin` nested record — three-part provenance key: `SourceId` (Guid), `DocumentType` (string), `DocumentId` (string). Constructor validates all fields are non-empty.
- `Chunk.ChunkIndex` — zero-based position of the chunk within its source document. Set by `RagPipeline` during ingestion.
- `IVectorStore.DeleteByDocumentAsync(Document.Origin)` — removes all chunks belonging to a specific document.
- `PostgresVectorStore`: schema now includes `source_id UUID NOT NULL`, `document_type TEXT NOT NULL`, `document_id TEXT NOT NULL`, `chunk_index INT`, and a `(source_id, document_type, document_id)` index.
- `PostgresRetriever` populates `Chunk.Origin` and `Chunk.ChunkIndex` on retrieved chunks.

### Changed

- **Breaking:** `Document` is now `abstract`. Callers must subclass it and implement `abstract Origin Source { get; }`. Use `[SetsRequiredMembers]` on the constructor to satisfy the `required string Text` constraint.
- **Breaking:** `Chunk.Origin` is now `required Document.Origin` (non-nullable). Every `Chunk` construction site must provide an `Origin`.
- `FixedSizeChunker` propagates `document.Source` to each produced chunk.
- `RagPipeline.IngestAsync` assigns `ChunkIndex` (0-based counter) to each chunk alongside the existing `Id` and `Embedding`.

## [0.1.0] - 2026-05-29

### Added

#### Infrastructure
- Solution structure with `src/`, `tests/unit/`, `tests/integration/`, `tests/e2e/`, `automation/` folders
- `Directory.Build.props` — shared `TargetFramework`, `Nullable`, `TreatWarningsAsErrors`
- `Directory.Build.targets` — `GenerateDocumentationFile` scoped to src packages only
- `Directory.Packages.props` — central NuGet version management
- Solution filters: `IV.RAG.Unit.slnf`, `IV.RAG.Integration.slnf`, `IV.RAG.E2E.slnf`

#### IV.RAG.Abstractions
- `Document` — raw input (text + metadata)
- `Chunk` — unit of currency: text, id, embedding, metadata
- `SearchResult` — chunk with cosine similarity score in `[-1, 1]`
- `RetrievalOptions` — `TopK` and `MinScore` (defaults: 5, 0.0)
- `IChunker` — splits a `Document` into `IAsyncEnumerable<Chunk>`
- `IEmbedder` — generates `float[]` embedding for text
- `IVectorStore` — upsert and delete chunks
- `IRetriever` — cosine similarity search returning `IReadOnlyList<SearchResult>`
- `IRagPipeline` — `IngestAsync` and `QueryAsync` (public-facing API)
- `RAGBuilder` — fluent DI registration contract for provider packages

#### IV.RAG.Core
- `FixedSizeChunker` — fixed character-size chunking with configurable overlap
- `FixedSizeChunkerOptions` — `ChunkSize` (default 512), `Overlap` (default 50)
- `RagPipeline` — orchestrates chunk → embed → store (ingest) and embed → retrieve (query)
- `AddRagToolkit()` and `AddFixedSizeChunker()` DI extensions

#### IV.RAG.Ollama
- `OllamaEmbedder` — calls `/api/embed` endpoint, returns `float[]`
- `OllamaOptions` — `Endpoint` (default `http://localhost:11434`), `EmbeddingModel` (default `nomic-embed-text`)
- `AddOllamaEmbedder()` DI extension with named `IHttpClientFactory` registration

#### IV.RAG.Postgres
- `PostgresVectorStore` — upsert (transactional) and delete via pgvector
- `PostgresRetriever` — cosine similarity search using `<=>` operator; score = `1 - cosine_distance`; filters with `> MinScore`
- `PostgresOptions` — `ConnectionString`, `TableName` (default `chunks`), `VectorDimension` (default 768)
- Schema auto-created on first upsert (`CREATE TABLE IF NOT EXISTS`)
- `AddPostgresVectorStore()` DI extension
- **Note:** the `vector` PostgreSQL extension must be pre-installed before application start

#### Tests
- **Unit** (19 tests): `FixedSizeChunker` (9), `RagPipeline` (6), `OllamaEmbedder` (4)
- **Integration** (14 tests): `PostgresVectorStore`, `PostgresRetriever`, full pipeline — real Postgres via Testcontainers, deterministic `FakeEmbedder` with 3D unit vectors
- **E2E** (3 tests): full pipeline against real Ollama + Testcontainers Postgres — verifies embedding dimension and semantic similarity ordering
