using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Claude;

/// <summary>
/// Calls Claude with ONLY <see cref="DocumentTypeSummary"/> rows
/// (DocumentType + extension + count) and asks it to design a directory
/// layout convention. No file contents, no PII, no original file names
/// ever leave the host process via this service.
/// </summary>
public sealed class ClaudeStructureService : IClaudeStructureService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeOptions _opts;

    private const string SystemPrompt =
        "You are a directory-architecture expert. " +
        "You will receive a JSON array of document buckets — each entry has " +
        "{ \"documentType\": string, \"extension\": string, \"count\": int }. " +
        "You DO NOT receive file contents, file names, or any PII. " +
        "Design a clean, hierarchical folder convention. Return JSON with the shape: " +
        "{ \"summary\": \"...\", \"rules\": [ " +
        "  { \"order\": int, \"documentType\": string, \"extension\": string, " +
        "    \"folderPathTemplate\": \"e.g. Invoices/{{Year}}\", " +
        "    \"fileNameTemplate\": \"e.g. {{InvoiceNumber}}_{{Date}}\", " +
        "    \"requiredTokenSlots\": [\"Year\",\"InvoiceNumber\",\"Date\"], " +
        "    \"rationale\": \"why this layout\" } " +
        "] }. " +
        "Templates may reference any of these token slots which the host will substitute " +
        "from a separate encrypted store: PersonName, EmailAddress, ClientName, Year, " +
        "Date, InvoiceNumber, AccountNumber, CaseNumber, Subject. " +
        "Use lowercase-hyphenated folders for generic categories and PascalCase only for " +
        "tokens. Respond ONLY with the JSON object — no preamble, no markdown fences.";

    public ClaudeStructureService(IOptions<BruscaOptions> options)
    {
        _opts = options.Value.Claude;
        _client = new AnthropicClient(new APIAuthentication(_opts.ApiKey));
    }

    public async Task<DirectoryStructurePlan> AnalyzeStructureAsync(
        Guid cleaningId,
        IReadOnlyList<DocumentTypeSummary> summaries,
        CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(summaries.Select(s => new
        {
            documentType = s.DocumentType.ToString(),
            extension    = s.Extension,
            count        = s.Count
        }));

        var raw = await CompleteAsync(SystemPrompt,
            "Anonymized document inventory:\n" + payload, ct);

        return ParsePlan(cleaningId, raw);
    }

    private async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var request = new MessageParameters
        {
            Model     = _opts.Model,
            MaxTokens = _opts.MaxTokens,
            SystemMessage = systemPrompt,
            Messages  = [new Message { Role = RoleType.User, Content = new List<ContentBase> { new TextContent { Text = userPrompt } } }]
        };
        var response = await _client.Messages.GetClaudeMessageAsync(request, null, ct);
        return response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
    }

    private static DirectoryStructurePlan ParsePlan(Guid cleaningId, string raw)
    {
        var plan = new DirectoryStructurePlan
        {
            CleaningId  = cleaningId,
            RawPlanJson = raw
        };

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            plan.Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";

            var rules = new List<DirectoryStructureRule>();
            if (root.TryGetProperty("rules", out var rs))
            {
                foreach (var r in rs.EnumerateArray())
                {
                    rules.Add(new DirectoryStructureRule
                    {
                        Order              = r.TryGetProperty("order", out var o) ? o.GetInt32() : 0,
                        DocumentType       = ParseDocType(r),
                        Extension          = r.TryGetProperty("extension", out var ex) ? ex.GetString() ?? "" : "",
                        FolderPathTemplate = r.TryGetProperty("folderPathTemplate", out var fp) ? fp.GetString() ?? "" : "",
                        FileNameTemplate   = r.TryGetProperty("fileNameTemplate", out var fn) ? fn.GetString() ?? "" : "",
                        RequiredTokenSlots = r.TryGetProperty("requiredTokenSlots", out var ts)
                            ? ts.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList()
                            : (IReadOnlyList<string>)[],
                        Rationale          = r.TryGetProperty("rationale", out var rat) ? rat.GetString() : null
                    });
                }
            }
            plan.Rules = rules;
        }
        catch
        {
            plan.Summary = "Could not parse Claude response — see RawPlanJson for the raw text.";
        }
        return plan;
    }

    private static DocumentType ParseDocType(JsonElement r) =>
        r.TryGetProperty("documentType", out var dt)
        && Enum.TryParse<DocumentType>(dt.GetString(), true, out var v)
            ? v : DocumentType.Unknown;
}
