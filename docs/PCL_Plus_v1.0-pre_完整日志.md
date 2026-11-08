# PCL_Plus v1.0-pre 完整版本日志

> 版本：`PCL_Plus v1.0-pre`
> 基线：PCL 1.6.4 / PCL 上游 2.12.1
> 记录时间：2026-11-01
> 状态：可运行预发布版，已完成本机验证与展示视频交付

---

## 1. 版本概览

| 项目 | 内容 |
| --- | --- |
| 产品名 | PCL_Plus |
| 版本号 | 1.0-pre |
| FileVersion | 1.0.0 |
| ProductVersion | 1.0-pre |
| 上游基线 | PCL 2.12.1 / PCL 1.6.4 |
| 目标框架 | `net10.0-windows` |
| 架构 | x64（win-x64） |
| 发布方式 | framework-dependent，非 self-contained |
| 清单 | `requireAdministrator` |
| 主程序 | `release/PCL_Plus_x64.exe` |
| 主程序大小 | 28,776,930 bytes |
| 主程序 SHA-256 | `83ea4d83703871504e77a2cc14975a6c9e711b2929f0fc7754bc2be21f7e527b` |
| 展示视频 | `PCL_Plus_Showcase.mp4` |
| 视频时长 | 55.50 秒 |
| 视频 SHA-256 | `26bca72776573a76ad5e6560cc8e90d08d487cc4e5463f3ad0e42ca2b27be8d2` |

运行环境需要安装 **.NET 10 Desktop Runtime**；当前没有打包安装器，也没有做代码签名。

---

## 2. 开发阶段记录

### 阶段一：基线确认

- 以 PCL 1.6.4 为基线，保留原有 WPF 页面结构、主题系统、实例管理与下载流程。
- 确认项目为 C# / WPF，主项目为 `Plain Craft Launcher 2/Plain Craft Launcher 2.csproj`，内部命名空间仍为 `PCL`。
- 版本信息统一由 `Plain Craft Launcher 2/metadata.json` 提供。

### 阶段二：陶瓦联机（Terracotta）接入

- 新增 `Plain Craft Launcher 2/Modules/Terracotta/` 模块：
  - `TerracottaAddon.cs`：设置项、启停、房间与邀请码入口。
  - `TerracottaApiClient.cs`：本地 HTTP API 调用。
  - `TerracottaModels.cs`：房间、状态、邀请码等模型。
  - `TerracottaProcess.cs`：进程启动、端口文件、退出清理。
  - `TerracottaInviteCode.cs`：邀请码解析与生成。
  - `TerracottaHost.cs`：模块生命周期与初始化。
  - `TerracottaBundledRuntime.cs`：随包运行时释放。
- 内置运行时：
  - `Modules/Terracotta/Runtime/terracotta.exe`（9,402,880 bytes）
  - `Modules/Terracotta/Runtime/VCRUNTIME140.DLL`（97,656 bytes）
- 在 `Modules/Base/ModSetup.cs` 中加入受保护的初始化，未启用时不阻塞启动。
- 设置页新增陶瓦卡片：程序路径、昵称、自动启动、联机后自动填入地址、退出时停止、创建/加入/复制邀请码。
- 启动页保留“联机”快捷入口；本展示视频刻意不包含该入口与相关页面。
- 通过本地 HTTP 与本机 `terracotta.exe` 通信，未修改陶瓦源码；运行时使用 `--hmcl <port-file>` 方式获取端口。

### 阶段三：界面、玻璃与亚克力

- 配置项：
  - `UiAcrylic`，默认 `false`。
  - `UiLiquidGlass`，默认 `false`。
  - 液态玻璃参数：`UiLiquidGlassRefraction`、`UiLiquidGlassHighlight`、`UiLiquidGlassDispersion`。
- `Modules/UI/Theme/AcrylicHelper.cs`：
  - DWM `DWMWA_SYSTEMBACKDROP_TYPE=38`，`DWMSBT_TRANSIENTWINDOW=3`。
  - `WindowChrome.GlassFrameThickness=-1`。
  - `PanForm.Background=Transparent`，`PanBack.Margin=0`。
  - 处理标题栏、侧栏与卡片透明度，避免开启后出现白色边框。
- `Modules/UI/Theme/GlassHelper.cs` 与 `LiquidGlass.hlsl/.ps`：
  - 玻璃按钮、折射、高光与色散参数。
  - 修复过曝与预乘 alpha 合成问题。
- 当前策略：亚克力与液态玻璃均默认关闭，保留可选开关；开启路径已实现，但不同 GPU / 缩放比例下仍需继续验证。
- 界面修正：
  - 根 Grid 增加主题背景，移除窗口外圈黑边。
  - `PanBack` 改为对角主题渐变。
  - `RectForm` 圆角为 0，避免外层白边。
  - `MyCard` 圆角 9，增加细边框与轻阴影。
  - `MyButton` 圆角 7。
- 备份：
  - `artifact-backup-20261101-154825/Plain Craft Launcher 2.exe`
  - `release/EdPCL_v1_PRE_backup_20261101-154825.exe`

### 阶段四：品牌与启动页改造

- 品牌统一为 `PCL_Plus`：
  - 窗口标题、标题栏 Logo、关于页圆形标识。
  - `LabTitleLogo` 显示 `PCL_Plus`，`CELogo` 隐藏。
  - `UiLogoText` 在自定义文本为空时回退到 `PCL_Plus`。
- 新图标：
  - `assets/PCL_Plus_icon.png`（1024×1024）
  - `assets/PCL_Plus_icon_512.png`
  - `assets/PCL_Plus_banner.png`（1600×900）
  - `assets/PCL_Plus.ico`
  - 已替换 `Plain Craft Launcher 2/Images/icon.ico`。
  - 设计：蓝紫圆角方块 + 白色 P + 青色加号 + 底部像素点。
- `metadata.json`：
  - `name`：`PCL_Plus`
  - `base`：`1.0`
  - `upstream`：`2.12.1`
  - `suffix`：`pre`
  - `code`：`1`
- `app.manifest`：
  - `assemblyIdentity` 名称 `PCL_Plus.v1.PRE`
  - 活动执行级别 `requireAdministrator`
- `Plain Craft Launcher 2.csproj`：
  - `AssemblyTitle` / `Product` 均为 `PCL_Plus`
  - `ApplicationManifest` 指向 `app.manifest`

### 阶段五：主页与导航调整

- 主页 `Pages/PageLaunch/PageWelcome.xaml.cs`：
  - 移除旧的 Hero、网络、隐私卡片。
  - 新 Hero 为“性能状态”：
    - `GetSystemTimes` P/Invoke 读取 CPU。
    - `GlobalMemoryStatusEx` 读取内存。
    - 2 秒刷新，显示 CPU 使用率、内存占用、已用/总量/可用。
    - 文案：“本机实时 · 仅本地计算”。
  - 社区卡片保留，QQ 群号改为 `1134321588`。
- 导航精简：
  - 顶部 `BtnTitleSelectLunar` 删除。
  - 设置侧栏 `ItemLunar` 删除。
  - 隐藏页 `CheckHiddenPageLunar` 删除。
  - `CheckLunarVerify` 删除。
  - `PageSetupUI.xaml.cs` 不再引用 Lunar 导航。
  - `Pages/PageLunar/*` 与 `Modules/Lunar/ModLunar.cs` 仍保留为未可达代码，后续可清理。

### 阶段六：构建与资源

- 构建命令：

```powershell
dotnet publish "Plain Craft Launcher 2\Plain Craft Launcher 2.csproj" `
  -c Release -p:Platform=x64 --no-self-contained --no-restore
```

- 构建期需要写入：
  - `PCL_WRITE_SECRET=1`
  - `PCL_MS_CLIENT_ID`（Microsoft OAuth 客户端 ID）
- 由 `PCL.Core.SourceGenerators/EnvironmentInteropGenerator.cs` 在编译期注入，`PCL.Core/App/Secrets.cs` 读取。
- 生成物：
  - `release/PCL_Plus_x64.exe`
  - `release/PCL_Plus_x64.exe.sha256`
- 已验证：
  - ProductName：`PCL_Plus`
  - FileVersion：`1.0.0`
  - ProductVersion：`1.0-pre`
  - 管理员清单生效。

---

## 3. 展示视频记录

### 3.1 交付信息

- 文件：`PCL_Plus_Showcase.mp4`
- 封面：`PCL_Plus_Showcase-cover.png`
- 分辨率：1920×1080
- 帧率：30 FPS
- 时长：55.50 秒
- 音轨：纯音乐，无旁白
- 大小：10,624,445 bytes
- SHA-256：`26bca72776573a76ad5e6560cc8e90d08d487cc4e5463f3ad0e42ca2b27be8d2`

### 3.2 分镜与功能说明

| 时间（约） | 画面 | 字幕 |
| --- | --- | --- |
| 0.0–2.3s | 像素 PCL_Plus 图标 + 标题 | PCL_Plus 独立 MC 启动器 |
| 2.0–6.0s | 红粉主界面、性能状态、社区卡片 | 性能状态 · CPU/内存实时监控 · 实例与版本入口 |
| 5.7–7.5s | 真实主界面定格 + 光标点击「下载」 | 点击「下载」→ 打开版本下载页 |
| 7.2–15.4s | 真实下载页、进度、速度、剩余文件 | 选择版本 → 任务队列 → 进度/速度 → 断点续传与校验 |
| 15.1–23.1s | 真实实例/资源页，模组、数据包、资源包、光影 | 模组 / 光影 / 资源包 / 世界 · 实例隔离管理 |
| 22.8–27.3s | 暖金主题完整设置页（真实截图静帧） | 完整展示 1/3 · 暖金主题 · 完整设置页 |
| 27.0–31.5s | 森绿主题完整设置页（真实截图静帧） | 完整展示 2/3 · 森绿主题 · 完整设置页 |
| 31.2–35.7s | 天蓝主题完整设置页（真实截图静帧） | 完整展示 3/3 · 天蓝主题 · 完整设置页 |
| 35.4–39.9s | 回到红粉主界面 | 回到红粉主界面 · 主题配置本地保存 |
| 39.6–47.6s | 真实 Java 页面，JDK 列表 | Java 自动匹配 · 启动参数 · 日志输出 |
| 47.3–53.3s | 真实语言/社区页（静态） | PCL_Plus 社区共建 · 更新 / 反馈 / 日志 |
| 53.0–55.5s | 像素图标 + PCL_Plus 收尾 | 更多功能 等你探索 |

> 时间含 0.3 秒交叉淡入，表中为近似值。

### 3.3 视频处理说明

- 素材来自本机真实录屏与当前 PCL_Plus 实机截图，没有使用 AI 生成页面。
- 旧素材顶部标题栏已裁掉，统一叠加 `PCL_Plus` 标题栏。
- 主界面账号区、底部按钮区做了模糊/裁切处理，避免出现账号与联机入口。
- 下载段的“点击”用真实主界面帧叠加光标与点击环表示跳转，后续下载页、进度与速度均为真实录屏。
- 主题段使用真实设置页静帧完整展示，避免原录屏切换过快导致字幕与画面错位。
- 关于段使用真实语言/社区页静帧，刻意避开陶瓦联机页面。
- 全程没有出现「联机」「登录」相关画面、字幕或配音。
- 音轨为本地合成音乐：Fm–Db–Ab–Eb 铺底、100 BPM 轻节拍、高频微光；无旁白。

### 3.4 视频相关文件

- `tools/showcase/build_pclplus_video.py`
- `tools/showcase/make_pclplus_music.py`
- `tools/showcase/build32/video-only.mp4`
- `tools/showcase/build32/music-only.wav`
- `tools/showcase/PCL_Plus-55s.mp4`
- `PCL_Plus_Showcase.mp4`
- `PCL_Plus_Showcase-cover.png`

---

## 4. 验证记录

### 启动器

- `metadata.json` 读取正常，版本 `1.0-pre`。
- 主程序版本资源：
  - ProductName `PCL_Plus`
  - FileVersion `1.0.0`
  - ProductVersion `1.0-pre`
- 管理员清单已嵌入。
- 无管理员副本可启动并完成界面截图；正式 exe 仍为 `requireAdministrator`。
- 主页性能状态可刷新，CPU/内存数据来自本机。
- 陶瓦模块未启用时不阻塞启动；启用路径已接入设置页与启动页。

### 视频

- `ffprobe`：55.50 秒，1920×1080，30 FPS，H.264 + AAC。
- 抽查时间点：
  - 3.2s：主界面，红粉色调，字幕正确。
  - 6.5s：下载点击光标与点击环可见。
  - 8–12s：下载进度从 26.16% 到 71.91%，速度/剩余文件真实。
  - 18–22s：实例/资源列表，字幕与分类匹配。
  - 25–39s：暖金、森绿、天蓝完整设置页，最后回到红粉主界面。
  - 45s：Java 页面。
  - 52s：语言/社区页，无陶瓦联机内容。
- 旧标题未再出现。
- 视频中未出现「联机」「登录」。

---

## 5. 已知限制与后续事项

1. **主题素材限制**
   - 现有真实录屏中的主题设置页为暖金、森绿、天蓝，没有红粉主题设置页。
   - 视频以红粉主界面开场/收尾，主题段展示三个真实主题，未伪造红粉设置页。
   - 如需红粉主题设置页，需要重新录制一次真实主题切换。

2. **关于页素材限制**
   - 真实录屏中“关于”段会进入陶瓦联机页，因此视频改用语言/社区页静帧。
   - 该页内容为英文 “PCL_Plus Go Internationalised”，与中文字幕“社区共建”语义接近，但不是独立分支声明原文。
   - 如需严格“独立维护”声明，应补录关于页。

3. **下载点击为合成表现**
   - 点击光标与点击环叠加在真实主界面帧上，不是实际鼠标事件录屏。
   - 下载页、进度、速度、校验步骤均为真实录屏。

4. **Lunar 残留**
   - 导航和设置入口已移除，但 `Pages/PageLunar/*` 与 `Modules/Lunar/ModLunar.cs` 仍在仓库中，属于不可达代码。

5. **亚克力/液态玻璃**
   - 默认关闭，保留开关。
   - 已实现 DWM 背景与玻璃 shader 路径，但未覆盖所有 GPU、DPI 与多显示器组合。

6. **发布形态**
   - 当前为 framework-dependent，需要 .NET 10 Desktop Runtime。
   - 无安装器、无自动更新、无代码签名。
   - 陶瓦运行时随包分发，需继续确认其许可与升级策略。

---

## 6. 关键文件清单

| 路径 | 说明 |
| --- | --- |
| `release/PCL_Plus_x64.exe` | 当前主程序 |
| `release/PCL_Plus_x64.exe.sha256` | 主程序校验值 |
| `Plain Craft Launcher 2/metadata.json` | 名称与版本源 |
| `Plain Craft Launcher 2/app.manifest` | 管理员清单 |
| `Plain Craft Launcher 2/Modules/Terracotta/` | 陶瓦模块与内置运行时 |
| `Plain Craft Launcher 2/Pages/PageSetup/PageSetupLauncherMisc.xaml(.cs)` | 陶瓦设置卡片 |
| `Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchLeft.xaml(.cs)` | 启动页联机入口 |
| `Plain Craft Launcher 2/Pages/PageLaunch/PageWelcome.xaml.cs` | 性能状态主页 |
| `Plain Craft Launcher 2/Modules/UI/Theme/AcrylicHelper.cs` | 亚克力 |
| `Plain Craft Launcher 2/Modules/UI/Theme/GlassHelper.cs` | 液态玻璃 |
| `PCL.Core/App/Config.cs` | `UiAcrylic` / `UiLiquidGlass` 等配置 |
| `Plain Craft Launcher 2/Images/icon.ico` | 新图标 |
| `PCL_Plus_Showcase.mp4` | 55 秒展示视频 |
| `PCL_Plus_Showcase-cover.png` | 视频封面 |
| `tools/showcase/build_pclplus_video.py` | 视频渲染脚本 |
| `tools/showcase/make_pclplus_music.py` | 纯音乐生成脚本 |

---

## 7. 变更摘要

### Added

- 陶瓦联机模块、内置运行时、设置页与启动页入口。
- 主页性能状态卡片（CPU/内存实时监控）。
- PCL_Plus 品牌、图标、标题栏与关于页标识。
- 亚克力与液态玻璃可选配置。
- 55 秒展示视频与封面。

### Changed

- 窗口标题、产品名、程序集信息、版本源统一为 PCL_Plus。
- 启动页主页卡片结构、QQ 群号、窗口背景与卡片圆角。
- 旧素材在视频中统一替换为 PCL_Plus 标题栏。

### Fixed

- 亚克力开启后的白边、黑边与不透明背景问题。
- 液态玻璃过曝与预乘 alpha 合成问题。
- 启动器根窗口外圈黑边。
- 视频中旧品牌标题残留。
- 视频中账号/联机入口暴露。
- 视频主题段字幕与画面错位。
- 视频关于段误入陶瓦联机页。

### Removed

- 顶部 Lunar 导航入口。
- 设置侧栏 Lunar 项。
- 隐藏页 Lunar 开关。
- Lunar 正版验证相关设置入口。

---

## 8. 当前结论

`PCL_Plus v1.0-pre` 已达到可运行、可展示、可继续迭代的预发布状态：

- 启动器主程序可启动，版本与管理员清单正确。
- 陶瓦联机、性能状态、主题与玻璃配置已接入。
- 55.5 秒展示视频完成，纯音乐、无旁白、无联机/登录内容。
- 剩余工作主要是补录红粉主题与关于页素材、清理 Lunar 死代码、完善安装/更新与跨设备验证。
