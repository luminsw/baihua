#!/bin/bash
# bh-k3s - 百花 k3s（全容器化）部署 CLI 入口（Linux/WSL，仓库内）
#
# 与 bh.sh 完全等价：Linux 上只有 k3s 一种形态，两个名字都是它的入口。
# 保留 bh-k3s 是为了与 Windows 侧命令名对齐（Windows: bh = native，bh-k3s = k3s 经 WSL）。
#
# 用法:
#   bh-k3s <command> [args]      执行 k3s 命令（= bh <command>）
#   bh-k3s install               安装定位器到 PATH（装 bh 与 bh-k3s）
#   bh-k3s uninstall             移除定位器
set -u

ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"
exec bash "$ROOT/bh.sh" "$@"
