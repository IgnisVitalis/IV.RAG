# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-05-29

### Added

#### Infrastructure
- Solution structure with `src/`, `tests/unit/`, `tests/integration/`, `tests/e2e/`, `automation/` folders
- `Directory.Build.props` — shared `TargetFramework`, `Nullable`, `TreatWarningsAsErrors`
- `Directory.Build.targets` — `GenerateDocumentationFile` scoped to src packages only
- `Directory.Packages.props` — central NuGet version management
- Solution filters: `IV.RagToolkit.Unit.slnf`, `IV.RagToolkit.Integration.slnf`, `IV.RagToolkit.E2E.slnf`

#### IV.RagToolkit.Abstractions
- `Document` — raw input (text + metadata)
- `Chunk` — unit of currency: text, id, embedding, metadata
- `SearchResult` — chunk with cosine similarity score in `[-1, 1]`
- `RetrievalOptions` — `TopK` and `MinScore` (defaults: 5, 0.0)
- `IChunker` — splits a `Document` into `IAsyncEnumerable<Chunk>`
- `IEmbedder` — generates `float[]` embedding for text
- `IVectorStore` — upsert and delete chunks
- `IRetriever` — cosine similarity search returning `IReadOnlyList<SearchResult>`
- `IRagPipeline` — `IngestAsync` and `QueryAsync` (public-facing API)
- `RagToolkitBuilder` — fluent DI registration contract for provider packages

#### IV.RagToolkit.Core
- `FixedSizeChunker` — fixed character-size chunking with configurable overlap
- `FixedSizeChunkerOptions` — `ChunkSize` (default 512), `Overlap` (default 50)
- `RagPipeline` — orchestrates chunk → embed → store (ingest) and embed → retrieve (query)
- `AddRagToolkit()` and `AddFixedSizeChunker()` DI extensions

#### IV.RagToolkit.Ollama
- `OllamaEmbedder` — calls `/api/embed` endpoint, returns `float[]`
- `OllamaOptions` — `Endpoint` (default `http://localhost:11434`), `EmbeddingModel` (default `nomic-embed-text`)
- `AddOllamaEmbedder()` DI extension with named `IHttpClientFactory` registration

#### IV.RagToolkit.Postgres
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
