# 百花（Baihua）文档中心

## 文档目标

本文档中心提供项目相关的设计文档、配置指南和运维手册。**贯彻代码即文档：历史计划、一次性修复记录、已完成快照一律删除，以 git 历史为准。**

## 现有文档

| 文档 | 用途 | 状态 |
|---|---|---|
| [DSH_INTEGRATION.md](DSH_INTEGRATION.md) | DSH（DeepSeek Harness）插件生态集成总文档（桥接/运维/绘图/本地 AI/MCP） | 现行 |
| [COMPUTE_POOL_DRAW.md](COMPUTE_POOL_DRAW.md) | 算力池文生图/文生视频（ComfyUI 网关）使用指南 | 现行 |
| [LAN_COMPUTE_POOL.md](LAN_COMPUTE_POOL.md) | 局域网算力池（LAN Compute Pool）整体架构设计 | 现行 |
| [CONFIG_STORAGE_ARCHITECTURE.md](CONFIG_STORAGE_ARCHITECTURE.md) | 配置与存储架构（单库 `baihua` PostgreSQL / 密钥加密 / 数据目录） | 现行 |
| [sync_protocol.md](sync_protocol.md) | 移动端与后端同步协议（manifest → 文件 → 本地写入） | 现行 |
| [mobile_vault_distribution.md](mobile_vault_distribution.md) | 移动端知识库分发/导入方案（多知识库 vaultId 隔离） | 现行 |
| [openclaw-openvino-integration.md](openclaw-openvino-integration.md) | OpenClaw 本地 AI（OpenVINO/llama.cpp/Ollama/LM Studio）集成 | 现行 |
| [IMPROVEMENT_PLAN.md](IMPROVEMENT_PLAN.md) | 插件体系优化计划（12 项全部完成）+ 工具层全灭事故排障记录 | 已完成/排障记录 |

## 相关文档位置

- **项目根目录**：`README.md` — 项目介绍（baihua 仓库）
- **开发助手**：`AGENTS.md` — 开发操作说明（架构 / 命名约定 / 测试命令）
- **移动端文档**：`arkts/docs/`（鸿蒙）/ `kotlin/docs/`（安卓）
- **上架与合规**：鸿蒙 `arkts/docs/` 下 AGC_SETUP_GUIDE / AGC_SUBMISSION_COPY / CHECKLIST_BEFORE_SUBMIT / APP_ICON_GUIDE
- **部署**：`k8s/README.md`（Linux k8s）、`docker/`（compose 配置）、`tools/bh/README.md`（bh CLI）

## 清理记录

- 2026-08-23：删除 22 个过时/一次性文档（架构草案、DB 隔离交接、SQLite→PG 迁移记录、WebUI 优化计划、
  审计快照、i18n 计划、VMASTER 一次性提示词、测试报告等），历史可查 git。
- 2026-08-06：清理历史分析类、故障快照类、临时修复记录类文档，以 git 历史为准。

最后更新：2026-09-12（三服务合一 commit `aa053f1` 后的文档同步）
