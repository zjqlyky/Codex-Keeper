using System.Text.Json.Nodes;
using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

/// <summary>Reads saved local project names and roots without inferring task ownership.</summary>
internal static class CodexProjectDiscovery
{
    public static IReadOnlyList<CodexProject> ReadSavedProjects(string codexHome)
    {
        var projects = new Dictionary<string, CodexProject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(codexHome, ".codex-global-state.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is not JsonObject root) return [];
            var atoms = root["electron-persisted-atom-state"] as JsonObject;
            var local = root["local-projects"] as JsonObject ?? atoms?["local-projects"] as JsonObject;
            if (local is not null)
            {
                foreach (var entry in local.Take(1000))
                {
                    if (entry.Value is not JsonObject project || project["rootPaths"] is not JsonArray paths) continue;
                    var label = project["name"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : null;
                    foreach (var item in paths.Take(100))
                        if (item is JsonValue value && value.TryGetValue<string>(out var directory)) Add(projects, directory, label);
                }
            }
        }
        catch { }
        return projects.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static CodexProject? FindSavedProject(IEnumerable<CodexProject> saved, string directory) =>
        saved.Where(project => ProjectPath.Contains(project.WorkingDirectory, directory))
            .OrderByDescending(project => project.WorkingDirectory.Length).FirstOrDefault();

    public static IReadOnlyList<CodexProject> Merge(IReadOnlyList<CodexProject> saved, IEnumerable<string> directories)
    {
        var projects = saved.ToDictionary(project => project.WorkingDirectory, project => project with { SessionCount = 0 }, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            try
            {
                var normalized = ProjectPath.Normalize(directory);
                var root = FindSavedProject(saved, normalized)?.WorkingDirectory ?? normalized;
                if (projects.TryGetValue(root, out var existing)) projects[root] = existing with { SessionCount = existing.SessionCount + 1 };
                else projects[root] = new CodexProject(LeafName(root), root, 1);
            }
            catch { }
        }
        return projects.Values.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(project => project.WorkingDirectory, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string LeafName(string path)
    {
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(leaf) ? path : leaf;
    }

    private static void Add(IDictionary<string, CodexProject> projects, string? path, string? label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return;
        try
        {
            var normalized = ProjectPath.Normalize(path);
            var name = string.IsNullOrWhiteSpace(label) ? LeafName(normalized) : label.Trim();
            if (name.Length > 256) name = name[..256];
            projects.TryAdd(normalized, new CodexProject(name, normalized, 0));
        }
        catch { }
    }
}
