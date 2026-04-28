using System.Runtime.Versioning;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Metadata;

/// <summary>
/// Strips EXIF/XMP metadata from common raster images by re-encoding the
/// pixel buffer through GDI+. The output has only the codec's own metadata
/// (i.e. nothing user-identifying).
///
/// Windows-only because <c>System.Drawing.Common</c> is a Windows-supported
/// API. Cross-platform deployments should substitute an ImageSharp-based
/// stripper behind the same contract.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ImageMetadataStripper : IFileMetadataStripper
{
    private static readonly HashSet<string> _supported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"
        };

    /// <inheritdoc />
    public bool CanStrip(string extension) => _supported.Contains(extension);

    /// <inheritdoc />
    public Task<Result> StripAsync(
        string filePath, string extension, CancellationToken ct = default)
    {
        try
        {
            // Re-encode pixel data into a fresh Bitmap; the resulting file
            // carries no EXIF/XMP from the source.
            byte[] sanitized;
            using (var src = System.Drawing.Image.FromFile(filePath))
            using (var bmp = new System.Drawing.Bitmap(src.Width, src.Height))
            using (var g   = System.Drawing.Graphics.FromImage(bmp))
            using (var ms  = new MemoryStream())
            {
                g.DrawImage(src, 0, 0, src.Width, src.Height);
                bmp.Save(ms, src.RawFormat);
                sanitized = ms.ToArray();
            }
            File.WriteAllBytes(filePath, sanitized);
            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(new ExceptionalError(ex)));
        }
    }
}
