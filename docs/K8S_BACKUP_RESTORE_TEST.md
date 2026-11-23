# k8s 形态 `bh backup` / `bh restore` 真机验收手册

> 对应 Windows wslc 形态的手册：`docs/WSLC_BACKUP_RESTORE_TEST.md`（两份口径对齐，报告模板同构）。

> **给执行本手册的 AI 助手**：你在 Linux 服务器上按本手册逐步执行并验收。
> - 全程以 **root** 执行（`sudo -i` 或直接 root 登录）。
> - 每步对照「预期结果」逐项核对，**任何一步失败立即停止**：保留现场（不要清理）、完整摘录命令输出，按文末模板汇报。
> - 不要跳步、不要自创命令、不要"顺手修复"被测代码——你的任务是**验收并报告**，不是修 bug。
> - 手册末尾有收尾清理段，全部通过后才执行。

## 0. 背景（为什么做这次验收）

`bh backup` / `bh restore`（k8s/Linux k3s 形态）是新实现的功能（commit `69d1c89` 起），此前只在
WSL Ubuntu 做过**无 k3s 环境**的冒烟测试（语法/help/参数校验/优雅退出）。以下路径**从未在真实
k3s 集群上执行过**，是本次验收的重点：

1. PV hostPath 路径动态解析（`kubectl get pv -o jsonpath`）
2. 缩容/等待 pod 终止/按原副本数扩容（`kubectl scale` / `rollout status`）
3. 恢复演练：一次性 pod 跑 `pg_controldata`（`kubectl run --overrides`，本手册最高风险点）
4. hostPath 数据目录 tar 打包/解包（含属主保留）
5. secret/configmap 导出与回放（`kubectl get -o yaml` / `kubectl apply`）
6. `pg_isready` 健康检查（`kubectl exec deploy/bh-postgres`）

被测脚本：`tools/bh/linux/k8s/bh.sh`（备份/恢复函数 `backup_cmd` / `restore_cmd` / `verify_drill`）。
参考实现说明：`k8s/README.md`「数据备份与恢复」节。

## 1. 环境要求与准备

| 项 | 要求 |
|---|---|
| OS | 原生 Linux x64/arm（**不要在 WSL 里做**，项目约定不支持 WSL 环境） |
| 权限 | root（或免密 sudo） |
| 磁盘 | ≥ 5 GB 空闲（备份产物 + 镜像） |
| 网络 | 能拉 `rancher-mirror`/`get.k3s.io`（装 k3s）；快速路径**不需要**拉 .NET/nuget 镜像 |
| 版本 | 仓库 main，commit ≥ `69d1c89`（`git log --oneline -1` 确认） |

```bash
# 1a. 获取仓库（已有则跳过；确认在 main 且包含 69d1c89）
git clone https://github.com/luminsw/baihua.git && cd baihua
git log --oneline -1

# 1b. k3s 未安装时（已装则跳过，用 k3s --version 确认）
#     国内机器用镜像源；海外直连官方脚本
curl -sfL https://rancher-mirror.rancher.cn/k3s/k3s-install.sh | INSTALL_K3S_MIRROR=cn sh -
#   或: curl -sfL https://get.k3s.io | sh -
sudo k3s kubectl get nodes   # 节点 Ready 即可继续
```

**k3s 安装后会创建 `/usr/local/bin/kubectl` 软链**；本手册统一用 `kubectl`（等价 `k3s kubectl`）。

## 2. 快速路径：仅 postgres 的最小栈（推荐先做，约 10-15 分钟）

> 不构建 .NET 镜像、不部署 server/webui——备份恢复的核心逻辑（冷备/演练/回放/健康检查）
> 全部在这条路径上。全栈验收见第 7 节（可选）。

### 2.1 调整 vaults 路径（本机没有 /home/lumin）

`k8s/03-pvc.yaml` 里 vaults PV 的 hostPath 硬编码为 `/home/lumin/Vaults`（原开发机用户名），
新机器需改成测试路径（**测试结束后用 `git checkout -- k8s/03-pvc.yaml` 还原**）：

```bash
sed -i 's|/home/lumin/Vaults|/opt/baihua/vaults-test|' k8s/03-pvc.yaml
grep -n 'vaults-test' k8s/03-pvc.yaml   # 预期：83 行附近一条 hostPath path
```

### 2.2 部署最小栈

```bash
kubectl apply -f k8s/00-namespace.yaml
kubectl apply -f k8s/01-configmap.yaml -f k8s/02-secret.yaml -f k8s/03-pvc.yaml -f k8s/25-postgres.yaml
kubectl -n baihua rollout status deploy/bh-postgres --timeout=300s
```

**预期**：rollout 输出 `deployment "bh-postgres" successfully rolled out`；`kubectl -n baihua get pods`
显示 `bh-postgres-xxx  1/1  Running`。数据库口令用模板里的 `Baihua2026Pg!`（仓库公开模板值，测试用）。

### 2.3 造测试数据（备份后用来验证恢复）

```bash
# DB：建表插行
kubectl -n baihua exec deploy/bh-postgres -- \
  psql -U baihua -d baihua -c \
  "CREATE TABLE bh_backup_test(id int, note text); INSERT INTO bh_backup_test VALUES (1,'before-backup');"
kubectl -n baihua exec deploy/bh-postgres -- psql -U baihua -d baihua -c "SELECT * FROM bh_backup_test;"
# 预期：一行 (1 | before-backup)

# 配置基线：记录 baihua-config 此刻的 key 数（后面验证回放用）
kubectl -n baihua get configmap baihua-config -o jsonpath='{.data}' | tr ',' '\n' | wc -l
```

**注意**：此刻 `/opt/baihua/vaults-test`、`/opt/baihua/data` 目录**故意不创建**——第一轮备份要验证"目录不存在→跳过"的告警分支。

## 3. 第一轮 `bh backup`（验证核心 + 跳过分支）

```bash
bash tools/bh/bh.sh backup          # 非 root 会自动 sudo 提权；root 直接跑
```

**预期输出要点**（逐项核对，顺序大致如下）：
1. `[backup] 跳过 baihua-vaults-pv（hostPath 不可用: /opt/baihua/vaults-test）`——**目录不存在，跳过告警**
2. 同样跳过 `baihua-data-pv`、`baihua-openwebui-pv`（本最小栈没有 pod 创建过这些目录）
3. `[backup] 缩容: bh-postgres` → `[backup] 等待 pod 终止...`
4. `[backup] baihua-postgres-pv -> <仓库>/backup/baihua-backup-<时间戳>/baihua-postgres-pv.tgz  (/opt/baihua/postgres)`
5. `[backup] 恢复演练: 解包 postgres 归档...`
6. `[backup] 恢复演练: pg_controldata 校验（一次性 pod: bh-backup-verify-<ts>）...` ——**重点观察**
7. `[backup] 演练通过: Database cluster state = shut down` ——**本手册最关键的验收点**
8. `[backup] 按原副本数扩容: bh-postgres`
9. `[backup] 完成: <目录>` + `du -sh` 大小

**验收检查**：
```bash
ls -l <上面输出的备份目录>/        # 预期：baihua-postgres-pv.tgz、k8s-config.yaml、k8s-pv.yaml、k8s-pvc.yaml、backup.meta
cat <备份目录>/backup.meta          # 预期：form=k8s、namespace=baihua、archive baihua-postgres-pv /opt/baihua/postgres、replica bh-postgres 1
grep -c 'kind: Secret' <备份目录>/k8s-config.yaml   # 预期 ≥1（含 baihua-secret）
kubectl -n baihua get pods          # 预期：bh-postgres 重新 Running（扩容回来了）
kubectl -n baihua exec deploy/bh-postgres -- psql -U baihua -d baihua -c "SELECT * FROM bh_backup_test;"   # 数据仍在
```

**失败处置**：
- 演练 pod 起不来 → 输出会显示 `[backup] 演练降级: 一次性 pod 起不来（跳过 pg_controldata）...`，
  备份仍会成功。这不算 FAIL，但**必须记录**（说明 `kubectl run --overrides` 在该环境有问题，
  摘录 `kubectl get pod bh-backup-verify-<ts> -o yaml` 与 `kubectl describe pod` 输出）。
- 若演练输出 `Database cluster state = in production` → FAIL：说明缩容后数据仍被写入，保留现场。

## 4. 第二轮 `bh backup`（补齐 vaults/data 目录，验证多 PV 全量备份）

```bash
mkdir -p /opt/baihua/vaults-test /opt/baihua/data
echo "vaults-backup-test" > /opt/baihua/vaults-test/BACKUP_TEST.txt
echo "data-backup-test"  > /opt/baihua/data/BACKUP_TEST.txt
bash tools/bh/bh.sh backup
```

**预期**：本次**没有**"跳过"告警；tar 了 3 个 PV（postgres/vaults/data，openwebui 仍跳过——
`/opt/baihua/open-webui-data` 未创建）；演练通过；扩容恢复。记下这个新备份目录（第 5 节用它）。

```bash
ls <新备份目录>/   # 预期：baihua-postgres-pv.tgz、baihua-vaults-pv.tgz、baihua-data-pv.tgz、k8s-config.yaml、k8s-pv.yaml、k8s-pvc.yaml、backup.meta
tar -tzf <新备份目录>/baihua-vaults-pv.tgz   # 预期：列表含 BACKUP_TEST.txt
```

## 5. 破坏数据 → `bh restore` 恢复（核心闭环）

### 5.1 三个方向的破坏

```bash
# ① 删表
kubectl -n baihua exec deploy/bh-postgres -- psql -U baihua -d baihua -c "DROP TABLE bh_backup_test;"
# ② 删 vaults 文件
rm /opt/baihua/vaults-test/BACKUP_TEST.txt
# ③ 篡改 configmap（备份后新加的 key，restore 回放备份版时应被移除）
kubectl -n baihua patch configmap baihua-config -p '{"data":{"BH_RESTORE_TEST":"corrupted"}}'
kubectl -n baihua get configmap baihua-config -o jsonpath='{.data.BH_RESTORE_TEST}'   # 预期输出 corrupted
```

### 5.2 恢复

```bash
# 错误路径先测两个（预期都失败且不动数据）：
bash tools/bh/bh.sh restore; echo "exit=$?"                  # 预期：用法提示，exit=1
bash tools/bh/bh.sh restore /tmp -y; echo "exit=$?"          # 预期：不是 bh backup 产物，exit=1

# 正式恢复（AI 非交互场景用 -y；人工执行可去掉 -y 验证 yes 交互提示）：
bash tools/bh/bh.sh restore <第 4 节的备份目录> -y
```

**预期输出要点**：
1. `[restore] 缩容: bh-postgres，等待 pod 终止...`
2. `[restore] baihua-postgres-pv -> /opt/baihua/postgres`（vaults/data 同样各一条）
3. `[restore] 回放 secret/configmap...`
4. `[restore] 按原副本数扩容...`
5. `[restore] postgres 就绪（pg_isready 通过）`
6. `[restore] 完成。旧数据保留为 *.pre-restore-<时间>（确认无误后可删除）`

### 5.3 恢复结果验收

```bash
# ① 表回来了
kubectl -n baihua exec deploy/bh-postgres -- psql -U baihua -d baihua -c "SELECT * FROM bh_backup_test;"
# 预期：一行 (1 | before-backup)

# ② vaults 文件回来了
cat /opt/baihua/vaults-test/BACKUP_TEST.txt        # 预期：vaults-backup-test
cat /opt/baihua/data/BACKUP_TEST.txt               # 预期：data-backup-test

# ③ configmap 回放：篡改 key 被移除
kubectl -n baihua get configmap baihua-config -o jsonpath='{.data.BH_RESTORE_TEST}'   # 预期：空输出

# ④ 旧数据保留
ls -d /opt/baihua/postgres.pre-restore-* /opt/baihua/vaults-test.pre-restore-*        # 预期：两个目录都在

# ⑤ postgres 数据目录属主正确（postgres 容器以 uid 999 写数据）
ls -ln /opt/baihua/postgres | head -3   # 预期：目录/文件属主为 999（数字属主，--numeric-owner 保留）
```

## 6. 收尾清理（全部通过后执行）

```bash
# 删除命名空间与测试数据
bash tools/bh/bh.sh destroy                 # kubectl delete namespace baihua
rm -rf /opt/baihua/vaults-test* /opt/baihua/data* /opt/baihua/postgres* /opt/baihua/logs /opt/baihua/models
rm -rf backup/baihua-backup-*               # 备份产物（含密码，勿留存）
# 还原清单改动
git checkout -- k8s/03-pvc.yaml
git status                                  # 预期：working tree clean（或只剩你自己的文件）
# （可选）整台机器不再用时卸载 k3s：
# /usr/local/bin/k3s-uninstall.sh
```

## 7. （可选）全栈验收：`bh up` 后重跑备份/恢复

> 需要**拉基础镜像 + nuget restore**（mcr.microsoft.com / daocloud 加速），首次约 10-30 分钟，
> 网络受限的机器慎做。目的：验证**多 Deployment 同时缩容/扩容**（server/webui/postgres/open-webui）
> 与 Traefik 入口下的完整业务闭环。

```bash
bash tools/bh/bh.sh up                      # 构建 server/webui 镜像 + deploy 全栈
bash tools/bh/bh.sh status                  # 预期：全部 Deployment 就绪，端口 80/8788/5177 可连
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8788/api/health || true   # 或任一 200 端点
# 然后重复第 4/5 节：backup（预期缩容列表含 4-5 个 Deployment）→ 破坏 → restore → 验收
# 额外验收：restore 后 curl 上面同一端点，业务可用
```

## 8. 验收报告模板（执行完按此汇报）

```
## k8s backup/restore 真机验收报告
- 机器：<OS/架构/内核>；k3s 版本：<vX.Y.Z>；仓库 commit：<hash>
- 执行路径：快速路径 / 快速+全栈
- 用时：备份 X 分 Y 秒 / 恢复 X 分 Y 秒；备份产物大小：<du -sh>

| # | 验收点 | 结果 |
|---|--------|------|
| 1 | 最小栈部署（postgres Running） | PASS/FAIL |
| 2 | backup 跳过分支（vaults/data 目录不存在 → 告警跳过） | PASS/FAIL |
| 3 | 缩容→冷备→按原副本数扩容（服务自动恢复） | PASS/FAIL |
| 4 | 恢复演练 pg_controldata（"shut down" / 降级 / FAIL 三态记录） | <实际状态> |
| 5 | 第二轮全量备份（3 PV tar + backup.meta + config 导出） | PASS/FAIL |
| 6 | restore 错误路径（无参 / 非备份目录 exit=1） | PASS/FAIL |
| 7 | 数据恢复闭环（表/vaults 文件/configmap 回放三方向） | PASS/FAIL |
| 8 | .pre-restore- 旧数据保留 + 属主 999 保留 | PASS/FAIL |
| 9 | （可选）全栈多 Deployment 缩扩容 + 业务端点恢复 | PASS/FAIL/未做 |

异常与摘录：<任何 FAIL/降级/意外输出，原样粘贴关键日志>
清理确认：<已按第 6 节清理 / 保留现场原因>
```
