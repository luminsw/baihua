# 恢复 Native Cell 计划

## 背景

WSL2 + k3s 环境不稳定：WSL init 系统反复 poweroff 导致 k3s 每 3-4 分钟重启一次，
所有 pod sandbox 反复重建（postgres 重启 88 次、openvino 41 次）。
根因是 WSL2 VM 管理层的 bug，非 k3s 配置问题。

用户决定恢复 native cell，让服务直接在 Windows 上跑，不依赖 WSL/k3s。

## 现状

- native cell 在 commit `0e1336d1`（2026-09-12）被删除，部署收敛为唯一 k8s cell
- native cell 适配的是合并前三服务架构（ai/vault/family/webui），需改造为合并后单一服务（server/webui）
- native cell 不管 PostgreSQL（用户自行安装）和 OpenVINO（独立 Windows 系统服务）

## 改动清单

### 第 1 步：恢复并改造 `tools/bh/win/native/bh.ps1`

从 git 历史 `0e1336d1~1:tools/bh/win/native/bh.ps1` 取回原文件，然后改造：

| 项 | 原值（三服务） | 新值（单一服务） |
|---|---|---|
| 服务列表 | ai/vault/family/webui (4) | server/webui (2) |
| 项目路径 | `services\Baihua.{AI,Vault,Family,Web}` | `services\Baihua.Server` + `Baihua.Web` |
| exe 名 | bh-ai/bh-vault/bh-family/bh-webui | bh-server/bh-webui |
| 端口 | 8791/8790/8788/5177 | 8788/5177 |
| 启动顺序 | ai→vault→family→webui | server→webui |
| server 绑定 | family 绑 0.0.0.0 | server 绑 0.0.0.0 |
| 服务间 URL | BAIHUA_VAULT_URL/BAIHUA_AI_URL | 删除（进程内直调） |
| WebUI 后端 | FamilyApi/AiApi/VaultApi 三个 BaseUrl | 单一 BaihuaServer__BaseUrl |
| PG 环境变量 | 无 | PG_HOST/USER/PASSWORD/DATABASE（单库 baihua） |

### 第 2 步：改顶层路由 `tools/bh/bh.ps1`

```powershell
$Cells = @{
    'native' = @{ Script = 'win\native\bh.ps1'; Desc = 'Windows native（dotnet 进程）' }
    'k8s'    = @{ Script = 'linux\k8s\bh.sh';    Desc = 'Linux k3s（经 WSL，root）' }
}
$DefaultCell = 'native'  # 改默认为 native
```

### 第 3 步：PostgreSQL（用户手动安装）

native cell 不管 PG 安装。用户需：
1. Windows 上安装 PostgreSQL（或用已有的）
2. 建单一 `baihua` 库
3. 在 `BAIHUA_HOME` 或环境变量里配 `PG_HOST`/`PG_USER`/`PG_PASSWORD`

可提供 `scripts/init-pg.ps1` 辅助建库。

### 第 4 步：OpenVINO（恢复 ovms 系统服务）

恢复 `scripts/install-openvino-ovms-service.ps1`（已退役），安装 OVMS 为 Windows 系统服务。
native cell 在 status 里展示 8000 端口状态，不参与启停。
环境变量 `OpenVinoOms__BaseUrl=http://127.0.0.1:8000`。

### 第 5 步：Open WebUI（Windows venv 方式）

Open WebUI 在 Windows 上用 Python venv 方式跑（之前的方式）：
1. `python -m venv venv && venv\Scripts\activate && pip install open-webui`
2. 启动脚本 `scripts/start-open-webui.ps1`
3. 端口 8080，iframe 指向 localhost:8080（不用改）
4. 环境变量：HF_HUB_OFFLINE=1、RAG_EMBEDDING_ENGINE=openai、OPENAI_API_BASE_URL=http://127.0.0.1:8000/v1

### 第 6 步：DSH 插件适配

`baihua-dsh-plugin` 的 bh_status/bh_action 等工具需适配 native cell：
- native cell 的 status 输出格式与 k8s cell 不同（进程列表 vs pod 列表）
- native cell 没有 build/deploy/up 命令（只有 start/stop/restart/status）
- 或者：DSH 插件只支持 k8s cell，native cell 不通过 DSH 管理

### 第 7 步：更新 AGENTS.md

更新部署形态说明，加回 native cell 作为 Windows 默认部署。

## 实施顺序

1. **先做第 1-2 步**（恢复 native cell + 改路由）→ 用户能用 `bh start` 启动 server+webui
2. **第 3 步**（PostgreSQL）→ 用户手动安装
3. **第 4-5 步**（OpenVINO + Open WebUI）→ 可选，不影响核心功能
4. **第 6-7 步**（DSH 插件 + 文档）→ 后续完善

## 风险

- native cell 用 `Start-Process` 拉起服务，记忆 `feedback-start-process-unstable.md` 指出此环境不稳定
- 但 native cell 的 Start-Process 是在 Windows PowerShell 里直接跑，不是在 WSL 里，可能更稳定
- PostgreSQL 需用户手动安装，增加部署复杂度
- Open WebUI venv 方式需要 Python 环境