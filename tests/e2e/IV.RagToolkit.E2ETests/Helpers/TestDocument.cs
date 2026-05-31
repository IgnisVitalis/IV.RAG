using System.Diagnostics.CodeAnalysis;

namespace IV.RagToolkit.E2ETests.Helpers;

internal sealed record TestDocument : Document
{
    private static readonly Guid TestSourceId = new("a0000000-0000-0000-0000-000000000001");

    public override Origin Source { get; }

    [SetsRequiredMembers]
    public TestDocument(
        string text,
        string documentId = "doc-1",
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        Text = text;
        Metadata = metadata;
        Source = new Origin(TestSourceId, "Test", documentId);
    }
}
