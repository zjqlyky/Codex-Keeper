# Windows 开发说明

Windows 默认目标是 `windows/CodexKeeper.Windows/` 中的 .NET WPF 托盘应用。解决方案 `CodexKeeper.Windows.sln` 包含该项目；原 macOS Swift 源码和开发记录仍在仓库中。

## 构建与发布

需要 .NET 9 SDK 和 Windows Desktop targeting pack。在仓库根目录运行：

```powershell
.\build.ps1
```

发布自包含 x64 单文件：

```powershell
.\publish.ps1
```

发布结果位于 `.publish\win-x64\`，带版本号的可运行文件也会复制到仓库根目录。Keeper 正在运行时，脚本不会覆盖其 exe；从托盘退出后再次发布即可更新 `CodexKeeper.Windows.exe`。项目不依赖额外 NuGet 包。

## Windows 运行方式

- WPF 主窗体通过 `NotifyIcon` 常驻系统托盘；关闭窗体只隐藏它。托盘菜单负责打开、启停和退出。
- Keeper 初始停用。启用后读取额度；用户可单独关闭自动续跑，并可选择在登录 Windows 时启动。
- 项目列表合并 Codex 保存的本地项目与数据库中的工作目录；嵌套工作目录归入最长匹配的已保存项目根目录。未勾选项目时不发送计划保活；同一账户每个计划节点只做一次隔离保活。
- 自动续跑要求项目路径与任务 ID 均明确勾选。项目取消勾选时同时取消其任务选择；列表刷新不改变用户选择。项目与任务在 Keeper 停用时也能刷新。
- `CodexLocator` 查找 `codex.exe`：先用设置中的路径，再查 `PATH`，最后查 `%LOCALAPPDATA%\OpenAI\Codex\bin\<版本>\codex.exe`。
- `CodexAppServerClient` 通过标准输入输出与 `codex app-server` 使用 JSONL 通信。每次请求有超时和 8 MiB 单行上限；stderr 只排空，不保存。
- 额度读取使用 `account/read` 与 `account/rateLimits/read`，比较认证文件中的 account ID，不把账户 ID 写入设置或活动记录。
- 任务扫描从 `CODEX_HOME` 下最近更新的 `state*.sqlite` 只读查询 `threads.rollout_path` 和 `creator_account_id`，再核对 rollout 首条 `session_meta` 的 thread ID。SQLite 错误、账户不匹配、缺失的归属或任务数据都会停止自动操作。
- 保活使用临时 Codex home，复制当前认证后配置为 Responses HTTP、关闭插件，并在只读 sandbox 中通过 `thread/start` 与 `turn/start` 发送短提示。成功要求完成事件、实际模型答复和保活后的新额度读数；不凭进程退出或普通成功响应推断完成。操作结束会清理临时目录。
- 续跑只接受 rollout 中明确的额度停止，并要求最新同账户额度读数在已知重置时间之后、工作目录存在、恢复后的 thread ID 和 cwd 一致且任务不处于活动状态。提前恢复时不自动发送。每个停止事件最多自动提交一次；不确定结果不重发。

## 本地数据与兼容性

设置与最多 30 条脱敏活动记录放在 `%LOCALAPPDATA%\CodexKeeper\settings.json`，其中会保存用户明确勾选的项目路径与任务 ID。登录启动项由用户在设置中启用，保存在当前用户的 `Run` 注册表项。Keeper 不修改 Codex 工作区或 `config.toml`，不保存 token、提示词或完整 rollout；活动记录不包含完整项目路径。

Codex app-server 的 API 以及 `state*.sqlite` / rollout 格式可能随 Codex 升级改变。这些本地记录属于版本敏感接口；发生格式、权限、账户或任务状态不确定时，必须停止自动操作并在界面提示检查 Codex。不要根据 rollout 文件名猜 thread ID，也不要直接修改 Codex 数据库。
