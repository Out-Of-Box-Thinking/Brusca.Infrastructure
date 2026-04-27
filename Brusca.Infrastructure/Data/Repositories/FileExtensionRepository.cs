using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Extensions;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Stored procedures:
///   fileext.usp_FileExtension_GetAll
///   fileext.usp_FileExtension_GetByExtension
///   fileext.usp_FileExtension_Upsert
///   fileext.usp_FileExtension_UpdateStatus
/// </summary>
public sealed class FileExtensionRepository : DapperRepositoryBase, IFileExtensionRepository
{
    public FileExtensionRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<IReadOnlyList<FileExtensionRecord>>> GetAllAsync(CancellationToken ct = default)
    {
        try
        {
            var results = await QueryAsync<FileExtensionRecord>("fileext.usp_FileExtension_GetAll", ct: ct);
            return r.Ok<IReadOnlyList<FileExtensionRecord>>(results.ToList());
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result<FileExtensionRecord?>> GetByExtensionAsync(string extension, CancellationToken ct = default)
    {
        try
        {
            var result = await QuerySingleOrDefaultAsync<FileExtensionRecord>(
                "fileext.usp_FileExtension_GetByExtension",
                new { Extension = extension.ToLowerInvariant() }, ct);
            return Result.Ok(result);
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result> UpsertAsync(FileExtensionRecord record, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("fileext.usp_FileExtension_Upsert", new
            {
                record.Id,
                Extension = record.Extension.ToLowerInvariant(),
                Status = (int)record.Status,
                record.Description,
                record.ReaderNuGetPackage,
                record.ReaderImplementationType,
                record.FirstSeenUtc,
                LastSeenUtc = DateTime.UtcNow
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result> BulkUpsertAsync(IEnumerable<FileExtensionRecord> records, CancellationToken ct = default)
    {
        // Bulk via table-valued parameter; SP: fileext.usp_FileExtension_BulkUpsert
        try
        {
            foreach (var record in records)
                await UpsertAsync(record, ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result> UpdateStatusAsync(
        string extension, FileExtensionStatus status, string? nuGetPackage, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("fileext.usp_FileExtension_UpdateStatus", new
            {
                Extension = extension.ToLowerInvariant(),
                Status = (int)status,
                NuGetPackage = nuGetPackage
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }
}
