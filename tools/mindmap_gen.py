# -*- coding: utf-8 -*-
"""Generate the PCL_Plus architecture mind map as SVG + PNG."""
import os
from PIL import Image, ImageDraw, ImageFont

OUT_DIR = r"D:\Desktop\src\PCL_PLus_1\PCL_PLus\docs"
FONT = r"C:\Windows\Fonts\msyh.ttc"
FONT_B = r"C:\Windows\Fonts\msyhbd.ttc"

S = 2  # supersample for PNG

BRANCHES = [
    ("入口与生命周期", "#4E79A7", [
        "Program.Main → Lifecycle.OnInitialize()",
        "9 个 LifecycleState 顺序推进",
        "ApplicationService 创建 WPF Application",
        "MainWindowService 创建 FormMain",
        "[LifecycleService] + [LifecycleScope]",
        "注册表编译期生成，无 DI 容器",
        "服务 = 受管静态单例，无构造注入",
    ]),
    ("工程结构", "#A0CBE8", [
        "Plain Craft Launcher 2：WPF 前端",
        "（命名空间跟随目录，GlobalUsings 兜底）",
        "PCL.Core：基础设施 + 领域模型",
        "PCL.Core.SourceGenerators：代码生成",
        "PCL.Core.Test：xUnit（部分永久红）",
        "依赖方向：前端 → Core 静态 API",
        "SRC-Backups：V1.0-SRC / Beta-V1.2-Aero",
    ]),
    ("UI 体系", "#F28E2B", [
        "FormMain：PanMainLeft / PanMainRight 双宿主",
        "PageType + PageSubType 两级路由",
        "MyPageLeft / MyPageRight（页面状态机）",
        "~45 个自绘控件（MyCard / MyButton …）",
        "ThemeService：ColorBrush1..8 动态主题",
        "本地化：4 份 Languages/*.xaml",
        "动画：Aa* 旧 API + AnimationService 新引擎",
    ]),
    ("加载器框架", "#FFBE7D", [
        "LoaderBase：状态机 + 进度 + 任务栏",
        "LoaderTask<In,Out>：Task.Run 执行单函数",
        "LoaderCombo：串行，output → input 传递",
        "block=false 的子任务并行执行",
        "取消是协作式：轮询 IsAborted",
        "LoaderFolderRun：目录变更才重扫",
        "全应用耗时操作的唯一范式",
    ]),
    ("配置系统", "#59A14F", [
        "外观：Config（偏好）/ States（会话）",
        "[ConfigGroup] + [ConfigItem] 声明式",
        "4 种 ConfigSource：漫游 / 加密 / 本地 / 实例",
        "config.v1.json（漫游）/ config.v1.yml（本地）",
        "ModSetup：配置 → UI 的总闸（35+ Observer）",
        "写盘批量异步（10s），勿回读验证",
        "改配置项必须 Rebuild（生成器不增量）",
    ]),
    ("Minecraft 域", "#8CD17D", [
        "McInstance：versions\\<名>\\ + PathIndie 隔离",
        "启动 = 10 阶段 LoaderCombo（前 3 阶段并行）",
        "Java 选择 → 参数构建 → natives 解压",
        "启动进程 → ModWatcher 监控 / 崩溃分析",
        "下载：DownloadFile → LoaderDownload",
        "→ FileDownloader（分块 / 重试 / 校验）",
        "崩溃规则：CrashRuleCatalog 扁平规则表",
    ]),
    ("网络与安全", "#B6992D", [
        "NetworkService：全应用单一 HttpClient 管线",
        "NetworkGuardHandler：按「用途」白名单拦截",
        "首次联网弹同意框（可整体放行）",
        "镜像源竞速 + Polly 重试 + DoH",
        "多个命名 client（mojang / modrinth …）",
    ]),
    ("特色模块", "#F1CE63", [
        "MCP 服务器：32 个 pcl_* 工具（回环 HTTP）",
        "Terracotta：内置运行时 + 联机隧道",
        "Bedrock：UWP AppX + 便携 GDK",
        "Lunar：IPC / 安装 / 账号 / 社交（回迁自 PCL Lite）",
        "实例选择侧边栏：基岩版 / Lunar 入口",
        "更新：v2→v1 清单降级 + 多源竞速",
        "Discord RPC / 缓存 / 存档 / 服务器 Ping",
        "主服务器：源码仓 PCL_PLus（清单 + Releases）",
        "备用服务器：OTA 仓 PCL_Plus_OTA（全挂才切换）",
        "双线：main 正式版 / beta 测试线",
    ]),
    ("数据与路径", "#499894", [
        "<exe>\\PCL：本地数据、日志、实例配置",
        "%APPDATA%\\PCL_Plus：共享配置（漫游）",
        "%LOCALAPPDATA%\\PCL_Plus：实例锁、本地数据",
        "%TEMP%\\PCL_Plus：缓存与临时文件",
        "Debug 版使用 PCL_Plus_Debug 后缀",
        "两版共用同一实例锁，不能同时运行",
        "不可写时逐级回退：<exe>\\PCL → %LOCALAPPDATA%",
        "清单 requireAdministrator：启动即请求管理员",
    ]),
    ("开发约定与陷阱", "#E15759", [
        "构建只用 csproj，.slnx 已损坏",
        "XAML 事件签名错误只在运行时炸",
        "4 份语言文件必须同步",
        "生成器改动：build-server shutdown + Rebuild",
        "源码编码混杂（GBK / UTF-8）",
        "含中文的 .ps1 必须存 UTF-8 with BOM",
        "版本号只改 metadata.json",
        "禁止提交 client_id / 密钥 / bin / obj",
    ]),
]

PAD_X = 14
LEAF_LH = 30
BRANCH_GAP = 26
ROOT_W, ROOT_H = 270, 108
SIDE_GAP = 96
LEAF_GAP = 46
MARGIN_Y = 40


def f(px, bold=False):
    return ImageFont.truetype(FONT_B if bold else FONT, px)


F_ROOT = f(34 * S, True)
F_BRANCH = f(21 * S, True)
F_LEAF = f(17 * S)
F_FOOT = f(15 * S)
F_SUB = f(22 * S)


def tw(draw, text, font):
    return draw.textlength(text, font=font)


def layout():
    probe = Image.new("RGBA", (10, 10))
    d = ImageDraw.Draw(probe)

    branches = []
    for title, color, leaves in BRANCHES:
        w = tw(d, title, F_BRANCH) / S + PAD_X * 2
        lw = max(tw(d, l, F_LEAF) / S for l in leaves)
        h = len(leaves) * LEAF_LH
        branches.append({
            "title": title, "color": color, "leaves": leaves,
            "bw": w, "bh": 46, "leaves_h": h, "leaf_w": lw,
            "block_h": max(h, 46),
        })

    right = branches[0::2]
    left = branches[1::2]

    def side_height(side):
        return sum(b["block_h"] for b in side) + BRANCH_GAP * max(0, len(side) - 1)

    hr, hl = side_height(right), side_height(left)
    body_h = max(hr, hl)

    max_leaf_w = max(b["leaf_w"] for b in branches)
    max_bw = max(b["bw"] for b in branches)
    half = ROOT_W / 2 + SIDE_GAP + max_bw + LEAF_GAP + max_leaf_w + 110
    W = int(half * 2)
    H = int(body_h + MARGIN_Y * 2 + 40)

    cx = W / 2
    cy = H / 2

    def place(side, direction, total_h):
        y = cy - total_h / 2
        for b in side:
            block_top = y
            by = block_top + (b["block_h"] - b["bh"]) / 2
            if direction > 0:
                bx = cx + ROOT_W / 2 + SIDE_GAP
                leaf_x = bx + b["bw"] + LEAF_GAP
                b["text_x"] = bx + PAD_X
                b["leaf_text_x"] = leaf_x + 14
            else:
                bx = cx - ROOT_W / 2 - SIDE_GAP - b["bw"]
                leaf_x = bx - LEAF_GAP
                b["text_x"] = bx + PAD_X
                b["leaf_text_x"] = leaf_x - 14
            b["x"], b["y"] = bx, by
            b["dir"] = direction
            b["leaf_x"] = leaf_x
            b["leaf_top"] = block_top + (b["block_h"] - b["leaves_h"]) / 2
            y += b["block_h"] + BRANCH_GAP

    place(right, 1, hr)
    place(left, -1, hl)
    return branches, W, H, cx, cy


def bezier(p0, p1, p2, p3, n=24):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1 - t
        x = mt**3 * p0[0] + 3 * mt**2 * t * p1[0] + 3 * mt * t**2 * p2[0] + t**3 * p3[0]
        y = mt**3 * p0[1] + 3 * mt**2 * t * p1[1] + 3 * mt * t**2 * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def esc(t):
    return t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def render_svg(path, branches, W, H, cx, cy):
    out = []
    out.append(f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
               f'viewBox="0 0 {W} {H}" font-family="Microsoft YaHei, Segoe UI, sans-serif">')
    out.append(f'<rect width="{W}" height="{H}" fill="#1e1f22"/>')

    rx, ry = cx - ROOT_W / 2, cy - ROOT_H / 2
    out.append(f'<rect x="{rx}" y="{ry}" width="{ROOT_W}" height="{ROOT_H}" rx="16" '
               f'fill="#2b2d31" stroke="#eac54f" stroke-width="2.5"/>')
    out.append(f'<text x="{cx}" y="{cy - 12}" fill="#ffffff" font-size="34" font-weight="bold" '
               f'text-anchor="middle">PCL_Plus</text>')
    out.append(f'<text x="{cx}" y="{cy + 26}" fill="#eac54f" font-size="22" '
               f'text-anchor="middle">架构导图</text>')

    for b in branches:
        bx, by, bw, bh, d = b["x"], b["y"], b["bw"], b["bh"], b["dir"]
        sx = cx + ROOT_W / 2 * d
        ex = bx if d > 0 else bx + bw
        pts = bezier((sx, cy), (sx + 50 * d, cy), (ex - 50 * d, by + bh / 2), (ex, by + bh / 2))
        dd = "M " + " L ".join(f"{x:.1f},{y:.1f}" for x, y in pts)
        out.append(f'<path d="{dd}" fill="none" stroke="{b["color"]}" stroke-width="3" opacity="0.85"/>')
        out.append(f'<rect x="{bx}" y="{by}" width="{bw}" height="{bh}" rx="10" '
                   f'fill="{b["color"]}" opacity="0.92"/>')
        out.append(f'<text x="{b["text_x"]}" y="{by + bh / 2 + 7}" fill="#ffffff" font-size="21" '
                   f'font-weight="bold" text-anchor="start">{esc(b["title"])}</text>')
        lx = b["leaf_x"]
        for i, leaf in enumerate(b["leaves"]):
            ly = b["leaf_top"] + i * LEAF_LH + LEAF_LH / 2
            out.append(f'<circle cx="{lx}" cy="{ly}" r="4" fill="{b["color"]}"/>')
            pts2 = bezier((ex, by + bh / 2), (ex, by + bh / 2), (lx, ly), (lx, ly), 16)
            d2 = "M " + " L ".join(f"{x:.1f},{y:.1f}" for x, y in pts2)
            out.append(f'<path d="{d2}" fill="none" stroke="{b["color"]}" stroke-width="1.4" opacity="0.45"/>')
            tx = b["leaf_text_x"]
            ta = "start" if d > 0 else "end"
            out.append(f'<text x="{tx:.1f}" y="{ly + 6:.1f}" fill="#d8d9da" font-size="17" '
                       f'text-anchor="{ta}">{esc(leaf)}</text>')

    out.append(f'<text x="{W - 28}" y="{H - 22}" fill="#6b6f76" font-size="15" '
               f'text-anchor="end">PCL_Plus v1.1.0 · 架构导图</text>')
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out))


def render_png(path, branches, W, H, cx, cy):
    img = Image.new("RGB", (int(W * S), int(H * S)), "#1e1f22")
    d = ImageDraw.Draw(img)

    def rrect(x, y, w, h, r, fill, outline=None, width=1):
        d.rounded_rectangle([x * S, y * S, (x + w) * S, (y + h) * S], radius=r * S,
                            fill=fill, outline=outline, width=width * S)

    def text(x, y, s, font, fill, anchor="la"):
        d.text((x * S, y * S), s, font=font, fill=fill, anchor=anchor)

    rx, ry = cx - ROOT_W / 2, cy - ROOT_H / 2
    rrect(rx, ry, ROOT_W, ROOT_H, 16, "#2b2d31", "#eac54f", 3)
    text(cx, cy - 14, "PCL_Plus", F_ROOT, "#ffffff", "mm")
    text(cx, cy + 30, "架构导图", F_SUB, "#eac54f", "mm")

    for b in branches:
        bx, by, bw, bh, dd = b["x"], b["y"], b["bw"], b["bh"], b["dir"]
        sx = cx + ROOT_W / 2 * dd
        ex = bx if dd > 0 else bx + bw
        pts = bezier((sx, cy), (sx + 50 * dd, cy), (ex - 50 * dd, by + bh / 2), (ex, by + bh / 2))
        d.line([(x * S, y * S) for x, y in pts], fill=b["color"], width=3 * S, joint="curve")
        rrect(bx, by, bw, bh, 10, b["color"])
        text(b["text_x"], by + bh / 2, b["title"], F_BRANCH, "#ffffff", "lm")

        lx = b["leaf_x"]
        for i, leaf in enumerate(b["leaves"]):
            ly = b["leaf_top"] + i * LEAF_LH + LEAF_LH / 2
            d.ellipse([(lx - 4) * S, (ly - 4) * S, (lx + 4) * S, (ly + 4) * S], fill=b["color"])
            pts2 = bezier((ex, by + bh / 2), (ex, by + bh / 2), (lx, ly), (lx, ly), 16)
            d.line([(x * S, y * S) for x, y in pts2], fill=b["color"], width=2)
            anchor = "lm" if dd > 0 else "rm"
            text(b["leaf_text_x"], ly, leaf, F_LEAF, "#d8d9da", anchor)

    text(W - 28, H - 16, "PCL_Plus v1.1.0 · 架构导图", F_FOOT, "#6b6f76", "rs")
    img = img.resize((int(W), int(H)), Image.LANCZOS)
    img.save(path)


if __name__ == "__main__":
    os.makedirs(OUT_DIR, exist_ok=True)
    branches, W, H, cx, cy = layout()
    svg = os.path.join(OUT_DIR, "architecture-mindmap.svg")
    png = os.path.join(OUT_DIR, "architecture-mindmap.png")
    render_svg(svg, branches, W, H, cx, cy)
    render_png(png, branches, W, H, cx, cy)
    print("size", W, H)
    print(svg)
    print(png)
