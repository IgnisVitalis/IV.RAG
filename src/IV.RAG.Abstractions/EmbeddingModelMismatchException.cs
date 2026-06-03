namespace IV.RAG;

/// <summary>
/// Thrown when the embedding model stored in the vector table differs from the currently configured model.
/// Call <see cref="IEmbeddingMigrator.MigrateAsync"/> to re-embed all outdated chunks.
/// </summary>
public sealed class EmbeddingModelMismatchException : InvalidOperationException
{
    /// <summary>Model info stored in the vector table, or <c>null</c> if the origin is unknown (pre-tracking data).</summary>
    public EmbedderInfo? StoredModel { get; }

    /// <summary>Model info of the currently configured embedder.</summary>
    public EmbedderInfo CurrentModel { get; }

    /// <summary>Name of the vector table where the mismatch was detected.</summary>
    public string TableName { get; }

    /// <summary>Initializes a new instance with stored and current model info.</summary>
    public EmbeddingModelMismatchException(EmbedderInfo? storedModel, EmbedderInfo currentModel, string tableName)
        : base(BuildMessage(storedModel, currentModel, tableName))
    {
        StoredModel = storedModel;
        CurrentModel = currentModel;
        TableName = tableName;
    }

    private static string BuildMessage(EmbedderInfo? stored, EmbedderInfo current, string tableName)
    {
        var storedDesc = stored is null
            ? "an unknown model (pre-tracking data)"
            : $"{stored.Provider}/{stored.ModelName} ({stored.Dimensions}d)";
        return $"Vector table '{tableName}' contains chunks embedded with {storedDesc}, " +
               $"but the current embedder is {current.Provider}/{current.ModelName} ({current.Dimensions}d). " +
               $"Call IEmbeddingMigrator.MigrateAsync() to re-embed all outdated chunks.";
    }
}
