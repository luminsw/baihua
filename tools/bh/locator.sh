#!/bin/bash
# bh — 百花统一 CLI 自包含定位器（安装时复制到 PATH，与仓库路径解耦）
#
# 解决的问题：旧版 bh install 创建软链接（~/.local/bin/bh -> 仓库绝对路径），
# 仓库目录改名/移动后软链变死链，且 bash 执行断链时根本不会读取脚本内容，
# 只能手动重新安装。本定位器被【复制】到 PATH，自身不依赖任何固定路径：
# 每次调用时按优先级定位仓库根，再转发到 <root>/tools/bh/bh.sh。
#
# 定位优先级：
#   1. $BAIHUA_REPO 环境变量（显式指定仓库根，最可靠）
#   2. $BAIHUA_HOME —— 仅当它确实指向仓库（含 tools/bh/bh.sh）时采用；
#      注意 BAIHUA_HOME 的正式含义是**数据目录**（~/.baihua），不能再当仓库根用
#   3. 常见候选路径（新旧目录名都覆盖，改一次名无需任何操作）
#   4. 从当前目录向上查找 tools/bh/bh.sh（在仓库内或其子目录执行时必然命中）
#
# 仓库目录随便改、随便移动（只要还在常见位置或设置 BAIHUA_REPO），bh 都能用。
set -u

find_root() {
    # 1) 环境变量显式指定
    if [ -n "${BAIHUA_REPO:-}" ] && [ -f "$BAIHUA_REPO/tools/bh/bh.sh" ]; then
        printf '%s\n' "$BAIHUA_REPO"
        return 0
    fi

    # 2) 兼容旧习惯：BAIHUA_HOME 指到仓库根时仍可用（指到数据目录时自然不命中）
    if [ -n "${BAIHUA_HOME:-}" ] && [ -f "$BAIHUA_HOME/tools/bh/bh.sh" ]; then
        printf '%s\n' "$BAIHUA_HOME"
        return 0
    fi

    # 3) 常见候选路径（可按需增删）
    local cand
    for cand in \
        "$HOME/src/mdyj/baihua" \
        "$HOME/src/mdyj/baihuagu" \
        "$HOME/src/baihua" \
        "$HOME/src/baihuagu" \
        "$HOME/baihua" \
        "$HOME/baihuagu" \
        "$HOME/work/baihua"; do
        if [ -f "$cand/tools/bh/bh.sh" ]; then
            printf '%s\n' "$cand"
            return 0
        fi
    done

    # 4) 从当前目录向上查找
    local dir
    dir="$(pwd)"
    while [ "$dir" != "/" ]; do
        if [ -f "$dir/tools/bh/bh.sh" ]; then
            printf '%s\n' "$dir"
            return 0
        fi
        dir="$(dirname "$dir")"
    done

    return 1
}

root="$(find_root)" || {
    echo "[bh] 未找到 baihua 仓库（缺少 tools/bh/bh.sh）" >&2
    echo "[bh] 请设置 BAIHUA_REPO 指向仓库根，或在仓库目录内执行 bh" >&2
    exit 1
}

exec bash "$root/tools/bh/bh.sh" "$@"
