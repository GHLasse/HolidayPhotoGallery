"""Converts the installer artwork (installer/art/png) into the .bmp layers Setup draws with.

Setup stacks the layers with the Windows GDI (BitBlt, StretchBlt, AlphaBlend), which needs plain
bitmaps: opaque layers become 24-bit .bmp files, layers with transparency 32-bit .bmp files with
premultiplied alpha, as AlphaBlend expects.

Usage: python installer/art/png2bmp.py [output folder]   (default: installer/art/bmp)
Needs Pillow (pip install pillow).
"""

import struct
import sys
from pathlib import Path

from PIL import Image

ART = Path(__file__).resolve().parent


def write_bmp(path: Path, image: Image.Image) -> None:
    width, height = image.size
    alpha = image.mode == "RGBA"
    bpp = 32 if alpha else 24
    stride = (width * bpp // 8 + 3) & ~3
    rows = []
    pixels = image.load()
    for y in range(height - 1, -1, -1):  # bottom-up
        row = bytearray()
        for x in range(width):
            if alpha:
                r, g, b, a = pixels[x, y]
                row += bytes((b * a // 255, g * a // 255, r * a // 255, a))
            else:
                r, g, b = pixels[x, y]
                row += bytes((b, g, r))
        row += b"\0" * (stride - len(row))
        rows.append(bytes(row))
    data = b"".join(rows)
    header = struct.pack("<IiiHHIIiiII", 40, width, height, 1, bpp, 0, len(data), 2835, 2835, 0, 0)
    file_header = struct.pack("<2sIHHI", b"BM", 14 + len(header) + len(data), 0, 0, 14 + len(header))
    path.write_bytes(file_header + header + data)


def main() -> None:
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else ART / "bmp"
    out.mkdir(parents=True, exist_ok=True)
    count = 0
    for png in sorted((ART / "png").glob("*.png")):
        image = Image.open(png).convert("RGBA")
        if image.getextrema()[3][0] == 255:
            image = image.convert("RGB")
        write_bmp(out / (png.stem + ".bmp"), image)
        count += 1
    print(f"{count} layers written to {out}")


if __name__ == "__main__":
    main()
