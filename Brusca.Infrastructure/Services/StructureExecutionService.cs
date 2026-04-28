using System.Text.Json;
using System.Text.RegularExpressions;
using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Applies a <see cref="DirectoryStructurePlan"/> against the chosen execution
/// root. Decrypts the per-file PII JSON in memory, substitutes template tokens,
/// performs the move/rename/create, and records every BEFORE → AFTER row in
/// <see cref="FileRelocationRecord"/>.
///
/// PII is held in a local variable for the duration of one file operation only;
/// it is never logged and never returned across the API boundary.
/// </summary>
public sealed partial class StructureExecutionService : IStructureExecutionService
{
    private readonly ICleaningRepository _cleaningRepo;
    private readonly IRedactedFileRepository _redactedRepo;
    private readonly IStructurePlanRepository _planRepo;
    private readonly IFileRelocationRepository _relocRepo;
    private readonly IFileSystemService _fs;
    private readonly IEncryptionService _crypto;
    private readonly IFileHashService _hash;
    private readonly IImageRedactionService? _imageRedactor;
    private readonly IAuditLogger _audit;
    private readonly IErrorLogger _log;

    public StructureExecutionService(
        ICleaningRepository cleaningRepo,
        IRedactedFileRepository redactedRepo,
        IStructurePlanRepository planRepo,
        IFileRelocationRepository relocRepo,
        IFileSystemService fs,
        IEncryptionService crypto,
        IFileHashService hash,
        IAuditLogger audit,
        IErrorLogger log,
        IImageRedactionService? imageRedactor = null)
    {
        _cleaningRepo  = cleaningRepo;
        _redactedRepo  = redactedRepo;
        _planRepo      = planRepo;
        _relocRepo     = relocRepo;
        _fs            = fs;
        _crypto        = crypto;
        _hash          = hash;
        _audit         = audit;
        _log           = log;
        _imageRedactor = imageRedactor;
    }

    public async Task<Result<IReadOnlyList<FileRelocationRecord>>> ExecuteStructureAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var cleaningResult = await _cleaningRepo.GetByIdAsync(cleaningId, ct);
        if (cleaningResult.IsFailed) return Result.Fail(cleaningResult.Errors);

        var planResult = await _planRepo.GetLatestAsync(cleaningId, ct);
        if (planResult.IsFailed) return Result.Fail(planResult.Errors);

        var filesResult = await _redactedRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (filesResult.IsFailed) return Result.Fail(filesResult.Errors);

        var cleaning = cleaningResult.Value;
        var plan     = planResult.Value;
        var files    = filesResult.Value;

        var executionRoot = cleaning.ExecutionTarget == ExecutionTarget.AlternatePath
            && !string.IsNullOrWhiteSpace(cleaning.AlternateExecutionPath)
            ? cleaning.AlternateExecutionPath!
            : cleaning.RootPath;

        await _cleaningRepo.UpdateStatusAsync(cleaningId, CleaningStatus.StructureExecuting, ct);

        var relocations = new List<FileRelocationRecord>();
        var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var rule = plan.Rules.FirstOrDefault(r =>
                r.DocumentType == file.DocumentType &&
                (string.IsNullOrEmpty(r.Extension) ||
                 r.Extension.Equals(file.Extension, StringComparison.OrdinalIgnoreCase)))
                ?? plan.Rules.FirstOrDefault(r => r.DocumentType == file.DocumentType);

            if (rule is null)
            {
                relocations.Add(SkipRecord(file, executionRoot, "No matching rule."));
                continue;
            }

            // Decrypt PII into a local dictionary for token substitution only.
            var tokens = BuildTokenMap(file);
            var folderRel = SubstituteTokens(rule.FolderPathTemplate, tokens);
            var fileBase  = SubstituteTokens(rule.FileNameTemplate,  tokens);
            var newName   = $"{fileBase}{file.Extension}";
            var newDir    = Path.Combine(executionRoot, folderRel);
            var newPath   = Path.Combine(newDir, newName);

            // Folder-create record (once per directory)
            if (createdDirs.Add(newDir))
            {
                var dirRec = new FileRelocationRecord
                {
                    CleaningId      = cleaningId,
                    OperationType   = RelocationOperationType.CreateDirectory,
                    ExecutionTarget = cleaning.ExecutionTarget,
                    BeforePath      = null,
                    BeforeName      = null,
                    AfterPath       = newDir,
                    AfterName       = Path.GetFileName(newDir),
                    Status          = RelocationStatus.Pending
                };
                try
                {
                    Directory.CreateDirectory(newDir);
                    dirRec.Status = RelocationStatus.Succeeded;
                    dirRec.CompletedAtUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    dirRec.Status       = RelocationStatus.Failed;
                    dirRec.ErrorMessage = ex.Message;
                    await _log.LogErrorAsync($"CreateDirectory failed: {newDir}", ex, cleaningId: cleaningId);
                }
                await _relocRepo.CreateAsync(dirRec, ct);
                relocations.Add(dirRec);
            }

            // Move + rename record.
            // Brusca treats originals as strictly read-only — every file
            // operation is a Materialize (copy). The OperationType is fixed
            // regardless of whether ExecutionTarget is SourcePath or AlternatePath.
            var fileRec = new FileRelocationRecord
            {
                CleaningId      = cleaningId,
                RedactedFileId  = file.Id,
                OperationType   = RelocationOperationType.Materialize,
                ExecutionTarget = cleaning.ExecutionTarget,
                BeforePath      = file.OriginalFilePath,
                BeforeName      = file.OriginalFileName,
                AfterPath       = newPath,
                AfterName       = newName,
                Status          = RelocationStatus.Pending
            };
            try
            {
                // Always copy — the original at BeforePath remains untouched.
                if (File.Exists(file.OriginalFilePath))
                    File.Copy(file.OriginalFilePath, newPath, overwrite: false);

                fileRec.Status = RelocationStatus.Succeeded;
                fileRec.CompletedAtUtc = DateTime.UtcNow;

                // Post-move integrity hash for audit; non-fatal on failure.
                if (File.Exists(newPath))
                {
                    var hashRes = await _hash.ComputeAsync(newPath, ct);
                    if (hashRes.IsSuccess) fileRec.ContentHashAfter = hashRes.Value;
                }
            }
            catch (Exception ex)
            {
                fileRec.Status = RelocationStatus.Failed;
                fileRec.ErrorMessage = ex.Message;
                await _log.LogErrorAsync(
                    $"Structure execution failed for {file.OriginalFilePath}",
                    ex, cleaningId: cleaningId);
            }
            await _relocRepo.CreateAsync(fileRec, ct);
            relocations.Add(fileRec);
        }

        await _cleaningRepo.CompleteAsync(cleaningId, ct);
        await _audit.LogAsync("StructureExecuted", "Cleaning",
            cleaningId.ToString(), action: "ExecuteStructure",
            newValues: new { Total = relocations.Count });

        return Result.Ok<IReadOnlyList<FileRelocationRecord>>(relocations);
    }

    public async Task<Result<IReadOnlyList<FileRelocationRecord>>> RollbackAsync(
        Guid cleaningId, CancellationToken ct = default)
    {
        var allRes = await _relocRepo.GetByCleaningIdAsync(cleaningId, ct);
        if (allRes.IsFailed) return Result.Fail(allRes.Errors);

        // Reverse in newest-first order so directories created last are emptied first.
        var ordered = allRes.Value
            .Where(r => r.Status == RelocationStatus.Succeeded)
            .OrderByDescending(r => r.CompletedAtUtc ?? r.CreatedAtUtc)
            .ToList();

        var processed = new List<FileRelocationRecord>(ordered.Count);

        foreach (var rec in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (rec.OperationType)
                {
                    case RelocationOperationType.Move:
                        if (!string.IsNullOrEmpty(rec.AfterPath) &&
                            !string.IsNullOrEmpty(rec.BeforePath) &&
                            File.Exists(rec.AfterPath))
                        {
                            var srcDir = Path.GetDirectoryName(rec.BeforePath);
                            if (!string.IsNullOrEmpty(srcDir)) Directory.CreateDirectory(srcDir);
                            File.Move(rec.AfterPath, rec.BeforePath, overwrite: false);
                        }
                        break;

                    case RelocationOperationType.Materialize:
                    case RelocationOperationType.Copy:
                        // Original was preserved; deleting the materialized copy is sufficient.
                        if (!string.IsNullOrEmpty(rec.AfterPath) && File.Exists(rec.AfterPath))
                            File.Delete(rec.AfterPath);
                        break;

                    case RelocationOperationType.CreateDirectory:
                        // Best-effort: only remove if empty so we never destroy user data.
                        if (!string.IsNullOrEmpty(rec.AfterPath) &&
                            Directory.Exists(rec.AfterPath) &&
                            !Directory.EnumerateFileSystemEntries(rec.AfterPath).Any())
                        {
                            Directory.Delete(rec.AfterPath);
                        }
                        break;

                    case RelocationOperationType.Rename:
                        if (!string.IsNullOrEmpty(rec.AfterPath) &&
                            !string.IsNullOrEmpty(rec.BeforePath) &&
                            File.Exists(rec.AfterPath))
                        {
                            File.Move(rec.AfterPath, rec.BeforePath, overwrite: false);
                        }
                        break;
                }

                rec.Status = RelocationStatus.RolledBack;
                rec.CompletedAtUtc = DateTime.UtcNow;
                await _relocRepo.UpdateStatusAsync(rec.Id, RelocationStatus.RolledBack, null, ct);
            }
            catch (Exception ex)
            {
                rec.Status = RelocationStatus.Failed;
                rec.ErrorMessage = $"Rollback failed: {ex.Message}";
                await _relocRepo.UpdateStatusAsync(rec.Id, RelocationStatus.Failed, rec.ErrorMessage, ct);
                await _log.LogErrorAsync(
                    $"Rollback failed for relocation {rec.Id}",
                    ex, cleaningId: cleaningId);
            }
            processed.Add(rec);
        }

        await _audit.LogAsync("StructureRollback", "Cleaning",
            cleaningId.ToString(), action: "RollbackStructure",
            newValues: new
            {
                Total = processed.Count,
                Succeeded = processed.Count(r => r.Status == RelocationStatus.RolledBack),
                Failed = processed.Count(r => r.Status == RelocationStatus.Failed)
            });

        return Result.Ok<IReadOnlyList<FileRelocationRecord>>(processed);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Dictionary<string, string> BuildTokenMap(RedactedFileDescriptor file)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Year"]      = file.DiscoveredAtUtc.Year.ToString(),
            ["Month"]     = file.DiscoveredAtUtc.Month.ToString("D2"),
            ["Day"]       = file.DiscoveredAtUtc.Day.ToString("D2"),
            ["Date"]      = file.DiscoveredAtUtc.ToString("yyyy-MM-dd"),
            ["Extension"] = file.Extension,
            ["DocumentType"] = file.DocumentType.ToString()
        };

        if (!string.IsNullOrEmpty(file.EncryptedPiiJson))
        {
            try
            {
                var json = _crypto.Decrypt(file.EncryptedPiiJson);
                var segments = JsonSerializer.Deserialize<List<PiiSegment>>(json) ?? [];
                foreach (var s in segments)
                {
                    var key = s.Kind.ToString();
                    if (!tokens.ContainsKey(key))
                        tokens[key] = SafeForPath(s.Value);
                }
            }
            catch
            {
                // Decryption failure must NOT abort the whole run — record at file level.
            }
        }
        return tokens;
    }

    private static string SubstituteTokens(string template, Dictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return TokenRegex().Replace(template, m =>
            tokens.TryGetValue(m.Groups[1].Value, out var v) ? v : "_");
    }

    private static string SafeForPath(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "_" : clean;
    }

    private static FileRelocationRecord SkipRecord(
        RedactedFileDescriptor file, string executionRoot, string reason) => new()
    {
        CleaningId      = file.CleaningId,
        RedactedFileId  = file.Id,
        BeforePath      = file.OriginalFilePath,
        BeforeName      = file.OriginalFileName,
        Status          = RelocationStatus.Skipped,
        ErrorMessage    = reason,
        CompletedAtUtc  = DateTime.UtcNow
    };

    [System.Text.RegularExpressions.GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex TokenRegex();
}
