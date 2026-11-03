# Beta-V1.2-Aero · 源码快照

本目录是 **PCL_Plus v1.2 Beta（Aero 回归版）** 的完整源码快照，可直接作为一个独立 GitHub 仓库上传。

| 项 | 值 |
| --- | --- |
| 版本 | `1.2.0-beta`（`Plain Craft Launcher 2/metadata.json`，code = 2） |
| 上游基线 | PCL 2.12.1 / PCL-CE 1.6.4 |
| 目标框架 | .NET 10（`net10.0-windows`）+ WPF |
| 基线 | 由 `V1.0-SRC` 复制后改造 |

## 相对 v1.0 的改动

**窗口亚克力（Aero）回归**，这是本版唯一的功能性改动：

1. `PCL.Core/App/Config.cs`：`UiAcrylic` 默认值 `false` → `true`。
2. `Plain Craft Launcher 2/Modules/Base/ModSetup.cs`：不再在 `ApplyAll()` 里强制 `AcrylicEnabled = false`。
3. `Plain Craft Launcher 2/Modules/UI/Theme/AcrylicHelper.cs`：`IsEnabled` 由硬编码 `false` 改为读取 `Config.Preference.Theme.AcrylicEnabled`。
4. `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml`：设置页的「亚克力窗口」开关去掉 `Visibility="Collapsed"`，恢复可切换。
5. `metadata.json`：版本 `1.0.0-pre` → `1.2.0-beta`（code 1 → 2）。

开启后由 Windows DWM 提供真实桌面亚克力（Win11 22H2+ 走 `DWMWA_SYSTEMBACKDROP_TYPE`，旧系统回退到 `SetWindowCompositionAttribute`），自定义背景图/视频会自动隐藏以免盖住模糊；按钮的液态玻璃不受影响，仍跟随主题。若要退回 v1.0 的观感，在「设置 → 个性化」里关掉亚克力开关即可。

## 构建

```powershell
dotnet build "Plain Craft Launcher 2\Plain Craft Launcher 2.csproj" -c Debug
```

产物在 `Plain Craft Launcher 2\bin\Debug\net10.0-windows\`。发布单文件：

```powershell
dotnet publish "Plain Craft Launcher 2\Plain Craft Launcher 2.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained true -o ./publish-x64
```

> 不要用 `Plain Craft Launcher 2.slnx` 构建（Restore 阶段会失败）。
> `Modules\Terracotta\Runtime\terracotta.exe` 是编译期嵌入资源，删掉会导致编译失败。

## 与主线的关系

本快照与源码仓 `x1B1C1/PCL_PLus` 的 v1.0 分支并行存在；Aero 改动如果后续要并回主线，只需对比上面 5 处即可。

## 分支与更新

- 本版属于 **测试线**：`metadata.json` 的 `suffix` 是 `beta`，客户端因此读 OTA 仓
  `x1B1C1/PCL_Plus_OTA` 的 **`beta` 分支**里的 `update.json` / `updatev2.json`；
  正式版读 `main` 分支。分支由版本后缀决定，不受「更新通道」设置影响。
- 两条线的 Release 资产在同一个仓库里共享，靠清单里的版本号区分，所以发测试版不用新开仓库。
- 详细说明见同目录的 `BETA.md`。
- 安装包清单为 `requireAdministrator`：双击即请求管理员权限（UAC 选「是」），任意目录都可运行；
  开发者署名 `xL1nUwU`（`csproj` 的 `Company`/`Authors`/`Copyright` + 关于页「开发者」一行）。
