using System.Runtime.Versioning;
using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Hash-gated, recycle-bin-based finalisation step. Runs only on Windows
/// because <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/> requires the
/// host to be running on Windows.
///
/// For every successfully-materialized relocation, verifies the post-move
/// hash matches the original then sends the original to the recycle bin.
/// Originals remain untouched on hash mismatch — the operator can still
/// recover them by hand because nothing has been deleted.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PromotionService : IPromotionService
{
    private readonly IFileRelocationRepository _relocRepo;
    private readonly IRedactedFileRepository _redactedRepo;
    private readonly IPromotionRepository _promoRepo;
    private readonly IAuditLogger _audit;
    private readonly IErrorLogger _log;

    public PromotionService(
        IFileRelocationRepository relocRepo,
        IRedactedFileRepository redactedRepo,
        IPromotionRepository promoRepo,
        IAuditLogger audit,
        IErrorLogger log)
    {
        _relocRepo    = relocRepo;
        _redactedRepo = redactedRepo;
        _promoRepo    = promoRepo;
        _audit        = audit;
        _log          = log;
    }

    public async Task<Result<IReadOnlyList<PromotionRecord>>> PromoteAsync(
        Guid cleaningId, string userId, CancellationToken ct = default)
    {
        var relocsResult = await _relocRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (relocsResult.IsFailed) return Result.Fail(relocsResult.Errors);

        var filesResult = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (filesResult.IsFailed) return Result.Fail(filesResult.Errors);

        var filesById = filesResult.Value.ToDictionary(f => f.Id);
        var records   = new List<PromotionRecord>();

        foreach (var r in relocsResult.Value
            .Where(x => x.Status == RelocationStatus.Succeeded
                     && x.OperationType == RelocationOperationType.Materialize
                     && x.RedactedFileId is not null))
        {
            ct.ThrowIfCancellationRequested();
            if (!filesById.TryGetValue(r.RedactedFileId!.Value, out var orig)) continue;

            var rec = new PromotionRecord
            {
                CleaningId       = cleaningId,
                FileRelocationId = r.Id,
                OriginalPath     = orig.OriginalFilePath,
                Status           = PromotionStatus.Pending
            };
            await _promoRepo.CreateAsync(rec, ct);

            // Hash gate: refuse to promote if before/after digests disagree.
            if (string.IsNullOrEmpty(orig.ContentHash)
                || string.IsNullOrEmpty(r.ContentHashAfter)
                || !string.Equals(orig.ContentHash, r.ContentHashAfter, StringComparison.OrdinalIgnoreCase))
            {
                rec.Status       = PromotionStatus.Failed;
                rec.ErrorMessage = "Hash mismatch — refusing to promote.";
                await _promoRepo.UpdateStatusAsync(
                    rec.Id, rec.Status, rec.ErrorMessage, null, null, ct);
                records.Add(rec);
                continue;
            }

            rec.VerifiedAtUtc = DateTime.UtcNow;
            rec.Status        = PromotionStatus.Verified;

            try
            {
                if (File.Exists(orig.OriginalFilePath))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        orig.OriginalFilePath,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                rec.PromotedAtUtc = DateTime.UtcNow;
                rec.Status        = PromotionStatus.Promoted;
            }
            catch (Exception ex)
            {
                rec.Status       = PromotionStatus.Failed;
                rec.ErrorMessage = ex.Message;
                await _log.LogErrorAsync(
                    $"Promotion failed for {orig.OriginalFilePath}", ex, cleaningId: cleaningId);
            }

            await _promoRepo.UpdateStatusAsync(
                rec.Id, rec.Status, rec.ErrorMessage, rec.VerifiedAtUtc, rec.PromotedAtUtc, ct);
            records.Add(rec);
        }

        await _audit.LogAsync("CleaningPromoted", "Cleaning",
            cleaningId.ToString(), userId: userId, action: "Promote",
            newValues: new
            {
                Total = records.Count,
                Promoted = records.Count(x => x.Status == PromotionStatus.Promoted),
                Failed   = records.Count(x => x.Status == PromotionStatus.Failed)
            });

        return Result.Ok<IReadOnlyList<PromotionRecord>>(records);
    }

    public Task<Result<IReadOnlyList<PromotionRecord>>> GetPromotionsAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _promoRepo.GetByCleaningIdAsync(cleaningId, ct);
}
