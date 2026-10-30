using System.Text.Json.Serialization;

namespace CodexKeeper.Windows.Models;

public sealed class KeeperSettings
{
    public bool Enabled { get; set; }
    public bool AutoResume { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public int DailyAnchorMinutes { get; set; } = 480;
    public string? CodexExecutable { get; set; }
    public string? LastPingNode { get; set; }
    public List<string> PingAttempts { get; set; } = [];
    public List<string> ResumeAttempts { get; set; } = [];
    public List<string> RecentActivity { get; set; } = [];
    public List<string> SelectedProjectPaths { get; set; } = [];
    public List<string> SelectedThreadIds { get; set; } = [];
    public QuotaWindow? LastFiveHour { get; set; }
    public QuotaWindow? LastWeekly { get; set; }
    public DateTimeOffset? LastQuotaReadAt { get; set; }

    [JsonIgnore]
    public string? CodexAccountId { get; set; }
}
