# 构建与发布

从源码编出一个能双击运行的 exe，以及怎么发版。

## 一、环境要求

| 需要 | 版本 | 备注 |
| --- | --- | --- |
| Windows | 10 1809+ / 11 | WPF 项目，只能在 Windows 上编 |
| .NET SDK | 10.0 | `dotnet --version` 确认 |
| Git | 任意 | |

不需要 Visual Studio，`dotnet` CLI 就够。装了 VS 也可以直接用 `Plain Craft Launcher 2.slnx` 打开。

## 二、最短路径

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" `
  -c Release -p:Platform=x64 --no-self-contained `
  -p:DeleteExistingFiles=true -o ./publish-x64
```

产物：`publish-x64/Plain Craft Launcher 2.exe`，单文件约 19 MB。

`--no-self-contained` 意味着**用户机器上要装 .NET 10 桌面运行时**。想要免安装运行时就把这个参数去掉，
体积会涨到 60 MB 以上。

### 单文件 vs 文件夹版

单文件产物启动时，.NET 宿主必须先把原生库解压到 `%TEMP%\.net\<exe 名>\`。如果用户的 `%TEMP%`
不可写（受管电脑、临时目录被策略锁死、磁盘满），程序会在应用代码跑起来之前就失败，提示
`Failure processing application bundle ... Error code: 5`，应用内无法补救。

这类机器要给**文件夹版**：`dotnet publish` 时加 `-p:PublishSingleFile=false`，产出一个
约 60 个文件、27 MB 的普通发布目录，不需要任何临时解压。本仓库的 `release\` 只放 exe，
文件夹版与校验值放在 `release-assets\`：

```powershell
# 单文件进 release\，文件夹版与 .sha256 进 release-assets\
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -All -Portable
```

## 三、密钥注入

这是本项目最容易被坑的地方。

### 机制

`PCL.Core.SourceGenerators/EnvironmentInteropGenerator.cs` 在**编译期**扫描环境变量：

- 存在 `PCL_WRITE_SECRET` 时，把**所有** `PCL_` 前缀的变量（排除 `PCL_WRITE_SECRET` 本身）
  去掉 `PCL_` 前缀后，生成到 `EnvironmentInterop.g.cs`：

  ```csharp
  private static readonly Dictionary<string, string?> SecretDictionary = new()
  {
      ["MS_CLIENT_ID"] = @"...",
      ["CURSEFORGE_API_KEY"] = @"...",
  };
  ```

- 代码里通过 `Secrets.MSOAuthClientId`（内部就是 `EnvironmentInterop.GetSecret("MS_CLIENT_ID")`）读取。

所以：

- **源码里搜不到任何 client_id**，这是设计如此，不是漏了
- 密钥是程序集元数据里的 UTF-16 字符串，**能被从 exe 里抠出来**。别把别人的密钥当自己的用
- 没设环境变量编出来的包，登录会提示"未配置 Client ID"

### 怎么带密钥编译

`tools\build-release.ps1` 已经替你把这件事做好了：脚本开头默认设置
`PCL_WRITE_SECRET=1` 与本项目的 `PCL_MS_CLIENT_ID`，发布前会清掉
`PCL.Core` / `PCL.Core.SourceGenerators` / `Plain Craft Launcher 2` 的 `obj`、`bin`
（生成器不看环境变量，增量编译会复用旧产物，不清理的话密钥永远进不去），
所以**只要走这个脚本出包，client id 一定是嵌入的**。想换一个 Azure 应用：

```powershell
$env:PCL_MS_CLIENT_ID = "<另一个 client_id>"      # 先设，脚本会沿用
powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -All -Portable
```

手工发布时等价的做法：

```powershell
$env:PCL_WRITE_SECRET = "1"
$env:PCL_MS_CLIENT_ID = "<你自己的 client_id>"     # 怎么申请见 AZURE-LOGIN.md
dotnet build-server shutdown
Remove-Item PCL.Core\obj, PCL.Core\bin -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" `
  -c Release -p:Platform=x64 --no-self-contained -o ./publish-x64
```

验证是否真的编进去了（在产物里按 UTF-16 搜 GUID 即可）：

```powershell
$g = [Text.Encoding]::Unicode.GetBytes("c14b0370-8d75-42f8-b329-5b60d39e319f")
# 在 exe / PCL.Core.dll 的字节里找这段序列，命中就是嵌入了
```

也可以只在运行时给：`Secrets` 会回退读环境变量 `PCL_MS_CLIENT_ID`，这样不用重编译。
但**发布给别人时别依赖这个**，用户机器上没有这个变量。

### 基岩版 GDK 内容密钥（CIK）

基岩版的 GDK 包（`.msixvc`）是加密的 XVC 容器，解包需要微软商店分发的内容密钥。
密钥同样**不写进源码**，编译时一起注入：

```powershell
$env:PCL_WRITE_SECRET = "1"
$env:PCL_BEDROCK_RELEASE_CIK = "<正式版 CIK 的十六进制串>"   # 至少 48 字节
$env:PCL_BEDROCK_PREVIEW_CIK = "<预览版 CIK 的十六进制串>"
```

`Secrets.BedrockReleaseCik` / `BedrockPreviewCik` 会校验长度（不足 48 字节按未配置处理）。
**没注入时启动器照常工作**：基岩版下载页能列出、能下载版本，只有点安装 GDK 包时
才会明确提示「当前构建未包含基岩版 GDK 内容密钥（CIK），无法解包这个安装包」。
UWP（商店包）的下载与安装不需要 CIK。

### ⚠️ 改密钥后必须重启编译服务器

**这是最容易白干半小时的坑。**

源生成器读的是**编译器进程**的环境变量，而 `VBCSCompiler` / MSBuild 是长驻进程，
它们继承的是**第一次启动时**的环境变量。你改了 `$env:PCL_MS_CLIENT_ID` 再 publish，
产物会和上次**逐字节相同**——因为编译器根本没重新读。

更隐蔽的是：`dotnet publish` 会判定"所有项目均是最新的"，连编译都跳过。

正确顺序：

```powershell
dotnet build-server shutdown     # 关掉 VBCSCompiler 和 MSBuild 服务器
dotnet publish ... -t:Rebuild    # 强制重编
```

验证产物里的密钥换成了新的：

```powershell
# UTF-16LE 搜索（PowerShell 直接搜字符串搜不到，必须转码）
$bytes = [IO.File]::ReadAllBytes("publish-x64/Plain Craft Launcher 2.exe")
$needle = [Text.Encoding]::Unicode.GetBytes("<新的 client_id>")
# 或用 Python：
#   d.count(cid.encode('utf-16-le'))
```

顺带一条：`-t:Rebuild` 就够了，**不用**手动删 `obj/` `bin/`。删目录反而可能触发一些
批量删除的保护机制。

## 四、代码签名

不签名也能跑，但 Windows SmartScreen 会拦，且文件属性里没有发布者。

```powershell
$pfx  = "path\to\CodeSigning.pfx"
$pwd  = "证书密码"
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfx, $pwd)

Set-AuthenticodeSignature `
  -FilePath "publish-x64/Plain Craft Launcher 2.exe" `
  -Certificate $cert -HashAlgorithm SHA256 `
  -TimestampServer "http://timestamp.digicert.com"

Get-AuthenticodeSignature "publish-x64/Plain Craft Launcher 2.exe" | Select Status
```

要点：

- 用 `X509Certificate2::new(pfx, 明文密码)` 加载。`ConvertTo-SecureString` 在某些受限环境会被拦
- **一定要加时间戳**，否则证书过期后签名失效
- 自签名证书：状态会是 `Valid`（本机装了 CER），但用户机器上没装根证书，依然显示"未知发布者"。
  要消 SmartScreen 警告只能买 OV / EV 证书
- 签名会改变文件哈希，**先签名再算 SHA-256** 写进发布说明

## 五、发版流程

发布包放在 **OTA 仓** `x1B1C1/PCL_Plus_OTA`，不在源码仓。清单（`update.json` /
`updatev2.json`）也放在那儿。

OTA 仓按分支分两条发布线：`main` 放正式版，`beta` 放测试版。发哪个版本就改哪条分支上的清单——
客户端读哪条分支由它自己的版本后缀决定（`suffix` 以 `beta` 开头读 `beta`，其余读 `main`），
详见 `docs/UPDATE.md`「分支」一节。Release 资产两条线共享，不用另开仓库。

```powershell
# 1. 改版本号（三处，见 docs/README.md「版本号在哪」）
# 2. 构建 + 签名
# 3. 复制成发布命名
copy "publish-x64/Plain Craft Launcher 2.exe" "release/PCL_Plus_1.1_x64.exe"

# 4. 算哈希
Get-FileHash "release/PCL_Plus_1.1_x64.exe" -Algorithm SHA256

# 5. 在 OTA 仓打 tag（必须是三段式 SemVer，v1.1 会被启动器的更新检查拒绝）
git -C <OTA 仓路径> tag v1.1.0
git -C <OTA 仓路径> push origin v1.1.0

# 6. 发 Release（到 OTA 仓）
gh release create v1.1.0 "release/PCL_Plus_1.1_x64.exe" `
  --repo x1B1C1/PCL_Plus_OTA --title "PCL_Plus 1.1.0" `
  --notes-file release/notes.md --latest
```

### 两个必踩的坑

1. **`gh` 必须显式带 `--repo`**
   源码仓的 `origin` 指向 `x1B1C1/PCL_PLus`，而发布包要进 OTA 仓。不带 `--repo` 时
   gh 会按远端推断，结果是打到源码仓（那里不该出现二进制附件）。
2. **tag 必须是三段式**
   启动器的更新检查用 `SemVer.TryParse`，`v1.0` 只有两段会解析失败，
   用户侧表现为"Release 的 tag 不是合法的 SemVer"。用 `v1.0.0`，不要用 `v1.0`。

### 没有 `gh` CLI 时：用仓库里的脚本

`tools/github/` 下放了一套只依赖 git + Python 的替代方案（本机没有 `gh` 时用）：

```powershell
# 1) 推源码分支（自动从 Windows 凭据管理器取 token，不落盘）
powershell -File tools/github/push-repo.ps1 `
  -Src . -Repo x1B1C1/PCL_PLus -Branch main `
  -Message "PCL_Plus v1.0.0 - source import"

# 2) 打包源码
python tools/github/release.py zip . .dist/src.zip

# 3) 建 Release 并上传资产
python tools/github/release.py release x1B1C1/PCL_Plus_OTA v1.0.0 main `
  "PCL_Plus v1.0.0" .dist/notes.md --assets "release/PCL_Plus_x64.exe"
```

两个前提：`git` 能直连 GitHub（本机 Clash fake-ip 下 curl/.NET 都不通，只有 git 和 Python 的
TLS 正常）；凭据管理器里存着 `GitHub - https://api.github.com/<owner>` 的 token。
`--pre` 表示预发布；带 `--pre` 的 Release 不会抢走 `releases/latest`。

## 六、不要把构建产物提交进仓库

`publish-x64/` 已经在 `.gitignore` 里，**别去掉**。

原因很直接：那个 exe 里带着编译期注入的 client_id，一旦入库就等于把它公开了。
同理 `publish-upstream/`（上游源码的对照构建）也不要入库。

如果发现某次构建产物已经被跟踪了：

```powershell
git rm --cached -r publish-x64     # 只从索引移除，本地文件保留
```

## 七、常见问题

| 现象 | 原因 | 解决 |
| --- | --- | --- |
| 改了密钥但产物哈希不变 | VBCSCompiler 缓存了旧环境变量 | `dotnet build-server shutdown` + `-t:Rebuild` |
| 编译报一堆 `CS0414`（字段已赋值但从未使用） | 上游遗留，不是你的问题 | 忽略 |
| `NU1510` 警告（PackageReference 不会删除） | .NET 10 对内置包的新提示 | 忽略 |
| 启动提示"未配置 Client ID" | 编译时没设 `PCL_WRITE_SECRET` / `PCL_MS_CLIENT_ID` | 见第三节 |
| 用户侧"检查更新时出错" | GitHub API 限流（未认证 60 次/小时/IP） | 代码里已有 Atom 降级通道，确认它没被改坏 |
| 单文件 exe 启动慢 | 单文件首次运行要解压到临时目录 | 正常，第二次会快 |
