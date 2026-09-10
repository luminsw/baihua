# 本地模型初始化提示词

> 把以下提示词粘贴给 DSH / OpenClaw 等 Agent，Agent 会自动诊断本机硬件、安装并启动推理服务，
> 然后通过百花 MCP 工具把模型信息写入注册表，WebUI 本地模型页自动展示。

## 提示词

```
你正在协助用户初始化本机 AI 推理环境。请按以下步骤操作：

1. 诊断硬件
   - 检查 CPU / GPU / 内存信息
   - 确认是否有 Intel Arc GPU（可跑 OpenVINO）、NVIDIA GPU（可跑 CUDA）、或纯 CPU

2. 查已注册模型（防重复）
   - 调用 baihua_local_model_list 查看百花已注册的本地模型
   - 如果已有同 tool + model_id 的条目，跳过重复注册

3. 选后端并安装模型
   - Intel Arc GPU → 用 OpenVINO + OVMS（OpenVINO Model Server）
     · 下载 int4 量化模型（如 Qwen3-4B-int4-ov）
     · 配置 OVMS config.json 注册模型
     · 启动 OVMS 服务（默认端口 8000）
   - NVIDIA GPU → 用 vLLM 或 Ollama
   - 纯 CPU → 用 llama.cpp / Ollama（选小模型如 1.5B-4B）

4. 启动并验证
   - 启动推理服务后，用 curl 请求 /v1/models 确认模型可用
   - 发一条测试 prompt 确认推理正常

5. 调 MCP 注册
   - 对每个成功启动的模型，调用 baihua_local_model_register：
     · tool: 推理后端类型（如 "oms" / "vllm" / "ollama" / "llama.cpp"）
     · model_id: 模型标识（如 "qwen3-4b"）
     · display_name: 展示名（如 "Qwen3-4B"）
     · endpoint: 推理服务地址（如 "http://localhost:8000"）
     · usage: "chat" 或 "embedding" 或 "vision"
     · parameter_size: 如 "4B"
     · quantization: 如 "int4"
     · registered_by: 你的 agent 名

完成后告知用户哪些模型已注册成功。
```

## MCP 工具

| 工具 | 用途 |
|------|------|
| `baihua_local_model_register` | 注册/更新模型（幂等，按 tool+model_id 去重） |
| `baihua_local_model_list` | 列出已注册模型（可按 tool 筛选） |
| `baihua_local_model_unregister` | 注销模型 |

工具端点：`POST http://<百花服务器>:8788/mcp`（MCP streamable-http，stateless）