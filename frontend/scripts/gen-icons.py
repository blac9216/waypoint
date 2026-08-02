#!/usr/bin/env python3
"""Regenerate the PWA/favicon PNGs under public/icons/ from the design tokens.

This is a build-prep script, not part of the Vite build (there is no Python
runtime dependency at build time) — the generated PNGs are committed like any
other static asset. Re-run it by hand (`python3 scripts/gen-icons.py`, needs
`pillow`: `pip install pillow`) if the brand mark or the `--acc`/`--bg` design
tokens (docs/ui/prototype/README.md) ever change.

The icon reproduces the top-bar brand mark exactly: a 45deg-rotated square
outline in `--acc` with a solid `--acc` square inset inside it. No network
access, no external fonts/icons — see CLAUDE.md / ADR-0007 zero-external-assets.
"""

import math
import os

from PIL import Image, ImageDraw

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(SCRIPT_DIR, "..", "public", "icons")


def oklch_to_srgb(lightness: float, chroma: float, hue_deg: float) -> tuple[int, int, int]:
    """Minimal OKLCH -> sRGB conversion, sufficient for a flat brand icon."""
    hue = math.radians(hue_deg)
    a = chroma * math.cos(hue)
    b = chroma * math.sin(hue)
    l_ = lightness + 0.3963377774 * a + 0.2158037573 * b
    m_ = lightness - 0.1055613458 * a - 0.0638541728 * b
    s_ = lightness - 0.0894841775 * a - 1.2914855480 * b
    l, m, s = l_**3, m_**3, s_**3
    r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s
    g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s
    bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s

    def to_srgb_channel(c: float) -> int:
        c = max(0.0, min(1.0, c))
        c = 12.92 * c if c <= 0.0031308 else 1.055 * (c ** (1 / 2.4)) - 0.055
        return max(0, min(255, round(c * 255)))

    return (to_srgb_channel(r), to_srgb_channel(g), to_srgb_channel(bl))


# Dark-theme tokens (docs/ui/prototype/README.md "Design Tokens" table).
ACC = oklch_to_srgb(0.70, 0.12, 235)  # --acc
BG = oklch_to_srgb(0.165, 0.008, 255)  # --bg


def rotated_square(cx: float, cy: float, half: float, degrees: float = 45) -> list[tuple[float, float]]:
    points = [(-half, -half), (half, -half), (half, half), (-half, half)]
    rad = math.radians(degrees)
    return [
        (cx + x * math.cos(rad) - y * math.sin(rad), cy + x * math.sin(rad) + y * math.cos(rad))
        for x, y in points
    ]


def make_icon(size: int, path: str, maskable: bool = False) -> None:
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    if maskable:
        # Maskable icons need a full-bleed safe-zone background (Android adaptive icons).
        draw.rectangle([0, 0, size, size], fill=BG + (255,))
    cx = cy = size / 2
    outer_half = size * 0.34
    stroke = max(2, round(size * 0.035))
    draw.polygon(rotated_square(cx, cy, outer_half), outline=ACC + (255,), width=stroke)
    draw.polygon(rotated_square(cx, cy, outer_half * 0.55), fill=ACC + (255,))
    img.save(path)


if __name__ == "__main__":
    os.makedirs(OUT_DIR, exist_ok=True)
    make_icon(32, os.path.join(OUT_DIR, "favicon-32.png"))
    make_icon(48, os.path.join(OUT_DIR, "favicon-48.png"))
    make_icon(192, os.path.join(OUT_DIR, "icon-192.png"))
    make_icon(512, os.path.join(OUT_DIR, "icon-512.png"))
    make_icon(192, os.path.join(OUT_DIR, "icon-maskable-192.png"), maskable=True)
    make_icon(512, os.path.join(OUT_DIR, "icon-maskable-512.png"), maskable=True)
    print(f"Wrote 6 icons to {OUT_DIR} (accent {ACC}, bg {BG})")
