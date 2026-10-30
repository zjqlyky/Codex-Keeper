using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string? _codexHomeOverride;
    private KeeperSettings _settings;
    private bool _loadHealthy;

    public SettingsStore(string? dataDirectory = null, string? codexHome = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexKeeper");
        _codexHomeOverride = codexHome;
        Directory.CreateDirectory(DataDirectory);
        _filePath = Path.Combine(DataDirectory, "settings.json");
        _settings = Load(_filePath, out _loadHealthy);
    }

    public string DataDirectory { get; }
    public bool IsHealthy => _loadHealthy;
    public string CodexHome
    {
        get
        {
            if (_codexHomeOverride is not null) return _codexHomeOverride;
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
        }
    }

    public KeeperSettings Snapshot()
    {
        lock (_gate) return Clone(_settings);
    }

    public void Update(Action<KeeperSettings> update)
    {
        lock (_gate)
        {
            if (!_loadHealthy) throw new InvalidOperationException(Localization.Text("activity.settingsFailed"));
            var updated = Clone(_settings);
            update(updated);
            updated.DailyAnchorMinutes = Math.Clamp(updated.DailyAnchorMinutes, 0, 1439);
            updated.PingAttempts = updated.PingAttempts.TakeLast(500).ToList();
            updated.ResumeAttempts = updated.ResumeAttempts.Distinct(StringComparer.Ordinal).TakeLast(500).ToList();
            updated.RecentActivity = updated.RecentActivity.Take(30).ToList();
            updated.SelectedProjectPaths = updated.SelectedProjectPaths.Select(ProjectPath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            updated.SelectedThreadIds = updated.SelectedThreadIds.Distinct(StringComparer.Ordinal).ToList();
            Save(updated);
            _settings = updated;
        }
    }

    public void AddActivity(string message)
    {
        var line = $"{DateTimeOffset.Now:O} | {message}";
        Update(settings => settings.RecentActivity.Insert(0, line));
    }

    public static string StableAttemptKey(string threadId, DateTimeOffset blockedAt)
    {
        var data = Encoding.UTF8.GetBytes($"{threadId}|{blockedAt.ToUnixTimeMilliseconds()}");
        return Convert.ToHexString(SHA256.HashData(data));
    }

    private static KeeperSettings Load(string path, out bool healthy)
    {
        try
        {
            if (!File.Exists(path)) { healthy = true; return new KeeperSettings(); }
            var settings = JsonSerializer.Deserialize<KeeperSettings>(File.ReadAllText(path), JsonOptions) ?? new KeeperSettings();
            settings.ResumeAttempts ??= [];
            settings.PingAttempts ??= [];
            settings.RecentActivity ??= [];
            settings.SelectedProjectPaths ??= [];
            settings.SelectedThreadIds ??= [];
            if (settings.DailyAnchorMinutes is < 0 or > 1439) settings.DailyAnchorMinutes = 480;
            healthy = true;
            return settings;
        }
        catch
        {
            healthy = false;
            return new KeeperSettings();
        }
    }

    private void Save(KeeperSettings settings)
    {
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions), new UTF8Encoding(false));
        File.Move(temporary, _filePath, true);
    }

    private static KeeperSettings Clone(KeeperSettings value) => new()
    {
        Enabled = value.Enabled,
        AutoResume = value.AutoResume,
        LaunchAtLogin = value.LaunchAtLogin,
        DailyAnchorMinutes = value.DailyAnchorMinutes,
        CodexExecutable = value.CodexExecutable,
        LastPingNode = value.LastPingNode,
        PingAttempts = [.. value.PingAttempts],
        ResumeAttempts = [.. value.ResumeAttempts],
        RecentActivity = [.. value.RecentActivity],
        SelectedProjectPaths = [.. value.SelectedProjectPaths],
        SelectedThreadIds = [.. value.SelectedThreadIds],
        LastFiveHour = value.LastFiveHour,
        LastWeekly = value.LastWeekly,
        LastQuotaReadAt = value.LastQuotaReadAt,
        CodexAccountId = value.CodexAccountId
    };
}
