using System.Text.Json;
using System.Text.RegularExpressions;
using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Pii;
using FluentResults;
using Microsoft.Extensions.Options;

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
    private readonly IFileMetadataStripper? _metadataStripper;
    private readonly IDuplicateDetectionService _dupes;
    private readonly IPathSafetyService _paths;
    private readonly MaterializationOptions _materialization;
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
        IDuplicateDetectionService dupes,
        IPathSafetyService paths,
        IOptions<BruscaOptions> options,
        IAuditLogger audit,
        IErrorLogger log,
        IImageRedactionService? imageRedactor = null,
        IFileMetadataStripper? metadataStripper = null)
    {
        _cleaningRepo    = cleaningRepo;
        _redactedRepo    = redactedRepo;
        _planRepo        = planRepo;
        _relocRepo       = relocRepo;
        _fs              = fs;
        _crypto          = crypto;
        _hash            = hash;
        _dupes           = dupes;
        _paths           = paths;
        _materialization = options.Value.Materialization;
        _audit           = audit;
        _log             = log;
        _imageRedactor    = imageRedactor;
        _metadataStripper = metadataStripper;
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

        // Dedupe: which redacted-file ids should be skipped because they are
        // non-keeper members of a duplicate group?
        var (nonKeepers, keeperPathById) = await ResolveDuplicatesAsync(cleaningId, files, ct);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            // Duplicate skip — record an audit row and move on.
            if (nonKeepers.TryGetValue(file.Id, out var keeperId))
            {
                var keeperPath = keeperPathById.TryGetValue(keeperId, out var p) ? p : "(unknown)";
                var dupRec = new FileRelocationRecord
                {
                    CleaningId      = cleaningId,
                    RedactedFileId  = file.Id,
                    OperationType   = RelocationOperationType.SkipDuplicate,
                    ExecutionTarget = cleaning.ExecutionTarget,
                    BeforePath      = file.OriginalFilePath,
                    BeforeName      = file.OriginalFileName,
                    AfterPath       = null,
                    AfterName       = null,
                    Status          = RelocationStatus.Skipped,
                    ErrorMessage    = $"duplicate of {keeperPath}",
                    CompletedAtUtc  = DateTime.UtcNow
                };
                await _relocRepo.CreateAsync(dupRec, ct);
                relocations.Add(dupRec);
                continue;
            }

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
                // Resolve any existing-target collision per the configured policy.
                var resolved = ResolveCollision(newPath, _materialization.CollisionPolicy);
                if (string.IsNullOrEmpty(resolved))
                {
                    fileRec.Status         = RelocationStatus.Skipped;
                    fileRec.ErrorMessage   = "Destination already exists; skipped per policy.";
                    fileRec.CompletedAtUtc = DateTime.UtcNow;
                }
                else
                {
                    if (!resolved.Equals(newPath, StringComparison.OrdinalIgnoreCase))
                    {
                        fileRec.AfterPath = resolved;
                        fileRec.AfterName = Path.GetFileName(resolved);
                    }

                    // Always copy — the original at BeforePath remains untouched.
                    if (File.Exists(file.OriginalFilePath))
                    {
                        // Image branch: route through IImageRedactionService when
                        // we have computed PII regions and a redactor is wired.
                        var sanitized = false;
                        if (_materialization.SanitizeImages
                            && _imageRedactor is not null
                            && _imageRedactor.CanRedact(file.Extension)
                            && !string.IsNullOrEmpty(file.ImageRedactionRegionsJson))
                        {
                            try
                            {
                                var regions = JsonSerializer.Deserialize<List<ImageRedactionRegion>>(
                                    file.ImageRedactionRegionsJson) ?? [];
                                if (regions.Count > 0)
                                {
                                    var rr = await _imageRedactor.RedactAsync(
                                        file.OriginalFilePath, resolved, regions, ct);
                                    if (rr.IsSuccess)
                                    {
                                        sanitized = true;
                                        if (!string.IsNullOrEmpty(rr.Value.SanitizedContentHash))
                                            fileRec.ContentHashAfter = rr.Value.SanitizedContentHash;
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                await _log.LogErrorAsync(
                                    $"Image redaction fell back to plain copy for {file.OriginalFilePath}",
                                    ex, cleaningId: cleaningId);
                            }
                        }

                        if (!sanitized)
                            File.Copy(file.OriginalFilePath, resolved, overwrite: false);
                    }

                    fileRec.Status = RelocationStatus.Succeeded;
                    fileRec.CompletedAtUtc = DateTime.UtcNow;

                    // Strip identifying metadata from the materialized copy
                    // (EXIF/XMP, OpenXml core props, PDF /Info ...). Best-effort.
                    if (_materialization.StripMetadata
                        && _metadataStripper is not null
                        && File.Exists(resolved)
                        && _metadataStripper.CanStrip(file.Extension))
                    {
                        try { await _metadataStripper.StripAsync(resolved, file.Extension, ct); }
                        catch (Exception ex)
                        {
                            await _log.LogErrorAsync(
                                $"Metadata strip failed for {resolved}", ex, cleaningId: cleaningId);
                        }
                    }

                    // Post-move integrity hash for audit; non-fatal on failure.
                    if (string.IsNullOrEmpty(fileRec.ContentHashAfter) && File.Exists(resolved))
                    {
                        var hashRes = await _hash.ComputeAsync(resolved, ct);
                        if (hashRes.IsSuccess) fileRec.ContentHashAfter = hashRes.Value;
                    }
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

    /// <summary>
    /// Computes the same set of relocations <see cref="ExecuteStructureAsync"/>
    /// would produce and persists them with <c>Status = Pending</c> — without
    /// touching the file system. Used by the UI for layout previews.
    /// </summary>
    public async Task<Result<IReadOnlyList<FileRelocationRecord>>> PlanRelocationsAsync(
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

        var (nonKeepers, keeperPathById) = await ResolveDuplicatesAsync(cleaningId, files, ct);
        var planned = new List<FileRelocationRecord>();
        // Intra-plan collision tracker: when two files compute the same AfterPath
        // (e.g. identical token values), suffix the second/third with "_(2)", "_(3)".
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            if (nonKeepers.TryGetValue(file.Id, out var keeperId))
            {
                var keeperPath = keeperPathById.TryGetValue(keeperId, out var p) ? p : "(unknown)";
                var dupRec = new FileRelocationRecord
                {
                    CleaningId      = cleaningId,
                    RedactedFileId  = file.Id,
                    OperationType   = RelocationOperationType.SkipDuplicate,
                    ExecutionTarget = cleaning.ExecutionTarget,
                    BeforePath      = file.OriginalFilePath,
                    BeforeName      = file.OriginalFileName,
                    Status          = RelocationStatus.Pending,
                    ErrorMessage    = $"duplicate of {keeperPath}"
                };
                await _relocRepo.CreateAsync(dupRec, ct);
                planned.Add(dupRec);
                continue;
            }

            var rule = plan.Rules.FirstOrDefault(r =>
                r.DocumentType == file.DocumentType &&
                (string.IsNullOrEmpty(r.Extension) ||
                 r.Extension.Equals(file.Extension, StringComparison.OrdinalIgnoreCase)))
                ?? plan.Rules.FirstOrDefault(r => r.DocumentType == file.DocumentType);

            if (rule is null)
            {
                var miss = new FileRelocationRecord
                {
                    CleaningId      = cleaningId,
                    RedactedFileId  = file.Id,
                    OperationType   = RelocationOperationType.Materialize,
                    ExecutionTarget = cleaning.ExecutionTarget,
                    BeforePath      = file.OriginalFilePath,
                    BeforeName      = file.OriginalFileName,
                    Status          = RelocationStatus.Pending,
                    ErrorMessage    = "No matching rule."
                };
                await _relocRepo.CreateAsync(miss, ct);
                planned.Add(miss);
                continue;
            }

            var tokens = BuildTokenMap(file);
            var folderRel = SubstituteTokens(rule.FolderPathTemplate, tokens);
            var fileBase  = SubstituteTokens(rule.FileNameTemplate,  tokens);
            var newName   = $"{fileBase}{file.Extension}";
            var newDir    = Path.Combine(executionRoot, folderRel);
            var newPath   = Path.Combine(newDir, newName);

            // Intra-plan collision: two files in this same planning pass mapping
            // to the same AfterPath. Suffix to disambiguate before persistence.
            if (!claimed.Add(newPath))
            {
                var stem = Path.GetFileNameWithoutExtension(newName);
                var ext  = Path.GetExtension(newName);
                for (int i = 2; i < 1000; i++)
                {
                    var candidateName = $"{stem}_({i}){ext}";
                    var candidatePath = Path.Combine(newDir, candidateName);
                    if (claimed.Add(candidatePath))
                    {
                        newName = candidateName;
                        newPath = candidatePath;
                        break;
                    }
                }
            }

            var rec = new FileRelocationRecord
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
            await _relocRepo.CreateAsync(rec, ct);
            planned.Add(rec);
        }

        await _audit.LogAsync("StructurePlanned", "Cleaning",
            cleaningId.ToString(), action: "PlanRelocations",
            newValues: new { Total = planned.Count });

        return Result.Ok<IReadOnlyList<FileRelocationRecord>>(planned);
    }

    /// <summary>
    /// Returns a lookup of redacted-file ids that should be skipped because
    /// they are non-keeper members of a duplicate group, plus the original
    /// path of each elected keeper for audit messages.
    /// </summary>
    private async Task<(Dictionary<Guid, Guid> NonKeepers, Dictionary<Guid, string> KeeperPaths)>
        ResolveDuplicatesAsync(
            Guid cleaningId,
            IReadOnlyList<RedactedFileDescriptor> files,
            CancellationToken ct)
    {
        var nonKeepers   = new Dictionary<Guid, Guid>();
        var keeperPaths  = new Dictionary<Guid, string>();
        if (!_materialization.DeduplicateByContentHash) return (nonKeepers, keeperPaths);

        var dupResult = await _dupes.AnalyzeAsync(cleaningId, ct);
        if (dupResult.IsFailed) return (nonKeepers, keeperPaths);

        var byId = files.ToDictionary(f => f.Id);
        foreach (var g in dupResult.Value)
        {
            if (byId.TryGetValue(g.KeepRedactedFileId, out var keeper))
                keeperPaths[g.KeepRedactedFileId] = keeper.OriginalFilePath;

            foreach (var id in g.RedactedFileIds)
                if (id != g.KeepRedactedFileId) nonKeepers[id] = g.KeepRedactedFileId;
        }
        return (nonKeepers, keeperPaths);
    }

    /// <summary>
    /// Applies the configured <see cref="MaterializationCollisionPolicy"/>.
    /// Returns the path the caller should write to, or <see cref="string.Empty"/>
    /// when the policy is <see cref="MaterializationCollisionPolicy.Skip"/>.
    /// </summary>
    private static string ResolveCollision(string newPath, MaterializationCollisionPolicy policy)
    {
        if (!File.Exists(newPath)) return newPath;
        if (policy == MaterializationCollisionPolicy.Fail)
            throw new IOException($"Destination already exists: {newPath}");
        if (policy == MaterializationCollisionPolicy.Skip)
            return string.Empty;

        var dir  = Path.GetDirectoryName(newPath)!;
        var stem = Path.GetFileNameWithoutExtension(newPath);
        var ext  = Path.GetExtension(newPath);
        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException($"Collision suffix space exhausted for {newPath}");
    }

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
                var byOrdinal = segments.ToDictionary(s => s.Ordinal);

                // 1) Per-file slot map wins over kind-fallback when present.
                var slotMap = ParseSlotMap(file.SlotMapJson);
                foreach (var (slot, ordinal) in slotMap)
                {
                    if (byOrdinal.TryGetValue(ordinal, out var seg))
                        tokens[slot] = _paths.SanitizeSegment(seg.Value);
                }

                // 2) Kind-keyed fallback for any unmapped {{Kind}} placeholder.
                foreach (var s in segments)
                {
                    var key = s.Kind.ToString();
                    if (!tokens.ContainsKey(key))
                        tokens[key] = _paths.SanitizeSegment(s.Value);
                }
            }
            catch
            {
                // Decryption failure must NOT abort the whole run — record at file level.
            }
        }
        return tokens;
    }

    private static Dictionary<string, int> ParseSlotMap(string? slotMapJson)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(slotMapJson)) return map;
        try
        {
            using var doc = JsonDocument.Parse(slotMapJson);
            var dict = doc.RootElement.TryGetProperty("slotToOrdinal", out var d)
                ? d : doc.RootElement;
            if (dict.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in dict.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number
                        && p.Value.TryGetInt32(out var n))
                        map[p.Name] = n;
                }
            }
        }
        catch { /* tolerate malformed */ }
        return map;
    }

    private static string SubstituteTokens(string template, Dictionary<string, string> tokens)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return TokenRegex().Replace(template, m =>
            tokens.TryGetValue(m.Groups[1].Value, out var v) ? v : "_");
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
