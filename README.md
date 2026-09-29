# ClipBridge

轻量级 Windows 跨设备剪贴板同步工具。

ClipBridge 用于解决：

> 本地主机和云桌面无法直接复制粘贴，但两端都可以访问同一台 Linux Server。

每位用户拥有独立账号，同账号的多台 Windows 设备可以同步文本和图片剪贴板历史。

---

# 1. Codex 必读

在修改任何代码之前，按顺序阅读：

1. [`01-architecture.md`](./01-architecture.md)
2. [`02-loop-engineering.md`](./02-loop-engineering.md)
3. 本 README
4. 当前代码
5. 当前测试
6. `git status`
7. `git log`

这三个文档的优先级：

```text
01-architecture.md
    ↓
决定“系统应该长什么样”

02-loop-engineering.md
    ↓
决定“应该如何逐步开发”

README.md
    ↓
决定“现在应该做什么”
```

---

# 2. Codex 总指令

你是 ClipBridge 的主开发工程师。

你的目标不是一次性完成整个系统，而是：

> **按 MVP 场景循环开发，每完成一个可验证闭环，验证通过后立即提交代码，然后再进入下一个 MVP。**

固定循环：

```text
检查当前状态
    ↓
选择一个 MVP
    ↓
定义验收条件
    ↓
实现最小功能
    ↓
Build
    ↓
自动测试
    ↓
人工验证
    ↓
修复
    ↓
回归
    ↓
Commit
    ↓
下一个 MVP
```

---

# 3. 禁止行为

禁止一次性：

- 实现多个 Phase。
- 实现整个系统。
- 大规模重构。
- 修改技术栈。
- 扩展产品范围。

禁止新增：

- 文件 Clipboard。
- 文件传输。
- 收藏。
- Pin。
- Group。
- 用户共享。
- 团队剪贴板。
- SSO。
- LDAP。
- Redis。
- MQ。
- Elasticsearch。
- MinIO。
- 微服务。

---

# 4. 固定技术栈

## Client

```text
.NET 10
WPF
Win32 P/Invoke
SQLite
```

## Server

```text
Go
Gin
PostgreSQL
WebSocket
JWT
Argon2id
Filesystem
Docker Compose
```

不得自行替换。

## Client 本机启动

开发时可运行 WPF 项目。Windows 发布包使用自包含的单文件配置，双击发布目录中的 `ClipBridge.App.exe` 启动，不需要通过控制台版 `dotnet.exe` 加载应用。

```powershell
dotnet publish src/client/ClipBridge.App/ClipBridge.App.csproj -p:PublishProfile=win-x64-self-contained
```

发布文件统一位于 `artifacts/ClipBridge/ClipBridge.App.exe`。

---

# 5. 项目核心能力

最终 V1 只需要完成：

```text
TEXT Clipboard
IMAGE Clipboard
Clipboard History
Search
Ctrl + `
Quick Paste
Account
Device
Cross-device Sync
Admin
```

---

# 6. 开发进度

Codex 每完成一个 MVP 后，在这里更新状态。

建议格式：

```text
[x] MVP-01 应用可启动
[x] MVP-02 文本 Clipboard 监听
[x] MVP-03 文本历史持久化
[x] MVP-04 历史列表 UI
[ ] MVP-05 历史搜索
[ ] MVP-06 Ctrl + ` 呼出
[ ] MVP-07 文本快速粘贴闭环
[ ] MVP-08 本地图片 Clipboard
[ ] MVP-09 图片快速粘贴
[ ] MVP-10 Server + PostgreSQL 启动
[ ] MVP-11 管理员创建用户
[ ] MVP-12 用户登录
[ ] MVP-13 设备注册
[ ] MVP-14 文本上传 Server
[ ] MVP-15 第二设备拉取历史
[ ] MVP-16 WebSocket 实时通知
[ ] MVP-17 断线补偿
[ ] MVP-18 图片上传
[ ] MVP-19 图片跨设备同步
[ ] MVP-20 用户隔离
[ ] MVP-21 账号禁用与重置
[ ] MVP-22 开机启动与安装
[ ] MVP-23 稳定性与恢复
```

MVP-01 验证记录：

```text
L0 Build: PASS
Unit Test: N/A（当前没有测试项目）
L1: PASS（用户确认窗口、托盘菜单和退出操作正常；自包含发布程序启动正常）
L2: N/A
L3: NOT YET
```

MVP-02 验证记录：

```text
L0 Build: PASS
Unit Test: N/A（当前没有测试项目）
L1: PASS（用户复制测试文本后确认识别内容与字符数正确）
L2: N/A
L3: NOT YET
```

MVP-03 验证记录：

```text
L0 Build: PASS（Release 自包含发布成功）
Unit Test: PASS（SQLite 记录写入并重新打开数据库读取，1/1）
Integration Test: PASS（应用启动时初始化本地数据库；文本记录在退出并重启后保留）
L1: PASS（用户确认复制测试文本后显示 1 条记录，退出并重启后仍显示 1 条）
L2: N/A
L3: NOT YET
```

MVP-04 验证记录：

```text
L0 Build: PASS（Release 自包含发布成功，输出固定到 artifacts/ClipBridge）
Unit Test: PASS（历史排序、条数限制、空白过滤、重复更新和旧记录清理，6/6）
Integration Test: PASS（WPF 启动加载 SQLite 历史，复制文本后刷新列表）
L1: PASS（用户确认新复制文本显示在列表顶部、空白项不显示、重复文本只保留一条并更新时间，Tooltip 锚点正确）
L2: N/A
L3: NOT YET
```

不要手工勾选未验证场景。

---

# 7. Codex 每次开始任务时必须输出

```text
## Current Repository State

- Current branch:
- git status:
- Last commit:
- Completed MVPs:
- Current failing tests:
- Existing uncommitted changes:

## Selected MVP

MVP ID:
Name:

## User Scenario

...

## Acceptance Criteria

Given ...
When ...
Then ...

## Out of Scope

...

## Files Expected to Change

...

## Test Plan

...

## Manual Verification Plan

...
```

输出后再开始编码。

---

# 8. Codex 每次完成任务时必须输出

```text
## Implementation Summary

## Files Changed

## Build Result

## Unit Test Result

## Integration Test Result

## Manual Verification Result

## Regression Result

## Git Diff Review

## Commit

commit hash:
commit message:

## Remaining Issues

## Next Recommended MVP
```

没有 commit hash，当前 MVP 视为未完成。

---

# 9. 提交要求

每个 MVP 完成后必须提交。

推荐：

```text
feat(app): complete MVP-01 tray application shell
feat(clipboard): complete MVP-02 text clipboard listener
feat(history): complete MVP-03 persistent text history
feat(ui): complete MVP-04 clipboard history list
feat(search): complete MVP-05 local clipboard search
feat(hotkey): complete MVP-06 quick paste activation
feat(paste): complete MVP-07 text quick paste loop
```

Bug：

```text
fix(clipboard): handle clipboard access contention
```

安全测试：

```text
test(security): verify cross-user isolation
```

---

# 10. 提交前检查

必须全部通过：

```text
[ ] 当前 MVP 完成
[ ] Build 成功
[ ] Unit Test 成功
[ ] Integration Test 成功（如适用）
[ ] Manual Verification 成功
[ ] 已完成 MVP 回归成功
[ ] git diff 已检查
[ ] 无 Debug 临时代码
[ ] 无密码
[ ] 无 Token
[ ] 无真实剪贴板敏感内容
[ ] 文档已同步
```

---

# 11. 当前开发顺序

必须按照：

```text
Phase A
本地文本闭环

Phase B
本地图片闭环

Phase C
账号闭环

Phase D
文本跨设备闭环

Phase E
图片跨设备闭环

Phase F
多人隔离闭环

Phase G
发布闭环
```

不要为了“网络同步是核心”就直接跳到 Server。

先确保：

```text
本地 Clipboard
+
Quick Paste
```

真正可用。

---

# 12. 首次启动 Codex Prompt

如果仓库为空，将下面内容直接作为第一次任务：

```text
阅读：

- README.md
- 01-architecture.md
- 02-loop-engineering.md

严格按照循环工程方式开发。

先检查当前仓库和 git 状态。

如果仓库为空，只执行：

MVP-01：应用可启动

不要提前实现 MVP-02 或其他功能。

本轮流程必须完整执行：

1. 输出 Current Repository State
2. 输出 MVP-01 验收条件
3. 输出实施计划
4. 实现
5. build
6. test
7. 给出人工验证步骤
8. 人工验证结果可确认后，检查 git diff
9. 提交代码
10. 输出 commit hash
11. 更新 README 中 MVP-01 状态
12. 停止，不进入 MVP-02

如果当前仓库已经有代码：

先把现有代码映射到 MVP-01 ~ MVP-23，
标记：

- 已完成且验证
- 已实现但未验证
- 部分实现
- 未实现
- 与架构冲突

然后选择最早一个尚未形成闭环的 MVP，
只完成该 MVP。
```

---

# 13. 日常继续开发 Prompt

每次继续开发可以直接使用：

```text
继续 ClipBridge 开发。

先阅读：

README.md
01-architecture.md
02-loop-engineering.md

检查：

git status
git log
当前测试
README MVP 状态

选择“最早一个未完成闭环的 MVP”。

严格执行：

Scenario
→ Plan
→ Implement
→ Build
→ Test
→ Manual Verify
→ Regression
→ Commit

一次只完成一个 MVP。

完成后：
- 更新 README MVP 状态
- 提交代码
- 输出 commit hash
- 停止

不要自动进入下一个 MVP。
```

---

# 14. 为什么采用循环工程

ClipBridge 涉及多个容易产生边界问题的能力：

- Windows Clipboard。
- Win32 Message。
- Global Hotkey。
- Foreground Window。
- 自动粘贴。
- WebSocket。
- 断线恢复。
- 多用户权限。
- 图片缓存。

如果一次开发大量功能，很容易出现：

```text
不知道哪一步坏了
+
回归困难
+
AI 大规模改代码
+
问题叠加
```

循环工程要求：

```text
每一步都能运行
每一步都能验证
每一步都能回滚
每一步都有 Commit
```

这样才能让 Codex 长时间持续开发而不逐渐偏离项目目标。

---

# 15. 完成标准

项目最终不是因为：

```text
所有目录都创建了
所有接口都写了
```

就算完成。

而是每个业务闭环都经过：

```text
实现
+
自动测试
+
人工验证
+
回归
+
Commit
```

最终至少形成以下闭环：

```text
本地文本复制粘贴闭环
本地图片复制粘贴闭环
账号登录闭环
设备管理闭环
文本跨设备闭环
断线恢复闭环
图片跨设备闭环
多用户隔离闭环
安装运行闭环
```
