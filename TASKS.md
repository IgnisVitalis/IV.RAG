# Tasks

Backlog ordered by priority. Complete items are removed.

## High priority

- [ ] **Origin-based scoping in retrieval**
  Extend `RetrievalOptions` with optional `SourceId`, `DocumentType`, and `DocumentId` fields.
  When set, the retrieval query adds a WHERE clause on the corresponding origin columns.
  This is the toolkit's access control primitive: the application resolves which scope
  the current user is allowed and passes it to options — the toolkit enforces it in SQL.

- [ ] **Mandatory retrieval filter (access control guard)**
  Allow registering a required `MetadataFilter` that is always merged into every `RetrievalOptions`,
  regardless of what the caller provides. Prevents accidental data leaks when the application
  forgets to apply a tenant or permission filter. Implemented as a thin `IRetrievalPipeline`
  decorator; filter factory receives the current scope (e.g. tenant ID from DI) and returns
  an `AndMetadataFilter` combined with the caller's own filter.

## Medium priority

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
