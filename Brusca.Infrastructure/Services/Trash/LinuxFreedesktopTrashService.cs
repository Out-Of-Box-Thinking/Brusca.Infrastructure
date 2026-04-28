using System.Globalization;
using System.Text;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Trash;

/// <summary>
/// Linux freedesktop.org Trash spec implementation. Moves the file into
/// <c>~/.local/share/Trash/files/</c> and writes a matching
/// <c>.trashinfo</c> sidecar into <c>~/.local/share/Trash/info/</c> so the
/// host's file manager can restore the file later.
///
/// Spec: https://specifications.freedesktop.org/trash-spec/trashspec-1.0.html
/// </summary>
public sealed class LinuxFreedesktopTrashService : ITrashService
{
    /// <inheritdoc />
    public Task<Result> MoveToTrashAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(path)) return Task.FromResult(Result.Ok());

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var trashRoot = Path.Combine(home, ".local", "share", "Trash");
            var filesDir  = Path.Combine(trashRoot, "files");
            var infoDir   = Path.Combine(trashRoot, "info");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);

            // Pick a non-colliding destination name inside Trash/files.
            var fileName    = Path.GetFileName(path);
            var destFile    = Path.Combine(filesDir, fileName);
            var destInfo    = Path.Combine(infoDir, fileName + ".trashinfo");
            for (int i = 1; File.Exists(destFile) || File.Exists(destInfo); i++)
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var ext  = Path.GetExtension(fileName);
                destFile = Path.Combine(filesDir, $"{stem}.{i}{ext}");
                destInfo = Path.Combine(infoDir, $"{stem}.{i}{ext}.trashinfo");
            }

            File.Move(path, destFile);

            var info = new StringBuilder()
                .AppendLine("[Trash Info]")
                .Append("Path=").AppendLine(Uri.EscapeDataString(path).Replace("%2F", "/"))
                .Append("DeletionDate=")
                .AppendLine(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            File.WriteAllText(destInfo, info.ToString(), Encoding.UTF8);

            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(new ExceptionalError(ex)));
        }
    }
}
