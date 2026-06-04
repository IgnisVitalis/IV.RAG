namespace IV.RAG;

/// <summary>
/// Decorator that always merges a required <see cref="MetadataFilter"/> (resolved per query, e.g. from
/// the current tenant) into every <see cref="RetrievalOptions"/>, AND-combined with the caller's own
/// filter. The access-control guard: it prevents a leak when the application forgets to pass a
/// tenant/permission filter. Registered via <c>AddMandatoryRetrievalFilter()</c>.
/// </summary>
/// <remarks>
/// Must wrap the <em>outermost</em> pipeline (outside any cache) so the required filter is part of the
/// cache key — otherwise cached results could be served across scopes.
/// </remarks>
internal sealed class GuardedRetrievalPipeline : IRetrievalPipeline
{
    private readonly IRetrievalPipeline _inner;
    private readonly Func<MetadataFilter?> _requiredFilter;

    public GuardedRetrievalPipeline(IRetrievalPipeline inner, Func<MetadataFilter?> requiredFilter)
    {
        _inner = inner;
        _requiredFilter = requiredFilter;
    }

    public Task<IReadOnlyList<SearchResult>> QueryAsync(
        string query,
        RetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var required = _requiredFilter();
        if (required is not null)
        {
            var opts = options ?? new RetrievalOptions();
            var combined = opts.MetadataFilter is null ? required : MetadataFilter.And(required, opts.MetadataFilter);
            options = opts with { MetadataFilter = combined };
        }

        return _inner.QueryAsync(query, options, cancellationToken);
    }
}
