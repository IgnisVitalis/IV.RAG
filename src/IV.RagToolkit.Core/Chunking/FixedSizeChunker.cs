using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;

namespace IV.RagToolkit;

/// <summary>
/// Splits a <see cref="Document"/> into fixed-size character chunks with optional overlap.
/// </summary>
public sealed class FixedSizeChunker : IChunker
{
    private readonly FixedSizeChunkerOptions _options;

    /// <summary>Initializes a new instance with the provided options.</summary>
    public FixedSizeChunker(IOptions<FixedSizeChunkerOptions> options)
    {
        var value = options.Value;
        if (value.Overlap >= value.ChunkSize)
            throw new InvalidOperationException($"{nameof(FixedSizeChunkerOptions.Overlap)} must be less than {nameof(FixedSizeChunkerOptions.ChunkSize)}.");
        _options = value;
    }

#pragma warning disable CS1998 // synchronous chunker implementing async interface
    /// <inheritdoc/>
    public async IAsyncEnumerable<Chunk> ChunkAsync(
        Document document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = document.Text;
        var chunkSize = _options.ChunkSize;
        var step = chunkSize - _options.Overlap;
        var position = 0;

        while (position < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(chunkSize, text.Length - position);
            yield return new Chunk
            {
                Text = text.Substring(position, length),
                Metadata = document.Metadata,
                Origin = document.Source
            };

            position += step;
        }
    }
#pragma warning restore CS1998
}
