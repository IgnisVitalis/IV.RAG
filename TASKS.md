# Tasks

Backlog ordered by priority. Complete items are removed.

Derived from the architecture analysis in `CLAUDERESULT.md`. Each task states **what** to
change, **where** (file references), and **how** / acceptance criteria. File references are
indicative of v0.9.0 and may shift as work lands.

---

## Tier 2 — Throughput & robustness

- [ ] **Batched / COPY-based inserts in the vector store**
  `SetAsync` inserts each chunk with a separate `INSERT ... ExecuteNonQueryAsync` inside the
  loop (`PostgresVectorStore.cs:71-91`).
  - Replace with Npgsql binary `COPY` (`NpgsqlBinaryImporter`) or multi-row parameterized
    `INSERT` batching, inside the existing delete-then-insert transaction.
  - Keep the up-front validation (origin match, non-null Id/Embedding) unchanged.
  - Add an integration test ingesting a large chunk set to guard correctness of the batched
    path (counts, origin columns, model_id all populated).

- [ ] **HTTP resilience & timeouts on provider clients**
  `AddOllamaEmbedder` / `AddOllamaGenerator` / `AddRemoteRetrievalPipeline` register a bare
  `AddHttpClient` with only `BaseAddress` (e.g. `Ollama/ServiceCollectionExtensions.cs:21`).
  No timeout, retry, or circuit breaker — a hung Ollama generation blocks the caller
  indefinitely.
  - Add `Microsoft.Extensions.Http.Resilience` and apply the standard resilience handler to
    each named client.
  - Expose `TimeoutSeconds` (and optional retry count) on `OllamaOptions` and `RemoteOptions`;
    wire into the handler. Pick generation-friendly defaults (generation timeout >> embed
    timeout).

- [ ] **Validate table / identifier names**
  `TextSearchLanguage` and metadata field names are validated, but `TableName` and
  `QueryCacheTableName` are interpolated into SQL unvalidated (e.g. `PostgresVectorStore.cs:60`,
  `PostgresQueryCache.cs:144`). Consistency gap and latent injection vector if a table name is
  ever sourced from config/user input.
  - Validate both against `^[a-zA-Z_][a-zA-Z0-9_]*$` (optionally allow a single
    `schema.table` qualification), once at first use / options binding. Reuse the existing
    `SafeLanguage`-style regex helper.

- [ ] **Startup configuration validation**
  Chunkers `ValidateOnStart`, but `PostgresOptions.ConnectionString` (empty default) and
  `OllamaOptions.Endpoint` are unvalidated until first use, so failures surface late and
  cryptically.
  - Add `ValidateOnStart` rules: non-empty `ConnectionString`; `Endpoint` parses as an
    absolute `Uri`. Apply in `AddPostgresVectorStore` / `AddOllamaEmbedder` /
    `AddOllamaGenerator` / `AddRemoteRetrievalPipeline`.

## Tier 3 — Service ergonomics

- [ ] **Streaming generation**
  `IGenerator.GenerateAsync` returns a materialized string and `OllamaGenerator` sets
  `Stream: false` (`OllamaGenerator.cs:36`). End-user chat UIs want token streaming.
  - Add `IAsyncEnumerable<string> GenerateStreamAsync(string query, IReadOnlyList<SearchResult>,
    CT)` to `IGenerator` and a corresponding `AnswerStreamAsync` on `IAnswerPipeline` /
    `RagPipeline` / `AnswerPipeline`.
  - Implement streaming in `OllamaGenerator` (NDJSON streaming from `/api/chat` with
    `Stream: true`).

- [ ] **Shared remote contract + server endpoint helper**
  The `Remote.Http` topology needs a server that exposes a matching JSON contract, but
  `QueryRequest`/`QueryResponse`/`SearchResultDto`/`ChunkDto`/`OriginDto` are `internal` to
  the client package (e.g. `QueryRequest.cs:5`). A server author re-implements the shape by
  hand, inviting drift.
  - Extract the DTOs into a new `IV.RAG.Remote.Contracts` package (public, depends only on
    `Abstractions`), referenced by `IV.RAG.Remote.Http`.
  - Add an optional server-side mapping helper (and/or a minimal-API endpoint extension) that
    binds `IRetrievalPipeline` to the query contract, closing the client/server loop.
  - Update README's server-only and client topologies to reference the shared package.

- [ ] **Observability: tracing, metrics, health checks**
  Good `ILogger` usage exists, but no `ActivitySource`/metrics and no `IHealthCheck` for
  Postgres/Ollama reachability.
  - Add an `ActivitySource` with spans around ingest / embed / retrieve / generate, and
    counters/histograms (chunks ingested, retrieval latency, cache hit ratio, embed calls).
  - Add `IHealthCheck` implementations for the Postgres data source and the Ollama endpoint,
    with `Add*HealthCheck()` extensions.

- [ ] **Structured answer with citations**
  `AnswerAsync` returns a bare string; end apps routinely need source attribution.
  - Add an `AnswerResult { string Text; IReadOnlyList<SearchResult> Sources }` and an
    overload/variant on `IAnswerPipeline` that returns it, leaving the simple string path
    intact.

- [ ] **Generator context-window budget**
  `OllamaGenerator.BuildContext` concatenates all retrieved chunks unbounded
  (`OllamaGenerator.cs:44`); large `TopK` × large chunks can overflow the model context.
  - Add a configurable char/token cap to `OllamaOptions` with truncation (drop lowest-ranked
    chunks first) and a debug log when truncation occurs.

## Tier 4 — Access control & multi-store (multi-tenant service primitives)

These were the original backlog. For a RAG service serving end applications they are
correctness/security primitives, not just features, and pair naturally with Tiers 1–2.

- [ ] **Origin-based scoping in retrieval**
  Extend `RetrievalOptions` with optional `SourceId`, `DocumentType`, and `DocumentId` fields.
  When set, the retrieval query adds a WHERE clause on the corresponding origin columns
  (already indexed via `{table}_origin_idx`). Apply in both `PostgresRetriever` and
  `PostgresLexicalRetriever` (and propagate through `HybridRetrievalPipeline`'s candidate
  options). This is the toolkit's access-control primitive: the application resolves which
  scope the current user is allowed and passes it to options — the toolkit enforces it in SQL.

- [ ] **Mandatory retrieval filter (access-control guard)**
  Allow registering a required filter that is always merged into every `RetrievalOptions`,
  regardless of what the caller provides. Prevents accidental data leaks when the application
  forgets a tenant/permission filter. Implement as a thin `IRetrievalPipeline` decorator (same
  pattern as `CachedRetrievalPipeline`, wired via the `InnerPipelineKey` keyed service); a
  filter factory receives the current scope (e.g. tenant ID from DI) and returns an
  `AndMetadataFilter` combined with the caller's own filter. Must compose correctly with both
  the cached and hybrid pipelines.

- [ ] **Multi-table / keyed DI support**
  Allow registering multiple `IVectorStore` instances for different domains, each pointing to a
  different table with its own model config. Design questions: key the `NpgsqlDataSource` and
  `PostgresOptions` per store; provide keyed `Add*` overloads; decide how pipelines select a
  store. Composes with the existing factory-based singleton registrations.

## Tier 5 — Polish

- [ ] **De-duplicate the answer loop**
  `RagPipeline.AnswerAsync` (`RagPipeline.cs:41`) duplicates `AnswerPipeline.AnswerAsync`
  (`AnswerPipeline.cs:24`). Have `RagPipeline` compose an `AnswerPipeline` (or a shared helper)
  instead of reimplementing the retrieve→generate logic.

- [ ] **In-memory cache LRU eviction**
  `InMemoryQueryCache` evicts FIFO via `_entries.RemoveAt(0)` (`InMemoryQueryCache.cs:70`),
  not least-recently-used. Either implement true LRU (touch on read) or document the FIFO
  behavior explicitly.

- [ ] **Robust Ollama embed response handling**
  `OllamaEmbedder` does `result!.Embeddings[0]` (`OllamaEmbedder.cs:42`); an empty payload
  throws an opaque `NullReference`/`IndexOutOfRange`. Guard with a descriptive exception
  naming the model and endpoint.

- [ ] **Resolve embedding dimensions eagerly when auto-detecting**
  `OllamaEmbedder.ModelInfo.Dimensions` mutates after the first embed when auto-detect is on
  (`OllamaEmbedder.cs:16`), making schema-init ordering significant (documented sharp edge).
  Consider a startup probe embed (or hosted-service warm-up) so `ModelInfo` is stable before
  any store operation, removing the ordering caveat.

- [ ] **Friendly incomplete-configuration errors**
  Resolving a pipeline without a required provider (embedder/store/generator) fails with a
  generic DI message. Add a `RAGBuilder.Validate()` / build-time guard that reports exactly
  which required registrations are missing and the ordering rules (e.g. `AddCachedRetrieval()`
  last; `AddPostgresVectorStore()` before `AddPostgresLexicalRetriever()`).

- [ ] **`.editorconfig`**
  Standardise line endings and formatting across the solution.
