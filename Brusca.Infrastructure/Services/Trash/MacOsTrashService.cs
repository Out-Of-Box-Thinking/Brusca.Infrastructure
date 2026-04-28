using System.Diagnostics;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Trash;

/// <summary>
/// macOS trash implementation. Tries Finder via <c>osascript</c> first so the
/// move surfaces in the Trash UI with full restore metadata; on failure
/// (e.g. headless host without osascript) falls back to a plain move into
/// <c>~/.Trash</c>.
/// </summary>
public sealed class MacOsTrashService : ITrashService
{
    /// <inheritdoc />
    public async Task<Result> MoveToTrashAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return Result.Ok();

            // 1. Preferred path — osascript Finder delete.
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "osascript",
                    RedirectStandardError  = true,
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"tell application \"Finder\" to delete POSIX file \"{path}\"");

                using var proc = Process.Start(psi);
                if (proc is not null)
                {
                    await proc.WaitForExitAsync(ct);
                    if (proc.ExitCode == 0) return Result.Ok();
                }
            }
            catch
            {
                // fall through to plain ~/.Trash move
            }

            // 2. Fallback — move into ~/.Trash with a non-colliding name.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var trashDir = Path.Combine(home, ".Trash");
            Directory.CreateDirectory(trashDir);

            var fileName = Path.GetFileName(path);
            var dest     = Path.Combine(trashDir, fileName);
            for (int i = 1; File.Exists(dest); i++)
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext  = Path.GetExtension(fileName);
                dest = Path.Combine(trashDir, $"{stem} ({i}){ext}");
            }
            File.Move(path, dest);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(new ExceptionalError(ex));
        }
    }
}
