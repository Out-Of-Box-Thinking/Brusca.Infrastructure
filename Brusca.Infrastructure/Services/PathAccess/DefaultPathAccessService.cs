using Brusca.Core.Contracts.Logging;
using Brusca.Core.Contracts.Repositories;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.PathAccess;
using FluentResults;

namespace Brusca.Infrastructure.Services.PathAccess;

/// <summary>
/// Default <see cref="IPathAccessService"/>. Probes a path against the
/// host's filesystem, detects "client-local-only" shapes the server can
/// never reach, and delegates remote-share mounting to the registered
/// <see cref="IPlatformShareMounter"/>s. Credentials are stored encrypted
/// via <see cref="IEncryptionService"/> and purged on archive.
/// </summary>
public sealed class DefaultPathAccessService : IPathAccessService
{
    private readonly IEnumerable<IPlatformShareMounter> _mounters;
    private readonly IPathCredentialRepository _credRepo;
    private readonly IEncryptionService _crypto;
    private readonly IAuditLogger _audit;

    public DefaultPathAccessService(
        IEnumerable<IPlatformShareMounter> mounters,
        IPathCredentialRepository credRepo,
        IEncryptionService crypto,
        IAuditLogger audit)
    {
        _mounters = mounters;
        _credRepo = credRepo;
        _crypto = crypto;
        _audit = audit;
    }

    public Task<Result<PathProbeResult>> ProbeAsync(
        string path, CancellationToken ct = default)
    {
        var result = new PathProbeResult { Path = path };

        if (string.IsNullOrWhiteSpace(path))
        {
            result.Reason = "Path is empty.";
            return Task.FromResult(Result.Ok(result));
        }

        // Direct existence check — works for server-local AND already-mounted shares.
        bool exists = false;
        try { exists = Directory.Exists(path); }
        catch { exists = false; }

        if (exists)
        {
            result.IsReachable = true;
            return Task.FromResult(Result.Ok(result));
        }

        // Looks like a remote share?
        bool looksRemote =
            path.StartsWith(@"\\") ||
            path.StartsWith("//") ||
            path.StartsWith("smb://") ||
            path.StartsWith("nfs://");

        if (looksRemote)
        {
            result.RequiresCredentials = true;
            result.Reason = "Remote share is not reachable from the server. Provide credentials or pre-mount it.";
            return Task.FromResult(Result.Ok(result));
        }

        // Looks like a local-rooted path on the WRONG side — i.e. the user's box.
        bool driveLetterShape =
            path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/');
        bool unixHomeShape =
            path.StartsWith("/Users/") || path.StartsWith("/home/");

        if (driveLetterShape && !OperatingSystem.IsWindows())
        {
            result.IsLikelyClientLocal = true;
            result.Reason = "This path uses a Windows drive letter but the server is not running on Windows. " +
                            "Share the folder over SMB/NFS or upload it; browsers cannot expose a local filesystem.";
            return Task.FromResult(Result.Ok(result));
        }
        if (unixHomeShape && OperatingSystem.IsWindows())
        {
            result.IsLikelyClientLocal = true;
            result.Reason = "This path looks like a Unix home directory but the server is running on Windows. " +
                            "Share the folder over SMB/NFS or upload it; browsers cannot expose a local filesystem.";
            return Task.FromResult(Result.Ok(result));
        }

        // Same-OS local path that doesn't exist on the server — treat as client-local.
        if (driveLetterShape || unixHomeShape || path.StartsWith("/"))
        {
            result.IsLikelyClientLocal = true;
            result.Reason = "Path is not visible from the server. " +
                            "If this folder lives on your machine, share it over SMB/NFS or copy it to the server.";
            return Task.FromResult(Result.Ok(result));
        }

        result.Reason = "Path is unreachable.";
        return Task.FromResult(Result.Ok(result));
    }

    public async Task<Result<PathMountHandle>> MountAsync(
        Guid cleaningId, string path, CancellationToken ct = default)
    {
        // Class-1: server-local — no platform call needed.
        if (Directory.Exists(path))
        {
            return Result.Ok(new PathMountHandle
            {
                CleaningId = cleaningId,
                RemotePath = path,
                LocalMountPoint = path,
                IsNoOp = true,
            });
        }

        var mounter = _mounters.FirstOrDefault(m => m.IsApplicable(path));
        if (mounter is null)
            return Result.Fail<PathMountHandle>(
                "No platform mounter is registered for this path shape.");

        // Look up saved credentials (may be null — try ambient identity first).
        var credResult = await _credRepo.GetAsync(cleaningId, path, ct);
        PathCredentials? creds = null;
        if (credResult.IsSuccess && credResult.Value is { } row)
        {
            creds = new PathCredentials
            {
                Username = row.Username,
                Password = _crypto.Decrypt(row.EncryptedPassword),
                Domain = row.Domain,
            };
        }

        var mountResult = await mounter.MountAsync(path, creds, cleaningId, ct);
        if (mountResult.IsFailed)
        {
            await _audit.LogAsync(
                eventType: "PathAccess.MountFailed",
                entityType: nameof(Brusca.Core.Models.Cleaning.Cleaning),
                entityId: cleaningId.ToString(),
                action: "Mount",
                newValues: new
                {
                    path,
                    reason = string.Join("; ", mountResult.Errors.Select(e => e.Message)),
                },
                ct: ct);
            return Result.Fail<PathMountHandle>(mountResult.Errors);
        }

        await _audit.LogAsync(
            eventType: "PathAccess.Mounted",
            entityType: nameof(Brusca.Core.Models.Cleaning.Cleaning),
            entityId: cleaningId.ToString(),
            action: "Mount",
            newValues: new
            {
                path,
                local = mountResult.Value,
                withCredentials = creds is not null,
            },
            ct: ct);

        return Result.Ok(new PathMountHandle
        {
            CleaningId = cleaningId,
            RemotePath = path,
            LocalMountPoint = mountResult.Value,
            IsNoOp = false,
        });
    }

    public async Task<Result> UnmountAsync(Guid cleaningId, CancellationToken ct = default)
    {
        foreach (var mounter in _mounters)
            await mounter.UnmountAsync(cleaningId, ct);
        return Result.Ok();
    }

    public async Task<Result> SaveCredentialsAsync(
        Guid cleaningId, string path, PathCredentials credentials,
        CancellationToken ct = default)
    {
        if (credentials is null) return Result.Fail("Credentials are required.");
        if (string.IsNullOrWhiteSpace(credentials.Username))
            return Result.Fail("Username is required.");

        var record = new PathCredentialRecord
        {
            CleaningId = cleaningId,
            RootPath = path,
            Username = credentials.Username,
            EncryptedPassword = _crypto.Encrypt(credentials.Password ?? string.Empty),
            Domain = credentials.Domain,
        };
        var saved = await _credRepo.SaveAsync(record, ct);
        if (saved.IsFailed) return saved;

        await _audit.LogAsync(
            eventType: "PathAccess.CredentialsSaved",
            entityType: nameof(Brusca.Core.Models.Cleaning.Cleaning),
            entityId: cleaningId.ToString(),
            action: "SaveCredentials",
            newValues: new
            {
                path,
                username = credentials.Username,
                domain = credentials.Domain,
            },
            ct: ct);
        return Result.Ok();
    }

    public Task<Result> PurgeCredentialsAsync(Guid cleaningId, CancellationToken ct = default)
        => _credRepo.DeleteByCleaningIdAsync(cleaningId, ct);
}
