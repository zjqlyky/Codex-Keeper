using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

public static class CodexLocator
{
    public static string Find(KeeperSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CodexExecutable))
        {
            var custom = Environment.ExpandEnvironmentVariables(settings.CodexExecutable.Trim().Trim('"'));
            if (File.Exists(custom)) return Path.GetFullPath(custom);
        }

        var pathMatches = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "codex.exe"))
            .Where(File.Exists)
            .ToList();
        if (pathMatches.Count > 0) return pathMatches[0];

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var versioned = Path.Combine(local, "OpenAI", "Codex", "bin");
        if (Directory.Exists(versioned))
        {
            var matches = Directory.EnumerateDirectories(versioned)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .ToList();
            if (matches.Count > 0) return matches[0];
        }

        throw new FileNotFoundException(Localization.Text("activity.noCli"));
    }
}
