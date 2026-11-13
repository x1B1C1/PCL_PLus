# 版本体系与发布规范

本文件说明 PCL_Plus 的版本号怎么定、发布物怎么命名、GitHub Release 怎么打勾。
规则参照 `askbbtsk/pcl-lite` 的发布实践（97 个 Release：正式版带 `-hotfix-N`，预发布带
`-pre-N` / `-snapshot-N` / `-nightly-N`，资产按 `Release` / `Beta` 两条线分开命名），
并结合 PCL 自身已有的通道判定逻辑（`PCL.Core\App\Updates\UpdateChannelLevels.cs`）。

---

## 一、五种版本类型

不稳定等级由 `UpdateChannelLevels.FromSuffix` 从后缀判定，只有四档：
`Stable(0)` / `Preview(1)` / `Snapshot(2)` / `Latest(3)`。
判定统一写成「版本等级 <= 通道等级」，所以高通道**包含**低通道。

| 类型 | GitHub tag | `metadata.json` 的 `suffix` | 界面显示 | 等级 | Release 勾选 |
|---|---|---|---|---|---|
| 正式版 | `v3.0.0` | `""` | `3.0.0` | Stable | 不勾 prerelease |
| 正式版（R 写法） | `v3.0.0-R` | `R` | `3.0.0R` | Stable | 不勾 |
| 热修复 | `v3.0.0-hotfix-1` | `hotfix-1` | `3.0.0-hotfix-1` | Stable | 不勾 |
| 测试版 / 预发布 | `v3.0.1-beta-1` | `Beta1` | `3.0.1-Beta1` | Preview | **勾 prerelease** |
| 快照 | `v3.0.1-snapshot-1` | `snapshot-1` | `3.0.1-snapshot-1` | Snapshot | **勾 prerelease** |
| 夜间版 | `v3.0.1-nightly-1` | `nightly-1` | `3.0.1-nightly-1` | Latest | **勾 prerelease** |

要点：

- **tag 必须是 SemVer 合法**。`v3.0.1-beta-1` ✅、`v3.0.1Beta1` ❌（会被
  `UpdateManifestPlan.OrderByPreference` 直接丢弃）。
- `R` 是正式版的另一种写法，只在**界面显示**时紧贴版本号（`3.0.0R`）；
  参与比较的名字是 `Metadata.SemVerName`（`3.0.0-R`），由 `ModBase.versionSemVerName` 提供。
- 热修复与正式版同属 Stable 通道——它是「打在同一个号上的补丁」，稳定版用户必须收得到。
- `beta` 前缀决定**清单分支**：`ProjectLinks.ManifestBranch` 看到 suffix 以 `beta` 开头
  （大小写不敏感）就取 `beta` 分支，否则取 `main`。所以测试版必须叫 `BetaN` / `beta` 开头，
  快照与夜间版若也要走 beta 分支，需额外确认该判断。

## 二、发布物命名

每条发布线固定两个资产名，与版本号解耦，方便直链下载与 OTA 清单引用：

| 发布线 | 单文件 exe | 校验 | 源码 | 备选 |
|---|---|---|---|---|
| 正式版 / 热修复 | `PCL_Plus_Release_x64.exe` | `.sha256` | `PCL_Plus-<版本>-src.zip` | `PCL_Plus_x64.exe`（直链，保留兼容） |
| 测试版 / 快照 / 夜间 | `PCL_Plus_Beta_x64.exe` | `.sha256` | 同上 | `PCL_Plus_v<版本>_x64.exe`（版本留档） |

本地 `release\` 仍然累积每个版本的单文件 exe（`PCL_Plus_v3.0.1-Beta1_x64.exe`）；
`release-assets\` 放校验值、说明与文件夹版。每个 EXE 同时写一份 `.sha256`。

`tools\build-release.ps1` 会自动按 `metadata.json` 的 suffix 判定发布线，
产出 `PCL_Plus_Release_x64.exe` 或 `PCL_Plus_Beta_x64.exe`，并复制一份版本化留档。

## 三、发布检查清单

1. `metadata.json` 改 `base` / `suffix` / `code`（`code` 只能递增；`base` 必须是三段，
   两段会让 `SemVer.Parse` 抛异常）。
2. `docs\CHANGELOG.md` 顶部加一节，标题格式 `## <显示版本>（正式线 / 测试线）`。
3. `release-assets\notes-<版本>.md` 写 GitHub Release 正文。
4. `dotnet build "Plain Craft Launcher 2\Plain Craft Launcher 2.csproj" -c Debug` 必须是 0 错误。
5. `powershell -ExecutionPolicy Bypass -File tools\build-release.ps1` 出包，
   确认 `release\` 里新 exe 的大小与哈希。
6. 推送源码分支（正式线 `main` / 测试线 `beta`）。
7. 建 Release：正式版 **不要**勾 prerelease；预发布 **一定要勾**——否则它会抢走
   `releases/latest`，把正式用户推到测试线上。

   也可以交给 CI：`Actions → Release publish → Run workflow`，版本号从 `metadata.json`
   自动读取、`Beta` 线自动带 prerelease，详见 [CI.md](CI.md)。

8. 发布后复核：`GET /repos/<owner>/<repo>/releases/latest` 是否仍指向正式版。

## 四、与上游 PCL-Lite 的差异

| 项目 | PCL-Lite | PCL_Plus |
|---|---|---|
| 发布机器人 | GitHub Actions（`github-actions[bot]`） | GitHub Actions（`release.yml`）或本地脚本 `tools\github\release.py` |
| 构建校验 | 源码结构 / 事件签名 / 主题往返 四个工具项目 | 只验证编译 0 错误 |
| 架构 | x64 + ARM64 双份 | 仅 x64 |
| 资产签名 | `.sigstore.json`（artifact attestation） | `.sha256` |
| 源码包 | 无 | 有（`*-src.zip`） |
| 早期通道 | `-early-N`（走正式通道） | 不设该档 |
| 双服务器 | 单仓 | 源码仓 `PCL_PLUS` + 备用 `PCL_Plus_OTA` |
