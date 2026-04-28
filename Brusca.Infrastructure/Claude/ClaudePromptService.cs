using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Brusca.Infrastructure.Claude;

/// <summary>
/// Calls the Anthropic Claude API to analyse a directory tree and produce
/// an ordered list of PromptStep + PromptStepCommand objects.
///
/// Each PromptStep gets three commands — C#, CMD, and PowerShell — so the
/// executor can pick whichever language is available on the host.
/// </summary>
public sealed class ClaudePromptService
{
    private readonly AnthropicClient _client;
    private readonly ClaudeOptions _opts;

    private const string SystemPrompt =
        "You are a professional file system organization specialist. " +
        "Given a directory tree and sample file contents, generate a JSON array of rename/reorganization steps. " +
        "Each element MUST be: " +
        "{ \"stepOrder\": int, \"stepType\": \"DirectoryRename|FileRename|FileMove|ContentSummary\", " +
        "\"promptText\": string (human rationale), " +
        "\"sourcePath\": string, \"proposedTargetPath\": string, " +
        "\"commands\": [ " +
        "  { \"language\": \"CSharp\",     \"commandOrder\": 1, \"commandBody\": \"...full C# snippet...\" }, " +
        "  { \"language\": \"Cmd\",        \"commandOrder\": 2, \"commandBody\": \"...CMD command...\" }, " +
        "  { \"language\": \"PowerShell\", \"commandOrder\": 3, \"commandBody\": \"...PS1 snippet...\" } " +
        "] }. " +
        "For C# use System.IO (File.Move, Directory.Move, Directory.CreateDirectory). " +
        "For CMD use REN, MOVE, MKDIR, XCOPY. " +
        "For PowerShell use Rename-Item, Move-Item, New-Item -ItemType Directory. " +
        "Use lowercase-hyphenated names for directories, PascalCase for documents, YYYY-MM-DD prefix for dated files. " +
        "Respond ONLY with the JSON array. No preamble. No markdown fences.";

    public ClaudePromptService(IOptions<BruscaOptions> options)
    {
        _opts = options.Value.Claude;
        _client = new AnthropicClient(new APIAuthentication(_opts.ApiKey));
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new MessageParameters
        {
            Model = _opts.Model,
            MaxTokens = _opts.MaxTokens,
            SystemMessage = systemPrompt,
            Messages = [new Message { Role = RoleType.User, Content = new List<ContentBase> { new TextContent { Text = userPrompt } } }]
        };
        var response = await _client.Messages.GetClaudeMessageAsync(request, null, ct);
        return response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? string.Empty;
    }

    /// <summary>
    /// Generates an ordered list of CleaningPromptStep objects, each with
    /// fully populated PromptStepCommand children (C#, CMD, PowerShell).
    /// </summary>
    public async Task<IReadOnlyList<CleaningPromptStep>> GenerateStepsAsync(
        Guid cleaningId,
        string directoryTreeJson,
        string fileContentsSample,
        CancellationToken ct = default)
    {
        var userPrompt =
            $"Directory tree:\n{directoryTreeJson}\n\nSample file contents:\n{fileContentsSample}\n\n" +
            "Generate the ordered rename and reorganization steps with commands in all three languages.";

        var raw = await CompleteAsync(SystemPrompt, userPrompt, ct);

        return ParseSteps(cleaningId, raw);
    }

    private static IReadOnlyList<CleaningPromptStep> ParseSteps(Guid cleaningId, string raw)
    {
        try
        {
            var steps = new List<CleaningPromptStep>();
            using var doc = JsonDocument.Parse(raw);

            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                var step = new CleaningPromptStep
                {
                    CleaningId = cleaningId,
                    StepOrder   = elem.TryGetProperty("stepOrder", out var o) ? o.GetInt32() : 0,
                    StepType    = ParseStepType(elem),
                    PromptText  = elem.TryGetProperty("promptText", out var pt) ? pt.GetString() ?? "" : "",
                    SourcePath  = elem.TryGetProperty("sourcePath", out var sp) ? sp.GetString() : null,
                    ProposedTargetPath = elem.TryGetProperty("proposedTargetPath", out var tp) ? tp.GetString() : null
                };

                var commands = new List<PromptStepCommand>();
                if (elem.TryGetProperty("commands", out var cmds))
                {
                    foreach (var c in cmds.EnumerateArray())
                    {
                        commands.Add(new PromptStepCommand
                        {
                            PromptStepId = step.Id,
                            Language     = ParseLanguage(c),
                            CommandBody  = c.TryGetProperty("commandBody", out var cb) ? cb.GetString() ?? "" : "",
                            CommandOrder = c.TryGetProperty("commandOrder", out var co) ? co.GetInt32() : 0
                        });
                    }
                }
                step.Commands = commands;
                steps.Add(step);
            }

            return steps;
        }
        catch
        {
            // Fallback: treat the raw text as a single descriptive step with no commands
            return [new CleaningPromptStep
            {
                CleaningId = cleaningId,
                StepOrder  = 1,
                StepType   = PromptStepType.ConventionSuggestion,
                PromptText = raw
            }];
        }
    }

    private static PromptStepType ParseStepType(JsonElement elem)
    {
        if (!elem.TryGetProperty("stepType", out var t)) return PromptStepType.ConventionSuggestion;
        return t.GetString() switch
        {
            "DirectoryRename" => PromptStepType.DirectoryRename,
            "FileRename"      => PromptStepType.FileRename,
            "FileMove"        => PromptStepType.FileMove,
            "ContentSummary"  => PromptStepType.ContentSummary,
            _                 => PromptStepType.ConventionSuggestion
        };
    }

    private static CommandLanguage ParseLanguage(JsonElement c)
    {
        if (!c.TryGetProperty("language", out var l)) return CommandLanguage.PowerShell;
        return l.GetString() switch
        {
            "CSharp"     => CommandLanguage.CSharp,
            "Cmd"        => CommandLanguage.Cmd,
            "PowerShell" => CommandLanguage.PowerShell,
            _            => CommandLanguage.PowerShell
        };
    }
}
