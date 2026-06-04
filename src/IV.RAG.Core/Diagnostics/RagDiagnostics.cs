using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace IV.RAG;

/// <summary>
/// Diagnostics primitives for the RAG pipeline. Exposes an <see cref="System.Diagnostics.ActivitySource"/>
/// and a <see cref="System.Diagnostics.Metrics.Meter"/>, both named <see cref="Name"/>. Subscribe with
/// OpenTelemetry via <c>.AddSource("IV.RAG")</c> (tracing) and <c>.AddMeter("IV.RAG")</c> (metrics).
/// </summary>
public static class RagDiagnostics
{
    /// <summary>The instrumentation name shared by the activity source and the meter.</summary>
    public const string Name = "IV.RAG";

    /// <summary>Activity source for pipeline spans (ingest, retrieve, answer, embed, generate).</summary>
    public static readonly ActivitySource ActivitySource = new(Name);

    /// <summary>Meter for pipeline metrics.</summary>
    public static readonly Meter Meter = new(Name);

    internal static readonly Counter<long> ChunksIngested =
        Meter.CreateCounter<long>("rag.chunks_ingested", "{chunk}", "Chunks stored during ingestion.");

    internal static readonly Histogram<double> RetrievalDuration =
        Meter.CreateHistogram<double>("rag.retrieval.duration", "ms", "Retrieval latency in milliseconds.");

    internal static readonly Counter<long> EmbedCalls =
        Meter.CreateCounter<long>("rag.embed_calls", "{call}", "Embedding operations performed.");

    internal static readonly Counter<long> CacheHits =
        Meter.CreateCounter<long>("rag.cache.hits", "{hit}", "Semantic query cache hits.");

    internal static readonly Counter<long> CacheMisses =
        Meter.CreateCounter<long>("rag.cache.misses", "{miss}", "Semantic query cache misses.");

    // Wraps a retrieval call in an "rag.retrieve" span and records its latency to the histogram.
    internal static async Task<IReadOnlyList<SearchResult>> MeasureRetrievalAsync(
        Func<Task<IReadOnlyList<SearchResult>>> retrieve)
    {
        using var activity = ActivitySource.StartActivity("rag.retrieve");
        var start = Stopwatch.GetTimestamp();
        var results = await retrieve();
        RetrievalDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        activity?.SetTag("rag.result_count", results.Count);
        return results;
    }
}
