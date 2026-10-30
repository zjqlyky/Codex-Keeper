# Windows 安装说明

[English](INSTALL-WINDOWS.en.md)

## 下载与安装

1. 从 [GitHub Releases](https://github.com/zjqlyky/Codex-Keeper/releases/latest) 下载 `CodexKeeper-Setup-1.0.0-win-x64.exe`。
2. 可选地下载同名 `.sha256` 文件，并用 PowerShell 核对：

   ```powershell
   Get-FileHash .\CodexKeeper-Setup-1.0.0-win-x64.exe -Algorithm SHA256
   ```

3. 运行安装程序。默认安装到当前用户的 `%LOCALAPPDATA%\Programs\Codex Keeper`，无需管理员权限。
4. 从开始菜单或桌面快捷方式启动 Codex Keeper。先在项目与任务卡片中勾选需要保活的项目，再按需勾选允许自动续跑的任务。

安装包针对 Windows 10/11 x64，已包含应用运行所需的 .NET Runtime。Codex 本身仍需单独安装并登录。

安装包目前未进行代码签名，Windows 可能将发布者显示为未知。请只从上述仓库的 Releases 下载，并在运行前核对 SHA-256。

## 升级与卸载

- 关闭托盘中的 Codex Keeper 后，运行新版安装程序。它会安装到相同目录并更新程序文件。
- 通过 Windows“设置 → 应用”卸载，或从开始菜单打开卸载项。
- 卸载会移除程序文件、快捷方式和 Keeper 的 Windows 登录启动项；不会删除 `%LOCALAPPDATA%\CodexKeeper\settings.json`。该文件保存 Keeper 设置以及用户选择的项目路径和任务 ID。
- 卸载前可以在 Keeper 设置中关闭“登录 Windows 时启动”。

## 从源码构建安装包

需要 .NET 9 SDK、Windows Desktop targeting pack 和 Inno Setup 6。在仓库根目录运行：

```powershell
.\build-installer.ps1
```

安装程序和 SHA-256 文件写入 `artifacts/`。默认版本从 `windows/CodexKeeper.Windows/CodexKeeper.Windows.csproj` 读取；也可以使用 `-Version 1.0.0` 指定版本。

在 GitHub Actions 页面手动运行 `Windows installer release` 工作流并选择 `main` 分支，会在 Windows 2025 Runner 上发布自包含程序、编译 Inno Setup 安装程序，并创建标题为 `Codex Keeper for Windows V1.0` 的 GitHub Release。发布使用独立标签 `windows-v1.0`，保留仓库已有的 macOS `v1.0.0` 标签。Release 包含安装程序和 SHA-256 文件，说明来自 [`docs/RELEASES/V1.0.md`](RELEASES/V1.0.md)。
