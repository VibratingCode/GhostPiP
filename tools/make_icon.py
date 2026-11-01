"""Draws assets/GhostPiP.ico: a luminous ghost on a dark rounded tile. Run: python tools/make_icon.py"""
import math
import os

from PIL import Image, ImageDraw, ImageFilter

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "assets", "GhostPiP.ico")
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def ghost_mask(s, x0, y0, w, h):
    """Ghost silhouette: dome head, straight sides, three scallops along the hem."""
    mask = Image.new("L", (s, s), 0)
    d = ImageDraw.Draw(mask)
    r = w / 2
    d.ellipse((x0, y0, x0 + w, y0 + w), fill=255)
    hem = y0 + h
    d.rectangle((x0, y0 + r, x0 + w, hem - w / 6), fill=255)
    bump = w / 3
    for i in range(3):
        d.ellipse((x0 + i * bump, hem - bump, x0 + (i + 1) * bump, hem), fill=255)
    return mask


def gradient(s, a, b):
    im = Image.new("RGB", (s, s))
    px = im.load()
    for y in range(s):
        for x in range(s):
            t = (x + y) / (2 * s - 2)
            px[x, y] = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))
    return im


def render(size):
    k = 8 if size <= 64 else 4
    s = size * k
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    small = size < 32
    if not small:
        tile = Image.new("L", (s, s), 0)
        ImageDraw.Draw(tile).rounded_rectangle((0, 0, s - 1, s - 1), radius=int(s * 0.24), fill=255)
        im.paste(gradient(s, (38, 30, 58), (14, 12, 22)), (0, 0), tile)
    # At tray sizes the tile only costs legibility, so the ghost fills the square.
    w, h = (s * 0.78, s * 0.92) if small else (s * 0.56, s * 0.66)
    x0, y0 = (s - w) / 2, (s - h) / 2
    mask = ghost_mask(s, x0, y0, w, h)
    if not small:
        glow = gradient(s, (126, 240, 255), (182, 156, 255)).convert("RGBA")
        glow.putalpha(mask.filter(ImageFilter.GaussianBlur(s * 0.05)).point(lambda v: int(v * 0.55)))
        im.alpha_composite(glow)
    im.paste(gradient(s, (126, 240, 255), (182, 156, 255)), (0, 0), mask)
    d = ImageDraw.Draw(im)
    ew, eh = w * 0.15, w * 0.21
    ey = y0 + w * 0.36
    for cx in (x0 + w * 0.34, x0 + w * 0.66):
        d.ellipse((cx - ew / 2, ey, cx + ew / 2, ey + eh), fill=(18, 15, 28, 255))
    return im.resize((size, size), Image.Resampling.LANCZOS)


if __name__ == "__main__":
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    images = [render(n) for n in SIZES]
    images[-1].save(OUT, format="ICO", append_images=images[:-1], sizes=[(n, n) for n in SIZES])
    print("wrote", os.path.normpath(OUT))
