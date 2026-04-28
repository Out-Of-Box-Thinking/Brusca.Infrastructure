using System.Security.Cryptography;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// SHA-256 file hashing used by the relocation pipeline to verify that
/// content survives a move/copy across NAS shares unchanged.
///
/// All I/O is local — the file is streamed from disk and hashed in
/// fixed-size buffers, so very large files do not pressure the LOH.
/// </summary>
public sealed class Sha256FileHashService : IFileHashService
{
    private const int BufferSize = 81920;

    /// <inheritdoc />
    public string AlgorithmName => "SHA-256";

    /// <inheritdoc />
    public async Task<Result<string>> ComputeAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(filePath))
                return Result.Fail($"File not found: {filePath}");

            await using var fs = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, useAsync: true);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Result.Ok(Convert.ToHexString(hash).ToLowerInvariant());
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    /// <inheritdoc />
    public async Task<Result<bool>> EqualsAsync(
        string leftPath, string rightPath, CancellationToken ct = default)
    {
        var left = await ComputeAsync(leftPath, ct);
        if (left.IsFailed) return Result.Fail(left.Errors);
        var right = await ComputeAsync(rightPath, ct);
        if (right.IsFailed) return Result.Fail(right.Errors);
        return Result.Ok(string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase));
    }
}
