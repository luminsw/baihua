# 百花文档中心

只收录**现行**设计/配置/使用文档。一次性计划、已完成快照、排障记录一律不写进文档，以 git 历史为准。

## 现行文档

| 文档 | 内容 |
|---|---|
| [DSH_INTEGRATION.md](DSH_INTEGRATION.md) | DSH 插件生态集成（桥接 / 运维 / 绘图 / 本地 AI / MCP） |
| [DSH_DEV_SETUP.md](DSH_DEV_SETUP.md) | DSH 开发环境配置（新机器/新开发者的安装与配置清单） |
| [COMPUTE_POOL_DRAW.md](COMPUTE_POOL_DRAW.md) | 算力池文生图/文生视频（ComfyUI 网关）使用 |
| [LAN_COMPUTE_POOL.md](LAN_COMPUTE_POOL.md) | 局域网算力池整体架构 |
| [CONFIG_STORAGE_ARCHITECTURE.md](CONFIG_STORAGE_ARCHITECTURE.md) | 配置与存储架构（单库 PostgreSQL / 密钥加密 / 数据目录） |
| [sync_protocol.md](sync_protocol.md) | 移动端与后端同步协议（manifest → 文件 → 本地写入） |
| [mobile_vault_distribution.md](mobile_vault_distribution.md) | 移动端知识库分发/导入（多库 vaultId 隔离） |
| [openclaw-openvino-integration.md](openclaw-openvino-integration.md) | OpenClaw 本地 AI（OVMS / llama.cpp / Ollama / LM Studio）集成 |
| [BAISHI_MASTER_SKILL.md](BAISHI_MASTER_SKILL.md) | 「拜师」AI 导师方法论（出题→批改→落库→跨会话续学），对应 DSH preset `baihua-baishi` + skill `baishi-master` |
| [CODE_MIRRORING.md](CODE_MIRRORING.md) | 代码镜像到华为云 CodeHub / 阿里云 Codeup（双远程持续同步建议） |

## 其它文档位置

- 项目概览与启动：根目录 `README.md`
- CLI 与排障：`tools/bh/README.md`
- Linux k3s 部署：`k8s/README.md`
- 开发约定（架构 / 命名 / 测试）：`AGENTS.md`
- 移动端：`arkts/docs/`（鸿蒙）、`kotlin/docs/`（安卓）