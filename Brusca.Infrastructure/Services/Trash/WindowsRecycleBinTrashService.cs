using System.Runtime.Versioning;
using Brusca.Core.Contracts.Services;
using FluentResults;

namespace Brusca.Infrastructure.Services.Trash;

/// <summary>
/// Sends a file to the Windows Recycle Bin via
/// <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>. The API is bundled
/// with the .NET SDK; no NuGet reference is needed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsRecycleBinTrashService : ITrashService
{
    /// <inheritdoc />
    public Task<Result> MoveToTrashAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            return Task.FromResult(Result.Ok());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Result.Fail(new ExceptionalError(ex)));
        }
    }
}
