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

## 1.1 当前全局产品目标（2026-10-02）

让已配对的 Windows 设备像处于同一台计算机上一样使用剪贴板：安装后可后台随系统启动；复制内容通过可恢复的独立 HTTP 请求同步；接收设备自动更新本机剪贴板，用户直接按 `Ctrl+V` 即可粘贴。配对必须由被请求设备确认后才持久化；同步文本和不超过 2 MiB 的图片；不实现文件传输。

体验原则：默认无感运行、托盘轻提示、避免弹窗打断；安全和数据正确性优先于“无感”，所以配对确认、冲突保护、用户数据隔离和可关闭自动剪贴板不能省略。

本目标覆盖并优先于旧路线中“WebSocket 实时通知”和“远端内容不自动写系统剪贴板”的未完成设想。现有已验收记录仍保留为历史事实，不因目标更新而自动改成 PASS。

## 1.2 后续循环顺序

| 顺序 | 闭环 | 主要交付 | 最低验证 |
|---|---|---|---|
| 1 | 剪贴板与同步故障稳定性 | 修复当前剪贴板写入报错；失败不停止监听/同步；诊断焦点、剪贴板争用、授权过期和网络中断 | L0 + L1 + L2 |
| 2 | 设备识别码配对 | 持久化 6 位大写字母/数字码；输入服务器地址和对端码发起请求；对端确认/拒绝；接受后双方保存双向配对；支持改码、解绑、撤销 | L0 + L1 + L2 |
| 3 | 文本无感同步 | 保留独立 HTTP 请求与短间隔拉取；补齐重试、退避、凭证续期/恢复；接收端自动更新系统剪贴板；实现本机新复制优先和同步回环抑制 | L0 + L1 + L2 |
| 4 | 2 MiB 图片同步 | 图片校验、上传下载、历史缩略图和自动剪贴板更新；拒绝超过上限的内容 | L0 + L1 + L2 |
| 5 | Windows 安装与开机启动 | 安装目录选择、升级/卸载、开机启动选项；保留用户历史和配对资料 | L0 + L1 + L2 |
| 6 | 发布稳定性闭环 | 断网、服务重启、休眠恢复、凭证撤销、重复与并发复制、跨版本升级 | L0 + L1 + L2；发布前 L3 |

进入每个循环前先复现并记录现象。剪贴板异常和同步停滞在证据证明同源前分别追踪；日志不得包含剪贴板正文、图片或长期凭证、令牌。

### 1.3 当前循环验收进度

上一循环（顺序 1：剪贴板与同步故障稳定性）已由用户完成验收。

- 诊断：服务日志确认历史拉取会遇到 `401`；访问令牌有效期为 15 分钟，客户端原实现会清空令牌并取消轮询。剪贴板写入错误是另一条故障链。
- 实现：剪贴板 `ExternalException` 最多重试 5 次；HTTP 请求收到 `401` 时使用当前进程内存中的登录信息重新认证并重试一次，不把密码或令牌写入同步配置文件。
- L0：PASS（剪贴板争用重试、令牌恢复、窗口/输入控件焦点恢复回归；客户端 48 项测试通过；Go 全量测试、构建通过；Release 构建通过）。
- L1/L2：PASS（用户确认 Host/VM 文本同步和快速粘贴修复验证通过）。
- 图片同步：属于全局计划第 4 项“2 MiB 图片同步”，不是本轮第 1 项稳定性循环；Server 上传与持久化是下一闭环，跨设备历史拉取和系统剪贴板接收仍待后续闭环。

### 1.4 当前循环：设备识别码配对

配对模式已由用户确认：匿名登记后由对端明确确认；设备码用于发现，不充当长期凭证。

- 实现：本机持久化 6 位设备码；Server 匿名登记并仅返回一次随机设备凭证；客户端以当前 Windows 用户 DPAPI 加密保存；对端轮询待确认请求，接受后创建双向配对并授权配对组历史；支持拒绝、改码撤销和解绑。
- L0：PASS（客户端 48 项测试；Go 全量测试和构建，覆盖匿名登记 IP 限流；Windows x64 自包含发布构建通过）。
- L1：PASS（独立临时 PostgreSQL/Server 实测匿名登记、凭证授权、配对前历史隔离、请求端码和目标端提示数据、确认后共享历史；重启 Server 后配对与设备凭证仍有效）。
- 启动恢复：客户端使用本机 DPAPI 保护的设备凭证和已保存 Server 地址自动恢复轮询；发起请求时保存目标识别码，接受请求时保存请求方识别码，并在配对窗口回显。已配对的旧设备可输入对端码补存；Server 返回 `already_paired` 时不会重复配对。旧版会话缺少对端码时，双方各需补录一次。
- L0：PASS（新增发起方/接收方重启恢复、旧会话兼容、已配对补存和延迟历史响应启动测试；55 项客户端测试通过；WPF Release 构建通过，0 警告、0 错误）。
- L2：PASS（用户确认配对、双向文字复制粘贴及重启后自动恢复；曾观察到恢复需 30–60 秒）。
- 启动恢复优化：此前恢复方法同步等待完整历史分页，恢复状态也只能在拉取完成后显示；轮询还会等待首个周期，随后与全量历史拉取竞争同一把锁。现在恢复时立即报告“正在恢复同步”并启动首轮轮询，首轮成功后报告“已恢复同步”；历史分页在后台继续，不阻塞会话恢复调用。
- 本次 L0：PASS（客户端 55 项测试；增加了模拟历史响应延迟 2 秒、恢复调用仍在 1 秒内返回的回归验证；WPF Release 构建通过）。
- 发布：新版已从 `artifacts/staging/ClipBridge` 同步到固定目录 `artifacts/ClipBridge`，6 个文件逐项 SHA256 一致；`ClipBridge.App.exe` SHA256 为 `7432610DBA009269970F133CD9E76CAA3AA065F683561EDC8AD17F54EEED951A`。需用户在 Host 和 VM 实际确认启动状态和数据同步响应。

### 1.5 启动恢复性能循环（本轮）

- 计时：日志写入 `%LOCALAPPDATA%\ClipBridge\startup-performance.log`，只记录阶段名、耗时和条数，不记录剪贴板正文、设备码、地址、令牌或密码。覆盖 SQLite 初始化/文本清理、主窗口本地历史显示、设备会话与配置读取、初始历史分页的 HTTP、JSON 解析、本地批量写入、游标保存和总耗时。
- 本地启动：文本去重清理改为一次性 SQLite 迁移。若已有文本唯一索引，则直接标记迁移完成，不再每次扫描/哈希全部文本；旧库无索引时清理一次，并在同一事务创建唯一索引和写入版本号。
- 历史页写入：新增 `UpsertRemoteTexts`，每页复用连接、预编译命令和单个事务；分页、去重、新旧来源优先级及未同步本地内容保护保持原语义。
- L0：PASS（57 项客户端测试通过；含旧版重复记录迁移及批量 upsert 语义测试；WPF Release 发布构建通过，0 警告、0 错误）。
- 上一版发布：`artifacts/ClipBridge` 的旧 SHA256 为 `7CA75065BC5862CCFE3A972138D31BF967F922611F740E710F065ECF52FFFCDF`。
- L2：待用户在 Host/VM 实机重启验证；启动计时日志位于 `%LOCALAPPDATA%\ClipBridge\startup-performance.log`。
- 后续瓶颈诊断：旧日志两次都显示历史 HTTP 请求约 21 秒，而解析、本地写入与游标保存合计不足 40 毫秒；旧计时把等待响应头和响应体下载合并，无法区分客户端到服务端链路与 Server 查询耗时。
- 本次诊断补充：客户端改为分别记录收到响应头、下载响应体（含字节数）和 JSON 解析；Server 在 `Server-Timing` 响应头中返回身份校验及历史查询耗时。服务端历史查询计时包含数据库查询、扫描和游标组装。
- 查询索引：为配对历史候选查询新增 `(origin_device_id, created_at, id)` 索引；通过 `CREATE INDEX IF NOT EXISTS` 幂等创建。待实际服务更新并生成新日志后，依据查询计划与实测耗时决定是否保留/调整该索引。
- 根因实验：Host 的 .NET `HttpClient` 请求 `http://localhost:18081/health`，默认和禁用代理两种方式都超过 2 秒超时；改为 `127.0.0.1` 后分别约 12 毫秒和 3 毫秒成功。DNS 返回 `::1` 和 `127.0.0.1`，直连 `::1:18081` 超时而 `127.0.0.1:18081` 约 5 毫秒连通；这与 localhost 优先走不可达 IPv6 回环吻合，能解释旧日志里 Server 未及时收到请求的现象。
- 客户端修复：将仅主机名为 `localhost` 的传输地址规范为 IPv4 回环；启动恢复时仍复用原设备凭证，且以原始 Server 地址继续读写历史游标，避免地址规范化导致重置游标或重新登记。其它地址不变。
- 数据库实测：当前本地配对历史约 80 条，PostgreSQL `EXPLAIN ANALYZE` 执行约 0.214 毫秒，因表规模小使用顺序扫描；新索引已幂等创建，但这份小数据不能证明大历史量下的计划表现。
- L0：客户端 57 项测试、Go 全量测试和 Server Docker 构建通过；L1 与 Host/VM 实机重启验收待固定目录客户端更新后进行。
- 发布：诊断与回环地址修复版已覆盖至固定目录 `artifacts/ClipBridge`，6 个文件逐项 SHA256 一致；`ClipBridge.App.exe` SHA256 为 `0AB4E19D7388E25AA7D1CDBFF59B1A3591691BA9DD9E493B085F7D556E7A2FCE`。L2 待 Host 重启并检查分段耗时日志。

启动恢复性能优化已由用户实机验证通过；配对与重启后自动恢复的功能验收已通过。

### 1.6 历史列表滚动序号循环

- 现象：相同历史内容在滚动后仍对应同一批记录，但左侧序号可能从正常区间跳到接近 1000 的旧值。
- 根因：序号读取虚拟化 `ListBoxItem` 的 `AlternationIndex`；列表启用容器回收，回收容器在重新绑定数据项时可能保留旧交替索引，导致序号与当前行脱节。
- 首次修复复测：通过 `ItemContainerGenerator.IndexFromContainer` 读取可视行的实时索引后，快速滚动仍会短暂显示空白；虚拟化回收期间容器尚未重新映射时，索引会暂时返回 `-1`。
- 当前修复：刷新数据时按当前筛选结果为每个列表项创建带固定序号的视图模型。滚动和回收只复用呈现容器，不再参与序号计算。窗口标题移除记录总数。
- 回归原因：后续安装包所基于的源码只接入了视图模型和 XAML 的 `SequenceNumber` 绑定，`RefreshHistory()` 却仍将原始 `ClipboardItem` 放入列表，绑定取不到属性；同时刷新与快捷键失败分支仍把记录数拼入窗口标题。因此新安装包准确包含了这段不完整的源码修复，并非打包到了不同版本。
- 本轮修复：`RefreshHistory()` 现在将匹配结果包装为带连续序号的 `HistoryListEntry`；回车与双击粘贴从包装项取回原始记录；普通及快捷键错误标题均不再包含记录数。
- L0：PASS（WPF Release 构建通过，0 警告、0 错误；Windows x64 自包含发布及 Inno Setup 安装包重建通过；安装包 SHA256 `CF699F73D541535366EE861DA2F891BA7C8E3A970EA5066958F7D4EE53082408`）。
- L1：待固定目录发布后验证顶部、中段、底部和快速滚动，序号始终连续且与内容对应；确认窗口标题不显示记录总数。

### 1.7 图片上传循环（MVP-18）

- 验收条件：已认证设备通过一次 HTTP 请求上传合法 PNG/JPEG；服务端拒绝空内容、伪造 MIME、格式损坏和超过 2 MiB 的载荷；记录归属由用户或已配对设备凭证确定；重复 ID 与相同内容幂等，内容冲突拒绝；图片和元数据可在 Server 重启后保留。
- 实现：新增 `POST /api/v1/images`，使用 `X-Clip-ID` 提供剪贴板 ID、Content-Type 标明图片格式、请求体只包含图片数据。图片对象原子写入 `CLIPBRIDGE_IMAGE_DIR`，PostgreSQL 保存来源设备、格式、哈希、大小和对象键；Compose 配置持久图片卷。
- GitNexus 影响：路由变更影响 Server 启动和兼容路由入口（LOW）；图片存储新增接口通过设备配对组/用户作用域校验；动态接口分发风险已用源码搜索和 API 回归覆盖。
- L0：PASS（Go 全量测试通过；`docker compose config --quiet` 及 Server 镜像构建通过）。
- L1：PASS（隔离 PostgreSQL/Server 环境中，设备凭证注册并上传 PNG 返回 201；相同 ID/内容重试返回 200；同 ID 不同内容返回 409；无凭证返回 401；恰好 2 MiB 返回 201、超过 2 MiB 返回 413；服务重启后图片对象与元数据均保留）。
- 范围说明：本循环完成 Server 接收与持久化。随后 MVP-19 已完成客户端图片推送、远端历史下载、历史缩略图及系统剪贴板同步，并由用户完成 Host/VM 验收（详见 1.8）。

### 1.8 图片跨设备同步与 Windows 安装循环

#### MVP-19 图片跨设备同步（已验收）

- 实现：客户端在 2 MiB 限制内上传 PNG 图片；接收端拉取远端图片历史并下载对象，保存到本地历史，使缩略图和系统剪贴板可用；超限或非法图片由 Server 拒绝。
- L0：PASS（客户端 57 项测试；Go 全量测试与构建通过；Windows x64 自包含发布构建通过）。
- L1/L2：PASS（用户确认 Host 与关闭剪贴板共享的 Windows VM 双向同步文字和截图；图片缩略图可见）。
- 回归修复：用户确认 VM 双击粘贴 Host 截图后，截图又作为新本地记录同步回 Host。旧逻辑的“一次性忽略下一张图”可能被排队中的旧剪贴板通知提前消费；改为写入图片成功后记录 Windows 剪贴板序号，仅忽略该次更新。Release 构建和安装包已重建，L1 回环复测待用户确认。

#### 当前循环：MVP-22 Windows 安装与登录自启

- 验收条件：用户可选择安装目录，升级时默认恢复上次安装目录；按当前用户安装且无需管理员权限；安装时可选登录 Windows 后在托盘后台启动；托盘菜单可随时开启/关闭；卸载前强制结束 ClipBridge 进程，并询问是否删除 `%LOCALAPPDATA%\\ClipBridge` 中的历史、图片、设备码和配对资料，默认保留。
- 安装目录页修正：Inno Setup 的 `DisableDirPage=auto` 会在检测到已安装版本时自动隐藏目录页。本轮显式设为 `no`，保留页面供用户更改，同时将既有目录作为默认值。
- 实现：以 Inno Setup 生成固定位置安装包；通过稳定 AppId、`UsePreviousAppDir=yes` 和当前用户注册表中的最近安装目录兜底复用上次目录；完成页提供“安装完成后启动 ClipBridge（托盘后台运行）”选项；卸载阶段以 `taskkill /F /T` 结束 ClipBridge 客户端进程，完成程序卸载后按用户选择保留或删除个人数据（默认保留）；自启只使用当前用户 Run 项并传入 `--background`；后台启动创建剪贴板监听和热键窗口但不显示历史主窗；应用内新增自启开关。安装目录与用户数据目录分离。
- 安装文案：向导固定使用简体中文，任务和托盘菜单统一显示“开机时自动启动”；安装任务默认勾选，升级时不恢复上次任务选择，用户仍可手动取消；语言文件及其 MIT 许可说明保存在 `installer/Languages`。
- 首次热键弹窗问题：用户确认安装后第一次按 `Ctrl+`` 只出现标题栏大小的窗口，手动展开后后续正常。后台启动此前先以最小化状态显示主窗口，再隐藏并还原，可能留下异常原生恢复尺寸；现在改用 `WindowInteropHelper.EnsureHandle()` 创建隐藏 HWND，让剪贴板监听和热键注册工作时不显示或最小化窗口。
- L0：PASS（Windows x64 自包含发布和 Inno Setup 简体中文安装包编译通过；安装目录页始终显示并默认带出上次路径，安装完成启动、卸载进程清理及个人数据选项均已编入；安装时自启默认勾选。当前包 SHA256 `43777E84F1B2916FA3E36C5F059F6E6CEB68848D2ADEA3141B45988F18569836`）。
- L1/L2：待用户验收安装目录选择、登录启动时窗口不闪现、托盘自启开关、卸载后数据保留；Host 与 VM 各安装一次后验证双向文字/图片同步。
- 交付：安装包统一归档在 `artifacts/ClipBridge-Installers`；当前使用 `ClipBridge-Setup.exe`，更早版本以 `Archive-` 前缀标记。原安装器进程曾占用旧输出文件，因此归档时未覆盖任何旧包。

### 1.9 当前循环：MVP-16 远端内容自动进入系统剪贴板

- 验收条件：另一设备复制文字或不超过 2 MiB 的图片后，本机历史与 Windows 系统剪贴板都收到相同内容，可直接 `Ctrl+V`；程序写剪贴板不回环上传；较新的本机复制优先；写入失败不停止同步。
- 现状诊断：远端图片已下载并落入本机历史，但历史更新回调之前只读取文字记录并写入系统剪贴板，所以图片缩略图可见、粘贴仍是旧文字。
- 实现：历史更新后按远端最新记录类型处理文字或图片；遇到较新的本机复制则跳过覆盖；图片写入系统剪贴板后登记当前剪贴板序列号，避免 ClipboardMonitor 把它重新记录并上传；本机新复制仍会取消待执行的远端写入。失败时保留历史记录并报告状态。
- L0：PASS（WPF Release 构建通过，0 警告、0 错误；Windows x64 自包含 Release 发布及简体中文 Inno Setup 编译通过；本轮客户端回归测试未运行）。安装包：`artifacts/ClipBridge-Installers/ClipBridge-Setup.exe`，SHA256 `43777E84F1B2916FA3E36C5F059F6E6CEB68848D2ADEA3141B45988F18569836`；应用文件 SHA256 `83A6565D3922B4FDC9D6FD23B1D557CD1F46A8BB9D3540340FDAD97EBC72D33D`。
- L1/L2：PASS（用户已在 Host/VM 验证远端文字和截图自动进入系统剪贴板并可直接粘贴；安装向导默认勾选开机启动）。

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
│        │ HTTPS REST                    │
│        ▼                               │
│ WSL2 Ubuntu                            │
│ └── Docker                             │
│     ├── clipbridge-server              │
│     └── postgres                       │
│                ▲                       │
│                │ HTTPS REST            │
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
- HTTP 请求与短间隔拉取。
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
HTTP polling / pairing
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

下表中的早期 MVP 用于保留既有实现和验收记录；下一轮的实际执行顺序以 §1.2 为准。旧计划中的 WebSocket 目标已经调整为独立 HTTP 请求与短间隔拉取；HTTP 单次请求模式已经存在，工作重点是恢复、重试和授权续期，不重复建设长会话。

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
| MVP-16 | HTTP 短间隔同步与通知 | **L2** |
| MVP-17 | HTTP 断线补偿、重试与凭证恢复 | **L2** |
| MVP-18 | 图片上传（上限 2 MiB） | L1 |
| MVP-19 | 图片跨设备同步与自动进入剪贴板 | **L2** |
| MVP-20 | 用户隔离 | L1 + 自动安全测试 |
| MVP-21 | 账号禁用与重置 | L1 |
| MVP-22 | Windows 安装与开机启动 | L2 |
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
feat(sync): complete MVP-16 HTTP text sync
```

最多可以保留未提交修改，或者提交明确的中间开发 Commit（如果项目策略允许），但 README 不能标记完成。

最终完成 Commit 必须：

```text
feat(sync): complete MVP-16 HTTP text sync
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
