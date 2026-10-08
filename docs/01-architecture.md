# 01-architecture.md

# ClipBridge 架构设计

> 项目：ClipBridge  
> 版本：v1.0  
> 目标：让 Windows 本地主机与云桌面通过配对设备共享剪贴板，使用体验尽量接近同一台计算机。
> 核心原则：**后台运行、远端内容自动进入本机剪贴板、配对须经对端确认、同步可恢复、按 MVP 闭环逐步交付。**

---

## 1. 产品目标

ClipBridge 解决以下办公问题：

- 本地主机和云桌面无法直接复制粘贴。
- 两端都能通过网络/代理访问同一台 Linux Server。
- 每台设备可以通过设备识别码发起配对并由对端确认。
- 只有配对并经对端确认的设备可以互相同步。
- 正常使用不需要用户账号；每个配对组只能访问自己的剪贴板数据。

### 1.1 2026-10 产品方向更新

本节更新后续开发目标，优先级高于本文旧版本中“账号密码登录、远端内容只进历史、不自动写系统剪贴板、WebSocket 实时通知”等尚未完成的设计描述。现有实现状态以代码和 loop engineering 验收记录为准。

ClipBridge 的目标使用体验：

```text
设备 A 复制
  → 后台自动同步
  → 设备 B 自动更新系统剪贴板
  → 用户在设备 B 直接 Ctrl+V
```

目标行为：

- 安装后可选择登录 Windows 时自动启动，应用常驻托盘并在后台工作。
- 配对设备间默认自动同步文本和不超过 2 MiB 的图片，并写入接收端系统剪贴板。
- 用户不需要先打开历史窗口、选择记录或手工触发同步。
- 远端到达与用户本机新复制发生冲突时，本机较新的复制优先，不覆盖较新的本机剪贴板。
- 自动写入必须携带来源与内容标记，避免两端互相回传形成同步回环。
- 自动剪贴板行为可在设置中关闭；关闭后内容仍进入本地历史。
- 设备发现使用 6 位大写字母与数字识别码；任何配对必须由被请求设备明确确认后才持久化。
- 识别码用于发起配对，不是长期认证凭据；长期通信使用可撤销、按设备授权的凭证。
- Server 数据通信采用可重试的独立 HTTP 请求。SSE 仅作为新内容到达的低延迟唤醒通知；HTTP 游标拉取仍是数据来源和正确性保障，SSE 断开、丢事件或服务重启均由重连补拉与 4 秒轮询兜底恢复。

图片范围限定为剪贴板截图/图片数据，不包括文件剪贴板；图片传输内容上限为 2 MiB，接收端在验证大小后才保存或更新系统剪贴板。

用户典型链路：

```text
设备 A Ctrl+C
   ↓
ClipBridge Client A → HTTP → Linux Server
   ↓
ClipBridge Client B 自动更新本机 Clipboard
   ↓
设备 B 直接 Ctrl+V
```

历史列表和 `Ctrl + \`` Quick Paste 保留为查找、选择旧记录及手动粘贴的补充入口。

---

## 2. V1 范围

### 2.1 必须实现

#### Windows Client

- 文本剪贴板监听。
- 图片剪贴板监听。
- 本地历史记录。
- 搜索。
- 图片缩略图。
- 系统托盘。
- `Ctrl + \`` 呼出 Quick Paste。
- `↑ / ↓` 选择。
- `Enter` 快速粘贴。
- `Esc` 关闭。
- 首次配对时显示本机识别码。
- 通过服务器地址和对端识别码发起配对请求。
- 对端确认/拒绝配对；确认后在两端持久化配对关系。
- 登录 Windows 后自动启动（安装时可选，设置中可更改）。
- 收到已配对设备的文本或合规图片后自动更新系统剪贴板。
- 同一配对组内双向同步。
- 短间隔 HTTP 拉取和断线补偿。
- 剪贴板去重。
- 本地历史清理。
- 本地历史可按保留天数、条数和内容容量自动清理；默认保留 7 天、最多 300 条；未同步到 Server 的本地记录不会被自动删除。

#### Linux Server

- 配对请求、对端确认/拒绝、配对撤销和设备识别码管理。
- 设备授权凭证签发、轮换和撤销。
- 剪贴板同步 API。
- 最大 2 MiB 的图片上传/下载。
- PostgreSQL。
- 管理后台。
- 配对组数据隔离。
- 数据生命周期清理。
- 可按保留天数、每个配对组的条数和文本/图片内容容量自动清理历史与图片对象文件；默认保留 15 天、最多 500 条。

---

### 2.2 明确不实现

V1 不做：

- 文件剪贴板。
- 多文件复制。
- 文件传输。
- 收藏。
- Pin/置顶。
- 分组。
- 用户间共享。
- 团队剪贴板。
- SSO。
- LDAP。
- AD。
- 手机端。
- Web Clipboard。
- Redis。
- MQ。
- Elasticsearch。
- MinIO。
- 微服务。

如果 Codex 发现上述功能缺失，**不得主动补充**。

---

# 3. 技术栈

## 3.1 Windows Client

```text
.NET 10
WPF
Win32 P/Invoke
SQLite
```

核心 Win32 能力：

```text
AddClipboardFormatListener
WM_CLIPBOARDUPDATE
RegisterHotKey
GetForegroundWindow
SetForegroundWindow
SendInput
Windows Clipboard API
System Tray
```

客户端固定使用 WPF，不切换：

```text
Electron
Tauri
Avalonia
WinUI
```

除非后续明确修改架构。

---

## 3.2 Linux Server

```text
Go
Gin
PostgreSQL
HTTP/JSON REST
Per-device credentials
Linux Filesystem
Docker Compose
```

V1 不引入额外基础设施。

---

# 4. 总体架构

```text
┌──────────────────────────┐
│ Windows Client A         │
│                          │
│ Clipboard Monitor        │
│ Local SQLite             │
│ Quick Paste              │
│ Sync Client              │
└────────────┬─────────────┘
             │
             │ HTTPS / REST
             ▼
┌──────────────────────────┐
│ Linux ClipBridge Server  │
│                          │
│ Pairing / Device Auth    │
│ Device                   │
│ Clipboard API            │
│ Image API                │
│ Pair Group Authorization │
│                          │
│ PostgreSQL               │
│ Object Storage           │
└────────────┬─────────────┘
             │
             │ HTTPS / REST
             ▼
┌──────────────────────────┐
│ Windows Client B         │
│                          │
│ paired device            │
│ Local SQLite             │
│ Quick Paste              │
└──────────────────────────┘
```

---

# 5. 配对组与数据隔离模型

关系：

```text
Owner / Pair Group
 ├── Device A (confirmed)
 ├── Device B (confirmed)
 └── Clipboard Items
```

示例：

```text
张三的配对组
├── 本地主机
└── 云桌面

李四的配对组
├── 本地主机
└── 云桌面
```

规则：

```text
设备只可读取其已确认配对组的数据。
不同配对组之间严格隔离。
```

任何普通用户 API：

```text
owner_id / pair_group_id
```

必须来自：

```text
Per-device credential
   ↓
Device Auth Middleware
   ↓
Current Owner Context
```

禁止客户端指定资源归属：

```text
GET /clips?ownerId=1001
```

---

# 6. Windows Client 分层

```text
src/client/

ClipBridge.App/
  App.xaml
  LoginWindow
  QuickPasteWindow
  SettingsWindow
  DeviceWindow
  Tray

ClipBridge.Core/
  Models/
  Interfaces/
  Services/
  Common/

ClipBridge.Windows/
  ClipboardMonitor
  ClipboardReader
  ClipboardWriter
  HotkeyManager
  PasteManager
  ForegroundWindowManager
  TrayManager

ClipBridge.Storage/
  SQLite
  ClipboardRepository
  SettingsRepository
  Migration

ClipBridge.Sync/
  ApiClient
  PairingClient
  DeviceCredentialStore
  SyncClient
  RetryPolicy
  SyncCursorStore

ClipBridge.Tests/
```

原则：

- `Core` 不直接依赖 WPF。
- `Windows` 只放 Windows OS 相关逻辑。
- `Storage` 只关心持久化。
- `Sync` 只关心网络、认证和同步。
- `App` 负责 UI、组合和用户交互。

---

# 7. Clipboard 设计

## 7.1 监听机制

必须使用：

```text
AddClipboardFormatListener
```

接收：

```text
WM_CLIPBOARDUPDATE
```

禁止使用定时轮询替代。

---

## 7.2 V1 类型

只支持：

```text
TEXT
IMAGE
```

暂不支持：

```text
FILE
CF_HDROP
RTF
复杂 HTML
自定义 Clipboard Format
```

---

## 7.3 Clipboard 流程

```text
WM_CLIPBOARDUPDATE
        │
        ▼
ClipboardReader
        │
        ├── TEXT
        └── IMAGE
        │
        ▼
Normalize
        │
        ▼
SHA-256
        │
        ▼
Deduplicate
        │
        ▼
SQLite
        │
        ▼
Sync Queue
```

---

# 8. 图片设计

统一转为：

```text
PNG
```

本地：

```text
%LOCALAPPDATA%\ClipBridge\
├── clipbridge.db
├── objects\
│   └── {sha256}.png
└── logs\
```

禁止把大图保存到 SQLite BLOB。

数据库只保存：

```text
image_path
content_hash
```

---

# 9. 本地数据模型

## clipboard_items

```sql
CREATE TABLE clipboard_items (
    id TEXT PRIMARY KEY,
    origin_device_id TEXT NOT NULL,
    type TEXT NOT NULL,
    text_content TEXT,
    image_path TEXT,
    content_hash TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    synced INTEGER NOT NULL DEFAULT 0,
    remote INTEGER NOT NULL DEFAULT 0
);
```

## settings

```sql
CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT
);
```

默认：

```text
history_limit = 1000
```

---

# 10. Quick Paste

默认快捷键：

```text
Ctrl + `
```

Win32：

```text
RegisterHotKey
CTRL + VK_OEM_3
```

流程：

```text
用户当前应用
   │
   │ Ctrl + `
   ▼
GetForegroundWindow
   │
   ▼
保存 HWND
   │
   ▼
显示 QuickPasteWindow
   │
   ▼
搜索 / ↑ / ↓
   │
   │ Enter
   ▼
ClipboardWriter
   │
   ▼
隐藏 QuickPasteWindow
   │
   ▼
恢复 HWND
   │
   ▼
SendInput Ctrl+V
```

---

# 11. 同步回环策略

已配对设备收到远端内容后：

```text
先校验来源、类型、大小和版本
  ↓
写入本地历史
  ↓
在未检测到较新本机复制时更新系统剪贴板
```

同步回环保护：

```text
记录来源设备、内容 ID 和本地程序写入标记
  ↓
监听到自身写入时不重新上传
```

本机复制与远端到达并发时，以接收端最近一次真实用户复制为准。自动写入失败不删除历史项、不停止监听、不停止网络同步；仅通过托盘状态提示并按退避策略重试。

自动同步剪贴板默认启用，可在设置中关闭；关闭后远端内容仅写入历史。用户手工选择历史记录粘贴仍使用独立的快速粘贴流程。

---

# 12. 配对与设备识别码

每个安装实例首次根据本机可用网卡的 MAC 地址派生一个 6 位大写字母/数字识别码，并立即持久化；启动时不重新生成。原始 MAC 不发送给 Server，也不得用作身份凭证。6 位码可能冲突，Server 必须检查唯一性并在冲突时要求重新生成。识别码可修改，修改后旧码失效并要求重新配对。

配对流程：

```text
设备 A 输入 Server 地址 + 设备 B 识别码
  ↓
Server 创建限时 pending 请求
  ↓
设备 B 收到托盘/应用确认提示
  ├── 拒绝/超时：不建立关系
  └── 确认：签发按设备授权的凭证
               ↓
            两端持久化配对
```

配对请求至少包含请求设备名、识别码、来源地址和有效期。需提供请求频率限制、拒绝后冷却、重复请求幂等、设备解绑和凭证撤销。识别码本身不具有长期授权能力。

双方任何一端都可以输入另一端识别码发起请求；接受请求的一端负责明确授权。配对完成后关系为双向同步。

---

# 13. HTTP 同步与恢复

所有同步步骤都通过有超时、可重试的独立 HTTP 请求完成：

```text
POST /pairing-requests
GET  /pairing-requests/pending
POST /pairing-requests/{id}/decision
POST /clips
GET  /clips?after=<cursor>&limit=100
POST /images
GET  /images/{id}
```

客户端维持两条互补路径：

1. **HTTP 游标拉取**是权威数据路径。客户端使用文本、图片各自已有的持久化 cursor 拉取记录，完成去重、保存和自动写入系统剪贴板；待确认配对请求仍按现有机制轮询。
2. **SSE 通知**仅发送“设备有新剪贴板内容”的轻量提示，不承载剪贴板正文、图片、游标状态或长期凭证，也不替代现有 REST API。服务端在内容成功持久化后通知目标配对设备；重复通知安全，客户端收到通知后立即触发现有游标拉取。
3. **4 秒轮询兜底**保持启用。SSE 建连、断线重连及重连完成后立即执行游标补拉；通知丢失、连接被代理/网络中断或服务重启时，轮询仍能收敛到完整状态。

客户端应先建立 SSE 订阅，再执行一次游标补拉，避免在“先补拉、后订阅”的间隙漏掉新内容。事件处理可合并短时间内的重复唤醒，并复用现有拉取锁与游标去重；SSE 回调不得并行绕过拉取锁，也不得阻塞剪贴板监听。认证失效按现有 HTTP 重新认证路径处理并重连；网络错误采用有上限的指数退避和抖动，同时保留 4 秒兜底。服务端应发送定期注释心跳，并限制单条流的存活时间，以便客户端周期性重新认证；请求取消或连接到期时释放订阅资源。

该设计只要求**通知连接**保持开放，不依赖它传输或确认数据；重启、断线和多次投递不会改变数据正确性。首期服务为单实例部署，通知通过进程内设备级订阅分发；若以后横向扩展 Server，需在扩容前引入跨实例通知总线，或明确保证同一配对组请求始终路由到同一实例。

---

# 14. 安装与启动

- Windows 安装包支持选择安装目录、升级和卸载。
- 安装器允许设置开机启动；默认启用但可取消，之后可在设置中更改。
- 开机启动以托盘后台方式运行，不创建额外控制台窗口。
- 历史数据库、图片对象和配对凭证保存在用户数据目录；升级或卸载不意外删除用户数据。

---

# 15. Server 结构

```text
server/

cmd/
└── server/

internal/
├── device/
├── pairing/
├── credential/
├── clipboard/
├── image/
├── sync/
├── admin/
└── storage/

migrations/

web/
└── admin/

Dockerfile
docker-compose.yml
```

---

# 16. Server 数据模型

配对识别码是设备发现标识，不代表数据归属。Server 使用内部 owner/pair-group 作为数据隔离边界，客户端不能自行指定 owner。

```text
Pair Group
├── Device A
├── Device B
└── Clipboard Items
```

同组设备可以同步；不同组设备不可读取彼此数据。配对待确认记录与已建立关系分开保存，只有确认后才创建有效关系并签发设备凭证。新配对流程以配对组和设备凭证为身份模型，不要求普通用户创建或输入账号。当前数据库中的 `users` 表以及旧账号登录 API 是兼容遗留实现；设备凭证请求的 owner 由 Server 根据设备所属配对组解析，不能由客户端提交。

## users（兼容遗留表）

该表供旧账号登录/迁移路径兼容，不是新设备配对流程的身份来源，也不应成为新功能的数据隔离边界。

```text
id
username
password_hash
display_name
role
status
created_at
last_login_at
```

角色：

```text
USER
ADMIN
```

状态：

```text
ACTIVE
DISABLED
```

---

## devices

```text
id
owner_id
device_code_hash
device_name
platform
credential_hash
status
last_seen_at
created_at
```

## pairing_requests

```text
id
requester_device_id
target_device_id
status                 -- pending / accepted / rejected / expired
expires_at
decided_at
created_at
```

## device_pairings

```text
id
owner_id
device_a_id
device_b_id
created_at
revoked_at
```

---

## clipboard_items

```text
id
owner_id
origin_device_id
type
text_content
image_object_key
content_hash
created_at
expires_at
```

必须由服务端认证上下文确定归属：

```text
owner_id
```

服务端持久化设备凭证的 Hash，不保存可直接重放的明文长期密钥。设备解绑或重置时撤销相关凭证。

---

# 17. REST API

新安装使用待确认配对和设备凭证；客户端同步不要求用户账号。旧版账号登录接口和管理员引导账号仅为兼容遗留路径，不是新设备配对的前置步骤。

```http
POST /api/v1/pairing-requests
GET  /api/v1/pairing-requests/pending
POST /api/v1/pairing-requests/{id}/decision
```

## Device

```http
GET    /api/v1/devices/me
PATCH  /api/v1/devices/me/code
DELETE /api/v1/devices/{id}/pairing
```

## Clipboard

```http
POST   /api/v1/clips
GET    /api/v1/clips?after=<cursor>&limit=100
DELETE /api/v1/clips/{id}
```

## Image

```http
POST /api/v1/images
GET  /api/v1/images/{id}
```

图片上传使用原始 PNG/JPEG 请求体：

```http
POST /api/v1/images
Authorization: Bearer <device credential>
X-Clip-ID: <clipboard item id>
Content-Type: image/png
```

最多接受 2 MiB 原始图片内容；Server 在读取请求体时限制大小，验证真实图片格式后才落盘。上传对象存储在 `CLIPBRIDGE_IMAGE_DIR` 指定的目录，Compose 部署将其挂载到持久卷。重复 ID 与相同来源、格式、内容哈希会刷新时间并返回已有记录；同 ID 对应不同内容返回冲突。

# 18. 配对设备断线补拉

客户端维护：

```text
last_sync_cursor
```

恢复流程：

```text
网络恢复
  ↓
重连 SSE 或恢复 HTTP 轮询
  ↓
发送端重试本地未确认文本
  ↓
GET /clips?after=cursor
  ↓
补齐缺失
  ↓
保存 cursor
  ↓
补拉图片游标并继续 HTTP 轮询
```

设备凭证被撤销/失效时停止授权请求，提示重新登记或重新配对；这与可重试的网络断线分开处理。

要求：

- Server 重启不永久丢数据。
- Client 休眠不永久丢数据。
- 代理断开不永久丢数据。
- HTTP 请求超时、Server 重启或设备休眠不永久丢数据。
- 本机复制成功后若网络上传失败，待发送项必须持久化；网络恢复后自动重试，服务端幂等去重。

---

# 19. 去重

文本：

```text
SHA256(TEXT + normalized_text)
```

图片：

```text
SHA256(IMAGE + png_bytes)
```

同设备短时间重复复制相同内容：

```text
不重复插入
```

Server：

```text
clip.id
```

必须幂等。

---

# 20. 安全边界

必须实现：

- HTTPS。
- 配对请求限流和短有效期。
- 设备凭证签发、轮换和撤销。
- 设备授权检查及配对关系隔离。
- 设备识别码唯一性校验；识别码不作为长期密钥。
- 用户数据隔离。
- 图片访问鉴权。
- 请求体大小限制。
- 图片格式验证。
- 防目录穿越。
- 配对请求限流。
- 日志脱敏。

日志禁止记录：

```text
password
access_token
refresh_token
clipboard text
clipboard image
```

---

# 21. Ditto 参考原则

参考：

```text
https://github.com/sabrogden/Ditto
```

Ditto 仅用于：

- 产品行为参考。
- Quick Paste 交互参考。
- Win32 Clipboard 边界处理参考。
- Clipboard 被占用时的处理思路。
- 快捷键/焦点恢复参考。

开发原则：

1. 优先依据 Microsoft Win32 官方 API 自主实现。
2. 不以 Ditto 源码作为 ClipBridge 代码基础。
3. 不复制 Ditto 源码。
4. 不照搬 Ditto 内部架构。
5. 不导入 Ditto 数据库设计。
6. 不因为 Ditto 有某功能就扩大 V1 范围。
7. 遇到 Win32 边界行为时可阅读 Ditto 对应实现验证思路，再独立实现。

---

# 22. 架构变更规则

已由用户确认的 2026-10 产品方向（设备确认配对、远端自动更新系统剪贴板、短间隔 HTTP、2 MiB 图片上限和可选开机启动）视为当前授权架构，不再受旧版“远程不自动覆盖 Clipboard”条款限制。

后续实现不得自行扩大以下边界：

- 技术栈。
- 核心模块边界。
- 设备授权、配对确认和撤销要求。
- 2 MiB 图片传输上限。
- 仅同步剪贴板文本与图片，不扩展为文件传输。

如果发现架构确实无法完成当前 MVP：

1. 停止扩大实现。
2. 在输出中说明问题。
3. 给出最小架构变更建议。
4. 等待确认后再修改。
