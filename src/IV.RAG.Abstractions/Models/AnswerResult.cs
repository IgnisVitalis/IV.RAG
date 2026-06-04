namespace IV.RAG;

/// <summary>A generated answer together with the retrieved chunks it was grounded in.</summary>
/// <param name="Text">The generated answer.</param>
/// <param name="Sources">The chunks retrieved as context, for source attribution / citations.</param>
public sealed record AnswerResult(string Text, IReadOnlyList<SearchResult> Sources);
