using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Stored procedures:
///   prompts.usp_PromptStepCommand_Insert
///   prompts.usp_PromptStepCommand_GetByStepId
///   prompts.usp_PromptStepCommand_GetByCleaningId
///   prompts.usp_PromptStepCommand_MarkExecuted
///   prompts.usp_PromptStepCommand_DeleteByCleaningId
/// </summary>
public sealed class PromptStepCommandRepository : DapperRepositoryBase, IPromptStepCommandRepository
{
    public PromptStepCommandRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<PromptStepCommand>> CreateAsync(PromptStepCommand cmd, CancellationToken ct = default)
    {
        try
        {
            var result = await QuerySingleOrDefaultAsync<PromptStepCommand>(
                "prompts.usp_PromptStepCommand_Insert", new
                {
                    cmd.Id,
                    cmd.PromptStepId,
                    Language = (int)cmd.Language,
                    cmd.CommandBody,
                    cmd.CommandOrder,
                    cmd.CreatedAtUtc
                }, ct);
            return result is not null ? Result.Ok(result) : Result.Fail("Insert failed.");
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> BulkCreateAsync(IEnumerable<PromptStepCommand> commands, CancellationToken ct = default)
    {
        try
        {
            foreach (var cmd in commands)
                await CreateAsync(cmd, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<PromptStepCommand>>> GetByStepIdAsync(Guid stepId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<PromptStepCommand>(
                "prompts.usp_PromptStepCommand_GetByStepId", new { PromptStepId = stepId }, ct);
            return Result.Ok<IReadOnlyList<PromptStepCommand>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<PromptStepCommand>>> GetByCleaningIdAsync(Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<PromptStepCommand>(
                "prompts.usp_PromptStepCommand_GetByCleaningId", new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<PromptStepCommand>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> MarkExecutedAsync(Guid commandId, string? error, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStepCommand_MarkExecuted",
                new { Id = commandId, ExecutedAtUtc = DateTime.UtcNow, ExecutionError = error }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> DeleteByCleaningIdAsync(Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStepCommand_DeleteByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
