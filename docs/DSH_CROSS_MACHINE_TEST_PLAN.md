# 百花 DSH 零配置 + 跨机算力 — 升级 & 测试计划

> 目标机器：**望月台**（192.168.3.9:8788，本机 ComfyUI 在线，可绘图）
> 对照机器：**桃夭馆**（192.168.3.13，本机 DSH，本机 ComfyUI 未开）
> 版本基线：与 `main`（baihua）+ 各插件 `master/main` 最新的「零配置 + 跨机按名调用」体系对齐。

## 0. 这套版本做了什么（一句话）
本机 DSH **单入口、零 token、零手工配置**自举百花拓扑；`/api/dsh/pool` 提供 **peer 名 → 绘图网关/能力**目录；`baihua_draw(target=节点名)` 跨机按名调用；绘图/AI shim 鉴权改为 **本机(回环+10.0.0.0/8)免鉴权、跨机要 token**。

---

## 1. 升级步骤（在 192.168.3.9 上执行）

> 假设望月台为 k8s 部署（与本机一致）。若为 native/Windows，用对应的 `bh` 脚本（`tools/bh/linux/native/bh.sh` 或 `tools/bh/win/*`），命令 `bh` 同理。

### 1.1 更新百花后端源码并重建
```bash
cd <baihua 仓库>
git pull origin main        # 拉到含 /api/dsh/config、/api/dsh/pool、鉴权硬化、vault DI 修复的版本
# k8s 部署（三服务合一后只有 server + webui 两个镜像）：
bh build server webui && bh deploy     # 或 bh update（会 git pull + build + deploy）
# 验证服务起来：
bh status --json | grep -E '"name"|"ready"|"upToDate"'
```

### 1.2 若不采用固定 ClusterIP（本机才需要）
本机桃夭馆通过 `/api/dsh/config` 给 DSH 返回**宿主机可达**地址，依赖各 Service 固定 ClusterIP。望月台若也被本机（或对端）的 DSH/插件直接访问，同样建议固定其 Service ClusterIP（`k8s/*.yaml` 的 `spec.clusterIP`）。若只是「被动被调用」，望月台自身的 Service IP 是否固定不影响其作为**调用方**——但望月台若是**调用方**（从望月台的 DSH 连望月台自身），同样需要固定。

### 1.3 DSH 插件（若望月台也跑 DSH）
```bash
cd ~/.dsh/profiles/web
pnpm update baihua-dsh-plugin baihua-local-ai-dsh-plugin hysteria-dsh-plugin
# 重启 DSH 生效
bh_dsh_restart 或手动重启
```
> 插件已是 **GitHub 引用安装**，`pnpm update` 即拉最新 commit。

### 1.4 想启用「跨机需 token」（可选，默认不做）
望月台若开启 `BAIHUA_AI_EXTERNAL_TOKEN`（`k8s/02-secret.yaml` 的 `baihua-secret`），则跨机调用望月台绘图/推理需带该 token；**同网段 10.0.0.0/8 仍免鉴权**。DSH 侧会从 `/api/dsh/config` 自动拿到并携带。

---

## 2. 测试用例

### A. 零配置自举（本机 DSH → 本机百花）
| # | 操作 | 预期 |
|---|---|---|
| A1 | `curl http://127.0.0.1/api/dsh/config` | 200，返回 `familyUrl/vaultUrl/aiUrl/webUrl/drawGatewayUrl/poolUrl/aiShimUrl/drawToken/poolToken/comfyModelType/comfyCheckpoint`，地址为宿主机可达（127.0.0.1 或本机 ClusterIP），无需 token。**合并后 family/vault/ai 是同一个 8788 进程**：`familyUrl`/`vaultUrl`/`aiUrl` 都应落在 8788 上；若仍返回 8790/8791，说明后端还有未清理的旧读取点 |
| A2 | 等待 DSH 插件 apply 自举 | 设置 → 插件 →「百花服务状态」卡片出现「**已自动发现（零配置自举）**」块，显示 family/vault/ai（同一 server）/算力池/绘图 地址 |
| A3 | `mcp__baihua__baihua_vault_list` / `baihua_budget_summary` / `baihua_tasks_list` | 返回正常数据（不等 token 报错） |

### B. 算力池目录（peer 名 → 能力）
| # | 操作 | 预期 |
|---|---|---|
| B1 | `curl http://127.0.0.1/api/dsh/pool` | 200，`nodes[]` 含**本机**与**对端**，每节点有 `name/hostUrl/isLocal/online/drawGatewayUrl/draw{comfyOnline,image,video}/models` |
| B2 | 确认「望月台」`draw.comfyOnline=true`（绘图可用），「桃夭馆」（若本机 ComfyUI 未开）为 false | 区分本机/对端能力 |

### C. 跨机按名绘图（核心）
| # | 操作 | 预期 |
|---|---|---|
| C1 | 在**桃夭馆** DSH：`baihua_draw(prompt="a red circle", width=256, height=256, target="望月台")` | 路径 `192.168.3.9`，返回图片 URL，`GET` 该 URL 得到 `PNG` 图片 |
| C2 | 在**望月台** DSH：`baihua_draw(prompt="...", target="桃夭馆")` | 若桃夭馆 ComfyUI 在线则出图；否则报「绘图网关不可达/ComfyUI 不在线」（确认路由与错误提示） |
| C3 | `baihua_draw(...)`（不带 target） | 走默认网关；桃夭馆默认为本机（未开 → 报不可达），望月台默认为本机（在线 → 出图） |
| C4 | `baihua_draw_video(..., target="望月台")` | 望月台支持视频则出视频，否则明确提示不支持 |

### D. 跨机互信/鉴权
| # | 操作 | 预期 |
|---|---|---|
| D1 | 未设 token（默认） | 本机(10.x)与望月台(192.168.x)互调均免鉴权 |
| D2 | 望月台设 `BAIHUA_AI_EXTERNAL_TOKEN=X` 后 | 从桃夭馆无 token 调望月台 `/mg/pool/v1/draw/*` 得 401；带 token 成功；桃夭馆本机(10.0.0.0/8)仍免鉴权 |
| D3 | 首次未配置 token 时 | 绘图/AI shim 均为局域网信任（`Authorize()` 放行），不报 401 |

### E. 图形/AI shim 鉴权硬化回归
| # | 操作 | 预期 |
|---|---|---|
| E1 | `curl -s http://127.0.0.1/mg/ai/v1/models` | 200（本机免鉴权） |
| E2 | `curl -s http://127.0.0.1/mg/pool/v1/draw/capabilities` | 200（本机免鉴权，无回归） |

### F. MCP 工具（不只读崩）
| # | 操作 | 预期 |
|---|---|---|
| F1 | `mcp__baihua__baihua_vault_search(query="test")` | 200，`{results:[], status:{errorMessage:"必须指定有效的知识库"}}`（不再是 500） |
| F2 | `baihua_vault_read_note(path=..., vaultId=...)` | 正常返回或明确「vault 不存在」 |

---

## 3. 关键命令速查
```bash
# 自举拓扑
curl http://127.0.0.1/api/dsh/config
# peer 目录
curl http://127.0.0.1/api/dsh/pool
# 服务状态
bh status --json
# 恢复「默认免鉴权」：清空 baihua-secret 的 BAIHUA_AI_EXTERNAL_TOKEN 后 bh deploy
```

---

## 4. 回滚
- 百花后端：`git -C <repo> checkout main~1 && bh build server webui && bh deploy`（或 `bh update` 前先记下 commit，失败时 `git reset --hard <前一张>` + 重部署）。
- DSH 插件：`pnpm update <plugin>@<上一版本提交>` 或改回 `github:luminsw/<repo>` 后重装；`pnpm install` 下拉历史版本。
- 若跨机绘图异常：确认 `/api/dsh/pool` 对端 `online` 与 `draw.comfyOnline`，以及对端是否开启 `BAIHUA_AI_EXTERNAL_TOKEN`（DSH 需带 token）。

---

## 5. 排查项
- `pool`/对端不可达 → 查 `/api/dsh/pool` 的 `online`、对端 8788 可达性（`curl http://192.168.3.9:8788/api/dsh/config`）。
- DSH 卡「已自动发现」为空 → 查 `/api/dsh/config` 是否 200、插件是否已自举（`window` 控制台 / DSH 日志）。
- `baihua_draw` 报不可达 → 查目标节点 ComfyUI 是否在线（`/api/dsh/pool` 的 `draw.comfyOnline`）、网关地址、token。
