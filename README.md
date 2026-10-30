# Codex Keeper for Windows

[English](README.en.md)

Codex Keeper 是 Windows 托盘助手，可按每日计划核对 Codex 额度，并在额度按计划恢复后继续符合条件的暂停任务。

当前 Windows 版本为预览版，尚未发布正式安装包。

## 功能

- 在托盘查看 5 小时和每周额度。
- 显示 Codex 已保存的项目和本地任务，按名称、路径和任务标题勾选范围，重启后保留选择。
- 设置每日开始时间；此后每 5 小时检查一次。已有可用的 5 小时窗口、额度已耗尽或 Codex 正在处理任务时，会跳过保活。
- 仅在核对账户、原任务、工作目录和额度重置证据后自动续跑。额度提前恢复时，Keeper 会等待预定重置时间，让你先检查原任务。
- 关闭窗口时缩到托盘；可选在登录 Windows 时启动。

Keeper 不会增加额度或绕过额度限制。保活需要账户提供 `gpt-6-luna`。

## 使用条件

- Windows 10/11 x64。
- 安装 Codex 并登录 ChatGPT 账户。
- Codex CLI 可通过 `PATH` 或默认安装路径找到；如果自动查找失败，可在设置中选择 `codex.exe`。

先在“Codex 项目”中勾选需要保活的项目，再启用 Keeper 并设置每日开始时间。未勾选项目时不执行计划保活。额度由账户共享，一次隔离保活即可服务所选项目，不会向每个原任务重复发送提示。

需要自动续跑时，在“任务恢复”中另外勾选具体任务，并启用“额度恢复后自动续跑”。项目勾选不会自动选中任务；只有所选项目内明确勾选、因额度停止且已核对重置证据的任务才会继续。

Keeper 默认处于停用状态。使用计划期间，请保持 Keeper 运行、电脑唤醒并联网；可开启“登录 Windows 时启动”。

## 构建

安装 .NET 9 SDK 和 Windows Desktop targeting pack 后，在仓库根目录运行：

```powershell
.\build.ps1
```

创建可独立运行的 x64 单文件：

```powershell
.\publish.ps1
```

输出位于 `.publish\win-x64\`，并将版本号 exe 复制到仓库根目录。Keeper 正在运行时发布脚本会保留现有副本；退出托盘程序后再次发布即可更新 `CodexKeeper.Windows.exe`。Visual Studio 也可打开根目录的 `CodexKeeper.Windows.sln`。

## Windows 版本说明

此版本通过 Codex `app-server` 读取额度和提交任务；任务恢复还会只读查询 Codex 当前的 `state*.sqlite`，并核对其中登记的 rollout。Codex 的本地任务记录和 app-server 协议会随版本变化；当账户、任务归属、重置时间或工作目录无法确认时，Keeper 会停止自动操作。升级 Codex 后，请留意 Keeper 是否仍能读取额度和任务。

用户设置（包括主动勾选的项目路径和任务 ID）保存在 `%LOCALAPPDATA%\CodexKeeper\settings.json`。保活使用临时隔离目录，并在操作结束后清理；Keeper 不把账户凭据写入设置或活动记录。`CODEX_HOME` 环境变量可用于指定与 Codex 相同的数据目录。

## 原 macOS 版本

原始 Swift / SwiftUI macOS 源码和说明仍保留在仓库中，见 [macOS README](README.macos.md)。Windows 项目位于 `windows/CodexKeeper.Windows/`。

## 许可证

本项目使用 [MIT 许可证](LICENSE)。这是独立项目，与 OpenAI 无隶属关系。
