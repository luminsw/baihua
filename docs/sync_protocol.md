# 桌面端 ↔ 移动端（鸿蒙）同步协议（最小可行版）

目标：移动端仅做"随身查询"，知识库的权威编辑与生成发生在桌面端。同步优先局域网（同 Wi‑Fi）直连，避免账号体系与云端合规成本。

## 核心原则

- **桌面端为权威源**：移动端第一阶段只读（降低冲突与恢复复杂度）
- **增量同步**：按文件变更集同步，而非整库拷贝
- **可恢复**：同步中断可续传（按文件/分块）
- **简化连接**：通过二维码扫描获取服务器地址，无需配对授权，无需 Token
- **合规优先**：移动端不提供联网 AI；桌面端联网 AI 需用户明示同意隐私/协议（可拒绝）

## 数据对象

知识库为本地目录（`.md` 文件 + 附件）。服务端支持**多知识库**，同步时必须显式指定目标 `vaultId`。同步对象为：

- 某个 `vaultId` 对应根目录下的文件（默认包含 `**/*.md`）
- 可选：附件（图片、PDF 等）在后续版本再开

### 文件标识

- `vaultId`：服务端知识库的唯一标识（如 UUID）
- `relPath`：相对该 vault 根目录的路径（统一使用 `/`）
- `size`：字节数
- `mtime`：桌面端文件修改时间（毫秒或 ISO）
- `sha256`：内容哈希（用于去重与续传校验）

> 注意：仅靠 mtime 不足以应对跨设备时钟差，必须有 hash。

## 同步流程（高层）

1. **选择目标知识库**
   - 移动端在「知识库管理」页面查看/添加服务器配置
   - 每个服务器下可配置多个知识库（`vaultId`），同步前必须选中一个作为当前目标
2. **扫描二维码**
   - 桌面端显示二维码，包含 HTTP/HTTPS 地址和主机名
   - 移动端扫码后提取服务器地址
3. **拉取清单**
   - 移动端 `GET /vault/manifest?vaultId={vaultId}&since={cursor}`
   - 桌面端返回变更文件列表 + 新 cursor
4. **按需拉取文件**
   - 对缺失/变更的文件逐个拉取：`GET /vault/file?vaultId={vaultId}&path=...`
   - 大文件支持分块：`GET /vault/file_chunk?vaultId={vaultId}&...&offset=&length=`
5. **移动端落盘**
   - ArkTS 端写入应用沙箱目录（按 `vaultId` 隔离存储）
6. **完成与记录**
   - 移动端保存 `cursor`（按 `vaultId` 隔离，下次只拉增量）

## cursor 设计（建议）

桌面端为每个 `vaultId` 维护独立的单调递增变更序列号：

- 每次文件写入/删除，都写一条"变更事件"到 `changes` 表（SQLite）或 append log
- `cursor` 即最后一次同步到的序列号

这样移动端 `since=cursor` 就能拿到精确增量，不依赖目录扫描的实时差异计算。

## 游标回退（cursor regression）

理论上 `cursor` 应单调递增；但当服务端重建/清空变更序列（例如换了 vaultRoot、数据库被重建）时，服务端可能返回比客户端本地更小的 `cursor`。

客户端应当：

- 发现 `serverCursor < localCursor` 时，丢弃增量并改走 `since=0` 全量重同步

## HTTP API 草案（桌面端服务提供）

所有与 vault 相关的 API 均要求传入 `vaultId` 查询参数。若缺失或无效，服务端返回 `400 Bad Request`。

### 二维码格式

桌面端生成的二维码为 JSON 格式：

```json
{
  "httpUrl": "http://192.168.1.100:8788",
  "hostName": "Desktop-PC"
}
```

- `httpUrl`：HTTP 访问地址
- `hostName`：服务器主机名（供显示）

移动端使用 `httpUrl` 连接服务端。

### 清单

- `GET /vault/manifest?vaultId={vaultId}&since={cursor}`
  - 无需 Authorization 头
  - 必须提供 `vaultId`；缺失返回 `400`
  - resp:
    ```json
    {
      "cursor": 42,
      "minSeq": 1,
      "files": [
        {"op":"upsert","relPath":"症状/头痛.md","size":1234,"mtime":1710000000000,"sha256":"..."},
        {"op":"delete","relPath":"旧文件.md"}
      ]
    }
    ```

- （可选）`minSeq`：服务端当前变更日志最早可用的序号；当客户端本地 `cursor < minSeq` 时应改走 `since=0` 全量重同步

### 文件下载

- `GET /vault/file?vaultId={vaultId}&path={relPath}`
  - 无需 Authorization 头
  - 必须提供 `vaultId`；缺失返回 `400`
  - resp: 二进制内容（或直接返回文本，视实现）

> 客户端容错：当 `manifest` 返回历史事件（例如本地 `cursor` 回退/不匹配）时，`files[]` 中可能包含该文件的历史 `upsert`，但服务端此刻该文件可能已经被删除，导致 `GET /vault/file` 返回 `404`。客户端应跳过该 `upsert` 并继续处理后续 `delete`，以确保最终状态一致。

### 分块下载（可选）

- `GET /vault/file_chunk?vaultId={vaultId}&path={relPath}&offset=0&length=1048576`

## 与当前实现的对应关系（task_runner v1）

当前 `services/Baihua.Modules.Vault`（由唯一后端 `Baihua.Server` 承载；三服务合一前在 `services/Baihua.Family` / `services/Baihua.Vault`）已实现以下最小接口（详见对应服务文档）：

- `GET /vault/manifest?vaultId=...&since=`（`since` 为空或 `0` 返回当前全部 `.md` 快照；`since` 为上次 `cursor` 时返回增量 `upsert`/`delete`；响应含 `incremental` 布尔字段）
- `GET /vault/file?vaultId=...`（v1：仅 `.md` 文本）
- `GET /vault/file_chunk?vaultId=...`（v1：仅 `.md` 文本，按字节切分，UTF-8 可能发生替换字符）
- `GET /pairing/qrcode`（获取二维码数据）

验证结果（本地端到端）：对临时目录启一个服务后，执行 `since=0` 全量落盘，再修改服务端 vault 中的一个 `.md`，随后用上次 `cursor` 拉增量，返回仅包含该文件一次 `upsert`，再同步一次返回空增量，说明 cursor 递增与客户端落盘流程工作正常。

服务端配置：

- 在 WebUI「配置」→「知识库设置」中添加多个知识库，每个知识库对应一个 `vaultId` 和本地路径
- 同步时移动端必须选择其中一个 `vaultId` 作为目标

## 安全边界（必须）

- 同步只在局域网有效（通过 Wi-Fi 直连）
- 所有 `path` 参数必须严格限制在对应 `vaultId` 的根目录内，禁止 `..` 等路径穿越
- 不暴露桌面端任意文件系统，只暴露指定 vault 子树

## 与后台任务系统的关系

- 桌面端后台服务（`Baihua.Server`，合并前为 `Baihua.Family`）负责"生成/整理/索引"等耗时任务
- 同步服务负责"把当前 vault 变更发布给移动端"
- 两者可以是同一个进程的不同模块，也可以分开进程

## 第一阶段的最小实现建议

- 不做上传（移动端只读）
- 不做附件
- 不做分块（`.md` 体积通常很小）
- manifest：服务端已用 `vault_change_log` + `vault_file_snapshot` 表实现 **cursor 增量**（每次请求先对账再返回；存储后端为 PostgreSQL，见 CONFIG_STORAGE_ARCHITECTURE.md）
