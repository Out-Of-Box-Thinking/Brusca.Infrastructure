using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Thin orchestrator over <see cref="IRedactedFileRepository.GetDuplicateGroupsAsync"/>
/// that applies the configured <see cref="DuplicateKeepStrategy"/> to elect the
/// keeper inside each group.
/// </summary>
public sealed class DuplicateDetectionService : IDuplicateDetectionService
{
    private readonly IRedactedFileRepository _redactedRepo;
    private readonly BruscaOptions _options;
    private readonly IAuditLogger _audit;

    public DuplicateDetectionService(
        IRedactedFileRepository redactedRepo,
        IOptions<BruscaOptions> options,
        IAuditLogger audit)
    {
        _redactedRepo = redactedRepo;
        _options      = options.Value;
        _audit        = audit;
    }

    public async Task<Result<IReadOnlyList<DuplicateGroup>>> AnalyzeAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var groupsResult = await _redactedRepo.GetDuplicateGroupsAsync(cleaningId, ct);
        if (groupsResult.IsFailed) return groupsResult;

        var strategy = _options.Materialization.DuplicateKeepStrategy;
        var filesResult = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (filesResult.IsFailed) return Result.Fail(filesResult.Errors);

        var byId = filesResult.Value.ToDictionary(f => f.Id);

        var resolved = groupsResult.Value.Select(g =>
        {
            var members = g.RedactedFileIds.Where(byId.ContainsKey).ToList();
            if (members.Count == 0) return g;

            var keeper = strategy switch
            {
                DuplicateKeepStrategy.KeepNewest =>
                    members.OrderByDescending(id => byId[id].DiscoveredAtUtc).First(),
                DuplicateKeepStrategy.KeepDeepestPath =>
                    members.OrderByDescending(id => byId[id].OriginalFilePath?.Length ?? 0).First(),
                _ /* KeepFirstPath */ =>
                    members.OrderBy(id => byId[id].OriginalFilePath, StringComparer.OrdinalIgnoreCase).First()
            };

            return new DuplicateGroup
            {
                ContentHash        = g.ContentHash,
                KeepRedactedFileId = keeper,
                RedactedFileIds    = members,
                Strategy           = strategy
            };
        }).ToList();

        await _audit.LogAsync("DuplicatesAnalyzed", "Cleaning",
            cleaningId.ToString(), action: "AnalyzeDuplicates",
            newValues: new { Groups = resolved.Count });

        return Result.Ok<IReadOnlyList<DuplicateGroup>>(resolved);
    }
}
