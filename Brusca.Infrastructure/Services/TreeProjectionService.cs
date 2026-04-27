using Brusca.Core.Contracts.Services;
using Brusca.Core.Enums;
using Brusca.Core.Models.Cleaning;
using System.Text.Json;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Projects the "after" directory tree by simulating approved steps against
/// a deep clone of the "before" snapshot — purely in memory, no I/O.
/// </summary>
public sealed class TreeProjectionService : ITreeProjectionService
{
    public DirectoryNode ProjectAfterTree(
        DirectoryNode before,
        IReadOnlyList<CleaningPromptStep> approvedSteps)
    {
        // Deep-clone via JSON round-trip so we never mutate the before snapshot
        var json = JsonSerializer.Serialize(before);
        var after = JsonSerializer.Deserialize<DirectoryNode>(json)!;

        foreach (var step in approvedSteps.OrderBy(s => s.StepOrder))
        {
            if (string.IsNullOrEmpty(step.SourcePath) ||
                string.IsNullOrEmpty(step.ProposedTargetPath))
                continue;

            switch (step.StepType)
            {
                case PromptStepType.DirectoryRename:
                    RenameDirectory(after, step.SourcePath, step.ProposedTargetPath);
                    break;
                case PromptStepType.FileRename:
                    RenameFile(after, step.SourcePath, step.ProposedTargetPath);
                    break;
                case PromptStepType.FileMove:
                    MoveFile(after, step.SourcePath, step.ProposedTargetPath);
                    break;
            }
        }

        return after;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static void RenameDirectory(DirectoryNode root, string src, string target)
    {
        var node = FindDirectory(root, src);
        if (node is null) return;
        node.Name = Path.GetFileName(target);
        node.FullPath = target;
        UpdateChildPaths(node, src, target);
    }

    private static void RenameFile(DirectoryNode root, string src, string target)
    {
        var dir = FindDirectoryContaining(root, src);
        if (dir is null) return;
        var idx = dir.Files.IndexOf(src);
        if (idx >= 0) dir.Files[idx] = target;
    }

    private static void MoveFile(DirectoryNode root, string src, string targetDir)
    {
        var fromDir = FindDirectoryContaining(root, src);
        if (fromDir is null) return;
        fromDir.Files.Remove(src);

        var toDir = FindOrCreateDirectory(root, targetDir);
        toDir.Files.Add(Path.Combine(targetDir, Path.GetFileName(src)));
    }

    private static DirectoryNode? FindDirectory(DirectoryNode node, string path)
    {
        if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
            return node;
        foreach (var child in node.Children)
        {
            var found = FindDirectory(child, path);
            if (found is not null) return found;
        }
        return null;
    }

    private static DirectoryNode? FindDirectoryContaining(DirectoryNode node, string filePath)
    {
        if (node.Files.Contains(filePath, StringComparer.OrdinalIgnoreCase))
            return node;
        foreach (var child in node.Children)
        {
            var found = FindDirectoryContaining(child, filePath);
            if (found is not null) return found;
        }
        return null;
    }

    private static DirectoryNode FindOrCreateDirectory(DirectoryNode root, string path)
    {
        var existing = FindDirectory(root, path);
        if (existing is not null) return existing;

        var parent = FindDirectory(root, Path.GetDirectoryName(path) ?? root.FullPath) ?? root;
        var newNode = new DirectoryNode
        {
            FullPath = path,
            Name = Path.GetFileName(path),
            Depth = parent.Depth + 1
        };
        parent.Children.Add(newNode);
        return newNode;
    }

    private static void UpdateChildPaths(DirectoryNode node, string oldBase, string newBase)
    {
        foreach (var child in node.Children)
        {
            child.FullPath = child.FullPath.Replace(oldBase, newBase, StringComparison.OrdinalIgnoreCase);
            UpdateChildPaths(child, oldBase, newBase);
        }
        for (int i = 0; i < node.Files.Count; i++)
            node.Files[i] = node.Files[i].Replace(oldBase, newBase, StringComparison.OrdinalIgnoreCase);
    }
}
