using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using CodexKeeper.Windows.Models;
using CodexKeeper.Windows.Services;
using Localization = CodexKeeper.Windows.Services.Localization;
using Application = System.Windows.Application;

namespace CodexKeeper.Windows.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly SettingsStore _store;
    private readonly KeeperService _keeper;
    private bool _isEnabled;
    private bool _autoResume;
    private bool _launchAtLogin;
    private bool _isBusy;
    private string _statusTitle = "";
    private string _statusDetail = "";
    private string _scheduleTime = "08:00";
    private string _codexExecutable = "";
    private string _fiveHourText = Localization.Text("quota.unknown");
    private string _weeklyText = Localization.Text("quota.unknown");
    private string _lastSyncText = Localization.Text("quota.unknown");
    private string _tasksSummary = Localization.Text("tasks.none");
    private string _selectedProjectsLine = Localization.Text("tasks.noProjects");
    private string _projectsSummary = Localization.Text("projects.noneSelected");
    private string _projectsEmptyMessage = Localization.Text("projects.loading");
    private string _nextNodeText = Localization.Text("quota.unknown");
    private double _fiveHourProgress;
    private double _weeklyProgress;

    public MainViewModel(SettingsStore store, KeeperService keeper)
    {
        _store = store;
        _keeper = keeper;
        var settings = store.Snapshot();
        _isEnabled = settings.Enabled;
        _autoResume = settings.AutoResume;
        _launchAtLogin = settings.LaunchAtLogin;
        _scheduleTime = FormatTime(settings.DailyAnchorMinutes);
        _codexExecutable = settings.CodexExecutable ?? "";
        _keeper.StateChanged += OnKeeperStateChanged;
        Apply(_keeper.GetState());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> RecentActivity { get; } = [];
    public ObservableCollection<string> PausedTasks { get; } = [];
    public ObservableCollection<ProjectOptionViewModel> Projects { get; } = [];
    public ObservableCollection<TaskOptionViewModel> Tasks { get; } = [];

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value)) return;
            _ = _keeper.SetEnabledAsync(value);
        }
    }
    public bool AutoResume
    {
        get => _autoResume;
        set
        {
            if (!Set(ref _autoResume, value)) return;
            _ = _keeper.SetAutoResumeAsync(value);
        }
    }
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (!Set(ref _launchAtLogin, value)) return;
            _ = _keeper.SetLaunchAtLoginAsync(value);
        }
    }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public string ScheduleTime { get => _scheduleTime; set => Set(ref _scheduleTime, value); }
    public string CodexExecutable { get => _codexExecutable; set => Set(ref _codexExecutable, value); }
    public string FiveHourText { get => _fiveHourText; private set => Set(ref _fiveHourText, value); }
    public string WeeklyText { get => _weeklyText; private set => Set(ref _weeklyText, value); }
    public string LastSyncText { get => _lastSyncText; private set => Set(ref _lastSyncText, value); }
    public string LastSyncLine => Localization.Format("quota.synced", LastSyncText);
    public string TasksSummary { get => _tasksSummary; private set => Set(ref _tasksSummary, value); }
    public string SelectedProjectsLine { get => _selectedProjectsLine; private set => Set(ref _selectedProjectsLine, value); }
    public string ProjectsSummary { get => _projectsSummary; private set => Set(ref _projectsSummary, value); }
    public string ProjectsEmptyMessage { get => _projectsEmptyMessage; private set => Set(ref _projectsEmptyMessage, value); }
    public bool HasProjects => Projects.Count > 0;
    public string NextNodeText { get => _nextNodeText; private set => Set(ref _nextNodeText, value); }
    public double FiveHourProgress { get => _fiveHourProgress; private set => Set(ref _fiveHourProgress, value); }
    public double WeeklyProgress { get => _weeklyProgress; private set => Set(ref _weeklyProgress, value); }
    public string AppTitle => Localization.Text("app.title");
    public string AppTagline => Localization.Text("app.tagline");
    public string WindowsBadge => Localization.Text("app.windows");
    public string QuotaTitle => Localization.Text("quota.title");
    public string FiveHourTitle => Localization.Text("quota.five");
    public string WeeklyTitle => Localization.Text("quota.week");
    public string ScheduleTitle => Localization.Text("schedule.title");
    public string ScheduleDescription => Localization.Text("schedule.description");
    public string ScheduleLabel => Localization.Text("schedule.start");
    public string NextNodeLabel => Localization.Text("schedule.next");
    public string SaveScheduleLabel => Localization.Text("schedule.save");
    public string KeeperEnableLabel => Localization.Text("keeper.enable");
    public string KeeperSettingsTitle => Localization.Text("keeper.settings");
    public string AutoResumeLabel => Localization.Text("keeper.autoResume");
    public string StartupLabel => Localization.Text("startup.enable");
    public string KeeperExplanation => Localization.Text("keeper.explanation");
    public string TasksTitle => Localization.Text("tasks.title");
    public string TasksExplanation => Localization.Text("tasks.explanation");
    public string ProjectsTitle => Localization.Text("projects.title");
    public string ProjectsExplanation => Localization.Text("projects.explanation");
    public string RefreshProjectsLabel => Localization.Text("projects.refresh");
    public string ActivityTitle => Localization.Text("activity.title");
    public string SyncLabel => Localization.Text("action.sync");
    public string CliTitle => Localization.Text("cli.title");
    public string CliHint => Localization.Text("cli.hint");
    public string BrowseLabel => Localization.Text("cli.browse");
    public string CliAutoLabel => Localization.Text("cli.autoLabel");
    public string TraySummary => IsEnabled ? Localization.Text("status.working") : Localization.Text("status.paused");
    public string CodexHomeLine => Localization.Format("about.path", _keeper.GetState().CodexHome);
    public string Footer => Localization.Text("about.footer");
    public string MinimizeLabel => Localization.Text("window.minimize");

    public async Task RefreshNowAsync() => await _keeper.RefreshNowAsync();
    public async Task RefreshProjectsAsync() => await _keeper.RefreshProjectsAsync();

    public async Task SaveScheduleAsync()
    {
        if (!TimeSpan.TryParseExact(ScheduleTime.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out var time) ||
            time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            StatusTitle = Localization.Text("status.error");
            StatusDetail = Localization.IsChinese ? "请输入 00:00 到 23:59 之间的时间。" : "Enter a time between 00:00 and 23:59.";
            return;
        }
        await _keeper.SaveScheduleAsync((int)time.TotalMinutes);
        ScheduleTime = time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
    }

    public async Task SaveCodexExecutableAsync(string? path)
    {
        CodexExecutable = path ?? "";
        await _keeper.SaveCodexExecutableAsync(path);
        await _keeper.RefreshNowAsync();
    }

    private void OnKeeperStateChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) Apply(_keeper.GetState());
        else _ = dispatcher.BeginInvoke(() => Apply(_keeper.GetState()));
    }

    private void Apply(KeeperDashboardState state)
    {
        _isEnabled = state.IsEnabled;
        _autoResume = state.AutoResume;
        _launchAtLogin = state.LaunchAtLogin;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(TraySummary));
        OnPropertyChanged(nameof(AutoResume));
        OnPropertyChanged(nameof(LaunchAtLogin));
        IsBusy = state.IsBusy;
        StatusTitle = state.StatusTitle;
        StatusDetail = state.StatusDetail;
        if (!StringComparer.Ordinal.Equals(CodexExecutable, state.CodexExecutable ?? "")) CodexExecutable = state.CodexExecutable ?? "";
        if (state.Quota is { } quota)
        {
            FiveHourProgress = Progress(quota.FiveHour);
            WeeklyProgress = Progress(quota.Weekly);
            FiveHourText = FormatQuota(quota.FiveHour);
            WeeklyText = FormatQuota(quota.Weekly);
            LastSyncText = state.LastQuotaReadAt is { } at ? at.ToLocalTime().ToString(Localization.IsChinese ? "HH:mm:ss" : "h:mm:ss tt", CultureInfo.CurrentCulture) : Localization.Text("quota.unknown");
            OnPropertyChanged(nameof(LastSyncLine));
        }
        else
        {
            FiveHourProgress = 0;
            WeeklyProgress = 0;
            FiveHourText = Localization.Text("quota.unknown");
            WeeklyText = Localization.Text("quota.unknown");
            LastSyncText = Localization.Text("quota.unknown");
            OnPropertyChanged(nameof(LastSyncLine));
        }
        NextNodeText = state.NextScheduledNode is { } next ? next.ToLocalTime().ToString(Localization.IsChinese ? "M月d日 HH:mm" : "MMM d, h:mm tt", CultureInfo.CurrentCulture) : Localization.Text("quota.unknown");
        ApplyProjectsAndTasks(state.SessionScan);
        Replace(RecentActivity, state.RecentActivity.Take(2).Select(FormatActivity));
    }

    private void ApplyProjectsAndTasks(CodexSessionScan? scan)
    {
        var settings = _store.Snapshot();
        var projects = scan?.Projects ?? [];
        for (var index = Projects.Count - 1; index >= 0; index--)
            if (!projects.Any(project => ProjectPath.Matches(project.WorkingDirectory, Projects[index].WorkingDirectory))) Projects.RemoveAt(index);
        for (var index = 0; index < projects.Count; index++)
        {
            var project = projects[index];
            var item = Projects.FirstOrDefault(option => ProjectPath.Matches(option.WorkingDirectory, project.WorkingDirectory));
            if (item is null)
            {
                item = new ProjectOptionViewModel((option, selected) => _ = _keeper.SetProjectSelectedAsync(option.WorkingDirectory, selected));
                Projects.Insert(index, item);
            }
            else if (Projects.IndexOf(item) != index) Projects.Move(Projects.IndexOf(item), index);
            item.Update(project, settings.SelectedProjectPaths.Any(path => ProjectPath.Matches(path, project.WorkingDirectory)));
        }
        OnPropertyChanged(nameof(HasProjects));
        var selectedProjects = Projects.Where(project => project.IsSelected).ToList();
        var selectedPaths = selectedProjects.Select(project => project.WorkingDirectory).ToList();
        ProjectsSummary = selectedPaths.Count == 0 ? Localization.Text("projects.noneSelected") : Localization.Format("projects.selected", selectedPaths.Count, Projects.Count);
        ProjectsEmptyMessage = scan is null ? Localization.Text("projects.loading") : Localization.Text("projects.empty");
        SelectedProjectsLine = selectedProjects.Count == 0 ? Localization.Text("tasks.noProjects")
            : Localization.Format("tasks.selectedProjects", string.Join(Localization.IsChinese ? "、" : ", ", selectedProjects.Select(project => project.Name)));

        var tasks = (scan?.Tasks ?? []).Where(task => selectedPaths.Any(path => ProjectPath.Contains(path, task.WorkingDirectory))).ToList();
        for (var index = Tasks.Count - 1; index >= 0; index--)
            if (!tasks.Any(task => task.ThreadId == Tasks[index].ThreadId)) Tasks.RemoveAt(index);
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var item = Tasks.FirstOrDefault(option => option.ThreadId == task.ThreadId);
            if (item is null)
            {
                item = new TaskOptionViewModel((option, selected) => _ = _keeper.SetTaskSelectedAsync(option.ThreadId, selected));
                Tasks.Insert(index, item);
            }
            else if (Tasks.IndexOf(item) != index) Tasks.Move(Tasks.IndexOf(item), index);
            item.Update(task, settings.SelectedThreadIds.Contains(task.ThreadId, StringComparer.Ordinal));
        }
        var selectedTasks = Tasks.Count(task => task.IsSelected);
        var waitingTasks = scan?.PausedTasks.Count(task => tasks.Any(item => item.ThreadId == task.ThreadId) && settings.SelectedThreadIds.Contains(task.ThreadId, StringComparer.Ordinal)) ?? 0;
        TasksSummary = selectedPaths.Count == 0 ? Localization.Text("tasks.selectProjects")
            : scan is { IsAvailable: false } ? scan.Error ?? Localization.Text("tasks.unavailable")
            : tasks.Count == 0 ? Localization.Text("tasks.emptyProject")
            : selectedTasks == 0 ? Localization.Format("tasks.selectTasks", tasks.Count)
            : Localization.Format("tasks.selection", selectedTasks, waitingTasks);
    }

    private static string FormatQuota(QuotaWindow? window)
    {
        if (window is null) return Localization.Text("quota.unknown");
        var percentage = Math.Clamp(window.UsedPercent, 0, 100);
        var reset = window.ResetsAt.ToLocalTime().ToString(Localization.IsChinese ? "M月d日 HH:mm" : "MMM d, h:mm tt", CultureInfo.CurrentCulture);
        return $"{Localization.Format("quota.used", percentage.ToString("0", CultureInfo.CurrentCulture))}  ·  {Localization.Format("quota.reset", reset)}";
    }

    private static double Progress(QuotaWindow? window) => window is null ? 0 : Math.Clamp(window.UsedPercent, 0, 100);
    private static string FormatTime(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static string FormatActivity(string raw)
    {
        var split = raw.IndexOf(" | ", StringComparison.Ordinal);
        if (split < 0) return raw;
        if (!DateTimeOffset.TryParse(raw[..split], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)) return raw[(split + 3)..];
        var formatted = time.ToLocalTime().ToString(Localization.IsChinese ? "MM-dd HH:mm" : "MMM d, h:mm tt", CultureInfo.CurrentCulture);
        return $"{formatted}  ·  {raw[(split + 3)..]}";
    }

    private static void Replace(ObservableCollection<string> collection, IEnumerable<string> values)
    {
        collection.Clear();
        foreach (var value in values) collection.Add(value);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
