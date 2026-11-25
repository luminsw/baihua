#!/usr/bin/env bash
# bh — 百花 CLI（Linux 原生形态：systemd + SQLite，无容器/无 k8s）
#
# 形态说明（2026-11 起 Linux 只有这一种）：
#   - 服务：systemd 用户实例 baihua-server@<user>.service（8788）/ baihua-webui@<user>.service（5177）
#   - 数据：$BAIHUA_HOME（默认 ~/.baihua）：db/baihua.db、config/*.env、vaults/
#   - 部署：bh deploy = 发布 dotnet 产物到 $BAIHUA_HOME/bin + 安装 systemd 单元 + 启动
#
# 为什么放弃 k8s/PostgreSQL：单节点家庭服务器上 k3s 与 PG 的启动/恢复过程会互相拖累
# （PG 崩溃恢复期间被存活探针反复重启 → 后端永远等不到数据库）。原生 systemd + SQLite
# 没有独立数据库进程、没有容器编排，日志进 journald，备份就是打包一个目录。
#
# 用法: bh <command> [args]     （bh help 看全部命令）
set -uo pipefail

# 用 sudo 调用时 $HOME 会变成 /root，产物/数据会被写到错误位置；需要的 root 操作脚本内部会自己 sudo。
if [ "$(id -u)" -eq 0 ] && [ -n "${SUDO_USER:-}" ]; then
    printf '[bh] 请用普通用户执行（需要 root 的步骤会自动 sudo），例如: sudo -u %s bh %s\n' \
        "$SUDO_USER" "${1:-status}" >&2
    exit 2
fi

ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/../../../.." && pwd)"
UNIT_DIR="$ROOT/tools/bh/linux/native/systemd"
LOCATOR="$ROOT/tools/bh/locator.sh"

DATA_HOME="${BAIHUA_HOME:-$HOME/.baihua}"
DB_PATH="${BAIHUA_DATABASE_PATH:-$DATA_HOME/db/baihua.db}"
BIN_DIR="$DATA_HOME/bin"
CONFIG_DIR="$DATA_HOME/config"
SERVER_PROJECT="$ROOT/services/Baihua.Server/Baihua.Server.csproj"
WEBUI_PROJECT="$ROOT/services/Baihua.Web/Baihua.Web.csproj"
SERVICES="server webui"
INSTANCE="$(systemd-escape -- "$(id -un)" 2>/dev/null || id -un)"

# 输出辅助（统一前缀，便于 journald/脚本抓取）
info() { printf '[%s] %s\n' "${TAG:-bh}" "$*" 2>/dev/null; }
warn() { printf '[%s] %s\n' "${TAG:-bh}" "$*" >&2; }
die()  { printf '[%s] %s\n' "${TAG:-bh}" "$*" >&2; exit 1; }
tag()  { TAG="$1"; }

usage() {
    cat <<'EOF'
bh — 百花 Linux 原生服务管理（systemd + SQLite）

用法: bh <command> [args]

服务生命周期
  bh install [--no-enable]   安装 CLI 定位器 + systemd 单元（默认 enable，不启动）
  bh build [server|webui|all] 发布 .NET 产物到 $BAIHUA_HOME/bin（会先停对应服务）
  bh deploy                  依次 build → install → start
  bh start|stop|restart [server|webui]   控制服务（省略 = 全部）
  bh status [--json]         服务状态 / HTTP 健康 / 数据库路径（--json 供 DSH 插件消费）
  bh logs <server|webui> [n] [-f]   journald 日志（-f 跟随）
  bh doctor                  体检：systemd/dotnet/单元/端口/磁盘/数据库/FTS5/nginx

数据
  bh backup [目录]           冷备份 $BAIHUA_HOME（要求服务已停止；默认 ./backup/）
  bh restore <archive> [-y]  从备份恢复（旧目录改名保留；需确认）
  bh db-migrate [--rewrite-prefix OLD=NEW]  把 PostgreSQL 的 baihua 库迁移到 SQLite（目标不能已存在）
                             旧容器路径可重写，如 --rewrite-prefix /app/data/vaults=$DATA_HOME/vaults

入口 / 升级
  bh proxy install           配置系统 nginx（:80 反代 WebUI 5177 + API 8788）
  bh dashboard               取 CLI token 并输出/打开 WebUI 地址
  bh update                   git pull --ff-only + build + restart
  bh uninstall [--purge]     停服务、删单元与定位器（--purge 同时删数据，慎用）

环境变量
  BAIHUA_HOME                数据目录（默认 ~/.baihua）
  BAIHUA_DATABASE_PATH       SQLite 文件（默认 $BAIHUA_HOME/db/baihua.db）
  BAIHUA_REPO                仓库根（定位器用；默认自动查找）
  BAIHUA_PUBLISH_SELF_CONTAINED=1  发布自包含产物（目标机无需装 .NET 运行时，体积更大）
EOF
}

# ---------- 基础设施 ----------

systemd_available() {
    [ -d /run/systemd/system ] && command -v systemctl >/dev/null 2>&1
}

require_systemd() {
    systemd_available || die "未检测到运行中的 systemd，无法管理服务（容器内请改用 dotnet 直接运行）。"
}

as_root() {
    if [ "$(id -u)" -eq 0 ]; then
        "$@"
    else
        command -v sudo >/dev/null 2>&1 || die "需要 root 权限，但本机没有 sudo。请用 root 重新执行。"
        sudo "$@"
    fi
}

unit_for() {
    case "$1" in
        server) printf 'baihua-server@%s.service\n' "$INSTANCE" ;;
        webui)  printf 'baihua-webui@%s.service\n' "$INSTANCE" ;;
        *) return 2 ;;
    esac
}

validate_service() {
    case "$1" in
        server|webui) ;;
        "") ;;
        *) die "未知服务: $1（可用: server / webui）" ;;
    esac
}

unit_installed() {
    # 模板单元（baihua-server@.service）才是安装产物；baihua-server@<user>.service 是运行时实例，不对应文件
    case "$1" in
        server) [ -f /etc/systemd/system/baihua-server@.service ] ;;
        webui)  [ -f /etc/systemd/system/baihua-webui@.service ] ;;
        *) return 1 ;;
    esac
}

service_active() {
    local unit; unit="$(unit_for "$1")" || return 1
    systemctl is-active --quiet "$unit" 2>/dev/null
}

require_built_services() {
    local target="${1:-all}" svc bin
    for svc in $SERVICES; do
        [ "$target" = all ] || [ "$target" = "$svc" ] || continue
        case "$svc" in
            server) bin="$BIN_DIR/server/bh-server" ;;
            webui)  bin="$BIN_DIR/webui/bh-webui" ;;
        esac
        [ -x "$bin" ] || die "缺少 $bin；请先执行 bh build $svc"
    done
}

git_head() { cd "$ROOT" && git rev-parse --short HEAD 2>/dev/null || echo unknown; }
git_branch() { cd "$ROOT" && git rev-parse --abbrev-ref HEAD 2>/dev/null || echo unknown; }
git_dirty() {
    local dirty=false
    if command -v git >/dev/null 2>&1 && [ -d "$ROOT/.git" ] \
        && [ -n "$(cd "$ROOT" && git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
        dirty=true
    fi
    printf '%s\n' "$dirty"
}

json_escape() {
    local s="$1"
    s="${s//\\/\\\\}"
    s="${s//\"/\\\"}"
    s="${s//$'\n'/\\n}"
    s="${s//$'\r'/}"
    s="${s//$'\t'/\\t}"
    printf '%s' "$s"
}

port_listening() {
    ss -ltn "sport = :$1" 2>/dev/null | grep -q LISTEN
}

human_bytes() {
    local bytes="${1:-0}"
    if [ "$bytes" -ge 1073741824 ] 2>/dev/null; then
        awk -v b="$bytes" 'BEGIN { printf "%.1fG", b/1073741824 }'
    elif [ "$bytes" -ge 1048576 ] 2>/dev/null; then
        awk -v b="$bytes" 'BEGIN { printf "%.1fM", b/1048576 }'
    elif [ "$bytes" -ge 1024 ] 2>/dev/null; then
        awk -v b="$bytes" 'BEGIN { printf "%.1fK", b/1024 }'
    else
        printf '%sB\n' "$bytes"
    fi
}

# 家庭局域网网段（排除 docker / k3s 网桥），用于放行 WebUI 的 /hubs/* 与管理 API
lan_cidr() {
    local ip_prefix
    ip_prefix="$(ip -4 -o addr show scope global 2>/dev/null \
        | awk '$2 !~ /^(docker|br-|cni|flannel|veth|virbr)/ {print $4; exit}')"
    [ -n "$ip_prefix" ] || return 1
    if command -v python3 >/dev/null 2>&1; then
        python3 - "$ip_prefix" <<'PYCODE'
import ipaddress, sys
print(ipaddress.ip_interface(sys.argv[1]).network)
PYCODE
    else
        # 回退：家庭局域网绝大多数是 /24
        local ip="${ip_prefix%/*}"
        printf '%s.0/24\n' "${ip%.*}"
    fi
}

db_size() {
    [ -f "$DB_PATH" ] || { printf '0\n'; return; }
    stat -c '%s' "$DB_PATH" 2>/dev/null || printf '0\n'
}

# ---------- 安装 ----------

# 单元模板里的 %h/.baihua 是默认数据目录；安装时替换为实际 BAIHUA_HOME
render_unit() {
    local src="$1" dst="$2"
    sed "s#%h/\.baihua#${DATA_HOME}#g" "$src" > "$dst" || die "渲染 systemd 单元失败: $src"
}

install_cli() {
    local bin_dir="${HOME}/.local/bin"
    mkdir -p "$bin_dir" || die "无法创建 $bin_dir"
    install -m 0755 "$LOCATOR" "$bin_dir/bh" || die "安装定位器失败"
    info "已安装 $bin_dir/bh（自包含定位器，仓库改名/移动后无需重装）"
    case ":$PATH:" in
        *":$bin_dir:"*) ;;
        *)
            if grep -q 'local/bin' "${HOME}/.bashrc" 2>/dev/null; then
                info "~/.local/bin 已在 ~/.bashrc，重新登录后生效"
            else
                printf 'export PATH="%s:$PATH"\n' "$bin_dir" >> "${HOME}/.bashrc"
                info "已把 $bin_dir 追加到 ~/.bashrc（重新登录或 source ~/.bashrc 后生效）"
            fi
            ;;
    esac
}

install_services() {
    local enable=1
    [ "${1:-}" = "--no-enable" ] && enable=0
    require_systemd

    # 不预建 vaults：知识库目录允许是指向已有目录的符号链接（迁移时把旧知识库挂进来）
    mkdir -p "$DATA_HOME/db" "$DATA_HOME/config" "$DATA_HOME/logs" || die "无法创建 $DATA_HOME 子目录"

    # 单元里写的是 %h/.baihua；安装时替换成实际 BAIHUA_HOME，CLI 与 systemd 才不会各用一套目录
    local tmp_dir; tmp_dir="$(mktemp -d)"
    render_unit "$UNIT_DIR/baihua-server@.service" "$tmp_dir/baihua-server@.service"
    render_unit "$UNIT_DIR/baihua-webui@.service" "$tmp_dir/baihua-webui@.service"
    as_root install -D -m 0644 "$tmp_dir/baihua-server@.service" /etc/systemd/system/baihua-server@.service
    as_root install -D -m 0644 "$tmp_dir/baihua-webui@.service" /etc/systemd/system/baihua-webui@.service
    rm -rf "$tmp_dir"
    as_root systemctl daemon-reload

    if [ "$enable" = 1 ]; then
        as_root systemctl enable "baihua-server@${INSTANCE}.service" "baihua-webui@${INSTANCE}.service" >/dev/null 2>&1 \
            || warn "systemctl enable 失败（服务仍可手动 bh start）"
    fi

    if [ -f "$CONFIG_DIR/server.env" ]; then
        info "已存在 $CONFIG_DIR/server.env（不会被覆盖）"
    else
        local admin_nets; admin_nets="$(lan_cidr 2>/dev/null || true)"
        {
            cat <<'ENVEOF'
# 百花后端（baihua-server@<user>.service）环境变量
# 每行 KEY=VALUE；改完执行 bh restart
# 配对二维码/移动端入口：留空时按本机 IP + 8788 自动探测；走 nginx 时设为 http://<host>
#Baihua__PublicBaseUrl=http://192.168.1.10
ENVEOF
            if [ -n "$admin_nets" ]; then
                printf '\n# 家庭局域网访问管理 API / WebUI 实时推送（/hubs/*）；收紧时改成更小网段或删掉\nBAIHUA_ADMIN_ALLOWED_NETS=%s\n' "$admin_nets"
            else
                printf '\n# 家庭局域网访问管理 API / WebUI 实时推送（/hubs/*）的放行网段\n#BAIHUA_ADMIN_ALLOWED_NETS=192.168.1.0/24\n'
            fi
        } > "$CONFIG_DIR/server.env"
        chmod 0600 "$CONFIG_DIR/server.env"
    fi

    info "systemd 单元已安装（用户实例: $INSTANCE）"
    info "接下来: bh build && bh deploy（或 bh start）"
}

uninstall_all() {
    local purge=0
    [ "${1:-}" = "--purge" ] && purge=1
    if systemd_available; then
        as_root systemctl disable --now "baihua-webui@${INSTANCE}.service" "baihua-server@${INSTANCE}.service" >/dev/null 2>&1 || true
        as_root rm -f /etc/systemd/system/baihua-server@.service /etc/systemd/system/baihua-webui@.service
        as_root systemctl daemon-reload || true
    fi
    rm -f "${HOME}/.local/bin/bh" 2>/dev/null || true
    if [ "$(id -u)" -eq 0 ]; then rm -f /usr/local/bin/bh 2>/dev/null || true; fi
    info "已移除 systemd 单元与 CLI 定位器"
    if [ "$purge" = 1 ]; then
        [ -n "$DATA_HOME" ] && [ "$DATA_HOME" != "/" ] || die "拒绝删除可疑路径: $DATA_HOME"
        read -r -p "确认删除全部数据 $DATA_HOME ？输入 yes 继续: " answer
        [ "$answer" = "yes" ] && rm -rf -- "$DATA_HOME" && info "已删除 $DATA_HOME" || info "已取消删除"
    else
        info "数据保留在 $DATA_HOME（需要时加 --purge）"
    fi
}

# ---------- 构建 ----------

require_dotnet() {
    command -v dotnet >/dev/null 2>&1 || die "需要 .NET SDK 10（dotnet）。安装后重试。"
    local major
    major="$(dotnet --version 2>/dev/null | cut -d. -f1)"
    [ "${major:-0}" -ge 10 ] 2>/dev/null || warn "当前 dotnet 版本 $(dotnet --version 2>/dev/null) 可能低于 10，请确认"
}

publish_service() {
    local svc="$1" project out_dir marker
    case "$svc" in
        server) project="$SERVER_PROJECT"; out_dir="$BIN_DIR/server" ;;
        webui)  project="$WEBUI_PROJECT";  out_dir="$BIN_DIR/webui" ;;
    esac

    mkdir -p "$out_dir" || die "无法创建 $out_dir"
    local args=(-c Release -o "$out_dir" --nologo)
    if [ "${BAIHUA_PUBLISH_SELF_CONTAINED:-0}" = "1" ]; then
        args+=(--self-contained true -r linux-x64)
    fi

    tag build
    info "发布 $svc → $out_dir"
    if ! dotnet publish "$project" "${args[@]}"; then
        die "$svc 发布失败"
    fi

    marker="$out_dir/.build-commit"
    {
        printf 'commit=%s\n' "$(git_head)"
        printf 'branch=%s\n' "$(git_branch)"
        printf 'builtAt=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    } > "$marker"
}

build_services() {
    local target="${1:-all}"
    case "$target" in all|server|webui) ;; *) die "未知构建目标: $target（可用: all / server / webui）" ;; esac
    require_dotnet

    # Linux 不允许写入正在运行的可执行文件（ETXTBSY）：发布前先停对应服务。
    if systemd_available; then
        local svc unit stopped=0
        for svc in $SERVICES; do
            [ "$target" = all ] || [ "$target" = "$svc" ] || continue
            unit="$(unit_for "$svc")"
            if systemctl is-active --quiet "$unit" 2>/dev/null; then
                tag build
                info "停止 $svc（发布期间不能覆盖运行中的可执行文件）"
                as_root systemctl stop "$unit" || die "停止 $unit 失败"
                stopped=1
            fi
        done
        [ "$stopped" = 1 ] && info "服务已停止；发布完成后执行 bh start 恢复"
    fi

    local svc
    for svc in $SERVICES; do
        [ "$target" = all ] || [ "$target" = "$svc" ] || continue
        publish_service "$svc"
    done
    tag build
    info "发布完成（commit $(git_head)）"
}

# ---------- 服务控制 ----------

service_action() {
    local action="$1" only="${2:-}"
    validate_service "$only"
    require_systemd

    local units=() svc unit
    if [ -n "$only" ]; then
        units+=("$(unit_for "$only")")
        require_built_services "$only"
    else
        # 先 server 后 webui（webui 的 unit 里也声明了 Requires/After，双保险）
        for svc in $SERVICES; do units+=("$(unit_for "$svc")"); done
        require_built_services all
    fi

    tag "$action"
    if ! as_root systemctl "$action" "${units[@]}"; then
        if [ "$action" = "stop" ]; then
            # stop 要幂等：单元没装/本来就停着时 systemctl 返回非 0，不应让脚本失败
            warn "stop 未完全成功（通常是单元未安装或已停止）：${units[*]}"
        else
            die "$action 失败：${units[*]}（查日志：bh logs server 100 / journalctl -u ${units[0]} -n 100）"
        fi
    fi
    info "$action 完成：${units[*]}"
}

unit_field() {
    systemctl show -p "$2" --value "$1" 2>/dev/null | head -n1
}

check_http() {
    curl -fsS --max-time 3 "$1" >/dev/null 2>&1
}

show_status() {
    local as_json="${1:-}"
    local head branch dirty
    head="$(git_head)"; branch="$(git_branch)"; dirty="$(git_dirty)"

    if [ "$as_json" = "--json" ] && systemd_available; then
        local entries="" first=1 ready_total=0 total=0 svc unit phase ready replicas restarts since pid image up_to_date health
        for svc in $SERVICES; do
            unit="$(unit_for "$svc")"
            if unit_installed "$svc"; then
                replicas=1
            else
                replicas=0
            fi
            phase="$(unit_field "$unit" ActiveState)"
            [ -n "$phase" ] || phase="not-installed"
            ready=0; service_active "$svc" && ready=1
            restarts="$(unit_field "$unit" NRestarts)"; [ -n "$restarts" ] || restarts=0
            pid="$(unit_field "$unit" MainPID)"; [ -n "$pid" ] || pid=0
            since="$(unit_field "$unit" ActiveEnterTimestamp)"
            case "$svc" in
                server) health=0; check_http "http://127.0.0.1:8788/health" && health=1 ;;
                webui)  health=0; check_http "http://127.0.0.1:5177/" && health=1 ;;
            esac
            image="unknown"; up_to_date=false
            if [ -f "$BIN_DIR/$svc/.build-commit" ]; then
                image="$(sed -n 's/^commit=//p' "$BIN_DIR/$svc/.build-commit" | head -n1)"
                [ -n "$image" ] || image="unknown"
                [ "$image" = "$head" ] && up_to_date=true
            fi
            ready_total=$((ready_total + ready))
            total=$((total + replicas))
            [ "$first" = 0 ] && entries="$entries,"
            first=0
            entries="$entries{\"name\":\"$svc\",\"ready\":$ready,\"replicas\":$replicas,\"image\":\"$image\",\"age\":\"$(json_escape "$since")\",\"restarts\":$restarts,\"phase\":\"$phase\",\"imageCommit\":\"$image\",\"upToDate\":$up_to_date,\"pid\":$pid,\"health\":$health}"
        done
        printf '{"cell":"native","updatedAt":"%s","git":{"head":"%s","branch":"%s","dirty":%s},"services":[%s],"summary":{"ready":%s,"total":%s},"database":{"path":"%s","bytes":%s,"exists":%s},"home":"%s"}\n' \
            "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$head" "$branch" "$dirty" "$entries" \
            "$ready_total" "$total" "$(json_escape "$DB_PATH")" "$(db_size)" \
            "$([ -f "$DB_PATH" ] && echo true || echo false)" "$(json_escape "$DATA_HOME")"
        return 0
    fi

    if systemd_available; then
        local svc unit phase sub pid restarts since
        for svc in $SERVICES; do
            unit="$(unit_for "$svc")"
            if ! unit_installed "$svc"; then
                info "$svc: 单元未安装（bh install）"
                continue
            fi
            phase="$(unit_field "$unit" ActiveState)"
            sub="$(unit_field "$unit" SubState)"
            pid="$(unit_field "$unit" MainPID)"
            restarts="$(unit_field "$unit" NRestarts)"
            since="$(unit_field "$unit" ActiveEnterTimestamp)"
            info "$svc: ${phase:-unknown}/${sub:-unknown} pid=${pid:-0} restarts=${restarts:-0} since=${since:-'-'}"
        done
    else
        warn "systemd 不可用，无法查询服务状态"
    fi

    check_http "http://127.0.0.1:8788/health" && info "health: server OK (8788)" || info "health: server 无响应 (8788)"
    check_http "http://127.0.0.1:5177/"      && info "health: webui OK (5177)"  || info "health: webui 无响应 (5177)"
    info "database: $DB_PATH ($(human_bytes "$(db_size)"))"
    info "git: $head ($branch) dirty=$dirty"
}

show_logs() {
    local svc="${1:-server}" count="${2:-50}" follow="${3:-}"
    case "$svc" in
        server|webui) ;;
        *) die "未知服务: $svc（可用: server / webui）" ;;
    esac
    [[ "$count" =~ ^[0-9]+$ ]] || die "行数必须是数字: $count"
    require_systemd
    local unit; unit="$(unit_for "$svc")"
    if [ "$follow" = "-f" ]; then
        journalctl -u "$unit" -n "$count" -f
    else
        journalctl -u "$unit" -n "$count" --no-pager
    fi
}

# ---------- 体检 ----------

doctor() {
    local failed=0
    tag doctor

    if systemd_available; then
        info "ok   systemd 运行中"
    else
        warn "fail systemd 未运行（容器/WSL 内无法用 systemd 形态）"; failed=1
    fi

    if command -v dotnet >/dev/null 2>&1; then
        local sdk; sdk="$(dotnet --version 2>/dev/null)"
        if [ "$(printf '%s' "${sdk%%.*}" | tr -dc '0-9')" -ge 10 ] 2>/dev/null; then
            info "ok   dotnet SDK $sdk"
        else
            warn "warn dotnet $sdk（构建需要 SDK 10）"
        fi
        if dotnet --list-runtimes 2>/dev/null | grep -q 'Microsoft.NETCore.App 10\.'; then
            info "ok   .NET 10 运行时已安装"
        else
            warn "warn 未发现 .NET 10 运行时（framework-dependent 产物无法启动）"
        fi
    else
        warn "warn 未安装 dotnet（仅运行已发布产物时可忽略）"
    fi

    local svc unit bin
    for svc in $SERVICES; do
        unit="$(unit_for "$svc")"
        case "$svc" in
            server) bin="$BIN_DIR/server/bh-server" ;;
            webui)  bin="$BIN_DIR/webui/bh-webui" ;;
        esac
        if unit_installed "$svc"; then info "ok   $svc 单元已安装"; else warn "warn $svc 单元未安装（bh install）"; fi
        if [ -x "$bin" ]; then info "ok   $svc 已发布（$bin）"; else warn "warn $svc 未发布（bh build $svc）"; fi
    done

    local port
    for port in 8788 5177; do
        if port_listening "$port"; then
            info "ok   端口 $port 监听中"
        else
            info "info 端口 $port 未监听"
        fi
    done

    if [ -f "$DB_PATH" ]; then
        local size; size="$(db_size)"
        info "ok   SQLite 数据库 $(human_bytes "$size")（$DB_PATH）"
        if command -v python3 >/dev/null 2>&1; then
            local result
            result="$(python3 - "$DB_PATH" <<'PY' 2>/dev/null
import sqlite3, sys
try:
    con = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
    print("|".join([
        con.execute("pragma integrity_check").fetchone()[0],
        str(len(con.execute("select name from sqlite_master where type='table'").fetchall())),
        "ft5" if any("ENABLE_FTS5" in row[0] for row in con.execute("pragma compile_options")) else "nofts5",
        con.execute("pragma journal_mode").fetchone()[0],
    ]))
except Exception as exc:
    print(f"error|{exc}")
PY
)"
            IFS='|' read -r integrity tables fts journal <<< "$result"
            if [ "${integrity:-}" = "ok" ]; then
                info "ok   integrity_check=ok，$tables 张表，journal=$journal，$fts"
            else
                warn "fail 数据库检查失败：${result:-无输出}"; failed=1
            fi
            [ "${fts:-}" = "ft5" ] || [ -z "${fts:-}" ] || warn "warn 当前 python sqlite 未启用 FTS5（仅影响体检，服务端用自带 e_sqlite3）"
        else
            info "info 未安装 python3，跳过完整性检查"
        fi
    else
        info "info 数据库尚未创建（首次 bh start 时自动建库）：$DB_PATH"
    fi

    local avail_kb df_target="$DATA_HOME"
    # 数据目录可能尚未创建：沿父目录向上找到第一个存在的路径再查磁盘
    while [ ! -d "$df_target" ] && [ "$df_target" != "/" ]; do df_target="$(dirname "$df_target")"; done
    avail_kb="$(df -Pk "$df_target" 2>/dev/null | awk 'NR==2 {print $4}')"
    if [ -n "${avail_kb:-}" ]; then
        if [ "$avail_kb" -lt 262144 ] 2>/dev/null; then
            warn "warn $DATA_HOME 剩余空间不足 256MB（${avail_kb}KB）"
        else
            info "ok   磁盘剩余 $(human_bytes $((avail_kb * 1024)))（$DATA_HOME）"
        fi
    fi

    if command -v nginx >/dev/null 2>&1; then
        if [ -f /etc/nginx/sites-enabled/baihua ]; then
            info "ok   nginx :80 入口已配置（/etc/nginx/sites-enabled/baihua）"
        else
            info "info 已安装 nginx 但未配置百花入口（bh proxy install）"
        fi
    else
        info "info 未安装 nginx（可选：局域网 :80 统一入口）"
    fi

    [ "$failed" = 0 ] && info "体检完成：没有致命问题" || warn "体检完成：存在致命问题（见上）"
    return "$failed"
}

# ---------- 数据 ----------

backup_data() {
    local out="${1:-$PWD/backup}"
    tag backup
    if systemd_available && { service_active server || service_active webui; }; then
        die "请先停止服务再冷备份：bh stop（SQLite 在运行中复制可能不一致）"
    fi
    [ -d "$DATA_HOME" ] || die "数据目录不存在: $DATA_HOME"
    mkdir -p "$out" || die "无法创建备份目录 $out"

    local stamp archive
    stamp="$(date +%Y%m%d-%H%M%S)"
    archive="$out/baihua-backup-$stamp.tar.gz"

    # 排除可重建的发布产物（bin）；db/config/vaults/logs 全部保留
    if ! tar -czf "$archive" --warning=no-file-changed \
        --exclude='./bin' -C "$DATA_HOME" .; then
        die "打包失败：$archive"
    fi
    tar -tzf "$archive" >/dev/null 2>&1 || die "备份包校验失败：$archive"
    if command -v sha256sum >/dev/null 2>&1; then
        (cd "$out" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
    fi
    info "备份完成：$archive（$(human_bytes "$(stat -c '%s' "$archive")")）"
    info "恢复：bh restore $archive（先 bh build 重新发布产物再 bh start）"
}

restore_data() {
    local archive="${1:-}" assume_yes="${2:-}"
    tag restore
    [ -n "$archive" ] && [ -f "$archive" ] || die "用法: bh restore <archive.tar.gz> [-y]"
    if systemd_available && { service_active server || service_active webui; }; then
        die "请先停止服务再恢复：bh stop"
    fi

    if [ "$assume_yes" != "-y" ]; then
        read -r -p "将用 $archive 覆盖 $DATA_HOME（旧目录会改名保留）。输入 yes 继续: " answer
        [ "$answer" = "yes" ] || { info "已取消"; return 0; }
    fi

    local staging old
    staging="$(mktemp -d "$(dirname "$DATA_HOME")/.baihua-restore.XXXXXX")" || die "无法创建临时目录"
    tar -xzf "$archive" -C "$staging" || { rm -rf "$staging"; die "解包失败"; }

    if [ -e "$DATA_HOME" ]; then
        old="$DATA_HOME.pre-restore-$(date +%Y%m%d-%H%M%S)"
        mv "$DATA_HOME" "$old" || { rm -rf "$staging"; die "无法移走旧数据目录"; }
        info "旧数据已保留在 $old"
    fi
    mv "$staging" "$DATA_HOME" || { rm -rf "$staging"; die "恢复失败"; }
    mkdir -p "$DATA_HOME/db" "$DATA_HOME/config" "$DATA_HOME/logs"
    [ -f "$DB_PATH" ] && chmod 0600 "$DB_PATH" 2>/dev/null || true
    info "恢复完成：$DATA_HOME"
    info "产物需重新发布：bh build && bh start"
}

migrate_database() {
    local extra=("$@")
    tag db-migrate
    require_dotnet
    if [ -e "$DB_PATH" ]; then
        die "目标已存在，拒绝覆盖：$DB_PATH（先移走旧库）"
    fi
    if [ -z "${BAIHUA_MIGRATION_POSTGRES_CONNECTION:-}" ] && [ -z "${PG_PASSWORD:-}" ]; then
        cat >&2 <<EOF
[db-migrate] 需要 PostgreSQL 源库连接：
  export BAIHUA_MIGRATION_POSTGRES_CONNECTION='Host=127.0.0.1;Port=5432;Database=baihua;Username=baihua;Password=...'
  或 export PG_PASSWORD=...（配合 PG_HOST/PG_PORT/PG_DATABASE/PG_USER）
EOF
        exit 2
    fi
    mkdir -p "$(dirname "$DB_PATH")"
    dotnet run --project "$ROOT/tools/db-migrate/Baihua.DbMigrator.csproj" -c Release -- \
        --target "$DB_PATH" ${extra[@]+"${extra[@]}"} || die "迁移失败（目标库未写入）"
    chmod 0600 "$DB_PATH" 2>/dev/null || true
    info "迁移完成：$DB_PATH"
    info "下一步：bh build && bh start"
}

# ---------- 入口 / 升级 ----------

configure_proxy() {
    tag proxy
    command -v nginx >/dev/null 2>&1 || die "请先安装 nginx（Debian/Ubuntu: sudo apt install nginx）"
    command -v envsubst >/dev/null 2>&1 || die "需要 envsubst（Debian/Ubuntu: sudo apt install gettext-base）"
    local template="$ROOT/docker/nginx/default.conf.template"
    local headers="$ROOT/docker/nginx/server-proxy-headers.conf"
    [ -f "$template" ] || die "缺少 nginx 模板: $template"

    local temp
    temp="$(mktemp)"
    BAIHUA_NGINX_PORT=80 BAIHUA_WEBUI_PREFIX= BAIHUA_NGINX_CLIENT_MAX_BODY_SIZE=100M \
        envsubst '${BAIHUA_NGINX_PORT} ${BAIHUA_WEBUI_PREFIX} ${BAIHUA_NGINX_CLIENT_MAX_BODY_SIZE}' \
        < "$template" > "$temp" || { rm -f "$temp"; die "渲染 nginx 配置失败"; }

    local target_dir="/etc/nginx/sites-available"
    [ -d "$target_dir" ] || target_dir="/etc/nginx/conf.d"
    as_root install -D -m 0644 "$temp" "$target_dir/baihua.conf"
    as_root install -D -m 0644 "$headers" /etc/nginx/server-proxy-headers.conf
    if [ -d /etc/nginx/sites-enabled ]; then
        # 老版本装的是 sites-enabled/baihua（无 .conf），留着会和 baihua.conf 争 :80
        as_root rm -f /etc/nginx/sites-enabled/baihua
        as_root ln -sfn "$target_dir/baihua.conf" /etc/nginx/sites-enabled/baihua.conf
        # 关掉 Debian/Ubuntu 默认站点，避免 :80 抢占
        [ -e /etc/nginx/sites-enabled/default ] && as_root rm -f /etc/nginx/sites-enabled/default || true
    fi
    rm -f "$temp"
    as_root nginx -t || die "nginx 配置校验失败，未重启"
    # 首次安装时 nginx 可能因发行版默认站点的 IPv6 listen 起不来；这里统一 enable + 重启
    as_root systemctl enable nginx >/dev/null 2>&1 || true
    as_root systemctl restart nginx || die "nginx 启动失败（journalctl -u nginx -n 50）"

    local lan_ip env_file="$CONFIG_DIR/server.env"
    lan_ip="$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([^ ]*\).*/\1/p' | head -n1)"
    mkdir -p "$CONFIG_DIR"
    touch "$env_file"
    chmod 0600 "$env_file" 2>/dev/null || true

    # WebUI 浏览器的 /hubs/*（设备待授权、任务进度等实时推送）走管理 API 的访问控制：
    # 只放行 loopback 时，局域网页面能打开但收不到推送（表现为"扫码后待授权一直是空的"）。
    if ! grep -q '^BAIHUA_ADMIN_ALLOWED_NETS=' "$env_file"; then
        local cidr; cidr="$(lan_cidr 2>/dev/null || true)"
        if [ -n "$cidr" ]; then
            printf '\n# 家庭局域网访问管理 API / WebUI 实时推送（/hubs/*）\nBAIHUA_ADMIN_ALLOWED_NETS=%s\n' "$cidr" >> "$env_file"
            info "已写入 BAIHUA_ADMIN_ALLOWED_NETS=$cidr（WebUI 实时推送需要）"
        else
            warn "未探测到局域网网段，请手工在 $env_file 设置 BAIHUA_ADMIN_ALLOWED_NETS"
        fi
    fi
    if [ -n "$lan_ip" ]; then
        if grep -q '^Baihua__PublicBaseUrl=' "$env_file"; then
            info "保留已有 Baihua__PublicBaseUrl（$(grep '^Baihua__PublicBaseUrl=' "$env_file" | tail -n1)）"
        else
            printf 'Baihua__PublicBaseUrl=http://%s\n' "$lan_ip" >> "$env_file"
            info "已写入 Baihua__PublicBaseUrl=http://$lan_ip（配对二维码/移动端入口走 :80）"
        fi
        service_active server && as_root systemctl restart "baihua-server@${INSTANCE}.service" >/dev/null 2>&1 || true
    else
        warn "未探测到局域网地址，请手工在 $env_file 设置 Baihua__PublicBaseUrl"
    fi
    info "nginx :80 入口已就绪（WebUI 5177 / API 8788）"
}

open_dashboard() {
    tag dashboard
    # CLI token 由 WebUI 签发（/api/auth/cli-token 在 5177），不是后端 8788
    local token="" attempt url base
    for attempt in 1 2 3 4 5; do
        token="$(curl -fsS -m 5 -X POST "http://127.0.0.1:5177/api/auth/cli-token" 2>/dev/null \
            | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')"
        [ -n "$token" ] && break
        [ "$attempt" = 5 ] || sleep 2
    done

    # 装了 nginx（:80）就走统一入口；否则直连 5177
    if port_listening 80; then
        base="http://$(ip -4 route get 1.1.1.1 2>/dev/null | sed -n 's/.* src \([^ ]*\).*/\1/p' | head -n1)"
        [ "$base" = "http://" ] && base="http://127.0.0.1"
    else
        base="http://127.0.0.1:5177"
    fi
    if [ -n "$token" ]; then
        url="$base/?cli-token=$token"
        info "CLI token 获取成功（5 分钟内可重复打开）"
    else
        url="$base/"
        warn "CLI token 获取失败（WebUI 未就绪？先 bh start / bh logs webui）"
    fi
    info "URL: $url"
    if [ "${BAIHUA_DASHBOARD_PRINT_ONLY:-0}" = "1" ]; then
        printf 'URL=%s\n' "$url"
        return 0
    fi
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$url" >/dev/null 2>&1 & disown 2>/dev/null || true
    fi
}

update_all() {
    tag update
    command -v git >/dev/null 2>&1 || die "需要 git"
    if [ -n "$(cd "$ROOT" && git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
        die "工作区有未提交的已跟踪改动，先处理后再 bh update"
    fi
    (cd "$ROOT" && git pull --ff-only) || die "git pull 失败（先确认分支与远端）"
    build_services all
    service_action start
}

# ---------- 入口 ----------

case "${1:-help}" in
    help|-h|--help|"") usage ;;
    install)   install_cli; install_services "${2:-}" ;;
    uninstall) uninstall_all "${2:-}" ;;
    build)     build_services "${2:-all}" ;;
    deploy)    build_services all; install_cli; install_services ""; service_action start ;;
    start|stop|restart) service_action "$1" "${2:-}" ;;
    status)    show_status "${2:-}" ;;
    logs)      show_logs "${2:-server}" "${3:-50}" "${4:-}" ;;
    doctor)    doctor ;;
    backup)    backup_data "${2:-}" ;;
    restore)   restore_data "${2:-}" "${3:-}" ;;
    db-migrate) shift; migrate_database "$@" ;;
    proxy)
        [ "${2:-}" = "install" ] || die "用法: bh proxy install"
        configure_proxy
        ;;
    dashboard) open_dashboard ;;
    update)    update_all ;;
    *)
        warn "未知命令: $1"
        usage
        exit 2
        ;;
esac
