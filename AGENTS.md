# Codex Keeper

- 未经用户明确同意，不执行 Git push；提交、发布也不从普通修改请求中推定授权。
- 项目根目录就是本目录。当前默认目标是 Windows 原生 WPF 托盘应用，源码位于 `windows/CodexKeeper.Windows/`，解决方案是根目录 `CodexKeeper.Windows.sln`。
- Windows 行为、限制、数据位置和构建方式见 `docs/WINDOWS.md`；根目录原 Swift / SwiftUI / AppKit 实现保留为 macOS 原版，说明见 `docs/DEVELOPMENT.md`。
- 构建 Windows 目标：`./build.ps1`；发布独立 Windows x64 单文件：`./publish.ps1`。原 macOS 脚本 `build.sh` 和 `run-tests.sh` 仅适用于 macOS 原版。
- 不因普通修改请求推定可以提交、推送、发布或替换用户安装的应用。
- 自动保活和续跑必须 fail closed：核对 ChatGPT 账户、最新额度、当前登记的 thread 与 rollout、工作目录、额度恢复时间；身份或状态不明时不发送，也不猜测文件名对应的 thread ID。
- 保活不得触碰 Codex 用户工作区或更改其配置；使用临时隔离 home、只读 sandbox、无工具提示，依据模型实际回复和新额度读数确认结果。尝试记录在提交前持久化，未知提交结果不自动重发。
- app-server 是版本敏感接口。只读查询 Codex 状态数据库；不改 SQLite journal、用户数据或正在运行的 Codex 会话。日志不保存凭据、提示词、完整路径或原始 rollout 内容。
- Windows 用户设置位于 `%LOCALAPPDATA%\CodexKeeper\settings.json`；保活隔离目录在系统临时目录，正常完成后清理。不要复制、清空或迁移 Codex 的本地任务历史。
- 不新增外部运行时依赖，除非 Windows SDK 已提供的方案无法满足需求。默认设置保持关闭，启动 Windows 和自动续跑都是用户可选项。
- UI 文案须同时支持简体中文与英语；系统语言决定默认显示语言。UI 保持主次清晰，不把后台边缘逻辑堆成用户选项。
- `.build/`、`bin/`、`obj/` 和 `.publish/` 是生成输出；旧副本不是现役源码或指令。
