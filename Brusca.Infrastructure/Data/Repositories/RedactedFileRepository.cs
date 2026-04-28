using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using Dapper;
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
                descriptor.ImageRedactionRegionsJson,
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

    public async Task<Result<PiiSlotCatalog>> GetSlotCatalogAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            // SP returns rows of (DocumentType:int, PiiKind:int)
            var rows = await QueryAsync<SlotCatalogRow>(
                "cleaning.usp_RedactedFile_GetSlotCatalog",
                new { CleaningId = cleaningId }, ct);

            var entries = rows
                .GroupBy(r => (DocumentType)r.DocumentType)
                .Select(g => new DocumentTypeSlotEntry
                {
                    DocumentType   = g.Key,
                    AvailableKinds = g.Select(x => (PiiKind)x.PiiKind).Distinct().ToList()
                })
                .ToList();

            return Result.Ok(new PiiSlotCatalog
            {
                CleaningId = cleaningId,
                Entries    = entries
            });
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<DuplicateGroup>>> GetDuplicateGroupsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            // Two result sets:
            //   1) (ContentHash, KeepRedactedFileId)
            //   2) (ContentHash, RedactedFileId)
            await using var conn = OpenConnection();
            using var multi = await conn.QueryMultipleAsync(new global::Dapper.CommandDefinition(
                "cleaning.usp_RedactedFile_GetDuplicateGroups",
                new { CleaningId = cleaningId },
                commandType: System.Data.CommandType.StoredProcedure,
                cancellationToken: ct));

            var keepers = (await multi.ReadAsync<DuplicateKeeperRow>())
                .ToDictionary(k => k.ContentHash, k => k.KeepRedactedFileId, StringComparer.OrdinalIgnoreCase);

            var members = (await multi.ReadAsync<DuplicateMemberRow>())
                .GroupBy(m => m.ContentHash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.RedactedFileId).ToList());

            var groups = keepers.Select(k => new DuplicateGroup
            {
                ContentHash        = k.Key,
                KeepRedactedFileId = k.Value,
                RedactedFileIds    = members.TryGetValue(k.Key, out var ids) ? ids : []
            }).ToList();

            return Result.Ok<IReadOnlyList<DuplicateGroup>>(groups);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> SaveDetectedPiiKindsAsync(
        Guid redactedFileId, IEnumerable<PiiKind> kinds, CancellationToken ct = default)
    {
        try
        {
            foreach (var k in kinds.Distinct())
            {
                await ExecuteAsync("cleaning.usp_RedactedFilePiiKind_Insert", new
                {
                    RedactedFileId = redactedFileId,
                    PiiKind = (int)k,
                    Count = 1
                }, ct);
            }
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    private sealed class SlotCatalogRow
    {
        public int DocumentType { get; set; }
        public int PiiKind { get; set; }
    }

    private sealed class DuplicateKeeperRow
    {
        public string ContentHash { get; set; } = string.Empty;
        public Guid KeepRedactedFileId { get; set; }
    }

    private sealed class DuplicateMemberRow
    {
        public string ContentHash { get; set; } = string.Empty;
        public Guid RedactedFileId { get; set; }
    }
}
