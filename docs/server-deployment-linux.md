# ClipBridge Linux 服务部署指南

## 部署包内容

- `docker-compose.yml`：ClipBridge Server 与 PostgreSQL 配置。
- `.env`：已预置随机数据库密码、JWT 密钥及默认管理员密码，解压后不需要手动生成或填写。
- `.env.example`：环境变量模板，供需要自行更换配置时参考。
- `images.tar`：Linux/AMD64 的 ClipBridge Server 1.0.0 与 PostgreSQL 18.6 镜像，可离线导入。

## 环境要求

- Linux x86_64（AMD64）。本部署镜像不适用于 ARM/树莓派服务器。
- Docker Engine 及 Docker Compose v2 插件。
- 建议至少 2 核 CPU、2 GB 内存；磁盘空间按剪贴板历史和截图增长预留。
- 局域网直连默认使用 TCP 18080。公网部署请配置 HTTPS 反向代理，并避免将明文 HTTP 端口暴露到公网。

## 1. 将包复制到 Linux 并解压

把 `ClipBridge-Linux-Server-Deploy.zip` 上传到服务器，例如放在 `/opt`：

```bash
sudo mkdir -p /opt/clipbridge
sudo chown "$USER":"$USER" /opt/clipbridge
cd /opt/clipbridge
unzip /path/to/ClipBridge-Linux-Server-Deploy.zip
```

如果压缩包解出一个同名子目录，可进入该目录再继续。确认当前目录内有 `images.tar` 和 `docker-compose.yml`。

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

部署包已附 `.env`，包含随机生成的本地数据库密码和 JWT 密钥，因此默认启动无需手动创建或编辑配置文件。`docker load` 仅在新服务器首次导入镜像时执行；之后启动或重启只需运行 `docker compose up -d`。

此配置用于 ClipBridge 自己的可信局域网测试。`.env` 内的凭据随部署包提供，拿到部署包的人可以读取它们；不要将服务直接暴露到公网。若改为公网或多人共享部署，请先自行更换 `.env` 中的密码和 JWT 密钥，保护该文件（例如 `chmod 600 .env`），并配置 HTTPS 反向代理。更换 JWT 密钥会使所有客户端现有登录令牌失效，需要重新登录。

## 3. 检查服务

```bash
docker compose ps
```

服务首次启动时会自动创建数据库表。检查服务健康状态：

```bash
curl --fail http://127.0.0.1:18080/health
```

预期响应：`{"status":"ok"}`。查看日志：

```bash
docker compose logs --tail=100 server postgres
```

若服务器启用了 UFW，且客户端要从其他设备连接，可按需开放端口：

```bash
sudo ufw allow 18080/tcp
```

请在客户端同步设置中填入可从客户端访问的地址，例如 `http://服务器局域网IP:18080`。不同网络之间必须有路由可达且防火墙放行该端口。

## 4. 登录默认管理员

首次启动时服务会自动创建默认管理员账号：

```text
用户名：admin
密码：admin
角色：ADMIN
```

服务重启时不会重置已有 `admin` 账号的密码或角色。首次启动后，在本地客户端使用此账号登录，并按客户端的设备配对流程完成两端配对。默认密码仅适用于可信局域网测试；不要将服务或凭据暴露给不可信网络。

## 5. HTTPS 与公网部署

直接 HTTP 只适合可信的隔离局域网。公网或不可信网络部署时，应通过 Caddy、Nginx 等反向代理提供 HTTPS，并将 `.env` 中的 `CLIPBRIDGE_SERVER_BIND` 改为 `127.0.0.1`，只让代理访问本机的 18080 端口。客户端填写 `https://你的域名`。反向代理需支持长连接/流式响应，SSE 路径 `/api/v1/events` 不要缓冲响应。

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

备份 PostgreSQL 元数据和账号、历史记录：

```bash
mkdir -p backups
docker compose exec -T postgres pg_dump -U "$(grep '^POSTGRES_USER=' .env | cut -d= -f2-)" -d "$(grep '^POSTGRES_DB=' .env | cut -d= -f2-)" > "backups/clipbridge-$(date +%F-%H%M%S).sql"
```

图片对象存放在 `clipbridge_images` Docker 卷。升级或迁移服务器时，除 SQL 备份外还要备份该卷。可先停服，再打包两个卷：

```bash
docker compose down
# 卷名由 Compose 项目名加后缀构成，可用 docker volume ls 查看。
PROJECT="$(basename "$PWD")"
docker run --rm -v "${PROJECT}_postgres_data:/data" -v "$PWD/backups:/backup" alpine tar czf /backup/postgres-data.tgz -C /data .
docker run --rm -v "${PROJECT}_clipbridge_images:/data" -v "$PWD/backups:/backup" alpine tar czf /backup/clipbridge-images.tgz -C /data .
```

恢复前先停止服务并备份现有数据；恢复卷前请确认目标卷名和数据来源，避免覆盖新数据。

## 7. 升级与卸载

新版部署包中的 `images.tar` 导入后，执行：

```bash
docker load -i images.tar
docker compose up -d
```

卸载容器但保留数据库和图片：

```bash
docker compose down
```

**删除全部数据是不可逆的**。只有确定不再需要历史、配对账号及图片时，才执行：

```bash
docker compose down -v
```

## 故障检查

- `/health` 无响应：先看 `docker compose ps` 和 `docker compose logs --tail=200 server postgres`。
- 服务启动时报数据库连接失败：确认 PostgreSQL 已 `healthy`，且 `.env` 用户名、密码只含十六进制字符并一致。
- 客户端访问超时：检查服务端地址、TCP 18080 防火墙、VirtualBox/路由转发；客户端不能使用 `localhost` 指向 Linux 服务器。
- SSE 通知未及时到达但轮询仍可用：反向代理需要关闭 SSE 缓冲并允许长连接；当前客户端仍有 4 秒 HTTP 轮询兜底。
