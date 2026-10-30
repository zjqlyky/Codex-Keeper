using System.Runtime.InteropServices;
using System.IO;
using System.Text.Json;
using CodexKeeper.Windows.Models;
using CodexKeeper.Windows.Services;

namespace CodexKeeper.RegressionTests;

internal static class Program
{
    private const string Account = "fixture-account";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--inspect-live", var codexHome])
            {
                using var fixture = new Fixture();
                var store = new SettingsStore(fixture.Settings, Path.GetFullPath(codexHome));
                var scan = new CodexSessionScanner(store).Scan(UsageService.ReadAuthAccountId(store.CodexHome));
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    scan.IsAvailable,
                    Projects = scan.Projects?.Count ?? 0,
                    Tasks = scan.Tasks?.Count ?? 0,
                    EligibleTasks = scan.Tasks?.Count(task => task.CanAutoResume) ?? 0,
                    PausedTasks = scan.PausedTasks.Count,
                    RunningTasks = scan.Tasks?.Count(task => task.IsRunning) ?? 0,
                    scan.HasRunningTask,
                    scan.HasUnverifiedTask,
                    scan.HasUnlocatedUnverifiedTask
                }));
                return scan.IsAvailable ? 0 : 2;
            }
            if (args.Length != 0) throw new ArgumentException("Usage: no arguments, or --inspect-live <codexHome>");

            Run("String session source and task identity", StringSourceAndIdentity);
            Run("Saved project labels and longest root grouping", SavedProjectGrouping);
            Run("Account mismatch cannot resume", AccountOwnership);
            Run("Malformed rollout preserves catalog and stops automation", MalformedRollout);
            Run("Guardian sessions without rollouts do not poison the project scan", IgnoreGuardianStub);
            Run("Windows path boundaries and extended paths", WindowsPaths);
            await RunAsync("Explicit selections persist and deselection clears task", SelectionPersistenceAsync);
            await RunAsync("Nested projects require their own explicit consent", NestedProjectConsentAsync);
            Console.WriteLine("All 8 regression cases passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error.Message);
            return 1;
        }
    }

    private static void StringSourceAndIdentity()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Project("workspace");
        fixture.SaveProjects(("Example project", workspace));
        fixture.AddTask("thread-string", workspace, "Named quota task", Account, quotaBlocked: true);
        var scan = fixture.Scan();
        Require(scan.IsAvailable, "A normal source string should not invalidate the scan.");
        var task = (scan.Tasks ?? []).Single();
        Require(task.ThreadId == "thread-string" && task.Title == "Named quota task", "Task identity/title was lost.");
        Require(task.CanAutoResume && task.IsQuotaBlocked && !task.IsRunning, "Verified quota task should be selectable and paused.");
        Require(scan.PausedTasks.Single().ThreadId == task.ThreadId, "Quota pause evidence was not retained.");
    }

    private static void SavedProjectGrouping()
    {
        using var fixture = new Fixture();
        var parent = fixture.Project("workspace");
        var child = fixture.Project("workspace", "nested");
        var cwd = fixture.Project("workspace", "nested", "repo");
        fixture.SaveProjects(("Parent", parent), ("Nested project", child));
        fixture.AddTask("thread-nested", cwd, "Nested task", Account);
        var scan = fixture.Scan();
        Require(scan.IsAvailable, "A valid completed task should be readable.");
        var projects = scan.Projects ?? [];
        Require(projects.Count == 2, "Saved roots should be retained without adding a duplicate cwd project.");
        var nested = projects.Single(project => ProjectPath.Matches(project.WorkingDirectory, child));
        Require(nested.Name == "Nested project" && nested.SessionCount == 1, "Task should belong to the longest saved root.");
        Require((scan.Tasks ?? []).Single().Project == "Nested project", "Task should carry the saved project label.");
    }

    private static void AccountOwnership()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Project("workspace");
        fixture.SaveProjects(("Example", workspace));
        fixture.AddTask("thread-other", workspace, "Other account task", "another-account", quotaBlocked: true);
        var scan = fixture.Scan();
        Require((scan.Tasks ?? []).Single().CanAutoResume == false, "A different account's task must not be eligible.");
        Require(scan.PausedTasks.Count == 0, "A different account's stop must not enter automatic resume.");
    }

    private static void MalformedRollout()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Project("workspace");
        fixture.SaveProjects(("Still visible", workspace));
        fixture.AddTask("thread-good", workspace, "Readable task", Account);
        fixture.AddMalformedTask("thread-bad", workspace, Account);
        var scan = fixture.Scan();
        Require(scan.IsAvailable && scan.HasUnverifiedTask, "A malformed rollout should keep the readable catalog visible but flag automation as uncertain.");
        Require((scan.Projects ?? []).Any(project => project.Name == "Still visible"), "A malformed rollout must not erase project choices.");
        Require((scan.Tasks ?? []).Any(task => task.ThreadId == "thread-good"), "A malformed rollout must not erase readable tasks.");
        Require((scan.Tasks ?? []).All(task => task.ThreadId != "thread-bad" || !task.CanAutoResume), "Malformed task must not be selectable for resume.");
    }

    private static void IgnoreGuardianStub()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Project("workspace");
        fixture.SaveProjects(("Project remains visible", workspace));
        fixture.AddGuardianStub("guardian-without-rollout", workspace, Account);
        var scan = fixture.Scan();
        Require(scan.IsAvailable && !scan.HasUnverifiedTask, "A known guardian subagent without a rollout should not invalidate the project list.");
        Require(scan.Tasks?.Count == 0 && scan.Projects?.Any(project => project.Name == "Project remains visible") == true,
            "A guardian row must not become a user-selectable task.");
    }

    private static void WindowsPaths()
    {
        Require(ProjectPath.Matches(@"\\?\C:\Work\Project", @"c:\work\project\"), "Extended paths, case and trailing separators should identify one project.");
        Require(ProjectPath.Matches(@"\\?\UNC\server\share\project", @"\\server\share\project"), "UNC extended prefix should normalize.");
        Require(ProjectPath.Contains(@"C:\work\project", @"c:\WORK\project\repo"), "A nested repo should belong to its project.");
        Require(!ProjectPath.Contains(@"C:\work\project", @"C:\work\project-two\repo"), "A path prefix alone must not grant project scope.");
    }

    private static async Task SelectionPersistenceAsync()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Project("workspace");
        fixture.SaveProjects(("Example", workspace));
        fixture.AddTask("thread-choice", workspace, "Chosen task", Account, quotaBlocked: true);
        var store = fixture.Store();
        var keeper = new KeeperService(store);
        await keeper.RefreshProjectsAsync();
        Require(store.Snapshot().SelectedProjectPaths.Count == 0 && store.Snapshot().SelectedThreadIds.Count == 0, "New settings must require explicit choices.");
        await keeper.SetTaskSelectedAsync("thread-choice", true);
        Require(store.Snapshot().SelectedThreadIds.Count == 0, "A task outside selected projects must not be opted in.");
        await keeper.SetProjectSelectedAsync(workspace, true);
        await keeper.SetTaskSelectedAsync("thread-choice", true);
        var restored = fixture.Store().Snapshot();
        Require(restored.SelectedProjectPaths.Any(path => ProjectPath.Matches(path, workspace)), "Project selection did not persist.");
        Require(restored.SelectedThreadIds.SequenceEqual(["thread-choice"]), "Task selection did not persist.");
        await keeper.SetProjectSelectedAsync(workspace, false);
        var deselected = fixture.Store().Snapshot();
        Require(deselected.SelectedProjectPaths.Count == 0 && deselected.SelectedThreadIds.Count == 0, "Deselecting a project must also remove its task consent.");
    }

    private static async Task NestedProjectConsentAsync()
    {
        using var fixture = new Fixture();
        var parent = fixture.Project("workspace");
        var child = fixture.Project("workspace", "nested");
        var cwd = fixture.Project("workspace", "nested", "repo");
        fixture.SaveProjects(("Parent", parent), ("Child", child));
        fixture.AddTask("thread-child", cwd, "Child task", Account, quotaBlocked: true);
        var store = fixture.Store();
        var keeper = new KeeperService(store);
        await keeper.RefreshProjectsAsync();
        await keeper.SetProjectSelectedAsync(parent, true);
        await keeper.SetTaskSelectedAsync("thread-child", true);
        Require(store.Snapshot().SelectedThreadIds.Count == 0, "Selecting a parent must not opt an unchecked nested project into resume.");
        await keeper.SetProjectSelectedAsync(child, true);
        await keeper.SetTaskSelectedAsync("thread-child", true);
        Require(store.Snapshot().SelectedThreadIds.Contains("thread-child"), "Selecting the owning nested project should allow explicit task consent.");
        await keeper.SetProjectSelectedAsync(parent, false);
        Require(store.Snapshot().SelectedThreadIds.Contains("thread-child"), "Deselecting the parent must preserve consent for a separately selected child project.");
        await keeper.SetProjectSelectedAsync(child, false);
        Require(store.Snapshot().SelectedThreadIds.Count == 0, "Deselecting the owning nested project must remove task consent.");
    }

    private static void Run(string name, Action test)
    {
        test();
        Console.WriteLine("PASS: " + name);
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine("PASS: " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CodexKeeper-Regression-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(Root, "home");
        public string Settings => Path.Combine(Root, "keeper-settings");
        private string Database => Path.Combine(Home, "state_5.sqlite");

        public Fixture()
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(Path.Combine(Home, "auth.json"), JsonSerializer.Serialize(new { tokens = new { account_id = Account } }));
            Execute("CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, creator_account_id TEXT, cwd TEXT, title TEXT, updated_at INTEGER, archived INTEGER DEFAULT 0, source TEXT, thread_source TEXT, attached_thread_parent_id TEXT);");
        }

        public string Project(params string[] parts)
        {
            var path = Path.Combine(new[] { Root }.Concat(parts).ToArray());
            Directory.CreateDirectory(path);
            return path;
        }

        public SettingsStore Store() => new(Settings, Home);
        public CodexSessionScan Scan() => new CodexSessionScanner(Store()).Scan(Account);

        public void SaveProjects(params (string Name, string RootPath)[] projects)
        {
            var local = projects.Select((project, index) => new
                { Key = "project-" + index, Value = new { id = "project-" + index, name = project.Name, rootPaths = new[] { project.RootPath } } })
                .ToDictionary(item => item.Key, item => item.Value);
            File.WriteAllText(Path.Combine(Home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = local }));
        }

        public void AddTask(string id, string cwd, string title, string account, bool quotaBlocked = false)
        {
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var rollout = Path.Combine(Home, id + ".jsonl");
            var lines = new List<string>
            {
                JsonSerializer.Serialize(new { timestamp = at, type = "session_meta", payload = new { id, cwd, source = "cli" } }),
                JsonSerializer.Serialize(new { timestamp = at.AddSeconds(1), type = "event_msg", payload = new { type = "task_started", turn_id = "turn-fixture" } })
            };
            if (quotaBlocked)
            {
                lines.Add(JsonSerializer.Serialize(new
                {
                    timestamp = at.AddSeconds(2), type = "event_msg", payload = new
                    {
                        type = "token_count", turn_id = "turn-fixture", rate_limits = new
                        {
                            limit_id = "codex",
                            primary = new { used_percent = 100, window_minutes = 300, resets_at = at.AddHours(1).ToUnixTimeSeconds() },
                            secondary = new { used_percent = 20, window_minutes = 10080, resets_at = at.AddDays(2).ToUnixTimeSeconds() }
                        }
                    }
                }));
                lines.Add(JsonSerializer.Serialize(new
                {
                    timestamp = at.AddSeconds(3), type = "event_msg", payload = new
                    { type = "error", turn_id = "turn-fixture", error = new { type = "usage_limit_reached" }, message = "usage_limit_reached", will_retry = false }
                }));
            }
            else
                lines.Add(JsonSerializer.Serialize(new { timestamp = at.AddSeconds(2), type = "event_msg", payload = new { type = "task_complete", turn_id = "turn-fixture" } }));
            File.WriteAllLines(rollout, lines);
            Insert(id, rollout, cwd, title, account);
        }

        public void AddMalformedTask(string id, string cwd, string account)
        {
            var rollout = Path.Combine(Home, id + ".jsonl");
            File.WriteAllText(rollout, "{malformed json\n");
            Insert(id, rollout, cwd, "Unreadable task", account);
        }

        public void AddGuardianStub(string id, string cwd, string account)
        {
            Insert(id, Path.Combine(Home, id + ".jsonl"), cwd, "Guardian task", account, "guardian_review",
                "{\"subagent\":{\"other\":\"guardian\"}}");
        }

        private void Insert(string id, string rollout, string cwd, string title, string account, string threadSource = "cli", string source = "cli") =>
            Execute($"INSERT INTO threads (id, rollout_path, creator_account_id, cwd, title, updated_at, archived, source, thread_source) VALUES ({Sql(id)},{Sql(rollout)},{Sql(account)},{Sql(cwd)},{Sql(title)},{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},0,{Sql(source)},{Sql(threadSource)});");

        private static string Sql(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        private void Execute(string sql)
        {
            if (NativeSqlite.sqlite3_open_v2(Database, out var database, 2 | 4, IntPtr.Zero) != 0)
                throw new InvalidOperationException("Fixture database could not be opened.");
            try
            {
                var code = NativeSqlite.sqlite3_exec(database, sql, IntPtr.Zero, IntPtr.Zero, out var error);
                if (code == 0) return;
                var message = Marshal.PtrToStringUTF8(error) ?? "Fixture SQLite execution failed.";
                NativeSqlite.sqlite3_free(error);
                throw new InvalidOperationException(message);
            }
            finally { NativeSqlite.sqlite3_close_v2(database); }
        }

        public void Dispose()
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Root);
            if (full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("CodexKeeper-Regression-", StringComparison.Ordinal))
                Directory.Delete(full, recursive: true);
        }
    }

    private static class NativeSqlite
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr database, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, IntPtr callback, IntPtr argument, out IntPtr error);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close_v2(IntPtr database);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr value);
    }
}
