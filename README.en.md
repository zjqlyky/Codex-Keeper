# Codex Keeper for Windows

[简体中文](README.md)

Codex Keeper is a Windows tray companion that checks Codex usage on a daily schedule and resumes eligible paused tasks after quota recovers at its scheduled reset.

The Windows version is a preview. There is no official installer yet.

## Features

- View 5-hour and weekly usage from the system tray.
- List saved Codex projects and local tasks, choose the scope by project path and task title, and retain choices across restarts.
- Set a daily start time. Keeper checks again every five hours and skips keep-alive when a usable window is already active, quota is exhausted, or Codex is working on a task.
- Resume only after checking the account, original task, working directory, and quota-reset evidence. If quota recovers early, Keeper waits until the scheduled reset so you can review the original task first.
- Closing the window minimizes Keeper to the tray. Starting with Windows is optional.

Keeper does not add quota or bypass limits. Keep-alive requires an account that offers `gpt-6-luna`.

## Requirements

- Windows 10/11 x64.
- Codex installed and signed in with a ChatGPT account.
- Codex CLI discoverable on `PATH` or in its default install location. If automatic discovery fails, choose `codex.exe` in settings.

Select projects in “Codex projects”, then enable Keeper and set the daily start time. With no project selected, scheduled keep-alive is paused. Quota is shared by the account, so one isolated keep-alive serves the selected projects without sending repeated prompts to their original tasks.

For automatic resume, separately check specific tasks in “Task recovery” and enable automatic resume. Checking a project does not select its tasks. Only explicitly selected tasks in selected projects may continue after a verified quota stop and reset.

Keeper starts disabled. Keep Keeper running, keep the PC awake, and stay online during scheduled use. You can enable “Start with Windows”.

## Build

Install the .NET 9 SDK and Windows Desktop targeting pack, then run this from the repository root:

```powershell
.\build.ps1
```

Publish a self-contained x64 single-file app:

```powershell
.\publish.ps1
```

Output is written to `.publish\win-x64\`, and a versioned standalone executable is copied to the repository root. If Keeper is running, publishing preserves its current executable; quit it from the tray menu and publish again to update `CodexKeeper.Windows.exe`. You can also open `CodexKeeper.Windows.sln` in Visual Studio.

## Windows version notes

This version uses the Codex `app-server` to read usage and submit task turns. Task recovery also reads Codex's current `state*.sqlite` database in read-only mode and follows its registered rollout paths. Codex's local task data and app-server protocol can change between versions. Keeper stops automatic actions when it cannot confirm the account, task ownership, reset time, or working directory. After updating Codex, check that Keeper can still read usage and task state.

Settings, including selected project paths and task IDs, are stored in `%LOCALAPPDATA%\CodexKeeper\settings.json`. Keep-alive uses a temporary isolated directory and removes it when the operation ends; account credentials are not written to settings or activity history. Set `CODEX_HOME` when Codex uses a non-default data directory.

## Original macOS version

The original Swift / SwiftUI macOS source and instructions remain in the repository. See the [macOS README](README.en.macos.md). The Windows app lives in `windows/CodexKeeper.Windows/`.

## License

This project is licensed under the [MIT License](LICENSE). It is an independent project and is not affiliated with OpenAI.
