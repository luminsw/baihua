# 百花本地文生图 / 文生视频（算力池共享）使用指南

> 百花把**本地 ComfyUI 的文生图 / 文生视频**接入算力池，作为与聊天对等的一等能力广播共享。
> 局域网内其它**百花服务器**或安装了 **DSH 插件**的 **DSH**，可通过统一绘图网关跨机调用本机的绘图能力。
>
> 配套代码：Baihua.Contracts 的 DrawCapabilityDto、Baihua.Core 的 ComfyDrawService、
> Baihua.Modules.Family 的 DrawController（本机 API）与 DrawGatewayController（跨机网关），
> 都跑在唯一的后端进程 `Baihua.Server`（8788）里。
> 相关：docs/LAN_COMPUTE_POOL.md（算力池整体架构）。

---

## 1. 端点总览

### 本机管理 API（仅本机可访问，默认 loopback）
| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /api/draw/status | ComfyUI 在线 + 可用 checkpoint |
| POST | /api/draw/image | 文生图（SD） |
| POST | /api/draw/video | 文生视频（LTX Video） |
| GET | /api/draw/file?filename=... | 取生成文件（图片/视频字节） |

> 这些走 /api/draw/*，受后端管理 API（`Baihua.Server`，8788）的 loopback / BAIHUA_ADMIN_ALLOWED_NETS 限制。

### 算力池绘图网关（**跨机可用**，token 鉴权）
| 方法 | 路径 | 说明 |
|---|---|---|
| GET | /mg/pool/v1/draw/capabilities | 绘图能力（ComfyUI 在线 + 支持图像/视频 + checkpoint） |
| POST | /mg/pool/v1/draw/image | 文生图（跨机提交并等待完成） |
| POST | /mg/pool/v1/draw/video | 文生视频（跨机提交并等待完成） |
| GET | /mg/pool/v1/draw/file?filename=... | 取生成文件（经百花中转，对端无需直连 ComfyUI） |

### 能力广播（对端发现用）
GET /mg/capabilities 返回 ComputeNodeCapabilitiesDto.Draw，即 { ComfyOnline, Image, Video, ImageCheckpoint, VideoCheckpoint }。
对端据此判断某节点能否绘图。

---

## 2. 鉴权

- /mg/pool/v1/draw/* 用 **X-Server-Token** 或 **Authorization: Bearer <token>**，值即本机环境变量 **BAIHUA_AI_EXTERNAL_TOKEN**。
- **若该变量未设置，则这些端点无鉴权开放**，LAN 上任意机器都能触发本机绘图（吃 GPU）——**生产环境建议务必设置**。
- 校验行为：未设 token → 放行；已设 token → 无/错 token 返回 401 {"error":"invalid token"}，正确的返回 200。

### 如何设置 token（Windows native）
tools/bh/win/native/bh.ps1 已内置：后端（server）启动时从 **~/.baihua/ai-external-token.txt** 读取并注入 BAIHUA_AI_EXTERNAL_TOKEN。

1. 生成并写入密钥文件：
   ```powershell
   $bytes = New-Object byte[] 20
   [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
   $token = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
   [System.IO.File]::WriteAllText("$HOME/.baihua/ai-external-token.txt", $token)
   ```
2. bh restart server。
3. 对端（另一台百花 / DSH 插件）使用**同一个 token**。

> 手动部署则直接设环境变量 BAIHUA_AI_EXTERNAL_TOKEN=<token> 再启动后端（`Baihua.Server`）。
>
> ⚠️ 注意：bh.ps1 为 **UTF-8 带 BOM**，修改时不要破坏 BOM，否则 PowerShell 5.1 会按 GBK 误读导致中文乱码。

---

## 3. 从 DSH 插件调用（推荐）

**插件**：baihua-dsh-plugin，工具 baihua_draw / baihua_draw_video，均经绘图网关，支持跨机。

### 插件配置（~/.dsh/profiles/web/cordis.patch.yml）
```yaml
- id: dsh-baihua-bridge
  config:
    drawGatewayUrl: http://127.0.0.1:8788       # 本机默认（跨机填对方节点，如 http://192.168.3.9:8788）
    drawToken: <BAIHUA_AI_EXTERNAL_TOKEN>       # 目标节点 token（本地回环且未设 token 时可留空）
```

> 合并后 `baihua-dsh-plugin` 已不再消费 `familyUrl` / `vaultUrl` / `comfyUrl`：
> 8788 就是唯一后端（`Baihua.Server`），绘图统一走 `drawGatewayUrl`，知识库/家庭数据走内置 `/mcp`。

### 工具
- **baihua_draw**：文生图。参数 prompt（必填）、negativePrompt、width/height（默认 512）、steps（默认 20）。返回图片访问 URL。
- **baihua_draw_video**：文生视频（LTX）。参数 prompt、negativePrompt、width/height（建议 ≤768）、length（帧数，默认 97 ≈4s）、fps（默认 25）、steps（默认 20）。生成约 1-5 分钟，返回视频访问 URL。

**本机**：drawGatewayUrl 留空（或指向本机后端 127.0.0.1:8788），即可在 DSH 里用 baihua_draw / baihua_draw_video 出图/出视频。
**跨机**：把 drawGatewayUrl 指向目标百花节点，DSH 即调用该节点的绘图能力。

---

## 4. 从另一台百花调用（无 DSH）

对端百花先 GET /mg/capabilities 看本机 Draw 是否为 ComfyOnline: true，再直接调用网关：

```bash
# 能力查询
curl -H "X-Server-Token: <token>" http://192.168.3.9:8788/mg/pool/v1/draw/capabilities

# 文生图（提交并等待，返回 {success,fileName,contentType,elapsedSeconds}）
curl -X POST -H "X-Server-Token: <token>" -H "Content-Type: application/json" \
  -d '{"prompt":"a lighthouse at sunset, ocean waves","negativePrompt":"blurry"}' \
  http://192.168.3.9:8788/mg/pool/v1/draw/image

# 文生视频
curl -X POST -H "X-Server-Token: <token>" -H "Content-Type: application/json" \
  -d '{"prompt":"a sailboat on a calm sea at golden hour","length":33,"fps":25,"steps":15}' \
  http://192.168.3.9:8788/mg/pool/v1/draw/video

# 取文件
curl -H "X-Server-Token: <token>" \
  "http://192.168.3.9:8788/mg/pool/v1/draw/file?filename=baihua-draw_00001_.png" -o out.png
```

---

## 5. 请求 / 响应字段

| 类型 | 字段 | 默认 | 说明 |
|---|---|---|---|
| 文生图 | prompt | — | 必填，正向提示词（英文效果更佳） |
| | negativePrompt | "" | 负向提示词 |
| | width / height | 512 | 建议 256-1024 |
| | steps | 20 | 采样步数 |
| | checkpoint | SD1.5 | 出图模型 |
| 文生视频 | prompt | — | 必填 |
| | negativePrompt | "" | 负向提示词 |
| | width / height | 512 | **建议 ≤768** |
| | length | 97 | 帧数（约 4s @25fps；建议 25-121） |
| | fps | 25 | 帧率 |
| | steps | 20 | 采样步数 |
| | checkpoint | LTX | 出视频模型 |
| 响应 | success / fileName / contentType / error / elapsedSeconds | | fileName 用于拼 /mg/pool/v1/draw/file 下载 URL |

---

## 6. 模型与硬件

- **出图**：v1-5-pruned-emaonly.safetensors（SD1.5）。
- **出视频**：ltx-video-2b-v0.9.safetensors（LTX Video 2B，含 DiT+VAE）+ t5xxl_fp8_e4m3fn.safetensors（文本编码器，CLIPLoader type=ltxv）。
- **ComfyUI**：0.33+（原生 LTX 节点），本机监听 127.0.0.1:8188（仅本机，由百花网关代调）。
- 生成耗时：图片约 15-60s（含模型冷加载）、视频约 1-5 分钟。

---

## 7. 排查

| 现象 | 原因 / 处理 |
|---|---|
| 401 invalid token | token 不对，或目标节点未用同一 BAIHUA_AI_EXTERNAL_TOKEN |
| 403 访问 /mg/pool/v1/draw/* | 不在公开路径（应为 /mg/pool/ 前缀）；确认未走错端点 |
| ComfyUI 未运行或不可达 | 目标节点本机 ComfyUI（127.0.0.1:8188）未启动 |
| 生成超时 | 调低分辨率/帧数，或检查目标节点 GPU 负载 |
| 对端能力为空 | /mg/capabilities 的 Draw.ComfyOnline 为 false（ComfyUI 未跑） |
