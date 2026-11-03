# Windows task recovery: resume after selection

修复日期：2026-11-03  
适用范围：Codex Keeper for Windows

## 问题

在“任务恢复”中勾选一个因额度耗尽而暂停的任务，只会保存该任务的自动续跑授权。Keeper 依赖后台轮询检查任务和额度，因此额度已经恢复时，勾选操作本身不会立即触发续跑检查。此外，旧逻辑把暂停时记录的预计重置时间当作硬门槛；即使最新额度已显示恢复，也会拦住续跑。

## 修复

勾选已确认因额度暂停的任务后，Keeper 现在会立即重新扫描 Codex 任务、读取最新额度，并进入现有的续跑流程。若实时读取确认暂停时耗尽的额度已经恢复，即使恢复早于记录中的预计重置时间，也会继续复核并尝试续跑。没有确认暂停状态的任务不会触发这次检查。

自动续跑仍要求 Keeper 和“额度恢复后自动续跑”已启用，并且项目与任务都已勾选。发送续跑请求前，Keeper 仍会核对账户、暂停后读取的实时额度、任务身份和项目目录；状态无法确认时不会发送请求。

这项代码修复已同步到 GitHub `main`。它没有生成新安装包或发行版；Windows 应用版本号仍为 1.0.2。

---

## English

**Fix date:** 2026-11-03  
**Applies to:** Codex Keeper for Windows

### Issue

Selecting a task paused by quota exhaustion saved permission for automatic resume, but did not immediately check whether the quota had recovered. Keeper relied on its background polling cycle to make that check. The previous logic also treated the stop-time reset estimate as a hard deadline and blocked resume even when a fresh quota reading showed recovery.

### Fix

Selecting a task that is verified as quota-paused now triggers an immediate task scan, a fresh quota read, and the existing resume flow. If the fresh reading confirms that the exhausted quota has recovered, Keeper proceeds even when recovery happens before the recorded reset estimate. Tasks without verified pause evidence do not trigger this check.

Automatic resume still requires Keeper and “Automatically resume tasks after quota recovery” to be enabled, with both the project and task selected. Before sending a continuation, Keeper continues to verify the account, a fresh quota reading taken after the pause, task identity, and project directory. It sends no request when the state cannot be verified.

The code fix is synced to GitHub `main`. No new installer or release was produced; the Windows app version remains 1.0.2.
