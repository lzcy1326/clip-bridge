# 01-architecture.md

# ClipBridge 架构设计

> 项目：ClipBridge  
> 版本：v1.0  
> 目标：为 Windows 本地主机与云桌面提供轻量级、账号隔离的跨设备剪贴板能力。  
> 核心原则：**本地优先、同账号同步、远程内容不自动覆盖系统剪贴板、按 MVP 闭环逐步交付。**

---

## 1. 产品目标

ClipBridge 解决以下办公问题：

- 本地主机和云桌面无法直接复制粘贴。
- 两端都能通过网络/代理访问同一台 Linux Server。
- 每个员工有自己的本地主机和云桌面。
- 每个员工只能看到自己的剪贴板历史。
- 同一账号下的设备可以同步文本和图片。

用户典型链路：

```text
本地主机 Ctrl+C
   ↓
ClipBridge Client
   ↓
本地历史
   ↓
Linux Server
   ↓
云桌面 Client 历史
   ↓
Ctrl + `
   ↓
选择内容
   ↓
Enter
   ↓
粘贴到当前应用
```

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
- 登录/退出。
- 修改密码。
- 设备注册。
- 我的设备列表。
- 同账号跨设备同步。
- WebSocket 实时通知。
- 断线补偿。
- 剪贴板去重。
- 本地历史清理。

#### Linux Server

- 用户认证。
- 用户管理。
- 设备管理。
- 剪贴板同步 API。
- 图片上传/下载。
- WebSocket Hub。
- PostgreSQL。
- 管理后台。
- 数据隔离。
- 数据生命周期清理。

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
WebSocket
JWT
Argon2id
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
             │ HTTPS / WSS
             ▼
┌──────────────────────────┐
│ Linux ClipBridge Server  │
│                          │
│ Auth                     │
│ User                     │
│ Device                   │
│ Clipboard API            │
│ Image API                │
│ WebSocket Hub            │
│ Admin                    │
│                          │
│ PostgreSQL               │
│ Object Storage           │
└────────────┬─────────────┘
             │
             │ HTTPS / WSS
             ▼
┌──────────────────────────┐
│ Windows Client B         │
│                          │
│ same user                │
│ Local SQLite             │
│ Quick Paste              │
└──────────────────────────┘
```

---

# 5. 多用户模型

关系：

```text
User
 ├── Device
 ├── Device
 ├── Device
 └── Clipboard Items
```

示例：

```text
张三
├── 本地主机
└── 云桌面

李四
├── 本地主机
└── 云桌面
```

规则：

```text
张三只能看到张三的数据。
李四只能看到李四的数据。
```

任何普通用户 API：

```text
user_id
```

必须来自：

```text
Access Token
   ↓
Auth Middleware
   ↓
Current User Context
```

禁止客户端指定资源归属：

```text
GET /clips?userId=1001
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
  AuthClient
  SyncClient
  WebSocketClient
  ReconnectManager
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

远程同步内容：

```text
只进入本地历史
```

默认：

```text
不自动写 Windows Clipboard
```

因此远程同步不会自然触发回环。

当用户主动选择某条记录粘贴时：

```text
suppressNextClipboardUpdate = true
```

然后：

```text
ClipboardWriter
   ↓
WM_CLIPBOARDUPDATE
   ↓
发现 suppress
   ↓
IGNORE
```

---

# 12. Server 结构

```text
server/

cmd/
└── server/

internal/
├── auth/
├── user/
├── device/
├── clipboard/
├── image/
├── sync/
├── websocket/
├── admin/
└── storage/

migrations/

web/
└── admin/

Dockerfile
docker-compose.yml
```

---

# 13. Server 数据模型

## users

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
user_id
name
machine_name
platform
client_version
status
last_seen_at
created_at
```

---

## clipboard_items

```text
id
user_id
origin_device_id
type
text_content
image_object_key
content_hash
created_at
expires_at
```

必须直接保存：

```text
user_id
```

---

## refresh_tokens

```text
id
user_id
device_id
token_hash
expires_at
revoked_at
created_at
```

---

# 14. REST API

## Auth

```http
POST /api/v1/auth/login
POST /api/v1/auth/refresh
POST /api/v1/auth/logout
POST /api/v1/auth/change-password
```

## Device

```http
POST   /api/v1/devices
GET    /api/v1/devices
PATCH  /api/v1/devices/{id}
DELETE /api/v1/devices/{id}
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

## WebSocket

```text
GET /api/v1/ws
```

---

# 15. WebSocket

连接建立后绑定：

```text
user_id
device_id
```

Hub：

```text
User 1001
├── Device A
├── Device B
└── Device C
```

Device A 上传：

```text
broadcast:
user_id = 1001
exclude device = A
```

示例事件：

```json
{
  "event": "clipboard.created",
  "data": {
    "id": "01K...",
    "originDeviceId": "device-a",
    "type": "TEXT",
    "text": "hello",
    "contentHash": "sha256...",
    "createdAt": 1790658000000
  }
}
```

---

# 16. 断线补偿

WebSocket 只负责实时性，不是唯一事实来源。

客户端维护：

```text
last_sync_cursor
```

恢复流程：

```text
网络恢复
  ↓
Refresh Token
  ↓
GET /clips?after=cursor
  ↓
补齐缺失
  ↓
保存 cursor
  ↓
重新建立 WebSocket
```

要求：

- Server 重启不永久丢数据。
- Client 休眠不永久丢数据。
- 代理断开不永久丢数据。
- WS 重连不永久丢数据。

---

# 17. 去重

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

# 18. 安全边界

必须实现：

- HTTPS。
- WSS。
- Argon2id。
- Access Token。
- Refresh Token。
- Refresh Token 只保存 Hash。
- 用户禁用。
- Device 撤销。
- 用户数据隔离。
- 图片访问鉴权。
- 请求体大小限制。
- 图片格式验证。
- 防目录穿越。
- 登录限流。
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

# 19. Ditto 参考原则

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

# 20. 架构变更规则

Codex 不得自行修改：

- 技术栈。
- 核心模块边界。
- 用户隔离原则。
- 远程不自动覆盖 Clipboard 原则。
- V1 功能范围。

如果发现架构确实无法完成当前 MVP：

1. 停止扩大实现。
2. 在输出中说明问题。
3. 给出最小架构变更建议。
4. 等待确认后再修改。
