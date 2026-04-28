using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models;
using Brusca.Core.Models.Cleaning;
using Brusca.Core.Models.Extensions;
using FluentResults;
using Microsoft.Extensions.Options;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Walks file system paths (local or UNC network shares) to discover
/// file extensions and build directory trees.
/// </summary>
public sealed class FileSystemService : IFileSystemService
{
    private readonly FileSystemOptions _opts;
    private readonly IErrorLogger _log;

    public FileSystemService(IOptions<BruscaOptions> options, IErrorLogger log)
    {
        _opts = options.Value.FileSystem;
        _log = log;
    }

    public async Task<Result<ExtensionScanResult>> ScanForExtensionsAsync(
        string rootPath, Guid cleaningId, CancellationToken ct = default)
    {
        try
        {
            var extensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var counters = new ScanCounters();

            await WalkAsync(rootPath, 0, extensions, counters, ct);

            return Result.Ok(new ExtensionScanResult
            {
                CleaningId = cleaningId,
                AllExtensions = extensions.Keys.OrderBy(e => e).ToList(),
                TotalFileCount = counters.Files,
                TotalDirectoryCount = counters.Directories
            });
        }
        catch (Exception ex)
        {
            await _log.LogErrorAsync("Extension scan failed", ex, cleaningId: cleaningId);
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public Task<Result<DirectoryNode>> BuildDirectoryTreeAsync(
        string rootPath, CancellationToken ct = default)
    {
        try
        {
            var node = BuildNode(rootPath, 0);
            return Task.FromResult(Result.Ok(node));
        }
        catch (Exception ex)
        {
            _log.LogErrorAsync("Directory tree build failed", ex);
            return Task.FromResult(Result.Fail<DirectoryNode>(new ExceptionalError(ex)));
        }
    }

    public async Task<Result<string>> ReadFileContentAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length > _opts.MaxFileSizeBytes)
                return Result.Fail($"File exceeds max read size ({_opts.MaxFileSizeBytes} bytes): {filePath}");

            return Result.Ok(await File.ReadAllTextAsync(filePath, ct));
        }
        catch (Exception ex)
        {
            await _log.LogErrorAsync($"Read failed: {filePath}", ex);
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result> RenameAsync(string sourcePath, string targetPath, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(sourcePath))
                File.Move(sourcePath, targetPath);
            else if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, targetPath);
            else
                return Result.Fail($"Path not found: {sourcePath}");

            return Result.Ok();
        }
        catch (Exception ex)
        {
            await _log.LogErrorAsync($"Rename failed: {sourcePath} -> {targetPath}", ex);
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public async Task<Result> MoveAsync(string sourcePath, string targetDirectory, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            var target = Path.Combine(targetDirectory, Path.GetFileName(sourcePath));
            File.Move(sourcePath, target);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            await _log.LogErrorAsync($"Move failed: {sourcePath}", ex);
            return Result.Fail(new ExceptionalError(ex));
        }
    }

    public bool IsNetworkShare(string path) =>
        path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("//", StringComparison.OrdinalIgnoreCase);

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task WalkAsync(
        string dir, int depth,
        Dictionary<string, int> extensions,
        ScanCounters counters,
        CancellationToken ct)
    {
        if (depth > _opts.MaxDepth || ct.IsCancellationRequested) return;

        var dirName = Path.GetFileName(dir);
        if (_opts.IgnoredDirectories.Contains(dirName, StringComparer.OrdinalIgnoreCase)) return;

        counters.Directories++;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            counters.Files++;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext))
                extensions[ext] = extensions.GetValueOrDefault(ext) + 1;
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
            await WalkAsync(sub, depth + 1, extensions, counters, ct);
    }

    private sealed class ScanCounters
    {
        public int Files;
        public int Directories;
    }

    private DirectoryNode BuildNode(string path, int depth)
    {
        if (depth > _opts.MaxDepth)
            return new DirectoryNode { FullPath = path, Name = Path.GetFileName(path), Depth = depth };

        var node = new DirectoryNode
        {
            FullPath = path,
            Name = Path.GetFileName(path),
            Depth = depth
        };

        foreach (var file in Directory.EnumerateFiles(path))
        {
            node.FileCount++;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (!node.Extensions.Contains(ext))
                node.Extensions.Add(ext);
        }

        foreach (var sub in Directory.EnumerateDirectories(path))
        {
            if (_opts.IgnoredDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                continue;
            node.Children.Add(BuildNode(sub, depth + 1));
        }

        return node;
    }
}
