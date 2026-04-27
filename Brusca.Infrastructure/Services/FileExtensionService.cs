using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Extensions;
using FluentResults;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Manages the master file extension list — syncing from scans and tracking unknown types.
/// </summary>
public sealed class FileExtensionService : IFileExtensionService
{
    private readonly IFileExtensionRepository _repo;

    // Known extensions with built-in readers (expand as NuGets are added)
    private static readonly HashSet<string> _builtInExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".yaml", ".yml",
        ".pdf",   // PdfPig
        ".docx", ".xlsx", ".pptx",  // DocumentFormat.OpenXml
        ".html", ".htm",
        ".cs", ".ts", ".js", ".py", ".sql", ".sh", ".bat", ".ps1",
        ".log", ".ini", ".toml", ".cfg", ".config"
    };

    public FileExtensionService(IFileExtensionRepository repo)
    {
        _repo = repo;
    }

    public Task<Result<IReadOnlyList<FileExtensionRecord>>> GetMasterListAsync(CancellationToken ct = default)
        => _repo.GetAllAsync(ct);

    public async Task<Result> SyncFromScanAsync(ExtensionScanResult scanResult, CancellationToken ct = default)
    {
        var records = scanResult.AllExtensions.Select(ext => new FileExtensionRecord
        {
            Extension = ext.ToLowerInvariant(),
            Status = _builtInExtensions.Contains(ext)
                ? FileExtensionStatus.Known
                : FileExtensionStatus.Unknown,
            TotalTimesEncountered = 1,
            LastSeenUtc = DateTime.UtcNow
        }).ToList();

        return await _repo.BulkUpsertAsync(records, ct);
    }

    public async Task<Result<IReadOnlyList<string>>> GetUnknownExtensionsAsync(
        IEnumerable<string> extensions, CancellationToken ct = default)
    {
        var unknown = extensions
            .Where(e => !_builtInExtensions.Contains(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Result.Ok<IReadOnlyList<string>>(unknown);
    }

    public Task<Result> RegisterPackageForExtensionAsync(
        string extension, string nuGetPackage, CancellationToken ct = default)
        => _repo.UpdateStatusAsync(extension, FileExtensionStatus.PendingPackage, nuGetPackage, ct);
}
