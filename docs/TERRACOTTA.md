# 陶瓦联机（Terracotta）集成与液态玻璃修复说明

> 本文档记录本次改动的**新增代码位置**、**修改代码位置**与**原有代码边界**，便于审查和回退。
> 所有改动均在本地工作目录内完成，未联网拉取任何仓库或资源。

---

## 1. 约束与实现方式

| 约束 | 实现 |
| --- | --- |
| 原有 UI / 页面布局 / 交互样式保持原样 | 只在「设置 → 启动器 → 杂项」页末尾追加一张 `MyCard`；未修改任何已有卡片、控件、样式、页面切换逻辑 |
| 新增一组陶瓦联机配置，默认关闭 | 新增 `Config.Terracotta` 配置组，全部默认值关闭；卡片总开关默认不勾选 |
| 集成陶瓦联机能力 | 使用陶瓦为 HMCL 预留的 `--hmcl <端口文件>` 模式启动，读取本地 HTTP API，支持创建房间 / 加入房间 / 断开 / 复制邀请码与连接地址 |
| 可插拔、解耦 | 插件全部代码在 `Plain Craft Launcher 2/Modules/Terracotta/`；启动器侧只有 **1 行初始化调用**（`ModSetup`）与设置页卡片。删除该目录并去掉这两处入口即可完全移除 |
| 不破坏原有流程 | 插件不接管 `ModLaunch`、不修改启动参数、不监听启动事件；关闭时无进程、线程、HTTP 轮询 |
| 允许使用本地 `tools/t` 源码 | 该目录是陶瓦的上游 Rust 源码（AGPL-3.0），仅用于核对协议与版本，不参与编译；启动器不链接其中的代码 |
| 随包内置陶瓦运行时 | `Plain Craft Launcher 2/Modules/Terracotta/Runtime/` 内置**未修改**的 `terracotta.exe` 0.4.2 与 `VCRUNTIME140.DLL`，以 `EmbeddedResource` 打包，运行期由 `TerracottaBundledRuntime.cs` 释放到 `%LOCALAPPDATA%\PCL\Terracotta\0.4.2\`；符合 AGPL-3.0 的打包例外（不链接、仅 IPC 调用），界面已标注版权 |

> **版权说明**：Terracotta（陶瓦联机）由 burningtnt 开发，以 AGPL-3.0 发布。本启动器仅通过其
> 本地 HTTP 进程间接口调用，未修改其代码，并随包内置未修改的官方二进制（源码见 `tools/t/`，
> 许可全文见 `LICENSE-AGPL-3.0`）。设置卡片中也保留了相同的版权说明。

---

## 2. 新增文件（全部为 `[新增文件]`）

| 文件 | 行数 | 职责 |
| --- | ---: | --- |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaAddon.cs` | 540 | 插件入口：配置观察、生命周期、启动/停止、创建/加入房间、状态轮询、快照发布 |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaApiClient.cs` | 99 | 陶瓦本地 HTTP API 客户端（仅 127.0.0.1，显式禁用代理） |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaModels.cs` | 151 | 状态快照、JSON 状态解析、异常编号到中文文案的映射 |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaProcess.cs` | 222 | 查找 `terracotta.exe`、`--hmcl` 启动、等待并读取端口文件 |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaInviteCode.cs` | 44 | 邀请码规范化与格式校验 |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaHost.cs` | 94 | 与启动器的唯一胶水层：注入日志回调、把联机地址回填到当前实例 |
| `Plain Craft Launcher 2/Modules/Terracotta/TerracottaBundledRuntime.cs` | 104 | 释放随启动器内置的陶瓦 0.4.2 运行文件 |

每个文件头部都有：

```csharp
// ============================================================================
// [新增文件] 文件名.cs
// 所属功能：陶瓦联机（Terracotta）可插拔附加模块
// 说明：本文件全部为新增代码，不修改启动器原有类型。
// ============================================================================
```

### 插件内部依赖方向

```
TerracottaHost (启动器胶水)
   └─ TerracottaAddon (入口/状态机)
        ├─ TerracottaProcess (进程 + --hmcl 端口文件)
        ├─ TerracottaApiClient (本地 HTTP)
        │    └─ TerracottaModels (快照/解析)
        └─ TerracottaInviteCode (邀请码)
```

`TerracottaAddon` **不引用** `ModBase`、`ModInstanceList`、`ModLaunch` 等启动器内部类型；
日志与「回填服务器地址」通过 `Action` 回调由 `TerracottaHost` 注入。

---

## 3. 修改的原有文件（代码内均有标记）

| 文件 | 位置 | 改动类型 | 说明 |
| --- | --- | --- | --- |
| `PCL.Core/App/Config.cs` | 718–771 | `[新增代码开始/结束]` | 新增 `TerracottaConfigGroup`，6 个配置项全部默认关闭 |
| `Plain Craft Launcher 2/Modules/Base/ModSetup.cs` | 127–139 | `[新增代码开始/结束]` | 仅新增 `TerracottaHost.Initialize();`（带异常保护），用于注册配置观察者 |
| `Plain Craft Launcher 2/Pages/PageSetup/PageSetupLauncherMisc.xaml` | 356–467 | `[新增代码开始/结束]` | 新增「陶瓦联机（Terracotta）」卡片；已有卡片、控件、样式未改 |
| `Plain Craft Launcher 2/Pages/PageSetup/PageSetupLauncherMisc.xaml.cs` | 43–48 | `[新增]` 注释 | 页面卸载时停止陶瓦状态定时器 |
| 同上 | 124–134 | `[新增代码开始/结束]` | `Reload()` 末尾同步陶瓦配置到控件 |
| 同上 | 718–1045 | `#region 陶瓦联机（Terracotta）` | 设置页交互全部为新增方法 |
| `Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchLeft.xaml` | 29–35 | `[新增]` 注释 | 启动页新增「联机」一键创建房间入口 |
| `Plain Craft Launcher 2/Pages/PageLaunch/PageLaunchLeft.xaml.cs` | 46、64–65 | `[新增]` 注释 | 联机按钮字段与事件挂接 |
| 同上 | 394–468 | `[新增]` 注释 | 自动启用/启动陶瓦并创建房间，成功后复制邀请码 |
| `Plain Craft Launcher 2/app.manifest` | 18–19 | `[新增]` 注释 | 主程序改为 `requireAdministrator`，启动时请求管理员权限 |
| `Plain Craft Launcher 2/Plain Craft Launcher 2.csproj` | 132–137 | `[新增]` 注释 | 将陶瓦 0.4.2 运行文件嵌入 exe |
| `Plain Craft Launcher 2/Modules/UI/Theme/GlassHelper.cs` | 全文 | `[修改文件]` + 代码内 `[修改]` | 液态玻璃覆盖层的尺寸、透明度、裁剪、叠放修复 |
| `PCL.Core/UI/Effects/LiquidGlassEffect.cs` | 1、31–45、165–205 | `[修改文件]` + `[修改]` | `IsAvailable`、Freezable 克隆顺序、着色器加载失败降级 |
| `PCL.Core/UI/Assets/Shaders/LiquidGlass.hlsl` | 1–98 | `[修改文件]` | 重写透明混合与分层合成 |
| `PCL.Core/UI/Assets/Shaders/LiquidGlass.ps` | 二进制 | 重新编译 | 由修改后的 HLSL 编译生成（2,532 字节） |

### 原有代码边界

- `TerracottaHost.Initialize()` 是启动器原有初始化流程中唯一新增的陶瓦调用点。
- 设置页新增卡片与 `#region`；启动页按新需求新增「联机」按钮，这是唯一新增的主界面入口。
- 液态玻璃的公开 API 保持 `Apply(...)` / `RefreshAll()` / `RefreshParams()` 不变，三个控件的调用点
  （`MyButton.xaml.cs`、`MyIconButton.xaml.cs`、`MyRadioButton.xaml.cs`）没有改动。
- 未修改 `ModLaunch`、`PageSetupUI`、主题引擎、下载/更新/登录等任何原有模块。
- `app.manifest` 的 `requireAdministrator` 会让主程序每次启动弹出 UAC，这是按需求显式设置的行为。

---

## 4. 配置项

配置组：`Config.Terracotta`，存储键如下（全部 `ConfigSource.Local`，默认关闭）：

| 属性 | 存储键 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `Enabled` | `TerracottaEnabled` | `false` | 总开关 |
| `ExecutablePath` | `TerracottaExePath` | `""` | 程序路径；留空自动查找 |
| `PlayerName` | `TerracottaPlayerName` | `""` | 联机昵称 |
| `AutoStart` | `TerracottaAutoStart` | `false` | 启动启动器时自动启动 |
| `AutoFillServer` | `TerracottaAutoFillServer` | `false` | 加入成功后写入当前实例的「启动时自动进入服务器」 |
| `StopOnExit` | `TerracottaStopOnExit` | `false` | 退出启动器时停止陶瓦 |

---

## 5. 使用方式

1. 双击 `Plain Craft Launcher 2.exe`，在 UAC 提示中选择「是」（主程序已设置为管理员权限运行）。
2. **快捷创建**：启动页左上角的「联机」按钮会自动启用陶瓦、启动组件并创建房间；
   成功后邀请码会复制到剪贴板，并弹提示。
3. **设置页创建**：打开「设置 → 启动器 → 杂项 → 陶瓦联机（Terracotta）」，
   勾选「启用陶瓦联机」→「启动组件」→「创建房间」。
4. 创建房间前，请先在 Minecraft 里打开一个单人存档并保持运行；
   成功后邀请码会自动填入输入框，可点「复制邀请码」发给朋友。
5. **加入房间**：把邀请码粘贴到设置页输入框，点「加入房间」；成功后状态区显示
   `127.0.0.1:<端口>`，可点「复制连接地址」，或提前勾选「联机成功后自动填入当前版本服务器地址」。
6. 不需要时点「断开房间」（只重置房间状态）或「停止组件」（关闭陶瓦进程）。
   关闭总开关只会停止启动器这边的会话与轮询，不会杀掉可能被其他启动器共享的陶瓦进程。

> 陶瓦 0.4.2 Windows x64 运行文件已作为嵌入资源打包进 exe，首次使用时自动释放到
> `%LOCALAPPDATA%\PCL\Terracotta\0.4.2\`（不可写时回退到临时目录），无需手动下载。

### 自动检测顺序

`TerracottaProcess.DetectExecutable` 依次检查：

1. 用户配置的路径；
2. 启动器目录、当前目录下的
   `PCL\Terracotta\terracotta.exe`、`PCL\Terracotta\Terracotta.exe`、`PCL\terracotta.exe`、
   `Terracotta\terracotta.exe`、`terracotta.exe`；
3. `tools\t\target\release\terracotta.exe`、`tools\t\target\debug\terracotta.exe`（便于本地调试）；
4. `PATH` 中的 `terracotta.exe`；
5. 内置资源释放出的 `terracotta.exe`（上面都没有时）。

### 协议来源（本地 `tools/t`）

- `src/main.rs`：`--hmcl <file>` 模式会把 `{"port":<端口>}` 写入文件；
- `src/server/mod.rs`：`/`、`/meta`、`/panic?peaceful=true`；
- `src/server/states.rs`：`/state`、`/state/ide`、`/state/scanning`、`/state/guesting`；
- `src/controller/api.rs`：`host-ok` / `guest-ok` / `exception` 等状态字段；
- `src/controller/rooms/scaffolding/room.rs`：邀请码格式 `U/XXXX-XXXX-XXXX-XXXX`；
- `web/_.html`：异常编号 0–5 对应的中文提示。

---

## 6. 液态玻璃修复说明

### 6.1 已修复的显示问题与原因

| 现象 | 根因 | 修复 |
| --- | --- | --- |
| 窗口缩放后玻璃尺寸/圆角不对，边缘错位 | 只在 `Loaded` 时 `Sync()` 一次，没有监听 `SizeChanged` | `target.SizeChanged` → `Sync()`；渲染帧里再兜底检查尺寸变化 |
| 页面切换时页面已淡出，玻璃还亮着（“玻璃幽灵”） | `Adorner` 不是控件的视觉子级，**不继承祖先 `Opacity`** | `CompositionTarget.Rendering` 中累乘「控件 → 共同祖先（AdornerDecorator）」的 `Opacity`；共同祖先及以上由 WPF 自动作用于覆盖层，不重复乘 |
| 设置页滚动后玻璃浮在页头/滚动条上 | `MyPageRight` 的 `AdornerLayer` 在 `ScrollViewer` 外层，覆盖层不会被滚动视口裁剪 | 手动把覆盖层裁剪到「AdornerLayer ∩ ScrollViewer 视口 ∩ 控件」的交集；完全不可见时折叠 |
| 覆盖层叠放次序与页面状态不一致 | 覆盖层在独立 AdornerLayer 上，页面动画/隐藏不自动作用于它 | 每帧同步可见性、透明度与裁剪；页面/控件不可见时立即折叠 |
| 按钮按压缩放、页面位移动画时玻璃与控件错位 | WPF `AdornerLayer` 只跟随布局位置，**不跟随 `RenderTransform`** | 每帧计算「控件实际变换 vs 覆盖层当前变换」的残差矩阵，写到覆盖层 `RenderTransform` |
| 边缘发灰、发脏、亮边突兀 | 着色器把多层光效按 alpha 加权平均，总 alpha 超过 1 后被硬截断 | 着色器改为「底部阴影 → 边缘聚光 → 顶部高光」从后到前 `over` 合成，最后统一预乘输出 |
| 着色器缺失时挂一个空 Effect | 旧降级逻辑会 `new PixelShader()` | 新增 `LiquidGlassEffect.IsAvailable`；不可用时直接不挂覆盖层 |

### 6.2 `GlassHelper.cs` 关键新增/修改位置

- 文件头 `[修改文件]` 说明：第 1–13 行；
- 渲染帧钩子：`EnsureRenderHook` / `ReleaseRenderHook` / `OnRendering`，约 205–235 行；
- `GlassAdorner`：
  - 尺寸同步：`Sync()`，约 337–368 行；
  - 透明度/裁剪：`UpdatePresentation()`，约 414–480 行；
  - 透明度累乘：`ComputeOpacity()`，约 483 行；
  - AdornerLayer + ScrollViewer 裁剪：`TryComputeClip()`，约 502 行；
  - RenderTransform 残差补偿：`UpdateRenderTransform()`，约 573 行；
- `IsEligible` 增加着色器可用性检查：约 172 行。

### 6.3 着色器

- 源码：`PCL.Core/UI/Assets/Shaders/LiquidGlass.hlsl`；
- 编译产物：`PCL.Core/UI/Assets/Shaders/LiquidGlass.ps`（2,532 字节，PS 3.0）；
- 常量寄存器与 C# 的 `PixelShaderConstantCallback(0..6)` **一一对应，未增删参数**，因此 `LiquidGlassEffect` 的属性/配置项保持兼容；
- 颜色空间：直通色 + 标量 alpha 做 `over` 合成，最后输出 `rgb * a` 的预乘 alpha，符合 WPF `ShaderEffect` 约定。

---

## 7. 本次实际执行的验证

> 说明：由于本机 NuGet 缓存不完整，且任务要求禁止联网，**没有执行完整的 `dotnet build`**。
> 以下是本地可执行、且不依赖外部包的验证。

1. **Roslyn 语法检查**：全部新增/修改的 `.cs` 文件通过（0 syntax error）。
2. **独立类型检查**：
   - `TerracottaModels.cs` + `TerracottaApiClient.cs` + `TerracottaProcess.cs` 使用真实 .NET 10 引用编译通过；
   - `TerracottaAddon.cs`、`TerracottaHost.cs` 使用最小桩类型编译通过，确认 `Config.Terracotta`、`Application.Exit`、`ArgConfig` 索引器等 API 用法正确；
   - `GlassHelper.cs` 使用真实 WPF 引用 + 最小桩类型编译通过，确认 `AdornerLayer.Update`、`Rect.Intersect`、`CompositionTarget.Rendering`、`TransformToAncestor` 等 API 正确；
   - `LiquidGlassEffect.cs` 使用真实 WPF 引用编译通过。
3. **HTTP 客户端集成测试**：用本地 TCP 假服务器验证：
   - `GET /state` 解析 `host-ok`、房间号、玩家列表、`index`；
   - `GET /state/scanning?player=...` 中文昵称被正确 URL 编码；
   - `GET /state/guesting?room=...` 邀请码被正确 URL 编码；
   - `exception` 的 `type=4` 映射为中文提示。
4. **着色器编译/加载**：
   - `LiquidGlass.hlsl` 用 `d3dcompiler_47` 的 `D3DCompile`（`ps_3_0`）编译成功；
   - 产物可被 WPF `PixelShader` 正常加载（`UriSource` 加载无异常）。
5. **XAML 校验**：
   - `PageSetupLauncherMisc.xaml` 通过 XML 解析；
   - 所有新增 `Click` / `Change` / `TextChanged` 事件处理方法均存在；
   - 所有新增 `x:Name` 与代码引用一一对应。
6. **插件端到端测试**（本地假 `terracotta.exe` + 假 HTTP 服务）：
   - `StartAsync` → 等待端口文件 → `waiting`；
   - `CreateRoomAsync` → 轮询到 `host-ok`，房间号正确；
   - `JoinRoomAsync`（不带 `U/` 前缀的邀请码）→ `guest-ok`，`127.0.0.1:25566` 正确；
   - `AutoFillServer` 回调成功收到地址；
   - `ShutdownAsync` → `running=False`。
7. **邀请码规范化测试**：全大写、小写、带空格、缺 `U/` 前缀均能规范化；长度不足或非法字符被拒绝。

### 未执行的验证

- **完整 `dotnet build`**：本机 NuGet 缓存缺少项目依赖，离线执行
  `dotnet build PCL.Core -c Debug --no-restore` 报 `NETSDK1004`（找不到 `project.assets.json`）；
  `dotnet restore` 需要联网，本任务明确禁止，因此未执行。
- **真实 `terracotta.exe` 端到端联机**：本地没有该二进制文件，且不允许联网下载；
  协议层已按 `tools/t` 源码实现，并用假服务验证。
- **液态玻璃最终视觉截图**：`RenderTargetBitmap` 软件渲染不支持自定义 `ShaderEffect`，
  无法在无头环境生成真实玻璃截图；着色器已完成编译与 WPF `PixelShader` 加载验证。

---

## 8. 回退方式（可插拔）

如需完全移除陶瓦联机功能：

1. 删除目录 `Plain Craft Launcher 2/Modules/Terracotta/`；
2. 删除 `ModSetup.cs` 中 `TerracottaHost.Initialize();` 及上下 `[新增代码]` 注释；
3. 删除 `PageSetupLauncherMisc.xaml` 中的 `CardTerracotta` 整段；
4. 删除 `PageSetupLauncherMisc.xaml.cs` 中的 `#region 陶瓦联机（Terracotta）` 以及 `Reload()` / `Unloaded` 中的新增行；
5. 删除 `Config.cs` 中的 `TerracottaConfigGroup`。

液态玻璃修复与陶瓦功能相互独立，可按需保留。
