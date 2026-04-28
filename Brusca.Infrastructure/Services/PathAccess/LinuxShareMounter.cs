using System.Diagnostics;
using System.Runtime.Versioning;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.PathAccess;
using FluentResults;

namespace Brusca.Infrastructure.Services.PathAccess;

/// <summary>
/// Best-effort SMB share mounter for Linux. Delegates to <c>mount.cifs</c>
/// for paths shaped <c>//server/share[/...]</c>. Requires that the host
/// either runs as root or has <c>mount.cifs</c> set-uid (default on most
/// distributions). When neither is true the call returns a failure that
/// the API layer surfaces verbatim to the UI.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxShareMounter : IPlatformShareMounter
{
    private const string MountRoot = "/tmp/brusca-mounts";

    private readonly Dictionary<Guid, List<string>> _mountsByCleaning = new();
    private readonly object _gate = new();

    public bool IsApplicable(string path) =>
        !string.IsNullOrEmpty(path) && (path.StartsWith("//") || path.StartsWith("smb://"));

    public async Task<Result<string>> MountAsync(
        string path, PathCredentials? credentials, Guid cleaningId,
        CancellationToken ct = default)
    {
        try
        {
            var share = path.StartsWith("smb://")
                ? "//" + path.Substring("smb://".Length)
                : path;
            var local = Path.Combine(MountRoot, cleaningId.ToString("N"));
            Directory.CreateDirectory(local);

            var opts = new List<string> { "vers=3.0", "sec=ntlmssp" };
            if (credentials is not null)
            {
                opts.Add($"user={credentials.Username}");
                if (!string.IsNullOrEmpty(credentials.Domain))
                    opts.Add($"domain={credentials.Domain}");
                opts.Add($"pass={credentials.Password}");
            }
            else opts.Add("guest");

            var psi = new ProcessStartInfo("mount.cifs")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add(share);
            psi.ArgumentList.Add(local);
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(string.Join(",", opts));

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var stderr = await p.StandardError.ReadToEndAsync(ct);
                return Result.Fail<string>(
                    $"mount.cifs exited with {p.ExitCode}: {stderr.Trim()}");
            }

            lock (_gate)
            {
                if (!_mountsByCleaning.TryGetValue(cleaningId, out var list))
                    _mountsByCleaning[cleaningId] = list = new List<string>();
                list.Add(local);
            }
            return Result.Ok(local);
        }
        catch (Exception ex) { return Result.Fail<string>(new ExceptionalError(ex)); }
    }

    public async Task<Result> UnmountAsync(Guid cleaningId, CancellationToken ct = default)
    {
        List<string>? list;
        lock (_gate)
        {
            if (!_mountsByCleaning.TryGetValue(cleaningId, out list)) return Result.Ok();
            _mountsByCleaning.Remove(cleaningId);
        }
        foreach (var local in list)
        {
            try
            {
                var psi = new ProcessStartInfo("umount") { UseShellExecute = false };
                psi.ArgumentList.Add(local);
                using var p = Process.Start(psi)!;
                await p.WaitForExitAsync(ct);
                if (Directory.Exists(local) && Directory.GetFileSystemEntries(local).Length == 0)
                    Directory.Delete(local);
            }
            catch { /* best-effort */ }
        }
        return Result.Ok();
    }
}
