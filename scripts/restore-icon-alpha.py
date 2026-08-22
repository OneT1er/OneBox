#!/usr/bin/env python3
"""Restore the transparent edge of OneBox's authored tray/application icon.

The original artwork was rasterized over white, so the RGB channels contain
white-background compositing rather than a useful alpha channel.  This tool
recovers alpha from the red/green channels (the purple foreground has a much
larger contrast there than in blue), normalizes visible pixels to the brand
color, and emits a multi-resolution ICO without a white matte.

Requires Pillow (``py -m pip install pillow``).  The command is intentionally
deterministic and safe to rerun: once an alpha channel exists it is preserved.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


BRAND = (0x8E, 0x8C, 0xD8)
ICO_SIZES = tuple((size, size) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256))


def restored(source: Image.Image) -> Image.Image:
    rgba = source.convert("RGBA")
    pixels = list(rgba.getdata())
    has_alpha = any(pixel[3] != 255 for pixel in pixels)
    output = []
    for red, green, blue, alpha in pixels:
        if has_alpha:
            recovered = alpha
        else:
            # White compositing gives c = 255 - alpha * (255 - fg).
            # Red/green provide the stable estimate; blue's 39-level range
            # amplifies rounding and artwork-color noise at the edge.
            red_alpha = (255 - red) / (255 - BRAND[0])
            green_alpha = (255 - green) / (255 - BRAND[1])
            recovered = round(255 * max(0.0, min(1.0, (red_alpha + green_alpha) / 2)))
        if recovered == 0:
            output.append((0, 0, 0, 0))
        else:
            output.append((*BRAND, recovered))
    result = Image.new("RGBA", rgba.size)
    result.putdata(output)
    return result


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, default=Path("src/app.png"))
    parser.add_argument("--png", type=Path, default=Path("src/app.png"))
    parser.add_argument("--ico", type=Path, default=Path("src/app.ico"))
    args = parser.parse_args()

    with Image.open(args.input) as source:
        image = restored(source)
    if image.size != (256, 256):
        raise SystemExit(f"expected a 256x256 source icon, got {image.size}")

    args.png.parent.mkdir(parents=True, exist_ok=True)
    args.ico.parent.mkdir(parents=True, exist_ok=True)
    image.save(args.png, format="PNG", optimize=False)

    # Passing sizes makes Pillow write all requested directory entries. Each
    # entry is generated from the transparent RGBA source, so no white matte
    # is introduced during downsampling.
    image.save(args.ico, format="ICO", sizes=ICO_SIZES)


if __name__ == "__main__":
    main()
