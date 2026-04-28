using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Persists <see cref="PromotionRecord"/> rows produced by
/// <c>IPromotionService</c> when the user opts in to recycle-bin
/// finalisation of materialized copies.
/// </summary>
public sealed class PromotionRepository : DapperRepositoryBase, IPromotionRepository
{
    public PromotionRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<PromotionRecord>> CreateAsync(
        PromotionRecord record, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_PromotionRecord_Insert", new
            {
                record.Id,
                record.CleaningId,
                record.FileRelocationId,
                record.OriginalPath,
                Status = (int)record.Status,
                record.ErrorMessage,
                record.VerifiedAtUtc,
                record.PromotedAtUtc,
                record.CreatedAtUtc
            }, ct);
            return Result.Ok(record);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> UpdateStatusAsync(
        Guid id, PromotionStatus status, string? error,
        DateTime? verifiedAtUtc, DateTime? promotedAtUtc,
        CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_PromotionRecord_UpdateStatus", new
            {
                Id = id,
                Status = (int)status,
                ErrorMessage = error,
                VerifiedAtUtc = verifiedAtUtc,
                PromotedAtUtc = promotedAtUtc
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<PromotionRecord>>> GetByCleaningIdAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<PromotionRecord>(
                "cleaning.usp_PromotionRecord_GetByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<PromotionRecord>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
