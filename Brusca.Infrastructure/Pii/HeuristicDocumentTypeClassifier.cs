using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using FluentResults;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Heuristic document-type classifier. Maps file extensions and a small set of
/// keyword cues found in the REDACTED content to a <see cref="DocumentType"/>.
///
/// This classifier never sees PII — by the time it runs, redaction has already
/// replaced PII spans with tokens.
/// </summary>
public sealed class HeuristicDocumentTypeClassifier : IDocumentTypeClassifier
{
    private static readonly Dictionary<string, DocumentType> ExtensionMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"]  = DocumentType.Photo,    [".jpeg"] = DocumentType.Photo,
        [".png"]  = DocumentType.Image,    [".gif"]  = DocumentType.Image,
        [".bmp"]  = DocumentType.Image,    [".tiff"] = DocumentType.Image,
        [".webp"] = DocumentType.Image,    [".heic"] = DocumentType.Photo,

        [".mp3"]  = DocumentType.Audio,    [".wav"]  = DocumentType.Audio,
        [".flac"] = DocumentType.Audio,    [".m4a"]  = DocumentType.Audio,

        [".mp4"]  = DocumentType.Video,    [".mov"]  = DocumentType.Video,
        [".mkv"]  = DocumentType.Video,    [".avi"]  = DocumentType.Video,
        [".webm"] = DocumentType.Video,

        [".xls"]  = DocumentType.Spreadsheet, [".xlsx"] = DocumentType.Spreadsheet,
        [".csv"]  = DocumentType.Spreadsheet, [".ods"]  = DocumentType.Spreadsheet,

        [".ppt"]  = DocumentType.Presentation, [".pptx"] = DocumentType.Presentation,
        [".key"]  = DocumentType.Presentation, [".odp"]  = DocumentType.Presentation,

        [".cs"]   = DocumentType.SourceCode, [".ts"]   = DocumentType.SourceCode,
        [".js"]   = DocumentType.SourceCode, [".py"]   = DocumentType.SourceCode,
        [".go"]   = DocumentType.SourceCode, [".rs"]   = DocumentType.SourceCode,
        [".java"] = DocumentType.SourceCode, [".cpp"]  = DocumentType.SourceCode,
        [".c"]    = DocumentType.SourceCode, [".h"]    = DocumentType.SourceCode,
        [".rb"]   = DocumentType.SourceCode, [".php"]  = DocumentType.SourceCode,
        [".sql"]  = DocumentType.SourceCode,

        [".zip"]  = DocumentType.Archive, [".7z"]  = DocumentType.Archive,
        [".tar"]  = DocumentType.Archive, [".gz"]  = DocumentType.Archive,
        [".rar"]  = DocumentType.Archive,

        [".json"] = DocumentType.Configuration, [".yaml"] = DocumentType.Configuration,
        [".yml"]  = DocumentType.Configuration, [".xml"]  = DocumentType.Configuration,
        [".ini"]  = DocumentType.Configuration, [".toml"] = DocumentType.Configuration,
        [".conf"] = DocumentType.Configuration,

        [".log"]  = DocumentType.Log,
        [".txt"]  = DocumentType.PlainText, [".md"]   = DocumentType.PlainText,
    };

    /// <summary>Keyword cues — applied AFTER extension mapping if still Unknown.</summary>
    private static readonly (DocumentType Type, string[] Keywords)[] KeywordRules =
    [
        (DocumentType.Invoice,            ["invoice", "invoice number", "amount due", "billed to", "bill to"]),
        (DocumentType.Receipt,            ["receipt", "thank you for your purchase", "subtotal", "transaction id"]),
        (DocumentType.Contract,           ["agreement", "this contract", "hereinafter", "party of the first part"]),
        (DocumentType.Resume,             ["curriculum vitae", "resume", "work experience", "professional summary"]),
        (DocumentType.MedicalRecord,      ["patient", "diagnosis", "prescription", "medical record"]),
        (DocumentType.FinancialStatement, ["balance sheet", "income statement", "assets", "liabilities", "equity"]),
        (DocumentType.LegalDocument,      ["plaintiff", "defendant", "court", "case no", "jurisdiction"]),
        (DocumentType.TaxDocument,        ["form 1040", "w-2", "1099", "tax year", "irs"]),
        (DocumentType.Form,               ["please fill", "applicant signature", "form id", "submission"]),
        (DocumentType.Identification,     ["passport", "driver license", "id card", "date of birth"]),
        (DocumentType.Report,             ["executive summary", "findings", "conclusion", "report"]),
        (DocumentType.Correspondence,     ["dear ", "sincerely", "regards,", "to whom it may concern"]),
    ];

    public Task<Result<DocumentType>> ClassifyAsync(
        string redactedContent, string extension, CancellationToken ct = default)
    {
        var ext = (extension ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrEmpty(ext) && ExtensionMap.TryGetValue(ext, out var fromExt)
            && fromExt is not DocumentType.PlainText
                       and not DocumentType.Configuration
                       and not DocumentType.Log)
        {
            return Task.FromResult(Result.Ok(fromExt));
        }

        var content = (redactedContent ?? string.Empty).ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(content))
        {
            foreach (var (type, keywords) in KeywordRules)
            {
                foreach (var kw in keywords)
                {
                    if (content.Contains(kw, StringComparison.Ordinal))
                        return Task.FromResult(Result.Ok(type));
                }
            }
        }

        return Task.FromResult(Result.Ok(
            ExtensionMap.TryGetValue(ext, out var fallback) ? fallback : DocumentType.Unknown));
    }
}
