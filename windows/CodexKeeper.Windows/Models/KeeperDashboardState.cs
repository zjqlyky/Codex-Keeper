namespace CodexKeeper.Windows.Models;

public sealed record KeeperDashboardState(
    bool IsEnabled,
    bool AutoResume,
    bool LaunchAtLogin,
    bool IsBusy,
    QuotaSnapshot? Quota,
    DateTimeOffset? LastQuotaReadAt,
    CodexSessionScan? SessionScan,
    DateTimeOffset? NextScheduledNode,
    string StatusTitle,
    string StatusDetail,
    IReadOnlyList<string> RecentActivity,
    string CodexHome,
    string? CodexExecutable);
