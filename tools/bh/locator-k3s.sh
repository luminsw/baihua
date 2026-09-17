#!/bin/bash
# bh-k3s - 百花 k3s（全容器化）部署 CLI —— Linux/WSL 入口（自包含定位器）
#
# 与仓库路径解耦：本脚本由 bh install 复制到 PATH（~/.local/bin/bh-k3s 或 /usr/local/bin/bh-k3s），
# 每次调用时按下述优先级定位仓库根（可执行入口为 tools/bh/bh-k3s.sh），
# 仓库目录改名/移动后无需重装。
#
# 与 locator.sh（bh）是同一套定位逻辑，区别只在查找的入口文件名；两者都指向 bh.sh 同一实现。
#
# 定位优先级：
#   1. $BAIHUA_HOME 环境变量（显式指定，最可靠）
#   2. 常见候选路径（覆盖新旧目录名）
#   3. 从当前目录向上查找 tools/bh/bh-k3s.sh
#
# 注：Windows 侧的 bh-k3s 是另一个脚本（tools/bh/bh-k3s.ps1，经 WSL 转发），不要混淆。
set -u

ENTRY="bh-k3s.sh"

find_root() {
    # 1) 环境变量显式指定
    if [ -n "${BAIHUA_HOME:-}" ] && [ -f "$BAIHUA_HOME/tools/bh/$ENTRY" ]; then
        printf '%s\n' "$BAIHUA_HOME"
        return 0
    fi

    # 2) 常见候选路径（可按需增删）
    local cand
    for cand in \
        "$HOME/src/mdyj/baihua" \
        "$HOME/src/mdyj/baihuagu" \
        "$HOME/src/baihua" \
        "$HOME/src/baihuagu" \
        "$HOME/baihua" \
        "$HOME/baihuagu" \
        "$HOME/work/baihua"; do
        if [ -f "$cand/tools/bh/$ENTRY" ]; then
            printf '%s\n' "$cand"
            return 0
        fi
    done

    # 3) 从当前目录向上查找
    local dir
    dir="$(pwd)"
    while [ "$dir" != "/" ]; do
        if [ -f "$dir/tools/bh/$ENTRY" ]; then
            printf '%s\n' "$dir"
            return 0
        fi
        dir="$(dirname "$dir")"
    done

    return 1
}

root="$(find_root)" || {
    echo "[bh-k3s] 未找到 baihua 仓库（缺少 tools/bh/$ENTRY）" >&2
    echo "[bh-k3s] 请设置 BAIHUA_HOME 指向仓库根，或在仓库目录内执行 bh-k3s" >&2
    exit 1
}

exec bash "$root/tools/bh/bh-k3s.sh" "$@"
