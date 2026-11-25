# Linux 原生部署（systemd + SQLite）

> 2026-11 起 Linux 只有这一种部署形态。k3s 清单与 PostgreSQL 已退役（历史实现见 git 历史）。

## 为什么换掉 k3s + PostgreSQL

单节点家庭服务器上，k3s 与 PostgreSQL 的启动/恢复过程会互相拖累：

- PostgreSQL 升级到 18 后需要较长的崩溃恢复，而 k8s 的 `livenessProbe` 在恢复期间就会判失败并重启容器；
  恢复被反复打断 → 永远起不来 → `bh-server` 的 Init 容器一直等数据库，整个栈不可用（实机表现为
  `bh-postgres` 0/1 重启几十次、`bh-server` 卡在 `Init:0/1`）。
- 家庭场景只需要"一个进程 + 一个数据目录"，不需要编排层：没有独立数据库进程就没有启动顺序问题，
  日志进 journald，备份就是打包一个目录。

因此 Linux 侧改为：**systemd 用户实例 + 单文件 SQLite**。Windows 侧的 wslc/native 形态保持不变
（后端同样使用 SQLite；其中的 PostgreSQL 容器/检查属于待清理的遗留项，见文末）。

## 形态概览

| 组件 | 实现 | 端口 |
|---|---|---|
| 后端 | `baihua-server@<user>.service` → `~/.baihua/bin/server/bh-server` | 8788 |
| WebUI | `baihua-webui@<user>.service` → `~/.baihua/bin/webui/bh-webui` | 5177 |
| 数据库 | `~/.baihua/db/baihua.db`（SQLite，WAL） | — |
| 局域网入口 | 系统 nginx（`bh proxy install`） | 80 |

数据目录（`BAIHUA_HOME`，默认 `~/.baihua`）：

```
~/.baihua/
├── bin/                # bh build 发布的产物（server/ webui/）
├── db/
│   ├── baihua.db       # 唯一数据库（+ -wal/-shm 运行期文件）
│   ├── .baihua-key     # 主加密密钥（AI API Key 等，必须随库一起备份）
│   └── vault-index-snapshots.json
├── config/
│   ├── server.env      # baihua-server 的环境变量（KEY=VALUE）
│   └── webui.env       # baihua-webui 的环境变量（可选）
├── logs/               # 预留目录（journald 之外，server 的 JSON 行日志实际写在 bin/server/logs/）
└── vaults/             # 知识库根目录（允许是指向已有目录的软链）
```

> **端口写在 appsettings 的 Kestrel 端点里**，`ASPNETCORE_URLS` 对已配置 Kestrel 端点的应用不生效
> （会打印 "Overriding address(es) ... Binding to endpoints defined via IConfiguration"）。
> 要改端口，在 `config/server.env` / `config/webui.env` 里设 `Kestrel__Endpoints__Http__Url=http://0.0.0.0:<port>`。

## 安装

```bash
# 仓库内
bh install              # 安装 CLI 定位器 + systemd 单元（enable，不启动）
bh build                # 发布到 ~/.baihua/bin（发布前会停对应服务）
bh start                # 启动 server → webui
bh status               # 状态 / HTTP 健康 / 数据库
bh doctor               # 体检：systemd / dotnet / 单元 / 端口 / 磁盘 / 数据库 / FTS5 / nginx

# 或一步到位
bh deploy               # build + install + start
```

一键安装（新机器）：

```bash
curl -fsSL https://raw.githubusercontent.com/luminsw/baihua/main/scripts/install-baihua.sh | bash
```

前置条件：Linux + systemd、普通用户 + sudo、.NET SDK 10（仅构建需要；运行只要 .NET 10 运行时）。
不需要 Docker / k3s / PostgreSQL。

### 常用命令

| 命令 | 作用 |
|---|---|
| `bh start\|stop\|restart [server\|webui]` | 控制服务（省略 = 全部） |
| `bh status [--json]` | 状态；`--json` 供 DSH 插件消费 |
| `bh logs <server\|webui> [n] [-f]` | journald 日志 |
| `bh build [server\|webui]` | 发布产物（会先停服务：Linux 不允许写运行中的可执行文件） |
| `bh update` | `git pull --ff-only` + build + restart |
| `bh backup [目录]` | 冷备份数据目录（要求服务已停止） |
| `bh restore <archive> [-y]` | 恢复（旧目录改名保留；恢复后需 `bh build`） |
| `bh proxy install` | 配置 nginx :80（含 `/api/auth/` → WebUI 的 CLI token 路由） |
| `bh dashboard` | 取 CLI token 并输出 WebUI 地址 |
| `bh db-migrate [--rewrite-prefix OLD=NEW]` | PostgreSQL → SQLite 一次性迁移 |
| `bh doctor` | 体检 |

### 环境变量

| 变量 | 说明 |
|---|---|
| `BAIHUA_HOME` | 数据目录（默认 `~/.baihua`；`bh install` 会把它渲染进 systemd 单元） |
| `BAIHUA_DATABASE_PATH` | 覆盖数据库文件路径 |
| `BAIHUA_REPO` | 仓库根（定位器用；不再用 `BAIHUA_HOME` 找仓库） |
| `BAIHUA_PUBLISH_SELF_CONTAINED=1` | 发布自包含产物（目标机无需 .NET 运行时） |
| `BAIHUA_MIGRATION_POSTGRES_CONNECTION` | 迁移工具读的来源库连接串 |
| `BAIHUA_MIGRATION_SKIP_TABLES` | 迁移时显式跳过的已退役表（逗号分隔） |

## 从 k3s + PostgreSQL 迁移（实机验证过的步骤）

> 原则：**PostgreSQL 源库只读、不修改**；SQLite 目标必须不存在（工具拒绝覆盖）。
> 迁移工具会逐表复制并做内容摘要校验（行数 + 规范化值的异或/求和摘要），失败即中止且不落盘。

```bash
# 0) 停掉 k3s 里的百花工作负载，避免 8788/5177 冲突（数据卷保留）
sudo kubectl -n baihua scale deploy bh-server --replicas=0
sudo kubectl -n baihua scale deploy bh-webui  --replicas=0

# 1) 安装 CLI 与 systemd 单元（先不启动）
bh install

# 2) 复用旧数据目录里的密钥与配置（关键：.baihua-key 丢了，库里的 AI API Key 就解不开）
mkdir -p ~/.baihua/db ~/.baihua/config ~/.baihua/logs
sudo cp /opt/baihua/data/db/.baihua-key ~/.baihua/db/
sudo cp /opt/baihua/data/db/vault-index-snapshots.json \
        /opt/baihua/data/db/prompt-templates.json ~/.baihua/db/
sudo cp /opt/baihua/data/webui.settings.json ~/.baihua/
sudo chown -R "$USER:$USER" ~/.baihua
chmod 0600 ~/.baihua/db/.baihua-key

# 3) 知识库：旧容器把宿主 /home/lumin/Vaults 挂在 /app/data/vaults
ln -s /home/lumin/Vaults ~/.baihua/vaults

# 4) 准备一个可读的 PostgreSQL 源（二选一）
#    a. 修好 k3s 里的 PG（给它 startupProbe，别在恢复期杀它），然后 kubectl port-forward；
#    b. 复制数据目录，用一次性容器读（不动线上数据；适合 PG 已经在崩溃循环时）：
sudo kubectl -n baihua scale deploy bh-postgres --replicas=0
sudo cp -a /opt/baihua/postgres/18/docker /tmp/pg18live
sudo chown -R 70:70 /tmp/pg18live
sudo kubectl -n baihua scale deploy bh-postgres --replicas=1
docker run -d --name bh-pg-read -e PGDATA=/var/lib/postgresql \
  -p 127.0.0.1:15433:5432 -v /tmp/pg18live:/var/lib/postgresql postgres:18-alpine

# 5) 迁移：把数据库里的容器路径重写成原生路径
export BAIHUA_MIGRATION_POSTGRES_CONNECTION='Host=127.0.0.1;Port=15433;Database=baihua;Username=baihua;Password=<旧 PG 口令>'
bh db-migrate --rewrite-prefix /app/data/vaults=$HOME/.baihua/vaults

# 6) 发布 + 启动 + 验证
bh build
bh start
bh status
bh doctor

# 关键验证（密钥迁移是否成功）
curl -s http://127.0.0.1:8788/api/ai/config/providers | head -c 300   # hasApiKey 应为 true
```

迁移工具会打印每张表的行数与 `data digest verified`，最后输出 `[done] N tables, M rows copied and verified`。
`VaultNoteFts` 是派生索引，不迁移；服务启动后由索引任务按磁盘内容重建。

### 暂时保留 k3s（只跑 OVMS / Open WebUI）

如果还不想整机卸载 k3s（例如想继续用 `bh-openvino` 和 Open WebUI），可以只退役百花自己的工作负载：

```bash
# 1) 停掉被原生服务替换的工作负载（数据/PVC 保留）
sudo kubectl -n baihua scale deploy bh-server --replicas=0
sudo kubectl -n baihua scale deploy bh-webui  --replicas=0
sudo kubectl -n baihua scale deploy bh-postgres --replicas=0

# 2) 释放宿主 :80：Traefik 不再需要对外暴露（改为 ClusterIP，svclb 随之移除）
sudo kubectl -n kube-system patch svc traefik --type=merge -p '{"spec":{"type":"ClusterIP"}}'

# 3) 把 OVMS 暴露到宿主 :8000，原生后端继续用本地推理
sudo kubectl -n baihua patch svc bh-openvino --type=merge -p '{"spec":{"type":"LoadBalancer"}}'
```

之后 `bh proxy install` 用系统 nginx 接管 :80；Open WebUI 自己的 LoadBalancer 仍占 8080。
验证：`curl -s http://127.0.0.1:8000/v1/models`、`curl -I http://<lan-ip>/`。

### 迁移工具细节

- 时间统一转成 EF Core SQLite 的文本格式 `yyyy-MM-dd HH:mm:ss.FFFFFFF`（UTC），避免 `T` 与空格两种分隔符混用破坏排序；
- `decimal` 用 EF 的 `0.0###########################` 格式，`Guid` 大写，`bool` = 1/0；
- 源库多出来的旧列会以 `ALTER TABLE ... ADD COLUMN ... BLOB` 保留（打印 `[preserve]`）；
- 目标模型有而源库没有的 NOT NULL 列（且无默认值）会直接报错，避免静默丢数据；
- `--rewrite-prefix` 只作用于列名以 `Path` 结尾的列（`Vaults.Path`、`NoteEmbeddings.NotePath` 等），不动正文与 JSON。

## 备份与恢复

SQLite 是单文件，冷备份 = 打包数据目录：

```bash
bh stop
bh backup ~/backup        # 产物 ~/backup/baihua-backup-<时间>.tar.gz（排除可重建的 bin/）
bh start

# 恢复
bh stop
bh restore ~/backup/baihua-backup-20261125-090000.tar.gz
bh build && bh start
```

- 备份包含 `db/baihua.db`（含 `-wal/-shm`）、`db/.baihua-key`、`config/*.env`、`vaults/`、`logs/`；
- 若知识库根目录被配置成 `~/.baihua/vaults` 之外的路径（软链），实际文件不在包里，需要单独备份；
- 运行中不要用 `cp` 复制 `.db`（WAL 可能不一致）；服务运行中需要在线备份时用 `sqlite3 baihua.db ".backup out.db"`
  （本机未装 sqlite3 时：`bh stop` 后再 `bh backup`）。

## 排障

| 现象 | 检查 |
|---|---|
| 服务起不来 | `bh logs server 200` / `journalctl -u baihua-server@$USER.service -n 200` |
| 端口被占 | `ss -ltnp \| grep -E ':8788\|:5177'`（k3s 残留工作负载先 scale 到 0） |
| 数据库打不开 / 权限 | `~/.baihua/db` 必须属于服务用户；unit 用 `UMask=0077`，文件应为 `0600` |
| `database is locked` | 只应有一个 server 实例；WAL + `busy_timeout=15s` 已由启动流程设置 |
| 知识库全部"路径不存在" | `~/.baihua/vaults` 软链指向的目录是否还在；迁移是否做了 `--rewrite-prefix` |
| 配对二维码地址不对 | `config/server.env` 的 `Baihua__PublicBaseUrl`（`bh proxy install` 会自动写） |
| 局域网 :80 打不开 | `nginx -t`、`/etc/nginx/sites-enabled/baihua.conf`、`/api/auth/` 是否转发到 5177 |
| AI API Key 失效 | `db/.baihua-key` 是否随库迁移；`/api/ai/config/providers` 的 `hasApiKey` |

## 设计要点

- **systemd 单元**：`baihua-server@.service` / `baihua-webui@.service`（用户实例，`User=%I`）。
  `WorkingDirectory` 指向发布目录（appsettings/静态资源按 ContentRoot 解析），数据路径全部由环境变量显式给出。
  `bh install` 会把单元里的 `%h/.baihua` 渲染成实际的 `BAIHUA_HOME`。
- **SQLite 参数**：启动时确保 `journal_mode=WAL`（库级、持久）；`busy_timeout=15000` 与
  `synchronous=NORMAL` 由 `SqlitePragmasInterceptor` 在**每个连接**上设置（SQLite 默认 busy_timeout=0，
  并发写会立刻 `database is locked`）；`foreign_keys=ON` 由连接串 `ForeignKeys=true` 保证。
- **单一数据库、多上下文**：三个 DbContext 共用 `baihua.db`，`DatabaseInitializer` 按模型建表/补表。
  新增列需自行提供迁移（当前没有 EF Migrations：升级时用一次性 SQL 或在 `DatabaseInitializer` 补）。
- **为什么不双数据库（PG + SQLite）**：两种 SQL 方言的差异（`now()`、部分唯一索引、identity、FTS）
  会持续制造"测试环境过、生产环境炸"的问题；单文件库让备份、恢复、迁移都只有一个对象。

## Windows 遗留项

Windows 的 `bh`（wslc 容器）与 `bh dev`（native）目录/脚本里仍有 PostgreSQL 容器、`PG_*` 环境变量与检查。
服务端代码已不再读写 PostgreSQL（SQLite 落在 `bh-data` 卷 / `%BAIHUA_HOME%\db`），这些属于**待清理的遗留项**：
清理时删除 postgres 容器创建/等待、备份里的 `bh-pgdata` 与恢复演练、doctor 的 PG 检查即可，不影响数据。
