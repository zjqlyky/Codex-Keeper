namespace CodexKeeper.Windows.Models;

public sealed record QuotaWindow(double UsedPercent, int WindowMinutes, DateTimeOffset ResetsAt);

public sealed record QuotaSnapshot(
    QuotaWindow? FiveHour,
    QuotaWindow? Weekly,
    DateTimeOffset CapturedAt,
    string? AccountId,
    bool? ZeroUseWindowActive = null)
{
    public bool IsFresh(DateTimeOffset now)
    {
        var age = now - CapturedAt;
        return age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(60);
    }
    public bool? HasActiveFiveHourWindow
    {
        get
        {
            if (FiveHour is null || FiveHour.ResetsAt <= CapturedAt) return false;
            if (FiveHour.UsedPercent > 0) return true;
            return ZeroUseWindowActive;
        }
    }

    public bool HasAvailableQuota(DateTimeOffset now) => IsFresh(now) &&
        (FiveHour?.UsedPercent ?? 0) < 100 && (Weekly?.UsedPercent ?? 0) < 100 &&
        (FiveHour is not null || Weekly is not null);
}

public sealed record PausedTask(
    string ThreadId,
    string Project,
    string WorkingDirectory,
    DateTimeOffset BlockedAt,
    string? StoppedTurnId,
    DateTimeOffset? FiveHourResetAt,
    DateTimeOffset? WeeklyResetAt,
    bool FiveHourWasExhausted,
    bool WeeklyWasExhausted,
    string AttemptKey);

public sealed record CodexSessionScan(
    bool IsAvailable,
    IReadOnlyList<PausedTask> PausedTasks,
    bool HasRunningTask,
    string? Error,
    IReadOnlyList<CodexProject>? Projects = null,
    IReadOnlyList<CodexTask>? Tasks = null,
    bool HasUnverifiedTask = false,
    bool HasUnlocatedUnverifiedTask = false);
