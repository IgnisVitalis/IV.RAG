# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
