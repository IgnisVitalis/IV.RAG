# Roadmap

Next steps for future sessions, grouped by theme. No particular order within groups.

## v0.2 — Core pipeline improvements

- [ ] **`SourceId` on `Document` and `Chunk`**
  First-class source document reference. Propagated automatically during ingestion.
  Enables filtering by source and deletion of all chunks belonging to a document.
  Add `DeleteBySourceAsync(string sourceId)` to `IVectorStore`.

- [ ] **Metadata filtering in retrieval**
  Extend `RetrievalOptions` with a `MetadataFilter` property.
  Filter chunks by stored metadata values during similarity search.

- [ ] **Embedding model versioning**
  Expose model identity (`provider`, `name`, `version`, `dimensions`) on `IEmbedder`.
  Store model metadata alongside each vector table.
  Detect model mismatch on startup and throw a clear error.
  Add `ClearAsync()` and `GetSourceIdsAsync()` to `IVectorStore` to support re-ingestion.

- [ ] **Multi-table / keyed DI support**
  Allow registering multiple `IVectorStore` instances for different domains.
  Each registration points to a different table with its own model config.

## v0.2 — Search quality

- [ ] **Hybrid search (vector + lexical/BM25)**
  Add `ILexicalRetriever` interface alongside `IRetriever`.
  Add `HybridRetriever` in Core that fuses both rankings via Reciprocal Rank Fusion (RRF).
  Add `PostgresLexicalRetriever` backed by `tsvector/tsquery`.

## v0.3 — Generation

- [ ] **`IGenerator` interface**
  Takes a query and a list of retrieved chunks, returns a generated answer string.
  Add `AnswerAsync` to `IRagPipeline` as the full RAG loop: retrieve + generate.
  Add `OllamaGenerator` backed by `/api/chat` endpoint.

## v0.3 — Performance

- [ ] **Semantic query cache**
  Add `IQueryCache` interface. Cache query embeddings and their results.
  Add `CachedRagPipeline` decorator in Core (transparent to consumers).
  Add in-memory implementation in Core.
  Add Postgres implementation in the Postgres package.
  Configurable similarity threshold (default 0.95) and TTL.

## Infrastructure / tooling

- [ ] **NuGet publishing automation**
  Scripts in `automation/` for pack and publish.
  CI pipeline distinguishing unit, integration, and E2E stages.

- [ ] **`.editorconfig`**
  Standardise line endings and formatting across the solution.
