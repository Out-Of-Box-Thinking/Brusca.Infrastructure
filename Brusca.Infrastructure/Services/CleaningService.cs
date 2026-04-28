using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Extensions;
using Brusca.Core.Models.Pii;
using Brusca.Infrastructure.Claude;
using Brusca.Infrastructure.Pii;
using FluentResults;
using System.Text.Json;

namespace Brusca.Infrastructure.Services;

public sealed class CleaningService : ICleaningService
{
    private readonly ICleaningRepository _cleaningRepo;
    private readonly IPromptStepRepository _promptRepo;
    private readonly IPromptStepCommandRepository _cmdRepo;
    private readonly IRedactedFileRepository _redactedRepo;
    private readonly IStructurePlanRepository _planRepo;
    private readonly IFileRelocationRepository _relocRepo;
    private readonly IFileSystemService _fs;
    private readonly IFileExtensionService _extService;
    private readonly ITreeProjectionService _projection;
    private readonly IPiiRedactionService _redactor;
    private readonly IDocumentTypeClassifier _classifier;
    private readonly IEncryptionService _crypto;
    private readonly IClaudeStructureService _claudeStructure;
    private readonly IStructureExecutionService _structureExec;
    private readonly IDuplicateDetectionService _dupes;
    private readonly IPromotionService? _promotion;
    private readonly ClaudePromptService _claude;
    private readonly IAuditLogger _audit;
    private readonly IErrorLogger _log;

    public CleaningService(
        ICleaningRepository cleaningRepo,
        IPromptStepRepository promptRepo,
        IPromptStepCommandRepository cmdRepo,
        IRedactedFileRepository redactedRepo,
        IStructurePlanRepository planRepo,
        IFileRelocationRepository relocRepo,
        IFileSystemService fs,
        IFileExtensionService extService,
        ITreeProjectionService projection,
        IPiiRedactionService redactor,
        IDocumentTypeClassifier classifier,
        IEncryptionService crypto,
        IClaudeStructureService claudeStructure,
        IStructureExecutionService structureExec,
        IDuplicateDetectionService dupes,
        ClaudePromptService claude,
        IAuditLogger audit,
        IErrorLogger log,
        IPromotionService? promotion = null)
    {
        _cleaningRepo    = cleaningRepo;
        _promptRepo      = promptRepo;
        _cmdRepo         = cmdRepo;
        _redactedRepo    = redactedRepo;
        _planRepo        = planRepo;
        _relocRepo       = relocRepo;
        _fs              = fs;
        _extService      = extService;
        _projection      = projection;
        _redactor        = redactor;
        _classifier      = classifier;
        _crypto          = crypto;
        _claudeStructure = claudeStructure;
        _structureExec   = structureExec;
        _dupes           = dupes;
        _promotion       = promotion;
        _claude          = claude;
        _audit           = audit;
        _log             = log;
    }

    public async Task<Result<Cleaning>> StartCleaningAsync(
        string rootPath, string userId, CancellationToken ct = default)
    {
        var cleaning = new Cleaning { RootPath = rootPath, CreatedByUserId = userId };
        var created = await _cleaningRepo.CreateAsync(cleaning, ct);
        if (created.IsFailed) return created;
        await _audit.LogAsync("CleaningStarted", "Cleaning",
            created.Value.Id.ToString(), userId, "Create");
        return created;
    }

    public async Task<Result> RestartCleaningAsync(
        Guid cleaningId, string userId, CancellationToken ct = default)
    {
        // Delete all child data so the Cleaning can be re-scanned from scratch
        var del1 = await _cmdRepo.DeleteByCleaningIdAsync(cleaningId, ct);
        if (del1.IsFailed) return del1;

        var del2 = await _promptRepo.DeleteByCleaningIdAsync(cleaningId, ct);
        if (del2.IsFailed) return del2;

        // Clear snapshots and reset status to Pending
        await _cleaningRepo.SaveTreeSnapshotsAsync(cleaningId, null, null, ct);
        var restart = await _cleaningRepo.RestartAsync(cleaningId, ct);
        if (restart.IsFailed) return restart;

        await _audit.LogAsync("CleaningRestarted", "Cleaning",
            cleaningId.ToString(), userId, "Restart");
        return Result.Ok();
    }

    public async Task<Result<ExtensionScanResult>> ScanExtensionsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaningResult = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaningResult.IsFailed) return Result.Fail(cleaningResult.Errors);

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.Scanning, ct);

        var scanResult = await _fs.ScanForExtensionsAsync(
            cleaningResult.Value.RootPath, cleaningId, ct);
        if (scanResult.IsFailed) return scanResult;

        // Capture the before-tree snapshot
        var treeResult = await _fs.BuildDirectoryTreeAsync(cleaningResult.Value.RootPath, ct);
        if (treeResult.IsSuccess)
        {
            var treeJson = JsonSerializer.Serialize(treeResult.Value);
            await _cleaningRepo.SaveTreeSnapshotsAsync(cleaningId, treeJson, null, ct);
        }

        await _extService.SyncFromScanAsync(scanResult.Value, ct);

        var extRecords = scanResult.Value.AllExtensions.Select(ext => new CleaningFileExtension
        {
            CleaningId   = cleaningId,
            ExtensionId  = Guid.NewGuid(),
            Extension    = ext,
            Status       = scanResult.Value.UnknownExtensions.Contains(ext)
                           ? FileExtensionStatus.Unknown : FileExtensionStatus.Known
        }).ToList();

        await _cleaningRepo.AddFileExtensionsAsync(cleaningId, extRecords, ct);

        var newStatus = scanResult.Value.UnknownExtensions.Any()
            ? CleaningStatus.AwaitingExtensionResolution
            : CleaningStatus.Analyzing;

        await _cleaningRepo.UpdateStatusAsync(cleaningId, newStatus, ct);
        await _audit.LogAsync("ExtensionScanCompleted", "Cleaning",
            cleaningId.ToString(), action: "Scan");

        return scanResult;
    }

    public async Task<Result> GeneratePromptStepsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaningResult = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaningResult.IsFailed) return Result.Fail(cleaningResult.Errors);

        var tree = await _fs.BuildDirectoryTreeAsync(cleaningResult.Value.RootPath, ct);
        if (tree.IsFailed) return Result.Fail(tree.Errors);

        var treeJson = JsonSerializer.Serialize(
            tree.Value, new JsonSerializerOptions { WriteIndented = false });

        var sampleContents = await SampleFileContentsAsync(
            cleaningResult.Value.RootPath, ct);

        // Claude returns fully-populated CleaningPromptStep objects with Commands
        var steps = await _claude.GenerateStepsAsync(cleaningId, treeJson, sampleContents, ct);

        foreach (var step in steps)
        {
            var stepResult = await _promptRepo.CreateAsync(step, ct);
            if (stepResult.IsFailed) continue;

            if (step.Commands.Any())
                await _cmdRepo.BulkCreateAsync(step.Commands, ct);
        }

        // Compute and persist the projected after-tree
        var after = _projection.ProjectAfterTree(tree.Value, steps);
        var afterJson = JsonSerializer.Serialize(after);
        await _cleaningRepo.SaveTreeSnapshotsAsync(
            cleaningId,
            treeJson,   // re-persist before snapshot in case it was cleared
            afterJson,
            ct);

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.PromptGenerated, ct);
        await _audit.LogAsync("PromptStepsGenerated", "Cleaning",
            cleaningId.ToString(), action: "Generate");

        return Result.Ok();
    }

    public async Task<Result> SetExecutionTargetAsync(
        Guid cleaningId, ExecutionTarget target, string? alternatePath,
        string userId, CancellationToken ct = default)
    {
        var result = await _cleaningRepo.SetExecutionTargetAsync(
            cleaningId, target, alternatePath, ct);
        if (result.IsFailed) return result;

        await _audit.LogAsync("ExecutionTargetSet", "Cleaning",
            cleaningId.ToString(), userId,
            $"Target={target},AlternatePath={alternatePath}");

        return Result.Ok();
    }

    public async Task<Result> ExecuteApprovedStepsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaningResult = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaningResult.IsFailed) return Result.Fail(cleaningResult.Errors);

        var cleaning = cleaningResult.Value;
        var executionRoot = cleaning.ExecutionTarget == ExecutionTarget.AlternatePath
            && !string.IsNullOrEmpty(cleaning.AlternateExecutionPath)
            ? cleaning.AlternateExecutionPath
            : cleaning.RootPath;

        var stepsResult = await _promptRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (stepsResult.IsFailed) return Result.Fail(stepsResult.Errors);

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.Executing, ct);

        foreach (var step in stepsResult.Value
            .Where(s => s.IsApproved && !s.IsExecuted)
            .OrderBy(s => s.StepOrder))
        {
            // Translate source/target paths to the execution root if alternate
            var src = TranslateToExecutionRoot(
                step.SourcePath, cleaning.RootPath, executionRoot);
            var tgt = TranslateToExecutionRoot(
                step.ProposedTargetPath, cleaning.RootPath, executionRoot);

            string? stepError = null;
            try
            {
                if (!string.IsNullOrEmpty(src) && !string.IsNullOrEmpty(tgt))
                {
                    var r = await _fs.RenameAsync(src, tgt, ct);
                    if (r.IsFailed)
                        stepError = string.Join("; ", r.Errors.Select(e => e.Message));
                }
            }
            catch (Exception ex)
            {
                stepError = ex.Message;
                await _log.LogErrorAsync("Step execution failed", ex, cleaningId: cleaningId);
            }

            await _promptRepo.MarkExecutedAsync(step.Id, stepError, ct);

            // Mark all commands on this step as executed
            var cmdsResult = await _cmdRepo.GetByStepIdAsync(step.Id, ct);
            if (cmdsResult.IsSuccess)
                foreach (var cmd in cmdsResult.Value)
                    await _cmdRepo.MarkExecutedAsync(cmd.Id, stepError, ct);
        }

        await _cleaningRepo.CompleteAsync(cleaningId, ct);
        await _audit.LogAsync("CleaningExecuted", "Cleaning",
            cleaningId.ToString(), action: "Execute");
        return Result.Ok();
    }

    public Task<Result<Cleaning>> GetCleaningAsync(Guid id, CancellationToken ct = default)
        => _cleaningRepo.GetByIdAsync(id, ct);

    public async Task<Result<TreeComparisonResult>> GetTreeComparisonAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaning = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaning.IsFailed) return Result.Fail(cleaning.Errors);

        DirectoryNode? before = null;
        DirectoryNode? after  = null;

        if (!string.IsNullOrEmpty(cleaning.Value.BeforeTreeJson))
            before = JsonSerializer.Deserialize<DirectoryNode>(cleaning.Value.BeforeTreeJson);

        if (!string.IsNullOrEmpty(cleaning.Value.AfterTreeJson))
            after = JsonSerializer.Deserialize<DirectoryNode>(cleaning.Value.AfterTreeJson);

        var stepsResult = await _promptRepo.GetByCleaningIdAsync(cleaningId, ct);
        var steps = stepsResult.IsSuccess ? stepsResult.Value : [];

        var renames = steps.Count(s => s.StepType is
            PromptStepType.FileRename or PromptStepType.DirectoryRename);
        var moves = steps.Count(s => s.StepType == PromptStepType.FileMove);

        return Result.Ok(new TreeComparisonResult
        {
            CleaningId       = cleaningId,
            BeforeTree       = before,
            AfterTree        = after,
            TotalRenames     = renames,
            TotalMoves       = moves,
            IsAfterProjected = !cleaning.Value.IsCompleted()
        });
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task<string> SampleFileContentsAsync(
        string rootPath, CancellationToken ct)
    {
        var samples = new List<string>();
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(
            rootPath, "*", SearchOption.AllDirectories))
        {
            if (count >= 5) break;
            var read = await _fs.ReadFileContentAsync(file, ct);
            if (read.IsSuccess)
            {
                var truncated = read.Value.Length > 500
                    ? read.Value[..500] + "..."
                    : read.Value;
                samples.Add($"--- {Path.GetFileName(file)} ---\n{truncated}");
                count++;
            }
        }
        return string.Join("\n\n", samples);
    }

    private static string? TranslateToExecutionRoot(
        string? path, string originalRoot, string executionRoot)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (string.Equals(originalRoot, executionRoot, StringComparison.OrdinalIgnoreCase))
            return path;
        return path.Replace(originalRoot, executionRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── PII redaction + structure-plan pipeline ──────────────────────────────

    public async Task<Result<IReadOnlyList<RedactedFileDescriptor>>> RedactAndClassifyAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaningResult = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaningResult.IsFailed) return Result.Fail(cleaningResult.Errors);
        var cleaning = cleaningResult.Value;

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.Redacting, ct);
        // Wipe any prior redaction artefacts for this cleaning.
        await _redactedRepo.DeleteByCleaningIdAsync(cleaningId, ct);

        var descriptors = new List<RedactedFileDescriptor>();

        foreach (var file in Directory.EnumerateFiles(
            cleaning.RootPath, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file).ToLowerInvariant();

            var read = await _fs.ReadFileContentAsync(file, ct);
            if (read.IsFailed) continue; // unreadable / too large — skip silently

            var redactionResult = await _redactor.RedactAsync(read.Value, ct);
            if (redactionResult.IsFailed) continue;

            var classifyResult = await _classifier.ClassifyAsync(
                redactionResult.Value.RedactedContent, ext, ct);

            // Encrypt the PII segments before they ever land at rest
            string? sealedPii = null;
            if (redactionResult.Value.Segments.Count > 0)
            {
                var json = JsonSerializer.Serialize(redactionResult.Value.Segments);
                sealedPii = _crypto.Encrypt(json);
            }

            var descriptor = new RedactedFileDescriptor
            {
                CleaningId         = cleaningId,
                OriginalFilePath   = file,
                OriginalFileName   = Path.GetFileName(file),
                Extension          = ext,
                DocumentType       = classifyResult.IsSuccess ? classifyResult.Value : DocumentType.Unknown,
                RedactedContent    = redactionResult.Value.RedactedContent,
                EncryptedPiiJson   = sealedPii,
                PiiSegmentCount    = redactionResult.Value.Segments.Count,
                ContentHash        = RegexPiiRedactionService.Sha256Hex(read.Value)
            };

            await _redactedRepo.CreateAsync(descriptor, ct);

            // Record per-file PiiKind set so the slot catalog can be assembled later.
            if (redactionResult.Value.Segments.Count > 0)
            {
                await _redactedRepo.SaveDetectedPiiKindsAsync(
                    descriptor.Id,
                    redactionResult.Value.Segments.Select(s => s.Kind).Distinct(),
                    ct);
            }

            descriptors.Add(descriptor);
        }

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.Redacted, ct);
        await _audit.LogAsync("CleaningRedacted", "Cleaning",
            cleaningId.ToString(), action: "RedactAndClassify",
            newValues: new { Count = descriptors.Count });

        return Result.Ok<IReadOnlyList<RedactedFileDescriptor>>(descriptors);
    }

    public async Task<Result<DirectoryStructurePlan>> GenerateStructurePlanAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var summariesResult = await _redactedRepo.GetDocumentTypeSummariesAsync(cleaningId, ct);
        if (summariesResult.IsFailed) return Result.Fail(summariesResult.Errors);

        if (summariesResult.Value.Count == 0)
            return Result.Fail("No redacted descriptors exist — run RedactAndClassify first.");

        // Slot catalog tells Claude which PII tokens it may legitimately reference
        // per DocumentType. Failure to load is non-fatal — we just send no catalog.
        var slotResult = await _redactedRepo.GetSlotCatalogAsync(cleaningId, ct);
        var slotCatalog = slotResult.IsSuccess ? slotResult.Value : null;

        var plan = await _claudeStructure.AnalyzeStructureAsync(
            cleaningId, summariesResult.Value, slotCatalog, ct);

        // Replace any prior plan
        await _planRepo.DeleteByCleaningIdAsync(cleaningId, ct);
        var saved = await _planRepo.CreateAsync(plan, ct);
        if (saved.IsFailed) return saved;

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.StructurePlanGenerated, ct);
        await _audit.LogAsync("StructurePlanGenerated", "Cleaning",
            cleaningId.ToString(), action: "GenerateStructure",
            newValues: new { Rules = plan.Rules.Count });

        return Result.Ok(plan);
    }

    public Task<Result<DirectoryStructurePlan>> GetStructurePlanAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _planRepo.GetLatestAsync(cleaningId, ct);

    public Task<Result<IReadOnlyList<FileRelocationRecord>>> ExecuteStructurePlanAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _structureExec.ExecuteStructureAsync(cleaningId, ct);

    public Task<Result<IReadOnlyList<FileRelocationRecord>>> GetRelocationsAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _relocRepo.GetByCleaningIdAsync(cleaningId, ct);

    public async Task<Result<IReadOnlyList<FileRelocationRecord>>> RollbackStructurePlanAsync(
        Guid cleaningId, string userId, CancellationToken ct = default)
    {
        var result = await _structureExec.RollbackAsync(cleaningId, ct);
        if (result.IsSuccess)
        {
            await _audit.LogAsync("StructureRollbackRequested", "Cleaning",
                cleaningId.ToString(), userId: userId, action: "RollbackStructure",
                newValues: new { Total = result.Value.Count });
        }
        return result;
    }

    public Task<Result<Cleaning?>> GetActiveCleaningAsync(CancellationToken ct = default)
        => _cleaningRepo.GetActiveAsync(ct);

    public async Task<Result> ArchiveCleaningAsync(
        Guid cleaningId, string userId, CancellationToken ct = default)
    {
        var archive = await _cleaningRepo.ArchiveAsync(cleaningId, ct);
        if (archive.IsFailed) return archive;

        await _audit.LogAsync("CleaningArchived", "Cleaning",
            cleaningId.ToString(), userId: userId, action: "Archive");
        return Result.Ok();
    }

    // ── Cowork-parity additions ──────────────────────────────────────────────

    public Task<Result<IReadOnlyList<FileRelocationRecord>>> PlanStructureRelocationsAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _structureExec.PlanRelocationsAsync(cleaningId, ct);

    public Task<Result<IReadOnlyList<DuplicateGroup>>> AnalyzeDuplicatesAsync(
        Guid cleaningId, CancellationToken ct = default)
        => _dupes.AnalyzeAsync(cleaningId, ct);

    public Task<Result<IReadOnlyList<PromotionRecord>>> PromoteCleaningAsync(
        Guid cleaningId, string userId, CancellationToken ct = default)
    {
        if (_promotion is null)
        {
            return Task.FromResult(
                Result.Fail<IReadOnlyList<PromotionRecord>>(
                    "Promotion is unavailable on this host (Windows-only feature)."));
        }
        return _promotion.PromoteAsync(cleaningId, userId, ct);
    }

    public Task<Result<IReadOnlyList<PromotionRecord>>> GetPromotionsAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        if (_promotion is null)
        {
            return Task.FromResult(
                Result.Ok<IReadOnlyList<PromotionRecord>>([]));
        }
        return _promotion.GetPromotionsAsync(cleaningId, ct);
    }
}

// ── Extension helpers ────────────────────────────────────────────────────────
internal static class CleaningExtensions
{
    internal static bool IsCompleted(this Cleaning c) =>
        c.Status == CleaningStatus.Completed;
}
