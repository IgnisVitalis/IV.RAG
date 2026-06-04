# Tasks

Backlog ordered by priority. Complete items are removed.

Derived from the architecture analysis in `CLAUDERESULT.md`. Each task states **what** to
change, **where** (file references), and **how** / acceptance criteria. File references are
indicative of v0.9.0 and may shift as work lands.

---

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
