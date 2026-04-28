using Brusca.Core.Contracts.Services;
using DocumentFormat.OpenXml.Packaging;
using FluentResults;

namespace Brusca.Infrastructure.Services.Metadata;

/// <summary>
/// Strips OpenXml core/extended/custom file properties from <c>.docx</c>,
/// <c>.xlsx</c>, and <c>.pptx</c> packages. Operates in-place on the
/// materialized copy — originals are never opened.
/// </summary>
public sealed class OpenXmlMetadataStripper : IFileMetadataStripper
{
    private static readonly HashSet<string> _supported =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".docm", ".dotx", ".dotm",
            ".xlsx", ".xlsm", ".xltx", ".xltm",
            ".pptx", ".pptm", ".potx", ".potm"
        };

    /// <inheritdoc />
    public bool CanStrip(string extension) => _supported.Contains(extension);

    /// <inheritdoc />
    public Task<Result> StripAsync(
        string filePath, string extension, CancellationToken ct = default)
    {
        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".docx": case ".docm": case ".dotx": case ".dotm":
                {
                    using var pkg = WordprocessingDocument.Open(filePath, isEditable: true);
                    StripParts(pkg);
                    break;
                }
                case ".xlsx": case ".xlsm": case ".xltx": case ".xltm":
                {
                    using var pkg = SpreadsheetDocument.Open(filePath, isEditable: true);
                    StripParts(pkg);
                    break;
                }
                case ".pptx": case ".pptm": case ".potx": case ".potm":
                {
                    using var pkg = PresentationDocument.Open(filePath, isEditable: true);
                    StripParts(pkg);
                    break;
                }
            }

            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(new ExceptionalError(ex)));
        }
    }

    private static void StripParts(WordprocessingDocument pkg)
    {
        if (pkg.CoreFilePropertiesPart is not null) pkg.DeletePart(pkg.CoreFilePropertiesPart);
        if (pkg.ExtendedFilePropertiesPart is not null) pkg.DeletePart(pkg.ExtendedFilePropertiesPart);
        if (pkg.CustomFilePropertiesPart is not null) pkg.DeletePart(pkg.CustomFilePropertiesPart);
    }

    private static void StripParts(SpreadsheetDocument pkg)
    {
        if (pkg.CoreFilePropertiesPart is not null) pkg.DeletePart(pkg.CoreFilePropertiesPart);
        if (pkg.ExtendedFilePropertiesPart is not null) pkg.DeletePart(pkg.ExtendedFilePropertiesPart);
        if (pkg.CustomFilePropertiesPart is not null) pkg.DeletePart(pkg.CustomFilePropertiesPart);
    }

    private static void StripParts(PresentationDocument pkg)
    {
        if (pkg.CoreFilePropertiesPart is not null) pkg.DeletePart(pkg.CoreFilePropertiesPart);
        if (pkg.ExtendedFilePropertiesPart is not null) pkg.DeletePart(pkg.ExtendedFilePropertiesPart);
        if (pkg.CustomFilePropertiesPart is not null) pkg.DeletePart(pkg.CustomFilePropertiesPart);
    }
}
