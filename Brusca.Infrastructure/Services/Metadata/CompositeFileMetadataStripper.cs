using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Metadata;

/// <summary>
/// Routes <see cref="StripAsync"/> to the first registered child stripper
/// that <see cref="IFileMetadataStripper.CanStrip"/> the extension. When
/// nothing handles the extension, the call succeeds as a no-op so callers
/// can run the pass unconditionally.
/// </summary>
public sealed class CompositeFileMetadataStripper : IFileMetadataStripper
{
    private readonly IReadOnlyList<IFileMetadataStripper> _children;

    public CompositeFileMetadataStripper(IEnumerable<IFileMetadataStripper> children)
    {
        _children = children.Where(c => c is not CompositeFileMetadataStripper).ToList();
    }

    /// <inheritdoc />
    public bool CanStrip(string extension) =>
        _children.Any(c => c.CanStrip(extension));

    /// <inheritdoc />
    public async Task<Result> StripAsync(
        string filePath, string extension, CancellationToken ct = default)
    {
        foreach (var c in _children)
        {
            if (c.CanStrip(extension))
                return await c.StripAsync(filePath, extension, ct);
        }
        return Result.Ok(); // no handler — degrade gracefully
    }
}
