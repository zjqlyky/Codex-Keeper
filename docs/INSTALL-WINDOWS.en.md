# Windows Installation Guide

[简体中文](INSTALL-WINDOWS.md)

## Download and install

1. Download `CodexKeeper-Setup-1.0.0-win-x64.exe` from [GitHub Releases](https://github.com/zjqlyky/Codex-Keeper/releases/latest).
2. Optionally download the matching `.sha256` file and verify it in PowerShell:

   ```powershell
   Get-FileHash .\CodexKeeper-Setup-1.0.0-win-x64.exe -Algorithm SHA256
   ```

3. Run the installer. The default per-user location is `%LOCALAPPDATA%\Programs\Codex Keeper`; administrator rights are not required.
4. Start Codex Keeper from the Start menu or desktop shortcut. Select projects in the project and task card, then optionally select tasks that may resume automatically.

The installer targets Windows 10/11 x64 and includes the .NET Runtime required by the app. Codex must be installed and signed in separately.

The installer is currently unsigned, so Windows may display an unknown publisher. Download it only from this repository's Releases page and verify its SHA-256 before running it.

## Upgrade and uninstall

- Quit Codex Keeper from the tray before running an updated installer. It installs to the same location and updates the app files.
- Uninstall it from Windows Settings → Apps, or use the Start menu uninstall entry.
- Uninstall removes the app files, shortcuts, and Keeper's Windows startup entry. It preserves `%LOCALAPPDATA%\CodexKeeper\settings.json`, which contains Keeper settings and selected project paths and task IDs.
- You can turn off “Start with Windows” in Keeper settings before uninstalling.

## Build the installer from source

Install the .NET 9 SDK, Windows Desktop targeting pack, and Inno Setup 6, then run this from the repository root:

```powershell
.\build-installer.ps1
```

The installer and its SHA-256 file are written to `artifacts/`. The version defaults to the value in `windows/CodexKeeper.Windows/CodexKeeper.Windows.csproj`; override it with `-Version 1.0.0` if needed.

Pushing the `v1.0.0` tag triggers GitHub Actions on a Windows 2025 runner. It publishes the self-contained app, compiles the Inno Setup installer, and creates a GitHub Release titled `V1.0` with the installer and checksum. Release notes are in [`docs/RELEASES/V1.0.md`](RELEASES/V1.0.md).
