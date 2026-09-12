#!/bin/bash
# bh - baihua 统一 CLI 入口（Linux）
#
# 部署形态只有一种：Linux k3s（containerd + nerdctl 构建）——tools/bh/linux/k8s/bh.sh。
# 合并为单进程 + 单库后不再需要 native / docker 两套 cell 脚本。
#
# 用法:
#   bh <command> [args]          直接执行（可选写 bh k8s <command> 兼容旧习惯）
#   bh install                   复制自包含定位器到 PATH（~/.local/bin/bh 或 /usr/local/bin/bh）
#   bh uninstall                 移除定位器
set -u

# 用 readlink -f 解析软链，确保通过 ~/.local/bin/bh 软链调用时 ROOT 指向真实目录
ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"

case "${1:-}" in
    k8s)
        shift
        exec "$ROOT/linux/k8s/bh.sh" "$@"
        ;;
    install)
        if [ "$(id -u)" = "0" ]; then
            # root 安装：复制自包含定位器到 /usr/local/bin（sudo secure_path 默认包含，sudo bh 与普通用户 bh 都可用）
            install -m 0755 "$ROOT/locator.sh" /usr/local/bin/bh && \
                echo "[install] 已安装: /usr/local/bin/bh（自包含定位器，目录改名/移动后无需重装）"
            echo "[install] /usr/local/bin 在 sudo secure_path 内，bh 与 sudo bh 均直接可用"
        else
            bin="${HOME}/.local/bin"
            if ! mkdir -p "$bin" 2>/dev/null; then
                echo "[install] 无法创建 $bin（权限不足）"
                echo "         请用 root 执行: sudo bash $ROOT/bh.sh install  （或 WSL: wsl -u root）"
                exit 1
            fi
            install -m 0755 "$ROOT/locator.sh" "$bin/bh" && echo "[install] 已安装: $bin/bh（自包含定位器，目录改名/移动后无需重装）"
            case ":$PATH:" in
                *":$bin:"*) echo "[install] ~/.local/bin 已在 PATH，直接可用: bh <command>" ;;
                *)
                    if grep -q 'local/bin' "${HOME}/.bashrc" 2>/dev/null; then
                        echo "[install] ~/.local/bin 已配置在 ~/.bashrc（重新登录或 source ~/.bashrc 后生效）"
                    else
                        echo "export PATH=\"$bin:\$PATH\"" >> "${HOME}/.bashrc" && \
                            echo "[install] 已把 ~/.local/bin 追加到 ~/.bashrc（重新登录或 source ~/.bashrc 后生效）"
                    fi
                    ;;
            esac
            echo "[install] 定位器自动查找: \$BAIHUA_HOME > 常见路径 > 当前目录向上；仓库改名/移动后无需重新安装"
        fi
        ;;
    uninstall)
        rm -f "${HOME}/.local/bin/bh" 2>/dev/null
        rm -f /usr/local/bin/bh 2>/dev/null
        echo "[uninstall] 已移除 bh（~/.local/bin/bh、/usr/local/bin/bh；无权限删除的请用 root）"
        ;;
    help|-h|--help|"")
        echo "bh - baihua 统一 CLI（Linux）"
        echo ""
        echo "用法:"
        echo "  bh <command> [args]           执行命令（k8s 部署形态）"
        echo "  bh k8s <command> [args]       同上（显式写 cell，兼容旧习惯）"
        echo "  bh install / uninstall        安装到 PATH（root→/usr/local/bin，普通用户→~/.local/bin）/ 移除"
        echo "                                安装的是自包含定位器（非软链），仓库改名/移动后无需重装"
        echo ""
        echo "提示: k8s cell 需要 root（k3s 配置 /etc/rancher/k3s/k3s.yaml 仅 root 可读），WSL 下用 wsl -u root"
        echo "cell 内可用命令: bh <cell> help"
        ;;
    *)
        exec "$ROOT/linux/k8s/bh.sh" "$@"
        ;;
esac
