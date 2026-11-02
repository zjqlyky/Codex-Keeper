using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodexKeeper.Windows.Models;

namespace CodexKeeper.Windows.Services;

/// <summary>
/// Reads Codex's current state database and its registered rollout files. A session is never selected by filename alone.
/// Windows ships the read-only SQLite API in winsqlite3.dll, so this project has no third-party runtime dependency.
/// </summary>
public sealed class CodexSessionScanner(SettingsStore store)
{
    private const int MaximumTasks = 500;
    private static readonly Regex StateName = new(@"^state(?:_[0-9]+)?\.sqlite$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public CodexSessionScan Scan(string? boundAccount)
    {
        var savedProjects = CodexProjectDiscovery.ReadSavedProjects(store.CodexHome);
        try
        {
            var database = FindCurrentDatabase(store.CodexHome);
            if (database is null) return new(false, [], false, Localization.Text("activity.taskScan"), savedProjects, []);
            var catalog = ReadThreads(database);
            var currentThreadNames = ReadCurrentThreadNames(store.CodexHome);
            var paused = new List<PausedTask>();
            var tasks = new List<CodexTask>();
            var running = false;
            var unverifiedOwnedTask = false;
            var unlocatedUnverifiedTask = false;
            var available = !string.IsNullOrWhiteSpace(boundAccount) && catalog.HasAccountColumn && !catalog.Truncated;
            foreach (var row in catalog.Rows)
            {
                if (row.IsSubagent) continue;
                var ownsTask = !string.IsNullOrWhiteSpace(boundAccount) && StringComparer.Ordinal.Equals(row.AccountId, boundAccount);
                var couldBelongToAccount = ownsTask || string.IsNullOrWhiteSpace(row.AccountId);
                RolloutState? state = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(row.RolloutPath))
                        state = ReadRollout(ResolveRolloutPath(row.RolloutPath, store.CodexHome), row.ThreadId);
                }
                catch { } // A single unavailable file must not hide the remaining catalog.
                if (state?.IdentityVerified == true && state.IsSubagent) continue;
                var cwd = NormalizeDirectory(row.Cwd) ?? NormalizeDirectory(state?.Cwd);
                if (cwd is null)
                {
                    if (couldBelongToAccount)
                    {
                        unverifiedOwnedTask = true;
                        unlocatedUnverifiedTask = true;
                    }
                    continue;
                }
                var verified = state is { IdentityVerified: true, ContentReliable: true, RunningKnown: true } &&
                    ProjectPath.Matches(cwd, state.Cwd);
                if (couldBelongToAccount && !verified) unverifiedOwnedTask = true;
                if (couldBelongToAccount && state?.IsRunning == true) running = true;
                var project = CodexProjectDiscovery.FindSavedProject(savedProjects, cwd)?.Name ?? CodexProjectDiscovery.LeafName(cwd);
                var blocked = verified && state!.BlockedAt is not null;
                var evidence = blocked && (state!.FiveHour is { UsedPercent: >= 100 } || state.Weekly is { UsedPercent: >= 100 });
                // Let the user save their selection even while quota evidence is incomplete.
                // This does not make the task resumable: only verified, owned quota stops enter PausedTasks below.
                var canSelect = Directory.Exists(cwd);
                string? reason = null;
                if (!verified) reason = Localization.Text("activity.taskScan");
                else if (!ownsTask) reason = Localization.Text(string.IsNullOrWhiteSpace(boundAccount) ? "activity.account" : "activity.authChanged");
                else if (!Directory.Exists(cwd)) reason = Localization.Text("activity.workspace");
                else if (blocked && !evidence) reason = Localization.Text("tasks.quotaEvidencePending");
                // Codex's sidebar title is kept in session_index.jsonl. The SQLite title can remain the
                // original prompt after a conversation has been renamed in Codex.
                var title = currentThreadNames.TryGetValue(row.ThreadId, out var currentTitle) ? currentTitle : row.Title;
                tasks.Add(new CodexTask(row.ThreadId, DisplayTitle(title, project), project, cwd, row.UpdatedAt,
                    verified && state!.IsRunning, blocked && evidence, canSelect, reason, verified));
                if (!ownsTask || !verified || !Directory.Exists(cwd) || !blocked || !evidence) continue;
                var at = state!.BlockedAt!.Value;
                paused.Add(new PausedTask(row.ThreadId, project, cwd, at, state.StoppedTurnId,
                    StopResetAt(state.FiveHourAtStop, at), StopResetAt(state.WeeklyAtStop, at),
                    state.FiveHour is { UsedPercent: >= 100 }, state.Weekly is { UsedPercent: >= 100 },
                    SettingsStore.StableAttemptKey(row.ThreadId, at)));
            }
            var projects = CodexProjectDiscovery.Merge(savedProjects, tasks.Select(task => task.WorkingDirectory));
            var error = available ? null : Localization.Text(string.IsNullOrWhiteSpace(boundAccount) ? "activity.account" : "activity.taskScan");
            return new(available, paused.OrderByDescending(item => item.BlockedAt).ToList(), running, error,
                projects, tasks.OrderByDescending(task => task.UpdatedAt).ToList(), unverifiedOwnedTask, unlocatedUnverifiedTask);
        }
        catch (Exception)
        {
            // Keep parser and database details out of the UI and activity log; they can include local paths.
            return new(false, [], false, Localization.Text("activity.taskScan"), savedProjects, []);
        }
    }

    private static string? FindCurrentDatabase(string codexHome)
    {
        if (!Directory.Exists(codexHome)) return null;
        return Directory.EnumerateFiles(codexHome, "state*.sqlite", SearchOption.TopDirectoryOnly)
            .Where(path => StateName.IsMatch(Path.GetFileName(path)))
            .OrderByDescending(path => Math.Max(File.GetLastWriteTimeUtc(path).Ticks,
                File.Exists(path + "-wal") ? File.GetLastWriteTimeUtc(path + "-wal").Ticks : 0))
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, string> ReadCurrentThreadNames(string codexHome)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexPath = Path.Combine(codexHome, "session_index.jsonl");
        try
        {
            using var stream = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false));
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject item) continue;
                    var threadId = item["id"]?.ToString();
                    var name = item["thread_name"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(name))
                        names[threadId] = name.Trim();
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return names;
    }

    private static string ResolveRolloutPath(string path, string codexHome)
    {
        var normalized = path.Replace('/', Path.DirectorySeparatorChar);
        return ProjectPath.Normalize(Path.IsPathRooted(normalized) ? normalized : Path.Combine(codexHome, normalized));
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return ProjectPath.Normalize(path); }
        catch { return null; }
    }

    private static ThreadCatalog ReadThreads(string databasePath)
    {
        var result = new List<ThreadRow>();
        var code = NativeSqlite.sqlite3_open_v2(databasePath, out var database, NativeSqlite.ReadOnly, IntPtr.Zero);
        if (code != NativeSqlite.Ok)
        {
            if (database != IntPtr.Zero) NativeSqlite.sqlite3_close_v2(database);
            throw new InvalidDataException("Codex state database is unavailable.");
        }
        try
        {
            NativeSqlite.sqlite3_busy_timeout(database, 1000);
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var schema = Prepare(database, "PRAGMA table_info(threads)");
            try
            {
                while ((code = NativeSqlite.sqlite3_step(schema)) == NativeSqlite.Row)
                    if (NativeSqlite.ReadText(schema, 1) is { } name) columns.Add(name);
                if (code != NativeSqlite.Done) throw new InvalidDataException("Codex state schema could not be read.");
            }
            finally { NativeSqlite.sqlite3_finalize(schema); }
            if (new[] { "id", "rollout_path", "cwd", "title", "updated_at", "archived" }.Any(column => !columns.Contains(column)))
                throw new InvalidDataException("Codex state database schema is not recognized.");
            var hasAccount = columns.Contains("creator_account_id");
            var accountColumn = hasAccount ? "creator_account_id" : "NULL";
            var sourceColumn = columns.Contains("source") ? "source" : "NULL";
            var threadSourceColumn = columns.Contains("thread_source") ? "thread_source" : "NULL";
            var parentColumn = columns.Contains("attached_thread_parent_id") ? "attached_thread_parent_id" : "NULL";
            var statement = Prepare(database, $"SELECT id, rollout_path, {accountColumn}, cwd, title, updated_at, {sourceColumn}, {threadSourceColumn}, {parentColumn} FROM threads WHERE archived = 0 ORDER BY updated_at DESC LIMIT {MaximumTasks + 1}");
            try
            {
                while ((code = NativeSqlite.sqlite3_step(statement)) == NativeSqlite.Row)
                {
                    var threadId = NativeSqlite.ReadText(statement, 0);
                    var rollout = NativeSqlite.ReadText(statement, 1);
                    if (!string.IsNullOrWhiteSpace(threadId))
                        result.Add(new ThreadRow(threadId, rollout, NativeSqlite.ReadText(statement, 2),
                            NativeSqlite.ReadText(statement, 3), NativeSqlite.ReadText(statement, 4), ParseUnixTime(NativeSqlite.ReadText(statement, 5)),
                            NativeSqlite.ReadText(statement, 6), NativeSqlite.ReadText(statement, 7), NativeSqlite.ReadText(statement, 8)));
                }
                if (code != NativeSqlite.Done) throw new InvalidDataException("Codex state database could not be read.");
            }
            finally { NativeSqlite.sqlite3_finalize(statement); }
            return new ThreadCatalog(result.Take(MaximumTasks).ToList(), hasAccount, result.Count > MaximumTasks);
        }
        finally { NativeSqlite.sqlite3_close_v2(database); }
    }

    private static IntPtr Prepare(IntPtr database, string sql)
    {
        var code = NativeSqlite.sqlite3_prepare_v2(database, sql, -1, out var statement, IntPtr.Zero);
        if (code == NativeSqlite.Ok) return statement;
        if (statement != IntPtr.Zero) NativeSqlite.sqlite3_finalize(statement);
        throw new InvalidDataException("Codex state query could not be prepared.");
    }

    private static RolloutState ReadRollout(string path, string expectedThreadId)
    {
        var result = new RolloutState();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var snapshotLength = stream.Length;
        var headerBytes = new List<byte>();
        while (stream.Position < snapshotLength && headerBytes.Count < 64 * 1024)
        {
            var value = stream.ReadByte();
            if (value < 0 || value == '\n') break;
            headerBytes.Add((byte)value);
        }
        if (headerBytes.Count == 0 || headerBytes.Count >= 64 * 1024) return result;
        var utf8 = new System.Text.UTF8Encoding(false, true);
        var headerLine = utf8.GetString(headerBytes.ToArray());
        try
        {
            if (JsonNode.Parse(headerLine) is not JsonObject header || header["type"]?.ToString() != "session_meta" || header["payload"] is not JsonObject metadata)
                return result;
            result.HeaderRead = true;
            if (metadata["id"]?.ToString() != expectedThreadId) return result;
            result.IdentityVerified = true;
            result.Cwd = metadata["cwd"]?.ToString() ?? "";
            result.IsSubagent = metadata["source"] is JsonObject source && source["subagent"] is not null;
        }
        catch (JsonException) { return result; }

        if (result.IsSubagent) return result;
        const int tailLimit = 512 * 1024;
        var contentStart = stream.Position;
        var truncated = snapshotLength - contentStart > tailLimit;
        var tailStart = truncated ? snapshotLength - tailLimit : contentStart;
        stream.Position = tailStart;
        var bytes = new byte[checked((int)(snapshotLength - tailStart))];
        stream.ReadExactly(bytes);
        var offset = 0;
        if (truncated)
        {
            offset = Array.IndexOf(bytes, (byte)'\n') + 1;
            if (offset == 0) return result;
        }
        using var reader = new StringReader(utf8.GetString(bytes, offset, bytes.Length - offset));
        result.ContentReliable = true;
        result.RunningKnown = !truncated;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject? entry;
            try { entry = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { result.ContentReliable = false; continue; }
            if (entry?["payload"] is not JsonObject payload) { result.ContentReliable = false; continue; }
            var entryType = entry["type"]?.ToString();
            var type = payload["type"]?.ToString() ?? "";
            if (entryType == "session_meta")
            {
                continue;
            }
            if (!result.IdentityVerified) continue;
            var eventThreadId = payload["thread_id"]?.ToString() ?? payload["threadId"]?.ToString();
            if (eventThreadId is not null && eventThreadId != expectedThreadId) continue;
            var turnId = payload["turn_id"]?.ToString() ?? payload["turnId"]?.ToString();
            if (type == "task_started")
            {
                result.IsRunning = true;
                result.RunningKnown = true;
                result.BlockedAt = null;
                result.StoppedTurnId = null;
                if (turnId is not null) result.CurrentTurnId = turnId;
            }
            else if (entryType == "turn_context" && turnId is not null) result.CurrentTurnId = turnId;

            var at = ParseTimestamp(entry["timestamp"]?.ToString());
            if (at is not null && result.BlockedAt is { } previousBlock && at < previousBlock) continue;
            if (type == "token_count" && payload["rate_limits"] is JsonObject rateLimits && IsCodexQuota(rateLimits))
            {
                (result.FiveHour, result.Weekly) = DecodeWindows(rateLimits);
                var reached = rateLimits["rate_limit_reached_type"]?.ToString() ?? rateLimits["rateLimitReachedType"]?.ToString();
                if (!string.IsNullOrWhiteSpace(reached) && !reached.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    result.IsRunning = false;
                    result.RunningKnown = true;
                    result.BlockedAt = at;
                    result.StoppedTurnId = turnId ?? result.CurrentTurnId;
                    result.FiveHourAtStop = result.FiveHour;
                    result.WeeklyAtStop = result.Weekly;
                }
            }

            var blockBeforeEvent = result.BlockedAt;
            var hasError = payload["error"] is JsonNode errorNode && errorNode.ToJsonString() != "null";
            var errorText = hasError ? payload["error"]!.ToJsonString() : "";
            var quotaError = IsQuotaError(errorText) || IsQuotaError(payload["message"]?.ToString() ?? "");
            if ((type == "error" || type == "turn_failed" || type == "task_complete") && quotaError && payload["will_retry"]?.ToString() != "true")
            {
                result.IsRunning = false;
                result.RunningKnown = true;
                result.BlockedAt = at;
                result.StoppedTurnId = turnId ?? result.CurrentTurnId;
                result.FiveHourAtStop = result.FiveHour;
                result.WeeklyAtStop = result.Weekly;
            }
            else if ((type == "error" || type == "turn_failed" || type == "task_complete") && hasError && payload["will_retry"]?.ToString() != "true")
            {
                result.IsRunning = false;
                result.RunningKnown = true;
                result.BlockedAt = null;
            }
            else if (type == "task_complete" || type == "turn_aborted")
            {
                result.IsRunning = false;
                result.RunningKnown = true;
                if (type == "turn_aborted" || type == "task_complete" && !hasError) result.BlockedAt = null;
            }

            // A user or assistant event after a quota error means this is no longer an untouched paused task.
            var role = payload["role"]?.ToString();
            if (blockBeforeEvent is not null && result.BlockedAt is not null && type == "user_message") result.BlockedAt = null;
            if ((role == "user" || role == "assistant") && blockBeforeEvent is not null && result.BlockedAt is not null)
            {
                var content = payload["content"] as JsonArray;
                var hasText = content?.OfType<JsonObject>().Any(item => !string.IsNullOrWhiteSpace(item["text"]?.ToString())) == true;
                if (hasText) result.BlockedAt = null;
            }
        }
        if (result.BlockedAt is not null)
        {
            result.FiveHour = result.FiveHourAtStop ?? result.FiveHour;
            result.Weekly = result.WeeklyAtStop ?? result.Weekly;
        }
        if (result.ContentReliable && !result.RunningKnown)
        {
            // A long turn can begin before the bounded tail. Treat it as busy until
            // a start/terminal event is visible, keeping a valid catalog available.
            result.IsRunning = true;
            result.RunningKnown = true;
        }
        return result;
    }

    private static bool IsCodexQuota(JsonObject rateLimits) =>
        (rateLimits["limit_id"]?.ToString() ?? rateLimits["limitId"]?.ToString() ?? "codex") == "codex";

    private static (RolloutQuotaWindow? FiveHour, RolloutQuotaWindow? Weekly) DecodeWindows(JsonObject rateLimits)
    {
        RolloutQuotaWindow? five = null;
        RolloutQuotaWindow? weekly = null;
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (rateLimits[key] is not JsonObject item) continue;
            var used = Number(item["usedPercent"] ?? item["used_percent"]);
            var duration = Number(item["windowDurationMins"] ?? item["window_duration_mins"] ?? item["window_minutes"]);
            var reset = DecodeReset(item["resetsAt"] ?? item["resets_at"]);
            if (used is null || used < 0 || duration is null || duration <= 0) continue;
            try
            {
                var window = new RolloutQuotaWindow(used.Value, checked((int)duration.Value), reset);
                if (window.WindowMinutes is >= 240 and <= 360) five = window;
                if (window.WindowMinutes is >= 8000 and <= 12000) weekly = window;
            }
            catch (OverflowException) { }
        }
        return (five, weekly);
    }

    private static DateTimeOffset? DecodeReset(JsonNode? value)
    {
        if (value is null) return null;
        if (Number(value) is { } number)
        {
            try
            {
                var milliseconds = checked((long)(number > 100_000_000_000 ? number : number * 1000));
                return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            }
            catch (ArgumentOutOfRangeException) { return null; }
            catch (OverflowException) { return null; }
        }
        return DateTimeOffset.TryParse(value.ToString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static DateTimeOffset? StopResetAt(RolloutQuotaWindow? window, DateTimeOffset blockedAt)
    {
        if (window is null) return null;
        if (window.ResetsAt is { } reset && reset > blockedAt) return reset;
        try { return blockedAt.AddMinutes(window.WindowMinutes); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static double? Number(JsonNode? value) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;

    private static DateTimeOffset? ParseTimestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static bool IsQuotaError(string value)
    {
        var lower = value.ToLowerInvariant();
        return new[] { "usage_limit_reached", "usage_limit_exceeded", "quota_exhausted", "insufficient_quota", "you’ve hit your usage limit", "you've hit your usage limit" }
            .Any(lower.Contains);
    }

    private static DateTimeOffset ParseUnixTime(string? value)
    {
        try
        {
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                return DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(number > 100_000_000_000 ? number : number * 1000)));
        }
        catch (ArgumentOutOfRangeException) { }
        catch (OverflowException) { }
        return DateTimeOffset.MinValue;
    }

    private static string DisplayTitle(string? title, string project)
    {
        var value = string.IsNullOrWhiteSpace(title) ? project : title.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 240 ? value : value[..240];
    }

    private sealed record ThreadRow(string ThreadId, string? RolloutPath, string? AccountId, string? Cwd, string? Title, DateTimeOffset UpdatedAt,
        string? Source, string? ThreadSource, string? ParentThreadId)
    {
        public bool IsSubagent
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ParentThreadId) || ThreadSource?.StartsWith("guardian_", StringComparison.OrdinalIgnoreCase) == true)
                    return true;
                try { return JsonNode.Parse(Source ?? "null") is JsonObject source && source["subagent"] is not null; }
                catch (JsonException) { return false; }
            }
        }
    }
    private sealed record ThreadCatalog(IReadOnlyList<ThreadRow> Rows, bool HasAccountColumn, bool Truncated);
    private sealed record RolloutQuotaWindow(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);

    private sealed class RolloutState
    {
        public bool HeaderRead { get; set; }
        public bool IdentityVerified { get; set; }
        public bool ContentReliable { get; set; }
        public bool RunningKnown { get; set; }
        public bool IsSubagent { get; set; }
        public bool IsRunning { get; set; }
        public string Cwd { get; set; } = "";
        public string? CurrentTurnId { get; set; }
        public string? StoppedTurnId { get; set; }
        public DateTimeOffset? BlockedAt { get; set; }
        public RolloutQuotaWindow? FiveHour { get; set; }
        public RolloutQuotaWindow? Weekly { get; set; }
        public RolloutQuotaWindow? FiveHourAtStop { get; set; }
        public RolloutQuotaWindow? WeeklyAtStop { get; set; }
    }

    private static class NativeSqlite
    {
        public const int Ok = 0;
        public const int Row = 100;
        public const int Done = 101;
        public const int ReadOnly = 0x00000001;

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
        public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2", CharSet = CharSet.Ansi)]
        public static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int byteCount, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

        public static string? ReadText(IntPtr statement, int column)
        {
            var pointer = sqlite3_column_text(statement, column);
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
        }
    }
}
