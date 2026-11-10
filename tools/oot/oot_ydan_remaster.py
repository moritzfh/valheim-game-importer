"""Deku-Baum innen als Valheim-Remaster (wie Kokiri-Wald und Links Haus): Originalform, aber
  - Waende: Valheims Eichenrinde (nahtlose Kachel aus oak_bark + Normal-Map), zylindrisch um die Raummitte gelegt,
    nach oben geneigte Flaechen bemoost
  - Boeden: Valheims Waldboden (Gelaende-Schicht 1 "forest"), Pflaster (Schicht 12) wo im Original Steine liegen
  - Wasser: Valheims Wasser
  - Ranken, Wurzel-Fransen, Jahresringe, Bretter, Boss-Muster: Original, aber 4x weich hochgerechnet (keine Pixel)
  - N64-Licht (Vertexfarben) bleibt als Helligkeitsstufe je Dreieck erhalten (Farbfaktor statt neuer Textur)

Texturen: aus dem Spiel geholt mit "statics dumpmat terrain" / "statics dumpmat Oak1" (src/Glb.cs DumpMat) nach
GI_VMAT (Standard /home/claude/work/vmat): mat_terrain_DiffuseArrayTex_N.png, mat_Oak1_MainTex.png, mat_Oak1_BumpMap.png.
"""
import collections
import copy
import math
import os

import numpy as np

VMAT = os.environ.get("GI_VMAT", "/home/claude/work/vmat")

# Materialgruppen (Textur-Adresse & 0xFFFFFF im Szenen-Segment), aus dem Kontaktbogen der 113 ydan-Materialien
WATER = {"12da0", "13ee8", "8498"}
VINES = {"11da0", "12ee8", "3d78", "44d8", "4e28", "7498", "7908"}             # Ranken: N64-Gelb -> natuerliches Gruen-Braun
WEB = {"131a0", "142e8", "3150", "3bc8", "6968", "8898"}                     # Netz-Flaechen an Waenden (bleiben ausgeblendet)
KEEP_UP = {"11da0", "12ee8", "3d78", "44d8", "4e28", "7498", "7908",           # Ranken (Kletterflaechen)
           "7160", "74b0", "8160",                                              # Wurzel-Fransen an Decken
           "2c80", "5308", "54d8", "6b18", "84b0", "b0b0",                      # Boss-Raum-Muster
           "0", "6f18", "b4b0", "bd60"}                                         # bunte Rahmen, Eingangsschein
# Jahresringe (Plattform-Oberseiten) und Maserung: jetzt auch Remaster (oben Waldboden, Seiten Rinde) - hochgerechnet sahen
# sie im flachen Blickwinkel wie Zebrastreifen aus
RINGS = {"1b60", "1c08", "1ec0", "20c0", "2480", "2b08", "4718", "9160", "ee8", "3408", "ad60", "b8b0", "d8b0", "d960", "e8b0", "e960", "f960"}
STONE = {"30c0", "4b08", "6318", "6708", "7318", "a8b0", "d0b0", "d560"}
SAND = {"d0b0", "7318", "d560", "6708"}       # helle Sandstein-Flecken (32x32) -> Valheim-Erde statt Pflaster

BARK_REP_U, BARK_REP_V = 105.0, 130.0          # OoT-Einheiten je Kachel (~2,5 x 3,1 m)
FLOOR_REP = 70.0                            # Waldboden: eine 256er-Kachel je ~2,3 m
STONE_REP = 80.0


def hexkey(k):
    if isinstance(k, tuple) and k and isinstance(k[0], int):
        return "%x" % (k[0] & 0xFFFFFF)
    return str(k)


def _srgb_to_lin(x):
    x = x / 255.0
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def _seamless(a, frac=0.55):
    """Kachel nahtlos: Rand mit der um eine halbe Kachel verschobenen Kopie ueberblenden (wie oot_kf.bark_tile)."""
    H, W = a.shape[:2]
    yy = np.abs(np.linspace(-1, 1, H))[:, None]; xx = np.abs(np.linspace(-1, 1, W))[None, :]
    m = np.clip((np.maximum(yy, xx) - frac) / (1 - frac), 0, 1)[:, :, None]
    return a * (1 - m) + np.roll(np.roll(a, H // 2, 0), W // 2, 1) * m


_CACHE = {}


def _img(name):
    from PIL import Image
    return np.asarray(Image.open(os.path.join(VMAT, name)), float)


def bark(moss=False):
    """Eichenrinde (Farbe, Normal-Map) als nahtlose Kachel aus dem oak_bark-Atlas (sauberer Ausschnitt ohne Moos)."""
    key = ("bark", moss)
    if key in _CACHE:
        return _CACHE[key]
    from PIL import Image
    box = (205, 45, 375, 255)
    col = _img("mat_Oak1_MainTex.png")[box[1]:box[3], box[0]:box[2], :3]
    nm = _img("mat_Oak1_BumpMap.png")[box[1]:box[3], box[0]:box[2]]
    if moss:
        mo = _img("mat_Oak1_MainTex.png")[170:250, 400:500, :3]
        mo = np.asarray(Image.fromarray(mo.astype(np.uint8)).resize((col.shape[1], col.shape[0]), Image.BICUBIC), float)
        from scipy import ndimage
        rng = np.random.default_rng(5)
        nz = ndimage.gaussian_filter(rng.random(col.shape[:2]), 8.0, mode="wrap"); nz = (nz - nz.min()) / (np.ptp(nz) + 1e-9)
        w = np.clip((nz - 0.3) * 2.0, 0, 0.9)[:, :, None]
        col = col * (1 - w) + mo * w
    col = _seamless(col) * 0.9
    # Normal-Map: Unity-Format (x in Alpha, y in Gruen, beim Auslesen sRGB-kodiert) -> normale RGB-Normal-Map
    nx = nm[:, :, 3] / 255.0 * 2 - 1
    ny = _srgb_to_lin(nm[:, :, 1]) * 2 - 1
    n = np.stack([nx, ny, np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))], 2)
    n = _seamless(n)
    n /= np.linalg.norm(n, axis=2, keepdims=True) + 1e-9
    # 2x hochrechnen (weicher, die Kachel ist nur 170x210)
    H, W = col.shape[:2]
    colb = np.asarray(Image.fromarray(np.clip(col, 0, 255).astype(np.uint8)).resize((W * 2, H * 2), Image.BICUBIC))
    nb = np.asarray(Image.fromarray(((n * 0.5 + 0.5) * 255).astype(np.uint8)).resize((W * 2, H * 2), Image.BICUBIC))
    rgba = np.concatenate([colb, np.full(colb.shape[:2] + (1,), 255, np.uint8)], 2)
    nrgba = np.concatenate([nb, np.full(nb.shape[:2] + (1,), 255, np.uint8)], 2)
    _CACHE[key] = (rgba.tobytes(), W * 2, H * 2, nrgba.tobytes())
    return _CACHE[key]


def terrain(layer, tint=(1.0, 1.0, 1.0), mix=None):
    """Valheim-Gelaendeschicht als Kachel (256x256, schon nahtlos), optional getoent / mit einer zweiten Schicht gemischt."""
    key = ("terrain", layer, tint, mix)
    if key in _CACHE:
        return _CACHE[key]
    a = _img("mat_terrain_DiffuseArrayTex_%d.png" % layer)[:, :, :3]
    if mix is not None:
        b = _img("mat_terrain_DiffuseArrayTex_%d.png" % mix[0])[:, :, :3]
        # periodisches Rauschen (FFT) -> Flecken einer zweiten Schicht, Kachel bleibt nahtlos
        rng = np.random.default_rng(11)
        f = np.fft.fft2(rng.standard_normal(a.shape[:2]))
        fy = np.fft.fftfreq(a.shape[0])[:, None]; fx = np.fft.fftfreq(a.shape[1])[None, :]
        f *= np.exp(-(fx * fx + fy * fy) / (2 * 0.02 ** 2))
        nz = np.real(np.fft.ifft2(f)); nz = (nz - nz.min()) / (np.ptp(nz) + 1e-9)
        w = np.clip((nz - (1 - mix[1])) * 4, 0, 1)[:, :, None]
        a = a * (1 - w) + b * w
    a = np.clip(a * np.array(tint), 0, 255).astype(np.uint8)
    rgba = np.concatenate([a, np.full(a.shape[:2] + (1,), 255, np.uint8)], 2)
    _CACHE[key] = (rgba.tobytes(), a.shape[1], a.shape[0], None)
    return _CACHE[key]


def upscale(m, f=4):
    """Original-Textur weich hochrechnen (bikubisch, Alpha mit), damit keine N64-Pixel mehr zu sehen sind."""
    from PIL import Image
    im = Image.frombytes("RGBA", (m.w, m.h), bytes(m.rgba)).resize((m.w * f, m.h * f), Image.BICUBIC)
    return im.tobytes(), m.w * f, m.h * f


def room_tris(rom, scene, keep):
    """Wie oot_pack.scene_tris, aber mit Raumnummer je Dreieck."""
    import oot_pack
    from oot_gfx import Gfx, Segs
    sb = open(os.path.join(rom, scene + "_scene"), "rb").read()
    gk = open(os.path.join(rom, "gameplay_keep"), "rb").read()
    kk = open(os.path.join(rom, "gameplay_%s_keep" % keep), "rb").read()
    tris, mats = [], {}
    i = 0
    while os.path.exists(os.path.join(rom, "%s_room_%d" % (scene, i))):
        rb = open(os.path.join(rom, "%s_room_%d" % (scene, i)), "rb").read()
        g = Gfx(Segs({2: sb, 3: rb, 4: gk, 5: kk}))
        n0 = 0
        for opa, xlu in oot_pack.room_dlists(rb):
            if opa:
                g.reset(); g.run(opa, xlu=False)
            if xlu:
                g.reset(); g.run(xlu, xlu=True)
        tris += [(i, k, vs) for k, vs in g.tris]; mats.update(g.mats)
        i += 1
    return sb, tris, mats


class _M:
    pass


def _mat(base, key, rgba, w, h, normal=None, tint=None, lift=0.0, wrap=10497):
    m = copy.copy(base)
    m.rgba, m.w, m.h = rgba, w, h
    m.alpha = False; m.xlu = False; m.combine = None; m.valheim = None; m.hidden = False
    m.wrap_s = m.wrap_t = wrap; m.key = key; m.matte = True
    m.normal = normal; m.tint = tint; m.lift = lift
    return m


RELIEF = os.environ.get("GI_YDAN_RELIEF", "1") != "0"
# Akteure an/vor Waenden, um die das Relief ausblendet (Radius in OoT-Einheiten)
RELIEF_AVOID = {"ACTOR_EN_SW": 55.0, "ACTOR_BG_YDAN_SP": 140.0, "ACTOR_OBJ_SWITCH": 75.0, "ACTOR_BG_BREAKWALL": 130.0,
                "ACTOR_BG_YDAN_MARUTA": 120.0, "ACTOR_EN_BOX": 45.0}


def _relief_avoid():
    import struct
    import oot_ydan as Y
    ids = Y.I.actor_ids(); inv = {v: k for k, v in ids.items()}
    pts = []
    for r, aid, x, y, z, rx, ry, rz, prm in Y.room_actors("ydan", 12):
        rad = RELIEF_AVOID.get(inv.get(aid & 0xFFFF, "?"))
        if rad:
            pts.append((x, y, z, rad))
    sb, h = Y.scene_header("ydan")
    n, a = h[0x0E]
    for i in range(n):
        fr, fc, br, bc, aid, x, y, z, ry, prm = struct.unpack(">bbbbhhhhhH", sb[a + i * 16:a + i * 16 + 16])
        pts.append((x, y, z, 115.0))                     # Tueren, Raumuebergaenge
    n, a = h[0x00]
    for i in range(n):
        s = struct.unpack(">hhhhhhhh", sb[a + i * 16:a + i * 16 + 16])
        pts.append((s[1], s[2], s[3], 70.0))             # Eingang
    return np.array(pts, float)


def remaster(rom, lift=0.4):
    """-> (tris, mats) fuer oot_pack.write_tiles: [(key, [(pos, uv, rgba), ...])], {key: mat}"""
    sb, rtris, mats = room_tris(rom, "ydan", "dangeon")
    # Raummitte (xz) je Raum aus den Wand-Dreiecken: Mittelpunkt der Zylinder-Abbildung der Rinde
    cen = {}
    acc = collections.defaultdict(list)
    for r, k, vs in rtris:
        P = np.array([v[0] for v in vs], float)
        acc[r].append(P.mean(0))
    for r, l in acc.items():
        a = np.array(l); cen[r] = (float(np.median(a[:, 0])), float(np.median(a[:, 2])))
    cen[0] = (0.0, 0.0)                     # Hauptraum: Stammmitte
    base = next(iter(mats.values()))
    out, dm = [], {}
    up_cache = {}
    warm = np.array([1.0, 0.94, 0.84])

    def level(vs):
        c = np.mean([v[2][:3] for v in vs], axis=0) / 255.0
        lum = float(0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2])
        return max(0.2, min(1.0, round(lum * 6) / 6.0))

    def tint_of(b):
        # N64-Licht nur noch sanft (sonst sieht man die Dreiecke als harte Flecken); Valheims Licht macht den Rest
        t = (0.6 + 0.4 * b) * warm
        return tuple(round(float(x), 3) for x in t)

    import oot_ydan_relief as RL
    rsrc, rfloors, decal = [], [], []

    def make_mat(key, tex, tint, lift_=None):
        rgba, w, h, nrm = tex
        return _mat(base, key, rgba, w, h, normal=(nrm, w, h) if nrm is not None else None, tint=tint,
                    lift=lift if lift_ is None else lift_)

    for r, k, vs in rtris:
        hk = hexkey(k)
        m0 = mats[k]
        P = np.array([v[0] for v in vs], float)
        n = np.cross(P[1] - P[0], P[2] - P[0]); ln = np.linalg.norm(n)
        if ln < 1e-6:
            continue
        n = n / ln
        b = level(vs)
        if RELIEF and (hk in WEB or hk in KEEP_UP) and hk != "0":
            # Ranken, Netze, Fransen, Rahmen: hier blendet das Relief aus
            e = max(np.linalg.norm(P[1] - P[0]), np.linalg.norm(P[2] - P[1]), np.linalg.norm(P[0] - P[2]))
            nn = int(min(30, max(1, math.ceil(e / 15.0))))
            B, _t = RL._tpl(nn)
            decal.extend(list(B @ P))
        if hk in WATER:
            kk = ("rm_water",)
            if kk not in dm:
                m = copy.copy(m0); m.valheim = "@water"; m.key = kk; dm[kk] = m
            out.append((kk, [(p, uv, (255, 255, 255, 255)) for p, uv, _ in vs]))
            continue
        if hk == "0":
            # Eingang: Licht von draussen (Segment 8) -> weicher Schein statt weisser Flaeche
            kk = ("rm_glow",)
            if kk not in dm:
                m = copy.copy(m0); m.glow = True; m.key = kk; m.matte = True; dm[kk] = m
            out.append((kk, [(p, uv, (255, 255, 255, 255)) for p, uv, _ in vs]))
            continue
        if hk in WEB or m0.rgba is None or hk in KEEP_UP or (m0.alpha and hk not in STONE and hk not in RINGS):
            # Original behalten, 4x weich hochgerechnet, Licht als Farbfaktor
            kk = ("rm_up", k, b)
            if kk not in dm:
                m = copy.copy(m0); m.matte = True; m.lift = lift
                if m.rgba is not None:
                    if k not in up_cache:
                        up_cache[k] = upscale(m0)
                    m.rgba, m.w, m.h = up_cache[k]
                    m.key = ("rm_up", k)               # gleiche Bilddatei fuer alle Helligkeitsstufen
                    m.tint = tint_of(b)
                    if hk in VINES:
                        m.tint = tuple(round(x * y, 3) for x, y in zip(m.tint, (0.62, 0.72, 0.42)))
                    if hk in WEB:
                        # Spinnweben in den Ecken: im N64-Look ausgeblendet (zu weiss) -> jetzt zarte, weich leuchtende Faeden
                        m.glow = 0.018; m.tint = (0.75, 0.76, 0.78); m.xlu = False; m.alpha = False; m.lift = 0
                elif m.rgba is None:
                    m.prim = tuple(int(x * b) for x in m.prim[:3]) + (m.prim[3],)
                dm[kk] = m
            out.append((kk, [(p, uv, (255, 255, 255, 255)) for p, uv, _ in vs]))
            continue
        # Remaster-Flaechen: Weltlage-UVs
        if n[1] > 0.6:
            kind = "stone" if hk in STONE else "floor"
        elif n[1] < -0.6:
            kind = "ceil"
        elif n[1] > 0.3:
            kind = "moss"
        else:
            kind = "wall"
        if kind == "stone" and hk in SAND:
            tex = terrain(3, (1.0, 0.95, 0.82), mix=(1, 0.3)); rep = FLOOR_REP
            uv = [(p[0] / rep, p[2] / rep) for p in P]
        elif kind == "stone":
            tex = terrain(12, (0.72, 0.72, 0.68)); rep = STONE_REP
            uv = [(p[0] / rep, p[2] / rep) for p in P]
        elif kind == "floor":
            tex = terrain(1, (1.18, 1.1, 0.78), mix=(2, 0.4)); rep = FLOOR_REP
            uv = [(p[0] / rep, p[2] / rep) for p in P]
        elif kind == "ceil":
            tex = bark(False)
            uv = [(p[0] / BARK_REP_U, p[2] / BARK_REP_U) for p in P]
        else:
            tex = bark(kind == "moss")
            cx, cz = cen.get(r, (0.0, 0.0))
            d = P.mean(0) - np.array([cx, 0, cz]); d[1] = 0
            dl = np.linalg.norm(d)
            nh = np.array([n[0], 0, n[2]]); nl = np.linalg.norm(nh)
            radial = dl > 1 and nl > 1e-6 and abs(np.dot(d / dl, nh / nl)) > 0.55
            if radial:
                # zylindrisch um die Raummitte: u = Winkel * Radius (stetig entlang gebogener Waende)
                rr = max(60.0, float(dl))
                ang = [math.atan2(p[0] - cx, p[2] - cz) for p in P]
                for j in (1, 2):
                    while ang[j] - ang[0] > math.pi: ang[j] -= 2 * math.pi
                    while ang[j] - ang[0] < -math.pi: ang[j] += 2 * math.pi
                uv = [(a * rr / BARK_REP_U, -p[1] / BARK_REP_V) for a, p in zip(ang, P)]
            else:
                t = np.array([-n[2], 0.0, n[0]]); t /= (np.linalg.norm(t) or 1.0)
                uv = [(float(np.dot(p, t)) / BARK_REP_U, -p[1] / BARK_REP_V) for p in P]
        if RELIEF and kind in ("floor", "stone"):
            rfloors.append((r, kind if not (kind == "stone" and hk in SAND) else "sand", P, n, b, tex, rep))
            continue
        if RELIEF and kind in ("wall", "moss", "ceil"):
            # Grundschicht: gleiches Holz wie das Relief (Luecken im Relief fallen nicht auf), Relief kommt davor
            cc = cen.get(r, (0.0, 0.0))
            rsrc.append((r, kind, P, n, b))
            kk = ("rlb", "moss" if kind == "moss" else "wood", b)
            if kk not in dm:
                dm[kk] = make_mat(("rl_tex", "moss" if kind == "moss" else "wood"), RL.wood(0.75 if kind == "moss" else 0.0), tint_of(b))
            uv = RL._uv_wall(P, cc, RL.mapping(n, P, cc))
            out.append((kk, [(tuple(p), uv[i], (255, 255, 255, 255)) for i, p in enumerate(P)]))
            continue
        kk = ("rm", kind, b)
        if kk not in dm:
            rgba, w, h, nrm = tex
            dm[kk] = _mat(base, ("rm_tex", kind), rgba, w, h, normal=(nrm, w, h) if nrm is not None else None,
                          tint=tint_of(b), lift=lift)
        out.append((kk, [(tuple(p), uv[i], (255, 255, 255, 255)) for i, p in enumerate(P)]))
    if RELIEF:
        ro, rdm, rl = RL.build(rsrc, rfloors, np.array(decal, float), _relief_avoid(), cen, tint_of, base, make_mat)
        out += ro; dm.update(rdm); EXTRA_LIGHTS[:] = rl
    return sb, out, dm


EXTRA_LIGHTS = []          # Leuchtpilz-Lichter aus dem Relief (remaster() fuellt, lights() haengt an)


def lights():
    """Remaster-Licht im Deku-Baum (pack.json oot_play.lights, Valheim-Weltkoordinaten): warmes Licht von oben durch den
    hohlen Stamm, warme Lichtpunkte je Stockwerk im Hauptraum, je ein weiches Licht in den Seitenraeumen, unten kuehler."""
    import oot_ydan as Y
    sb, rtris, mats = room_tris(Y.ROM, "ydan", "dangeon")
    out = []
    warm, cool = [1.0, 0.82, 0.56], [0.62, 0.8, 1.0]
    # Lichtschacht durch die Stammmitte (Spot nach unten, mit Schatten)
    out.append({"pos": Y.YV((0, 1150, 0)), "spot": [0, -1, 0], "angle": 46, "range": 55, "intensity": 2.4, "color": [1.0, 0.88, 0.66], "pixel": True})
    # Hauptraum: Ringe aus Lichtpunkten je Stockwerk (1. Stock um das Netz, 2./3. Stock an den Rundgaengen)
    for y, r, n, it in ((110, 330, 4, 0.9), (520, 380, 3, 0.8), (900, 360, 2, 0.7)):
        for i in range(n):
            a = 2 * math.pi * (i + 0.5) / n
            out.append({"pos": Y.YV((r * math.sin(a), y, r * math.cos(a))), "range": 11, "intensity": it, "color": warm, "shadows": False})
    # Seitenraeume: Mitte + 90 Einheiten ueber dem typischen Boden
    acc = collections.defaultdict(list)
    for r, k, vs in rtris:
        P = np.array([v[0] for v in vs], float)
        n = np.cross(P[1] - P[0], P[2] - P[0]); ln = np.linalg.norm(n)
        if ln > 1e-6 and n[1] / ln > 0.8:
            acc[r].append(P.mean(0))
    for r, l in sorted(acc.items()):
        if r == 0:
            continue
        a = np.array(l); c = np.median(a, axis=0)
        low = c[1] < -300
        out.append({"pos": Y.YV((float(c[0]), float(c[1]) + 95, float(c[2]))), "range": 10, "intensity": 0.8 if not low else 0.65,
                    "color": cool if low else warm, "shadows": False})
    # B1 unter dem Netz (Wasser, kuehl)
    out.append({"pos": Y.YV((0, -560, 0)), "range": 14, "intensity": 0.7, "color": cool, "shadows": False})
    # Leuchtpilze an den Waenden (aus remaster(): erst geometry() laufen lassen)
    out += [dict(l) for l in EXTRA_LIGHTS]
    return out


def shaft(out_dir, rel_dir):
    """Lichtschacht durch die Stammmitte (zum Spot-Licht aus lights()): offener Kegel, weich additiv ("glow"), oben hell,
    nach unten auslaufend, feine senkrechte Strahlen. -> Statics-Eintrag (ohne Kollision)."""
    import oot_glb
    import oot_pack
    from oot_ydan import OFF_YDAN
    segs, y0, y1, r0, r1 = 40, 1180.0, 0.0, 85.0, 175.0
    W, H = 128, 256
    rng = np.random.default_rng(4)
    u = np.linspace(0, 1, W, endpoint=False)
    rays = np.zeros(W)
    for _ in range(9):                        # periodische Strahlen-Streifen
        f = rng.integers(2, 9); ph = rng.random() * 2 * np.pi
        rays += (0.5 + 0.5 * np.sin(2 * np.pi * f * u + ph)) ** 6
    rays = 0.45 + 0.55 * rays / rays.max()
    v = np.linspace(0, 1, H)[:, None]
    a = np.clip(np.sin(np.pi * np.clip(v * 1.15, 0, 1)) ** 0.7 * (1 - 0.75 * v), 0, 1) * rays[None, :]
    img = np.zeros((H, W, 4), np.uint8); img[..., :3] = 255; img[..., 3] = np.clip(a * 255, 0, 255).astype(np.uint8)
    base = _M()
    m = _mat(base, ("rm_shaft",), img.tobytes(), W, H, tint=(1.0, 0.9, 0.7), lift=0.0)
    m.glow = 0.045; m.matte = True; m.prim = (255, 255, 255, 255); m.combine = None
    tris = []
    for i in range(segs):
        a0, a1 = 2 * math.pi * i / segs, 2 * math.pi * (i + 1) / segs
        p00 = (r0 * math.sin(a0), y0, r0 * math.cos(a0)); p01 = (r0 * math.sin(a1), y0, r0 * math.cos(a1))
        p10 = (r1 * math.sin(a0), y1, r1 * math.cos(a0)); p11 = (r1 * math.sin(a1), y1, r1 * math.cos(a1))
        u0, u1 = i / segs * 3, (i + 1) / segs * 3
        c = (255, 255, 255, 255)
        tris.append([(p00, (u0, 0.0), c), (p10, (u0, 1.0), c), (p11, (u1, 1.0), c)])
        tris.append([(p00, (u0, 0.0), c), (p11, (u1, 1.0), c), (p01, (u1, 0.0), c)])
    name = "ydan_shaft.glb"
    oot_glb.write(os.path.join(out_dir, name), {("rm_shaft",): tris}, {("rm_shaft",): m}, scale=oot_pack.S, offset=(0, 0, 0), name="ydan_shaft")
    p = oot_pack.V((0, 0, 0))
    return {"m": rel_dir + "/" + name, "p": [round(p[k] + OFF_YDAN[k], 3) for k in range(3)], "q": oot_pack.Q180, "s": [1, 1, 1],
            "r": round(r1 * 1.5 * oot_pack.S, 2), "c": 0, "vis": 300}


def atmos():
    """Teilchen-Stimmung (pack.json "oot_atmos", src/OotAtmos.cs, Weltkoordinaten): je Stockwerk eines Raums Bodennebel
    ueber den echten Bodenflaechen, leuchtende Sporen im Raum, hier und da Gluehwuermchen; im Hauptraum Staub und
    Lichtstrahlen im Schacht von oben und grosse Nebelschwaden. Oben warm, B1 tuerkis, B2/Boss violett."""
    import oot_ydan as Y
    sb, rtris, mats = room_tris(Y.ROM, "ydan", "dangeon")
    S = 0.033
    groups = collections.defaultdict(list)
    for r, k, vs in rtris:
        hk = hexkey(k)
        if hk in WATER:
            continue
        P = np.array([v[0] for v in vs], float)
        n = np.cross(P[1] - P[0], P[2] - P[0]); ln = np.linalg.norm(n)
        if ln < 1e-6 or n[1] / ln < 0.8:
            continue
        groups[(r, int(round(P[:, 1].mean() / 60.0)))].append(P)
    rng = np.random.default_rng(31)
    out = []

    def pal(y):
        # Bodennebel nahe der Nebelfarbe (unbeleuchtet: sonst leuchten die Schwaden wie weisse Flecken), sehr duenn
        if y > -300:
            return {"mist": [0.62, 0.56, 0.38, 0.13], "spores": [1.0, 0.8, 0.42, 1.0], "flies": [0.82, 1.0, 0.42, 1.0]}
        if y > -1100:
            return {"mist": [0.24, 0.44, 0.42, 0.15], "spores": [0.45, 1.0, 0.85, 1.0], "flies": [0.6, 1.0, 0.75, 1.0]}
        return {"mist": [0.34, 0.28, 0.38, 0.14], "spores": [0.85, 0.6, 1.0, 0.9], "flies": [0.95, 0.7, 1.0, 1.0]}

    def box(p0, p1):
        a, b = Y.YV(tuple(p0)), Y.YV(tuple(p1))
        return [round(min(a[0], b[0]), 2), round(min(a[1], b[1]), 2), round(min(a[2], b[2]), 2),
                round(max(a[0], b[0]), 2), round(max(a[1], b[1]), 2), round(max(a[2], b[2]), 2)]

    for (r, lv), tris in sorted(groups.items()):
        A = sum(0.5 * np.linalg.norm(np.cross(P[1] - P[0], P[2] - P[0])) for P in tris) * S * S     # m^2
        if A < 6:
            continue
        allp = np.concatenate(tris)
        y = float(np.median(allp[:, 1]))
        c = pal(y)
        x0, z0 = allp[:, 0].min(), allp[:, 2].min(); x1, z1 = allp[:, 0].max(), allp[:, 2].max()
        flat = []
        for P in tris:
            for p in P:
                flat += [round(v, 2) for v in Y.YV(tuple(p))]
        out.append({"kind": "mist", "box": box((x0, y, z0), (x1, y + 60, z1)), "tris": flat, "n": int(np.clip(A / 4.0, 6, 120)),
                    "color": c["mist"], "size": [4.5, 8.5], "life": [22, 40], "speed": 0.05, "lift": 0.22, "jitter": 0.12, "near": 42})
        out.append({"kind": "spores", "box": box((x0, y + 25, z0), (x1, y + 330, z1)), "n": int(np.clip(A * 1.1, 20, 260)),
                    "color": c["spores"], "size": [0.04, 0.09], "life": [6, 12], "speed": 0.05, "rise": 0.04, "near": 38})
        if rng.random() < 0.6:
            out.append({"kind": "flies", "box": box((x0, y + 40, z0), (x1, y + 220, z1)), "n": int(np.clip(A * 0.06, 3, 16)),
                        "color": c["flies"], "size": [0.07, 0.12], "life": [5, 9], "speed": 0.25, "near": 38})
    # Hauptraum: Lichtschacht von oben (Staub, Strahlen), grosse Schwaden im Stamm
    out.append({"kind": "dust", "box": box((-150, 20, -150), (150, 1150, 150)), "n": 520, "color": [1.0, 0.94, 0.78, 0.55],
                "size": [0.015, 0.035], "life": [8, 14], "speed": 0.03, "rise": -0.01, "near": 60})
    out.append({"kind": "rays", "box": box((-110, 0, -110), (110, 1170, 110)), "n": 11, "color": [1.0, 0.88, 0.64, 0.12],
                "size": [1.5, 3.6], "life": [10, 16], "near": 80})
    out.append({"kind": "haze", "box": box((-420, 60, -420), (420, 1100, 420)), "n": 26, "color": [0.6, 0.54, 0.38, 0.08],
                "size": [6.0, 11.0], "life": [20, 34], "speed": 0.05, "near": 60})
    out.append({"kind": "haze", "box": box((-650, -1000, -500), (450, -600, 650)), "n": 18, "color": [0.24, 0.44, 0.42, 0.09],
                "size": [5.0, 9.0], "life": [20, 34], "speed": 0.05, "near": 50})
    return out
