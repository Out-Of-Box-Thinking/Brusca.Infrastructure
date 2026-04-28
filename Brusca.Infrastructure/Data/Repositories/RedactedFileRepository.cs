using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Pii;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Persists per-file redaction descriptors. The <c>EncryptedPiiJson</c> column
/// is sealed by the calling service before reaching this repository.
/// </summary>
public sealed class RedactedFileRepository : DapperRepositoryBase, IRedactedFileRepository
{
    public RedactedFileRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<RedactedFileDescriptor>> CreateAsync(
        RedactedFileDescriptor descriptor, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_RedactedFile_Insert", new
            {
                descriptor.Id,
                descriptor.CleaningId,
                descriptor.OriginalFilePath,
                descriptor.OriginalFileName,
                descriptor.Extension,
                DocumentType = (int)descriptor.DocumentType,
                descriptor.RedactedContent,
                descriptor.EncryptedPiiJson,
                descriptor.PiiSegmentCount,
                descriptor.ContentHash,
                descriptor.DiscoveredAtUtc
            }, ct);
            return Result.Ok(descriptor);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> BulkCreateAsync(
        IEnumerable<RedactedFileDescriptor> descriptors, CancellationToken ct = default)
    {
        try
        {
            foreach (var d in descriptors)
            {
                var r = await CreateAsync(d, ct);
                if (r.IsFailed) return r.ToResult();
            }
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<RedactedFileDescriptor>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var row = await QuerySingleOrDefaultAsync<RedactedFileDescriptor>(
                "cleaning.usp_RedactedFile_GetById", new { Id = id }, ct);
            return row is not null
                ? Result.Ok(row)
                : Result.Fail($"RedactedFile {id} not found.");
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<RedactedFileDescriptor>>> GetByCleaningIdAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<RedactedFileDescriptor>(
                "cleaning.usp_RedactedFile_GetByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<RedactedFileDescriptor>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<DocumentTypeSummary>>> GetDocumentTypeSummariesAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<DocumentTypeSummary>(
                "cleaning.usp_RedactedFile_GetDocumentTypeSummaries",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<DocumentTypeSummary>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> DeleteByCleaningIdAsync(Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_RedactedFile_DeleteByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
