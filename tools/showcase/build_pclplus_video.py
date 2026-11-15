# -*- coding: utf-8 -*-
"""Build the 32 s PCL_Plus showcase from real local recordings."""
from __future__ import annotations

import math
import os
import subprocess
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[2]  # repo root (tools/showcase -> root)
SHOW = Path(__file__).resolve().parent
BUILD = SHOW / "build32"
BUILD.mkdir(exist_ok=True)
FFMPEG = r"C:\Users\PC\.lunarclient\launcher-cache\Badlion Client\ffmpeg.exe"
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"
FONT_SEMI = r"C:\Windows\Fonts\msyh.ttc"
FONT_LATIN = r"C:\Windows\Fonts\segoeuib.ttf"
W, H = 1920, 1080
FPS = 30
PINK = (255, 111, 168)
PINK_LIGHT = (255, 176, 207)


def font(path, size, index=0):
    try:
        return ImageFont.truetype(path, size, index=index)
    except Exception:
        return ImageFont.truetype(FONT_LATIN, size)


def gradient(size, a, b):
    w, h = size
    y, x = np.mgrid[0:h, 0:w]
    t = (x / max(w - 1, 1) * 0.45 + y / max(h - 1, 1) * 0.55).clip(0, 1)
    aa = np.array(a, float); bb = np.array(b, float)
    return Image.fromarray((aa[None, None] * (1 - t[..., None]) + bb[None, None] * t[..., None]).astype("uint8"), "RGB")


def titlebar():
    im = Image.new("RGBA", (W, 48), (0, 0, 0, 0))
    d = ImageDraw.Draw(im, "RGBA")
    d.rectangle((0, 0, W, 48), fill=(35, 14, 25, 235))
    d.rectangle((0, 46, W, 48), fill=(*PINK, 120))
    icon = Image.open(ROOT / "assets" / "PCL_Plus_icon.png").convert("RGBA").resize((28, 28), Image.LANCZOS)
    im.alpha_composite(icon, (18, 10))
    d.text((54, 10), "PCL_Plus", font=font(FONT_LATIN, 21), fill=(255, 255, 255, 255))
    labels = ["启动", "下载", "基岩版", "设置", "工具"]
    x = 1130
    for label in labels:
        tw = d.textlength(label, font=font(FONT_BOLD, 17))
        d.rounded_rectangle((x, 9, x + tw + 34, 39), radius=15, fill=(255, 255, 255, 22), outline=(255, 255, 255, 48))
        d.text((x + 17, 14), label, font=font(FONT_BOLD, 17), fill=(255, 255, 255, 235))
        x += int(tw + 52)
    d.line((W - 112, 24, W - 112, 24), fill=(255, 255, 255, 0))
    for cx, glyph in ((W - 92, "—"), (W - 54, "×")):
        d.ellipse((cx - 15, 9, cx + 15, 39), outline=(255, 255, 255, 55), width=1)
        d.text((cx - 7, 9), glyph, font=font(FONT_LATIN, 18), fill=(255, 255, 255, 230))
    im.save(BUILD / "titlebar.png")


def caption(text: str, name: str, y=932):
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(im, "RGBA")
    f = font(FONT_SEMI, 31)
    tw = d.textlength(text, font=f)
    x = 82
    d.rounded_rectangle((x - 22, y - 14, x + tw + 22, y + 48), radius=16, fill=(20, 8, 16, 190), outline=(*PINK, 110), width=1)
    d.rectangle((x - 22, y - 14, x - 16, y + 48), fill=(*PINK, 240))
    d.text((x, y - 4), text, font=f, fill=(255, 255, 255, 245))
    im.save(BUILD / f"caption-{name}.png")


def pixel_icon(size):
    icon = Image.open(ROOT / "assets" / "PCL_Plus_icon.png").convert("RGBA")
    small = icon.resize((64, 64), Image.LANCZOS)
    return small.resize((size, size), Image.NEAREST)


def intro(name="intro", subtitle="独立 MC 启动器", footer="PIXEL  ·  SIMPLE  ·  YOURS"):
    im = gradient((W, H), (25, 9, 22), (108, 26, 63)).convert("RGBA")
    d = ImageDraw.Draw(im, "RGBA")
    # soft pink aura
    aura = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(aura).ellipse((220, -180, 1000, 620), fill=(*PINK, 50))
    im = Image.alpha_composite(im, aura.filter(ImageFilter.GaussianBlur(120)))
    icon = pixel_icon(380)
    # pixel grid glow
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(glow).rounded_rectangle((265, 300, 675, 710), radius=26, fill=(*PINK, 64))
    im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(40)))
    im.alpha_composite(icon, (280, 315))
    d = ImageDraw.Draw(im, "RGBA")
    d.text((760, 375), "PCL_Plus", font=font(FONT_LATIN, 128), fill=(255, 255, 255, 255))
    d.text((765, 530), subtitle, font=font(FONT_SEMI, 40), fill=(*PINK_LIGHT, 255))
    d.rounded_rectangle((765, 602, 938, 607), radius=2, fill=(*PINK, 255))
    d.text((765, 635), footer, font=font(FONT_LATIN, 23), fill=(255, 255, 255, 150))
    im.convert("RGB").save(BUILD / f"{name}.png")


def main_frame():
    src = Image.open(SHOW / "shot-download3.png").convert("RGBA")
    # Give the blue live capture a red-pink cast without destroying text contrast.
    pink = Image.new("RGBA", src.size, (*PINK, 44))
    src = Image.blend(src, pink, 0.16)
    # Redact account block and the bottom launch/联机 row.
    for box in ((100, 180, 270, 390), (0, 580, 610, 688)):
        crop = src.crop(box).filter(ImageFilter.GaussianBlur(26))
        src.paste(crop, box)
    # Place the real window on a pink stage.
    stage = gradient((W, H), (25, 9, 22), (88, 24, 55)).convert("RGBA")
    d = ImageDraw.Draw(stage, "RGBA")
    scale = 1.55
    shot = src.resize((int(src.width * scale), int(src.height * scale)), Image.LANCZOS)
    x = (W - shot.width) // 2
    y = 64
    d.rounded_rectangle((x - 8, y - 8, x + shot.width + 8, y + shot.height + 8), radius=15, fill=(*PINK, 70))
    stage.alpha_composite(shot, (x, y))
    stage.convert("RGB").save(BUILD / "main-frame.png")


def scene_clip(name, src, start, dur, caption_name, with_title=True, wash=True):
    cap = BUILD / f"caption-{caption_name}.png"
    inputs = ["-ss", str(start), "-t", str(dur), "-i", str(src)]
    filters = ["[0:v]crop=1920:996:0:84,scale=1920:1032:flags=lanczos,pad=1920:1080:0:48:color=#1a0c14,fps=30[base]"]
    n = 1
    if wash:
        inputs += ["-loop", "1", "-i", str(BUILD / "pink-wash.png")]
        filters.append(f"[{n}:v]format=rgba[wash]")
        n += 1
    if with_title:
        inputs += ["-loop", "1", "-i", str(BUILD / "titlebar.png")]
        filters.append(f"[{n}:v]format=rgba[title]")
        n += 1
    inputs += ["-loop", "1", "-i", str(cap)]
    filters.append(f"[{n}:v]format=rgba[cap]")
    cur = "base"
    if wash:
        filters.append(f"[{cur}][wash]overlay=0:0:format=auto[w]")
        cur = "w"
    if with_title:
        filters.append(f"[{cur}][title]overlay=0:0:format=auto[t]")
        cur = "t"
    filters.append(f"[{cur}][cap]overlay=0:0:format=auto,format=yuv420p[v]")
    cmd = [FFMPEG, "-y", *inputs, "-filter_complex", ";".join(filters), "-map", "[v]", "-an",
           "-r", str(FPS), "-t", str(dur), "-c:v", "libx264", "-preset", "veryfast", "-crf", "19",
           str(BUILD / f"{name}.mp4")]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=(BUILD / "ffmpeg.log").open("ab"))


def image_clip(name, image, dur, caption_name=None):
    inputs = ["-y", "-loop", "1", "-framerate", str(FPS), "-i", str(image)]
    if caption_name:
        inputs += ["-loop", "1", "-framerate", str(FPS), "-i", str(BUILD / f"caption-{caption_name}.png")]
        cmd = [FFMPEG, *inputs, "-t", str(dur), "-filter_complex",
               "[0:v][1:v]overlay=0:0:format=auto,format=yuv420p[v]", "-map", "[v]",
               "-r", str(FPS), "-c:v", "libx264", "-preset", "veryfast", "-crf", "19",
               str(BUILD / f"{name}.mp4")]
    else:
        cmd = [FFMPEG, *inputs, "-t", str(dur), "-vf", "format=yuv420p", "-r", str(FPS),
               "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", str(BUILD / f"{name}.mp4")]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=(BUILD / "ffmpeg.log").open("ab"))


def download_click_frame():
    base = Image.open(BUILD / "main-frame.png").convert("RGBA")
    d = ImageDraw.Draw(base, "RGBA")
    # Download pill in the live capture (scaled into the stage).
    x, y = 842, 139
    d.ellipse((x - 42, y - 42, x + 42, y + 42), outline=(255, 255, 255, 235), width=3)
    d.ellipse((x - 28, y - 28, x + 28, y + 28), outline=(*PINK, 220), width=2)
    pts = [(x, y), (x, y + 28), (x + 8, y + 22), (x + 16, y + 39), (x + 21, y + 35),
           (x + 13, y + 18), (x + 25, y + 18)]
    d.polygon(pts, fill=(255, 255, 255, 250), outline=(20, 10, 20, 230))
    base.convert("RGB").save(BUILD / "download-click.png")


def theme_scene():
    # Red -> green -> blue -> red; all real settings footage.
    parts = [
        ("theme-r1", SHOW / "UI+主题展示.mp4", 44.0, 1.45),
        ("theme-g", SHOW / "UI+主题展示.mp4", 48.0, 1.45),
        ("theme-b", SHOW / "UI+主题展示.mp4", 52.0, 1.45),
        ("theme-r2", SHOW / "UI+主题展示.mp4", 44.0, 1.0),
    ]
    for n, src, st, d in parts:
        scene_clip(n, src, st, d, "theme")
    concat = BUILD / "theme-parts.txt"
    concat.write_text("\n".join(f"file '{BUILD / (n + '.mp4')}'" for n, _, _, _ in parts), encoding="utf-8")
    out = BUILD / "themes.mp4"
    subprocess.run([FFMPEG, "-y", "-f", "concat", "-safe", "0", "-i", str(concat), "-c", "copy", str(out)],
                   check=True, stdout=subprocess.DEVNULL, stderr=(BUILD / "ffmpeg.log").open("ab"))


def theme_still(name, src_time, caption_name, dur=4.5):
    """Freeze one real full settings frame and give it the PCL_Plus chrome."""
    raw = BUILD / f"{name}-raw.png"
    subprocess.run([FFMPEG, "-y", "-ss", str(src_time), "-i", str(SHOW / "UI+主题展示.mp4"),
                    "-frames:v", "1", str(raw)],
                   check=True, stdout=subprocess.DEVNULL, stderr=(BUILD / "ffmpeg.log").open("ab"))
    shot = Image.open(raw).convert("RGBA").crop((0, 84, 1920, 1080)).resize((1920, 1032), Image.LANCZOS)
    stage = Image.new("RGBA", (W, H), (12, 7, 12, 255))
    stage.paste(shot, (0, 48))
    stage.alpha_composite(Image.open(BUILD / "titlebar.png").convert("RGBA"), (0, 0))
    stage.alpha_composite(Image.open(BUILD / "pink-wash.png").convert("RGBA"), (0, 0))
    out = BUILD / f"{name}.png"
    stage.convert("RGB").save(out)
    image_clip(name, out, dur, caption_name)


def xfade():
    names = ["intro", "main", "download-click", "download", "instances",
             "theme-gold", "theme-green", "theme-blue", "theme-return", "launch", "about", "outro"]
    durs = [2.3, 4.0, 1.8, 8.2, 8.0, 4.5, 4.5, 4.5, 4.5, 8.0, 6.0, 2.5]
    inputs = []
    for n in names:
        inputs += ["-i", str(BUILD / f"{n}.mp4")]
    filters = []
    cur = "0:v"
    offset = 0.0
    for i in range(1, len(names)):
        offset += durs[i - 1] - 0.3
        nxt = f"v{i}"
        filters.append(f"[{cur}][{i}:v]xfade=transition=fade:duration=0.3:offset={offset:.3f}[{nxt}]")
        cur = nxt
    filters.append(f"[{cur}]format=yuv420p[v]")
    cmd = [FFMPEG, "-y", *inputs, "-filter_complex", ";".join(filters), "-map", "[v]", "-an",
           "-r", str(FPS), "-t", "56", "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
           str(BUILD / "video-only.mp4")]
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=(BUILD / "ffmpeg.log").open("ab"))


def pink_wash():
    im = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    y, x = np.mgrid[0:H, 0:W]
    # strongest at the edges, transparent in the middle
    edge = np.clip((np.abs(x - W / 2) / (W / 2)) ** 2 * 0.10 + (np.abs(y - H / 2) / (H / 2)) ** 2 * 0.05, 0, 1)
    arr = np.zeros((H, W, 4), np.uint8)
    arr[..., 0] = 255
    arr[..., 1] = 82
    arr[..., 2] = 145
    arr[..., 3] = np.clip(edge * 38 + 24, 0, 62).astype(np.uint8)
    Image.fromarray(arr, "RGBA").save(BUILD / "pink-wash.png")


def main():
    pink_wash()
    titlebar()
    for key, text in {
        "intro": "PCL_Plus 独立 MC 启动器",
        "main": "性能状态 · CPU/内存实时监控 · 实例与版本入口",
        "download-click": "点击「下载」→ 打开版本下载页",
        "download": "选择版本 → 任务队列 → 进度/速度 → 断点续传与校验",
        "instances": "模组 / 光影 / 资源包 / 世界 · 实例隔离管理",
        "theme-gold": "完整展示 1/3 · 暖金主题 · 完整设置页",
        "theme-green": "完整展示 2/3 · 森绿主题 · 完整设置页",
        "theme-blue": "完整展示 3/3 · 天蓝主题 · 完整设置页",
        "theme-return": "回到红粉主界面 · 主题配置本地保存",
        "launch": "Java 自动匹配 · 启动参数 · 日志输出",
        "about": "PCL_Plus 社区共建 · 更新 / 反馈 / 日志",
        "outro": "更多功能 等你探索",
    }.items():
        caption(text, key)
    intro("intro")
    intro("outro", "更多功能 等你探索", "MORE TO EXPLORE")
    main_frame()
    download_click_frame()
    image_clip("intro", BUILD / "intro.png", 2.3)
    image_clip("main", BUILD / "main-frame.png", 4.0, "main")
    image_clip("download-click", BUILD / "download-click.png", 1.8, "download-click")
    scene_clip("download", SHOW / "下载MC.mp4", 4.0, 8.2, "download")
    scene_clip("instances", SHOW / "UI+主题展示.mp4", 12.0, 8.0, "instances")
    theme_still("theme-gold", 44.0, "theme-gold")
    theme_still("theme-green", 48.0, "theme-green")
    theme_still("theme-blue", 52.0, "theme-blue")
    image_clip("theme-return", BUILD / "main-frame.png", 4.5, "theme-return")
    scene_clip("launch", SHOW / "UI+主题展示.mp4", 32.0, 8.0, "launch")
    theme_still("about", 60.0, "about", 6.0)
    image_clip("outro", BUILD / "outro.png", 2.5)
    xfade()
    print("video", BUILD / "video-only.mp4")


if __name__ == "__main__":
    main()
