# wslc 形态 `bh backup` / `bh restore` 真机验收手册（Windows）

> **给执行本手册的 AI 助手**：你在 Windows 机器上按本手册逐步执行并验收。
> - 用 **pwsh 7** 执行；每步对照「预期结果」逐项核对，**任何一步失败立即停止**：保留现场（不要清理）、完整摘录命令输出，按文末模板汇报。
> - 不要跳步、不要自创命令、不要"顺手修复"被测代码——你的任务是**验收并报告**，不是修 bug。
> - 路径 A 会在测试机上拉起完整百花栈（约 3-5 GB 镜像），确认第 1 节的磁盘/网络要求后再开始。
> - 手册末尾有收尾清理段，全部通过后才执行。
>
> 对应 Linux k8s 形态的手册：`docs/K8S_BACKUP_RESTORE_TEST.md`（两份口径对齐，报告模板同构）。

## 0. 背景（备份范围与本次验收重点）

`bh backup` / `bh restore`（Windows wslc 形态）是数据安全网功能，实现见
`tools/bh/win/wslc/bh.ps1`（`Backup-Stack` / `Restore-Stack`）。

**备份范围（设计边界，验收报告需如实转述）**：
- ✅ 卷 `bh-pgdata`（PostgreSQL 数据）/ `bh-data`（应用数据：密钥、配对设备等）/ `bh-openwebui-data`（AI 对话历史）
- ✅ `docker/.env.wslc` → `env.wslc.bak`（PG_PASSWORD 与 pgdata 配对，必须成对备份/恢复）
- ❌ `bh-logs`（日志，不备份）
- ❌ `VAULTS_DIR`（知识库文件本体，宿主目录 bind mount）——**不在卷备份范围**，知识库文件需另行文件级备份（robocopy/Copy-Item）

以下路径是本次验收的重点（每步都设计了预期结果）：
1. 冷备份：栈在跑时自动停栈 → tar 卷 → 自动重启（快速路径不触发，见 2.2 注）
2. 卷 tar helper（postgres 镜像做一次性容器）
3. **恢复演练**：scratch 卷 + 一次性 postgres 容器 → `pg_isready` + `SELECT 1` → 现场清理（本手册核心验收点）
4. `restore` 三条错误路径（不动数据直接 exit 1）
5. 破坏 → 恢复全闭环（表 / 文件 / 配置三方向）
6. 恢复末尾自动重启全栈（拉镜像 + 等端口就绪）

## 1. 环境要求与预检

| 项 | 要求 |
|---|---|
| OS | Windows 10 1809+ / Windows 11 x64 |
| WSL | ≥ 2.9.3（WSL 3，自带 `wslc.exe`；缺则 `wsl --update`） |
| Shell | pwsh 7（本手册命令按 pwsh 7 写；`bh.cmd` 走 5.1 也可，但不要混用） |
| 磁盘 | ≥ 10 GB 空闲（镜像 + 备份产物） |
| 网络 | 能拉 `docker.m.daocloud.io`（postgres/nginx）、华为云 SWR（bh-server/bh-webui）、`ghcr.nju.edu.cn`（open-webui） |
| 版本 | 仓库 main（`git log --oneline -1` 确认） |
| 权限 | 普通用户即可；:80 被 http.sys 占用时仅 nginx 告警，不算失败 |

```powershell
# 1a. 预检（逐条执行，任何一条不满足先解决再继续）
pwsh -Version                    # 预期：PowerShell 7.x
wslc --version                   # 预期：WSL wslc 客户端版本（≥ 2.9.3）；报错则 wsl --update
Get-PSDrive C | Select-Object Free    # 预期：Free ≥ 10GB

# 1b. 获取仓库
git clone https://github.com/luminsw/baihua.git
cd baihua
git log --oneline -1             # 预期：main 最新提交
Set-ExecutionPolicy -Scope Process Bypass -Force
```

> 也可用 GitHub Release 附件 `baihua-deploy-v0.1.0.zip` 解压后验收（最贴近最终用户形态，
> 无需下文改 `BAIHUA_IMAGE_SOURCE` 的步骤，其余命令相同、路径按解压目录）。

## 2. 路径 A · 准备与造测试数据（不启全栈）

### 2.1 配置（等价部署包形态）

```powershell
.\tools\bh\bh.ps1 env
# 预期：打印 [env] docker\.env.wslc 及全部键值；首次运行生成该文件
#（PG_PASSWORD 随机 24 位；BAIHUA_WSL_MIRROR=docker.m.daocloud.io；BAIHUA_IMAGE_SOURCE=auto）

# git clone 有源码时 auto=本地构建；验收要拉现成镜像（与瘦部署包一致）→ 改 registry：
(Get-Content docker\.env.wslc) -replace '^BAIHUA_IMAGE_SOURCE=auto$', 'BAIHUA_IMAGE_SOURCE=registry' |
    Set-Content docker\.env.wslc
Select-String -Path docker\.env.wslc -Pattern '^BAIHUA_IMAGE_SOURCE='
# 预期：BAIHUA_IMAGE_SOURCE=registry

# 公共变量（后续步骤都用；直接整段执行）
$mirror = (Select-String -Path docker\.env.wslc -Pattern '^BAIHUA_WSL_MIRROR=(.*)$').Matches[0].Groups[1].Value.Trim()
$pgImg  = if ($mirror) { "$mirror/library/postgres:18-alpine" } else { 'postgres:18-alpine' }
$pgPass = (Select-String -Path docker\.env.wslc -Pattern '^PG_PASSWORD=(.+)$').Matches[0].Groups[1].Value
$pgImg; $pgPass.Substring(0,4) + '****'    # 预期：镜像名 + 密码前 4 位（不打印全文）

.\tools\bh\bh.ps1 pull           # 预期：[pull] <pgImg> ... 成功（daocloud 镜像）
```

### 2.2 造测试数据（只建 pgdata，另两个卷留给第一轮备份验证"跳过"分支）

> 测试容器故意命名 `bh-test-pg`（不叫 `bh-postgres`）：`bh backup` 通过"bh-postgres 是否在跑"
> 判断要不要停栈/重启——不在跑就不触发，快速路径不会拉全栈镜像。停栈/重启分支由第 5 节
> restore 末尾的 `Start-Stack`（全栈自启）覆盖。

```powershell
wslc volume create bh-pgdata

wslc run -d --name bh-test-pg -v bh-pgdata:/var/lib/postgresql `
    -e POSTGRES_DB=baihua -e POSTGRES_USER=baihua -e POSTGRES_PASSWORD=$pgPass $pgImg

# 等就绪（≤60s）
$deadline = (Get-Date).AddSeconds(60)
do { $r = wslc exec bh-test-pg pg_isready -U baihua -d baihua 2>&1; Start-Sleep 2 }
until ("$r" -match 'accepting connections' -or (Get-Date) -gt $deadline)
"$r"      # 预期：... accepting connections

# 造数（备份后用来验证恢复）
wslc exec bh-test-pg psql -U baihua -d baihua -c "CREATE TABLE bh_backup_test(id int, note text); INSERT INTO bh_backup_test VALUES (1,'before-backup');"
wslc exec bh-test-pg psql -U baihua -d baihua -c "SELECT * FROM bh_backup_test;"
# 预期：一行 (1 | before-backup)

# 干净停机（冷备前提；容器 stop = PG 干净 shutdown）
wslc container stop bh-test-pg
wslc container rm bh-test-pg
```

## 3. 路径 A · 第一轮 `bh backup`（跳过分支 + 恢复演练）

```powershell
.\tools\bh\bh.ps1 backup
```

**预期输出要点**（顺序大致如下）：
1. `[backup] bh-pgdata -> bh-pgdata.tgz ...（数据量大时需几分钟）` + `[backup]   bh-pgdata.tgz  <n> MB`
2. `bh-data` / `bh-openwebui-data` **没有任何输出**——不存在 的卷是**静默跳过**（与 k8s 版打印告警不同），只记录在 backup.json 的 `skipped` 数组。如实记录即可，属可改进项，不算 FAIL
3. `[backup] docker/.env.wslc -> env.wslc.bak（PG_PASSWORD 与卷配对，恢复时一起还原）`
4. `[verify] pgdata 恢复演练（scratch 卷 + 一次性 postgres 容器）...`
5. `[verify] 备份可用：pgdata 已在 scratch 卷成功启动并完成查询`（绿色）——**本手册最关键的验收点**
6. **没有** `[backup] 停止全栈` / `[backup] 重新启动全栈`（2.2 的设计，本路径不触发）
7. `[backup] 完成：<仓库>\backup\baihua-backup-<时间>` + `[backup] 恢复用：bh restore "<目录>"`

**验收检查**：

```powershell
$bak = (Get-ChildItem backup\baihua-backup-* | Sort-Object Name | Select-Object -Last 1).FullName
Get-ChildItem $bak | Select-Object Name,Length
# 预期：bh-pgdata.tgz、env.wslc.bak、backup.json 三个文件

Get-Content (Join-Path $bak 'backup.json')
# 预期：type=baihua-wslc-backup；volumes 含 bh-pgdata；
#       skipped 含 bh-data 和 bh-openwebui-data；stackWasRunning=false；pgImage=<pgImg>

# 演练现场清理干净（scratch 卷与一次性容器都已删）
wslc volume inspect bh-backup-verify 2>&1 | Select-Object -First 2     # 预期：报错/不存在
wslc container inspect bh-backup-verify 2>&1 | Select-Object -First 2  # 预期：报错/不存在
```

**失败处置**：若出现 `[verify] 恢复演练失败：postgres 60s 内未就绪...` 或 `psql 查询失败`，
脚本直接 throw——保留现场，摘录 `wslc container list --all` 与备份目录内容后停止。

## 4. 路径 A · 第二轮 `bh backup`（三卷全量）

```powershell
# 补齐另两个卷并放标记文件
wslc volume create bh-data
wslc volume create bh-openwebui-data
wslc run --rm -v bh-data:/data $pgImg sh -c 'echo data-backup-test > /data/BACKUP_TEST.txt'
wslc run --rm -v bh-openwebui-data:/data $pgImg sh -c 'echo ow-backup-test > /data/BACKUP_TEST.txt'

.\tools\bh\bh.ps1 backup
```

**预期**：三个卷各一条 `-> .tgz` + 大小；无静默跳过（三卷都在）；演练通过。

```powershell
$bak2 = (Get-ChildItem backup\baihua-backup-* | Sort-Object Name | Select-Object -Last 1).FullName
Get-ChildItem $bak2 | Select-Object Name
# 预期：bh-pgdata.tgz / bh-data.tgz / bh-openwebui-data.tgz / env.wslc.bak / backup.json

(Get-Content (Join-Path $bak2 'backup.json') | ConvertFrom-Json) | Select-Object volumes,skipped
# 预期：volumes = bh-pgdata,bh-data,bh-openwebui-data；skipped 为空

tar -tzf (Join-Path $bak2 'bh-data.tgz')            # Windows 自带 bsdtar；预期列表含 BACKUP_TEST.txt
tar -tzf (Join-Path $bak2 'bh-openwebui-data.tgz')  # 同上
```

记下 `$bak2`——第 5 节恢复用它。

## 5. 路径 A · 破坏 → 错误路径 → `bh restore` → 验收（核心闭环）

### 5.1 错误路径 ×3（预期都失败且不动数据）

```powershell
.\tools\bh\bh.ps1 restore; "exit=$LASTEXITCODE"
# 预期：用法提示（bh restore <备份目录> [-y]），exit=1

.\tools\bh\bh.ps1 restore $env:TEMP -y; "exit=$LASTEXITCODE"
# 预期：[restore] ... 缺少 bh-pgdata.tgz（不是 bh backup 生成的备份目录），exit=1

Copy-Item $bak2 "$env:TEMP\bak-noenv" -Recurse
Remove-Item "$env:TEMP\bak-noenv\env.wslc.bak"
.\tools\bh\bh.ps1 restore "$env:TEMP\bak-noenv" -y; "exit=$LASTEXITCODE"
# 预期：[restore] 缺少 env.wslc.bak（PG_PASSWORD 与卷配对...），exit=1
```

### 5.2 三个方向的破坏

```powershell
# ① 删表（起一次性 PG）
wslc run -d --name bh-test-pg -v bh-pgdata:/var/lib/postgresql `
    -e POSTGRES_DB=baihua -e POSTGRES_USER=baihua -e POSTGRES_PASSWORD=$pgPass $pgImg
Start-Sleep 10
wslc exec bh-test-pg psql -U baihua -d baihua -c "DROP TABLE bh_backup_test;"
wslc container stop bh-test-pg; wslc container rm bh-test-pg

# ② 删 bh-data 标记文件
wslc run --rm -v bh-data:/data $pgImg sh -c 'rm /data/BACKUP_TEST.txt'

# ③ 篡改配置（restore 应回放备份版）
Add-Content docker\.env.wslc 'BH_RESTORE_TEST=corrupted'
Select-String -Path docker\.env.wslc -Pattern 'BH_RESTORE_TEST'    # 预期：一行 corrupted
```

### 5.3 恢复

```powershell
.\tools\bh\bh.ps1 restore $bak2 -y
# 人工执行可去掉 -y 验证交互：提示「确认恢复请输入 yes」，输其它任意值显示 [restore] 已取消 且 exit=0
```

**预期输出要点**：
1. `[restore] 将执行：` 四条计划 + `[restore] ⚠ 上述卷的现有数据将被覆盖，不可撤销`
2. `-y` 跳过确认；三个卷各一条 `[restore] bh-pgdata ...` / `bh-data ...` / `bh-openwebui-data ...`
3. `[restore] 配置已还原（docker/.env.wslc）`
4. **自动重启全栈**：`[start] postgres ...` → `[start] server ...`（首次拉 SWR 镜像 bh-server/bh-webui）
   → `[start] webui ...` → `[start] open-webui ...`（ghcr 南大镜像）→ `[start] nginx ...` → `[start] 完成`
5. `[restore] 完成。若行为异常：bh stop，把 pre-restore 配置拷回 docker/.env.wslc 后 bh start`

> 拉镜像 + server 首次初始化建表整体约 5-20 分钟（视网络）。`[start] server` 内部等 8788 就绪最多 180s，
> 超时会 throw——若失败先看 `.\tools\bh\bh.ps1 logs server` 再按模板汇报。

### 5.4 恢复结果验收

```powershell
# ① 表回来了（栈的 postgres 容器叫 bh-postgres）
wslc exec bh-postgres psql -U baihua -d baihua -c "SELECT * FROM bh_backup_test;"
# 预期：一行 (1 | before-backup)

# ② 标记文件回来了
wslc run --rm -v bh-data:/data $pgImg sh -c 'cat /data/BACKUP_TEST.txt'            # 预期：data-backup-test
wslc run --rm -v bh-openwebui-data:/data $pgImg sh -c 'cat /data/BACKUP_TEST.txt'  # 预期：ow-backup-test

# ③ 配置回放：篡改行消失 + pre-restore 留存
Select-String -Path docker\.env.wslc -Pattern 'BH_RESTORE_TEST'    # 预期：无输出
Get-ChildItem docker\.env.wslc.pre-restore-* | Select-Object Name  # 预期：一个留存文件

# ④ 栈健康
.\tools\bh\bh.ps1 status    # 预期：五容器（postgres/server/webui/nginx/open-webui）Running
.\tools\bh\bh.ps1 doctor   # 预期：体检通过（exit 0）
```

## 6. 路径 A · 收尾清理（全部通过后执行）

```powershell
.\tools\bh\bh.ps1 stop                        # 停全栈（删容器；卷保留，下一步手动清）

wslc volume remove -f bh-pgdata
wslc volume remove -f bh-data
wslc volume remove -f bh-logs
wslc volume remove -f bh-openwebui-data

# 备份产物含 PG_PASSWORD（env.wslc.bak），勿留存
Remove-Item -Recurse -Force backup\baihua-backup-*
Remove-Item -Recurse -Force "$env:TEMP\bak-noenv"
Remove-Item -Force docker\.env.wslc, docker\.env.wslc.pre-restore-* -ErrorAction SilentlyContinue

git status    # 预期：working tree clean（.env.wslc 与 backup/ 均被 gitignore）
# （可选）释放磁盘：wslc image list 查看 → wslc image remove <镜像>
```

## 7. 路径 B：生产机（已有家庭数据）非破坏性验收

> 适用：正在跑 wslc 栈、承载真实家庭数据的机器。**不在测试机时不要做破坏性方向**
> （5.2 的删表/删文件/篡改配置都跳过），恢复环节改为**等值回放**：restore 刚生成的备份，
> 恢复的内容与线上数据相同，停机窗口 ≈ 备份时长 + 恢复时长。

1. 选家庭服务空闲时段，先告知用户将停栈几分钟
2. `.\tools\bh\bh.ps1 status` 记录基线（容器列表与状态）
3. `.\tools\bh\bh.ps1 backup` → 预期：**停栈**（`[backup] 停止全栈（冷备份要求 PG 干净 shutdown）...`）
   → 三卷 tar → 演练通过 → `[backup] 重新启动全栈...`
4. 按第 3 节「验收检查」核对产物（backup.json 应为 `stackWasRunning=true`、skipped 为空）
5. `.\tools\bh\bh.ps1 status` / `doctor`：栈恢复运行、体检通过
6. （最严格闭环，可选）`.\tools\bh\bh.ps1 restore <刚生成的备份目录> -y` 等值回放；
   恢复后 `doctor` 通过 + 人工抽查：WebUI 登录、知识库列表、AI 对话页（:8080）
7. 报告中注明：**VAULTS_DIR（知识库文件）不在备份范围**，需另行文件级备份；备份目录含
   PG_PASSWORD，注意存放位置权限

## 8. 验收报告模板（执行完按此汇报）

```
## wslc backup/restore 真机验收报告
- 机器：<Windows 版本/架构>；WSL/wslc 版本：<wslc --version>；仓库 commit：<hash>
- 执行路径：路径 A（测试机破坏闭环）/ 路径 B（生产机非破坏）/ A+B
- 用时：备份 X 分 Y 秒 / 恢复 X 分 Y 秒；备份产物大小：<MB>

| # | 验收点 | 结果 |
|---|--------|------|
| 1 | 环境预检（pwsh/wslc/磁盘）+ bh env 生成 .env.wslc（随机 PG_PASSWORD） | PASS/FAIL |
| 2 | 一次性 postgres 初始化测试卷 + 造数（表 / 标记文件） | PASS/FAIL |
| 3 | 第一轮 backup：不存在卷静默跳过（backup.json.skipped 记录） | PASS/FAIL |
| 4 | 恢复演练（scratch 卷起 postgres → pg_isready + SELECT 1 → scratch/容器清理） | PASS/FAIL |
| 5 | 备份产物三件套（*.tgz / env.wslc.bak / backup.json）+ tar 内容抽查 | PASS/FAIL |
| 6 | restore 错误路径 ×3（无参 / 缺 bh-pgdata.tgz / 缺 env.wslc.bak，均 exit=1 不动数据） | PASS/FAIL |
| 7 | 破坏三方向（删表 / 删标记文件 / 篡改 .env.wslc） | PASS/FAIL |
| 8 | restore 恢复闭环（表 / 文件 / 配置全部回到备份时状态） | PASS/FAIL |
| 9 | 恢复末尾全栈自启 + doctor 通过 + pre-restore 配置留存 | PASS/FAIL |
| 10 | （路径 B）生产机等值回放闭环（backup → restore → doctor/人工抽查） | PASS/FAIL/未做 |

异常与摘录：<任何 FAIL/意外输出，原样粘贴关键日志>
已知观察：<如"跳过卷无告警输出"等可改进项，如实记录>
清理确认：<已按第 6 节清理 / 保留现场原因>
```
