using System.Text.Json;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Data.Repositories;

/// <summary>
/// Persists Claude-generated <see cref="DirectoryStructurePlan"/> rows along with
/// their child <see cref="DirectoryStructureRule"/> rows.
/// </summary>
public sealed class StructurePlanRepository : DapperRepositoryBase, IStructurePlanRepository
{
    public StructurePlanRepository(IOptions<BruscaOptions> options) : base(options) { }

    public async Task<Result<DirectoryStructurePlan>> CreateAsync(
        DirectoryStructurePlan plan, CancellationToken ct = default)
    {
        try
        {
            var rulesJson = JsonSerializer.Serialize(plan.Rules);
            await ExecuteAsync("cleaning.usp_StructurePlan_Insert", new
            {
                plan.Id,
                plan.CleaningId,
                plan.Summary,
                RulesJson   = rulesJson,
                plan.RawPlanJson,
                plan.GeneratedAtUtc
            }, ct);
            return Result.Ok(plan);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result<DirectoryStructurePlan>> GetLatestAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var row = await QuerySingleOrDefaultAsync<StructurePlanRow>(
                "cleaning.usp_StructurePlan_GetLatest",
                new { CleaningId = cleaningId }, ct);

            if (row is null)
                return Result.Fail($"No structure plan for cleaning {cleaningId}.");

            var plan = new DirectoryStructurePlan
            {
                Id             = row.Id,
                CleaningId     = row.CleaningId,
                Summary        = row.Summary ?? string.Empty,
                RawPlanJson    = row.RawPlanJson,
                GeneratedAtUtc = row.GeneratedAtUtc,
                Rules          = string.IsNullOrEmpty(row.RulesJson)
                    ? []
                    : JsonSerializer.Deserialize<List<DirectoryStructureRule>>(row.RulesJson) ?? []
            };
            return Result.Ok(plan);
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    public async Task<Result> DeleteByCleaningIdAsync(Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync("cleaning.usp_StructurePlan_DeleteByCleaningId",
                new { CleaningId = cleaningId }, ct);
            return Result.Ok();
        }
        catch (Exception ex) { return Result.Fail(new ExceptionalError(ex)); }
    }

    private sealed class StructurePlanRow
    {
        public Guid Id { get; set; }
        public Guid CleaningId { get; set; }
        public string? Summary { get; set; }
        public string? RulesJson { get; set; }
        public string? RawPlanJson { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }
}
