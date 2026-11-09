# ClipBridge

轻量级 Windows 跨设备剪贴板同步工具。

ClipBridge 用于解决：

> 本地主机和云桌面无法直接复制粘贴，但两端都可以访问同一台 Linux Server。

通过设备识别码和对端确认配对的 Windows 设备可以同步文本和图片剪贴板。正常使用不需要创建或输入用户账号；数据访问边界是设备配对组。

## 下载与安装

请从 [GitHub Releases](https://github.com/lzcy1326/clip-bridge/releases/latest) 下载正式交付物：

- **Windows：** `ClipBridge-Setup.exe`。运行安装程序，可选择安装目录和是否开机自动启动。
- **Linux Server：** `ClipBridge-Server-Deploy.zip`。解压后导入离线镜像，并运行 Compose 启动服务：

  ```bash
  docker load -i images.tar
  docker compose up -d
  ```

Linux 部署包不包含共享密码。首次启动时，Compose 会为数据库和旧版登录兼容接口生成随机密钥，保存在 Docker 持久化卷中；删除该卷会同时删除密钥。局域网连接默认端口为 `10002`。完整步骤见[Linux 部署指南](docs/server-deployment-linux.md)。

ClipBridge 的发布渠道仅提供 Windows 安装包和 Linux 部署包，不单独发布 Windows 客户端文件。

## 主要功能

- 文本和截图图片的本地剪贴板历史、搜索及快速粘贴。
- 设备码配对后跨设备同步文本与不超过 2 MiB 的图片。
- SSE 主动通知，配合 HTTP 游标拉取和 4 秒轮询兜底。
- 本机历史默认保留 7 天、最多 300 条；服务器默认保留 15 天、每个配对组最多 500 条。历史保留规则可配置。

## 开源许可证

本项目采用 [MIT License](LICENSE)。

---

# 1. Codex 必读

在修改任何代码之前，按顺序阅读：

1. [`01-architecture.md`](./docs/01-architecture.md)
2. [`02-loop-engineering.md`](./docs/02-loop-engineering.md)
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
HTTP REST + SSE
Per-device credentials
Filesystem
Docker Compose
```

JWT、Argon2id 与默认管理员初始化仅属于旧账号兼容路径，不是设备配对同步的必要组件。

不得自行替换。

## Client 本机启动

开发时可运行 WPF 项目。Windows 发布包使用自包含的单文件配置，双击发布目录中的 `ClipBridge.exe` 启动，不需要通过控制台版 `dotnet.exe` 加载应用。

```powershell
dotnet publish src/client/ClipBridge.App/ClipBridge.App.csproj -p:PublishProfile=win-x64-self-contained
```

发布文件统一位于 `artifacts/ClipBridge/ClipBridge.exe`。

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
Device
Cross-device Sync
```

设备码仅用于发现和发起配对；Server 使用设备凭证认证，并按配对组隔离数据。旧版账号登录与管理员初始化代码暂时保留作兼容，不属于当前用户流程或后续 MVP 目标。

---

# 6. 开发进度

Codex 每完成一个 MVP 后，在这里更新状态。

MVP-11/12 的账号创建与登录记录是历史实现及兼容接口，不是当前产品身份流程；新设备走 MVP-24 设备识别码配对。MVP-20 按配对组而非用户账号验证数据隔离。

建议格式：

```text
[x] MVP-01 应用可启动
[x] MVP-02 文本 Clipboard 监听
[x] MVP-03 文本历史持久化
[x] MVP-04 历史列表 UI
[x] MVP-05 历史搜索
[x] MVP-06 Ctrl + ` 呼出
[x] MVP-07 文本快速粘贴闭环
    L0: PASS（23 项测试；成功粘贴后历史记录置顶，保留原复制时间）
    L1: PASS（用户确认单窗、光标处打开、回车粘贴并置顶、Esc 关闭无闪现，复制监听正常）
[x] MVP-08 本地图片 Clipboard
    L0: PASS（26 项自动测试；Release 构建通过）
    L1: PASS（用户确认截图记录正常生成，历史列表缩略图可见）
[x] MVP-09 图片快速粘贴
    L0: PASS（27 项自动测试；Release 构建通过）
    L1: PASS（用户确认快捷呼出显示旧图片，回车粘贴并置顶）
[x] MVP-10 Server + PostgreSQL 启动
    L0: PASS（Go 单元测试通过；服务二进制构建成功）
    L1: PASS（PostgreSQL 与 Gin 容器均健康；`/health` 返回 HTTP 200 和 `{"status":"ok"}`）
[x] MVP-11 管理员创建用户
    L0: PASS（账号名校验、密码策略、Argon2id 独立随机盐测试；Go 全量测试与构建通过）
    L1: PASS（本机管理命令成功创建 USER/ACTIVE 账号；数据库验证密码字段为 Argon2id 哈希）
[x] MVP-12 用户登录
    L0: PASS（Argon2id 密码核验、JWT 签名与声明、统一凭据错误、请求体限制及 IP 限流测试；Go 全量测试与构建通过）
    L1: PASS（本机 Compose 服务中，测试账号登录返回 15 分钟 Bearer Token；错误密码、未知及禁用账号均返回 401；健康检查正常）
[x] MVP-13 设备注册
    L0: PASS（JWT 签名/过期校验、设备字段校验、未授权和伪造 user_id 拒绝测试；Go 全量测试与构建通过）
    L1: PASS（本机登录后注册设备返回 201；PostgreSQL 设备归属与令牌用户一致；无令牌返回 401，客户端 user_id 返回 400）
[x] MVP-14 文本上传 Server
    L0: PASS（文本校验、SHA-256、认证、设备归属、伪造 user_id、幂等重试、请求体上限测试；Go 全量测试与构建通过）
    L1: PASS（本机 Compose 登录并注册设备后上传返回 201；相同 ID 重试返回 200，数据库仅一条；无令牌 401、未知设备 403、伪造 user_id 400）
[x] MVP-15 第二设备拉取历史
    L0: PASS（Server Go 全量测试通过；Client 回归测试通过，WPF Release 构建通过）
    L1/L2: PASS（用户已确认 Host 与无剪贴板共享的 VirtualBox Windows 11 VM 双向文字同步和快速粘贴验证通过）
[x] MVP-16 HTTP 短间隔同步与自动进入系统剪贴板
    L0: PASS（WPF Release 构建通过，0 警告、0 错误；Windows x64 自包含发布与简体中文 Inno Setup 安装包编译通过；本轮客户端回归测试未运行）
    L1/L2: PASS（用户已验证远端文字和截图自动进入本机系统剪贴板，并可直接粘贴；安装向导默认勾选开机启动）。当前安装包 SHA256 `43777E84F1B2916FA3E36C5F059F6E6CEB68848D2ADEA3141B45988F18569836`
[ ] MVP-17 配对设备断线补拉、文本待发送队列与重试
    保留原因：接收端须按持久化游标补齐离线期间的文本和图片；发送端目前文本上传失败后没有持久化待发送队列，必须补上重试/重连发送。设备凭证无效时提示重新登记/配对，不做用户登录或 refresh-token 恢复。
[x] MVP-18 图片上传
    L0: PASS（Go 全量测试通过；Compose 配置及 Server 镜像构建通过）
    L1: PASS（隔离 PostgreSQL/Server：设备凭证上传 PNG 201；重复上传 200；内容冲突 409；未授权 401；2 MiB 边界 201/413；Server 重启后图片与元数据保留）
[x] MVP-19 图片跨设备同步
    L0: PASS（客户端 57 项测试；Go 全量测试及构建通过；自包含客户端发布通过）
    L1/L2: PASS（用户确认 Host 与无剪贴板共享的 VM 可双向同步截图，历史缩略图正常显示）
[ ] MVP-20 配对组数据隔离
    保留原因：即使没有用户账号体系，不同配对组之间仍必须不可读取、删除或覆盖彼此的数据；pair_group 是当前安全边界。数据库/API 自动隔离测试及跨组实测尚未完成。
[—] MVP-21 账号禁用与重置（移出当前路线）
    原因：新用户流程没有账号；设备撤销、解绑、重新配对属于设备生命周期，不再用账号禁用/密码重置建模。
[ ] MVP-22 开机启动与安装
    本轮范围：可选安装目录、当前用户安装与卸载；安装时默认勾选“开机时自动启动”（用户可取消）；托盘菜单可随时开关；安装向导为简体中文；用户历史与配对资料保存在安装目录之外。
    L0: PASS（客户端 57 项测试通过；Windows x64 自包含发布与 Inno Setup 编译通过）
[ ] MVP-23 稳定性与恢复
[ ] MVP-24 设备识别码配对
    L0: PASS（客户端 57 项测试、Go 全量测试与构建、Windows x64 自包含发布构建通过）
    L1: PASS（临时 Server/PostgreSQL 端到端验证：匿名登记、配对前隔离、对端确认、共享历史、Server 重启后凭证与关系保持）
    L2: PASS（用户已确认配对成功、双向文字同步与重启后自动恢复；恢复曾需 30–60 秒，启动恢复改为先恢复轮询、后台补拉历史）
[ ] MVP-25 SSE 通知加速同步
    方案：服务端持久化后发送轻量 SSE 唤醒；客户端收到后通过现有 HTTP 游标立即拉取；4 秒轮询继续兜底，断线重连后补拉。SSE 不承载剪贴板数据。
    L0: PASS（客户端 59 项测试；Go 全量测试；WPF Release 构建；Server Docker 镜像构建通过）
    L1: PASS（SSE 触发即时拉取；禁用 SSE 时 4 秒轮询仍能收到内容；SSE 不含剪贴板正文；更新后的 Server 健康检查正常）
    L2: 待 Host/VM 实测通知延迟、断线恢复及文字/图片双向同步。
[ ] MVP-26 本机与 Server 历史清理
    本机托盘设置按天数、条数和容量清理，默认保留 7 天、最多 300 条；Server 按环境变量配置，默认保留 15 天、每个配对组最多 500 条。自动保留待发送本机记录，并移除不再引用的图片对象。
    L0 基础清理版本：PASS（客户端 62 项测试；Go 全量测试与构建；PostgreSQL 清理集成验证；WPF Release 与 Linux AMD64 Server 镜像构建通过）
    当前增量（默认 7 天/300 条、Server 15 天/500 条、默认端口 10002）：待自动化回归。
    L1/L2: 待用户验收本机清理设置、待发送记录保护、服务端历史删除及配对组容量隔离。
```

MVP-01 验证记录：

```text
L0 Build: PASS
Unit Test: N/A（当前没有测试项目）
L1: PASS（用户确认窗口、托盘打开/退出正常；关闭主窗口隐藏到托盘；自包含发布程序启动正常，应用图标已显示在窗口标题栏）
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
L1: PASS（用户确认历史更新与去重、Tooltip 锚点正确；Ditto 风格序号靠左显示，数字与正文间的竖线贯穿记录行；主页面表头已收进窗口标题栏，记录正文垂直居中）
L2: N/A
L3: NOT YET
```

MVP-04 大段文本显示修正：

```text
L0 Build: PASS（Release 构建成功，0 警告、0 错误）
Unit Test: PASS（两行截断、换行格式、短文本保留，7/7）
L1: PASS（用户确认固定行高、两行预览、Tooltip 可滚动查看完整文本）
```

MVP-05 验证记录：

```text
L0 Build: PASS（Release 构建成功）
Unit Test: PASS（搜索大小写不敏感、最近优先、LIKE 特殊字符按字面匹配、空查询恢复最近记录、条数限制，10/10）
L1: PASS（用户确认关键字筛选、无匹配提示和清空后恢复最近记录均正常）
L2: N/A
L3: NOT YET
```

MVP-06 验证记录：

```text
L0 Build: PASS（Release 构建成功，0 警告、0 错误）
Unit Test: PASS（完整自动测试回归 17/17）
L1: PASS（用户确认全局快捷键唤醒、重复激活、最小化恢复及退出释放）
L2: N/A
L3: NOT YET
```

MVP-10 验证记录：

```text
L0 Unit Test: PASS（Go 1.27，`go test ./...`）
L0 Build: PASS（`go build ./...`；Linux 容器镜像构建成功）
L1: PASS（Docker Compose 中 PostgreSQL 和 Gin 服务均为 healthy；`http://localhost:18080/health` 返回 HTTP 200 和 `{"status":"ok"}`）
端口说明：以上为历史验收记录；当前 Linux 部署默认主机端口为 10002，服务容器仍监听 8080。
启动命令：`docker compose up --build -d`
```

MVP-11 验证记录：

```text
L0 Unit Test: PASS（账号包 3 项；Go 全量测试通过）
L0 Build: PASS（Go 服务与管理命令构建成功；Docker 镜像构建成功）
L1: PASS（通过容器内 `clipbridge-admin create-user` 创建本地 USER；PostgreSQL 中角色为 USER、状态为 ACTIVE，密码仅保存 Argon2id 哈希）
管理命令从标准输入读取两行：第一行为用户名，第二行为密码；只在本机 Docker 容器内运行，不开放未认证的网络创建接口。
本机测试账号凭据保存在当前 Windows 用户的 `%LOCALAPPDATA%\ClipBridge\local-test-account.txt`，该文件未加入版本控制。
```

MVP-12 验证记录：

```text
L0 Unit Test: PASS（账号密码哈希验证、JWT 签发、登录 API 请求校验和 IP 限流测试；Go 全量测试通过）
L0 Build: PASS（Go 全量构建及 Docker 镜像构建成功）
L1: PASS（本机登录 API 实测成功；错误密码、未知用户及禁用用户统一返回 HTTP 401；`/health` 正常）
Access Token: HS256，15 分钟有效；刷新令牌留待后续 MVP。
本机密钥通过 `.env` 的 `CLIPBRIDGE_JWT_SECRET` 配置，要求至少 32 字节；`.env` 已忽略，`.env.example` 只含占位值。
```

MVP-13 验证记录：

```text
L0 Unit Test: PASS（Access Token 签名与有效期校验、设备元数据标准化与边界校验、Bearer 认证、伪造 user_id 拒绝、未认证请求拒绝）
L0 Build: PASS（Go 全量构建；Docker 镜像构建成功）
L1: PASS（本机 Compose 实测登录并注册 Windows 设备；HTTP 201，数据库 user_id 与 Access Token 的 sub 对应用户一致；无令牌 HTTP 401，伪造 user_id HTTP 400）
范围：完成 POST /api/v1/devices；设备列表、修改、删除留待后续场景。
```

MVP-14 验证记录：

```text
L0 Unit Test: PASS（文本/UUID/设备 ID 校验、内容 SHA-256、Bearer 认证、用户归属来自 Token、伪造 user_id 拒绝、重复上传幂等、冲突与未知设备状态映射、请求体上限与 JSON 格式校验）
L0 Build: PASS（Go 全量构建；Docker 镜像构建成功）
L1: PASS（本机 Compose 登录并注册设备；文本上传 HTTP 201；相同 clip ID 重试 HTTP 200；PostgreSQL 只有一条对应记录且 origin_device_id 正确；无令牌 HTTP 401、未知设备 HTTP 403、伪造 user_id HTTP 400；/health 正常）
接口：POST /api/v1/clips，接收 id、device_id、text；兼容本地仓储使用的 32 位无连字符 GUID；user_id 来自 Access Token。服务端按 SHA-256(TEXT + NFC(text)) 计算内容哈希，保留原始文本；同 ID 同内容重试幂等，不同内容复用 ID 返回 HTTP 409。
范围：完成文本上传到 Server；历史拉取、实时通知、断线补偿与图片上传留待后续 MVP。
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
- HTTP 与 SSE 通知。
- 断线恢复。
- 配对设备凭证与配对组隔离。
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
设备识别码配对闭环
设备管理闭环
文本跨设备闭环
断线恢复闭环
图片跨设备闭环
配对组数据隔离闭环
安装运行闭环
```
