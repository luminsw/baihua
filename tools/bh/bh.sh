#!/bin/bash
# bh - baihua CLI 入口（Linux）—— k3s（全容器化）形态
#
# Linux 上部署形态只有一种：k3s（containerd + nerdctl 构建）→ tools/bh/linux/k8s/bh.sh。
# 命令名与 Windows 侧对齐：k3s = bh-k3s，native = bh（仅 Windows 有）。
# 本脚本与 bh-k3s.sh 完全等价（同一实现的两个名字），保留 bh 以兼容既有文档与习惯。
#
# 用法:
#   bh <command> [args]          执行 k3s 命令（= bh-k3s <command>）
#   bh install                   复制自包含定位器到 PATH（装 bh 与 bh-k3s）
#   bh uninstall                 移除两个定位器
set -u

# 用 readlink -f 解析软链，确保通过 ~/.local/bin/bh 软链调用时 ROOT 指向真实目录
ROOT="$(cd "$(dirname "$(readlink -f "${BASH_SOURCE[0]}")")" && pwd)"

case "${1:-}" in
    k8s)
        echo "[bh] 'k8s' 不再是子命令：Linux 上 bh 本身就是 k3s 形态。" >&2
        echo "     请改用: bh ${*:2}   （Windows 侧请用: bh-k3s ${*:2}）" >&2
        exit 1
        ;;
    install)
        # 两个命令名装同一份自包含定位器（locator.sh 按调用名转发），装哪个入口都会装齐两个
        if [ "$(id -u)" = "0" ]; then
            install -m 0755 "$ROOT/locator.sh" /usr/local/bin/bh && \
                install -m 0755 "$ROOT/locator.sh" /usr/local/bin/bh-k3s && \
                echo "[install] 已安装: /usr/local/bin/bh 与 /usr/local/bin/bh-k3s（自包含定位器，目录改名/移动后无需重装）"
            echo "[install] /usr/local/bin 在 sudo secure_path 内，bh/bh-k3s 与 sudo 调用均直接可用"
        else
            bin="${HOME}/.local/bin"
            if ! mkdir -p "$bin" 2>/dev/null; then
                echo "[install] 无法创建 $bin（权限不足）"
                echo "         请用 root 执行: sudo bash $ROOT/bh.sh install  （或 WSL: wsl -u root）"
                exit 1
            fi
            install -m 0755 "$ROOT/locator.sh" "$bin/bh" && \
                install -m 0755 "$ROOT/locator.sh" "$bin/bh-k3s" && \
                echo "[install] 已安装: $bin/bh 与 $bin/bh-k3s（自包含定位器，目录改名/移动后无需重装）"
            case ":$PATH:" in
                *":$bin:"*) echo "[install] ~/.local/bin 已在 PATH，直接可用: bh <command> / bh-k3s <command>" ;;
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
        rm -f "${HOME}/.local/bin/bh" "${HOME}/.local/bin/bh-k3s" 2>/dev/null
        rm -f /usr/local/bin/bh /usr/local/bin/bh-k3s 2>/dev/null
        echo "[uninstall] 已移除 bh / bh-k3s（~/.local/bin、/usr/local/bin；无权限删除的请用 root）"
        ;;
    help|-h|--help|"")
        echo "bh - baihua CLI（Linux，k3s 形态；等价命令: bh-k3s）"
        echo ""
        echo "用法:"
        echo "  bh <command> [args]           执行 k3s 命令（无参数 = status）"
        echo "  bh install / uninstall        安装（bh + bh-k3s）到 PATH / 移除"
        echo "                                安装的是自包含定位器（非软链），仓库改名/移动后无需重装"
        echo ""
        echo "常用: status / build / up / update / deploy / logs <svc> / openvino / destroy"
        echo "提示: k3s 需要 root（k3s 配置 /etc/rancher/k3s/k3s.yaml 仅 root 可读），WSL 下用 wsl -u root"
        echo "命令详情: bh help"
        ;;
    *)
        exec "$ROOT/linux/k8s/bh.sh" "$@"
        ;;
esac
