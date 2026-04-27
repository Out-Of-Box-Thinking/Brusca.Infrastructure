using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

public sealed class PromptStepRepository : DapperRepositoryBase, IPromptStepRepository
{
    public PromptStepRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<CleaningPromptStep>> CreateAsync(CleaningPromptStep step, CancellationToken ct = default)
    {
        try
        {
            var result = await QuerySingleOrDefaultAsync<CleaningPromptStep>(
                "prompts.usp_PromptStep_Insert", new
                {
                    step.Id, step.CleaningId, step.StepOrder,
                    StepType = (int)step.StepType,
                    step.PromptText, step.SourcePath,
                    step.ProposedTargetPath, step.CreatedAtUtc
                }, ct);
            return result is not null ? Result.Ok(result) : Result.Fail("Insert failed.");
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<IReadOnlyList<CleaningPromptStep>>> GetByCleaningIdAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var rows = await QueryAsync<CleaningPromptStep>(
                "prompts.usp_PromptStep_GetByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok<IReadOnlyList<CleaningPromptStep>>(rows.ToList());
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> UpdateResponseAsync(Guid stepId, string response, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStep_UpdateResponse",
                new { Id = stepId, GeneratedResponse = response }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> ApproveStepAsync(Guid stepId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStep_Approve", new { Id = stepId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> MarkExecutedAsync(Guid stepId, string? error, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStep_MarkExecuted",
                new { Id = stepId, ExecutedAtUtc = DateTime.UtcNow, ExecutionError = error }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> DeleteByCleaningIdAsync(Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("prompts.usp_PromptStep_DeleteByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }
}
