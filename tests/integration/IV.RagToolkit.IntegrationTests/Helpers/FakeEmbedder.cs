namespace IV.RagToolkit.IntegrationTests.Helpers;

/// <summary>
/// Deterministic embedder for integration tests.
/// Uses known vectors so similarity relationships are predictable.
/// </summary>
internal sealed class FakeEmbedder : IEmbedder
{
    private readonly Func<string, float[]> _embed;

    internal FakeEmbedder(Func<string, float[]> embed) => _embed = embed;

    /// <summary>Creates an embedder backed by an explicit text → vector dictionary.</summary>
    internal static FakeEmbedder FromDictionary(Dictionary<string, float[]> map) =>
        new(text => map[text]);

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(_embed(text));
}
