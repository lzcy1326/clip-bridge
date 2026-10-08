# ClipBridge Linux 服务部署指南

## 部署包内容

- `docker-compose.yml`：ClipBridge Server 与 PostgreSQL 配置。
- `.env.example`：可选环境变量模板，用于修改端口和历史保留规则。
- 首次启动自动生成数据库密码和 JWT 密钥，并保存在 `clipbridge_secrets` Docker 卷中；发布包不包含共享密钥。
- `images.tar`：Linux/AMD64 的 ClipBridge Server 1.0.0 与 PostgreSQL 18.6 镜像，可离线导入。

## 环境要求

- Linux x86_64（AMD64）。本部署镜像不适用于 ARM/树莓派服务器。
- Docker Engine 及 Docker Compose v2 插件。
- 建议至少 2 核 CPU、2 GB 内存；磁盘空间按剪贴板历史和截图增长预留。
- 局域网直连默认使用 TCP 10002。公网部署请配置 HTTPS 反向代理，并避免将明文 HTTP 端口暴露到公网。

## 1. 将包复制到 Linux 并解压

把 `ClipBridge-Linux-Server-Deploy.zip` 上传到服务器，例如放在 `/opt`：

```bash
sudo mkdir -p /opt/clipbridge
sudo chown "$USER":"$USER" /opt/clipbridge
cd /opt/clipbridge
unzip /path/to/ClipBridge-Linux-Server-Deploy.zip
```

如果压缩包解出一个同名子目录，可进入该目录再继续。确认当前目录内有 `images.tar`、`docker-compose.yml` 和 `.env.example`。

确认 Docker 和 Compose 可用：

```bash
docker --version
docker compose version
```

## 2. 导入镜像并启动

```bash
docker load -i images.tar
docker compose up -d
```

发布包不附带任何可复用的凭据。`docker compose up -d` 会先在 Docker 持久化卷中创建随机数据库密码和 JWT 密钥，再启动服务；无需手工创建 `.env`。密钥仅在首次初始化时生成，容器重启和升级时会沿用。请备份 `clipbridge_secrets` 卷；删除该卷会丢失密钥。`docker load` 仅在新服务器首次导入镜像时执行；之后启动或重启只需运行 `docker compose up -d`。

历史清理默认保留 15 天、每个配对组最多 500 条。需要自定义时，先复制模板为 `.env`，再修改天数、条数或监听端口；单项设置为 `0` 表示不限。也可选配内容容量上限（字节）：

```dotenv
CLIPBRIDGE_SERVER_PORT=10002
CLIPBRIDGE_HISTORY_RETENTION_DAYS=15
CLIPBRIDGE_HISTORY_MAX_ITEMS=500
CLIPBRIDGE_HISTORY_MAX_BYTES=0
```

```bash
cp .env.example .env
```

时间限制按记录创建时间计算；条数和可选容量限制均按每个配对组聚合，超过限制时优先删除较旧记录。Server 启动时执行一次清理，之后每 24 小时执行一次。删除的历史会从配对组内所有设备移除；图片对象文件会同步删除。PostgreSQL 会回收已删除行占用的表空间供后续复用，但默认不会执行会锁表并重写数据库文件的 `VACUUM FULL`。修改 `.env` 后运行 `docker compose up -d` 使配置生效。

直接 HTTP 只适合可信的隔离局域网。公网或不可信网络部署时，应通过 HTTPS 反向代理并限制访问范围。首次启动时生成的 JWT 密钥保存在 Docker 卷中；更换该密钥会使旧版登录令牌失效。设备码配对同步不要求账号登录。

## 3. 检查服务

```bash
docker compose ps
```

服务首次启动时会自动创建数据库表。检查服务健康状态：

```bash
curl --fail http://127.0.0.1:10002/health
```

预期响应：`{"status":"ok"}`。查看日志：

```bash
docker compose logs --tail=100 server postgres
```

若服务器启用了 UFW，且客户端要从其他设备连接，可按需开放端口：

```bash
sudo ufw allow 10002/tcp
```

请在客户端同步设置中填入可从客户端访问的地址，例如 `http://服务器局域网IP:10002`。不同网络之间必须有路由可达且防火墙放行该端口。

## 4. 客户端身份

新设备通过设备识别码发起配对，并由对端确认后使用设备凭证同步；正常剪贴板同步不需要用户账号。Server 保留旧账号登录代码用于兼容，但默认不会创建管理员账号。不要在公网部署中启用旧版管理员引导。

## 5. HTTPS 与公网部署

公网或不可信网络部署时，应通过 Caddy、Nginx 等反向代理提供 HTTPS，并将 `.env` 中的 `CLIPBRIDGE_SERVER_BIND` 改为 `127.0.0.1`，只让代理访问本机的 10002 端口。客户端填写 `https://你的域名`。反向代理需支持长连接/流式响应，SSE 路径 `/api/v1/events` 不要缓冲响应。

不要将 PostgreSQL 的 5432 端口映射到公网；本 Compose 配置没有暴露该端口。

## 6. 日常管理

```bash
# 状态
docker compose ps

# 实时日志
docker compose logs -f server

# 停止但保留数据
docker compose down

# 再次启动
docker compose up -d
```

### 数据备份

备份 PostgreSQL 元数据、设备配对关系和历史记录：

```bash
mkdir -p backups
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB"' > "backups/clipbridge-$(date +%F-%H%M%S).sql"
```

图片对象存放在 `clipbridge_images` Docker 卷。升级或迁移服务器时，除 SQL 备份外还要备份该卷。可先停服，再打包两个卷：

```bash
docker compose down
# 卷名由 Compose 项目名加后缀构成，可用 docker volume ls 查看。
PROJECT="$(basename "$PWD")"
docker run --rm -v "${PROJECT}_postgres_data:/data" -v "$PWD/backups:/backup" postgres:18.6-alpine3.24 tar czf /backup/postgres-data.tgz -C /data .
docker run --rm -v "${PROJECT}_clipbridge_images:/data" -v "$PWD/backups:/backup" postgres:18.6-alpine3.24 tar czf /backup/clipbridge-images.tgz -C /data .
docker run --rm -v "${PROJECT}_clipbridge_secrets:/data" -v "$PWD/backups:/backup" postgres:18.6-alpine3.24 tar czf /backup/clipbridge-secrets.tgz -C /data .
```

恢复前先停止服务并备份现有数据；恢复卷前请确认目标卷名和数据来源，避免覆盖新数据。

## 7. 升级与卸载

新版部署包中的 `images.tar` 导入后，执行：

```bash
docker load -i images.tar
docker compose up -d
```

卸载容器但保留数据库、图片及密钥：

```bash
docker compose down
```

**删除全部数据是不可逆的**。只有确定不再需要历史、设备配对关系及图片时，才执行：

```bash
docker compose down -v
```

## 故障检查

- `/health` 无响应：先看 `docker compose ps` 和 `docker compose logs --tail=200 server postgres`。
- 服务启动时报数据库连接失败：确认 PostgreSQL 已 `healthy`，且 `.env` 用户名、密码只含十六进制字符并一致。
- 客户端访问超时：检查服务端地址、TCP 10002 防火墙、VirtualBox/路由转发；客户端不能使用 `localhost` 指向 Linux 服务器。
- SSE 通知未及时到达但轮询仍可用：反向代理需要关闭 SSE 缓冲并允许长连接；当前客户端仍有 4 秒 HTTP 轮询兜底。
