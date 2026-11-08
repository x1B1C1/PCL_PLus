[简体中文](README.md) | **English** | [繁體中文](README-ZH_TW.md)

<div align="center">

# PCL_Plus

Developer: **xL1nUwU** · Based on [PCL Lite](https://github.com/askbbtsk/pcl-lite) and [PCL](https://github.com/Meloong-Git/PCL)

Preface: PCL and its guard squad keep saying we "touched the cake", and hand out labels while they're at it. I'd like to know — does this hat factory have some grudge against the bakery? How come the hat factory is the one always touching the cake?
[A mysterious article, courtesy of cubk](https://github.com/x1B1C1/PCL_Plus/blob/main/readmedlc.md)

[![Stars](https://img.shields.io/github/stars/x1B1C1/PCL_Plus?style=for-the-badge&logo=github&labelColor=444444&color=eac54f)](https://github.com/x1B1C1/PCL_Plus/stargazers)
[![Release](https://img.shields.io/github/v/release/x1B1C1/PCL_Plus?include_prereleases&label=release&logo=github&style=for-the-badge)](https://github.com/x1B1C1/PCL_Plus/releases)
[![Downloads](https://img.shields.io/github/downloads/x1B1C1/PCL_Plus/total?style=for-the-badge&logo=github&labelColor=444444&color=1F883D)](https://github.com/x1B1C1/PCL_Plus/releases)

[Source repository](https://github.com/x1B1C1/PCL_Plus) |
[Download](https://github.com/x1B1C1/PCL_Plus/releases) |
[Documentation](docs/) |
[License](#-license) |
[Submit issues](https://github.com/x1B1C1/PCL_Plus/issues/new)

</div>

> **NOT AN OFFICIAL MINECRAFT PRODUCT.** NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.
> 非 MINECRAFT 官方产品。未经 MOJANG 或 MICROSOFT 批准，也不与 MOJANG 或 MICROSOFT 关联。

PCL_Plus is an independent Minecraft launcher project, with extensive changes and fixes on top of its open-source base.

This project is released under the **MIT License** with additional copyleft terms — see [License](#-license).

**Do not use this project if you are located in mainland China.**

## 📦 Download

Get the latest build from [Releases](https://github.com/x1B1C1/PCL_Plus/releases).

| Architecture | File | Notes |
|---|---|---|
| x64 | `PCL_Plus_x64.exe` | Stable channel (`main`), single file, no extraction needed |
| x64 | `PCL_Plus_v2.0.0-beta_x64.exe` | Beta channel (`beta`), Aero enabled by default |

- Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- The single-file build extracts native libraries to `%TEMP%\.net\<exe name>\`; if that directory is
  not writable, use the **folder build** under `release-assets/` (run `Plain Craft Launcher 2.exe`)
- Built-in updater: stable builds read the `main` branch manifests of `x1B1C1/PCL_Plus`, beta builds
  read `beta`; `x1B1C1/PCL_Plus_OTA` is the backup server, used only when the primary is unavailable
- Pre-release versions (e.g. `2.0.0-beta`) are treated as the beta channel by the updater

## 💻 Supported Platforms

| Operating System | Support Status | Requirements |
|---|---|---|
| Windows 10 1809 (17763) or later | ✅ Fully supported | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 8 to Windows 10 1809 (17763) | ⚠️ Expected to run; support offered at discretion | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Windows 7 or earlier | ❌ Not supported | N/A |
| macOS / Linux / Other OS | ⚠️ Cross-platform development only (cross-compilation) | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |

**✅ Fully supported**: we will provide as much support as possible, but you must ensure you are using the latest version of the launcher.

**⚠️ Expected to run; support offered at discretion**: PCL_Plus should run on these platforms, but full functionality is not guaranteed. You may need to upgrade to a fully supported OS version to receive further support.

**❌ Not supported**: PCL_Plus is barely usable on these platforms and may not even launch. Please upgrade your OS to use PCL_Plus.

**⚠️ Cross-platform development only (cross-compilation)**: PCL_Plus's source code can be built on macOS and Linux, but cannot run there directly. As a developer, you can develop on these platforms and then move the build output to Windows for testing.

**Note**:
Only the latest version of the launcher is supported.
Depending on the nature of certain issues (e.g. an incomplete system environment), you may still need to upgrade your OS to keep receiving support.
PCL_Plus always recommends using the latest version of your OS for the best experience.
You can still try PCL_Plus on unsupported systems, but you may run into many additional problems.

## 📖 Documentation

> The documents below are currently written in Simplified Chinese.

| Document | What it covers |
| --- | --- |
| [docs/README.md](docs/README.md) | Index, project layout, where the version number actually lives |
| [Theming & colours](docs/THEME.md) | How the eight-step palette is computed, the 14 presets, custom themes, adding a new preset |
| [Config & data directories](docs/CONFIG.md) | Where the config files are, editing them by hand, migration logic |
| [Discord Rich Presence](docs/DISCORD.md) | What is shown, privacy boundaries, switching to your own Discord app |
| [Build & release](docs/BUILD.md) | Compiling, secret injection, signing, publishing a release |
| [Azure login](docs/AZURE-LOGIN.md) | Registering an app from scratch to sign in with your own client_id |
| [MCP](docs/MCP.md) | Letting AI clients drive the launcher directly |
| [Open-source inventory](docs/CREDITS.md) | Where the code came from, third-party libraries, and their licenses |
| [NOTICE](NOTICE) | Third-party components bundled with the distribution and their licence obligations |

## 🔒 License

This project follows the **[PCL Limited Distribution License](LICENSE)** (including the *PCL Repository Fair Use Guide*); the [`LICENSE`](LICENSE) file is authoritative.
**PCL_Plus is an independent third-party derivative of PCL and is not affiliated with, endorsed by, or connected to the PCL author (LTCat / 龙腾猫跃).**

**Lineage**: PCL_Plus -> [PCL Lite](https://github.com/askbbtsk/pcl-lite) -> [PCL (Plain Craft Launcher)](https://github.com/Meloong-Git/PCL).

To satisfy the "heavy use" requirements of that license, this project:

- **Credits the original author**: the first entry of the About page is **龙腾猫跃 (LTCat)** with a [sponsorship link](https://meloong.com/afd/a/LTCat)
- **Publishes the source**: this repository holds the complete source; build steps are in [the docs](docs/README.md)
- **Keeps the original license**: no extra terms are layered on top of it
- **Keeps a distinguishable name**: starts with **PCL** plus a third-party suffix (`PCL_Plus`), without implying official status
- **Keeps the genuine-copy prompt**: the same (or stronger) purchase and sponsorship prompt as `McLaunchPrecheck`
- **Has no sponsorship unlock**: no feature is gated behind a donation, and the main hue stays blue
- **Is unrelated to Mojang / Microsoft**: not approved by or associated with Mojang Studios or Microsoft
- **Regional restriction**: do not use in mainland China
- **License may change**: the original license may be updated at any time without notice; updates apply only to versions obtained afterwards

**Third-party components carry their own licences**, which cover only those parts and do not change the licence of the project as a whole:

- **Bedrock Edition support** (download + install + launch) is ported from
  [PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition) and remains under
  **Apache-2.0** (per its clause 4). Full text: [`LICENSE-APACHE`](LICENSE-APACHE).
- **Terracotta** is bundled as an unmodified binary and remains under **AGPL-3.0-or-later** (with its
  packaging / IPC exception). Full text: [`LICENSE-AGPL-3.0`](LICENSE-AGPL-3.0); its source ships in
  [`tools/t/`](tools/t/); the required copyright credit is shown in the launcher UI
  (Settings → Launcher → Terracotta).
- Other bundled components (`VCRUNTIME140.DLL`, Lucide icons, …) are listed in [`NOTICE`](NOTICE);
  a complete inventory of code sources and third-party libraries is in
  [the open-source inventory](docs/CREDITS.md).

## ❤️ Credits

- **龙腾猫跃 (LTCat)** — author of PCL (Plain Craft Launcher) and the starting point of this project; you can support him via [Afdian](https://meloong.com/afd/a/LTCat).
- The maintainers of **PCL Lite** and **PCL-Symbio-Edition** — the direct upstreams of this project.
- **burningtnt**, author of Terracotta, and everyone behind the open-source projects that make the Minecraft community better.
- Everyone who filed issues, sent pull requests, or gave feedback.

The full list of code sources is in [the open-source inventory](docs/CREDITS.md); bundled third-party components are listed in [NOTICE](NOTICE).
