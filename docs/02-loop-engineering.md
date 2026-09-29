# 02-loop-engineering.md

# ClipBridge 循环工程开发规范

> 本文档定义 Codex 如何开发、验证和提交 ClipBridge。  
> 核心原则：**每次只完成一个可验证的 MVP 场景，完成闭环验证后提交代码，再进入下一循环。**

---

# 1. 开发目标

ClipBridge 不采用“一次性把所有功能做完，最后统一联调”的方式。

统一采用以下循环：

```text
Scenario
  ↓
Plan
  ↓
Implement
  ↓
Build
  ↓
Automated Test
  ↓
L1 Verify
  ↓
L2 Verify（跨设备场景必须）
  ↓
Fix
  ↓
Regression
  ↓
Commit
  ↓
Next Scenario
```

原则：

```text
一个 MVP
→ 一个可运行闭环
→ 一个明确验收结果
→ 一个逻辑完整 Commit
```

---

# 2. 当前开发环境

当前开发环境固定为：

```text
Windows 物理机
│
├── ClipBridge Client A
│   └── 模拟本地主机
│
├── WSL2 Ubuntu
│   └── Docker
│       ├── clipbridge-server
│       └── PostgreSQL
│
└── Windows Sandbox / Windows VM
    └── ClipBridge Client B
        └── 模拟云桌面
```

推荐最终测试拓扑：

```text
┌────────────────────────────────────────┐
│ Windows Host                           │
│                                        │
│ ClipBridge Client A                    │
│        │                               │
│        │ HTTP/WSS                      │
│        ▼                               │
│ WSL2 Ubuntu                            │
│ └── Docker                             │
│     ├── clipbridge-server              │
│     └── postgres                       │
│                ▲                       │
│                │ HTTP/WSS              │
│                │                       │
│ Windows Sandbox / Windows VM           │
│ ClipboardRedirection = Disable         │
│ ClipBridge Client B                    │
└────────────────────────────────────────┘
```

---

# 3. 为什么不能只用同一 Windows 双实例验证

同一 Windows 启动两个 ClipBridge Client：

```text
Client A
Client B
```

即使使用不同：

```text
device_id
data_dir
profile
```

两个进程仍然共享：

```text
同一个 Windows Clipboard
```

因此双实例只能验证：

- Login。
- Device 注册。
- REST。
- WebSocket。
- Server Broadcast。
- Cursor。
- SQLite。
- 用户隔离。
- 消息去重。

不能证明：

```text
真实的跨 Windows Clipboard 闭环
```

所以：

> 同机双实例可以作为 L1 验证，但不能代替 L2。

---

# 4. 验证等级

所有 MVP 必须明确记录验证等级。

定义：

```text
L0 = Automated Test
L1 = Local Functional Verification
L2 = Isolated Windows End-to-End Verification
L3 = Real Environment Verification
```

---

## 4.1 L0：自动测试

包括：

```text
Unit Test
Integration Test
Repository Test
API Test
Security Test
```

典型命令：

```bash
dotnet build
dotnet test

go test ./...
go build ./...
```

L0 用于验证代码逻辑。

但 GUI、Win32 Clipboard、Hotkey、窗口焦点等不能只依赖 L0。

---

## 4.2 L1：本机功能验证

L1 在当前 Windows Host 上完成。

适用于：

```text
Clipboard Listener
SQLite
History UI
Search
Hotkey
Quick Paste
Login UI
REST API
WebSocket
双 Client Profile
```

例如：

```text
Host Client
Ctrl+C "hello"
→ History
→ Ctrl+`
→ Enter
→ Notepad
```

属于 L1。

双实例：

```text
ClipBridge.exe --profile dev-a
ClipBridge.exe --profile dev-b
```

也属于 L1。

---

## 4.3 L2：隔离 Windows 端到端验证

L2 用于真正验证：

```text
Windows A Clipboard
→ Linux Server
→ Windows B
```

必须使用：

```text
Windows Host
+
Windows Sandbox / Windows VM
```

两个 Windows 必须拥有：

```text
独立系统 Clipboard
```

跨设备相关 MVP：

> **没有通过 L2，不允许标记完成，也不允许以“跨设备功能完成”的名义提交最终 Commit。**

---

## 4.4 L3：真实环境验证

L3 使用最终办公环境：

```text
真实本地主机
+
真实云桌面
+
真实 Linux Server / 网络代理
```

L3 主要在 Release 前执行。

开发期间不要求每个 MVP 都做 L3。

---

# 5. Windows Sandbox 配置

如果使用 Windows Sandbox 模拟云桌面，必须关闭宿主与 Sandbox 的原生 Clipboard Redirection。

创建：

```text
clipbridge-dev.wsb
```

内容：

```xml
<Configuration>
  <Networking>Enable</Networking>
  <ClipboardRedirection>Disable</ClipboardRedirection>
  <MemoryInMB>4096</MemoryInMB>
</Configuration>
```

启动后，先执行隔离校验。

---

# 6. Sandbox Clipboard 隔离校验

在任何 L2 测试前必须先验证。

Host：

```text
Ctrl+C
HOST-ONLY-123456
```

Sandbox：

```text
Ctrl+V
```

预期：

```text
不能直接粘贴 HOST-ONLY-123456
```

然后 Sandbox：

```text
Ctrl+C
SANDBOX-ONLY-654321
```

Host：

```text
Ctrl+V
```

预期：

```text
不能直接粘贴 SANDBOX-ONLY-654321
```

只有以上两个方向都隔离，当前 Sandbox 才允许作为：

```text
L2 测试环境
```

---

# 7. WSL Server 测试要求

WSL2 Ubuntu 中：

```text
Docker
├── clipbridge-server
└── postgres
```

Server 开发环境监听：

```text
0.0.0.0:8080
```

Host 先验证：

```powershell
curl http://localhost:8080/health
```

然后 Sandbox/VM 验证：

```powershell
Test-NetConnection <HOST-IP> -Port 8080
curl http://<HOST-IP>:8080/health
```

必须成功。

注意：

```text
Sandbox 中 localhost != Host
```

因此跨设备测试不能配置：

```text
http://localhost:8080
```

两个 Client 应统一使用一个：

```text
Sandbox/Host 都可访问的 Server 地址
```

例如：

```text
http://<HOST-IP>:8080
```

---

# 8. MVP 定义

一个 MVP 必须满足：

- 有明确用户场景。
- 有明确输入。
- 有明确输出。
- 可以自动测试。
- 可以人工验证。
- 修改范围小。
- 通过后系统仍然可运行。

示例：

```text
MVP:
用户复制文本后，历史列表出现该文本。

Input:
Ctrl+C "hello"

Output:
SQLite 新增 TEXT
History 显示 hello

Verification:
重启后仍存在
```

以下不是合格 MVP：

```text
“实现 Clipboard 模块”
“实现 Sync 模块”
“完善 Server”
```

这些范围太大，无法形成单一闭环。

---

# 9. 每轮开发固定流程

---

## Step 1：读取当前状态

Codex 必须先读取：

```text
README.md
01-architecture.md
02-loop-engineering.md
当前代码
当前测试
git status
git log
```

输出：

```text
Current MVP
Completed MVPs
Current Branch
Uncommitted Changes
Failing Tests
Current Runtime Capability
```

不得直接开始修改代码。

---

## Step 2：选择一个 MVP

一次只允许：

```text
1 个 MVP
```

输出：

```text
MVP ID:
MVP Name:
User Scenario:
Acceptance Criteria:
Verification Level Required:
Out of Scope:
Files Expected to Change:
```

如果 MVP 太大：

```text
先拆分
```

---

## Step 3：定义验收

使用：

```text
Given
When
Then
```

例如：

```text
Given ClipBridge 正在运行
When 用户复制文本 "hello"
Then 本地历史中出现 hello
And 重启后记录仍然存在
```

跨设备：

```text
Given Host Client A 与 Sandbox Client B 登录同一账号
And Sandbox Clipboard Redirection 已关闭
When Host 复制 "SYNC-001"
Then Client B 的历史中出现 "SYNC-001"
And Sandbox 原生 Ctrl+V 在选择该记录之前不能得到该文本
```

---

## Step 4：实现最小范围

Codex 只修改当前 MVP 必须的代码。

禁止顺手：

- 实现下一个 MVP。
- 重构不相关模块。
- 更换技术栈。
- 引入额外基础设施。
- 扩大业务范围。

执行原则：

```text
Make it work
→ Verify it
→ Small cleanup
→ Commit
```

---

## Step 5：L0 自动测试

Client：

```bash
dotnet build
dotnet test
```

Server：

```bash
go test ./...
go build ./...
```

涉及 DB：

```text
Migration Test
Repository Test
```

涉及 API：

```text
Integration Test
Authorization Test
```

测试失败：

```text
不得进入提交阶段
```

---

## Step 6：L1 验证

所有 GUI/Win32 功能至少执行一次 L1。

Codex 必须输出：

```text
L1 Manual Verification

Environment:
Steps:
Expected:
Actual:
Result:
```

如果 Codex 无法亲自完成人工 GUI 操作，则必须：

1. 给出精确操作步骤。
2. 等待人工反馈。
3. 未收到 PASS 前不得声称 L1 已通过。

---

## Step 7：L2 验证

以下类型必须 L2：

```text
跨设备 Clipboard
跨设备 Text Sync
跨设备 Image Sync
Realtime Sync
Offline Catch-up
跨设备 Quick Paste
```

标准环境：

```text
Client A = Windows Host
Server   = WSL2 Docker
Client B = Windows Sandbox / Windows VM
```

Codex 必须输出：

```text
L2 Verification

Topology:
Account:
Client A device:
Client B device:
Server address:

Pre-check:
[ ] Clipboard redirection disabled
[ ] Host -> Sandbox native paste blocked
[ ] Sandbox -> Host native paste blocked
[ ] Both clients can access /health

Steps:
1.
2.
3.

Expected:
Actual:
Result:
```

未通过 L2：

```text
不得标记跨设备 MVP 完成
```

---

## Step 8：修复

失败时：

```text
Reproduce
→ Diagnose
→ Minimal Fix
→ L0
→ Required L1/L2
→ Regression
```

不要使用：

```text
TODO
skip
temporary bypass
```

代替当前 MVP 的核心能力。

---

## Step 9：回归

每次提交前至少回归：

```text
当前 MVP
+
所有已完成关键闭环
```

例如已经完成：

```text
文本监听
历史
搜索
Quick Paste
```

实现 Sync 时必须保证上述能力不退化。

---

## Step 10：Git Diff 检查

提交前：

```bash
git status
git diff
```

Codex 必须检查：

- 是否包含当前 MVP 外修改。
- 是否包含真实密码。
- 是否包含 Token。
- 是否包含 Debug 临时代码。
- 是否包含测试生成文件。
- 是否修改了不该修改的架构。

---

## Step 11：提交

满足门禁后提交：

```bash
git add <本 MVP 相关文件>
git commit -m "..."
```

不要无脑：

```bash
git add .
```

如果存在无关工作区修改。

---

# 10. 提交门禁

普通本地 MVP：

```text
[ ] Acceptance Criteria PASS
[ ] L0 PASS
[ ] L1 PASS
[ ] Regression PASS
[ ] Git Diff Reviewed
```

跨设备 MVP：

```text
[ ] Acceptance Criteria PASS
[ ] L0 PASS
[ ] L1 PASS
[ ] L2 PASS
[ ] Regression PASS
[ ] Git Diff Reviewed
```

Release：

```text
[ ] L0 PASS
[ ] L1 PASS
[ ] L2 PASS
[ ] L3 PASS
[ ] Stability PASS
```

---

# 11. 验证结果记录格式

README 中每个 MVP 不只记录完成/未完成，还应记录验证等级。

推荐：

```text
[x] MVP-15 第二设备拉取历史
    L0: PASS
    L1: PASS
    L2: PASS
    L3: NOT YET
    Commit: abc1234
```

如果：

```text
L0 PASS
L1 PASS
L2 NOT RUN
```

则状态必须仍为：

```text
[ ] MVP-15
```

不能标记完成。

---

# 12. MVP 路线与最低验证等级

| MVP | 场景 | 最低验证 |
|---|---|---|
| MVP-01 | 应用启动/Tray | L1 |
| MVP-02 | 文本 Clipboard 监听 | L1 |
| MVP-03 | 文本历史持久化 | L1 |
| MVP-04 | 历史列表 | L1 |
| MVP-05 | 搜索 | L1 |
| MVP-06 | Ctrl + ` 呼出 | L1 |
| MVP-07 | 文本快速粘贴 | L1 |
| MVP-08 | 本地图片历史 | L1 |
| MVP-09 | 图片快速粘贴 | L1 |
| MVP-10 | Server + PostgreSQL | L1 |
| MVP-11 | 管理员创建用户 | L1 |
| MVP-12 | 用户登录 | L1 |
| MVP-13 | 设备注册 | L1 |
| MVP-14 | 文本上传 Server | L1 |
| MVP-15 | 第二设备拉取历史 | **L2** |
| MVP-16 | WebSocket 实时同步 | **L2** |
| MVP-17 | 断线补偿 | **L2** |
| MVP-18 | 图片上传 | L1 |
| MVP-19 | 图片跨设备同步 | **L2** |
| MVP-20 | 用户隔离 | L1 + 自动安全测试 |
| MVP-21 | 账号禁用与重置 | L1 |
| MVP-22 | Windows 安装 | L2 |
| MVP-23 | 稳定与恢复 | L2，发布前 L3 |

---

# 13. 关键 L2 闭环测试

---

## 13.1 文本 Host → Sandbox

前置：

```text
Host Client    = HOST-PC
Sandbox Client = CLOUD-DEV
Account        = devuser
```

Host：

```text
Ctrl+C

HOST-TEXT-001
```

Sandbox 原生：

```text
Ctrl+V
```

此时应该：

```text
不能直接得到 HOST-TEXT-001
```

然后：

```text
Ctrl + `
```

应该看到：

```text
HOST-TEXT-001
HOST-PC
```

选择并：

```text
Enter
```

才成功粘贴。

完整链路：

```text
Host Clipboard
→ Host Client
→ WSL Server
→ Sandbox Client
→ Sandbox History
→ Quick Paste
→ Sandbox Clipboard
→ Target App
```

---

## 13.2 文本 Sandbox → Host

必须反向测试：

```text
Sandbox Ctrl+C
CLOUD-TEXT-001
```

Host：

```text
Ctrl+`
```

出现：

```text
CLOUD-TEXT-001
CLOUD-DEV
```

Enter 可粘贴。

只有：

```text
Host → Sandbox PASS
Sandbox → Host PASS
```

才算文本跨设备闭环 PASS。

---

## 13.3 图片闭环

Host：

```text
Win+Shift+S
```

截图。

Sandbox：

```text
Ctrl+`
```

要求：

- 显示缩略图。
- 来源 HOST-PC。
- Enter 后可粘贴到 Paint/Word 等程序。

然后反向：

```text
Sandbox Screenshot
→ Host
→ Quick Paste
→ Paste
```

两个方向均 PASS。

---

## 13.4 断线补偿

步骤：

1. Client B 停止或断网。
2. Client A 连续复制：

```text
OFFLINE-001
OFFLINE-002
OFFLINE-003
```

3. Client B 恢复。
4. Client B 执行 catch-up。
5. 历史出现三条缺失记录。

验证：

```text
不能依赖“下一次新 Clipboard”才触发恢复
```

必须在恢复连接后主动补齐。

---

# 14. 多用户隔离验证

用户：

```text
user-a
user-b
```

Host：

```text
user-a
```

Sandbox：

```text
user-b
```

Host 复制：

```text
USER-A-SECRET-001
```

Sandbox：

```text
Ctrl+`
```

必须永远看不到。

自动 API 测试必须覆盖：

```text
User B GET User A Clip       -> deny
User B DELETE User A Clip    -> deny
User B GET User A Image      -> deny
User B PATCH User A Device   -> deny
User B DELETE User A Device  -> deny
```

---

# 15. Sandbox 与 VM 的使用建议

Windows Sandbox：

优点：

- 环境干净。
- Clipboard 可隔离。
- 启动快。
- 适合 L2 MVP 验证。
- 适合安装包测试。

缺点：

- 关闭后环境销毁。
- 需要重复部署 Client。

Hyper-V/其他 Windows VM：

优点：

- 持久化。
- Device ID 不变。
- SQLite 不丢。
- 更接近长期云桌面。

建议：

```text
开发早期：
Windows Sandbox

开发中后期：
持久 Windows VM

Release：
真实云桌面
```

---

# 16. Bug 循环

Bug 修复必须：

```text
Reproduce
  ↓
Write Test / Repro Steps
  ↓
Fix Minimal Scope
  ↓
L0
  ↓
Required L1/L2
  ↓
Regression
  ↓
Commit
```

提交：

```text
fix(clipboard): handle transient clipboard access failure
```

---

# 17. 重构规则

只有在：

```text
已有测试保护
```

情况下允许重构。

重构前：

```text
L0/L1/L2（按相关功能）全部 PASS
```

重构后：

```text
相同验证等级再次 PASS
```

禁止用“架构优化”跳过当前 MVP。

---

# 18. Codex 每轮开始输出模板

```text
## Current State

Branch:
Git status:
Last commit:
Completed MVPs:
Current failing tests:
Existing uncommitted changes:

## Selected MVP

MVP ID:
Name:
Required Verification Level:

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

## L0 Test Plan

...

## L1 Verification Plan

...

## L2 Verification Plan

N/A
或
具体 Host + WSL + Sandbox 步骤
```

---

# 19. Codex 每轮完成输出模板

```text
## Implementation Summary

## Files Changed

## L0 Result

Build:
Unit Test:
Integration Test:

## L1 Result

Environment:
Steps:
Expected:
Actual:
Result:

## L2 Result

Environment:
Topology:
Clipboard Isolation Pre-check:
Steps:
Expected:
Actual:
Result:

## Regression Result

## Git Diff Review

## Commit

Commit hash:
Commit message:

## Remaining Issues

## Next Recommended MVP
```

如果某等级尚未验证：

```text
Result: NOT VERIFIED
```

禁止写：

```text
PASS
```

---

# 20. Codex 提交规则

只有达到当前 MVP 最低验证等级后才能提交“完成型 Commit”。

例如 MVP-16：

```text
L0 PASS
L1 PASS
L2 NOT RUN
```

此时不能提交：

```text
feat(sync): complete MVP-16 realtime text sync
```

最多可以保留未提交修改，或者提交明确的中间开发 Commit（如果项目策略允许），但 README 不能标记完成。

最终完成 Commit 必须：

```text
feat(sync): complete MVP-16 realtime text sync
```

并满足：

```text
L2 PASS
```

---

# 21. 开发进度衡量方式

不以：

```text
代码行数
模块数量
接口数量
```

衡量。

只以：

```text
已经验证闭环的 MVP
```

衡量。

最终里程碑：

```text
✓ 本地文本闭环
✓ 本地图片闭环
✓ 登录闭环
✓ 设备闭环
✓ 文本跨设备闭环（L2）
✓ 实时同步闭环（L2）
✓ 断线恢复闭环（L2）
✓ 图片跨设备闭环（L2）
✓ 用户隔离闭环
✓ 安装运行闭环
✓ 真实云桌面闭环（L3）
```

---

# 22. 日常 Codex 指令

每次继续开发：

```text
继续 ClipBridge 开发。

阅读：

README.md
01-architecture.md
02-loop-engineering.md

检查：

git status
git log
当前测试
README MVP 状态

选择最早一个未完成闭环的 MVP。

一次只做一个 MVP。

严格执行：

Scenario
→ Plan
→ Implement
→ L0
→ L1
→ L2（如要求）
→ Regression
→ Git Diff Review
→ Commit

如果当前 MVP 要求 L2：

使用：
Windows Host Client A
+
WSL2 Docker Server
+
Windows Sandbox/VM Client B

Windows Sandbox 必须关闭 ClipboardRedirection，
并在测试前验证宿主与 Sandbox 原生 Clipboard 已隔离。

没有通过要求的验证等级：
不要标记 MVP 完成；
不要更新为 PASS；
不要提交 complete MVP 的 Commit。

完成后：
- 更新 README MVP 状态和 L0/L1/L2/L3 结果
- 提交代码
- 输出 commit hash
- 停止
- 不自动进入下一 MVP
```
