using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

public sealed class KeeperService(SettingsStore store)
{
    private static readonly TimeSpan NormalUsageInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CriticalUsageInterval = TimeSpan.FromSeconds(20);
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly UsageService _usageService = new(store);
    private readonly CodexSessionScanner _sessionScanner = new(store);
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private QuotaSnapshot? _usage;
    private CodexSessionScan? _sessionScan;
    private DateTimeOffset? _lastQuotaReadAt;
    private DateTimeOffset? _lastSessionScanAt;
    private DateTimeOffset _nextQuotaReadAt = DateTimeOffset.MinValue;
    private int _quotaFailureCount;
    private string _statusTitle = Localization.Text("status.paused");
    private string _statusDetail = "";
    private bool _busy;
    private string? _lastScanErrorLogged;
    private readonly HashSet<string> _earlyRecoveryNotices = new(StringComparer.Ordinal);

    public event EventHandler? StateChanged;

    public KeeperDashboardState GetState()
    {
        var settings = store.Snapshot();
        lock (_stateGate)
        {
            var quota = _usage ?? (settings.LastQuotaReadAt is { } readAt && (settings.LastFiveHour is not null || settings.LastWeekly is not null)
                ? new QuotaSnapshot(settings.LastFiveHour, settings.LastWeekly, readAt, null)
                : null);
            return new KeeperDashboardState(settings.Enabled, settings.AutoResume, settings.LaunchAtLogin, _busy, quota,
                _lastQuotaReadAt ?? settings.LastQuotaReadAt, _sessionScan, GetNextScheduledNode(DateTimeOffset.Now, settings.DailyAnchorMinutes),
                _statusTitle, _statusDetail, settings.RecentActivity, store.CodexHome, settings.CodexExecutable);
        }
    }

    public void Start()
    {
        lock (_stateGate)
        {
            if (_loop is not null) return;
            _lifetime = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoopAsync(_lifetime.Token));
        }
        Publish();
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_stateGate)
        {
            _lifetime?.Cancel();
            loop = _loop;
        }
        if (loop is not null)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(4)); } catch { }
        }
        lock (_stateGate)
        {
            _lifetime?.Dispose();
            _lifetime = null;
            _loop = null;
        }
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        try { store.Update(settings => settings.Enabled = enabled); }
        catch (Exception error)
        {
            SetStatus(Localization.Text("status.error"), SafeErrorMessage(error));
            return;
        }
        store.AddActivity(Localization.Text(enabled ? "action.enabledOn" : "action.enabledOff"));
        if (enabled)
        {
            lock (_stateGate) { _usage = null; _lastQuotaReadAt = null; }
            await RefreshNowAsync();
        }
        else SetStatus(Localization.Text("status.paused"), Localization.Text("action.enabledOff"));
        Publish();
    }

    public Task SetAutoResumeAsync(bool enabled)
    {
        try { store.Update(settings => settings.AutoResume = enabled); }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); }
        Publish();
        return Task.CompletedTask;
    }

    public Task SetLaunchAtLoginAsync(bool enabled)
    {
        var previous = store.Snapshot().LaunchAtLogin;
        try
        {
            store.Update(settings => settings.LaunchAtLogin = enabled);
            try { StartupService.SetEnabled(enabled); }
            catch
            {
                store.Update(settings => settings.LaunchAtLogin = previous);
                throw;
            }
        }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); return Task.CompletedTask; }
        Publish();
        return Task.CompletedTask;
    }

    public Task SaveScheduleAsync(int minutes)
    {
        try
        {
            store.Update(settings => settings.DailyAnchorMinutes = Math.Clamp(minutes, 0, 1439));
            store.AddActivity(Localization.Text("schedule.saved"));
        }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); }
        Publish();
        return Task.CompletedTask;
    }

    public Task SaveCodexExecutableAsync(string? path)
    {
        try { store.Update(settings => settings.CodexExecutable = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim())); }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); return Task.CompletedTask; }
        lock (_stateGate) { _usage = null; _lastQuotaReadAt = null; }
        Publish();
        return Task.CompletedTask;
    }

    public Task SetProjectSelectedAsync(string path, bool selected)
    {
        try
        {
            var project = _sessionScan?.Projects?.FirstOrDefault(item => ProjectPath.Matches(item.WorkingDirectory, path));
            if (project is null) return Task.CompletedTask;
            var normalized = ProjectPath.Normalize(project.WorkingDirectory);
            store.Update(settings =>
            {
                settings.SelectedProjectPaths.RemoveAll(item => ProjectPath.Matches(item, normalized));
                if (selected) settings.SelectedProjectPaths.Add(normalized);
                else
                {
                    var removedThreads = (_sessionScan?.Tasks ?? []).Where(item => !IsProjectSelected(settings, item.WorkingDirectory))
                        .Select(item => item.ThreadId).ToHashSet(StringComparer.Ordinal);
                    settings.SelectedThreadIds.RemoveAll(removedThreads.Contains);
                }
            });
        }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); }
        Publish();
        return Task.CompletedTask;
    }

    public Task SetTaskSelectedAsync(string threadId, bool selected)
    {
        try
        {
            var task = _sessionScan?.Tasks?.FirstOrDefault(item => item.ThreadId == threadId);
            if (selected && (task is null || !task.CanSelect || !IsProjectSelected(store.Snapshot(), task.WorkingDirectory)))
                return Task.CompletedTask;
            store.Update(settings =>
            {
                settings.SelectedThreadIds.RemoveAll(item => item == threadId);
                if (selected) settings.SelectedThreadIds.Add(threadId);
            });
        }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); }
        Publish();
        return Task.CompletedTask;
    }

    public async Task RefreshProjectsAsync()
    {
        if (!await _operationGate.WaitAsync(0)) return;
        try { await ScanSessionsCoreAsync(CancellationToken.None); }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), SafeErrorMessage(error)); }
        finally { _operationGate.Release(); Publish(); }
    }

    public async Task RefreshNowAsync()
    {
        if (!await _operationGate.WaitAsync(0))
        {
            SetStatus(Localization.Text("status.working"), Localization.Text("status.loading"));
            return;
        }
        SetBusy(true, Localization.Text("status.loading"), "");
        try
        {
            await ScanSessionsCoreAsync(CancellationToken.None);
            await RefreshUsageCoreAsync(CancellationToken.None);
            store.AddActivity(Localization.Text("activity.sync"));
            SetStatus(Localization.Text(store.Snapshot().Enabled ? "status.ready" : "status.paused"),
                store.Snapshot().Enabled ? Localization.Text("status.working") : Localization.Text("action.enabledOff"));
        }
        catch (Exception error)
        {
            var detail = SafeErrorMessage(error);
            store.AddActivity(Localization.Format("activity.readFailed", detail));
            SetStatus(Localization.Text("status.error"), detail);
        }
        finally
        {
            SetBusy(false);
            _operationGate.Release();
            Publish();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (store.IsHealthy) store.AddActivity(Localization.Text("activity.started"));
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!store.IsHealthy)
                {
                    SetStatus(Localization.Text("status.error"), Localization.Text("activity.settingsFailed"));
                    await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                    continue;
                }
                var settings = store.Snapshot();
                if (settings.Enabled)
                {
                    await _operationGate.WaitAsync(cancellationToken);
                    try
                    {
                        var now = DateTimeOffset.Now;
                        var dueNode = GetCurrentScheduledNode(now, settings.DailyAnchorMinutes);
                        var current = GetState();
                        var pendingSchedule = HasSelectedProjects(settings) && dueNode is { } scheduled &&
                            !settings.PingAttempts.Contains(AttemptKey("ping", current.Quota?.AccountId ?? "", scheduled), StringComparer.Ordinal);
                        var pendingResume = settings.AutoResume && current.SessionScan?.PausedTasks.Any(task =>
                            IsTaskSelected(settings, task) && !settings.ResumeAttempts.Contains(task.AttemptKey, StringComparer.Ordinal) &&
                            ExpectedRecovery(task) is { } recovery && recovery <= now.AddMinutes(2)) == true;
                        var critical = pendingSchedule || pendingResume;
                        var readAge = current.LastQuotaReadAt is { } at ? now - at : TimeSpan.MaxValue;
                        var readDue = now >= _nextQuotaReadAt && readAge >= (critical ? CriticalUsageInterval : NormalUsageInterval);
                        if (readDue) await RefreshUsageCoreAsync(cancellationToken);

                        now = DateTimeOffset.Now;
                        if (current.SessionScan is null || _lastSessionScanAt is null || now - _lastSessionScanAt >= TimeSpan.FromSeconds(20))
                            await ScanSessionsCoreAsync(cancellationToken);

                        if (GetCurrentScheduledNode(now, store.Snapshot().DailyAnchorMinutes) is { } node)
                            await ProcessScheduledNodeAsync(node, cancellationToken);
                        if (store.Snapshot().AutoResume)
                            await ProcessPausedTasksAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        SetStatus(Localization.Text("status.error"), SafeErrorMessage(error));
                    }
                    finally { _operationGate.Release(); }
                    Publish();
                }
                else
                {
                    if (_lastSessionScanAt is null || DateTimeOffset.Now - _lastSessionScanAt >= TimeSpan.FromSeconds(20))
                    {
                        await _operationGate.WaitAsync(cancellationToken);
                        try { await ScanSessionsCoreAsync(cancellationToken); }
                        finally { _operationGate.Release(); }
                    }
                    SetStatus(Localization.Text("status.paused"), Localization.Text("action.enabledOff"));
                }
                await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { SetStatus(Localization.Text("status.error"), error.Message); }
        finally { SetBusy(false); Publish(); }
    }

    private async Task RefreshUsageCoreAsync(CancellationToken cancellationToken)
    {
        SetBusy(true, Localization.Text("status.loading"), "");
        QuotaSnapshot snapshot;
        try
        {
            snapshot = await _usageService.ReadAsync(cancellationToken);
            _quotaFailureCount = 0;
            _nextQuotaReadAt = DateTimeOffset.MinValue;
        }
        catch
        {
            _quotaFailureCount++;
            var delay = Math.Min(300, 20 * Math.Pow(2, Math.Min(_quotaFailureCount - 1, 4)));
            _nextQuotaReadAt = DateTimeOffset.Now.AddSeconds(delay);
            throw;
        }
        var previousAccount = store.Snapshot().CodexAccountId;
        if (previousAccount is not null && snapshot.AccountId != previousAccount)
        {
            store.AddActivity(Localization.Text("activity.authChanged"));
            _sessionScan = null;
        }
        lock (_stateGate)
        {
            _usage = snapshot;
            _lastQuotaReadAt = snapshot.CapturedAt;
        }
        store.Update(settings =>
        {
            settings.CodexAccountId = snapshot.AccountId;
            settings.LastFiveHour = snapshot.FiveHour;
            settings.LastWeekly = snapshot.Weekly;
            settings.LastQuotaReadAt = snapshot.CapturedAt;
        });
        SetStatus(Localization.Text(store.Snapshot().Enabled ? "status.ready" : "status.paused"),
            Localization.Text("status.working"));
    }

    private Task ScanSessionsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = store.Snapshot().CodexAccountId ?? _usage?.AccountId ?? UsageService.ReadAuthAccountId(store.CodexHome);
        var scan = _sessionScanner.Scan(account);
        lock (_stateGate)
        {
            _sessionScan = scan;
            _lastSessionScanAt = DateTimeOffset.Now;
        }
        if (!scan.IsAvailable && scan.Error != _lastScanErrorLogged)
        {
            store.AddActivity(Localization.Text("activity.taskScan"));
            _lastScanErrorLogged = scan.Error;
        }
        else if (scan.IsAvailable) _lastScanErrorLogged = null;
        return Task.CompletedTask;
    }

    private async Task ProcessScheduledNodeAsync(DateTimeOffset node, CancellationToken cancellationToken)
    {
        var settings = store.Snapshot();
        if (!settings.Enabled || !HasSelectedProjects(settings)) return;
        var account = _usage?.AccountId;
        if (string.IsNullOrWhiteSpace(account)) return;
        var key = AttemptKey("ping", account, node);
        if (settings.PingAttempts.Contains(key, StringComparer.Ordinal)) return;
        var scan = _sessionScan;
        if (scan is null || !scan.IsAvailable)
        {
            SetStatus(Localization.Text("status.error"), Localization.Text("activity.taskScan"));
            return;
        }
        if (scan.HasUnverifiedTask && HasUnverifiedSelectedTask(scan, settings))
        {
            SetStatus(Localization.Text("status.error"), Localization.Text("activity.taskScan"));
            return;
        }
        if (scan.HasRunningTask)
        {
            store.Update(value => value.PingAttempts.Add(key));
            store.AddActivity(Localization.Text("activity.busy"));
            return;
        }
        var now = DateTimeOffset.Now;
        if (_usage is null || !_usage.IsFresh(now))
        {
            try { await RefreshUsageCoreAsync(cancellationToken); }
            catch (Exception error)
            {
                SetStatus(Localization.Text("status.error"), error.Message);
                return;
            }
        }
        var usage = _usage;
        if (usage is null || usage.AccountId != account || !usage.IsFresh(DateTimeOffset.Now)) return;
        var exhausted = (usage.FiveHour?.UsedPercent ?? 0) >= 100 || (usage.Weekly?.UsedPercent ?? 0) >= 100;
        if (exhausted)
        {
            store.Update(value => value.PingAttempts.Add(key));
            store.AddActivity(Localization.Text("activity.quotaWait"));
            return;
        }
        if (usage.HasActiveFiveHourWindow == true)
        {
            store.Update(value => value.PingAttempts.Add(key));
            return;
        }

        store.Update(value => value.PingAttempts.Add(key)); // A submitted or uncertain turn is never sent twice for this node.
        SetBusy(true, Localization.Text("status.working"), "");
        try
        {
            var model = await _usageService.FindKeepAliveModelAsync(cancellationToken);
            var after = await RunKeepAliveAsync(usage, model, node, cancellationToken);
            if (after is not null && after.HasAvailableQuota(DateTimeOffset.Now) && after.HasActiveFiveHourWindow == true && after.AccountId == account)
            {
                UpdateUsage(after);
                store.AddActivity(Localization.Text("activity.ping"));
            }
            else store.AddActivity(Localization.Text("activity.pingSkipped"));
        }
        catch (Exception error)
        {
            var detail = SafeErrorMessage(error);
            store.AddActivity(Localization.Format("activity.pingFailed", detail));
            SetStatus(Localization.Text("status.error"), detail);
        }
        finally { SetBusy(false); }
    }

    private async Task<QuotaSnapshot?> RunKeepAliveAsync(QuotaSnapshot before, PingModel model, DateTimeOffset node, CancellationToken cancellationToken)
    {
        var isolatedRoot = Path.Combine(Path.GetTempPath(), "CodexKeeper-Ping-" + Guid.NewGuid().ToString("N"));
        try
        {
        var currentAccount = UsageService.ReadAuthAccountId(store.CodexHome);
        if (string.IsNullOrWhiteSpace(currentAccount) || currentAccount != before.AccountId)
            throw new InvalidOperationException(Localization.Text("activity.authChanged"));

        var home = Path.Combine(isolatedRoot, "home");
        var work = Path.Combine(isolatedRoot, "work");
        Directory.CreateDirectory(isolatedRoot);
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(work);
        var sourceAuth = Path.Combine(store.CodexHome, "auth.json");
        var authCopy = Path.Combine(home, "auth.json");
        File.Copy(sourceAuth, authCopy, overwrite: true);
        if (UsageService.ReadAuthAccountId(home) != currentAccount)
            throw new InvalidOperationException(Localization.Text("activity.authChanged"));
        var config = """
            model = "gpt-6-luna"
            model_provider = "keeper_http"
            sandbox_mode = "read-only"
            approval_policy = "never"
            forced_login_method = "chatgpt"
            developer_instructions = "Reply only OK. Do not call tools or read files."
            features.daemon_auto_start = false
            features.plugins = false
            features.remote_plugin = false
            model_providers.keeper_http.name = "OpenAI"
            model_providers.keeper_http.requires_openai_auth = true
            model_providers.keeper_http.supports_websockets = false
            model_providers.keeper_http.wire_api = "responses"
            """;
        await File.WriteAllTextAsync(Path.Combine(home, "config.toml"), config, new UTF8Encoding(false), cancellationToken);

        var executable = CodexLocator.Find(store.Snapshot());
        await using var client = await CodexAppServerClient.ConnectAsync(executable, home, usageOnly: true, cancellationToken);
        var accountRead = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, cancellationToken);
        if (accountRead["account"]?["type"]?.ToString() != "chatgpt" || UsageService.ReadAuthAccountId(home) != currentAccount)
            throw new InvalidOperationException(Localization.Text("activity.authChanged"));
        var started = await client.RequestAsync("thread/start", new JsonObject
        {
            ["model"] = model.Model,
            ["reasoningEffort"] = model.ReasoningEffort,
            ["cwd"] = work,
            ["sandbox"] = "read-only",
            ["approvalPolicy"] = "never",
            ["ephemeral"] = true,
            ["developerInstructions"] = "Reply only OK. Do not call tools or read files."
        }, cancellationToken);
        var threadId = started["thread"]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(threadId)) throw new InvalidDataException("Codex did not create a keep-alive thread.");

        // Recheck every input immediately before submitting work. The CLI can be used
        // independently while this isolated app-server is starting.
        var preflight = await ReadKeepAlivePreflightAsync(before.AccountId!, node, cancellationToken);
        if (preflight is null) return null;
        var turn = await client.RequestAsync("turn/start", new JsonObject
        {
            ["threadId"] = threadId,
            ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok", ["text_elements"] = new JsonArray() })
        }, cancellationToken);
        var turnId = turn["turn"]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidDataException("Codex did not confirm the keep-alive turn.");

        var streamedResponse = new StringBuilder();
        string? finalResponse = null;
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonObject message;
            try { message = await client.NextMessageAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; }
            var method = message["method"]?.ToString();
            var parameters = message["params"] as JsonObject;
            if (parameters is null || (parameters["threadId"]?.ToString() ?? parameters["thread_id"]?.ToString()) != threadId) continue;
            var eventTurn = parameters["turnId"]?.ToString() ?? parameters["turn_id"]?.ToString() ?? parameters["turn"]?["id"]?.ToString();
            if (eventTurn is not null && eventTurn != turnId) continue;
            if (method == "item/agentMessage/delta") streamedResponse.Append(parameters["delta"]?.ToString());
            if (method == "item/completed" && parameters["item"] is JsonObject item && item["type"]?.ToString() == "agentMessage")
                finalResponse = item["text"]?.ToString();
            if (method == "turn/completed")
            {
                if (parameters["turn"]?["items"] is JsonArray items)
                    finalResponse = items.OfType<JsonObject>().Where(finalItem => finalItem["type"]?.ToString() == "agentMessage")
                        .Select(finalItem => finalItem["text"]?.ToString()).LastOrDefault(value => !string.IsNullOrWhiteSpace(value));
                var status = parameters["turn"]?["status"]?.ToString();
                var response = finalResponse ?? streamedResponse.ToString();
                if (status != "completed" || !StringComparer.OrdinalIgnoreCase.Equals(response.Trim(), "OK"))
                    throw new InvalidOperationException(Localization.Text("activity.pingPending"));
                var after = await _usageService.ReadAsync(cancellationToken);
                return after;
            }
            if (message["id"] is not null && (method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"))
            {
                await client.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId }, cancellationToken);
                throw new InvalidOperationException(Localization.Text("activity.pingPending"));
            }
        }
        throw new TimeoutException(Localization.Text("activity.pingPending"));
        }
        finally
        {
            try
            {
                var tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullRoot = Path.GetFullPath(isolatedRoot);
                if (fullRoot.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(fullRoot))
                    Directory.Delete(fullRoot, recursive: true);
            }
            catch { }
        }
    }

    private async Task ProcessPausedTasksAsync(CancellationToken cancellationToken)
    {
        var state = GetState();
        var scan = state.SessionScan;
        var usage = _usage;
        if (scan is null || !scan.IsAvailable || usage is null) return;
        var settings = store.Snapshot();
        if (!settings.Enabled || !settings.AutoResume) return;

        foreach (var task in scan.PausedTasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            settings = store.Snapshot();
            if (!settings.Enabled || !settings.AutoResume) return;
            if (!IsTaskSelected(settings, task)) continue;
            if (settings.ResumeAttempts.Contains(task.AttemptKey, StringComparer.Ordinal)) continue;
            if (task.FiveHourWasExhausted && task.FiveHourResetAt is null || task.WeeklyWasExhausted && task.WeeklyResetAt is null ||
                !task.FiveHourWasExhausted && !task.WeeklyWasExhausted)
            {
                store.Update(value => value.ResumeAttempts.Add(task.AttemptKey));
                SetStatus(Localization.Text("status.ready"), Localization.Text("activity.noQuotaEvidence"));
                continue;
            }
            if (usage.AccountId is null || usage.CapturedAt <= task.BlockedAt || !usage.IsFresh(DateTimeOffset.Now)) continue;
            if (!usage.HasAvailableQuota(DateTimeOffset.Now))
            {
                SetStatus(Localization.Text("status.ready"), Localization.Text("activity.quotaWait"));
                continue;
            }
            var deadlines = new List<DateTimeOffset>();
            if (task.FiveHourWasExhausted && task.FiveHourResetAt is { } fiveReset) deadlines.Add(fiveReset);
            if (task.WeeklyWasExhausted && task.WeeklyResetAt is { } weeklyReset) deadlines.Add(weeklyReset);
            var expectedRecovery = deadlines.Max();
            if (DateTimeOffset.Now < expectedRecovery || usage.CapturedAt < expectedRecovery)
            {
                if (_earlyRecoveryNotices.Add(task.AttemptKey)) store.AddActivity(Localization.Text("activity.earlyRecovery"));
                SetStatus(Localization.Text("status.ready"), Localization.Text("activity.earlyRecovery"));
                continue;
            }
            if (task.FiveHourWasExhausted && (usage.FiveHour is null || usage.FiveHour.UsedPercent >= 100) ||
                task.WeeklyWasExhausted && (usage.Weekly is null || usage.Weekly.UsedPercent >= 100))
            {
                SetStatus(Localization.Text("status.ready"), Localization.Text("activity.quotaWait"));
                continue;
            }
            if (!Directory.Exists(task.WorkingDirectory))
            {
                store.Update(value => value.ResumeAttempts.Add(task.AttemptKey));
                store.AddActivity(Localization.Text("activity.workspace"));
                continue;
            }
            await ResumeTaskAsync(task, usage.AccountId, cancellationToken);
            break; // One task at a time; the next task is scanned after this turn ends.
        }
    }

    private async Task ResumeTaskAsync(PausedTask task, string accountId, CancellationToken cancellationToken)
    {
        SetBusy(true, Localization.Text("status.working"), Localization.Text("activity.waitReset"));
        try
        {
            var latestUsage = await _usageService.ReadAsync(cancellationToken);
            UpdateUsage(latestUsage);
            if (latestUsage.AccountId != accountId || latestUsage.CapturedAt <= task.BlockedAt || !latestUsage.HasAvailableQuota(DateTimeOffset.Now))
                return;
            var resets = new List<DateTimeOffset>();
            if (task.FiveHourWasExhausted && task.FiveHourResetAt is { } five) resets.Add(five);
            if (task.WeeklyWasExhausted && task.WeeklyResetAt is { } weekly) resets.Add(weekly);
            if (resets.Count == 0 || latestUsage.CapturedAt < resets.Max() || DateTimeOffset.Now < resets.Max())
            {
                if (_earlyRecoveryNotices.Add(task.AttemptKey)) store.AddActivity(Localization.Text("activity.earlyRecovery"));
                return;
            }
            if (task.FiveHourWasExhausted && latestUsage.FiveHour is null || task.WeeklyWasExhausted && latestUsage.Weekly is null) return;

            var executable = CodexLocator.Find(store.Snapshot());
            await using var client = await CodexAppServerClient.ConnectAsync(executable, store.CodexHome, usageOnly: false, cancellationToken);
            if (UsageService.ReadAuthAccountId(store.CodexHome) != accountId)
                throw new InvalidOperationException(Localization.Text("activity.authChanged"));
            var accountRead = await client.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, cancellationToken);
            if (accountRead["account"]?["type"]?.ToString() != "chatgpt")
                throw new InvalidOperationException(Localization.Text("activity.account"));
            var resumed = await client.RequestAsync("thread/resume", new JsonObject { ["threadId"] = task.ThreadId, ["excludeTurns"] = true }, cancellationToken);
            var thread = resumed["thread"] as JsonObject;
            var cwd = resumed["cwd"]?.ToString() ?? thread?["cwd"]?.ToString();
            if (thread?["id"]?.ToString() != task.ThreadId || !SamePath(cwd, task.WorkingDirectory))
                throw new InvalidOperationException("Codex session or project changed.");
            if (thread?["status"]?["type"]?.ToString() == "active") return;

            // Re-read the auth, quota, and session immediately before sending a
            // continuation. A user may have resumed or edited this session meanwhile.
            var preflight = await ReadResumePreflightAsync(task, accountId, cancellationToken);
            if (preflight is null) return;

            var prompt = Localization.IsChinese
                ? "Codex 额度已恢复。项目文件可能在任务暂停期间发生变化；请先核对当前项目文件和任务进度，再继续原任务。"
                : "Codex quota has recovered. Project files may have changed while this task was paused; review the current project and task state before continuing the original task.";
            store.Update(value => value.ResumeAttempts.Add(task.AttemptKey)); // Persist before content submission to prevent duplicate turns.
            var started = await client.RequestAsync("turn/start", new JsonObject
            {
                ["threadId"] = task.ThreadId,
                ["input"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray() })
            }, cancellationToken);
            var turnId = started["turn"]?["id"]?.ToString();
            if (string.IsNullOrWhiteSpace(turnId)) throw new InvalidDataException("Codex did not confirm the resume request.");

            var responded = false;
            var deadline = DateTimeOffset.UtcNow.AddHours(4);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonObject message;
                try { message = await client.NextMessageAsync(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { continue; }
                var method = message["method"]?.ToString();
                var parameters = message["params"] as JsonObject;
                if (parameters is null || (parameters["threadId"]?.ToString() ?? parameters["thread_id"]?.ToString()) != task.ThreadId) continue;
                var eventTurn = parameters["turnId"]?.ToString() ?? parameters["turn_id"]?.ToString() ?? parameters["turn"]?["id"]?.ToString();
                if (eventTurn is not null && eventTurn != turnId) continue;
                if (!responded && IsAssistantOutput(method, parameters))
                {
                    responded = true;
                    store.AddActivity(Localization.Text("activity.resume"));
                }
                if (message["id"] is not null && IsManualRequest(method))
                {
                    await client.RequestAsync("turn/interrupt", new JsonObject { ["threadId"] = task.ThreadId, ["turnId"] = turnId }, cancellationToken);
                    store.AddActivity(Localization.Text("activity.manualRequired"));
                    break;
                }
                if (method == "turn/completed")
                {
                    var status = parameters["turn"]?["status"]?.ToString();
                    if (status == "completed")
                    {
                        if (!responded) store.AddActivity(Localization.Text("activity.resume"));
                    }
                    else store.AddActivity(Localization.Format("activity.resumeFailed", status ?? "Codex stopped the task."));
                    break;
                }
            }
            if (DateTimeOffset.UtcNow >= deadline) store.AddActivity(Localization.Text("activity.resumePending"));
            UpdateUsage(latestUsage);
        }
        catch (Exception error)
        {
            var detail = SafeErrorMessage(error);
            store.AddActivity(Localization.Format("activity.resumeFailed", detail));
            SetStatus(Localization.Text("status.error"), detail);
        }
        finally { SetBusy(false); }
    }

    private async Task<QuotaSnapshot?> ReadKeepAlivePreflightAsync(string accountId, DateTimeOffset node, CancellationToken cancellationToken)
    {
        var settings = store.Snapshot();
        if (!store.IsHealthy || !settings.Enabled || !HasSelectedProjects(settings) || GetCurrentScheduledNode(DateTimeOffset.Now, settings.DailyAnchorMinutes) != node)
            return null;
        if (!StringComparer.Ordinal.Equals(UsageService.ReadAuthAccountId(store.CodexHome), accountId)) return null;

        var usage = await _usageService.ReadAsync(cancellationToken);
        if (usage.AccountId != accountId) return null;
        UpdateUsage(usage);
        if (!usage.HasAvailableQuota(DateTimeOffset.Now) || usage.HasActiveFiveHourWindow == true) return null;
        var scan = _sessionScanner.Scan(accountId);
        lock (_stateGate)
        {
            _sessionScan = scan;
            _lastSessionScanAt = DateTimeOffset.Now;
        }
        if (!scan.IsAvailable || scan.HasRunningTask || HasUnverifiedSelectedTask(scan, settings)) return null;

        settings = store.Snapshot();
        if (!store.IsHealthy || !settings.Enabled || !HasSelectedProjects(settings) ||
            GetCurrentScheduledNode(DateTimeOffset.Now, settings.DailyAnchorMinutes) != node ||
            UsageService.ReadAuthAccountId(store.CodexHome) != accountId) return null;
        return usage;
    }

    private async Task<QuotaSnapshot?> ReadResumePreflightAsync(PausedTask task, string accountId, CancellationToken cancellationToken)
    {
        var settings = store.Snapshot();
        if (!store.IsHealthy || !settings.Enabled || !settings.AutoResume || !IsTaskSelected(settings, task) || !Directory.Exists(task.WorkingDirectory) ||
            !StringComparer.Ordinal.Equals(UsageService.ReadAuthAccountId(store.CodexHome), accountId))
            return null;

        var usage = await _usageService.ReadAsync(cancellationToken);
        if (usage.AccountId != accountId) return null;
        UpdateUsage(usage);
        if (!IsQuotaRecoveredForTask(task, usage, DateTimeOffset.Now)) return null;
        var scan = _sessionScanner.Scan(accountId);
        lock (_stateGate)
        {
            _sessionScan = scan;
            _lastSessionScanAt = DateTimeOffset.Now;
        }
        if (!scan.IsAvailable || scan.HasRunningTask || scan.HasUnverifiedTask || !scan.PausedTasks.Any(candidate =>
                candidate.AttemptKey == task.AttemptKey && candidate.ThreadId == task.ThreadId &&
                candidate.BlockedAt == task.BlockedAt && candidate.StoppedTurnId == task.StoppedTurnId &&
                SamePath(candidate.WorkingDirectory, task.WorkingDirectory)))
            return null;

        settings = store.Snapshot();
        if (!store.IsHealthy || !settings.Enabled || !settings.AutoResume || !IsTaskSelected(settings, task) ||
            UsageService.ReadAuthAccountId(store.CodexHome) != accountId) return null;
        return usage;
    }

    private bool HasSelectedProjects(KeeperSettings settings) =>
        (_sessionScan?.Projects ?? []).Any(project => Directory.Exists(project.WorkingDirectory) &&
            settings.SelectedProjectPaths.Any(path => ProjectPath.Matches(path, project.WorkingDirectory)));

    private bool HasUnverifiedSelectedTask(CodexSessionScan scan, KeeperSettings settings) =>
        scan.HasUnlocatedUnverifiedTask || scan.HasUnverifiedTask &&
            (scan.Tasks ?? []).Any(task => !task.IsVerified && IsProjectSelected(settings, task.WorkingDirectory));

    private bool IsProjectSelected(KeeperSettings settings, string workingDirectory)
    {
        var owner = (_sessionScan?.Projects ?? []).Where(project => ProjectPath.Contains(project.WorkingDirectory, workingDirectory))
            .OrderByDescending(project => ProjectPath.Normalize(project.WorkingDirectory).Length).FirstOrDefault();
        return owner is not null && settings.SelectedProjectPaths.Any(path => ProjectPath.Matches(path, owner.WorkingDirectory));
    }

    private bool IsTaskSelected(KeeperSettings settings, PausedTask task) =>
        settings.SelectedThreadIds.Contains(task.ThreadId, StringComparer.Ordinal) && IsProjectSelected(settings, task.WorkingDirectory);

    private static bool IsQuotaRecoveredForTask(PausedTask task, QuotaSnapshot usage, DateTimeOffset now)
    {
        if (usage.CapturedAt <= task.BlockedAt || !usage.HasAvailableQuota(now)) return false;
        var resets = new List<DateTimeOffset>();
        if (task.FiveHourWasExhausted && task.FiveHourResetAt is { } five) resets.Add(five);
        else if (task.FiveHourWasExhausted) return false;
        if (task.WeeklyWasExhausted && task.WeeklyResetAt is { } weekly) resets.Add(weekly);
        else if (task.WeeklyWasExhausted) return false;
        if (resets.Count == 0 || now < resets.Max() || usage.CapturedAt < resets.Max()) return false;
        if (task.FiveHourWasExhausted && (usage.FiveHour is null || usage.FiveHour.UsedPercent >= 100)) return false;
        if (task.WeeklyWasExhausted && (usage.Weekly is null || usage.Weekly.UsedPercent >= 100)) return false;
        return true;
    }

    private static bool IsAssistantOutput(string? method, JsonObject parameters)
    {
        if (method == "item/agentMessage/delta" && !string.IsNullOrWhiteSpace(parameters["delta"]?.ToString())) return true;
        return (method is "item/started" or "item/completed") && parameters["item"] is JsonObject item &&
               item["type"]?.ToString() == "agentMessage" && !string.IsNullOrWhiteSpace(item["text"]?.ToString());
    }

    private static bool IsManualRequest(string? method) => method is
        "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" or "item/tool/requestUserInput" or
        "item/permissions/requestApproval" or "mcpServer/elicitation/request" or "execCommandApproval" or "applyPatchApproval";

    private void UpdateUsage(QuotaSnapshot usage)
    {
        lock (_stateGate) { _usage = usage; _lastQuotaReadAt = usage.CapturedAt; }
        store.Update(settings =>
        {
            settings.CodexAccountId = usage.AccountId;
            settings.LastFiveHour = usage.FiveHour;
            settings.LastWeekly = usage.Weekly;
            settings.LastQuotaReadAt = usage.CapturedAt;
        });
        Publish();
    }

    private void SetBusy(bool busy, string? title = null, string? detail = null)
    {
        lock (_stateGate)
        {
            _busy = busy;
            if (title is not null) _statusTitle = title;
            if (detail is not null) _statusDetail = detail;
        }
        Publish();
    }

    private void SetStatus(string title, string detail)
    {
        lock (_stateGate) { _statusTitle = title; _statusDetail = detail; }
        Publish();
    }

    private void Publish() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static string AttemptKey(string operation, string accountId, DateTimeOffset node)
    {
        var bytes = Encoding.UTF8.GetBytes($"{operation}|{accountId}|{node.ToUnixTimeSeconds()}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static DateTimeOffset? ExpectedRecovery(PausedTask task)
    {
        var resets = new List<DateTimeOffset>();
        if (task.FiveHourWasExhausted && task.FiveHourResetAt is { } five) resets.Add(five);
        else if (task.FiveHourWasExhausted) return null;
        if (task.WeeklyWasExhausted && task.WeeklyResetAt is { } weekly) resets.Add(weekly);
        else if (task.WeeklyWasExhausted) return null;
        return resets.Count == 0 ? null : resets.Max();
    }

    private static DateTimeOffset? GetCurrentScheduledNode(DateTimeOffset now, int anchorMinutes)
    {
        var candidates = new List<DateTimeOffset>();
        for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
        {
            var date = DateTime.SpecifyKind(now.LocalDateTime.Date.AddDays(dayOffset).AddMinutes(anchorMinutes), DateTimeKind.Local);
            var anchor = new DateTimeOffset(date);
            for (var index = 0; index < 4; index++) candidates.Add(anchor.AddHours(index * 5));
        }
        return candidates.Where(node => now >= node && now <= node.AddMinutes(10)).OrderByDescending(node => node).Select(node => (DateTimeOffset?)node).FirstOrDefault();
    }

    private static DateTimeOffset? GetNextScheduledNode(DateTimeOffset now, int anchorMinutes)
    {
        var candidates = new List<DateTimeOffset>();
        for (var dayOffset = -1; dayOffset <= 2; dayOffset++)
        {
            var date = DateTime.SpecifyKind(now.LocalDateTime.Date.AddDays(dayOffset).AddMinutes(anchorMinutes), DateTimeKind.Local);
            var anchor = new DateTimeOffset(date);
            for (var index = 0; index < 4; index++) candidates.Add(anchor.AddHours(index * 5));
        }
        return candidates.Where(node => node > now).OrderBy(node => node).Select(node => (DateTimeOffset?)node).FirstOrDefault();
    }

    private static bool SamePath(string? left, string right)
    {
        return ProjectPath.Matches(left, right);
    }

    private static string SafeErrorMessage(Exception error)
    {
        if (error is FileNotFoundException) return Localization.Text("activity.noCli");
        if (error is InvalidOperationException && new[] { "activity.account", "activity.authChanged", "activity.noModel", "activity.pingPending" }
                .Select(Localization.Text).Contains(error.Message, StringComparer.Ordinal))
            return error.Message;
        if (error is InvalidOperationException && error.Message == Localization.Text("activity.settingsFailed")) return error.Message;
        return Localization.Text("activity.readFailedGeneric");
    }
}
