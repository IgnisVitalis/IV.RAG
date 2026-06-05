# Tasks

Backlog ordered by priority. Complete items are removed.

Derived from the architecture analysis in `CLAUDERESULT.md`. Each task states **what** to
change, **where** (file references), and **how** / acceptance criteria. File references are
indicative of v0.9.0 and may shift as work lands.

---

## Tier 5 — Polish

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

