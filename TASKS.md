# Tasks

Backlog ordered by priority. Complete items are removed.

Derived from the architecture analysis in `CLAUDERESULT.md`. Each task states **what** to
change, **where** (file references), and **how** / acceptance criteria. File references are
indicative of v0.9.0 and may shift as work lands.

---

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
