# Beta-V1.2-Aero · 源码快照（内容已更新到 1.5-beta）

本目录是 **PCL_Plus 测试线（Aero）** 的完整源码快照，可直接作为一个独立 GitHub 仓库上传。
目录名沿用最初的 `Beta-V1.2-Aero`，内容随测试线一起前进（当前 = `1.5.0-beta`）。

| 项 | 值 |
| --- | --- |
| 版本 | `1.5.0-beta`（`Plain Craft Launcher 2/metadata.json`，code = 5） |
| 更新线 | 测试线（`beta`） |
| 上游基线 | PCL 2.12.1 / PCL-CE 1.6.4 |
| 目标框架 | .NET 10（`net10.0-windows`）+ WPF |
| 基线 | 由 `V1.0-SRC` 复制后改造 |

## 相对正式线的改动

**1.5 测试线 = 窗口亚克力（Aero）默认开启 + UI 专业化（设计系统 v1.5）。**

Aero：

1. `PCL.Core/App/Config.cs`：`UiAcrylic` 默认值 `false` → `true`。
2. `Plain Craft Launcher 2/Modules/Base/ModSetup.cs`：不再在 `ApplyAll()` 里强制 `AcrylicEnabled = false`。
3. `Plain Craft Launcher 2/Modules/UI/Theme/AcrylicHelper.cs`：`IsEnabled` 由硬编码 `false` 改为读取 `Config.Preference.Theme.AcrylicEnabled`。
4. `Plain Craft Launcher 2/Pages/PageSetup/PageSetupUI.xaml`：设置页的「亚克力窗口」开关去掉 `Visibility="Collapsed"`，恢复可切换。

UI 专业化（详见 `docs/UI-DESIGN.md`）：

5. `Plain Craft Launcher 2/Application.xaml`：新增统一的 `ToolTip` 样式；`ContextMenu` 圆角 3 → 8、投影改为柔和单层；`ComboBox` / `PasswordBox` 圆角统一为 8。
6. `Plain Craft Launcher 2/Controls/MyCard.cs`：卡片圆角 9 → 11，投影半径 3 → 10、悬停不透明度 0.4 → 0.16，卡片标题改为 13.5px SemiBold。
7. `Plain Craft Launcher 2/Controls/MyButton.xaml`：按钮圆角 7 → 8。
8. `Plain Craft Launcher 2/Controls/MyRadioButton.xaml`：胶囊改圆角矩形（13.5 → 8），高度 27 → 30，文字 13px、右侧内边距 14。
9. `Plain Craft Launcher 2/Controls/MyListItem.xaml`：选中态左侧指示条 2px → 3px。
10. `Plain Craft Launcher 2/Controls/MyTextBox.cs`：输入框圆角默认值 3 → 8。
11. `Plain Craft Launcher 2/FormMain.xaml`：顶部导航按钮组高度 27 → 30，与新的 30px 按钮统一。
12. `metadata.json`：版本 `1.1.0` → `1.5.0-beta`（code 2 → 5）。

开启亚克力后由 Windows DWM 提供真实桌面亚克力（Win11 22H2+ 走 `DWMWA_SYSTEMBACKDROP_TYPE`，旧系统回退到 `SetWindowCompositionAttribute`），自定义背景图/视频会自动隐藏以免盖住模糊；按钮的液态玻璃不受影响，仍跟随主题。配色按深/浅色分两套（`AcrylicHelper` 的「亚克力配色」区域），卡片不透明度约 74%–78% 以保证正文可读。若要退回正式线观感，在「设置 → 个性化」里关掉亚克力开关即可。

## 与正式线共有的内容

Lunar Client 支持、实例选择页侧边栏的「基岩版 / Lunar Client」入口、命名空间跟随目录、主/备更新服务器、可移植性修复、donor 外壳修复等，与 `V1.0-SRC` 完全一致；两树差异就是上面列出的 12 个文件。

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

## 分支与更新

- 本版属于 **测试线**：`metadata.json` 的 `suffix` 是 `beta`，客户端因此读两个仓库的
  **`beta` 分支**里的 `update.json` / `updatev2.json`（主服务器 `x1B1C1/PCL_PLus`，备用 `x1B1C1/PCL_Plus_OTA`）；
  正式版读 `main` 分支。分支由版本后缀决定，不受「更新通道」设置影响。
- 两条线的 Release 资产在同一个仓库里共享，靠清单里的版本号区分，所以发测试版不用新开仓库。
- 详细说明见同目录的 `BETA.md`。
- 安装包清单为 `requireAdministrator`：双击即请求管理员权限（UAC 选「是」），任意目录都可运行；
  开发者署名 `xL1nUwU`（`csproj` 的 `Company`/`Authors`/`Copyright` + 关于页「开发者」一行）。
