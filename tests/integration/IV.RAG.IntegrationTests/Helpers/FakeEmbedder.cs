namespace IV.RAG.IntegrationTests.Helpers;

/// <summary>
/// Deterministic embedder for integration tests.
/// Uses known vectors so similarity relationships are predictable.
/// </summary>
internal sealed class FakeEmbedder : IEmbedder
{
    private readonly Func<string, float[]> _embed;

    /// <inheritdoc/>
    public EmbedderInfo ModelInfo { get; }

    internal FakeEmbedder(Func<string, float[]> embed, int dimensions = 3, string modelName = "fake-model")
    {
        _embed = embed;
        ModelInfo = new EmbedderInfo("fake", modelName, dimensions);
    }

    /// <summary>Creates an embedder backed by an explicit text → vector dictionary.</summary>
    internal static FakeEmbedder FromDictionary(
        Dictionary<string, float[]> map,
        int dimensions = 3,
        string modelName = "fake-model") =>
        new(text => map[text], dimensions, modelName);

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(_embed(text));
}
