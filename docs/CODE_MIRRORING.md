# 代码镜像：GitHub 主仓 → 华为云 CodeHub / 阿里云 Codeup

> **定位**：百花代码主仓在 GitHub（`github.com/luminsw/baihua`）。本文是"把代码再镜像一份到国内
> 代码托管平台"的建议做法——目的：① 异地备份防单点；② 国内机器 clone 不走代理也快。
> 适用于新开发者/新机器，照做即可，无需决策。

## 0. 前提事实（做镜像前先知道）

- 本仓库**不含敏感文件**：`backup/`（含备份密钥）、`dist/` 等全部被 gitignore，
  git 内容可放心镜像到任何托管平台。
- **git 镜像带不走两样东西**：
  1. **Release 附件**——v0.1.0 的三个部署 zip 是 GitHub Release 资产，不是 git 对象；
  2. **CI**——`.github/workflows/` 只有 GitHub Actions 认，镜像端要跑 CI 得另配
     （Codeup 用云效 Flow，CodeHub 用其自带流水线）。
- 当前历史很干净：单提交 `main` + tag `v0.1.0`，镜像正好从零开始。

## 1. 方式一：平台侧一键导入（一次性快照，最省事）

阿里云 Codeup（codeup.aliyun.com）与华为云 CodeHub 都有「导入仓库」功能：

1. 平台上新建仓库 → 选"**导入外部仓库**"
2. 填 `https://github.com/luminsw/baihua.git`（公开仓库，无需 token）
3. 平台自己拉一遍完成镜像

适合"一次性备份"。缺点：之后 GitHub 更新不会自动跟上，要手动重新同步。

## 2. 方式二：git 双远程持续同步（推荐）

```powershell
# 1. 在平台上新建【空】仓库（不要勾选"初始化 README"），复制它给出的地址
#    阿里 Codeup：  https://codeup.aliyun.com/<组织>/baihua.git
#    华为 CodeHub：  仓库页「克隆/下载」里复制 HTTPS/SSH 地址

# 2. 添加第二远程并推送（main + 全部 tag）
git remote add mirror <平台仓库地址>
git push mirror main
git push mirror --tags

# 3. （推荐）让以后每次 push 自动同步两边：
git remote set-url --add --push origin git@github.com:luminsw/baihua.git   # 先补回 GitHub
git remote set-url --add --push origin <平台仓库地址>
# 此后 git push origin main 一条命令同时推 GitHub + 镜像
```

若目标仓库曾导入过旧历史（比如镜像过 v0.1.0 之前的版本），用 `git push mirror main --force` 覆盖。

## 3. 认证

| 方式 | 做法 |
|---|---|
| HTTPS | 平台账号 + **个人访问令牌**（Codeup：个人设置 → 个人访问令牌；CodeHub：安全设置 → HTTPS 密码）；Windows 上首次 push 由 Git 凭据管理器接管 |
| SSH | `ssh-keygen` 生成密钥对，公钥贴到平台「SSH 公钥」设置页；远程地址换成 SSH 形式 |

凭据只放系统凭据管理器 / `~/.ssh/`，**任何情况不进仓库**。

## 4. 维护口径

- 镜像方向始终 **GitHub → 国内托管**：日常开发推 GitHub（方式二第 3 步会自动带过去），
  不在镜像端直接提交，避免两边分叉。
- 打 tag 发版后记得 `git push mirror --tags`（方式二第 3 步的 pushurl 只对分支生效，
  tag 需单独推，或加 `git config push.followTags true` 让 `git push` 连带已合并 tag）。
- 发版时的部署 zip 如需在镜像端可得，去平台 Release 页手动上传同版本附件。
