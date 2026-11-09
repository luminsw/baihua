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

## 常见故障：服务「能读文件、不能写文件」（Low 完整性标签）

**症状**（2026-11-09 实际发生，排查了很久）：

- 知识库写笔记失败：MCP `baihua_vault_write_note` → `{"ok":false,"error":"写入失败"}`
- 日志刷 `UnauthorizedAccessException: Access to the path '...' is denied`；
  不只是 `~/.baihua`，连 `%TEMP%`、`C:\Users\Public` 也拒
- 索引快照 `~/.baihua/db/vault-index-snapshots.json.tmp`、自动备份临时目录同样被拒（长期告警）
- 读取、数据库、HTTP、AI 全部正常 —— **只有写文件失败**
- 同一用户、同一目录，普通终端 `Set-Content` 能写；换启动方式（WMI / Start-Process / 计划任务）**结果一致**

**根因**：Windows 会按**映像文件（.exe）的完整性标签**决定进程令牌的完整性级别（IL）。
`C:\Users\lumin\src\baihua`（仓库根）被外部工具打上了 **Low 强制完整性标签**
（`Mandatory Label\Low Mandatory Level:(OI)(CI)(NW)`，沙箱类工具常这么做：好让它降级后的令牌能写工作区）。
该标签沿目录继承，于是 `out\native\server\bh-server.exe` 自己也带 Low 标签（`(I)(NW)`），
**从它启动的进程一律是 Low IL**：对 Medium 级别对象（%TEMP%、用户目录、`~/.baihua`）只有读权限，
写操作被 MIC（强制完整性控制）的 no-write-up 直接拒绝。

这也解释了当时的三个「矛盾」：换启动方式无效（标签在文件上，不在启动器上）；
目录 ACL/SDDL 全正常（问题不在目标目录）；`dotnet bh-server.dll` 能写（映像是
`C:\Program Files\dotnet\dotnet.exe`，没有 Low 标签）。

**诊断一行命令**（看 exe 自己的标签，而不是目标目录）：

```powershell
icacls "C:\Users\lumin\src\baihua\out\native\server\bh-server.exe" | Select-String 'Mandatory'
# 出现 Low Mandatory Level → 就是这个问题
# 进程侧对照：Low → 0x1000 / Medium → 0x2000（服务启动日志「启动自检」会直接打出来）
```

**修复**（不影响仓库其它目录的语义，只改构建产物目录）：

```powershell
icacls "C:\Users\lumin\src\baihua\out\native" /setintegritylevel (OI)(CI)Medium
bh build-restart server webui
```

**已加的防线**（防止复发，2026-11-09）：

| 位置 | 行为 |
|---|---|
| `bh build` | 每个服务 publish 完成后立即 `Repair-LowIntegrityLabel`（检测到 Low 就打警告并改回 Medium） |
| `bh start` | 启动前对产物目录做同样的自愈；生成的一次性计划任务 `.cmd` 里也带一行 `icacls ... /setintegritylevel`（计划任务以 Medium 令牌运行，能修调用方即使在低完整性沙箱里修不了的标签） |
| `Baihua.Server` 启动自检 | `WindowsIntegrityGuard.LogSelfCheck`：读自身 IL + 临时目录写探针；Low IL 或写失败直接打 **ERROR**，并打印上面的修复命令（不让问题拖到「写笔记失败」才暴露） |
| 回归测试 | `scripts/verify-vault-write-e2e.cjs`：MCP 写笔记 → 落盘 → 读回 → `/browse` 打开笔记详情弹窗，写不进去就失败 |

**注意**：沙箱工具（如 DSH 的 Windows 沙箱）每次以 `workspace-write` 模式进入时可能**重新**给工作区打 Low 标签，
所以这类问题会复现；上面的 `bh build/start` 自愈就是为它准备的（执行一次即可把产物目录拉回 Medium）。

**勿再按这些结论排查**（已被实测推翻）：WMI 令牌降级、`schtasks /rl limited` 降级、目录 ACL/Deny ACE、
Defender 受控文件夹访问、reparse point、EFS、AppLocker/WDAC。
