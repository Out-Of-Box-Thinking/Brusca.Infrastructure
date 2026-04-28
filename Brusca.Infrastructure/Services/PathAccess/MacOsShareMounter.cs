using System.Diagnostics;
using System.Runtime.Versioning;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.PathAccess;
using FluentResults;

namespace Brusca.Infrastructure.Services.PathAccess;

/// <summary>
/// Best-effort SMB share mounter for macOS using <c>mount_smbfs</c>.
/// Mounts under <c>/Volumes/brusca-&lt;cleaning-id&gt;</c>.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsShareMounter : IPlatformShareMounter
{
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
            // mount_smbfs accepts smb://[user[:pass]@]server/share
            var hostShare = path.StartsWith("smb://")
                ? path.Substring("smb://".Length)
                : path.TrimStart('/');
            string url;
            if (credentials is not null)
            {
                var user = string.IsNullOrEmpty(credentials.Domain)
                    ? credentials.Username
                    : $"{credentials.Domain};{credentials.Username}";
                url = $"//{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(credentials.Password)}@{hostShare}";
            }
            else url = $"//{hostShare}";

            var local = $"/Volumes/brusca-{cleaningId:N}";
            Directory.CreateDirectory(local);

            var psi = new ProcessStartInfo("mount_smbfs")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(url);
            psi.ArgumentList.Add(local);

            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
            {
                var stderr = await p.StandardError.ReadToEndAsync(ct);
                return Result.Fail<string>(
                    $"mount_smbfs exited with {p.ExitCode}: {stderr.Trim()}");
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
            }
            catch { /* best-effort */ }
        }
        return Result.Ok();
    }
}
