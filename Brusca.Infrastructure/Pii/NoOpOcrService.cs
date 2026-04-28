using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.Pii;
using FluentResults;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Default <see cref="IOcrService"/> registered when no OCR engine is wired
/// in. Returns empty results so the pipeline degrades gracefully — non-image
/// content is unaffected, and image content materializes without
/// redaction-region hints (the image still copies, just unsanitized text).
///
/// Hosts wanting real OCR override this binding with a Tesseract-backed
/// implementation that fills <see cref="OcrTextResult.Words"/>.
/// </summary>
public sealed class NoOpOcrService : IOcrService
{
    /// <inheritdoc />
    public bool CanRead(string extension) => false;

    /// <inheritdoc />
    public Task<Result<string>> ExtractTextAsync(
        string filePath, CancellationToken ct = default)
        => Task.FromResult(Result.Ok(string.Empty));

    /// <inheritdoc />
    public Task<Result<OcrTextResult>> ExtractTextWithRegionsAsync(
        string filePath, CancellationToken ct = default)
        => Task.FromResult(Result.Ok(new OcrTextResult
        {
            Text = string.Empty,
            Words = []
        }));
}
