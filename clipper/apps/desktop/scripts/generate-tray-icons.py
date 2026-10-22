#!/usr/bin/env python3
"""Regenerate tray/app icons from the repo GamerPlug logo (assets/logo.png)."""

from pathlib import Path
from PIL import Image, ImageFilter

ROOT = Path(__file__).resolve().parents[1]  # apps/desktop
REPO = Path(__file__).resolve().parents[4]  # git root
SRC = REPO / "assets" / "logo.png"
OUT = ROOT / "icons"


def glyph_mask(src: Image.Image) -> Image.Image:
    w, h = src.size
    pix = src.load()
    mask = Image.new("L", (w, h), 0)
    mp = mask.load()
    for y in range(h):
        for x in range(w):
            r, g, b = pix[x, y]
            if r > 180 and g < r * 0.70 and b < r * 0.70:
                a = 0
            else:
                a = int(max(0, min(255, (g + b) / 2)))
            mp[x, y] = a
    bbox = mask.getbbox()
    pad = 12
    left, top, right, bottom = bbox
    left, top = max(0, left - pad), max(0, top - pad)
    right, bottom = min(w, right + pad), min(h, bottom + pad)
    cropped = mask.crop((left, top, right, bottom))
    cw, ch = cropped.size
    side = max(cw, ch)
    square = Image.new("L", (side, side), 0)
    square.paste(cropped, ((side - cw) // 2, (side - ch) // 2))
    return square


def to_template(mask_l: Image.Image, size: int) -> Image.Image:
    m = mask_l.resize((size, size), Image.Resampling.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(Image.new("RGBA", (size, size), (0, 0, 0, 255)), (0, 0), m)
    return out


def main() -> None:
    src = Image.open(SRC).convert("RGB")
    square = glyph_mask(src)
    thick = square.filter(ImageFilter.MaxFilter(5))
    OUT.mkdir(parents=True, exist_ok=True)
    to_template(thick, 16).save(OUT / "trayTemplate.png")
    to_template(thick, 32).save(OUT / "trayTemplate@2x.png")
    to_template(square, 32).save(OUT / "trayTemplate32.png")
    to_template(square, 64).save(OUT / "trayTemplate32@2x.png")
    color = src.convert("RGBA")
    color.save(OUT / "icon.png")
    color.save(
        OUT / "icon.ico",
        format="ICO",
        sizes=[(16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
    )
    print("wrote", OUT)


if __name__ == "__main__":
    main()
