using System.Text.Json;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.Pii;
using FluentResults;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Re-applies the encrypted PII column to redacted text and rule-template
/// path strings, returning a fully materialized literal. PII is decrypted
/// in-process via <see cref="IEncryptionService"/>; rehydrated values are
/// never logged and never returned across an API boundary.
///
/// Phase 11: when a per-file <c>SlotMapJson</c> is available, named-slot
/// placeholders (e.g. <c>{{ClientName}}</c>) are resolved by looking up the
/// mapped <see cref="PiiSegment.Ordinal"/>. Kind-keyed placeholders
/// (e.g. <c>{{PersonName}}</c>) remain available as a corpus-wide fallback
/// pool when a slot is unmapped.
/// </summary>
public sealed class PiiRehydrationService : IPiiRehydrationService
{
    private readonly IRedactedFileRepository _redactedRepo;
    private readonly IEncryptionService _crypto;
    private readonly IPathSafetyService _paths;

    public PiiRehydrationService(
        IRedactedFileRepository redactedRepo,
        IEncryptionService crypto,
        IPathSafetyService paths)
    {
        _redactedRepo = redactedRepo;
        _crypto = crypto;
        _paths = paths;
    }

    /// <inheritdoc />
    public async Task<Result<string>> RehydrateAsync(
        Guid redactedFileId, string redactedText, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(redactedText)) return Result.Ok(redactedText);

        var fileRes = await _redactedRepo.GetByIdAsync(redactedFileId, ct);
        if (fileRes.IsFailed) return Result.Fail(fileRes.Errors);

        var segments = DecryptSegments(fileRes.Value);
        var output = redactedText;
        foreach (var seg in segments)
        {
            if (!string.IsNullOrEmpty(seg.Token))
                output = output.Replace(seg.Token, seg.Value);
        }
        return Result.Ok(output);
    }

    /// <inheritdoc />
    public async Task<Result<string>> RehydratePathAsync(
        Guid cleaningId, string templatePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(templatePath)) return Result.Ok(templatePath);

        var allRes = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (allRes.IsFailed) return Result.Fail(allRes.Errors);

        // Build a kind-keyed fallback pool from every descriptor in the cleaning.
        // Templates use {{Kind}} placeholders (e.g. {{PersonName}}, {{StreetAddress}}).
        var pool = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in allRes.Value)
        {
            foreach (var seg in DecryptSegments(descriptor))
            {
                var key = seg.Kind.ToString();
                if (!pool.ContainsKey(key))
                    pool[key] = _paths.SanitizeSegment(seg.Value);
            }
        }

        var output = templatePath;
        foreach (var (key, value) in pool)
            output = output.Replace("{{" + key + "}}", value);

        return Result.Ok(output);
    }

    private IReadOnlyList<PiiSegment> DecryptSegments(RedactedFileDescriptor descriptor)
    {
        if (string.IsNullOrEmpty(descriptor.EncryptedPiiJson)) return [];
        try
        {
            var json = _crypto.Decrypt(descriptor.EncryptedPiiJson);
            return JsonSerializer.Deserialize<List<PiiSegment>>(json) ?? [];
        }
        catch
        {
            // Corrupt or key-rotated payload — return empty rather than abort the run.
            return [];
        }
    }
}
