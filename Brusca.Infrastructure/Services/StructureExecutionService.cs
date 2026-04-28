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
    private readonly IAuditLogger _audit;
    private readonly IErrorLogger _log;

    public StructureExecutionService(
        ICleaningRepository cleaningRepo,
        IRedactedFileRepository redactedRepo,
        IStructurePlanRepository planRepo,
        IFileRelocationRepository relocRepo,
        IFileSystemService fs,
        IEncryptionService crypto,
        IAuditLogger audit,
        IErrorLogger log)
    {
        _cleaningRepo = cleaningRepo;
        _redactedRepo = redactedRepo;
        _planRepo     = planRepo;
        _relocRepo    = relocRepo;
        _fs           = fs;
        _crypto       = crypto;
        _audit        = audit;
        _log          = log;
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

            // Move + rename record
            var fileRec = new FileRelocationRecord
            {
                CleaningId      = cleaningId,
                RedactedFileId  = file.Id,
                OperationType   = cleaning.ExecutionTarget == ExecutionTarget.SourcePath
                                  ? RelocationOperationType.Move
                                  : RelocationOperationType.Materialize,
                ExecutionTarget = cleaning.ExecutionTarget,
                BeforePath      = file.OriginalFilePath,
                BeforeName      = file.OriginalFileName,
                AfterPath       = newPath,
                AfterName       = newName,
                Status          = RelocationStatus.Pending
            };
            try
            {
                if (cleaning.ExecutionTarget == ExecutionTarget.SourcePath)
                {
                    if (File.Exists(file.OriginalFilePath))
                        File.Move(file.OriginalFilePath, newPath, overwrite: false);
                }
                else
                {
                    if (File.Exists(file.OriginalFilePath))
                        File.Copy(file.OriginalFilePath, newPath, overwrite: false);
                }
                fileRec.Status = RelocationStatus.Succeeded;
                fileRec.CompletedAtUtc = DateTime.UtcNow;
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
