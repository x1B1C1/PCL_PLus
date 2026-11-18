# 生成联机原理图（Pillow 手绘，无第三方绘图库依赖）
# 用法：python tools/link_diagram.py <输出 png>
import sys

from PIL import Image, ImageDraw, ImageFont

W, H = 1600, 2180
BG = "#FFFFFF"
INK = "#1F2937"
MUTED = "#6B7280"
LINE = "#D6DEE8"
BLUE = "#1E7FD4"
BLUE_BG = "#EAF3FC"
GREEN = "#1F9D63"
GREEN_BG = "#E9F6EF"
AMBER = "#B7791F"
AMBER_BG = "#FDF4E3"
RED = "#C0392B"
RED_BG = "#FDEEEC"
GREY_BG = "#F5F7FA"

FONT_PATH = r"C:\Windows\Fonts\msyh.ttc"
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"


def font(size, bold=False):
    return ImageFont.truetype(FONT_BOLD if bold else FONT_PATH, size)


img = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(img)


def text(x, y, s, size=26, color=INK, bold=False, anchor="la"):
    d.text((x, y), s, font=font(size, bold), fill=color, anchor=anchor)


def box(x1, y1, x2, y2, fill, outline, radius=14, width=2, dash=False):
    d.rounded_rectangle((x1, y1, x2, y2), radius=radius, fill=fill, outline=outline, width=width)


def arrow(x1, y1, x2, y2, color=BLUE, width=3, head=14, dash=False):
    if dash:
        # 手绘虚线
        import math
        length = math.hypot(x2 - x1, y2 - y1)
        steps = max(2, int(length // 18))
        for i in range(steps):
            t0, t1 = i / steps, (i + 0.55) / steps
            d.line(
                (x1 + (x2 - x1) * t0, y1 + (y2 - y1) * t0,
                 x1 + (x2 - x1) * t1, y1 + (y2 - y1) * t1),
                fill=color, width=width)
    else:
        d.line((x1, y1, x2, y2), fill=color, width=width)
    import math
    ang = math.atan2(y2 - y1, x2 - x1)
    for sign in (-0.45, 0.45):
        d.line((x2, y2,
                x2 - head * math.cos(ang + sign),
                y2 - head * math.sin(ang + sign)), fill=color, width=width)


def chip(x, y, s, size=20, fg=INK, bg=GREY_BG, padx=12, pady=6):
    f = font(size)
    w = d.textlength(s, font=f)
    box(x, y, x + w + padx * 2, y + size + pady * 2, bg, bg, radius=8, width=0)
    text(x + padx, y + pady - 2, s, size, fg)
    return w + padx * 2


# ───────────────────────── 标题 ─────────────────────────
text(70, 54, "PCL_Plus 联机 · IPv6 点对点直连原理", 40, INK, True)
text(70, 112, "两端各自持公网 IPv6，用 UDP 打洞直连；不经中继、不经服务器。", 24, MUTED)

# ───────────────────────── ① 拓扑 ─────────────────────────
text(70, 178, "① 谁连谁", 28, BLUE, True)

box(70, 226, 520, 520, BLUE_BG, "#BBD6F0", 16)
text(96, 246, "房主", 26, INK, True)
text(96, 288, "Minecraft 服务端", 22, MUTED)
text(96, 318, "127.0.0.1:25565", 22, MUTED)
d.line((96, 358, 494, 358), fill="#C9DDF2", width=2)
text(96, 376, "隧道端口 UDP :xxxxx", 22, INK)
text(96, 410, "持有房间 Token（4 字节）", 22, MUTED)
text(96, 470, "开房后生成 PCL6- 邀请码", 21, BLUE)

box(1080, 226, 1530, 520, GREEN_BG, "#BFE3CE", 16)
text(1106, 246, "加入者", 26, INK, True)
text(1106, 288, "Minecraft 客户端", 22, MUTED)
text(1106, 318, "连接 127.0.0.1:ppppp", 22, MUTED)
d.line((1106, 358, 1504, 358), fill="#CDE7D8", width=2)
text(1106, 376, "隧道端口 UDP :yyyyy", 22, INK)
text(1106, 410, "本地代理（自动分配端口）", 22, MUTED)
text(1106, 470, "粘贴邀请码后生成 ACK6- 回执码", 21, GREEN)

# 公网
text(800, 300, "公网 IPv6", 24, MUTED, anchor="ma")
arrow(530, 372, 1070, 372, BLUE, 4)
arrow(1070, 340, 530, 340, BLUE, 4)
text(800, 396, "UDP 打洞 / 加密无关的可靠隧道", 20, MUTED, anchor="ma")
text(800, 430, "双方都主动向对方发包，NAT 才会为对方开口", 20, MUTED, anchor="ma")

# ───────────────────────── ② 分层 ─────────────────────────
text(70, 566, "② 一条 TCP 连接怎么穿过 UDP", 28, BLUE, True)

layers = [
    ("Minecraft 走的 TCP 连接", "游戏只认 TCP，所以两端各有一段本地转发", "#FFFFFF", LINE),
    ("本地代理 / 端口转发", "把 TCP 字节流交进隧道，再把隧道回来的字节写回 TCP", BLUE_BG, "#BBD6F0"),
    ("可靠 UDP 隧道", "自己做序号、ACK、重传、窗口——因为 UDP 会丢包、会乱序", AMBER_BG, "#E8CFA4"),
    ("UDP 数据报（Magic + Token）", "首 7 字节是协议魔数与房间 Token，对不上直接丢", GREY_BG, LINE),
]
y = 614
for title, desc, fill, outline in layers:
    box(70, y, 1530, y + 74, fill, outline, 12, 2)
    text(96, y + 14, title, 23, INK, True)
    text(96, y + 46, desc, 20, MUTED)
    y += 88

# ───────────────────────── ③ 时序 ─────────────────────────
text(70, 996, "③ 从开房到进游戏", 28, BLUE, True)

HX, GX = 470, 1130
TOP, BOT = 1050, 1570
text(HX, TOP - 34, "房主", 24, INK, True, anchor="ma")
text(GX, TOP - 34, "加入者", 24, INK, True, anchor="ma")
d.line((HX, TOP, HX, BOT), fill="#CBD5E1", width=2)
d.line((GX, TOP, GX, BOT), fill="#CBD5E1", width=2)

steps = [
    (1080, "dot-h", "① 开房：绑 UDP、生成房间 Token", INK),
    (1140, "right", "② 得到 PCL6- 邀请码，经 QQ 发给好友", GREEN),
    (1210, "left", "③ 粘贴邀请码，向房主地址连发打洞包", BLUE),
    (1270, "dot-h", "④ 房主收到带正确 Token 的包，直接接纳", INK),
    (1335, "dot-g", "⑤ 若打不通：加入者生成 ACK6- 回执码", INK),
    (1395, "left-d", "⑥ 把回执码发回房主", AMBER),
    (1455, "right-solid", "⑦ 房主读出地址主动发包，双向打通", BLUE),
    (1515, "dot-g", "⑧ 隧道建立，127.0.0.1:%s 可进" % "ppppp", INK),
]
for yy, kind, label, color in steps:
    if kind == "dot-h":
        d.ellipse((HX - 7, yy - 7, HX + 7, yy + 7), fill=BLUE)
        text(HX + 22, yy - 26, label, 21, INK)
    elif kind == "dot-g":
        d.ellipse((GX - 7, yy - 7, GX + 7, yy + 7), fill=GREEN)
        text(GX - 22, yy - 26, label, 21, INK, anchor="ra")
    elif kind == "right":
        arrow(HX + 14, yy, GX - 14, yy, color, 3, dash=True)
        text((HX + GX) / 2, yy - 32, label, 21, color, anchor="ma")
    elif kind == "left":
        arrow(GX - 14, yy, HX + 14, yy, color, 3)
        text((HX + GX) / 2, yy - 32, label, 21, color, anchor="ma")
    elif kind == "right-solid":
        arrow(HX + 14, yy, GX - 14, yy, color, 3)
        text((HX + GX) / 2, yy - 32, label, 21, color, anchor="ma")
    elif kind == "left-d":
        arrow(GX - 14, yy, HX + 14, yy, color, 3, dash=True)
        text((HX + GX) / 2, yy - 32, label, 21, color, anchor="ma")

text(70, 1596, "⑧ 之后：加入者游戏里填 127.0.0.1:ppppp（卡片上会自动显示这个地址）", 21, MUTED)

# ───────────────────────── ④ 邀请码 ─────────────────────────
text(70, 1652, "④ 邀请码里装了什么", 28, BLUE, True)

fields = [
    ("类型 1 B", 110), ("IPv6 地址 16 B", 240), ("端口 2 B", 160), ("Token 4 B", 160),
    ("CRC16 2 B", 160),
]
x = 70
for label, w in fields:
    box(x, 1700, x + w, 1762, GREY_BG, LINE, 10, 2)
    text(x + w / 2, 1716, label, 19, INK, anchor="ma")
    x += w + 10
text(70, 1780, "共 25 字节，Base58 编码后加前缀：房主 PCL6- / 加入者 ACK6-", 21, MUTED)
text(70, 1812, "不含密码、不含昵称——它只是“我在这个地址等”的明信片，谁拿到都能来敲门。", 21, MUTED)

# ───────────────────────── ⑤ 三种结局 ─────────────────────────
text(70, 1868, "⑤ 三种结局", 28, BLUE, True)

cards = [
    ("直连成功", GREEN, GREEN_BG, "#BFE3CE",
     "房主没被防火墙挡，好友的包直接打进来，\n第 4 步就通了，回执码根本用不上。"),
    ("靠回执码打通", AMBER, AMBER_BG, "#E8CFA4",
     "房主一侧的 NAT 丢了对方的第一包，\n好友把 ACK6- 码发回，房主主动发一次即可。"),
    ("打不通", RED, RED_BG, "#E6C0BA",
     "任一端没有公网 IPv6（纯 IPv4 / 对称 NAT /\nCGNAT / 校园网），或端口被彻底封死。"),
]
x = 70
for title, color, bg, outline, desc in cards:
    box(x, 1916, x + 470, 2120, bg, outline, 12, 2)
    text(x + 22, 1934, title, 23, color, True)
    text(x + 22, 1978, desc, 19, INK)
    x += 490

out = sys.argv[1] if len(sys.argv) > 1 else "link-diagram.png"
img.save(out)
print("saved", out, img.size)
