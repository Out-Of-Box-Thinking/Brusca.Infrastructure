using Brusca.Core.Contracts.Services;

namespace Brusca.Infrastructure.Services;

/// <summary>
/// Cross-platform path/segment sanitizer. Strips characters illegal on the
/// most restrictive supported file system (Windows) so that paths
/// materialized on Windows, Linux, and macOS are all valid.
///
/// Applied by the rehydrator (token substitution) and by the structure-
/// execution planner (final AfterPath assembly).
/// </summary>
public sealed class PathSafetyService : IPathSafetyService
{
    private const int MaxSegmentLength = 200;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL",
        "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"
    };

    public char ReplacementChar => '_';

    public string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return "_";

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
        var buf = new System.Text.StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (invalid.Contains(c) || char.IsControl(c))
                buf.Append(ReplacementChar);
            else
                buf.Append(c);
        }

        // Trim trailing dots / spaces (illegal on Windows).
        while (buf.Length > 0 && (buf[^1] == ' ' || buf[^1] == '.'))
            buf.Length--;

        var clean = buf.ToString().Trim();
        if (clean.Length == 0) return "_";

        // Length cap — preserve extension.
        if (clean.Length > MaxSegmentLength)
        {
            var ext = Path.GetExtension(clean);
            var stem = Path.GetFileNameWithoutExtension(clean);
            var room = MaxSegmentLength - ext.Length;
            if (room <= 0) clean = clean[..MaxSegmentLength];
            else clean = stem[..Math.Min(stem.Length, room)] + ext;
        }

        // Reserved-name avoidance (case-insensitive, with or without extension).
        var stemForCheck = Path.GetFileNameWithoutExtension(clean);
        if (ReservedNames.Contains(stemForCheck))
            clean = "_" + clean;

        return clean;
    }

    public string SanitizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // Preserve a UNC or drive prefix unchanged.
        var prefix = string.Empty;
        var rest = path;

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\server\share\... — keep the first 4 components untouched.
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                prefix = @"\\" + parts[0] + @"\" + parts[1] + @"\";
                rest = string.Join('\\', parts.Skip(2));
            }
        }
        else if (path.Length >= 2 && path[1] == ':')
        {
            prefix = path[..2];
            rest = path.Length > 2 ? path[2..].TrimStart('\\', '/') : string.Empty;
            if (path.Length > 2 && (path[2] == '\\' || path[2] == '/'))
                prefix += Path.DirectorySeparatorChar;
        }

        var segments = rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var clean = segments.Select(SanitizeSegment);
        return prefix + string.Join(Path.DirectorySeparatorChar, clean);
    }
}
