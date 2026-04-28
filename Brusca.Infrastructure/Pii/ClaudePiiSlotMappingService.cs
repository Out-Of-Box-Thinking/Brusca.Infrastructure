using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models;
using Brusca.Core.Models.Pii;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Pii;

/// <summary>
/// Asks Claude to map a single file's PII segment ordinals to the named slots
/// the directory-structure plan requires (e.g. <c>"ClientName" → 3</c>).
///
/// Claude only sees:
///   ‣ the redacted content (PII already substituted with tokens),
///   ‣ a list of segment summaries <c>{ ordinal, kind, token, length }</c>
///     — token strings are placeholders, not literals,
///   ‣ the names of the slots required by the plan.
///
/// The original literals never leave the host. The returned slot map is
/// PLAINTEXT — slot names + ordinals are not PII.
/// </summary>
public sealed class ClaudePiiSlotMappingService : IPiiSlotMappingService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeOptions _opts;
    private readonly IEncryptionService _crypto;

    private const string SystemPrompt =
        "You are a redaction-aware document analyst. " +
        "You will receive: (1) the file's ORIGINAL FILE NAME, (2) its REDACTED CONTENT — " +
        "every PII span has been replaced by a stable token of the form " +
        "[[PII:Kind:Ordinal]], (3) a SEGMENTS array describing each PII segment by " +
        "{ ordinal:int, kind:string, token:string, length:int } — note that segment " +
        "VALUES are NEVER provided to you, and (4) a REQUIRED_SLOTS array of slot names " +
        "the host needs filled (e.g. ClientName, InvoiceNumber). " +
        "Decide which segment ORDINAL most likely supplies each required slot, using " +
        "only the redacted content's surrounding context and the segment Kind. " +
        "Return ONLY a single JSON object whose shape is { \"slotToOrdinal\": { \"<slotName>\": <ordinal int>, ... } }. " +
        "Omit any slot you cannot confidently map. Do NOT include any other keys. " +
        "Do NOT echo the file content. Do NOT emit any preamble, prose, or markdown fences.";

    public ClaudePiiSlotMappingService(
        IOptions<BruscaOptions> options,
        IEncryptionService crypto)
    {
        _opts = options.Value.Claude;
        _client = new AnthropicClient(new APIAuthentication(_opts.ApiKey));
        _crypto = crypto;
    }

    public async Task<Result<PiiSlotMap>> MapAsync(
        RedactedFileDescriptor file,
        IReadOnlyList<string> requiredSlots,
        CancellationToken ct = default)
    {
        if (requiredSlots is null || requiredSlots.Count == 0)
            return Result.Ok(new PiiSlotMap { RedactedFileId = file.Id });

        // Build the segment-summary view WITHOUT the literal Value field.
        var segmentSummaries = DecryptSegmentSummaries(file);

        // If we have nothing to map onto, return an empty map rather than burn a Claude call.
        if (segmentSummaries.Count == 0)
            return Result.Ok(new PiiSlotMap { RedactedFileId = file.Id });

        var payload = JsonSerializer.Serialize(new
        {
            originalFileName = file.OriginalFileName,
            redactedContent  = file.RedactedContent,
            segments         = segmentSummaries,
            requiredSlots
        });

        try
        {
            var raw = await CompleteAsync(SystemPrompt,
                "Map the required slots to segment ordinals for this file:\n" + payload, ct);

            return Result.Ok(ParseSlotMap(file.Id, raw));
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(
                $"Slot mapping call failed for RedactedFile {file.Id}", ex));
        }
    }

    private List<SegmentSummary> DecryptSegmentSummaries(RedactedFileDescriptor file)
    {
        if (string.IsNullOrEmpty(file.EncryptedPiiJson)) return [];
        try
        {
            var json = _crypto.Decrypt(file.EncryptedPiiJson);
            var segments = JsonSerializer.Deserialize<List<PiiSegment>>(json) ?? [];
            return segments.Select(s => new SegmentSummary
            {
                Ordinal = s.Ordinal,
                Kind    = s.Kind.ToString(),
                Token   = s.Token,
                Length  = s.Length
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new MessageParameters
        {
            Model     = _opts.Model,
            MaxTokens = _opts.MaxTokens,
            SystemMessage = systemPrompt,
            Messages  = [new Message
            {
                Role    = RoleType.User,
                Content = new List<ContentBase> { new TextContent { Text = userPrompt } }
            }]
        };
        var response = await _client.Messages.GetClaudeMessageAsync(request, null, ct);
        return response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
    }

    private static PiiSlotMap ParseSlotMap(Guid redactedFileId, string raw)
    {
        var map = new PiiSlotMap { RedactedFileId = redactedFileId };
        if (string.IsNullOrWhiteSpace(raw)) return map;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("slotToOrdinal", out var dict)
                && dict.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dict.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number
                        && prop.Value.TryGetInt32(out var ordinal))
                    {
                        map.SlotToOrdinal[prop.Name] = ordinal;
                    }
                }
            }
        }
        catch
        {
            // Malformed response — return whatever we collected (possibly empty).
        }
        return map;
    }

    private sealed class SegmentSummary
    {
        public int Ordinal { get; set; }
        public string Kind { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public int Length { get; set; }
    }
}
