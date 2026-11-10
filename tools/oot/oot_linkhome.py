"""Links Haus (link_home) im Valheim-Look.

Im Original ist der Raum ein vorgerendertes Bild (320x240 JPG) mit fester Kamera + unsichtbarer Tiefen-Geometrie.
Hier: die Tiefen-Geometrie als echter 3D-Raum; Flaechen, die die Original-Kamera sieht, bekommen das Original-Bild
(projiziert) als Relief in Valheim-Holz, der Rest Valheim-Eichenrinde mit sanftem Relief.

  build(root, out_dir, offset) -> statics-Eintraege (Liste) fuer statics_linkhome.json
"""
import os, math, copy, json, collections
import numpy as np
import oot_pack, oot_glb, oot_vpack
from oot_gfx import Gfx

S = oot_pack.S
JPG = "assets/scenes/indoors/link_home/link_home_room_0_030000A0_RoomShapeImageSingle_03002480_JFIF.jpg"
CAM_EYE = np.array([-118.0, 345.0, 47.0])
CAM_ROT = (12743, 20389)          # Binaerwinkel: Neigung (nach unten), Gierwinkel
CAM_FOV = 46.83                   # senkrecht, Grad (BgCam 4683 / 100)
VTEX = oot_vpack.VTEX


def cam_basis():
    b2r = 2 * math.pi / 65536
    pitch = -CAM_ROT[0] * b2r; yaw = CAM_ROT[1] * b2r
    fwd = np.array([math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw)])
    right = np.cross(fwd, [0, 1.0, 0]); right /= np.linalg.norm(right)
    up = np.cross(right, fwd)
    return fwd, right, up


def project(p):
    """OoT-Punkt -> (u, v) im 320x240-Bild (0..1), Tiefe"""
    fwd, right, up = cam_basis()
    d = np.asarray(p, float) - CAM_EYE
    z = d @ fwd
    t = math.tan(math.radians(CAM_FOV / 2))
    x = (d @ right) / (z * t * 4 / 3); y = (d @ up) / (z * t)
    return (x * 0.5 + 0.5, 0.5 - y * 0.5), z


class M:  # minimales Material fuer oot_glb.write
    def __init__(self, rgba, w, h, key):
        self.rgba, self.w, self.h, self.key = rgba, w, h, key
        self.wrap_s = self.wrap_t = 33071
        self.alpha = False; self.xlu = False; self.tint = None; self.valheim = None
        self.cutout = False


def _img_mat(img, key):
    from PIL import Image
    im = img.convert("RGBA"); return M(im.tobytes(), im.width, im.height, key)


def build(root, out_dir, offset, rel_dir="statics/linkhome"):
    from PIL import Image
    rom = os.path.join(root, "extracted", "gc-eu", "baserom")
    sb, tris, mats = oot_pack.scene_tris(rom, "link_home", "dangeon")
    jpg = Image.open(os.path.join(root, "extracted", "gc-eu", JPG)).convert("RGB")
    # Original-Bild -> Valheim-Holz mit Relief (Hochpass wie bei den Mauern)
    m0 = _img_mat(jpg, "lh_jpg")
    m0.rgba, m0.w, m0.h = oot_vpack.stone_blend(m0, (32.0, 32.0), base="wood", strong=False, f=2)
    m0.wrap_s = m0.wrap_t = 33071
    # Remaster: dieselbe saubere Eichenrinde wie im Kokiri-Wald (Haus im Baumstamm), matt
    import oot_kf
    rb, rw, rh = oot_kf.bark_tile(False)
    mb = M(rb, rw, rh, "lh_bark"); mb.wrap_s = mb.wrap_t = 10497; mb.matte = True
    mf = M(rb, rw, rh, "lh_floor"); mf.wrap_s = mf.wrap_t = 10497; mf.valheim = "wood_floor"      # Boden: Valheim-Dielen
    fwd, _, _ = cam_basis()
    groups = collections.OrderedDict([("jpg", []), ("bark", []), ("floor", [])])
    ymin = min(p[1] for _, vs in tris for p, _, _ in vs)
    for k, vs in tris:
        P = [np.array(p, float) for p, _, _ in vs]
        n = np.cross(P[1] - P[0], P[2] - P[0]); nn = np.linalg.norm(n)
        if nn < 1e-6:
            continue
        n /= nn
        c = sum(P) / 3
        view = (c - CAM_EYE); view /= np.linalg.norm(view)
        seen = (n @ view) < -0.25 and n[1] > 0.55       # nur nach oben zeigende Flaechen bekommen das Originalbild
        if seen and c[1] < ymin + 4 and n[1] > 0.9:
            groups["floor"].append([(tuple(p), (p[0] * S / 2.0, p[2] * S / 2.0), (255, 255, 255, 255)) for p in P]); continue
        if seen:
            tri = []
            for p in P:
                (u, v), _ = project(p)
                tri.append((tuple(p), (u, v), (255, 255, 255, 255)))
            groups["jpg"].append(tri)
        else:
            ax = int(np.argmax(np.abs(n)))
            a, b = [(1, 2), (0, 2), (0, 1)][ax]
            tri = [(tuple(p), (p[a] * S / 2.0, p[b] * S / 2.75), (255, 255, 255, 255)) for p in P]
            if ax != 1:      # Waende: senkrecht = v
                tri = [(tuple(p), ((p[0] + p[2]) * S / 2.0, -p[1] * S / 2.75), (255, 255, 255, 255)) for p in P]
            groups["bark"].append(tri)
    # Decke: das Original hat nur ein Hintergrundbild + Kollisionswaende (keine Decke) -> flache Kuppel aus Rinde ueber dem Raum
    from scipy.spatial import ConvexHull
    allp = np.array([p for _, vs in tris for p, _, _ in vs], float)
    ytop = float(allp[:, 1].max())
    hull = ConvexHull(allp[:, [0, 2]])
    ring = [allp[i] for i in hull.vertices]
    cx, cz = float(np.mean([r[0] for r in ring])), float(np.mean([r[2] for r in ring]))
    groups["ceil"] = []
    apex = (cx, ytop + 45.0, cz)
    for i in range(len(ring)):
        a, b = ring[i], ring[(i + 1) % len(ring)]
        pa, pb = (float(a[0]) * 1.03, ytop - 2.0, float(a[2]) * 1.03), (float(b[0]) * 1.03, ytop - 2.0, float(b[2]) * 1.03)
        uv = lambda q: (q[0] * S / 2.0, q[2] * S / 2.0)
        col = (255, 255, 255, 255)
        # nur nach innen sichtbar (Moritz 09.11.: im Intro sah die Startbild-Kamera von oben das Dach statt den Raum - im
        # Original gibt es keine Decke). Schatten wirft der Raum beidseitig (Eintrag "t": ["shadow2"]), also bleibt es innen dunkel.
        t1 = [(pa, uv(pa), col), (pb, uv(pb), col), (apex, uv(apex), col)]
        nrm = np.cross(np.subtract(pb, pa), np.subtract(apex, pa))
        groups["ceil"].append(t1 if nrm[1] < 0 else [t1[0], t1[2], t1[1]])
    # Fenster ueber dem Bett (im Original-Bild sichtbar; die Tiefen-Geometrie hat es nicht): Rahmen, helles Glas, offener Laden
    wood = Image.open(os.path.join(VTEX, "wood.png")).convert("RGB")
    mw = _img_mat(wood, "lh_winwood"); mw.wrap_s = mw.wrap_t = 10497
    glass = Image.new("RGB", (64, 64), (72, 88, 80))     # draussen gedimmt
    from PIL import ImageDraw
    gd = ImageDraw.Draw(glass)
    for i in range(36):                                     # Unterseite des nach aussen geklappten Ladens (wie im Original durchs Fenster sichtbar)
        ins = 2 + i * 0.22; c = int(30 + i * 0.6)
        gd.line([(ins, i), (63 - ins, i)], fill=(c + 8, c, c - 6))
    mg = _img_mat(glass, "lh_glass")
    groups["win_wood"], groups["win_glass"] = [], []
    WZ, WX, WY, WW, WH, T = 135.0, 0.0, 62.0, 66.0, 46.0, 5.0     # Wand z, Mitte x/y, Breite, Hoehe, Rahmenstaerke (OoT)

    def quad(g, a, b, c, d, uvs=((0, 0), (1, 0), (1, 1), (0, 1))):
        col = (255, 255, 255, 255)
        g.append([(a, uvs[0], col), (b, uvs[1], col), (c, uvs[2], col)])
        g.append([(a, uvs[0], col), (c, uvs[2], col), (d, uvs[3], col)])
        g.append([(a, uvs[0], col), (c, uvs[2], col), (b, uvs[1], col)])      # beidseitig
        g.append([(a, uvs[0], col), (d, uvs[3], col), (c, uvs[2], col)])

    def box(g, x0, x1, y0, y1, z0, z1):
        sx, sy, sz = (x1 - x0) * S / 2, (y1 - y0) * S / 2, (z1 - z0) * S / 2
        u = ((0, 0), (sx, 0), (sx, sy), (0, sy))
        quad(g, (x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), u)          # vorne (zum Raum, -z)
        quad(g, (x0, y1, z0), (x1, y1, z0), (x1, y1, z1), (x0, y1, z1), u)          # oben
        quad(g, (x0, y0, z1), (x1, y0, z1), (x1, y0, z0), (x0, y0, z0), u)          # unten
        quad(g, (x0, y0, z1), (x0, y0, z0), (x0, y1, z0), (x0, y1, z1), u)          # links
        quad(g, (x1, y0, z0), (x1, y0, z1), (x1, y1, z1), (x1, y1, z0), u)          # rechts
    x0, x1, y0, y1 = WX - WW / 2, WX + WW / 2, WY - WH / 2, WY + WH / 2
    zf = WZ - 6.0
    box(groups["win_wood"], x0 - T, x1 + T, y0 - T, y0, zf, WZ + 2)          # Fensterbank
    box(groups["win_wood"], x0 - T, x1 + T, y1, y1 + T, zf, WZ + 2)          # Sturz
    box(groups["win_wood"], x0 - T, x0, y0, y1, zf, WZ + 2)                  # links
    box(groups["win_wood"], x1, x1 + T, y0, y1, zf, WZ + 2)                  # rechts
    box(groups["win_wood"], WX - 1.5, WX + 1.5, y0, y1, zf + 2, WZ)          # Sprosse
    quad(groups["win_glass"], (x0, y0, WZ - 1), (x1, y0, WZ - 1), (x1, y1, WZ - 1), (x0, y1, WZ - 1),
         ((0, 1), (1, 1), (1, 0), (0, 0)))      # Laden ist ins Glas gemalt (Wand dahinter hat kein Loch)
    os.makedirs(os.path.join(out_dir, rel_dir), exist_ok=True)
    rel = rel_dir + "/linkhome.glb"
    oot_glb.write(os.path.join(out_dir, rel), groups, {"jpg": m0, "bark": mb, "floor": mf, "win_wood": mw, "win_glass": mg, "ceil": mb}, scale=S, offset=(0, 0, 0), name="linkhome")
    P = np.array([p for _, vs in tris for p, _, _ in vs], float)
    rmax = float(np.linalg.norm(P, axis=1).max()) * S
    pos = oot_pack.V((0, 0, 0)); pos = [round(pos[i] + offset[i], 3) for i in range(3)]
    return [{"m": rel, "p": pos, "q": oot_pack.Q180, "s": [1, 1, 1], "r": round(rmax, 2), "c": 1, "vis": 400, "t": ["shadow2"]}], \
        {"jpg": len(groups["jpg"]), "bark": len(groups["bark"]), "floor": len(groups["floor"]), "window": len(groups["win_wood"]) + len(groups["win_glass"])}
