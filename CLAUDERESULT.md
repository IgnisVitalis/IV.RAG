# IV.RAG — Architecture & Infrastructure Analysis

> Deep analysis of the IV.RAG toolkit as of v0.9.0 (2026-06-03).
> Scope: infrastructure and architecture only. No code changes were made.
> Goal lens: *easy to use, easy to configure, easy to extend, easy to customize.*

---

## 1. Executive summary

IV.RAG is a **well-architected, disciplined** pre-1.0 toolkit. The layering is clean,
the dependency rule is respected without exception, interface segregation is excellent,
and the DI ergonomics (`RAGBuilder` fluent chain + provider-supplied `Add*` extensions)
deliver on the "swap a provider in one registration" promise. XML docs, central package
management, and `TreatWarningsAsErrors` show real engineering hygiene.

The gaps are not in the *shape* of the architecture — they are in **production
infrastructure concerns** that a RAG service exposed to end applications will hit
quickly:

- **No ANN vector index** is ever created → similarity search is a full sequential scan. *(Critical, performance)*
- **Double embedding on every cache miss** → the cache layer embeds, then the retriever embeds again. *(Important, performance)*
- **No batch embedding API** → ingestion issues N sequential HTTP round-trips. *(Important, performance)*
- **Schema auto-DDL has no cross-process coordination** → multiple instances can race on destructive `ALTER`/`TRUNCATE`. *(Important, correctness)*
- **`TableName` is interpolated into SQL without validation**, unlike language and field names. *(Important, consistency/safety)*

None of these require architectural rework — they are additive. The bones are sound.

---

## 2. What the architecture gets right

| Area | Assessment |
|---|---|
| **Dependency rule** | `Abstractions` has zero project references; every other package references only `Abstractions`. No sibling references. Verified across all `src/` projects. |
| **Interface segregation** | `IIngestionPipeline` / `IRetrievalPipeline` / `IAnswerPipeline` split cleanly by deployment topology; `IRagPipeline` is a pure marker composing the three. This is what makes the server-only / client-only / full-local topologies work without conditional code. |
| **Decorator composition** | `CachedRetrievalPipeline` and `HybridRetrievalPipeline` both implement `IRetrievalPipeline` and wrap/replace transparently. The keyed-service trick (`InnerPipelineKey`) to break the cache→pipeline circular dependency is clean and correct. |
| **Extension model** | Every swappable concern is an interface in `Abstractions`; `IChunker<TDocument>` + `ChunkerDispatcher` (inheritance-chain walk) is a genuinely nice typed-dispatch design. Custom document types/chunkers are a 3-line registration. |
| **Embedding model versioning** | The `{table}_models` companion table, mismatch detection, in-place migration, dimension-change handling, and NOT NULL tightening form a thoughtful, complete feature. Few hobby RAG libraries handle model drift at all. |
| **SQL injection posture (mostly)** | Metadata field names validated against `^[a-zA-Z_][a-zA-Z0-9_]*$`; `TextSearchLanguage` validated; all *values* parameterized. `MetadataFilterSqlBuilder` is careful. |
| **Wire-format portability** | `[JsonPolymorphic]` on `MetadataFilter` + the custom `MetadataFilterValue`/`Metadata` converters mean filters survive `Remote.Http` transport without extra config. |
| **Config hygiene** | Central `Directory.Packages.props`, shared `Directory.Build.props`, options validation at startup for chunkers (`ValidateOnStart`). |

---

## 3. Findings by severity

References use `path:line`. Severity reflects impact on a production RAG service for end apps.

### 3.1 Critical

#### C1 — No ANN index on the vector column (full scan on every query)
`PostgresVectorStore.EnsureSchemaAsync` creates indexes on `(source_id, document_type, document_id)`,
`model_id`, and a GIN index on `text_search`, but **never** an HNSW or IVFFlat index on the
`embedding` column ([PostgresVectorStore.cs:227-232](src/IV.RAG.Postgres/PostgresVectorStore.cs:227)).
Confirmed by search: no `hnsw`/`ivfflat`/`vector_cosine_ops` anywhere in `src/`.

Consequence: `ORDER BY embedding <=> @embedding` ([PostgresRetriever.cs:49](src/IV.RAG.Postgres/PostgresRetriever.cs:49))
performs an **exact KNN with a sequential scan**. This is fine for a few thousand rows and
catastrophic beyond that — query latency grows linearly with corpus size. For a toolkit
whose entire value proposition is retrieval, this is the most important gap.

**Recommendation:** Add an opt-in HNSW index, e.g.
`CREATE INDEX ... USING hnsw (embedding vector_cosine_ops)` with `m`/`ef_construction`
exposed via `PostgresOptions`. Caveats to design around: HNSW build needs a known dimension
(already resolved in `EnsureSchemaAsync`), the dimension-change path must drop/recreate it,
and very large initial loads are faster if the index is created *after* bulk insert. A
`VectorIndex = None | Hnsw | IVFFlat` option with sensible defaults would suffice.

### 3.2 Important

#### I1 — Double embedding on cache miss
`CachedRetrievalPipeline.QueryAsync` embeds the query to probe the cache
([CachedRetrievalPipeline.cs:38](src/IV.RAG.Core/Pipeline/CachedRetrievalPipeline.cs:38)), then on
a miss calls `_inner.QueryAsync`, and `PostgresRetriever.RetrieveAsync` **embeds the same
query again** ([PostgresRetriever.cs:29](src/IV.RAG.Postgres/PostgresRetriever.cs:29)). Hybrid
retrieval embeds a third time inside its vector sub-retriever. Every cold query pays for 2–3
embedding calls.

The root cause is an interface seam: `IRetriever.RetrieveAsync(string query, ...)` was
deliberately changed in 0.7.0 to take a string and embed internally, which is great for
*simplicity* but removes the ability to reuse a precomputed vector. Options:
- Add an overload / optional precomputed-embedding path on `IRetriever`, or
- Introduce an internal `IVectorRetriever.RetrieveByVectorAsync(float[] ...)` that the
  string-based method delegates to, and let the cache/hybrid layers reuse the vector.

#### I2 — No batch embedding; ingestion is sequential round-trips
`IEmbedder.EmbedAsync` takes a single string ([IEmbedder.cs:10](src/IV.RAG.Abstractions/IEmbedder.cs:10)),
and `RetrievalPipeline.IngestAsync` embeds chunks one-by-one in a `foreach`
([RetrievalPipeline.cs:41-45](src/IV.RAG.Core/Pipeline/RetrievalPipeline.cs:41)). A 500-chunk
document = 500 serial HTTP calls to Ollama. Ollama's `/api/embed` accepts arrays.

**Recommendation:** Add `Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, ...)`
to `IEmbedder` (or a separate `IBatchEmbedder`), batch in the ingestion loop, and reuse the
same batching in `EmbeddingMigrator`.

#### I3 — Row-by-row INSERT in `SetAsync`
Inside the ingestion transaction, each chunk is a separate `INSERT ... ExecuteNonQueryAsync`
in a loop ([PostgresVectorStore.cs:71-91](src/IV.RAG.Postgres/PostgresVectorStore.cs:71)).
For large documents this is slow. Npgsql binary `COPY` (or at least multi-row `INSERT`
batching) would be markedly faster. Combined with I2, ingestion throughput is the weakest
operational dimension today.

#### I4 — Schema auto-DDL has no cross-process coordination
`EnsureSchemaAsync` guards with a process-local `SemaphoreSlim`
([PostgresVectorStore.cs:191](src/IV.RAG.Postgres/PostgresVectorStore.cs:191)), but the
dimension-change path runs `ALTER COLUMN ... TYPE vector(n) USING NULL`
([PostgresVectorStore.cs:283-287](src/IV.RAG.Postgres/PostgresVectorStore.cs:283)) and the
query cache runs `TRUNCATE` + `ALTER`
([PostgresQueryCache.cs:200-205](src/IV.RAG.Postgres/PostgresQueryCache.cs:200)). With more
than one app instance pointed at the same database, these can interleave destructively on a
shared startup. `CREATE TABLE IF NOT EXISTS` is idempotent, but the destructive `ALTER`/`TRUNCATE`
paths are not race-safe across processes.

**Recommendation:** Wrap the schema-mutation section in a PostgreSQL advisory lock
(`pg_advisory_xact_lock(<stable key derived from table name>)`) so only one instance performs
DDL at a time. Separately, consider a `MigrateSchema = Auto | None` switch so production
deployments that use explicit migrations / least-privilege runtime accounts can disable
runtime DDL entirely.

#### I5 — `TableName` / `QueryCacheTableName` not validated before interpolation
Every Postgres query interpolates `_options.TableName` and `{table}_models` directly into the
command text (e.g. [PostgresVectorStore.cs:60](src/IV.RAG.Postgres/PostgresVectorStore.cs:60),
[PostgresQueryCache.cs:144](src/IV.RAG.Postgres/PostgresQueryCache.cs:144)). Table/identifier
names can't be parameterized, so this is by necessity string interpolation — but `TextSearchLanguage`
and metadata field names *are* validated with a regex while table names are not. This is a
consistency gap and a latent injection vector if a consumer ever sources a table name from
configuration/user input.

**Recommendation:** Validate `TableName` and `QueryCacheTableName` against the same
`^[a-zA-Z_][a-zA-Z0-9_]*$` pattern (optionally allowing a schema-qualified `schema.table`),
once, at options binding / first use.

#### I6 — No streaming generation
`IGenerator.GenerateAsync` returns a fully materialized `string`
([IGenerator.cs:7](src/IV.RAG.Abstractions/IGenerator.cs:7)); `OllamaGenerator` sets
`Stream: false` ([OllamaGenerator.cs:36](src/IV.RAG.Ollama/OllamaGenerator.cs:36)). End-user
chat experiences almost always want token streaming. Consider an
`IAsyncEnumerable<string> GenerateStreamAsync(...)` (and a corresponding
`AnswerStreamAsync` on `IAnswerPipeline`).

#### I7 — No HTTP resilience, timeouts, or retry on provider clients
`AddOllamaEmbedder`/`AddOllamaGenerator`/`AddRemoteRetrievalPipeline` register a bare
`AddHttpClient` with only a `BaseAddress`
([Ollama/ServiceCollectionExtensions.cs:21](src/IV.RAG.Ollama/ServiceCollectionExtensions.cs:21)).
No timeout, no retry, no circuit breaker. A slow/hung Ollama generation will block the caller
indefinitely. Add `Microsoft.Extensions.Http.Resilience` (standard handler) and expose a
configurable timeout in the options.

#### I8 — Remote wire contract is private to the client package
The `Remote.Http` topology depends on a server exposing a matching JSON contract, but
`QueryRequest`/`QueryResponse`/`SearchResultDto`/`ChunkDto`/`OriginDto` are all `internal`
to `IV.RAG.Remote.Http` (e.g. [QueryRequest.cs:5](src/IV.RAG.Remote.Http/Http/QueryRequest.cs:5)).
A server author must hand-reimplement the exact shape with no shared, versioned contract —
fragile and easy to drift. Consider a small `IV.RAG.Remote.Contracts` package (public DTOs)
shared by client and server, plus an optional server-side mapping helper / minimal-API
endpoint extension to close the topology.

### 3.3 Minor / polish

- **M1 — `RagPipeline.AnswerAsync` duplicates `AnswerPipeline.AnswerAsync`.** Identical
  retrieve→generate logic in two places
  ([RagPipeline.cs:41](src/IV.RAG.Core/Pipeline/RagPipeline.cs:41),
  [AnswerPipeline.cs:24](src/IV.RAG.Core/Pipeline/AnswerPipeline.cs:24)). `RagPipeline` could
  compose an `AnswerPipeline` instead of reimplementing.
- **M2 — `PostgresQueryCache` schema init isn't locked like the vector store's.** It uses a
  `Volatile` int flag with no semaphore ([PostgresQueryCache.cs:149-181](src/IV.RAG.Postgres/PostgresQueryCache.cs:149)),
  so two concurrent first calls can both run the `TRUNCATE`/`ALTER` adaptation. Inconsistent
  with the careful `SemaphoreSlim` in `PostgresVectorStore`.
- **M3 — In-memory cache eviction is FIFO, not LRU.** `_entries.RemoveAt(0)` drops the
  oldest-inserted, not least-recently-used ([InMemoryQueryCache.cs:70](src/IV.RAG.Core/Cache/InMemoryQueryCache.cs:70)).
  Fine at `MaxEntries=1000`, but worth a doc note or a true LRU.
- **M4 — `OllamaEmbedder` assumes a non-empty response.** `result!.Embeddings[0]`
  ([OllamaEmbedder.cs:42](src/IV.RAG.Ollama/OllamaEmbedder.cs:42)) throws an opaque
  `NullReference`/`IndexOutOfRange` if Ollama returns an empty payload. A guarded, descriptive
  exception would aid diagnosis.
- **M5 — No generator context-window budget.** `OllamaGenerator.BuildContext` concatenates all
  retrieved chunks unbounded ([OllamaGenerator.cs:44-53](src/IV.RAG.Ollama/OllamaGenerator.cs:44)).
  Large `TopK` × large chunks can exceed the model's context. Consider a char/token cap with
  truncation.
- **M6 — Config validation gaps.** Chunkers `ValidateOnStart`, but `PostgresOptions.ConnectionString`
  (empty default) and `OllamaOptions.Endpoint` are unvalidated until first use; failures surface
  late and cryptically. Add presence/URI validation at startup.
- **M7 — `EmbedderInfo` is effectively mutable.** `OllamaEmbedder.ModelInfo.Dimensions` changes
  after the first embed when auto-detection is used ([OllamaEmbedder.cs:16-24](src/IV.RAG.Ollama/OllamaEmbedder.cs:16)).
  The vector store reads it during schema init, so ordering matters (the README documents the
  sharp edge). It works, but a model *identity* that mutates is a subtle smell; consider
  resolving dimensions eagerly (a probe embed at startup) when auto-detect is on.
- **M8 — Missing observability surface.** Good `ILogger` usage throughout, but no
  `ActivitySource`/metrics and no `IHealthCheck` for Postgres/Ollama reachability. For a
  service toolkit, OpenTelemetry spans (embed/retrieve/generate) and health checks are
  high-value, low-cost additions.
- **M9 — No structured answer / citations.** `AnswerAsync` returns a bare string; end apps
  routinely need source attribution. An `AnswerResult { string Text; IReadOnlyList<SearchResult> Sources }`
  variant would serve real UIs without breaking the simple path.

---

## 4. Assessment against the stated goals

### Easy to use — **Strong**
Quick-start is genuinely short; the `IRagPipeline` / `IAnswerPipeline` split means a consumer
injects exactly one interface for their topology. Main friction: the embedding-dimension
ordering caveat (M7), and the lack of a friendly error when a required provider isn't
registered (resolution fails with a generic DI message).

### Easy to configure — **Strong, with edges**
`RAGBuilder` + per-provider `Add*` + `Options` pattern is idiomatic and discoverable. The
"call `AddCachedRetrieval()` last" and "call `AddPostgresVectorStore()` before
`AddPostgresLexicalRetriever()`" ordering requirements are implicit and only enforced at
resolve time — a `RAGBuilder.Validate()` / build-time guard listing missing pieces would make
misconfiguration self-explanatory.

### Easy to extend — **Strong**
This is the architecture's best dimension. Every concern is an interface in `Abstractions`;
adding a new vector DB, embedder, generator, reranker, or cache is a single implementation +
a single `Add*` extension. The typed `IChunker<TDocument>` dispatch is excellent. The
`IReranker` seam (no built-in impl, by design) is a clean BYO point.

### Easy to customize — **Strong for behavior, weak for the client/server seam**
Swapping any implementation is trivial. The one place customization is awkward is the
`Remote.Http` topology (I8): because the wire DTOs are internal, customizing or implementing
the *server* half means re-deriving the contract by hand.

---

## 5. Suggested roadmap (prioritized)

The actionable, detailed breakdown of every item below — with file references and acceptance
criteria — lives in **`TASKS.md`**, organized into the same tiers. This section is the
rationale; `TASKS.md` is the work list. Keep the two in sync when items land.

**Tier 1 — production-readiness blockers**
1. HNSW/IVFFlat vector index with `PostgresOptions` knobs (C1).
2. Cross-process advisory-lock around schema DDL + `SchemaManagement { Auto | None }` opt-out (I4).
3. Eliminate double embedding via a vector-reuse seam on `IRetriever` (I1).

**Tier 2 — throughput & robustness**
4. Batch embedding API (I2).
5. Batched / `COPY`-based inserts in the vector store (I3).
6. HTTP resilience/timeout on all provider clients (I7).
7. Validate table/identifier names (I5).
8. Startup configuration validation (M6).

**Tier 3 — service ergonomics**
9. Streaming generation (`GenerateStreamAsync` / `AnswerStreamAsync`) (I6).
10. Shared `Remote.Contracts` package + optional server endpoint helper (I8).
11. Observability: tracing, metrics, health checks (M8).
12. Structured answer with citations (M9).
13. Generator context-window budget (M5).

**Tier 4 — access control & multi-store (multi-tenant service primitives)**
These were the original backlog. For a multi-tenant RAG service they are *more than* "high
priority" — they are correctness/security primitives and pair naturally with Tier 1–2 work.
14. Origin-based retrieval scoping.
15. Mandatory retrieval filter (access-control guard) — a thin `IRetrievalPipeline` decorator.
16. Multi-table / keyed-DI vector stores — compose cleanly with the existing factory
    registrations; the main design question is keying the `NpgsqlDataSource` and options per
    store.

**Tier 5 — polish**
17. De-duplicate the answer loop (M1).
18. Lock query-cache schema init for consistency with the vector store (M2).
19. In-memory cache LRU eviction (M3).
20. Robust Ollama embed response handling (M4).
21. Resolve embedding dimensions eagerly when auto-detecting (M7).
22. Friendly incomplete-configuration errors.
23. `.editorconfig`.

---

## 6. Bottom line

The architecture is **clean, principled, and extensible** — it already delivers on
"easy to use / configure / extend / customize" at the API level. The work remaining before
1.0 is concentrated almost entirely in **infrastructure hardening** (vector index, batching,
DDL coordination, resilience, observability) rather than redesign. Address Tier 1 first; it
unblocks real-world corpus sizes and multi-instance deployments, which is where the current
design will otherwise break first.
