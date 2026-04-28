using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Brusca.Core.Contracts.Services;
using Brusca.Core.Models.PathAccess;
using FluentResults;

namespace Brusca.Infrastructure.Services.PathAccess;

/// <summary>
/// Mounts UNC shares on Windows via mpr.dll's WNetAddConnection2W.
/// The connection is opened with CONNECT_TEMPORARY so it is torn down when
/// the host process exits even if <see cref="UnmountAsync"/> is never called.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShareMounter : IPlatformShareMounter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    private const int RESOURCETYPE_DISK = 1;
    private const int CONNECT_TEMPORARY = 4;
    private const int NO_ERROR = 0;
    private const int ERROR_ALREADY_ASSIGNED = 85;
    private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_BAD_NET_NAME = 67;
    private const int ERROR_INVALID_PASSWORD = 86;

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2W(
        ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2W(
        string lpName, int dwFlags, bool fForce);

    private readonly Dictionary<Guid, HashSet<string>> _mountsByCleaning = new();
    private readonly object _gate = new();

    public bool IsApplicable(string path) =>
        !string.IsNullOrEmpty(path) && path.StartsWith(@"\\");

    public Task<Result<string>> MountAsync(
        string path, PathCredentials? credentials, Guid cleaningId,
        CancellationToken ct = default)
    {
        var shareRoot = TrimToShareRoot(path);
        var nr = new NETRESOURCE
        {
            dwType = RESOURCETYPE_DISK,
            lpRemoteName = shareRoot
        };
        var user = credentials is null
            ? null
            : (string.IsNullOrEmpty(credentials.Domain)
                ? credentials.Username
                : $"{credentials.Domain}\\{credentials.Username}");

        var rc = WNetAddConnection2W(ref nr, credentials?.Password, user, CONNECT_TEMPORARY);
        if (rc == NO_ERROR || rc == ERROR_ALREADY_ASSIGNED)
        {
            lock (_gate)
            {
                if (!_mountsByCleaning.TryGetValue(cleaningId, out var set))
                    _mountsByCleaning[cleaningId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(shareRoot);
            }
            return Task.FromResult(Result.Ok(path));
        }

        var reason = rc switch
        {
            ERROR_LOGON_FAILURE => "Logon failure (bad username or password).",
            ERROR_INVALID_PASSWORD => "Invalid password.",
            ERROR_BAD_NET_NAME => "Network name not found.",
            ERROR_SESSION_CREDENTIAL_CONFLICT =>
                "Multiple connections to the same share with different credentials are not allowed.",
            _ => $"WNetAddConnection2W failed (Win32 error {rc})."
        };
        return Task.FromResult(Result.Fail<string>(reason));
    }

    public Task<Result> UnmountAsync(Guid cleaningId, CancellationToken ct = default)
    {
        HashSet<string>? set;
        lock (_gate)
        {
            if (!_mountsByCleaning.TryGetValue(cleaningId, out set)) return Task.FromResult(Result.Ok());
            _mountsByCleaning.Remove(cleaningId);
        }
        foreach (var share in set)
            WNetCancelConnection2W(share, 0, fForce: false);
        return Task.FromResult(Result.Ok());
    }

    private static string TrimToShareRoot(string uncPath)
    {
        // \\server\share\sub\dir → \\server\share
        if (string.IsNullOrEmpty(uncPath) || !uncPath.StartsWith(@"\\")) return uncPath;
        var trimmed = uncPath.TrimEnd('\\', '/');
        var parts = trimmed.Substring(2).Split(new[] { '\\', '/' }, 3,
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? @"\\" + parts[0] + @"\" + parts[1] : trimmed;
    }
}
