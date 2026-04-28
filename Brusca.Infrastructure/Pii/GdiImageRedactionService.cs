using System.Runtime.Versioning;
using System.Security.Cryptography;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Produces a sanitized copy of an image where every supplied PII region
/// is occluded with a solid black rectangle. The original image on the
/// source path is left untouched; only the sanitized copy is moved to
/// the execution target.
///
/// Implementation uses GDI+ via <c>System.Drawing.Common</c>, which is a
/// Windows-supported API. Brusca runs on Windows hosts that mount the
/// NAS share, so this is acceptable; cross-platform deployments should
/// substitute an ImageSharp-based implementation behind the same contract.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GdiImageRedactionService : IImageRedactionService
{
    private static readonly HashSet<string> _supported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff"
        };

    /// <inheritdoc />
    public bool CanRedact(string extension) =>
        !string.IsNullOrEmpty(extension) && _supported.Contains(extension);

    /// <inheritdoc />
    public async Task<Result<ImageRedactionResult>> RedactAsync(
        string sourceImagePath,
        string targetImagePath,
        IReadOnlyList<ImageRedactionRegion> regions,
        CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(sourceImagePath))
                return Result.Fail($"Source image not found: {sourceImagePath}");

            var dir = Path.GetDirectoryName(targetImagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var occluded = WriteSanitizedCopy(sourceImagePath, targetImagePath, regions);
            ct.ThrowIfCancellationRequested();

            var hash = await ComputeSha256Async(targetImagePath, ct);
            return Result.Ok(new ImageRedactionResult
            {
                SanitizedImagePath = targetImagePath,
                RegionsOccluded    = occluded,
                SanitizedContentHash = hash
            });
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    private static int WriteSanitizedCopy(
        string sourceImagePath,
        string targetImagePath,
        IReadOnlyList<ImageRedactionRegion> regions)
    {
        using var src = System.Drawing.Image.FromFile(sourceImagePath);
        using var bmp = new System.Drawing.Bitmap(src.Width, src.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.DrawImage(src, 0, 0, src.Width, src.Height);
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
            foreach (var r in regions)
            {
                if (r.Width <= 0 || r.Height <= 0) continue;
                g.FillRectangle(brush, r.X, r.Y, r.Width, r.Height);
            }
        }
        bmp.Save(targetImagePath);
        return regions.Count(r => r.Width > 0 && r.Height > 0);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
