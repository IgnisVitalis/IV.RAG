namespace IV.RAG;

/// <summary>Configuration for <see cref="RemoteRetrievalPipeline"/>.</summary>
public sealed class RemoteOptions
{
    /// <summary>Base URL of the retrieval server. Defaults to <c>http://localhost:5000</c>.</summary>
    public string Endpoint { get; set; } = "http://localhost:5000";

    /// <summary>Path of the query endpoint. Defaults to <c>/api/query</c>.</summary>
    public string QueryPath { get; set; } = "/api/query";
}
