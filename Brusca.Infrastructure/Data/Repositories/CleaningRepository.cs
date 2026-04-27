using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// All stored procedures follow: cleaning.usp_Entity_Action
/// </summary>
public sealed class CleaningRepository : DapperRepositoryBase, ICleaningRepository
{
    public CleaningRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<Cleaning>> CreateAsync(Cleaning cleaning, CancellationToken ct = default)
    {
        try
        {
            var result = await QuerySingleOrDefaultAsync<Cleaning>(
                "cleaning.usp_Cleaning_Create", new
                {
                    cleaning.Id, cleaning.RootPath, cleaning.CreatedByUserId,
                    cleaning.Notes, cleaning.CreatedAtUtc
                }, ct);
            return result is not null ? Result.Ok(result) : Result.Fail("Create failed.");
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<Cleaning>> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var result = await QuerySingleOrDefaultAsync<Cleaning>(
                "cleaning.usp_Cleaning_GetById", new { Id = id }, ct);
            return result is not null ? Result.Ok(result) : Result.Fail($"Cleaning {id} not found.");
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<Cleaning>>> GetPagedAsync(int page, int pageSize, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<Cleaning>("cleaning.usp_Cleaning_GetPaged",
                new { Page = page, PageSize = pageSize }, ct);
            return Result.Ok<IReadOnlyList<Cleaning>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> UpdateStatusAsync(Guid id, CleaningStatus status, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_Cleaning_UpdateStatus",
                new { Id = id, Status = (int)status }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_Cleaning_Complete",
                new { Id = id, CompletedAtUtc = DateTime.UtcNow }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> AddFileExtensionsAsync(Guid cleaningId,
        IEnumerable<CleaningFileExtension> extensions, CancellationToken ct = default)
    {
        try
        {
            foreach (var ext in extensions)
                await ExecuteAsync("cleaning.usp_CleaningFileExtension_Insert", new
                {
                    ext.Id, CleaningId = cleaningId, ext.ExtensionId,
                    ext.Extension, ext.FileCount,
                    Status = (int)ext.Status,
                    ext.SuggestedNuGetPackage, ext.DiscoveredAtUtc
                }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> SetExecutionTargetAsync(Guid id, ExecutionTarget target,
        string? alternatePath, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_Cleaning_SetExecutionTarget", new
            {
                Id = id,
                ExecutionTarget = (int)target,
                AlternateExecutionPath = alternatePath
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> SaveTreeSnapshotsAsync(Guid id, string? beforeJson,
        string? afterJson, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_Cleaning_SaveTreeSnapshots", new
            {
                Id = id,
                BeforeTreeJson = beforeJson,
                AfterTreeJson = afterJson
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> RestartAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_Cleaning_Restart",
                new { Id = id, LastRestartedAtUtc = DateTime.UtcNow }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
