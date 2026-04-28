using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Records the BEFORE and AFTER state of every file/folder operation
/// performed during structure execution.
/// </summary>
public sealed class FileRelocationRepository : DapperRepositoryBase, IFileRelocationRepository
{
    public FileRelocationRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<FileRelocationRecord>> CreateAsync(
        FileRelocationRecord record, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_FileRelocation_Insert", new
            {
                record.Id,
                record.CleaningId,
                record.RedactedFileId,
                OperationType   = (int)record.OperationType,
                ExecutionTarget = (int)record.ExecutionTarget,
                record.BeforePath, record.BeforeName,
                record.AfterPath,  record.AfterName,
                Status          = (int)record.Status,
                record.ErrorMessage,
                record.CreatedAtUtc,
                record.CompletedAtUtc,
                record.ContentHashAfter
            }, ct);
            return Result.Ok(record);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> BulkCreateAsync(
        IEnumerable<FileRelocationRecord> records, CancellationToken ct = default)
    {
        try
        {
            foreach (var r in records)
            {
                var res = await CreateAsync(r, ct);
                if (res.IsFailed) return res.ToResult();
            }
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> UpdateStatusAsync(
        Guid id, RelocationStatus status, string? error, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_FileRelocation_UpdateStatus", new
            {
                Id = id,
                Status = (int)status,
                ErrorMessage = error,
                CompletedAtUtc = DateTime.UtcNow
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<FileRelocationRecord>>> GetByCleaningIdAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<FileRelocationRecord>(
                "cleaning.usp_FileRelocation_GetByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<FileRelocationRecord>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
