# 百花服务 K8s 部署指南

> **架构状态（commit `aa053f1` 三服务合一）**：后端已从 `bh-family`(8788) + `bh-vault`(8790) + `bh-ai`(8791)
> 三个服务合并为**唯一后端 `bh-server`(8788)**（家庭 / AI / 知识库三模块同一进程 + 一个 `baihua` 数据库），
> WebUI(`bh-webui` 5177) 与推理(`bh-openvino` OVMS :8000) 不变。
> 本文中出现的 `bh-family`/`bh-ai`/`bh-vault`/8790/8791 只作为**合并前的历史**说明保留。

## 架构概览

```
┌─────────────────────────────────────────────────────────────────┐
│                    K8s Cluster (baihua namespace)                │
│                                                                  │
│  ┌──────────┐     ┌──────────┐     ┌──────────────────────┐     │
│  │ Traefik  │────▶│ bh-webui │────▶│ bh-server            │     │
│  │ :80      │     │ :5177    │     │ :8788 唯一后端        │     │
│  │ Ingress  │     │ Blazor   │     │ 家庭/AI/知识库 三模块 │     │
│  │ Route    │     │ Server   │     │ /mg/* /api/* /vault/* │     │
│  └────┬─────┘     └──────────┘     └──────┬───────────────┘     │
│       │  API 路由（/mg /api /vault /mcp 等）│ HTTP                │
│       └───────────────────────────────────┘                     │
│                   ┌───────────┐    ┌──────▼──────────────┐      │
│                   │bh-postgres│    │ bh-openvino         │      │
│                   │ :5432     │    │ :8000 OVMS REST     │      │
│                   │ 单库baihua│    │ (OVMS+Intel GPU)    │      │
│                   └───────────┘    └─────────────────────┘      │
│                                                                  │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐        │
│  │ data PVC │  │ logs PVC │  │ vaults   │  │ models   │        │
│  │ 10Gi     │  │ 5Gi      │  │ 50Gi     │  │ 50Gi     │        │
│  └──────────┘  └──────────┘  └──────────┘  └──────────┘        │
│                                              ↑ ↑                 │
│                                  RW(OpenVINO) RO(server scan)    │
└─────────────────────────────────────────────────────────────────┘
```

## 文件清单

| 文件 | 说明 |
|------|------|
| `00-namespace.yaml` | 命名空间 |
| `01-configmap.yaml` | 共享配置（单库 `PG_DATABASE`、`BaihuaServer__BaseUrl`、OpenVINO 服务 URL） |
| `02-secret.yaml` | 敏感配置（密码、密钥） |
| `03-pvc.yaml` | 持久化存储（data/logs/vaults/models） |
| `10-intel-gpu-plugin.yaml` | Intel GPU Device Plugin DaemonSet |
| **`20-server.yaml`** | **bh-server Deployment + Service（唯一后端 8788：家庭 / AI / 知识库三模块同一进程）** |
| **`22a-openvino.yaml`** | **bh-openvino Deployment + Service = Intel OVMS（OpenAI 兼容 /v3 推理：LLM / 视觉 / 嵌入，REST :8000）** |
| `23-webui.yaml` | bh-webui Deployment + Service |
| `24-traefik.yaml` | Traefik IngressRoute + Middleware（统一入口 :80，替代 nginx） |
| **`25-postgres.yaml`** | **bh-postgres Deployment + Service + PV/PVC/Secret（单库 `baihua`）** |
| `deploy.sh` | 一键部署脚本 |
| `./images/Dockerfile.openvino-server` | OpenVINO 推理容器 = 官方 `openvino/model_server` 镜像别名（bh build openvino 产出 bh-openvino:latest） |
| `./images/Dockerfile.server` | 唯一后端容器（合并前为 Dockerfile.family / .ai / .vault 三份） |
| `./images/Dockerfile.webui` | WebUI 容器（多阶段源码构建，容器内 publish） |

> 合并前存在 `20-vault.yaml` / `21-ai.yaml` / `22-family.yaml` 与对应三个 Dockerfile，已随合并删除；
> `25-postgres.yaml` 此前从未被任何部署路径 apply（已在 `deploy.sh` / `bh.sh` 中修好）。

## 架构设计：OpenVINO 独立容器

### 为什么拆分？

| 维度 | 之前（嵌入后端进程） | 之后（独立容器） |
|------|---------------------|------------------|
| 后端镜像大小 | ~3GB（.NET + Python + OpenVINO） | ~800MB（.NET only） |
| GPU 资源 | 绑定在后端 Pod | 仅 OpenVINO Pod |
| 升级 OpenVINO | 需重建后端镜像 | 独立重建 OpenVINO 镜像 |
| 扩缩容 | 后端 + GPU 一起扩 | 可独立扩 OpenVINO |
| 代码改动 | — | 推理对接 OVMS OpenAI 兼容 /v3 接口 |

### 通信方式（Intel OVMS）

自 777f860 起，百花本地 OpenVINO 推理由 **Intel OVMS（OpenVINO Model Server，官方 `openvino/model_server` 镜像）** 统一承载，
不再运行自研 Python 服务（`openvino_llm_server.py` / `vision_server.py` 已弃用）。唯一后端 bh-server 通过 OpenAI 兼容 REST 调用：

- `http://bh-openvino:8000/v3/chat/completions` — LLM 对话（模型 id：`qwen2.5`）与视觉识别（`qwen2.5-vl-7b`）
- `http://bh-openvino:8000/v3/embeddings` — RAG 嵌入（`bge-small-zh`，原 22b-embedding 已并入 OVMS）
- `http://bh-openvino:8000/v1/models` — 状态 / 模型列表探测

配置：`OpenVinoOms__BaseUrl`（ConfigMap 注入 `http://bh-openvino:8000`）或环境变量 `OPENVINO_LLM_URL`。
OVMS 模型由 `config.json`（`model_config_list`，22a-openvino.yaml 内联 ConfigMap）注册；
每个模型目录的 `graph.pbtxt` 由 bh-openvino Pod 的 initContainer `ovms --configure` 启动时幂等生成（模型缺失则跳过）。

### 模型文件存储

```
PVC: baihua-models-pvc (50Gi, hostPath: /opt/baihua/models)
├── bh-openvino Pod:  挂载 /models (RW) — OVMS 读取模型 + initContainer 写 graph.pbtxt
└── bh-server Pod:    挂载 /opt/baihua/models (RO) — 模型扫描（UI 列表）
```

单节点用 RWO hostPath 即可；多节点需改为 NFS + RWX。

## 前提条件

依赖分两类：**bh 命令能自动安装的**（缺失时 build 自动下载安装）与**需手动安装的**（系统级/交互式，见下文各节）：

| 依赖 | 用途 | 自动安装 | 触发 |
|------|------|---------|------|
| nerdctl | k8s 镜像构建（直连 containerd） | ✅ `bh.sh build` 自动装（GitHub release → /usr/local/bin） | build 时 |
| buildkit（buildkitd+buildctl） | nerdctl build 的守护进程 + 客户端 | ✅ 同上，一次下载装两个 | build 时 |
| .NET SDK 10 | native 部署/构建（linux-native） | ✅ `bh.sh build` 自动装（dotnet-install.sh → ~/.dotnet） | build 时 |
| .NET SDK 10 | native 部署/构建（win-native/win-docker） | ✅ winget 自动装 | build 时 |
| k3s | K8s 运行时 | ❌ 需 root + 网络，手动装 | 见 1 |
| Traefik（`traefik.io/v1alpha1` CRD） | 统一入口（IngressRoute/Middleware，:80） | ❌ k3s 默认自带；禁用过需手动装 | 见 1 |
| Docker Desktop | 本机 docker build（deploy.sh 构建镜像用） | ❌ GUI 交互安装 | 见 4 |
| Intel GPU 驱动 | openvino GPU 推理 | ❌ 系统级 | 见 2 |

> k8s 镜像构建为**多阶段源码构建**：build 阶段在 `dotnet/sdk` 容器内现场 `dotnet publish`
> （依赖 nuget-local/ 离线包源，构建全程无需外网），宿主/发布机**无需安装 .NET SDK**，
> 也无需任何 dotnet publish 产物。

> 注意：buildkitd 自动安装后**不会自动启动**（无 systemd 环境）。首次 build 会提示启动命令。

### 1. K8s 集群

推荐 k3s（单节点、轻量、自带 containerd，不依赖 docker）：

```bash
# 方式 A: k3s（推荐）
curl -sfL https://get.k3s.io | sh -
```

> 生产集群（kubeadm / RKE / 云托管 K8s）同样用 containerd/CRI，无需 docker。

> **Traefik 是硬前提**：`24-traefik.yaml` 用的是 `traefik.io/v1alpha1` 的 `IngressRoute` / `Middleware`，
> 集群里没有这两个 CRD 时 kubectl apply 会直接报 `no matches for kind` 而失败
> （`bh deploy` 在基础清单阶段 `exit 1`）。k3s 默认自带 Traefik；若安装时用了 `--disable traefik`
> 或 addon 未起来，需自行部署 Traefik v3 后再 deploy。
> 校验：`kubectl get crd ingressroutes.traefik.io middlewares.traefik.io`

### 2. 节点 GPU 驱动

```bash
# 检查节点是否有 /dev/dri
ls -la /dev/dri/
# 应看到: renderD128, card0 等

# 检查 Intel GPU 驱动
lspci | grep -i vga
# 应看到 Intel 显卡设备

# 当前用户需能访问 GPU 渲染节点（非 root 运行推理时）
groups
# 若无 video/render 组，先加入（重新登录后生效）:
# sudo usermod -aG video,render $USER

# 安装 Intel GPU 运行时（Ubuntu/Debian）
# ⚠️ Ubuntu 26.04 起 Level Zero 包已改名，旧命令中的 level-zero-dev 会报"无法定位软件包"
sudo apt install -y intel-opencl-icd libze-dev libze-intel-gpu1 libigdgmm12
```

> **Ubuntu 26.04 包名变化**（旧教程的 `level-zero` / `level-zero-dev` / `intel-level-zero-gpu` 已不存在）：
>
> | 旧包名（≤24.04） | 26.04 新包名 | 说明 |
> |---|---|---|
> | `level-zero` | `libze1` | oneAPI Level Zero 运行时库（作为 libze-dev 依赖自动安装） |
> | `level-zero-dev` | `libze-dev` | Level Zero 开发文件（头文件） |
> | `intel-level-zero-gpu` | `libze-intel-gpu1` | Intel GPU 的 Level Zero 实现（Arc / UHD 计算必需） |
>
> `intel-opencl-icd` 必须一起装：它提供 **OpenCL ICD 注册**（`/etc/OpenCL/vendors/intel.icd` +
> `libigdrcl.so`），只装 `libze-intel-gpu1` 时 `clinfo` 会报 `Number of platforms 0`。
> 两者冲突仅针对旧版 `intel-opencl-icd`（`Breaks: << 23.26.26690.22-1`），当前版本可共存。

> **镜像源 403 问题（2026-08 实测）**：`cn.archive.ubuntu.com` 会 302 重定向到
> `mirrors.tuna.tsinghua.edu.cn`，tuna 对异常网段返回 **403 Forbidden**（反滥用拦截，
> 页面提示"您所在的网段近期向本站发送过异常请求"），导致 apt 下载全部失败。
> 已确认可用的国内镜像：`mirrors.aliyun.com` / `mirrors.ustc.edu.cn` / `mirrors.163.com` /
> `repo.huaweicloud.com`。
>
> 永久切换（编辑 `/etc/apt/sources.list.d/ubuntu.sources`）：
> ```bash
> # URIs 改为 http://mirrors.aliyun.com/ubuntu/（aliyun 同一路径下也镜像 -security 套件）
> # Suites: resolute resolute-updates resolute-backports resolute-security
> sudo apt update
> ```
> 临时源（不动系统配置，仅本次命令生效）：
> ```bash
> sudo apt-get -o Dir::Etc::sourcelist=/tmp/alt.sources -o Dir::Etc::sourceparts=- update
> sudo apt-get -o Dir::Etc::sourcelist=/tmp/alt.sources -o Dir::Etc::sourceparts=- install <pkg>
> ```

> **验证 GPU 可用**：
> ```bash
> clinfo | grep -E 'Number of platforms|Device Name'
> # 期望: Number of platforms 1，Device Name 为 Intel(R) UHD Graphics / Arc 等
> ```

> **GPU 后端覆盖**：镜像已内置推理所需的 GPU 运行时（无需额外配置）：
> - **OpenVINO**（bh-openvino 容器）：intel-opencl-icd（NEO）→ /dev/dri（真机）或 /dev/dxg（WSL2）
>
> 合并后本地推理统一由 bh-openvino（OVMS）承载：唯一后端 bh-server 是纯 .NET HTTP 服务，
> 不需要 GPU，也不再内置 LlamaSharp / ONNX 后端（相关依赖已随合并移除）。

### 3. 构建依赖（自动安装）

`bh.sh build` 会自动下载安装缺失的 nerdctl 与 buildkit（buildkitd 守护进程 + buildctl 客户端，官方 GitHub release → /usr/local/bin，需 root/sudo）。
k8s 镜像构建**不需要** .NET SDK（容器内构建，见上表说明）；dotnet 仅 native 部署用，由 `bh-linux-native.sh build` 自动装到 ~/.dotnet。无需手动装：

```bash
# 验证（已安装时）
nerdctl --version      # 2.3.5+
buildkitd --version    # 0.32.x
buildctl --version     # 0.32.x（nerdctl build 需要）
dotnet --version       # 10.0+（仅 native 部署需要，k8s 构建不需要）
```

> buildkitd 是 nerdctl build 的后端守护进程，安装后需运行。`bh.sh build` 检测到 buildkitd 未运行时按环境给指引：
>
> - **systemd 环境（Ubuntu Server 等，默认）**：脚本自动写入 `/etc/systemd/system/buildkit.service`
>   （GitHub release 的 buildkit **不带** systemd 单元文件），然后执行：
>   ```bash
>   sudo systemctl enable --now buildkit
>   # 状态: systemctl status buildkit   /   日志: journalctl -u buildkit -f
>   ```
> - **无 systemd（WSL、容器等）**：手动 nohup 启动：
>   ```bash
>   nohup buildkitd -config /etc/buildkit/buildkitd.toml > /tmp/buildkitd.log 2>&1 &
>   ```
>
> `bh.sh build` 会自动生成 buildkitd.toml（daocloud 镜像加速 + k8s.io namespace，**禁用 OCI worker**）、
> `/etc/rancher/k3s/registries.yaml`（k3s 拉镜像走 daocloud，解决 pause/nginx 直连 docker.io 超时）
> 和 buildkit.service 单元；写 `/etc` 下配置时自动走 sudo（无需先 `sudo bh build`）。
> 已存在的配置文件不会被覆盖（幂等）。
>
> ⚠️ **两个"重启后生效"的坑（2026-08 实测）**：
> 1. **k3s 的 registries.yaml**：k3s 启动时读取该文件，**写入后必须 `sudo systemctl restart k3s`**，
>    否则 k3s 系统镜像（如 `rancher/mirrored-pause`）仍直连 docker.io → 超时，全部 Pod 卡 ContainerCreating。
> 2. **buildkitd 必须禁用 OCI worker**：buildkitd.toml 中 `[worker.oci] enabled = false` 必不可少。
>    否则 nerdctl build 走默认的 OCI(runc) worker，`-o type=image` 导出的镜像进不了 k3s containerd 的
>    k8s.io namespace，后续 `FROM bh/base-runtime:latest` 等本地镜像解析失败 → 回退去 docker.io 拉 →
>    daocloud 镜像返回 403（不在白名单）。现象：`n images` 看不到刚构建的镜像。
>
> ⚠️ **权限注意**：k3s 的 containerd socket（`/run/k3s/containerd/`）与 `k3s.yaml` 仅 root 可访问，
> 因此 **build/deploy/status 建议整体用 `sudo bh <cmd>` 执行**（sudo 下脚本内部写配置逻辑同样正确）。

> 镜像构建用 `nerdctl -a /run/k3s/containerd/containerd.sock build`，构建完直接进入 k3s 的 containerd，
> 无需 docker，也无需 load/import。日常入口：`../tools/bh/linux/k8s/bh.sh`（build/up/status/logs）。

> **容器系统版本（2026-08 起）**：全部容器基于 Ubuntu 26.04 —— .NET 服务用
> `mcr.microsoft.com/dotnet/aspnet:10.0-resolute`（sdk-offline 用 `sdk:10.0-resolute`），
> OpenVINO 容器用 `ubuntu:26.04`（Intel NEO 源仍指 noble，26.04 上兼容）。
> **OpenVINO 版本**：`2026.3.0`（openvino-genai 2026.3.0.0），Dockerfile.openvino-server 锁定。

### 4. Docker Desktop（仅 Windows docker 部署用）

`bh-win-docker.ps1` 需要 Docker Desktop。需 GUI 交互安装（无法自动完成）：

```powershell
winget install --id Docker.DockerDesktop
# 或手动下载: https://www.docker.com/products/docker-desktop/
# 安装后启动 Docker Desktop，等待引擎就绪（docker info 可用）
```

> Linux k8s 部署**不需要** docker（nerdctl 直连 containerd）。Windows 纯 native 部署（`bh-win-native.ps1`）也不需要。

## 部署步骤

### 一键部署

```bash
cd k8s
chmod +x deploy.sh
./deploy.sh all
```

### 分步部署

#### 1. 构建镜像

```bash
./deploy.sh build
```

构建 3 个镜像（另有外部镜像 `postgres:18-alpine`，由 k3s 直接拉取）：
- `bh-server:latest` — **唯一后端（家庭 / AI / 知识库三模块同一进程，无 OpenVINO）**
- `bh-webui:latest` — WebUI（Blazor Server）
- `bh-openvino:latest` — **Intel OVMS 推理服务（官方 `openvino/model_server` 镜像别名，CPU+GPU+NPU）**

#### 2. 加载镜像到集群

```bash
# k3s（推荐）：nerdctl 构建直接进 k3s containerd，无需 load
# minikube（可选）：./deploy.sh load
```

#### 3. 填写 Secret

编辑 `02-secret.yaml`，填入实际值：

```bash
# 移动端密钥
openssl rand -hex 32

# 加密密钥
openssl rand -base64 32
```

> 管理员密码合并后经管理面板 / 管理 API 设置，`ADMIN_PASSWORD_HASH` 是历史遗留键（可留空）。

#### 4. 部署

```bash
./deploy.sh deploy
```

### GPU 按需部署（只有 Intel GPU 才启动 OpenVINO 服务）

`deploy` / `bh up` 会自动探测节点是否有 Intel GPU，**有才部署** `10-intel-gpu-plugin`（kube-system）与 `22a-openvino`：

- 探测顺序：`BAIHUA_ENABLE_OPENVINO` 环境变量开关 → WSL2 GPU-PV（内核含 microsoft 且 `/dev/dxg` 为字符设备）→ 真机 `/dev/dri` 渲染节点 + `lspci` 厂商为 Intel
- ⚠️ 不把 `/dev/dxg` 存在当 WSL2 依据：k8s 的 `hostPath type: DirectoryOrCreate`（22a-openvino.yaml 的 dxg 挂载）会在**原生 Linux** 宿主机上自动建出空的 `/dev/dxg` 目录，只有字符设备（`-c`）才是真实的 WSL2 GPU-PV
- 无 Intel GPU 时跳过这两个清单；若之前部署过，自动停掉（`bh-openvino` 缩容至 0、`intel-gpu-plugin` 删除），避免无 GPU 节点上空转/崩溃循环
- bh-server 对远程 OpenVINO 有 5 秒超时优雅降级，无 openvino 时服务照常健康运行，仅 AI 推理功能不可用

显式强制开关（跳过自动探测）：

```bash
BAIHUA_ENABLE_OPENVINO=1 ./deploy.sh deploy   # 无 GPU 也强制部署（不推荐）
BAIHUA_ENABLE_OPENVINO=0 ./deploy.sh deploy   # 有 GPU 也强制跳过
```

运行时按需启停（`tools/bh/linux/k8s/bh.sh`，清单保留、随时可恢复）：

```bash
sudo bh openvino status     # 探测结果 + bh-openvino / intel-gpu-plugin 状态 + 节点 GPU 资源
sudo bh openvino off        # 停止：bh-openvino 缩容至 0，intel-gpu-plugin 删除
sudo bh openvino on         # 启动：重新 apply 两个清单并恢复副本数（无 GPU 时拒绝，除非 BAIHUA_ENABLE_OPENVINO=1）
```

> 部署完成后打开管理面板：
> ```bash
> bh dashboard              # 普通用户：自动带 cli-token 打开默认浏览器
> sudo bh dashboard         # root 无桌面授权，会打印带 token 的 URL，复制到浏览器即可
> ```

#### 5. 下载模型

> ⚠️ **Ubuntu 24.04+ 禁止系统级 pip（PEP 668）**：`pip install optimum[openvino]` 会报
> `externally-managed-environment`。必须用 venv（或 pipx）；国内网络 `huggingface.co` 直连不通，
> 需走 `hf-mirror.com` 镜像（`HF_ENDPOINT`）。

```bash
# 在节点上创建模型目录
sudo mkdir -p /opt/baihua/models

# ── 方式 A（推荐）：直接下载预转换 OpenVINO 模型（无需 pip）──
sudo apt install -y git-lfs && git lfs install
sudo git clone https://hf-mirror.com/OpenVINO/qwen2-vl-7b-instruct-int4-ov /opt/baihua/models/Qwen2.5-VL-7B-Instruct-int4-ov

# ── 方式 B：venv + optimum-cli 现场转换（模型名可换，如 Qwen2.5-VL-7B）──
python3 -m venv ~/.venvs/optimum
~/.venvs/optimum/bin/pip install -U pip optimum[openvino]
export HF_ENDPOINT=https://hf-mirror.com
~/.venvs/optimum/bin/optimum-cli export openvino \
    --model Qwen/Qwen2.5-VL-7B-Instruct --task image-text-to-text --weight-format int4 \
    /opt/baihua/models/Qwen2.5-VL-7B-Instruct-int4-ov
```

OVMS（`config.json`）注册的 3 个模型目录（22a-openvino.yaml 内联 ConfigMap），缺哪个 OVMS 就加载不了哪个：

| OVMS 模型 id | 目录（/opt/baihua/models/） | 任务 / 设备 | 下载 |
|---|---|---|---|
| `qwen2.5` | `Qwen2.5-7B-Instruct-int4-ov` | 对话 / GPU | `git clone https://hf-mirror.com/OpenVINO/Qwen2.5-7B-Instruct-int4-ov` |
| `qwen2.5-vl-7b` | `Qwen2.5-VL-7B-Instruct-int4-ov` | 视觉 / GPU | `git clone https://hf-mirror.com/OpenVINO/Qwen2.5-VL-7B-Instruct-int4-ov`（方式 A） |
| `bge-small-zh` | `bge-small-zh-v1.5` | 嵌入 / CPU | gated（或方式 B 转换 BAAI/bge-small-zh-v1.5） |

> 下载/转换完成后，`bh-openvino` Pod 会自动恢复（initContainer 会为已存在的模型目录生成 graph.pbtxt；
> 若模型目录缺失则跳过，OVMS 只加载已注册且可用的 servable）。可用 `sudo bh status` 确认。

#### 6. 验证 GPU + OpenVINO

```bash
./deploy.sh verify-gpu
```

预期输出：
```
1. Device Plugin 已部署
2. 节点有 1 个 Intel GPU
3. bh-openvino Pod OVMS 版本 ...（ovms --version）
4. bh-openvino Ready（/v2/health/ready 通过）+ /v1/models 模型列表
5. bh-server → OVMS 连通性: OK
```

## 验证部署

```bash
# 查看状态
./deploy.sh status

# 访问 WebUI（统一入口 Traefik :80，dashboard 命令会自动打开）
# http://<节点IP>/  或  http://lumin-ubuntu.local/

# 移动端（花记）入口：默认 80 端口，无显式端口号
# http://<节点IP>/          ← Traefik :80，/mg/* /api/* /vault/* /mcp 走 bh-server
# 配对二维码默认携带 Baihua:PublicBaseUrl（ConfigMap，如 http://192.168.3.13），不再带 :8788

# 当前仅 HTTP(:80)；HTTPS 留待以后上公网时启用
# （Let's Encrypt 需域名 + 公网可达，届时把 IngressRoute 复制一份加 websecure + tls）

# 查看日志
./deploy.sh logs bh-server 100
./deploy.sh logs bh-openvino 100
```

## 百花服务器互联（双服务器互发消息）

WebUI 侧边栏「服务器互联」页面（`/server-messages`）：登记其它百花服务器 → 点击打开对话 → 互发消息，接收方实时可见（5s 轮询）。

**工作原理**：发送方 bh-server 将消息 HTTP 推送到对方 `/mg/server-msg/inbox`（`X-Server-Token` 鉴权），接收方落库后 WebUI 轮询展示。

**两台机器部署配置**（`20-server.yaml` env，各自机器改自己的值）：

| 环境变量 | 说明 |
|---|---|
| `BAIHUA_SERVER_MSG_TOKEN` | 共享口令，**两台机器配成相同值**（留空则不鉴权，仅限可信局域网） |
| `BAIHUA_HOST_IP` | **k8s 自动注入**（下行 API `status.hostIP`），无需手动配置 |
| `BAIHUA_SERVER_PUBLIC_BASE_URL` | 可选覆盖（入口不在 80 或想用域名时配置） |

**使用**：WebUI → 服务器互联 → 「添加」→ 填对方名称 + 地址（`http://<对方节点IP>/`）+ 口令 → 打开对话收发。

**局域网自动发现**：bh-server 每 30s 在 UDP 45678 广播自身身份并监听，自动登记同网段其它百花服务器（Source=lan）。

> ⚠️ **实测限制（2026-08）**：**k8s 容器收不到局域网 UDP 广播**（Pod 网络隔离，只收到自身广播回环）。
> - **native 服务器 → 能自动发现 k8s 服务器**（native 收广播；k8s 广播经节点出口可达）
> - **k8s 服务器 → 不能自动发现 native/其它机器**，需在 WebUI 手动添加对方
> - 广播必须携带正确入口：**自动探测**——k8s 经下行 API 注入节点 IP（入口 traefik :80）；
>   native 自动探测本机 IP + Kestrel 端口；特殊入口再用 `BAIHUA_SERVER_PUBLIC_BASE_URL` 覆盖

## Intel GPU 配置详解

### Device Plugin 工作原理

1. `intel-gpu-plugin` DaemonSet 在每个节点运行
2. 扫描 `/dev/dri/renderD128` 等设备
3. 向 K8s 注册 `intel.com/gpu` 扩展资源
4. Pod 声明 `resources.limits.intel.com/gpu: 1` 时，自动挂载 GPU 设备

### bh-openvino Pod GPU 访问链路

```
Pod (bh-openvino)
├── /dev/dri/renderD128  ← Intel GPU Device Plugin 自动挂载（WSL2 另挂 /dev/dxg）
├── initContainer: ovms --configure
│   └── 为 /models 下已下载模型生成 graph.pbtxt（幂等，缺失跳过）
├── ovms (OpenVINO Model Server) ← OpenAI 兼容推理服务 (:8000 REST / :9000 gRPC)
│   ├── config.json (ConfigMap) ← model_config_list 注册 3 个 servable
│   │   ├── qwen2.5         → /models/Qwen2.5-7B-Instruct-int4-ov       (GPU)
│   │   ├── qwen2.5-vl-7b   → /models/Qwen2.5-VL-7B-Instruct-int4-ov    (GPU)
│   │   └── bge-small-zh    → /models/bge-small-zh-v1.5                 (CPU)
│   └── /v3/chat/completions · /v3/embeddings · /v1/models
└── /models/ ← PVC 挂载（模型文件）
```

### bh-server Pod（唯一后端，无 GPU）

```
Pod (bh-server)
├── .NET 服务（家庭 / AI / 知识库 三模块同一进程）
│   ├── OpenVinoOms__BaseUrl=http://bh-openvino:8000 (ConfigMap)
│   ├── OpenVinoChatInference / OpenVinoVisionService
│   │   └── /v3/chat/completions → OVMS（对话 qwen2.5 · 视觉 qwen2.5-vl-*）
│   ├── 嵌入（RAG）→ /v3/embeddings（bge-small-zh）
│   ├── OpenVinoToolService 探测
│   │   └── OPENVINO_LLM_URL/v1/models → OVMS 是否托管模型（200 + data[] 非空）
│   └── 模型扫描 → /opt/baihua/models/ (RO PVC) → UI 模型列表
└── /opt/baihua/models/ ← PVC 只读挂载（模型扫描）
```

> 合并前这里是 `bh-family`(8788) / `bh-ai`(8791) / `bh-vault`(8790) 三个 Pod（+ 三个数据库）——**历史说明**；
> 现在是一个 Pod、一个 `baihua` 库，模块间不再走 HTTP。

## 构建与部署提速

### 常见瓶颈：docker.io 解析黑洞

buildkit 每次构建解析 `FROM` 基础镜像时会直连 `registry-1.docker.io`（`buildkitd.toml` 的 mirror 配置不参与该解析），
国内网络下 docker.io CDN 常被黑洞（SYN 丢弃），每次连接超时 30s×2，单次构建凭空多花 60-150s。

**修复（本机已应用）**：在 `/etc/hosts` 钉死 docker.io，让解析瞬时失败、buildkit 立即回退本地镜像存储：

```
127.0.0.1 registry-1.docker.io auth.docker.io
```

修复前空 Dockerfile 构建 144s → 修复后 7s（约 20 倍）。如需从 docker.io 拉取真实镜像，删除这两行并 `sudo systemctl restart buildkit`。

### 缓存并发安全

各服务 Dockerfile 的 NuGet 缓存挂载已加 `sharing=locked`：并发构建不会互相写坏 `/root/.nuget/packages`（此前并行构建曾导致 restore/publish 报 NETSDK1064 / 缺 .vdm 文件）。

### 构建与镜像范围

- `bh up` / `bh update` 始终**全量重建**两个 .NET 应用镜像（`server` + `webui`）再 deploy：
  不做源码变更检测（曾用变更检测决定重建哪些，导致"部署镜像与源码脱节、标注撒谎"这类误导）
- `bh build server webui` 手动指定只构建部分镜像；`bh build` 不带参数 = server + webui + openvino
- `bh prune` 清空 buildkit 缓存（释放磁盘、修复缓存损坏）；缓存膨胀到百 GB 级时建议定期执行

### 免 sudo 状态查看

`bh status` / `bh logs` / `bh dashboard` 在 k3s 配置（/etc/rancher/k3s/k3s.yaml，root 600）不可读时自动提权，普通用户可直接使用；`build`/`deploy`/`update` 仍建议 sudo。

## 与 Docker Compose 对比

| 维度 | Docker Compose | K8s |
|------|---------------|-----|
| 网络 | host (Linux) / bridge (Windows) | Service DNS |
| 后端进程 | 1（`server` 8788）+ `webui` 5177 | 1（`bh-server` 8788）+ `bh-webui` 5177 |
| GPU 访问 | `--gpus all` (仅 NVIDIA) | `intel.com/gpu` (Device Plugin) |
| OpenVINO | ❌ WSL2 不支持 Intel GPU | ✅ 原生 Linux |
| OpenVINO 架构 | 独立 `openvino` 容器（`--profile inference`） | 独立 bh-openvino 容器（Intel OVMS，OpenAI 兼容 /v3） |
| 数据库 | `postgres:18-alpine` 单库 `baihua` | bh-postgres（`postgres:18-alpine` 单库 `baihua`） |
| 持久化 | bind mount | PVC |
| 配置 | .env 文件 | ConfigMap + Secret |
| 反向代理 | nginx container (host net) | Traefik IngressRoute (:80, svclb 绑定) |
| 扩缩容 | 手动 | `kubectl scale` |
| 自愈 | restart: unless-stopped | K8s 自动重启 + 健康检查 |
| 滚动更新 | 手动重建 | `kubectl set image` + RollingUpdate |

## 故障排查

### WebUI 白屏（blazor.web.js 404）

**现象**：dashboard 打开后白屏，webui Pod 日志持续 `GET /_framework/blazor.web.js 404`。

**根因（2026-08 实测）**：Blazor 框架静态资源 `_framework/blazor.web.js`、`blazor.server.js` 由
NuGet 包 `Microsoft.AspNetCore.App.Internal.Assets` 提供，该包由 AspNetCore 框架引用声明。
SDK 10.0.100 的 targeting pack **不声明**此包 → 容器内 `dotnet publish` 不产出 `_framework/`；
本地 SDK 10.0.110（dev 环境）正常。浮动标签 `sdk:10.0` 会漂到 10.0.400 同样缺失。

**修复**（已在 `Baihua.Web.csproj` 落地）：显式引用该包，任何 SDK 下 publish 都会生成 _framework：
```xml
<PackageReference Include="Microsoft.AspNetCore.App.Internal.Assets" Version="10.0.10" PrivateAssets="all" />
```
另外 `Dockerfile.sdk-offline` 已把 SDK 固定为 `10.0.100`（与运行时 aspnet 10.0.x 同波段，避免浮动漂移）。

> 排查技巧：容器与本地 publish 对比 `find /publish/wwwroot` 是否有 `_framework/`；
> 产物里 `bh-webui.staticwebassets.endpoints.json` 若不含 blazor 条目即该包缺失。

### Pod 无法调度（GPU 不足）

```bash
kubectl -n baihua describe pod -l app=bh-openvino
# 如果看到 "0/1 nodes are available: 1 Insufficient intel.com/gpu"
# 说明 Device Plugin 未注册 GPU，检查:
kubectl -n kube-system get pods -l app=intel-gpu-plugin
kubectl get nodes -o custom-columns=NAME:.metadata.name,GPU:.status.capacity.'intel\.com/gpu'
```

### OpenVINO 检测不到 GPU

> 先在本机（容器外）验证 GPU 驱动可用，排除宿主机问题：
> ```bash
> clinfo | grep -E 'Number of platforms|Device Name'   # 应为 1 个平台 + Intel GPU 设备
> groups                                              # 运行用户需在 video/render 组（否则见上文「节点 GPU 驱动」）
> ```
> 若宿主机 `Number of platforms 0`，按上文「节点 GPU 驱动」安装 `intel-opencl-icd`（提供 OpenCL ICD 注册）。

```bash
# 进入 OpenVINO Pod 检查（官方 OVMS 镜像无 python，用 ovms 与系统工具）
kubectl -n baihua exec -it deployment/bh-openvino -- bash

# 检查设备节点
ls -la /dev/dri/
# 应看到 renderD128（WSL2 为 /dev/dxg）

# 检查 OVMS 版本与模型列表
ovms --version
curl -s http://localhost:8000/v2/health/ready   # 200 = 就绪（若镜像无 curl，从 bh-server 侧探测）
```

### bh-server 无法连接 OpenVINO

```bash
# 检查 Service
kubectl -n baihua get svc bh-openvino
# 应有 Endpoints

# 从 bh-server Pod 测试连通性（OVMS 不提供 /health，用 /v1/models 或 /v2/health/ready）
kubectl -n baihua exec deployment/bh-server -- curl -s http://bh-openvino:8000/v1/models
kubectl -n baihua exec deployment/bh-server -- curl -s http://bh-openvino:8000/v2/health/ready
```

### 模型文件缺失

```bash
# 检查 OpenVINO Pod 的模型目录
kubectl -n baihua exec deployment/bh-openvino -- ls -la /models/

# 检查 bh-server Pod 的模型目录（只读）
kubectl -n baihua exec deployment/bh-server -- ls -la /opt/baihua/models/

# 如果为空，需要将模型上传到节点的 /opt/baihua/models/
scp -r Qwen2.5-VL-7B-Instruct-int4-ov user@node:/opt/baihua/models/
```

### bh-postgres 未就绪 / bh-server 连不上数据库

```bash
# 仓库只有一个数据库 baihua（POSTGRES_DB=baihua）
kubectl -n baihua get deploy,pod -l app=bh-postgres
kubectl -n baihua logs deploy/bh-postgres | tail -30
# Pod 若长期 Pending，检查 PV/PVC 是否绑定（hostPath /opt/baihua/postgres）
kubectl -n baihua get pv,pvc
```

### Traefik 502 / 路由不达

```bash
# 检查 IngressRoute 与后端服务
kubectl -n baihua get ingressroute,middleware
kubectl -n baihua get svc
# 确保 bh-server 和 bh-webui Service 有 Endpoints
kubectl -n baihua get endpoints
```

## 生产环境建议

1. **StorageClass**：替换 hostPath 为网络存储（NFS / Ceph / 云盘）
2. **HPA**：对 bh-webui 配置 HorizontalPodAutoscaler
3. **Ingress**：替换 NodePort 为 Ingress + TLS 证书
4. **监控**：部署 DCGM exporter + Prometheus（Intel GPU 指标）
5. **日志**：Fluentd/Fluent Bit 收集到 OpenObserve
6. **镜像仓库**：使用 Harbor / ACR 推送镜像，避免 `imagePullPolicy: IfNotPresent`
7. **OpenVINO 扩缩容**：多 GPU 节点时可 `kubectl scale deployment/bh-openvino --replicas=N`
