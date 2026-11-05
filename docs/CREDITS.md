# 开源项目清单

PCL_Plus 站在很多人的肩膀上。这份清单列出直接借用了代码或资源的项目，以及运行期依赖的第三方库。

> **本清单不改变本项目的许可证。** PCL_Plus 仍以 [MIT + 传染性附加条款](../LICENSE) 发布，
> 详见 [README 的许可证一节](../README.md#-许可证)。这里列出的上游项目各自保留自己的许可证；
> 随分发物一起打包的组件另见仓库根目录的 [`NOTICE`](../NOTICE)。

## 一、代码来源

### PCL（Plain Craft Launcher）

- 作者：PCL 开发组（龙腾猫跃 等）
- 许可证：MIT + 传染性（copyleft）附加条款
- 仓库：<https://github.com/Meloong-Git/PCL>
- 关系：**整个项目的基础**。PCL_Plus → PCL Lite → PCL，三层都是同一条衍生链；
  页面框架、加载器体系、Minecraft 启动与下载链路、主题引擎等均源自 PCL。
  上游的版权声明与署名保留在 [`LICENSE`](../LICENSE)、[`NOTICE`](../NOTICE) 与本文档中。

### PCL Lite

- 作者：askbbtsk
- 许可证：MIT + 传染性（copyleft）附加条款
- 仓库：<https://github.com/askbbtsk/pcl-lite>
- 关系：**Lunar Client 子系统与若干外壳修复**移植自此项目。

  具体范围：
  - `PCL.Core/App/Lunar/**`（17 个文件）——Lunar 客户端服务、安装器、IPC 协议与服务端、
    社交协议、账户库、启动配置、版本目录、身份存储。
  - `Plain Craft Launcher 2/Modules/Lunar/{ModLunar.cs, LunarIpcHost.cs}` ——
    启动器侧的 Lunar 流程编排与 IPC 宿主。
  - `Pages/PageLunar/**`、`Pages/PageSetup/PageSetupLunar.*` —— Lunar 页面与设置页。
  - `PCL.Core/Utils/OS/UserPathMasker.cs` —— 日志里的用户路径脱敏。
  - `Application.xaml` 中的原生 `ScrollBar` / `ScrollViewer` 隐式样式。
  - `FormMain.xaml.cs` 的页面切换动画修复（左右两栏合并成同一组动画、`AniControlEnabled`
    用 try/finally 保护）与导航按钮按 `Tag` 查找。
  - 多行输入弹框（`MyMsgBoxInput(..., multiline: true)`）。
  - 4 份语言文件中的 `Lunar.*` 文案，以及 `docs/LUNAR.md`（据此改写）。

  与上游的差异：命名空间跟随目录、接入 PCL_Plus 的更新/配置/日志体系；
  Lunar 的公共 OAuth client id 取自其官方启动器的明文常量（见 `PCL.Core/App/Secrets.cs`
  的 `LunarOAuthClientId`），不是本项目的凭据。

### PCL-Symbio-Edition

- 作者：Hydrogen-Peroxide
- 许可证：Apache-2.0
- 仓库：<https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition>
- 关系：**基岩版（下载 + 安装 + 启动）** 部分移植自此项目。

  具体范围：
  - `PCL.Core/Link/Bedrock/BedrockService.cs` —— 检测已安装的基岩版并启动
    （商店版走 `shell:appsFolder\<PackageFamilyName>!<AppId>`、GDK 便携版直接运行目录内的
    `Minecraft.Windows.exe`、进程名 `Minecraft.Windows` / `Minecraft.Win10.DX11`、
    存档目录 `%LOCALAPPDATA%\Packages\Microsoft.MinecraftUWP_8wekyb3d8bbwe\...`）。
  - `Plain Craft Launcher 2/Modules/Minecraft/ModBedrockUwp.cs` —— 商店包的 FE3 下载地址解析、
    清单改写、开发者模式检测、依赖补全、`Add-AppxPackage` 注册。
  - `Plain Craft Launcher 2/Modules/Minecraft/ModBedrockGdk.cs` —— `.msixvc`（XVC 容器）的
    解密与解包。
  - `Plain Craft Launcher 2/Modules/Minecraft/ModBedrockDownload.cs` /
    `ModBedrockInstaller.cs` —— 下载与安装编排，按 PCL_Plus 的结构重写。

  与上游的差异：GDK 版**不做 DLL 注入**（不移植 `ModBedrockLoader` / `ModBedrockMods`），
  解包后直接运行主程序；内容密钥（CIK）不写进源码，构建时通过环境变量
  `PCL_BEDROCK_RELEASE_CIK` / `PCL_BEDROCK_PREVIEW_CIK` 注入（见 `docs/BUILD.md`），
  未注入时下载页会明确提示「无法解包」，而不是静默失败。

  依据 Apache-2.0 第 4 条，此移植部分仍受 Apache-2.0 约束；本项目整体许可证不变。
  Apache-2.0 全文见仓库根目录的 [`LICENSE-APACHE`](../LICENSE-APACHE)。

## 二、随分发物打包的二进制组件

### Terracotta（陶瓦联机）

- 作者：burningtnt
- 许可证：**AGPL-3.0-or-later**，附打包与 IPC 例外
- 仓库：<https://github.com/burningtnt/Terracotta>
- 关系：**联机组件**以未修改的二进制随启动器分发
  （`Plain Craft Launcher 2/Modules/Terracotta/Runtime/terracotta.exe`，版本 0.4.2），
  运行期释放到本地目录，通过其本地 HTTP 接口（`127.0.0.1`）调用。

  许可证义务的落实方式：
  - 依其例外条款第 1 项（打包未修改二进制、不做静态/动态链接），本项目不因此被 AGPL 覆盖；
  - 例外条款要求「在程序界面明显处标识本作品的版权信息」——
    已写在「设置 → 启动器 → 陶瓦联机」卡片中；
  - 对应源码随本仓库提供：`tools/t/`；AGPL 全文见 [`LICENSE-AGPL-3.0`](../LICENSE-AGPL-3.0)；
  - 另附 `VCRUNTIME140.DLL`（Microsoft Visual C++ 可再发行组件），遵循其可再发行条款。

## 三、第三方库（NuGet）

运行期依赖，按包名排序。全部为宽松许可证（MIT / Apache-2.0 / BSD），与项目的 MIT 兼容。

| 包 | 用途 | 许可证 |
| --- | --- | --- |
| `Ae.Dns.Client` | DNS 解析（含 DoH） | MIT |
| `CommunityToolkit.Mvvm` | MVVM 基础设施 | MIT |
| `Dapper` | 轻量 ORM | Apache-2.0 |
| `DotNet.Glob` | 通配符匹配 | MIT |
| `Downloader` | 分块多线程下载 | MIT |
| `fNbt` | NBT 读写（存档、Level.dat） | MIT |
| `FluentValidation` | 参数校验 | Apache-2.0 |
| `Humanizer.Core.zh-CN` | 中文数字/时间人性化 | MIT |
| `LiteDB` | 嵌入式文档数据库 | MIT |
| `Markdig.Wpf` | Markdown 渲染（更新说明） | BSD-2-Clause |
| `Microsoft.Data.Sqlite` | SQLite 访问 | MIT |
| `Microsoft.Extensions.Http` | `HttpClientFactory` | MIT |
| `Microsoft.Extensions.Logging` | 日志抽象 | MIT |
| `Microsoft.Xaml.Behaviors.Wpf` | WPF 行为 | MIT |
| `NAudio` | 音频播放 | MIT |
| `PlainToolkit.CngProtectedData` | CNG 加密存储（口令） | MIT |
| `Polly` | 重试与熔断 | BSD-3-Clause |
| `protobuf-net` | Protobuf 序列化 | Apache-2.0 |
| `SharpZipLib` | zip 读写 | MIT |
| `SQLitePCLRaw.bundle_e_sqlite3` | SQLite 原生绑定 | Apache-2.0 |
| `System.IdentityModel.Tokens.Jwt` | JWT 解析（正版登录） | MIT |
| `System.Management` | WMI 查询 | MIT |
| `System.Security.Cryptography.ProtectedData` | DPAPI | MIT |
| `System.Text.Json` | JSON | MIT |
| `Wacton.Unicolour` | OKLCH / 色彩空间转换（主题引擎） | MIT |
| `YamlDotNet` | YAML 配置读写 | MIT |

运行平台：.NET 10 与 WPF（MIT，<https://github.com/dotnet>）。

## 四、资源与图标

| 资源 | 来源 | 许可证 |
| --- | --- | --- |
| SVG 图标集 | [Lucide](https://lucide.dev) | ISC（含 MIT 部分），原文见 `PCL.Core/UI/Assets/IconPacks/lucide/LICENSE.txt` |
| 部分界面图标 | [Iconmonstr](https://iconmonstr.com)（Alexander Kahlkopf） | 见其站点许可 |
| 部分界面图标 | [Iconfont](https://www.iconfont.cn)（ALIMAMA MUX） | 见其站点许可 |
| PCL_Plus 图标 | 本项目自制 | 随本项目 |

## 五、运行期按需下载、不随本项目分发的组件

以下内容在用户机器上按需下载或由游戏自身携带，本项目不打包、不修改：

- **LegacyFix**（GPL-3.0，betacraftuk）——老版本 Java 兼容修复；
- **Mesa Loader Windows**、**LWJGL Unsafe Agent**（Apache-2.0，HMCL-dev）——渲染器兼容；
- **Java Launch Wrapper**（MIT，00ll00）——启动包装；
- 各版本 Minecraft、Forge / Fabric / NeoForge 等加载器物料。

## 六、怎么补充这份清单

新增第三方代码或资源时：

1. 在上面对应小节加一行，写清**作者、许可证、仓库地址**；
2. 如果是移植代码，在源文件顶部注释里注明出处（参考 `BedrockService.cs` 的写法）；
3. 如果是**随启动器打包的二进制**（尤其是 GPL / AGPL 组件），必须同时更新
   根目录的 [`NOTICE`](../NOTICE)，并确认许可要求（源码提供、界面署名）已满足；
4. **不要因为引入外部代码而修改本项目的 LICENSE**。上游许可证的约束写在本文档里，
   而不是替换掉 `LICENSE`。
