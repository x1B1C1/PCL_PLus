[简体中文](README.md) | [English](README-EN.md) | **繁體中文**

<div align="center">

# PCL_Plus

開發者：**xL1nUwU** · 基於 [PCL Lite](https://github.com/askbbtsk/pcl-lite) 與 [PCL](https://github.com/Meloong-Git/PCL)

前言：pcl 和它的護衛隊天天說動了蛋糕，順手扣帽子，我想知道 pcl 這個帽子工廠和烘焙店是有仇嗎 怎麼帽子工廠天天動蛋糕？
[神祕文章來自 cubk](https://github.com/x1B1C1/PCL_Plus/blob/main/readmedlc.md)

[![Stars](https://img.shields.io/github/stars/x1B1C1/PCL_Plus?style=for-the-badge&logo=github&labelColor=444444&color=eac54f)](https://github.com/x1B1C1/PCL_Plus/stargazers)
[![Release](https://img.shields.io/github/v/release/x1B1C1/PCL_Plus?include_prereleases&label=release&logo=github&style=for-the-badge)](https://github.com/x1B1C1/PCL_Plus/releases)
[![Downloads](https://img.shields.io/github/downloads/x1B1C1/PCL_Plus/total?style=for-the-badge&logo=github&labelColor=444444&color=1F883D)](https://github.com/x1B1C1/PCL_Plus/releases)

[來源倉庫](https://github.com/x1B1C1/PCL_Plus) |
[下載](https://github.com/x1B1C1/PCL_Plus/releases) |
[文件](docs/) |
[授權條款](#-授權條款) |
[提交問題](https://github.com/x1B1C1/PCL_Plus/issues/new)

</div>

> **非 MINECRAFT 官方產品。** 未經 MOJANG 或 MICROSOFT 批准，也不與 MOJANG 或 MICROSOFT 關聯。
> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.

PCL_Plus 是一個獨立的 Minecraft 啟動器專案，在開放原始碼基礎上加入了大量改動與修復。

本專案以 **MIT 授權條款** 發布，並附帶傳染性（copyleft）附加條款，詳見[授權條款](#-授權條款)。

**中國大陸使用者請勿使用。**

## 📦 下載

前往 [Releases](https://github.com/x1B1C1/PCL_Plus/releases) 下載最新版本。

| 架構 | 檔案 | 說明 |
|---|---|---|
| x64 | `PCL_Plus_x64.exe` | 正式版（更新線 `main`），單一檔案、免解壓縮 |
| x64 | `PCL_Plus_v2.0.0-beta_x64.exe` | 測試版（更新線 `beta`，Aero 預設開啟） |

- 需要已安裝 [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0)
- 單一檔案版啟動時會把原生函式庫解壓到 `%TEMP%\.net\<exe 名稱>\`；若該目錄不可寫，
  請改用 `release-assets/` 裡的**資料夾版**（雙擊其中的 `Plain Craft Launcher 2.exe`）
- 內建自動更新：正式版讀 `x1B1C1/PCL_Plus` 的 `main` 分支清單，測試版讀 `beta`；
  `x1B1C1/PCL_Plus_OTA` 作為備用伺服器，主要伺服器不可用時才切換
- 預發布版本（如 `2.0.0-beta`）會被更新通道視為 beta 通道

## 💻 支援平台

| 作業系統 | 支援情況 | 環境要求 |
|---|---|---|
| Windows 10 1809 (17763) 或更高 | ✅ 完整支援 | [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 8 - Windows 10 1809 (17763) | ⚠️ 理論能跑，酌情提供支援 | [.NET 10 桌面執行階段](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 7 或更低版本 | ❌ 不支援 | / |
| macOS / Linux / 其他作業系統 | ⚠️ 僅跨平台開發支援（交叉編譯） | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

**✅ 完整支援**：盡可能提供一切相關支援，但必須確保啟動器為最新版本。

**⚠️ 理論能跑，酌情提供支援**：PCL_Plus 應該可以在這些平台上執行，但不保證功能完全可用。你可能需要升級到完整支援的系統版本以獲得進一步支援。

**❌ 不支援**：PCL_Plus 在這些平台的可用性較低，甚至根本打不開。請升級作業系統以使用 PCL_Plus。

**⚠️ 僅跨平台開發支援（交叉編譯）**：PCL_Plus 的原始碼可以在 macOS 與 Linux 平台編譯，但無法直接執行。作為開發者，你可以在這些平台上進行開發，然後將編譯產物轉移到 Windows 系統測試。

**註**：
僅對最新版本的啟動器提供支援。
取決於部分問題的特殊性（如系統不完整），有時你仍然必須升級作業系統以繼續獲得支援。
PCL_Plus 始終建議使用最新版本的作業系統以獲得最佳體驗。
你仍然可以嘗試在不受支援的系統上使用 PCL_Plus，但可能會遇到很多額外問題。

## 📖 文件

| 文件 | 講什麼 |
| --- | --- |
| [docs/README.md](docs/README.md) | 索引、專案結構、版本號在哪裡 |
| [主題與配色](docs/THEME.md) | 八級色階怎麼算、14 個預設、自訂主題、怎麼新增主題 |
| [設定與資料目錄](docs/CONFIG.md) | 設定檔位置、手動改法、遷移邏輯 |
| [Discord 狀態顯示](docs/DISCORD.md) | 顯示內容、隱私邊界、換成自己的應用程式 |
| [建置與發布](docs/BUILD.md) | 編譯、金鑰注入、簽章、發版 |
| [Azure 登入](docs/AZURE-LOGIN.md) | 從零註冊應用程式，用自己的 client_id 走正版登入 |
| [MCP](docs/MCP.md) | 讓 AI 用戶端直接操作啟動器 |
| [開源專案清單](docs/CREDITS.md) | 程式碼來源、第三方函式庫、各自的授權條款 |
| [NOTICE](NOTICE) | 隨發行物打包的第三方元件及其授權義務 |

## 🔒 授權條款

本專案沿用 **[《PCL 分發有限許可》](LICENSE)**（含《PCL 儲存庫合理使用指南》），完整條款以 [`LICENSE`](LICENSE) 檔案為準。
**PCL_Plus 是第三方基於 PCL 獨立進行的二次創作，與 PCL 官方（龍騰貓躍）沒有隸屬關係，也不代表官方立場。**

**衍生鏈**：PCL_Plus → [PCL Lite](https://github.com/askbbtsk/pcl-lite) → [PCL（Plain Craft Launcher）](https://github.com/Meloong-Git/PCL)。

依該許可中「重度使用」的要求，本專案已經做到：

- **署名原作者**：「設定 → 關於」頁面的**首位**給出**龍騰貓躍**的署名與[贊助連結](https://meloong.com/afd/a/LTCat)
- **公開原始碼**：本儲存庫即全部原始碼，建置方式見[文件](docs/README.md)
- **沿用原許可**：不另立條款，`LICENSE` 與原許可保持一致
- **名稱可辨識**：以 **PCL** 開頭並帶第三方修改後綴（`PCL_Plus`），不暗示由官方提供
- **正版勸導**：保留與 `McLaunchPrecheck` 一致的正版購買勸導與贊助勸導
- **不做贊助解鎖**：沒有任何「贊助才能解鎖」的功能，介面主要色相保持藍色
- **與 Mojang / Microsoft 無關**：本專案與 Mojang Studios、Microsoft 及其關聯公司沒有任何關係，未獲其批准、授權或背書
- **地域限制**：中國大陸使用者請勿使用
- **協議可變**：原許可可能隨時更新，且不提前通知；更新只對之後取得的版本生效，**已經 fork 或已經分發的版本不受影響**

**第三方元件另有授權條款**，它們只涵蓋各自那部分，不改變本專案整體的授權條款：

- **基岩版**（下載 + 安裝 + 啟動）移植自 [PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)，
  該部分仍受 **Apache-2.0** 約束（依據其第 4 條），全文見 [`LICENSE-APACHE`](LICENSE-APACHE)。
- **陶瓦聯機（Terracotta）** 以未修改的二進位檔隨啟動器分發，受 **AGPL-3.0-or-later**（附打包與 IPC 例外）約束。
  授權全文見 [`LICENSE-AGPL-3.0`](LICENSE-AGPL-3.0)，對應原始碼隨儲存庫提供於 [`tools/t/`](tools/t/)，
  介面中的版權標註在「設定 → 啟動器 → 陶瓦聯機」卡片裡。
- 其餘隨包元件（`VCRUNTIME140.DLL`、Lucide 圖示等）見 [`NOTICE`](NOTICE)；
  完整的程式碼來源與第三方函式庫清單見[開源專案清單](docs/CREDITS.md)。

## ❤️ 致謝

- **龍騰貓躍（LTCat）** —— PCL（Plain Craft Launcher）原作者，本專案的起點；歡迎透過[愛發電](https://meloong.com/afd/a/LTCat)支持他。
- **PCL Lite** 與 **PCL-Symbio-Edition** 的維護者 —— 本專案的直接上游。
- **陶瓦聯機（Terracotta）** 作者 burningtnt，以及所有讓 Minecraft 社群更好用的開源專案。
- 所有提交過 Issue、PR 與回饋的玩家。

完整的程式碼來源清單見[開源專案清單](docs/CREDITS.md)，隨包分發的第三方元件見 [NOTICE](NOTICE)。
