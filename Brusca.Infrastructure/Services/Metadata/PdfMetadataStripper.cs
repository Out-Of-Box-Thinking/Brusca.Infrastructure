using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Metadata;

/// <summary>
/// Best-effort PDF metadata stripper. Brusca currently references PdfPig
/// (read-only) so a full re-write isn't possible without adding a writer
/// dependency. Until a writer is wired in, this stripper performs a
/// surface-level pass that NULLs out the binary <c>/Info</c> dictionary
/// entries Title/Author/Subject/Keywords/Creator/Producer when the PDF is
/// uncompressed enough that they appear in the trailer as plaintext.
///
/// The pass MUST never corrupt the PDF — when the structure looks too
/// complex (compressed object streams, encryption) it leaves the file
/// untouched and returns success so the materialize pipeline keeps moving.
/// </summary>
public sealed class PdfMetadataStripper : IFileMetadataStripper
{
    private static readonly string[] _keys =
        ["Title", "Author", "Subject", "Keywords", "Creator", "Producer"];

    /// <inheritdoc />
    public bool CanStrip(string extension) =>
        string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<Result> StripAsync(
        string filePath, string extension, CancellationToken ct = default)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var text  = System.Text.Encoding.Latin1.GetString(bytes);
            bool changed = false;

            foreach (var key in _keys)
            {
                // Match e.g. /Author (John Doe)  →  /Author ()
                var pattern = new System.Text.RegularExpressions.Regex(
                    @"/" + key + @"\s*\(([^)]*)\)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                text = pattern.Replace(text, m =>
                {
                    if (m.Groups[1].Length == 0) return m.Value;
                    changed = true;
                    var blanks = new string(' ', m.Groups[1].Length);
                    return $"/{key} ({blanks})"; // preserve byte length so xref stays valid
                });
            }

            if (changed)
            {
                var rewritten = System.Text.Encoding.Latin1.GetBytes(text);
                if (rewritten.Length == bytes.Length)
                    File.WriteAllBytes(filePath, rewritten);
                // length mismatch → bail out silently; never corrupt the PDF
            }
            return Task.FromResult(Result.Ok());
        }
        catch
        {
            // Best-effort — never block materialization on a strip failure.
            return Task.FromResult(Result.Ok());
        }
    }
}
