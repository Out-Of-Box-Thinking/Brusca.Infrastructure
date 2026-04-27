using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Extensions;
using Brusca.Infrastructure.Claude;
using FluentResults;
using System.Text.Json;

namespace Brusca.Infrastructure.Services;

public sealed class CleaningService : ICleaningService
{
    private readonly ICleaningRepository _cleaningRepo;
    private readonly IPromptStepRepository _promptRepo;
    private readonly IPromptStepCommandRepository _cmdRepo;
    private readonly IFileSystemService _fs;
    private readonly IFileExtensionService _extService;
    private readonly ITreeProjectionService _projection;
    private readonly ClaudePromptService _claude;
    private readonly IAuditLogger _audit;
    private readonly IErrorLogger _log;

    public CleaningService(
        ICleaningRepository cleaningRepo,
        IPromptStepRepository promptRepo,
        IPromptStepCommandRepository cmdRepo,
        IFileSystemService fs,
        IFileExtensionService extService,
        ITreeProjectionService projection,
        ClaudePromptService claude,
        IAuditLogger audit,
        IErrorLogger log)
    {
        _cleaningRepo = cleaningRepo;
        _promptRepo   = promptRepo;
        _cmdRepo      = cmdRepo;
        _fs           = fs;
        _extService   = extService;
        _projection   = projection;
        _claude       = claude;
        _audit        = audit;
        _log          = log;
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
}

// ── Extension helpers ────────────────────────────────────────────────────────
internal static class CleaningExtensions
{
    internal static bool IsCompleted(this Cleaning c) =>
        c.Status == CleaningStatus.Completed;
}
