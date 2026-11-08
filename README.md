**简体中文** | [English](README-EN.md) | [繁體中文](README-ZH_TW.md)

<div align="center">

# PCL_Plus

开发者：**xL1nUwU** · 基于 [PCL Lite](https://github.com/askbbtsk/pcl-lite) 与 [PCL](https://github.com/Meloong-Git/PCL)

前言：pcl和它的护卫队天天说动了蛋糕，顺手扣帽子，我想知道pcl这个帽子工厂和烘焙店是有仇吗 怎么帽子工厂天天动蛋糕？
[神秘文章来自cubk](https://github.com/x1B1C1/PCL_Plus/blob/main/readmedlc.md)

[![Stars](https://img.shields.io/github/stars/x1B1C1/PCL_Plus?style=for-the-badge&logo=github&labelColor=444444&color=eac54f)](https://github.com/x1B1C1/PCL_Plus/stargazers)
[![Release](https://img.shields.io/github/v/release/x1B1C1/PCL_Plus?include_prereleases&label=release&logo=github&style=for-the-badge)](https://github.com/x1B1C1/PCL_Plus/releases)
[![Downloads](https://img.shields.io/github/downloads/x1B1C1/PCL_Plus/total?style=for-the-badge&logo=github&labelColor=444444&color=1F883D)](https://github.com/x1B1C1/PCL_Plus/releases)

[源仓库](https://github.com/x1B1C1/PCL_Plus) |
[下载](https://github.com/x1B1C1/PCL_Plus/releases) |
[文档](docs/) |
[许可证](#-许可证) |
[提交问题](https://github.com/x1B1C1/PCL_Plus/issues/new)

</div>

> **非 MINECRAFT 官方产品。** 未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。
> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

PCL_Plus 是一个独立的 Minecraft 启动器项目，在开源代码基础上加入了大量改动与修复。

本项目以 **MIT 许可证** 发布，并附带传染性（copyleft）附加条款，详见[许可证](#-许可证)。

**中国大陆用户请勿使用。**

## 📦 下载

前往 [Releases](https://github.com/x1B1C1/PCL_Plus/releases) 下载最新版本。

| 架构 | 文件 | 说明 |
|---|---|---|
| x64 | `PCL_Plus_x64.exe` | 正式版（更新线 `main`），单文件、免解压 |
| x64 | `PCL_Plus_v2.0.0-beta_x64.exe` | 测试版（更新线 `beta`，Aero 默认开启） |

- 需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- 单文件版启动时会把原生库解压到 `%TEMP%\.net\<exe 名>\`；若该目录不可写，请改用仓库
  `release-assets/` 里的**文件夹版**（双击其中的 `Plain Craft Launcher 2.exe`）
- 启动器内置自动更新：正式版读 `x1B1C1/PCL_Plus` 的 `main` 分支清单，测试版读 `beta` 分支；
  `x1B1C1/PCL_Plus_OTA` 作为备用服务器，主服务器不可用时才切换
- 预发布版本（如 `2.0.0-beta`）由更新通道视为 beta 通道

## 💻 支持平台

| 操作系统 | 支持情况 | 环境要求 |
|---|---|---|
| Windows 10 1809 (17763) 或更高 | ✅ 完整支持 | [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 8 - Windows 10 1809 (17763) | ⚠️ 理论能跑，酌情提供支持 | [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 7 或更低版本 | ❌ 不支持 | / |
| macOS / Linux / 其他操作系统 | ⚠️ 仅跨平台开发支持（交叉编译） | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

**✅ 完整支持**：尽可能提供一切相关支持，但必须确保启动器为最新版本。

**⚠️ 理论能跑，酌情提供支持**：PCL_Plus 应该可以在这些平台上运行，但不保证功能完全可用。你可能需要升级到完整支持的系统版本以获得进一步支持。

**❌ 不支持**：PCL_Plus 在这些平台的可用性较低，甚至压根打不开。请升级操作系统以使用 PCL_Plus。

**⚠️ 仅跨平台开发支持（交叉编译）**：PCL_Plus 的源代码可以在 macOS 与 Linux 平台编译，但无法直接运行。作为开发者，你可以在这些平台上进行开发，然后将编译产物转移到 Windows 系统测试。

**注**：
仅对最新版本的启动器提供支持。
取决于部分问题的特殊性（如系统不完整），有时你仍然必须升级操作系统以继续获得支持。
PCL_Plus 始终建议使用最新版本的操作系统以获得最佳体验。
你仍然可以尝试在不受支持的系统上使用 PCL_Plus，但可能会遇到很多额外问题。

## 📖 文档

| 文档 | 讲什么 |
| --- | --- |
| [docs/README.md](docs/README.md) | 索引、项目结构、版本号在哪 |
| [主题与配色](docs/THEME.md) | 八级色阶怎么算、14 个预设、自定义主题、怎么加新主题 |
| [配置与数据目录](docs/CONFIG.md) | 配置文件位置、手动改法、迁移逻辑 |
| [Discord 状态显示](docs/DISCORD.md) | 显示内容、隐私边界、换自己的应用 |
| [构建与发布](docs/BUILD.md) | 编译、密钥注入、签名、发版 |
| [Azure 登录](docs/AZURE-LOGIN.md) | 从零注册应用，用自己的 client_id 走正版登录 |
| [MCP](docs/MCP.md) | 让 AI 客户端直接操作启动器 |
| [开源项目清单](docs/CREDITS.md) | 代码来源、第三方库、各自的许可证 |
| [NOTICE](NOTICE) | 随分发物打包的第三方组件及其许可义务 |

## 🔒 许可证

本项目沿用 **[《PCL 分发有限许可》](LICENSE)**（含《PCL 存储库合理使用指南》），完整条款以 [`LICENSE`](LICENSE) 文件为准。
**PCL_Plus 是第三方基于 PCL 独立进行的二次创作，与 PCL 官方（龙腾猫跃）没有隶属关系，也不代表官方立场。**

**衍生链**：PCL_Plus → [PCL Lite](https://github.com/askbbtsk/pcl-lite) → [PCL（Plain Craft Launcher）](https://github.com/Meloong-Git/PCL)。

按该许可中「重度使用」的要求，本项目已经做到：

- **署名原作者**：「设置 → 关于」页面的**首位**给出**龙腾猫跃**的署名与[赞助链接](https://meloong.com/afd/a/LTCat)
- **公开源代码**：本仓库即全部源码，构建方式见[文档](docs/README.md)
- **沿用原许可**：不另立条款，`LICENSE` 与原许可保持一致
- **名称可辨识**：以 **PCL** 开头并带第三方修改后缀（`PCL_Plus`），不暗示由官方提供
- **正版劝导**：保留与 `McLaunchPrecheck` 一致的正版购买劝导与赞助劝导
- **不做赞助解锁**：没有任何「赞助才能解锁」的功能，界面主要色相保持蓝色
- **与 Mojang / Microsoft 无关**：本项目与 Mojang Studios、Microsoft 及其关联公司没有任何关系，未获其批准、授权或背书
- **地域限制**：中国大陆用户请勿使用
- **协议可变**：原许可可能随时更新，且不提前通知；更新只对之后获取的版本生效，**已经 fork 或已经分发的版本不受影响**

**第三方组件另有许可证**，它们只覆盖各自那部分，不改变本项目整体的许可证：

- **基岩版**（下载 + 安装 + 启动）移植自 [PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)，
  该部分仍受 **Apache-2.0** 约束（依据其第 4 条），全文见 [`LICENSE-APACHE`](LICENSE-APACHE)。
- **陶瓦联机（Terracotta）** 以未修改的二进制随启动器分发，受 **AGPL-3.0-or-later**（附打包与 IPC 例外）约束。
  许可全文见 [`LICENSE-AGPL-3.0`](LICENSE-AGPL-3.0)，对应源码随仓库提供于 [`tools/t/`](tools/t/)，
  界面中的版权标注在「设置 → 启动器 → 陶瓦联机」卡片里。
- 其余随包组件（`VCRUNTIME140.DLL`、Lucide 图标等）见 [`NOTICE`](NOTICE)；
  完整的代码来源与第三方库清单见[开源项目清单](docs/CREDITS.md)。



## ❤️ 致谢

- **龙腾猫跃（LTCat）** —— PCL（Plain Craft Launcher）原作者，本项目的起点；欢迎通过[爱发电](https://meloong.com/afd/a/LTCat)支持他。
- **PCL Lite** 与 **PCL-Symbio-Edition** 的维护者 —— 本项目的直接上游。
- **陶瓦联机（Terracotta）** 作者 burningtnt，以及所有让 Minecraft 社区更好用的开源项目。
- 所有提交过 Issue、PR 与反馈的玩家。

完整的代码来源清单见[开源项目清单](docs/CREDITS.md)，随包分发的第三方组件见 [NOTICE](NOTICE)。
