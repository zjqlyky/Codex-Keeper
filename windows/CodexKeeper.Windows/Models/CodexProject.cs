namespace CodexKeeper.Windows.Models;

public sealed record CodexProject(string Name, string WorkingDirectory, int SessionCount);

public sealed record CodexTask(
    string ThreadId,
    string Title,
    string Project,
    string WorkingDirectory,
    DateTimeOffset UpdatedAt,
    bool IsRunning,
    bool IsQuotaBlocked,
    bool CanAutoResume,
    string? UnavailableReason = null,
    bool IsVerified = false);

public static class ProjectPath
{
    public static string Normalize(string path)
    {
        var value = path.Trim();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) value = @"\\" + value[8..];
        else if (value.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) value = value[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static bool Matches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return StringComparer.OrdinalIgnoreCase.Equals(Normalize(left), Normalize(right)); }
        catch { return false; }
    }

    public static bool Contains(string root, string candidate)
    {
        try
        {
            var parent = Normalize(root);
            var child = Normalize(candidate);
            return StringComparer.OrdinalIgnoreCase.Equals(parent, child) ||
                child.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
