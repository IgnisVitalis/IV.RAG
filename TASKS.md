# Tasks

Backlog ordered by priority. Complete items are removed.

## High priority

- [ ] **Metadata filtering in retrieval**
  Extend `RetrievalOptions` with a `MetadataFilter` property.
  Filter chunks by stored metadata values during similarity search.

- [ ] **Origin-based scoping in retrieval**
  Extend `RetrievalOptions` with optional `SourceId`, `DocumentType`, and `DocumentId` fields.
  When set, the retrieval query adds a WHERE clause on the corresponding origin columns.
  This is the toolkit's access control primitive: the application resolves which scope
  the current user is allowed and passes it to options — the toolkit enforces it in SQL.

## Medium priority

- [ ] **Hybrid search (vector + lexical/BM25)**
  Add `ILexicalRetriever` interface alongside `IRetriever`.
  Add `HybridRetriever` in Core that fuses both rankings via Reciprocal Rank Fusion (RRF).
  Add `PostgresLexicalRetriever` backed by `tsvector/tsquery`.

- [ ] **Semantic query cache**
  Add `IQueryCache` interface. Cache query embeddings and their results.
  Add `CachedRagPipeline` decorator in Core (transparent to consumers).
  Add in-memory implementation in Core.
  Add Postgres implementation in the Postgres package.
  Configurable similarity threshold (default 0.95) and TTL.

- [ ] **Embedding model versioning**
  Expose model identity (`provider`, `name`, `version`, `dimensions`) on `IEmbedder`.
  Store model metadata alongside each vector table.
  Detect model mismatch on startup and throw a clear error.
  Add `ClearAsync()` and `GetSourceIdsAsync()` to `IVectorStore` to support re-ingestion.

- [ ] **Multi-table / keyed DI support**
  Allow registering multiple `IVectorStore` instances for different domains.
  Each registration points to a different table with its own model config.

## Lower priority

- [ ] **`.editorconfig`**
  Standardise line endings and formatting across the solution.
