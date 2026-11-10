"""Kokiri-Wald (spot04) im Valheim-Look, als eigenes Gebiet neben der Steppe (Versatz OFF_KF).

  Boden (Kollisions-Boeden)            -> Valheim-Gelaende (in dieselbe vmap wie die Steppe)
  Original-Boden-Dreiecke auf dem Boden -> fallen weg (Valheim-Gelaende + Gras)
  alles andere (Haeuser = Baumstuempfe, Zaeune, Baeume, Felswaende, Deku-Baum) -> Originalform mit Relief in
      Valheim-Material (Rinde / Holz / Laub / Stein), wie beim Wappen
Bach: Wasserkasten -> Valheims Wasser (Versatz so, dass er auf Valheims Wasserhoehe 30 m liegt)
"""
import os, copy, math, json, collections
import numpy as np
import oot_pack, oot_vpack
from oot_gfx import scene_collision

S = oot_pack.S
OFF_KF = (650.0, 6.0, -150.0)    # ganzer Wald ueber Valheims Meer (30 m): kein Valheim-Wasser im Kokiri-Wald (Original-Wasserflaechen bleiben Modell)

# Texturen, die nicht nach Farbe zugeordnet werden sollen (Adresse ohne Segment, hex)
GROUND_HEX = {"10618"}                       # Gras-Boden (spot04)
WATER_HEX = {"18a90"}
BARK_HEX = {"4ea8", "c708", "16108", "13f08", "2ff8", "16908", "11d08", "13108"}
CANOPY_HEX = {"66a8", "13708"}
MOSS_HEX = {"fa18", "12f08", "2bf8", "3bf8", "15308"}                        # Bach-Oberflaeche
LAST_PAINT = []
WATER_BED_KEY = ("kfwaterbed",)
BARK_MOSS_KEY = ("kfbarkmoss",)
LAST_KEEP_V = []
LAST_FLOOR_V = []
LAST_CEIL_V = []
LAST_SUPPORTS = []
FENCE_HEX = {"17108", "45f8"}
MOSS_REP = 250.0
TERRAIN_HEX = MOSS_HEX | {"10618", "13108"}   # Remaster: Felswaende, Grashaenge, Erdstufen -> Valheim-Gelaende (Steilhang = Valheims Fels), keine Polygone
VINE_HEX = {"11f08", "56a8"}                    # Wurzel-/Rankenbilder auf diesen Waenden: fallen mit weg (wuerden sonst schweben)
LAST_WALL_V = []
LOG_HEX = {"cf08"}                              # hohle Baumstaemme (Durchgaenge in den Verlorenen Wald / zur Bruecke)
LAST_TUBE_V = []
LAST_WATER_V = []
LAST_GROUND_V = []
# Schwert-Labyrinth (Moritz 09.11.): die Original-Felswaende bleiben sichtbar (gerade Waende, Kollision = Optik), mit Valheims
# Moosfels; Boden dort flach. Gebiet wie oot_kf_decor.MAZE (Valheim x0, x1, z0, z1).
MAZE_BOX = (604.0, 661.0, -251.0, -189.0)
MAZE_ROCK_KEY = ("kfmazerock",)
MAZE_COL_KEY = ("kfmazecol",)            # unsichtbare Kollision = Original-Kollisionswaende (beidseitig)
MAZE_FLOOR_OOT = 120.0      # Gangboden des Labyrinths (OoT-Einheiten)
MAZE_RAISE_OOT = 121.0      # Waende um 4 m hochziehen (Original 4 m: unsere Kamera sieht drueber; Moritz: "deutlich hoeher")
MAZE_RAISE_FROM = 230.0     # nur Wand-Oberkanten (Original 240-340) werden hochgezogen; Kisten-Plateau (180) und Wandfuesse bleiben
# Moritz 09.11.: "warum ist das Labyrinth direkt hinter Links Haus, wenn eh ein Ladebildschirm kommt?" - der Kriechgang ist ein
# Ortswechsel, also steht das Labyrinth jetzt an eigenem Ort (leere Ebene suedoestlich, hinter dem Waldrand nicht zu sehen).
# Hinter Links Haus bleibt der Wald wie im Remaster (Hecken = Gelaende-Huegel mit Baeumen).
MAZE_SHIFT = (190.0, 0.0, -312.0)                # Valheim-Meter; so, dass das vmap-Raster gleich bleibt (Rand 60 m)
MAZE_MIN_Y = 110.0                               # Labyrinth-Boeden liegen bei OoT-y 120-178, das Dorf daneben bei -80..100
MAZE_BOX_NEW = (MAZE_BOX[0] + MAZE_SHIFT[0], MAZE_BOX[1] + MAZE_SHIFT[0], MAZE_BOX[2] + MAZE_SHIFT[2], MAZE_BOX[3] + MAZE_SHIFT[2])
LAST_MAZE_V = []                                 # sichtbare Labyrinth-Waende (Valheim-Koordinaten) fuer die Deko                               # Wasserflaechen (Bach/Teich): darunter kein Valheim-Gras
PERIM_HEX = {"ea18", "e218"}          # Rand: aufgemalte Baumwand (Staemme/Kronen) -> unsichtbar (Kollision bleibt), echte Valheim-Baeume dahinter
LAST_PERIM = []
_KEEP, _DM, _EXTRA = [], {}, []
KF_TINT = {"4ea8": (0.6, 0.6, 0.47)}
KF_MAX_LUM = 86.0                     # hoechste mittlere Helligkeit (0..255) der Wald-Texturen
DECAL_PRIM_COMBINE = 5090756005724152      # Farbe = PRIM, Alpha = Bild * PRIM-Alpha (Wege, Lichtflecken)
KF_BASE = {
    "16108": "wood", "17108": "wood", "45f8": "wood", "d508": "wood", "16d08": "wood", "bf08": "wood",
    "15b08": "wood", "43f8": "wood", "cf08": "wood", "d908": "wood",
    "fa18": "leaf", "13108": "bark", "12f08": "leaf", "11d08": "bark", "2bf8": "leaf", "3bf8": "leaf", "15308": "leaf",   # Remaster: moosige Erdwaende statt Fels
    "18a90": "keep", "ea18": "keep", "f108": "keep", "f218": "keep", "19a90": "keep", "7d78": "keep", "19290": "keep",
    "1a290": "keep", "7b78": "keep", "1a490": "keep", "1ae90": "keep", "1ac90": "keep",
}


def V(p):
    v = oot_pack.V(p)
    return [v[0] + OFF_KF[0], v[1] + OFF_KF[1], v[2] + OFF_KF[2]]


def shift_oot(p):
    """OoT-Punkt -> derselbe Punkt im verlegten Labyrinth (V(z) = -150 - S*z)."""
    return (p[0] + MAZE_SHIFT[0] / S, p[1] + MAZE_SHIFT[1] / S, p[2] - MAZE_SHIFT[2] / S)


def shift_v(v):
    return [v[0] + MAZE_SHIFT[0], v[1] + MAZE_SHIFT[1], v[2] + MAZE_SHIFT[2]]


def in_box_v(v, box=None):
    b = box or MAZE_BOX
    return b[0] <= v[0] <= b[1] and b[2] <= v[2] <= b[3]


def maze_pos(v):
    """Valheim-Punkt (Truhe, Busch, Rubin, Felsbrocken, Kriechgang-Ausgang): liegt er im alten Labyrinth, an den neuen Ort."""
    return [round(x, 3) for x in shift_v(v)] if in_box_v(v) and v[1] >= V((0, MAZE_MIN_Y - 5, 0))[1] else list(v)


_MZ_FLOORS = None


def _maze_floors():
    global _MZ_FLOORS
    if _MZ_FLOORS is None:
        rom = os.path.join(os.environ.get("OOT_DECOMP", "/home/claude/oot"), "extracted", "gc-eu", "baserom")
        sb = open(os.path.join(rom, "spot04_scene"), "rb").read()
        v, polys = scene_collision(sb); Vv = np.array(v, float)
        fl = []
        for a, b, c, _ in polys:
            T = (Vv[a], Vv[b], Vv[c]); nn = np.cross(T[1] - T[0], T[2] - T[0]); ll = np.linalg.norm(nn)
            if ll > 0 and nn[1] / ll > 0.35:
                fl.append(T)
        _MZ_FLOORS = fl
    return _MZ_FLOORS


def maze_tri(P):
    """Gehoert das Original-Dreieck (OoT) zum Schwert-Labyrinth? Im alten Kasten und vor seiner Vorderseite (Waende) bzw.
    darunter (Boeden/Decken) liegt ein Labyrinth-Boden. So bleiben Links Haus und die Dorf-Seite der Hecken im Wald."""
    P = [np.asarray(p, float) for p in P]
    c = sum(P) / 3
    if not in_box_v(V(c)):
        return False
    nn = np.cross(P[1] - P[0], P[2] - P[0]); ll = np.linalg.norm(nn)
    q = c + nn / ll * 25.0 if ll > 0 and abs(nn[1] / ll) < 0.5 else c
    ys = [y for F in _maze_floors() for y in [_height_at(F, q)] if y is not None and y <= q[1] + 10]
    return bool(ys) and max(ys) >= MAZE_MIN_Y


def hexkey(k):
    return "%x" % (k[0] & 0xFFFFFF) if isinstance(k[0], int) else ""


def base_for(k, m):
    hx = hexkey(k)
    if hx in KF_BASE:
        return KF_BASE[hx]
    if m.rgba is None:
        return "keep"
    a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
    r, g, b = a[:, 0].mean(), a[:, 1].mean(), a[:, 2].mean()
    if g > r * 1.08 and g > b * 1.2:
        return "leaf"
    return "bark"


_BARK = {}


def bark_tile(moss=False):
    """Valheims Eichenrinde: sauberer Ausschnitt ohne Moos (Atlas 200..380 x 10..250), gespiegelt kachelbar.
    moss=True: dieselbe Rinde mit leichtem Moos (fuer nach oben zeigende Flaechen: Brauen, Schnurrbart, Aeste)."""
    if moss not in _BARK:
        from PIL import Image
        im = Image.open(os.path.join(oot_vpack.VTEX, "tex_Oak1_0.png")).convert("RGB").crop((205, 45, 375, 255))
        a = np.asarray(im, float)
        if moss:
            mo = np.asarray(Image.open(os.path.join(oot_vpack.VTEX, "tex_Oak1_0.png")).convert("RGB").crop((400, 170, 500, 250)).resize(im.size), float)
            rng = np.random.default_rng(3)
            from scipy import ndimage
            nz = ndimage.gaussian_filter(rng.random(a.shape[:2]), 9.0); nz = (nz - nz.min()) / (np.ptp(nz) + 1e-9)
            w = np.clip((nz - 0.35) * 2.2, 0, 0.85)[:, :, None]
            a = a * (1 - w) + mo * w
        # nahtlos kachelbar: mit der um eine halbe Kachel verschobenen Kopie ueberblenden (Rand -> Kopie, Mitte -> Original)
        H_, W_ = a.shape[:2]
        yy = np.abs(np.linspace(-1, 1, H_))[:, None]; xx = np.abs(np.linspace(-1, 1, W_))[None, :]
        m_ = np.clip((np.maximum(yy, xx) - 0.55) / 0.45, 0, 1)[:, :, None]
        t = a * (1 - m_) + np.roll(np.roll(a, H_ // 2, 0), W_ // 2, 1) * m_
        t = np.clip(t * 0.72, 0, 255).astype(np.uint8); h, w_ = t.shape[:2]      # dunkler (Wunsch)
        rgba = np.concatenate([t, np.full((h, w_, 1), 255, np.uint8)], 2)
        _BARK[moss] = (rgba.tobytes(), w_, h)
    return _BARK[moss]


def world_uv(vs, rep=70.0):
    """UVs aus der Weltlage (Rinde laeuft senkrecht, eine Wiederholung je ~2,3 m) statt der OoT-Kachel-UVs"""
    P = [np.array(p, float) for p, _, _ in vs]
    n = np.cross(P[1] - P[0], P[2] - P[0]); l = np.linalg.norm(n)
    n = n / l if l > 0 else np.array([0, 1.0, 0])
    if abs(n[1]) > 0.7:
        uv = [(p[0] / rep, p[2] / rep) for p in P]
    else:
        t = np.array([-n[2], 0.0, n[0]]); t /= (np.linalg.norm(t) or 1.0)
        uv = [(float(np.dot(p, t)) / rep, -p[1] / rep) for p in P]
    return [(p, uv[i], c) for i, (p, _, c) in enumerate(vs)]


def floor_grid(sb, cell=8.0):
    """Kollisions-Boeden -> Valheim-Dreiecke (fuer vmap) + Hoehenabfrage im OoT-Raum"""
    v, polys = scene_collision(sb); Vv = np.array(v, float)
    allf = []
    for a, b, c, _ in polys:
        A, B, C = Vv[a], Vv[b], Vv[c]
        n = np.cross(B - A, C - A); l = np.linalg.norm(n)
        if l > 0 and n[1] / l > 0.6:
            allf.append((A, B, C))
    # nur der unterste Boden ist Gelaende (Plattformen, Dachterrassen, Stege bleiben Bauwerk)
    tri_v, tri_o = [], []
    for T in allf:
        c = sum(T) / 3
        lower = False
        for U in allf:
            if U is T:
                continue
            y = _height_at(U, c)
            if y is not None and y < c[1] - 50:
                lower = True; break
        if not lower:
            tri_o.append(T); tri_v.append([V(T[0]), V(T[1]), V(T[2])])
    return np.array(tri_v, float), tri_o


def _height_at(T, c):
    A, B, C = T
    if not (min(A[0], B[0], C[0]) - 1 <= c[0] <= max(A[0], B[0], C[0]) + 1 and min(A[2], B[2], C[2]) - 1 <= c[2] <= max(A[2], B[2], C[2]) + 1):
        return None
    d = (B[2] - C[2]) * (A[0] - C[0]) + (C[0] - B[0]) * (A[2] - C[2])
    if abs(d) < 1e-6:
        return None
    l1 = ((B[2] - C[2]) * (c[0] - C[0]) + (C[0] - B[0]) * (c[2] - C[2])) / d
    l2 = ((C[2] - A[2]) * (c[0] - C[0]) + (A[0] - C[0]) * (c[2] - C[2])) / d
    l3 = 1 - l1 - l2
    if min(l1, l2, l3) < -0.02:
        return None
    return l1 * A[1] + l2 * B[1] + l3 * C[1]


def on_floor(P, floors):
    """liegt das Original-Dreieck (OoT) flach auf einem Kollisionsboden?"""
    c = sum(P) / 3
    for A, B, C in floors:
        x0 = min(A[0], B[0], C[0]); x1 = max(A[0], B[0], C[0]); z0 = min(A[2], B[2], C[2]); z1 = max(A[2], B[2], C[2])
        if not (x0 - 1 <= c[0] <= x1 + 1 and z0 - 1 <= c[2] <= z1 + 1):
            continue
        d = (B[2] - C[2]) * (A[0] - C[0]) + (C[0] - B[0]) * (A[2] - C[2])
        if abs(d) < 1e-6:
            continue
        l1 = ((B[2] - C[2]) * (c[0] - C[0]) + (C[0] - B[0]) * (c[2] - C[2])) / d
        l2 = ((C[2] - A[2]) * (c[0] - C[0]) + (A[0] - C[0]) * (c[2] - C[2])) / d
        l3 = 1 - l1 - l2
        if min(l1, l2, l3) < -0.02:
            continue
        y = l1 * A[1] + l2 * B[1] + l3 * C[1]
        if abs(c[1] - y) < 12:
            return True
    return False


def density(k, tris, m):
    du, dv = [], []
    for kk, vs in tris:
        if kk != k:
            continue
        P = [np.array(p, float) * S for p, _, _ in vs]; U = [np.array(uv) * [m.w, m.h] for _, uv, _ in vs]
        e1 = P[1] - P[0]; n = np.cross(e1, P[2] - P[0])
        if np.linalg.norm(n) < 1e-6 or np.linalg.norm(e1) < 1e-6:
            continue
        e1 /= np.linalg.norm(e1); e2 = np.cross(n / np.linalg.norm(n), e1)
        X = np.array([[np.dot(P[i] - P[0], e1), np.dot(P[i] - P[0], e2)] for i in (1, 2)])
        Y = np.array([U[i] - U[0] for i in (1, 2)])
        try:
            A = np.linalg.solve(X, Y).T
        except np.linalg.LinAlgError:
            continue
        du.append(np.linalg.norm(A[0])); dv.append(np.linalg.norm(A[1]))
        if len(du) > 40:
            break
    return (float(np.median(du)) if du else 10.0, float(np.median(dv)) if dv else 10.0)


def _write_tiles(out, keep, dm):
    st = oot_pack.write_tiles(keep, dm, os.path.join(out, "statics", "kf"), "statics/kf", "kf")
    for e in st:
        e["p"] = [round(e["p"][i] + OFF_KF[i], 3) for i in range(3)]
    return st


def retile(out, vmap_path, min_above=1.2):
    """Nach dem Gelaendebau: Erd-/Moos-Stufenwaende, die das Valheim-Gelaende fast ganz zudeckt, weglassen
    (sonst schaut nur ein harter Streifen heraus, von hinten unsichtbar). Hohe Felswaende bleiben."""
    import oot_kf_decor
    hgt, _, (x0, z0, cell, W, H) = oot_kf_decor.read_vmap(vmap_path)
    def ground(x, z):
        i, j = int((x - x0) / cell), int((z - z0) / cell)
        return float(hgt[j, i]) if 0 <= i < W and 0 <= j < H else None
    keep2, dropped, stripes = [], 0, 0
    # Kanten, an denen sichtbare Flaechen weitergehen (Plattform-Oberseite, Wurzel, Baumstamm): dort nichts wegnehmen
    def vk(p):
        return (round(p[0]), round(p[1]), round(p[2]))
    edge_n = collections.Counter()
    for k, vs in _KEEP:
        m_ = _DM.get(k)
        if m_ is None or getattr(m_, "hidden", False):
            continue
        q = [vk(p) for p, _, _ in vs]
        for a, b in ((0, 1), (1, 2), (0, 2)):
            edge_n[frozenset((q[a], q[b]))] += 1
    for k, vs in _KEEP:
        hx = hexkey(k) if isinstance(k, tuple) and k and isinstance(k[0], int) else ""
        m_ = _DM.get(k)
        if m_ is not None and not getattr(m_, "hidden", False) and not m_.alpha and not m_.xlu and hx not in FENCE_HEX | LOG_HEX | CANOPY_HEX | WATER_HEX:
            # Wandreste, die das Gelaende fast ganz zudeckt (nur ein harter Streifen schaut heraus): weg, egal welche Textur
            P = [np.array(p, float) for p, _, _ in vs]
            n = np.cross(P[1] - P[0], P[2] - P[0]); l = np.linalg.norm(n)
            if l > 0 and abs(n[1] / l) < 0.6:
                Vw = [V(p) for p in P] + [V(sum(P) / 3)] + [V((P[a] + P[b]) / 2) for a, b in ((0, 1), (1, 2), (0, 2))]
                gs = [ground(v[0], v[2]) for v in Vw]
                if all(g is not None for g in gs):
                    top = max(v[1] for v in Vw[:3]); bot = min(v[1] for v in Vw[:3])
                    o = sorted(range(3), key=lambda i: -P[i][1])
                    te = frozenset((vk(vs[o[0]][0]), vk(vs[o[1]][0])))
                    if top - max(gs) < 0.55 and bot < min(gs) + 0.3 and edge_n[te] <= 1:
                        stripes += 1
                        continue
        if hx in MOSS_HEX or hx in GROUND_HEX or (hx in KF_BASE and KF_BASE[hx] == "leaf"):
            P = [np.array(p, float) for p, _, _ in vs]
            n = np.cross(P[1] - P[0], P[2] - P[0]); l = np.linalg.norm(n)
            if l > 0 and abs(n[1] / l) < 0.6:
                Vw = [V(p) for p in P] + [V(sum(P) / 3)]
                gs = [ground(v[0], v[2]) for v in Vw]
                if all(g is not None for g in gs):
                    top = max(v[1] for v in Vw[:3])
                    if top - max(gs) < min_above:
                        dropped += 1
                        continue
        keep2.append((k, vs))
    st = _write_tiles(out, keep2, _DM) + _EXTRA
    json.dump(st, open(os.path.join(out, "statics_kf.json"), "w"), separators=(",", ":"))
    return {"walls_dropped": dropped, "stripes_dropped": stripes, "kept": len(keep2)}


def build(root, out):
    rom = os.path.join(root, "extracted", "gc-eu", "baserom")
    sb, tris, mats = oot_pack.scene_tris(rom, "spot04", "field")
    floorV, floorO = floor_grid(sb)
    keep, dropped = [], 0
    extra_floor, paint, walls, obj_tops, tubes, water_v = [], [], [], [], [], []
    maze_v = []
    def shift_from(idx):
        a, b, c_, d = idx
        for i in range(a, len(keep)):
            kk, vv = keep[i]; keep[i] = (kk, [(shift_oot(p), uv, col) for p, uv, col in vv])
        for i in range(b, len(extra_floor)):
            extra_floor[i] = [shift_v(q) for q in extra_floor[i]]
        for i in range(c_, len(paint)):
            pv, puv, pa = paint[i]; paint[i] = ([shift_v(q) for q in pv], puv, pa)
        for i in range(d, len(maze_v)):
            maze_v[i] = [shift_v(q) for q in maze_v[i]]
    mz, mz_idx, n_mz = False, None, 0
    for k, vs in tris:
        if mz:
            shift_from(mz_idx)                # Eintraege des vorigen Labyrinth-Dreiecks an den neuen Ort
        mz_idx = (len(keep), len(extra_floor), len(paint), len(maze_v))
        P = [np.array(p, float) for p, _, _ in vs]
        mz = maze_tri(P); n_mz += mz
        n = np.cross(P[1] - P[0], P[2] - P[0]); l = np.linalg.norm(n)
        up = l > 0 and n[1] / l > 0.7
        if hexkey(k) in PERIM_HEX and mz:
            dropped += 1                      # aufgemalte Baumwand: im Labyrinth keine unsichtbare (schraege) Wand
            continue
        m = mats[k]
        # Remaster: Wege werden in Valheims Boden gemalt (Erde), die Flecken-Modelle fallen weg
        if up and m.rgba is not None and m.combine == DECAL_PRIM_COMBINE:
            a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(m.h, m.w, 4)[:, :, 0].astype(float) * m.prim[3] / 255.0 / 255.0
            paint.append(([V(p) for p, _, _ in vs], [uv for _, uv, _ in vs], a))
            dropped += 1
            continue
        # Remaster: alle begehbaren Gras-Flaechen (auch Absaetze) werden Valheim-Gelaende (Gras, Halme)
        if l > 0 and n[1] / l > 0.35 and hexkey(k) in GROUND_HEX:      # auch Grashaenge
            lo = min((y for y in (_height_at(T, sum(P) / 3) for T in floorO) if y is not None), default=None)
            if lo is not None and sum(p[1] for p in P) / 3 - lo < 25:      # nur wo darunter kein Boden liegt (Tunnel/Ueberhaenge bleiben frei)
                extra_floor.append([V(p) for p in P])
                dropped += 1
                continue
        if up and base_for(k, m) != "keep" and on_floor(P, floorO):
            if base_for(k, m) == "leaf":
                dropped += 1          # Laub-/Grasboden: Valheim-Gelaende ersetzt ihn
                continue
            obj_tops.append(P)        # Holz-/Rindenoberseite (Plattform, Stumpf, Deku-Wurzel, Stamm-Boden): bleibt Modell, Gelaende darunter
        if hexkey(k) in LOG_HEX:
            tubes.append([V(p) for p in P])
        if hexkey(k) in TERRAIN_HEX:
            walls.append([V(p) for p in P])
            if mz:
                if hexkey(k) in GROUND_HEX and l > 0 and n[1] / l > 0.35:
                    # Rasen-Oberseiten der Heckenbloecke: grosse schraege Flaechen (160-280) quer ueber den Gaengen, in OoT
                    # von unten unsichtbar (einseitig). Bei uns sah man sie von der Kiste aus und konnte draufspringen (Moritz 09.11.)
                    dropped += 1
                    continue
                # Labyrinth: Felswand und Grasoberseiten der Waende (ohne Boden darunter, sonst waeren sie oben Gelaende
                # geworden) als sichtbares Modell mit Valheim-Moosfels (Valheims Fels-Shader legt oben Moos drauf).
                # Alles ueber 1 m ueber dem Gangboden wird um 4 m hochgezogen (gerade Waende bleiben gerade, Kollision waechst mit).
                vs = [((p[0], p[1] + MAZE_RAISE_OOT if p[1] >= MAZE_RAISE_FROM else p[1], p[2]), uv, col) for p, uv, col in vs]
                keep.append((MAZE_ROCK_KEY, world_uv(vs, rep=MOSS_REP)))
                maze_v.append([V(p) for p, _, _ in vs])
                continue
            if hexkey(k) in MOSS_HEX:
                keep.append((k, vs))       # Felswand bleibt als unsichtbare Kollision (Spielraum wie im Original, Hang beginnt am Wandfuss)
            else:
                dropped += 1
            continue
        if hexkey(k) in VINE_HEX:
            dropped += 1
            continue
        if hexkey(k) in BARK_HEX:
            vs = world_uv(vs)
        elif hexkey(k) in MOSS_HEX:
            vs = world_uv(vs, rep=MOSS_REP)          # Valheims Felstextur ist fuer grosse Felsen gemalt: eine Kachel ~ 8 m (sonst Schuppenmuster)
        if hexkey(k) in BARK_HEX and l > 0 and n[1] / l > 0.35:
            keep.append((BARK_MOSS_KEY, vs)); continue        # nach oben zeigende Rinde (Brauen, Schnurrbart) leicht bemoost
        keep.append((k, vs))
        if m.alpha and m.rgba is not None and not up:
            # OoT zeichnet Zaeune, Ranken, Blaetter beidseitig -> Rueckseite dazu (sonst von hinten unsichtbar)
            keep.append((k, [vs[0], vs[2], vs[1]]))
        if hexkey(k) in WATER_HEX:
            water_v.append([V(p) for p in P])
            # Remaster-Wasser: darunter eine dunkle Bachgrund-Schicht, damit es nicht so durchsichtig wirkt
            keep.append((WATER_BED_KEY, [((p[0], p[1] - 14, p[2]), uv, c) for p, uv, c in vs]))
    if mz:
        shift_from(mz_idx)
    # Labyrinth-Kollision: genau die Original-Kollisionswaende (beidseitig, wie die sichtbaren Waende hochgezogen). Die
    # sichtbaren Waende kollidieren auch, sind aber einseitig/teils anders geschnitten -> man rannte stellenweise durch.
    cv_, cpolys = scene_collision(sb); CV = np.array(cv_, float)
    ncol = 0
    for a, b, c_, _ in cpolys:
        Pc = [CV[a], CV[b], CV[c_]]
        if not maze_tri(Pc):
            continue
        nn = np.cross(Pc[1] - Pc[0], Pc[2] - Pc[0]); ll = np.linalg.norm(nn)
        if ll == 0 or abs(nn[1] / ll) > 0.5:
            continue
        Pc = [shift_oot((p[0], p[1] + MAZE_RAISE_OOT if p[1] >= MAZE_RAISE_FROM else p[1], p[2])) for p in Pc]
        tri = [(tuple(float(x) for x in p), (0.0, 0.0), (255, 255, 255, 255)) for p in Pc]
        keep.append((MAZE_COL_KEY, tri)); keep.append((MAZE_COL_KEY, [tri[0], tri[2], tri[1]])); ncol += 1
    global LAST_PAINT, LAST_KEEP_V, LAST_FLOOR_V, LAST_CEIL_V, LAST_SUPPORTS, LAST_WALL_V
    LAST_WALL_V = walls
    LAST_CEIL_V = []
    # Zaun-Fusspunkte: das Gelaende muss sie tragen (sonst schweben Zaeune auf Absaetzen)
    LAST_SUPPORTS = []
    for k, vs in tris:
        if hexkey(k) in FENCE_HEX:
            ys = [p[1] for p, _, _ in vs]; yb = min(ys)
            low = [np.array(V(p)) for p, _, _ in vs if p[1] - yb < 1.0]
            for a in low:                       # Fusspunkte und die ganze Unterkante dazwischen (alle 0,5 m)
                for b in low:
                    L = np.linalg.norm(b - a); nseg = max(1, int(L / 0.5))
                    for t in np.linspace(0, 1, nseg + 1):
                        q = maze_pos(list(a + (b - a) * t)); LAST_SUPPORTS.append((q[0], q[2], q[1]))
    for k, vs in tris:
        P = [np.array(p, float) for p, _, _ in vs]
        n = np.cross(P[1] - P[0], P[2] - P[0]); l = np.linalg.norm(n)
        if l > 0 and n[1] / l < -0.7:
            LAST_CEIL_V.append([(shift_v(V(p)) if maze_tri(P) else V(p)) for p in P])
    LAST_PAINT = paint
    global LAST_PERIM
    LAST_PERIM = []
    for k, vs in keep:
        if hexkey(k) in PERIM_HEX:
            P = sorted([np.array(p, float) for p, _, _ in vs], key=lambda q: q[1])
            if abs(P[0][1] - P[1][1]) < 40 and np.hypot(*(P[0] - P[1])[[0, 2]]) > 20:
                LAST_PERIM.append((V(P[0]), V(P[1])))
    LAST_KEEP_V = [[V(p) for p, _, _ in vs] for k, vs in keep if hexkey(k) not in PERIM_HEX]
    if obj_tops:
        # Boeden, die nur die Oberseite eines Modells sind (Plattform im Teich, Stumpf, Wurzel, Stamm): kein Gelaende dort
        # (sonst hebt sich das Gelaende bis zur Oberseite und frisst die Seitenwaende bzw. die Flaechen fehlen)
        keepf = [not any(_height_at(tuple(T), sum(F) / 3) is not None and abs(_height_at(tuple(T), sum(F) / 3) - sum(F)[1] / 3) < 12 for T in obj_tops)
                 for F in floorO]
        floorV = np.asarray(floorV)[np.array(keepf, bool)] if len(floorO) else floorV
        floorO = [F for F, kf_ in zip(floorO, keepf) if kf_]
    if len(floorO):
        floorV = np.array([[shift_v(q) for q in T] if maze_tri(F) else list(map(list, T)) for T, F in zip(np.asarray(floorV), floorO)], float)
    if extra_floor:
        floorV = np.concatenate([floorV, np.array(extra_floor, float)], 0)
    LAST_FLOOR_V = floorV
    global LAST_TUBE_V, LAST_WATER_V, LAST_GROUND_V, LAST_MAZE_V
    LAST_MAZE_V = maze_v
    LAST_GROUND_V = extra_floor                  # Rasen-Flaechen des Originals (begehbar): im Labyrinth = die Gaenge
    LAST_TUBE_V = tubes
    LAST_WATER_V = water_v
    dm, bases = {}, collections.Counter()
    wk = next((k for k in mats if hexkey(k) in WATER_HEX), None)
    if wk is not None:
        bed = copy.copy(mats[wk]); a = np.frombuffer(bytes(bed.rgba), np.uint8).reshape(-1, 4).astype(float)
        a[:, :3] = a[:, :3] * np.array([0.35, 0.5, 0.48]); a[:, 3] = 255
        bed.rgba = a.astype(np.uint8).tobytes(); bed.alpha = False; bed.xlu = False; bed.key = WATER_BED_KEY
        dm[WATER_BED_KEY] = bed
    if any(k == MAZE_COL_KEY for k, _ in keep):
        mc = copy.copy(next(m_ for k_, m_ in mats.items() if hexkey(k_) in MOSS_HEX))
        mc.hidden = True; mc.alpha = False; mc.xlu = False; mc.key = MAZE_COL_KEY; mc.combine = None
        dm[MAZE_COL_KEY] = mc
    if any(k == MAZE_ROCK_KEY for k, _ in keep):
        mr = copy.copy(next(m_ for k_, m_ in mats.items() if hexkey(k_) in MOSS_HEX))
        mr.valheim = "rock4_forest"; mr.alpha = False; mr.xlu = False; mr.wrap_s = mr.wrap_t = 10497; mr.key = MAZE_ROCK_KEY
        mr.combine = None; mr.matte = True; mr.hidden = False
        dm[MAZE_ROCK_KEY] = mr
    if any(k == BARK_MOSS_KEY for k, _ in keep):
        bm = copy.copy(next(iter(mats.values()))); bm.rgba, bm.w, bm.h = bark_tile(True)
        bm.alpha = False; bm.xlu = False; bm.wrap_s = bm.wrap_t = 10497; bm.key = BARK_MOSS_KEY; bm.combine = None
        bm.valheim = None; bm.matte = True
        dm[BARK_MOSS_KEY] = bm
    for k, _ in keep:
        if k in dm:
            continue
        m = copy.copy(mats[k]); b = base_for(k, m); bases[b] += 1
        if hexkey(k) in WATER_HEX:
            m.valheim = "@water"          # Remaster: Valheims Wasser-Material auf der Original-Wasserflaeche
        if hexkey(k) in BARK_HEX:
            # Remaster: Valheims Eichenrinde (Ausschnitt aus dem Atlas, Moos raus, gespiegelt kachelbar), Welt-UVs
            m.rgba, m.w, m.h = bark_tile(); m.alpha = False; m.wrap_s = m.wrap_t = 10497; m.key = ("kfbark",); b = "keep"; m.combine = None; m.matte = True
        if hexkey(k) in CANOPY_HEX:
            m.valheim = "Beech1:0"        # Remaster: echtes Blattwerk in den Kronen
        if hexkey(k) in PERIM_HEX:
            m.hidden = True
        if hexkey(k) in MOSS_HEX:
            m.hidden = True               # Remaster: die Felswand ist jetzt Valheim-Gelaende (Steilhang mit Valheims Fels), hier nur noch Kollision
            b = "keep"
        if b == "keep" and m.rgba is not None and m.combine == DECAL_PRIM_COMBINE:
            # Weg-/Schatten-Flecken: Farbe kommt im Original aus der PRIM-Farbe, das Graustufenbild ist nur Alpha
            a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
            dc = np.empty_like(a); dc[:, :3] = np.array(m.prim[:3], float); dc[:, 3] = a[:, 0] * m.prim[3] / 255.0
            m.rgba = dc.astype(np.uint8).tobytes(); m.alpha = True; m.key = ("kfdecal",) + tuple(k)
        if b != "keep" and m.rgba is not None:
            m.rgba, m.w, m.h = oot_vpack.stone_blend(m, density(k, keep, m), base=b, strong=False, f=2)
            m.key = ("kfblend",) + tuple(k)
            if hexkey(k) in KF_TINT:      # Baumrinde (Deku, Baumhaeuser): im Original grau-oliv, sonst leuchtet sie in der Sonne gelb
                a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float); a[:, :3] *= np.array(KF_TINT[hexkey(k)])
                m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
        if not getattr(m, "valheim", None) and not getattr(m, "hidden", False) and m.rgba is not None and not m.xlu:
            # Remaster: alles matt (Valheims Bau-Shader glaenzt sonst nass in der Sonne) und nichts heller als Valheims eigene
            # Texturen (helle Moos-Pfeiler, gelbe Buesche leuchteten im Sonnenlicht)
            m.matte = True
            a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
            w = a[:, 3] > 128
            if w.any():
                L = float((a[w, :3] @ np.array([0.3, 0.59, 0.11])).mean())
                if L > KF_MAX_LUM:
                    g = (a[:, :3] @ np.array([0.3, 0.59, 0.11]))[:, None]
                    a[:, :3] = (g + (a[:, :3] - g) * 0.85) * (KF_MAX_LUM / L)
                    m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes(); m.key = tuple(m.key) + ("dim",)
        dm[k] = m
    global _KEEP, _DM, _EXTRA
    _KEEP, _DM = keep, dm
    st = _write_tiles(out, keep, dm)
    # Deku-Baum-Mund (Bg_Treemouth, object_spot04_objects gDekuTreeMouthDL) mit Rinden-Relief
    mt, mm = oot_pack.object_tris(rom, "object_spot04_objects", 0x060009D0)
    mm2 = {}
    for k, m in mm.items():
        m = copy.copy(m)
        if m.rgba is not None:
            m.rgba, m.w, m.h = oot_vpack.stone_blend(m, density(k, mt, m), base="bark", strong=False, f=2)
            a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float); a[:, :3] *= np.array([0.55, 0.62, 0.42])   # im Original oliv und tief im Schatten
            m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
            m.key = ("kfmouth",) + tuple(k)
        mm2[k] = m
    e = oot_pack.write_single(mt, mm2, os.path.join(out, "statics", "kf"), "statics/kf/deku_mouth.glb", (3882, -171, -1161), "deku_mouth")
    e["p"] = [round(e["p"][i] + OFF_KF[i], 3) for i in range(3)]; e["vis"] = 900
    st.append(e); _EXTRA = [e]
    json.dump(st, open(os.path.join(out, "statics_kf.json"), "w"), separators=(",", ":"))
    wb = oot_vpack.water_boxes(sb)
    return floorV, wb, {"tris": len(tris), "dropped_ground": dropped, "kept": len(keep), "tiles": len(st), "bases": dict(bases)}


if __name__ == "__main__":
    import sys
    fv, wb, rep = build(sys.argv[1], sys.argv[2])
    print(json.dumps(rep), fv.shape, wb)
