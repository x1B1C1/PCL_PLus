# 持续集成与自动发版

仓库里有两个 GitHub Actions 工作流，都在 `.github/workflows/` 下。

| 文件 | 触发方式 | 作用 |
|---|---|---|
| `build.yml` | 推到 `main` / `beta`、提 PR、手动 | 只验证「能不能编译」，不产出发布物 |
| `release.yml` | 手动（Actions 页面点 Run workflow） | 构建单文件 exe、按发布线改名、算 sha256、建 Release |

## 为什么不是照搬 PCL-Lite 的四个工作流

PCL-Lite 的 `build-test.yml` / `reusable-build.yml` / `release-publish.yml` / `ui-smoke.yml`
依赖它自己仓库里的四个工具项目：

- `tools/SourceStructure`（源码结构契约检查）
- `tools/WpfEventSmoke`（XAML 事件签名静态校验）
- `tools/ThemeSmoke`（主题控件往返测试）
- `tools/UiSmoke`（原生控件检测）

这四个项目 PCL_Plus 没有，直接复制工作流会在 `dotnet run --project tools/...` 一步失败。
所以只保留了「构建 + 发版」这条主干，校验步骤换成我们实际有的东西：编译必须 0 错误。

## 需要配置的仓库 Secret

进入 `Settings → Secrets and variables → Actions`：

| 名称 | 必需 | 用途 |
|---|---|---|
| `CLIENT_ID` | 建议 | Microsoft OAuth client_id，编译期由源码生成器注入 `PCL_MS_CLIENT_ID`。不配也能构建，只是微软登录不可用 |
| `OTA_TOKEN` | 可选 | 用于把同一份 Release 同步到 `x1B1C1/PCL_Plus_OTA` 的 Personal Access Token（需要该仓库的 `contents: write`）。不配则只发布当前仓库 |

`OTA_TOKEN` 必须是 PAT，不能用默认的 `GITHUB_TOKEN`——默认令牌只能操作当前仓库。

## 怎么发一版

1. 本地改好 `Plain Craft Launcher 2/metadata.json` 的 `base` / `suffix` / `code`
   （`code` 只能递增），补 `docs/CHANGELOG.md` 与 `release-assets/notes-<版本>.md`。
2. 提交并推到对应分支（正式线 `main` / 测试线 `beta`）。
3. 打开 `Actions → Release publish → Run workflow`：
   - `draft`：先建草稿，确认没问题再去 Release 页面点 Publish；
   - `skip_ota`：只发源码仓，不碰 OTA 仓。
4. 工作流自己从 `metadata.json` 算出版本号与发布线，不需要手填版本。

## 工作流内部做了什么

1. 读 `metadata.json`（先剥 UTF-8 BOM），拼出 `base-suffix`；
2. 把 `2.5.2R` 这种「给人看」的名字归一成 `2.5.2-R`——否则 tag 不是合法 SemVer，
   会被 OTA 清单的 `UpdateManifestPlan.OrderByPreference` 丢掉；
3. 判定发布线：空后缀 / `R` / `hotfix-N` → `Release`，其余（`Beta1`、`snapshot-N`、`nightly-N`）→ `Beta`；
4. `dotnet publish` 前先删 `obj` / `bin`：源码生成器只在 `PCL_WRITE_SECRET` 存在时注入
   `PCL_*`，增量编译拿不到新 secret；
5. 产出三个文件：`PCL_Plus_<线>_x64.exe`、`PCL_Plus_v<版本>_x64.exe`、`PCL_Plus-<版本>-src.zip`，
   每个 exe 配一份 `.sha256`；
6. `gh release create` 发本仓；`Beta` 线自动带 `--prerelease`——不勾的话它会抢走
   `releases/latest`，把正式用户推到测试线上；
7. 若配置了 `OTA_TOKEN`，再用同样的资产在 OTA 仓建一份（不带源码包）。

## 已知限制

- **只有 x64**。ARM64 需要额外的 `Platform=ARM64` 构建矩阵，尚未加。
- **没有签名**。PCL-Lite 用 `sigstore/gh-action-sigstore-python` 给二进制做 attestation，
  我们的 OTA 目前用 `.sha256` 校验，接入签名要同时改客户端 `UpdateSignatureVerifier`。
- **Release 只建不删**。重复触发同版本会因 tag 已存在而失败，这是刻意的：宁可报错，
  也不要静默覆盖已发布的资产。

## 排查：工作流“解析失败”长什么样

如果 `GET /repos/<owner>/<repo>/actions/workflows` 里某个条目的 `name` 显示成**文件路径**
（例如 `.github/workflows/release.yml`），说明 GitHub 没把它当成合法工作流解析，
它不会被 `push` 触发，`Run workflow` 按钮也不会出现。踩过的三个坑：

1. **`workflow_dispatch` 型工作流必须存在于默认分支**。只把 `release.yml` 推到 `beta` 时，
   GitHub 在默认分支 `main` 上找不到它的 dispatch 入口，就把它当成普通 yml。
   修法：让 `main` 也有这两个文件（本次即用 Contents API 单独提交到 `main`）。
2. **`secrets` 不能出现在 `if:` 表达式里**。`if: ${{ !inputs.skip_ota && secrets.OTA_TOKEN != '' }}`
   会让整个文件解析失败。改成 `if: ${{ !inputs.skip_ota }}`，在 `run:` 开头判断
   `[string]::IsNullOrWhiteSpace($env:GH_TOKEN)` 再决定是否跳过。
3. **仓库根或子项目里有多份 `.gitignore`**。`PCL.Core/.gitignore` 里还有一条 `Backup*/`，
   把 `PCL.Core/Minecraft/Saves/Backups/` 整份源码忽略了：本地编译正常，CI 上却报
   `CS0234: 命名空间 'PCL.Core.Minecraft.Saves' 中不存在类型 'Backups'`。
   排查方式：`git add -A` 后用 `git ls-files | Select-String 'Saves/Backups'` 数文件。

