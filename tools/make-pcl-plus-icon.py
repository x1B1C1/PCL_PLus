# -*- coding: utf-8 -*-
"""Generate the PCL_Plus brand icon and banner."""
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "assets"
ASSETS.mkdir(exist_ok=True)
FONT_BOLD = r"C:\Windows\Fonts\segoeuib.ttf"
FONT_SEMI = r"C:\Windows\Fonts\seguisb.ttf"


def font(path, size):
    return ImageFont.truetype(path, size)


def gradient(size, top_left, bottom_right):
    w, h = size
    y, x = np.mgrid[0:h, 0:w]
    t = (x / max(w - 1, 1) * 0.55 + y / max(h - 1, 1) * 0.45).clip(0, 1)
    a = np.array(top_left, dtype=float)
    b = np.array(bottom_right, dtype=float)
    rgb = (a[None, None, :] * (1 - t[..., None]) + b[None, None, :] * t[..., None]).astype(np.uint8)
    return Image.fromarray(rgb, "RGB").convert("RGBA")


def make_icon(size=1024):
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    pad = int(size * 0.0625)
    radius = int(size * 0.205)
    box = (pad, pad, size - pad, size - pad)

    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(box, radius=radius, fill=255)

    bg = gradient((size, size), (36, 106, 255), (118, 70, 255))
    canvas.paste(bg, (0, 0), mask)

    # soft top highlight and inner edge
    overlay = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    d.rounded_rectangle(
        (pad + 2, pad + 2, size - pad - 2, size - pad - 2),
        radius=radius - 2,
        outline=(255, 255, 255, 90),
        width=max(2, size // 220),
    )
    d.ellipse(
        (int(size * -0.18), int(size * -0.28), int(size * 0.92), int(size * 0.62)),
        fill=(255, 255, 255, 28),
    )
    overlay = overlay.filter(ImageFilter.GaussianBlur(size // 90))
    canvas = Image.alpha_composite(canvas, Image.composite(overlay, Image.new("RGBA", (size, size)), mask))

    # P + monogram, optically centred
    p_font = font(FONT_BOLD, int(size * 0.52))
    plus_font = font(FONT_BOLD, int(size * 0.27))
    d = ImageDraw.Draw(canvas)
    p_box = d.textbbox((0, 0), "P", font=p_font)
    plus_box = d.textbbox((0, 0), "+", font=plus_font)
    gap = int(size * 0.015)
    total = (p_box[2] - p_box[0]) + gap + (plus_box[2] - plus_box[0])
    left = (size - total) // 2
    baseline = int(size * 0.68)
    p_x = left - p_box[0]
    p_y = baseline - p_box[3]
    plus_x = left + (p_box[2] - p_box[0]) + gap - plus_box[0]
    plus_y = int(size * 0.31) - plus_box[1]

    # shadow
    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    sd = ImageDraw.Draw(shadow)
    sd.text((p_x + 8, p_y + 14), "P", font=p_font, fill=(0, 0, 0, 90))
    sd.text((plus_x + 8, plus_y + 14), "+", font=plus_font, fill=(0, 0, 0, 90))
    shadow = shadow.filter(ImageFilter.GaussianBlur(size // 100))
    canvas = Image.alpha_composite(canvas, shadow)

    d = ImageDraw.Draw(canvas)
    d.text((p_x, p_y), "P", font=p_font, fill=(255, 255, 255, 255))
    d.text((plus_x, plus_y), "+", font=plus_font, fill=(126, 245, 255, 255))

    # small baseline dots, subtle
    dot_r = int(size * 0.008)
    for i, alpha in enumerate((150, 220, 255)):
        x = int(size * 0.5) + (i - 1) * int(size * 0.035)
        y = int(size * 0.855)
        d.ellipse((x - dot_r, y - dot_r, x + dot_r, y + dot_r), fill=(255, 255, 255, alpha))
    return canvas


def make_banner(icon):
    w, h = 1600, 900
    canvas = gradient((w, h), (9, 20, 42), (27, 50, 98))
    d = ImageDraw.Draw(canvas, "RGBA")
    # soft auras
    for cx, cy, r, a in ((250, 220, 360, 34), (1320, 720, 420, 30), (1060, 140, 280, 20)):
        layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
        ImageDraw.Draw(layer).ellipse((cx - r, cy - r, cx + r, cy + r), fill=(80, 150, 255, a))
        canvas = Image.alpha_composite(canvas, layer.filter(ImageFilter.GaussianBlur(90)))

    icon_size = 440
    icon = icon.resize((icon_size, icon_size), Image.LANCZOS)
    canvas.alpha_composite(icon, (120, (h - icon_size) // 2))

    d = ImageDraw.Draw(canvas, "RGBA")
    title_font = font(FONT_BOLD, 118)
    sub_font = font(FONT_SEMI, 34)
    tag_font = font(FONT_SEMI, 23)

    x = 640
    y = 310
    d.text((x, y), "PCL_Plus", font=title_font, fill=(255, 255, 255, 255))
    d.text((x + 3, y + 150), "Minecraft Launcher", font=sub_font, fill=(190, 215, 255, 230))
    d.rounded_rectangle((x + 3, y + 215, x + 100, y + 221), radius=3, fill=(126, 245, 255, 245))
    d.text((x + 3, y + 250), "PCL  ·  PLUS  ·  EDITION", font=tag_font, fill=(126, 245, 255, 210))
    return canvas.convert("RGB")


def main():
    icon = make_icon(1024)
    icon.save(ASSETS / "PCL_Plus_icon.png")
    icon.resize((512, 512), Image.LANCZOS).save(ASSETS / "PCL_Plus_icon_512.png")
    icon.save(
        ASSETS / "PCL_Plus.ico",
        format="ICO",
        sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    banner = make_banner(icon)
    banner.save(ASSETS / "PCL_Plus_banner.png")

    target = ROOT / "Plain Craft Launcher 2" / "Images" / "icon.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.exists():
        target.replace(target.with_suffix(".ico.before-pcl-plus"))
    Image.open(ASSETS / "PCL_Plus.ico").save(target, format="ICO")
    print("icon", target, target.stat().st_size)
    print("banner", ASSETS / "PCL_Plus_banner.png")


if __name__ == "__main__":
    main()
