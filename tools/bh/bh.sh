#!/bin/bash
# bh — 百花 CLI 入口（Linux）
#
# 2026-11 起 Linux 只有一种部署形态：**原生 systemd 服务 + SQLite**（无 k8s / 无 PostgreSQL）。
# 本脚本只做定位与转发，实现见 tools/bh/linux/native/bh.sh。
#
# 为什么换掉 k8s：单节点家庭服务器上 k3s + PostgreSQL 的启动/恢复互相拖累
# （PG 崩溃恢复期间被存活探针反复重启，后端永远等不到数据库就退出）。
# 原生形态没有独立数据库进程、没有编排层：日志进 journald，备份就是打包 ~/.baihua。
#
# 用法: bh <command> [args]      bh help 查看全部命令
set -u

# readlink -f 解析软链：经 ~/.local/bin/bh 调用时也能定位到仓库根
ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")/../.." && pwd)"

case "${1:-}" in
    k8s|k3s|bh-k3s)
        echo "[bh] k8s 形态已退役（Linux 改为原生 systemd + SQLite）。" >&2
        echo "     直接执行: bh ${*:2}" >&2
        exit 1
        ;;
    dev|wslc|container|docker)
        echo "[bh] '$1' 是 Windows 侧的形态名；Linux 上直接用 bh <command>。" >&2
        echo "     例: bh status / bh start / bh doctor" >&2
        exit 1
        ;;
esac

exec bash "$ROOT/tools/bh/linux/native/bh.sh" "$@"
