# GitHub Actions 构建与发布

## 自动构建

推送代码到 `main`、`beta` 或提交 Pull Request 时，`Build (CI)` 使用 Windows 和 .NET 10 编译 x64 Debug 版本。纯文档修改会跳过。也可在 Actions 页面手动运行。

成功后，在该次运行页面的 Artifacts 下载 `PCL_Plus-Debug-x64-运行编号`，保留 14 天。解压后保留整个目录运行；需要 .NET 10 Desktop Runtime。

## 手动发布

1. 修改 `Plain Craft Launcher 2/metadata.json` 的 `version.base`、`version.suffix` 和递增的 `version.code`。
2. 在 `docs/CHANGELOG.md` 更新发布说明，也可提交 `release-assets/notes-版本号.md`。空后缀版本如 `2.6.3`，非空后缀版本如 `2.6.3-Beta1`。
3. 将改动提交并推送到 GitHub。
4. 打开仓库 **Actions → Release publish → Run workflow**，选择包含改动的分支。
5. `draft` 控制是否创建草稿；`skip_ota` 控制是否跳过 OTA 仓同步。

发布会生成 x64 单文件 exe、SHA256 校验文件和源码 ZIP。exe 需要 .NET 10 Desktop Runtime。空后缀、`R`、`release`、`hotfix` 开头的后缀走 Release 线，其他后缀走 Beta 预发布线。

发布标签明确指向本次构建的提交。已存在的标签如果指向其他提交，工作流会失败；已存在的 Release 不会被覆盖，请递增版本后再发布。发布产物也保存到 Artifacts 30 天，便于发布步骤失败时下载检查。

## 标签发布

也可以推送与 metadata 版本一致的标签触发发布，例如 metadata 为 `base=2.6.3`、`suffix=""` 时：

```powershell
git tag v2.6.3
git push origin v2.6.3
```

Beta 示例：`base=2.6.4`、`suffix=Beta1` 对应 `v2.6.4-Beta1`。标签与版本不一致会在构建前失败。标签触发默认直接发布；需要草稿时使用手动发布。

## 仓库设置

- 在 Settings → Actions → General 中启用 Actions，并允许仓库内的工作流运行。工作流通过 `contents: write` 请求当前仓库 Release 写权限；组织策略也必须允许此权限。
- 在 Settings → Secrets and variables → Actions 添加可选的 `CLIENT_ID`，用于 Microsoft 登录。没有该值仍可编译，但构建产物的 Microsoft 登录不可用。
- 可选 `OTA_TOKEN` 用于向 `x1B1C1/PCL_Plus_OTA` 同步发布，需要该仓库的 Contents 写权限。未配置时自动跳过。Fork 使用者如需同步自己的 OTA 仓，应先修改 release.yml 中的 `OTA_REPO`。
- 当前仓库发布使用自动提供的 `GITHUB_TOKEN`，无需额外创建个人访问令牌。

不要将凭据写入源码或发布说明。Pull Request 构建不依赖私密凭据即可完成编译。
