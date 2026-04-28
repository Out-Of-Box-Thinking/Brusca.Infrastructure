using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Models;
using Brusca.Core.Models.PathAccess;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Persists <see cref="PathCredentialRecord"/> rows for the path-access
/// pipeline. All mutations go through stored procedures.
/// </summary>
public sealed class PathCredentialRepository : DapperRepositoryBase, IPathCredentialRepository
{
    public PathCredentialRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result> SaveAsync(
        PathCredentialRecord record, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_PathCredential_Save", new
            {
                record.Id,
                record.CleaningId,
                record.RootPath,
                record.Username,
                record.EncryptedPassword,
                record.Domain,
                record.CreatedAtUtc,
            }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<PathCredentialRecord?>> GetAsync(
        Guid cleaningId, string rootPath, CancellationToken ct = default)
    {
        try
        {
            var row = await QuerySingleOrDefaultAsync<PathCredentialRecord>(
                "cleaning.usp_PathCredential_Get",
                new { CleaningId = cleaningId, RootPath = rootPath }, ct);
            return Result.Ok<PathCredentialRecord?>(row);
        }
        catch (Exception ex) { return Result.Fail<PathCredentialRecord?>(new ExceptionalError(ex)); }
    }

    public async Task<Result> DeleteByCleaningIdAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_PathCredential_DeleteByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
