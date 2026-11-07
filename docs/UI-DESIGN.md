# PCL_Plus UI 设计规范（v1.5）

这份文档定义 1.5 测试线使用的界面规则。它不改变任何功能行为，只统一「看起来应该是什么样」。

## 1. 设计原则

1. **克制**：默认界面只有一层投影、一种描边、一种强调色。装饰性元素（渐变、光效）只出现在标题栏与背景，不进入内容区。
2. **层次靠对比，不靠装饰**：标题用字重与字号区分，说明文字用 `ColorBrush2`，不额外加边框或底色。
3. **一致优先**：同类元素用同一个圆角、同一档字号、同一个内边距。新页面直接复用下方组件，不自行发明尺寸。
4. **可读性优先于通透**：亚克力、液态玻璃都属于背景层，卡片与文字必须保持足够不透明度（卡片 ≥ 74%）。

## 2. 尺寸与圆角

| 用途 | 值 |
| --- | --- |
| 卡片圆角 | 11 px |
| 按钮 / 输入框 / 下拉框圆角 | 8 px |
| 顶部导航按钮圆角 | 8 px |
| 提示气泡 / 菜单圆角 | 6 px / 8 px |
| 控件高度（按钮、顶部导航） | 30 px |
| 列表项高度 | 36–42 px（由使用处决定） |
| 卡片内边距 | 18 / 13（标题区），内容区常见 `25,40,25,10` |
| 页面外边距 | 24–25 px |

所有尺寸都取整数像素；需要随 DPI 缩放时使用 `ModBase.GetWPFSize()`，不要写死 `1.5` 这类值。

## 3. 字号层级

| 层级 | 字号 | 字重 | 颜色 |
| --- | --- | --- | --- |
| 页面主标题 | 18–20 | SemiBold | `ColorBrush1` |
| 卡片标题 | 13.5 | SemiBold | `ColorBrush1`（悬停 `ColorBrush2`） |
| 正文 / 按钮 | 13 | Normal | `ColorBrush1` |
| 说明 / 次要信息 | 12 | Normal | `ColorBrush2` |
| 徽标 / 角标 | 11 | SemiBold | `ColorBrush2` |

字体统一走 `LaunchFontFamily`（`Resources/#PCL English` + 微软雅黑 UI + Segoe UI）。

## 4. 颜色用法

- 强调色只用 `ColorBrush3`（主蓝）与其深浅变体 `ColorBrush2/4/5`。
- 描边用 `ColorBrush6`，分隔线用 `ColorBrush7`，页面底色用 `ColorBrush8`。
- 卡片背景用 `ColorBrushTransparentBackground`；悬浮态用 `ColorBrushHalfWhite`。
- 语义色只用于状态：`ColorBrushError/Warn/Info/Fatal`。
- 全部通过 `{DynamicResource}` 引用，禁止在页面里写死颜色（亚克力模式依赖运行时替换）。

## 5. 组件规格

### 卡片 `MyCard`

- 圆角 11 px，1 px `ColorBrush6` 描边，背景 `ColorBrushTransparentBackground`。
- 投影：单层柔和投影，静止不透明度 0.05、悬停 0.16、半径 10 px。不再使用 0.4 的硬投影。
- 标题：13.5 px SemiBold，左对齐，`Margin="18,13,0,0"`；内容区自行预留约 40 px 标题高度。
- 可折叠卡片（`Swap`）的箭头与标题同色，悬停时一起变为 `ColorBrush2`。

### 按钮 `MyButton`

- 圆角 8 px，高度 30 px（页脚大按钮可到 36 px）。
- 描边随 `ColorType`：普通 `ColorBrush1`、强调 `ColorBrush3`、危险 `ColorBrushRedLight`。
- 文字 13 px，居中；悬停时描边与文字同步变色，不做位移动画。

### 顶部导航 `MyRadioButton`

- 圆角 8 px（v1.5 起不再是胶囊），高度 30 px。
- 图标 16 px，左内边距 12 px，图标与文字间距 8 px，右内边距 14 px。
- 选中：背景 `ColorBrush3`、文字与图标白；悬停：背景 `ColorBrush6/7`、文字 `ColorBrush3`。

### 左侧列表 `MyListItem`

- 选中态左侧指示条 3 px（`ColumnCheck`），颜色 `ColorBrush3`。
- 图标列 34 px，文字 13 px，右侧留 35 px 给计数或角标。
- 分组标题用 12 px + `ColorBrush2`，与条目左对齐。

### 输入框 / 下拉框

- 圆角 8 px，1 px 描边（默认 `ColorBrushBg0`，聚焦 `ColorBrush3`）。
- 高度 28–30 px，内边距 8.5 px，提示文字 12 px `ColorBrush2`。

### 提示气泡 `ToolTip`

- 背景 `ColorBrush8`，1 px `ColorBrush6` 描边，圆角 6 px，内边距 `10,7`。
- 文字 12 px `ColorBrush1`，柔和投影（0.14 / 8 px）。

### 右键菜单 `ContextMenu`

- 圆角 8 px，1 px `ColorBrush6` 描边，背景 `ColorBrushWhite`。
- 投影 0.18 / 10 px；菜单项高 26 px，悬停背景 `ColorBrush3` + 白字。

## 6. 动效

- 只在「进入 / 离开 / 展开」时动，不在悬停时做位移。
- 悬停变色 90 ms，页面切换 100–350 ms，缓动沿用 `AniEaseOutFluent` / `AniEaseOutBack`。
- 批量改样式时用 `ModAnimation.AniControlEnabled += 1` 包住，避免逐条动画。

## 7. 扩展方式

1. 新页面优先用 `MyCard` 分区，卡片标题直接传 `Title`。
2. 需要新控件时，先看 `Controls/` 里有没有近似组件；没有再加，并沿用上表的圆角与字号。
3. 改全局样式只改 `Application.xaml` 与 `Controls/` 下的控件模板，不要在页面里覆盖。
4. 改动后至少跑一遍：

```powershell
dotnet build "Plain Craft Launcher 2\Plain Craft Launcher 2.csproj" -c Debug
python tools/audit-xaml-events.py
```

## 8. v1.5 实际改动清单

| 文件 | 改动 |
| --- | --- |
| `Application.xaml` | 新增 `ToolTip` 样式；`ContextMenu` 圆角 3→8、投影 0.4/4→0.18/10；`ComboBox`、`PasswordBox` 圆角统一 8 |
| `Controls/MyCard.cs` | 圆角 9→11；投影半径 3→10、静止 0.07→0.05、悬停 0.4→0.16；标题 13 Bold → 13.5 SemiBold |
| `Controls/MyButton.xaml` | 圆角 7→8 |
| `Controls/MyRadioButton.xaml` | 圆角 13.5→8；高度 27→30；文字 13 px；右内边距 12→14 |
| `Controls/MyListItem.xaml` | 选中指示条 2→3 px |
| `Controls/MyTextBox.cs` | 圆角默认 3→8 |
| `FormMain.xaml` | 顶部导航容器高度 27→30、外边距 13→14 |
