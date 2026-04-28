using System.Text.Json;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Pre-execute sanity check that every redacted file matched by a structure
/// rule has a slot map satisfying that rule's <c>RequiredTokenSlots</c>.
/// "Universal" slots (Year/Month/Day/Date/Extension/DocumentType) are derived
/// by the host and never need a slot map entry.
/// </summary>
public sealed class SlotCompletenessValidator : ISlotCompletenessValidator
{
    private static readonly HashSet<string> UniversalSlots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Year","Month","Day","Date","Extension","DocumentType"
    };

    private readonly IRedactedFileRepository _redactedRepo;
    private readonly IStructurePlanRepository _planRepo;

    public SlotCompletenessValidator(
        IRedactedFileRepository redactedRepo,
        IStructurePlanRepository planRepo)
    {
        _redactedRepo = redactedRepo;
        _planRepo     = planRepo;
    }

    public async Task<Result<IReadOnlyList<MissingSlotReport>>> ValidateAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var planRes = await _planRepo.GetLatestAsync(cleaningId, ct);
        if (planRes.IsFailed) return Result.Fail(planRes.Errors);

        var filesRes = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (filesRes.IsFailed) return Result.Fail(filesRes.Errors);

        var rules = planRes.Value.Rules;
        var reports = new List<MissingSlotReport>();

        foreach (var file in filesRes.Value)
        {
            ct.ThrowIfCancellationRequested();

            var rule = MatchRule(rules, file);
            if (rule is null) continue;

            var required = rule.RequiredTokenSlots
                .Where(s => !UniversalSlots.Contains(s) && !IsKindSlot(s, file))
                .ToList();
            if (required.Count == 0) continue;

            var mapped = ParseSlotKeys(file.SlotMapJson);
            var missing = required
                .Where(s => !mapped.Contains(s))
                .ToList();

            if (missing.Count > 0)
            {
                reports.Add(new MissingSlotReport
                {
                    RedactedFileId   = file.Id,
                    OriginalFileName = file.OriginalFileName,
                    MissingSlots     = missing,
                    FallbackPath     = "_unsorted/" + file.OriginalFileName
                });
            }
        }

        return Result.Ok<IReadOnlyList<MissingSlotReport>>(reports);
    }

    private static DirectoryStructureRule? MatchRule(
        IReadOnlyList<DirectoryStructureRule> rules, RedactedFileDescriptor file) =>
            rules.FirstOrDefault(r =>
                r.DocumentType == file.DocumentType &&
                (string.IsNullOrEmpty(r.Extension) ||
                 r.Extension.Equals(file.Extension, StringComparison.OrdinalIgnoreCase)))
            ?? rules.FirstOrDefault(r => r.DocumentType == file.DocumentType);

    /// <summary>
    /// A "kind slot" name (e.g. <c>"PersonName"</c>) is satisfied implicitly by
    /// the rehydrator's kind-keyed pool fallback when at least one PII segment
    /// of that kind exists on the file. We don't require an explicit slot map
    /// entry for those.
    /// </summary>
    private static bool IsKindSlot(string slot, RedactedFileDescriptor file) =>
        Enum.TryParse<Brusca.Core.Enums.PiiKind>(slot, ignoreCase: true, out _);

    private static HashSet<string> ParseSlotKeys(string? slotMapJson)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(slotMapJson)) return keys;
        try
        {
            using var doc = JsonDocument.Parse(slotMapJson);
            if (doc.RootElement.TryGetProperty("slotToOrdinal", out var dict)
                && dict.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dict.EnumerateObject())
                    keys.Add(prop.Name);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Tolerate a flat { slot: ordinal } shape too.
                foreach (var prop in doc.RootElement.EnumerateObject())
                    keys.Add(prop.Name);
            }
        }
        catch
        {
            // Treat malformed map as empty.
        }
        return keys;
    }
}
