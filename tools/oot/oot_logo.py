"""Titel-Logo "The Legend of Valheim – Ocarina of Time" (eigenes Design im Zelda-Geist, Valheim-Elemente):
Rundschild aus Holzplanken mit Eisenrand, goldener Valknut (drei verschlungene Dreiecke) auf dem Buckel, Schwert dahinter,
Glut-Schein; Schrift in Cinzel (OFL). -> RGBA-PNG zum Ueberblenden.

  python oot_logo.py out_dir
"""
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
F900 = os.path.join(HERE, "assets", "cinzel-latin-900-normal.woff")
F700 = os.path.join(HERE, "assets", "cinzel-latin-700-normal.woff")


def _grad_text(size, text, font, top, bot, outline, ow, bevel=True):
    """Schrift mit Farbverlauf, Kontur und leichter Wulst"""
    W, H = size
    mask = Image.new("L", size, 0); d = ImageDraw.Draw(mask)
    bb = d.textbbox((0, 0), text, font=font); tw, th = bb[2] - bb[0], bb[3] - bb[1]
    x, y = (W - tw) // 2 - bb[0], (H - th) // 2 - bb[1]
    d.text((x, y), text, font=font, fill=255)
    grad = np.zeros((H, W, 3))
    t = np.linspace(0, 1, H)[:, None]
    for c in range(3):
        grad[:, :, c] = top[c] * (1 - t) + bot[c] * t
    m = np.asarray(mask, float) / 255
    if bevel:   # Licht oben links
        e = np.asarray(mask.filter(ImageFilter.GaussianBlur(2.5)), float) / 255
        gy, gx = np.gradient(e)
        sh = np.clip(-(gx + gy) * 6, -0.5, 0.6)[:, :, None]
        grad = grad * (1 + sh)
    out = Image.new("RGBA", size, (0, 0, 0, 0))
    ol = mask.filter(ImageFilter.MaxFilter(ow * 2 + 1))
    out.paste(Image.new("RGBA", size, outline + (255,)), (0, 0), ol)
    fill = Image.fromarray(np.clip(grad, 0, 255).astype(np.uint8), "RGB").convert("RGBA")
    out.paste(fill, (0, 0), mask)
    return out


def shield(r):
    S = r * 2 + 8
    im = Image.new("RGBA", (S, S), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    c = S // 2
    # Planken
    rng = np.random.default_rng(5)
    pl = Image.new("RGBA", (S, S), (0, 0, 0, 0)); dp = ImageDraw.Draw(pl)
    n = 7; w = 2 * r / n
    for i in range(n):
        x0 = c - r + i * w
        base = np.array([118, 74, 40]) * rng.uniform(0.82, 1.1)
        dp.rectangle([x0, c - r, x0 + w - 2, c + r], fill=tuple(int(v) for v in base) + (255,))
        for k in range(14):   # Maserung
            yy = c - r + rng.uniform(0, 2 * r); xx = x0 + rng.uniform(2, w - 4)
            dp.line([(xx, yy), (xx + rng.uniform(-2, 2), yy + rng.uniform(10, 40))], fill=(80, 48, 24, 160), width=2)
    m = Image.new("L", (S, S), 0); ImageDraw.Draw(m).ellipse([c - r, c - r, c + r, c + r], fill=255)
    im.paste(pl, (0, 0), m)
    # Eisenrand + Nieten
    d.ellipse([c - r, c - r, c + r, c + r], outline=(70, 72, 78, 255), width=int(r * 0.09))
    d.ellipse([c - r + 3, c - r + 3, c + r - 3, c + r - 3], outline=(140, 142, 150, 255), width=2)
    for a in range(0, 360, 30):
        x = c + math.cos(math.radians(a)) * r * 0.955; y = c + math.sin(math.radians(a)) * r * 0.955
        d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=(180, 182, 190, 255))
    # Buckel
    br = r * 0.36
    d.ellipse([c - br, c - br, c + br, c + br], fill=(92, 94, 102, 255), outline=(170, 172, 180, 255), width=3)
    # goldener Valknut: drei verschlungene Dreiecke
    gold = (238, 192, 74, 255); dark = (120, 80, 20, 255)
    tr = br * 0.62
    for k in range(3):
        cx = c + math.cos(math.radians(90 + k * 120)) * tr * 0.28
        cy = c - math.sin(math.radians(90 + k * 120)) * tr * 0.28
        pts = [(cx + math.cos(math.radians(90 + j * 120 + k * 40)) * tr, cy - math.sin(math.radians(90 + j * 120 + k * 40)) * tr) for j in range(3)]
        d.polygon(pts, outline=dark, width=7); d.polygon(pts, outline=gold, width=4)
    return im


def sword(L):
    im = Image.new("RGBA", (L, L), (0, 0, 0, 0)); d = ImageDraw.Draw(im)
    # schraeg von unten links nach oben rechts
    a = math.radians(-58); ux, uy = math.cos(a), math.sin(a)
    px, py = -uy, ux
    cx, cy = L * 0.5, L * 0.5
    def P(t, s):
        return (cx + ux * t + px * s, cy + uy * t + py * s)
    bl = L * 0.46
    d.polygon([P(-bl * 0.55, -14), P(bl, -14), P(bl + 34, 0), P(bl, 14), P(-bl * 0.55, 14)], fill=(196, 204, 214, 255))
    d.line([P(-bl * 0.55, 0), P(bl + 20, 0)], fill=(236, 240, 246, 255), width=4)
    d.polygon([P(-bl * 0.55, -52), P(-bl * 0.55 + 16, -52), P(-bl * 0.55 + 16, 52), P(-bl * 0.55, 52)], fill=(84, 60, 150, 255))   # Parierstange
    d.polygon([P(-bl * 0.55 - 70, -10), P(-bl * 0.55, -10), P(-bl * 0.55, 10), P(-bl * 0.55 - 70, 10)], fill=(70, 46, 28, 255))   # Griff
    x, y = P(-bl * 0.55 - 80, 0); d.ellipse([x - 13, y - 13, x + 13, y + 13], fill=(238, 192, 74, 255))
    return im


def make(out_dir, W=1320, H=700):
    os.makedirs(out_dir, exist_ok=True)
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    r = 190
    sh = shield(r); sw = sword(int(r * 3.0))
    cx, cy = 264, int(H * 0.47)
    # Glut-Schein hinter Schild und Schwert
    glow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gl = Image.new("L", (W, H), 0); ImageDraw.Draw(gl).ellipse([cx - r - 40, cy - r - 40, cx + r + 40, cy + r + 40], fill=255)
    gl = gl.filter(ImageFilter.GaussianBlur(38))
    glow.paste(Image.new("RGBA", (W, H), (255, 140, 40, 255)), (0, 0), gl)
    img = Image.alpha_composite(img, glow)
    img.alpha_composite(sw, (cx - sw.width // 2, cy - sw.height // 2))
    img.alpha_composite(sh, (cx - sh.width // 2, cy - sh.height // 2))
    # Schrift
    f_big = ImageFont.truetype(F900, 190); f_small = ImageFont.truetype(F700, 52); f_sub = ImageFont.truetype(F700, 64)
    t1 = _grad_text((760, 90), "THE LEGEND OF", f_small, (255, 236, 160), (210, 150, 50), (60, 30, 8), 4)
    t2 = _grad_text((1010, 260), "VALHEIM", f_big, (236, 60, 46), (120, 12, 14), (250, 214, 110), 7)
    t3 = _grad_text((820, 110), "OCARINA OF TIME", f_sub, (255, 236, 160), (200, 140, 40), (60, 30, 8), 4)
    tx = 810
    img.alpha_composite(t1, (tx - t1.width // 2, int(H * 0.17)))
    img.alpha_composite(t2, (tx - t2.width // 2 - 20, int(H * 0.26)))
    img.alpha_composite(t3, (tx - t3.width // 2, int(H * 0.62)))
    img.save(os.path.join(out_dir, "title_logo.png"))
    # "PRESS START"
    ps = _grad_text((600, 90), "PRESS START", ImageFont.truetype(F700, 54), (255, 90, 70), (190, 30, 24), (40, 10, 6), 3, bevel=False)
    ps.save(os.path.join(out_dir, "title_press.png"))
    return os.path.join(out_dir, "title_logo.png")


if __name__ == "__main__":
    print(make(sys.argv[1]))
