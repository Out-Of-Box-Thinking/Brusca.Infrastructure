using System.Text.Json;
using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Phase-11 slot-mapping + slot-completeness orchestration. Lives as a partial
/// of <see cref="CleaningService"/> so the existing class remains the single
/// entry point for the cleaning lifecycle.
/// </summary>
public sealed partial class CleaningService
{
    public async Task<Result> MapSlotsAsync(Guid cleaningId, CancellationToken ct = default)
    {
        var planRes = await _planRepo.GetLatestAsync(cleaningId, ct);
        if (planRes.IsFailed) return planRes.ToResult();

        var filesRes = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (filesRes.IsFailed) return filesRes.ToResult();

        var slotMapper = _slotMapper
            ?? throw new InvalidOperationException("IPiiSlotMappingService is not registered.");

        var rules = planRes.Value.Rules;
        var mapped = 0;

        foreach (var file in filesRes.Value)
        {
            ct.ThrowIfCancellationRequested();

            var rule = rules.FirstOrDefault(r =>
                r.DocumentType == file.DocumentType &&
                (string.IsNullOrEmpty(r.Extension) ||
                 r.Extension.Equals(file.Extension, StringComparison.OrdinalIgnoreCase)))
                ?? rules.FirstOrDefault(r => r.DocumentType == file.DocumentType);

            if (rule is null || rule.RequiredTokenSlots.Count == 0) continue;

            var mapRes = await slotMapper.MapAsync(file, rule.RequiredTokenSlots, ct);
            if (mapRes.IsFailed)
            {
                await _log.LogErrorAsync(
                    $"Slot mapping failed for RedactedFile {file.Id}: {string.Join("; ", mapRes.Errors.Select(e => e.Message))}",
                    null, cleaningId: cleaningId);
                continue;
            }

            var json = JsonSerializer.Serialize(new { slotToOrdinal = mapRes.Value.SlotToOrdinal });
            var save = await _redactedRepo.UpdateSlotMapAsync(file.Id, json, ct);
            if (save.IsSuccess) mapped++;
        }

        await _audit.LogAsync("SlotsMapped", "Cleaning",
            cleaningId.ToString(), action: "MapSlots",
            newValues: new { Total = filesRes.Value.Count, Mapped = mapped });

        return Result.Ok();
    }

    public async Task<Result<IReadOnlyList<MissingSlotReport>>> ValidateSlotsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        if (_slotValidator is null)
            return Result.Fail("ISlotCompletenessValidator is not registered.");

        var res = await _slotValidator.ValidateAsync(cleaningId, ct);
        if (res.IsSuccess)
        {
            await _audit.LogAsync("SlotsValidated", "Cleaning",
                cleaningId.ToString(), action: "ValidateSlots",
                newValues: new { Missing = res.Value.Count });
        }
        return res;
    }
}
