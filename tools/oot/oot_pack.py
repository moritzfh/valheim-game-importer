"""OoT -> Valheim-Spielpaket (packs/oot): Hyrule-Steppe als feste Modelle, Gelaende darunter, Albtraum-Cutscene.

  python oot_pack.py <decomp-root> <pack-out> [--version gc-eu]

Koordinaten (pack.json -> transform):
  Valheim x = OoT x * S, y = OoT y * S + Y0, z = -OoT z * S   (OoT -z = Norden -> Valheim +z = Norden)
Die GLBs bleiben in OoT-Achsen (glTF), der Lader spiegelt x; mit 180 Grad um y ergibt das genau die Abbildung oben.
"""
import os, sys, json, math, gzip, struct, argparse, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from oot_gfx import Segs, Gfx, room_dlists, scene_collision
import oot_glb, oot_cutscene

S = 0.033          # Meter pro OoT-Einheit (erwachsener Link ~56 Einheiten ~ 1,85 m)
Y0 = 70.0          # Valheim-Hoehe von OoT y=0 (Steppe liegt dann bei 34..94 m, ueber Valheims Wasser bei 30 m)
TILE = 640         # Kachelgroesse in OoT-Einheiten (~21 m)
Q180 = [0.0, 1.0, 0.0, 0.0]


def V(p):
    return [round(p[0] * S, 3), round(p[1] * S + Y0, 3), round(-p[2] * S, 3)]


def vdir(d):
    return [d[0], d[1], -d[2]]


def yaw_v(binang):
    """OoT-Blickrichtung (Binaerwinkel, 0 = +z) -> Valheim-Gierwinkel in Grad (0 = +z, rechtsrum)."""
    a = binang * 2 * math.pi / 65536.0
    return round(math.degrees(math.atan2(math.sin(a), -math.cos(a))), 2)


def quat_look(fwd, up):
    """Unity Quaternion.LookRotation(fwd, up) -> [x, y, z, w]."""
    f = np.array(fwd, float); f /= np.linalg.norm(f)
    u = np.array(up, float)
    r = np.cross(u, f); r /= np.linalg.norm(r)
    u = np.cross(f, r)
    m = np.array([r, u, f]).T          # Spalten = lokale x, y, z
    t = m[0, 0] + m[1, 1] + m[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2
        w, x, y, z = 0.25 * s, (m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = math.sqrt(1.0 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        w, x, y, z = (m[2, 1] - m[1, 2]) / s, 0.25 * s, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s
    elif m[1, 1] > m[2, 2]:
        s = math.sqrt(1.0 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        w, x, y, z = (m[0, 2] - m[2, 0]) / s, (m[0, 1] + m[1, 0]) / s, 0.25 * s, (m[1, 2] + m[2, 1]) / s
    else:
        s = math.sqrt(1.0 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        w, x, y, z = (m[1, 0] - m[0, 1]) / s, (m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, 0.25 * s
    return [round(x, 5), round(y, 5), round(z, 5), round(w, 5)]


def oot_up(eye, at, roll_deg):
    """Up-Vektor wie Camera_CalcUpFromPitchYawRoll: Grund-Up um die Blickachse um -roll gedreht (OoT-Achsen)."""
    f = np.array(at, float) - np.array(eye, float); f /= np.linalg.norm(f)
    base = np.array([0.0, 1.0, 0.0]) - f * f[1]
    base /= np.linalg.norm(base)
    th = math.radians(-roll_deg)
    # Rodrigues
    return base * math.cos(th) + np.cross(f, base) * math.sin(th) + f * np.dot(f, base) * (1 - math.cos(th))


# ---------------- Geometrie ----------------
def scene_tris(rom, scene, keep="field"):
    sb = open(os.path.join(rom, scene + "_scene"), "rb").read()
    gk = open(os.path.join(rom, "gameplay_keep"), "rb").read()
    kk = open(os.path.join(rom, "gameplay_%s_keep" % keep), "rb").read()
    tris, mats = [], {}
    i = 0
    while os.path.exists(os.path.join(rom, "%s_room_%d" % (scene, i))):
        rb = open(os.path.join(rom, "%s_room_%d" % (scene, i)), "rb").read()
        g = Gfx(Segs({2: sb, 3: rb, 4: gk, 5: kk}))
        for opa, xlu in room_dlists(rb):
            if opa:
                g.reset(); g.run(opa, xlu=False)
            if xlu:
                g.reset(); g.run(xlu, xlu=True)
        tris += g.tris; mats.update(g.mats)
        i += 1
    return sb, tris, mats


def object_tris(rom, obj, dl, extra=None):
    ob = open(os.path.join(rom, obj), "rb").read()
    gk = open(os.path.join(rom, "gameplay_keep"), "rb").read()
    m = {6: ob, 4: gk}
    m.update(extra or {})
    g = Gfx(Segs(m))
    g.reset(); g.run(dl)
    return g.tris, g.mats


def write_tiles(tris, mats, out_dir, rel_dir, prefix, tile=TILE):
    cells = collections.defaultdict(list)
    for k, vs in tris:
        cx = sum(v[0][0] for v in vs) / 3.0
        cz = sum(v[0][2] for v in vs) / 3.0
        cells[(int(math.floor(cx / tile)), int(math.floor(cz / tile)))].append((k, vs))
    statics = []
    os.makedirs(out_dir, exist_ok=True)
    for (i, j), lst in sorted(cells.items()):
        c = ((i + 0.5) * tile, 0.0, (j + 0.5) * tile)
        groups = collections.OrderedDict()
        rmax = 0.0
        for k, vs in lst:
            groups.setdefault(k, []).append(vs)
            for p, *_ in vs:
                rmax = max(rmax, math.sqrt((p[0] - c[0]) ** 2 + (p[1] - c[1]) ** 2 + (p[2] - c[2]) ** 2))
        name = "%s_%d_%d.glb" % (prefix, i, j)
        oot_glb.write(os.path.join(out_dir, name), groups, mats, scale=S, offset=c, name=name[:-4])
        statics.append({"m": rel_dir + "/" + name, "p": V(c), "q": Q180, "s": [1, 1, 1], "r": round(rmax * S, 2), "c": 1,
                        "vis": 900})
    return statics


def write_single(tris, mats, out_dir, rel, origin, name, extra=None):
    groups = collections.OrderedDict()
    for k, vs in tris:
        groups.setdefault(k, []).append(vs)
    oot_glb.write(os.path.join(out_dir, os.path.basename(rel)), groups, mats, scale=S, offset=(0, 0, 0), name=name)
    rmax = max(math.sqrt(sum(c * c for c in p)) for _, vs in tris for p, *_ in vs)
    e = {"m": rel, "p": V(origin), "q": Q180, "s": [1, 1, 1], "r": round(rmax * S, 2), "c": 1}
    e.update(extra or {})
    return e


# ---------------- Gelaende ----------------
def make_vmap(sb, out, cell=2.0, margin=80.0, sink=0.35):
    verts, polys = scene_collision(sb)
    V3 = np.array(verts, float)
    vx, vy, vz = V3[:, 0] * S, V3[:, 1] * S + Y0, -V3[:, 2] * S
    x0, x1 = vx.min() - margin, vx.max() + margin
    z0, z1 = vz.min() - margin, vz.max() + margin
    W = int((x1 - x0) / cell) + 1
    H = int((z1 - z0) / cell) + 1
    hm = np.full((H, W), np.nan)
    for a, b, c, _t in polys:
        if max(a, b, c) >= len(V3):
            continue
        P = np.array([[vx[a], vy[a], vz[a]], [vx[b], vy[b], vz[b]], [vx[c], vy[c], vz[c]]])
        n = np.cross(P[1] - P[0], P[2] - P[0])
        ln = np.linalg.norm(n)
        if ln == 0 or abs(n[1]) / ln < 0.35:      # nur begehbare Flaechen (Boden), keine Waende
            continue
        i0 = max(0, int((P[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((P[:, 0].max() - x0) / cell) + 1)
        j0 = max(0, int((P[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((P[:, 2].max() - z0) / cell) + 1)
        if i1 < i0 or j1 < j0:
            continue
        xs = x0 + np.arange(i0, i1 + 1) * cell
        zs = z0 + np.arange(j0, j1 + 1) * cell
        X, Z = np.meshgrid(xs, zs)
        # baryzentrisch in xz
        (ax, az), (bx, bz), (cx, cz) = P[0, [0, 2]], P[1, [0, 2]], P[2, [0, 2]]
        d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
        if abs(d) < 1e-9:
            continue
        l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
        l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
        l3 = 1 - l1 - l2
        inside = (l1 >= -0.02) & (l2 >= -0.02) & (l3 >= -0.02)
        Y = l1 * P[0, 1] + l2 * P[1, 1] + l3 * P[2, 1]
        sub = hm[j0:j1 + 1, i0:i1 + 1]
        upd = inside & (np.isnan(sub) | (Y < sub))     # niedrigste Bodenflaeche (keine Mauerkronen)
        sub[upd] = Y[upd]
    known = ~np.isnan(hm)
    from scipy import ndimage
    idx = ndimage.distance_transform_edt(~known, return_distances=False, return_indices=True)
    dist = ndimage.distance_transform_edt(~known) * cell
    filled = hm[idx[0], idx[1]]
    # ausserhalb der Steppe: Rand halten, dann sanft absenken (nicht ins Meer: Valheim-Wasser liegt bei 30 m)
    h = filled - sink - np.clip(dist - 2.0, 0, None) * 0.6   # ausserhalb schnell weg (nichts ragt ins Bild)
    h = np.maximum(h, 31.0)
    hmin = float(h.min()); hstep = float(max(h.max() - hmin, 1.0) / 65535.0)
    q = np.clip(np.round((h - hmin) / hstep), 0, 65535).astype("<u2")
    bcell = 8.0
    BW, BH = int((x1 - x0) / bcell) + 1, int((z1 - z0) / bcell) + 1
    bio = np.full((BH, BW), 1, np.uint8)            # Wiesen
    forest = np.full((BH, BW), 255, np.uint8)       # Waldfaktor 2.55 = kein Wald
    buf = bytearray(b"VMAP" + struct.pack("<iiifffff", 1, W, H, x0, z0, cell, hmin, hstep))
    buf += q.tobytes()
    buf += struct.pack("<iifff", BW, BH, x0, z0, bcell) + bio.tobytes() + forest.tobytes()
    with gzip.open(out, "wb", compresslevel=6) as f:
        f.write(bytes(buf))
    return {"W": W, "H": H, "h": [round(hmin, 1), round(float(h.max()), 1)], "covered": round(float(known.mean()), 3)}


# ---------------- Cutscene ----------------
def track_from_cs(cs_path, fps=20.0):
    cs = oot_cutscene.structure(oot_cutscene.parse(open(cs_path).read()))
    end = cs["destination"]["start"] if cs["destination"] else cs["frames"]
    cam = oot_cutscene.camera(cs, end)
    first = next(i for i, c in enumerate(cam) if c)
    for i in range(first):
        cam[i] = cam[first]
    pos, rot, fov = [], [], []
    for c in cam:
        e, a = c["eye"], c["at"]
        up = oot_up(e, a, c["roll"])
        pos += V(e)
        rot += quat_look(vdir(np.array(a) - np.array(e)), vdir(up))
        fov.append(round(c["fov"], 3))
    cuts = oot_cutscene.cuts(cam)
    # Blenden: FILL_IN = Bild wird schwarz, FILL_OUT = Bild kommt aus Schwarz
    fades = []
    for t in sorted(cs["transitions"], key=lambda t: t["start"]):
        ty = str(t["type"])
        d = max(1, t["end"] - t["start"]) / fps
        if ty.endswith("FILL_IN"):
            fd = {"t": round(t["start"] / fps, 3), "in": False, "dur": round(d, 3)}
            if "GRAY" in ty:
                fd["col"] = [0.627, 0.627, 0.627]                  # z_demo.c: screenFillColor 160,160,160
            elif "WHITE" in ty:
                fd["col"] = [1.0, 1.0, 1.0]
            fades.append(fd)
        elif ty.endswith("FILL_OUT"):
            fades.append({"t": round(t["start"] / fps, 3), "in": True, "dur": round(d, 3)})
    return cs, cam, {"fps": fps, "dur": round(end / fps, 3), "fov_kind": "v", "time": 0.5,
                     "cam": {"pos": pos, "rot": rot, "fov": fov}, "cuts": cuts, "fades": fades,
                     "actors": {}, "lines": [], "hide_terrain": [], "clip": []}


def actor_path(cues, n, spawn, yaw_fixed=None, yaw_from_cue=False, lift=0.0):
    """Position je Bild wie En_Viewer_UpdatePosition / Player-Cue: im Cue-Fenster linear, sonst letzter Stand."""
    pos, yaw = [], []
    cur = list(spawn); cy = yaw_fixed if yaw_fixed is not None else 0
    for f in range(n):
        c = None
        for q in cues:
            if q["start"] <= f < q["end"]:
                c = q
        if c is not None:
            cur = oot_cutscene.lerp_pos(c, f)
            if yaw_from_cue:
                cy = c["rot"][1]
        p = V(cur); p[1] = round(p[1] + lift, 3)
        pos += p
        yaw.append(yaw_v(cy))
    return pos, yaw


def build(root, out, version="gc-eu"):
    rom = os.path.join(root, "extracted", version, "baserom")
    assets = os.path.join(root, "extracted", version, "assets")
    os.makedirs(out, exist_ok=True)
    report = {}
    # Steppe
    sb, tris, mats = scene_tris(rom, "spot00", "field")
    st = write_tiles(tris, mats, os.path.join(out, "statics", "spot00"), "statics/spot00", "spot00")
    # Zugbruecke (unten) + Ketten
    bt, bm = object_tris(rom, "object_spot00_objects", 0x06000430)
    st.append(write_single(bt, bm, os.path.join(out, "statics", "spot00"), "statics/spot00/drawbridge.glb",
                           (0, -10, 670), "drawbridge", {"t": ["drawbridge"]}))
    json.dump(st, open(os.path.join(out, "statics_spot00.json"), "w"), separators=(",", ":"))
    report["statics"] = len(st)
    report["vmap"] = make_vmap(sb, os.path.join(out, "oot.vmap"))
    # Albtraum-Cutscene
    csp = os.path.join(assets, "scenes", "overworld", "spot00", "gHyruleFieldIntroNightmareCs.csdata.inc.c")
    cs, cam, tr = track_from_cs(csp)
    n = len(cam)
    ch0 = cs["actors"].get("ch0_2", [])
    ch1 = cs["actors"].get("ch1_1", [])
    # Reiter sitzen auf dem Pferd (vorerst Valheim-Lox als Pferd), Hoehe Sattel ~1,3 m
    zp, zy = actor_path(ch0, n, (2313, -11, 2013), yaw_fixed=0)
    gp, gy = actor_path(ch1, n, (2361, -9, 2062), yaw_fixed=0)
    lp, ly = actor_path(cs["player"], n, (-1, 0, 1348), yaw_from_cue=True)

    def lifted(p, dy, dz=0.0):
        q = list(p)
        for i in range(0, len(q), 3):
            q[i + 1] = round(q[i + 1] + dy, 3)
            q[i + 2] = round(q[i + 2] + dz, 3)
        return q
    tr["actors"] = {
        "link": {"step": 1, "pos": lp, "yaw": ly, "pose": [0] * n, "scale": 0.7},
        "impa": {"step": 1, "pos": lifted(zp, 1.25, 0.25), "yaw": zy, "pose": [1] * n},
        "zelda": {"step": 1, "pos": lifted(zp, 1.25, -0.45), "yaw": zy, "pose": [1] * n, "scale": 0.66},
        "ganondorf": {"step": 1, "pos": lifted(gp, 1.25), "yaw": gy, "pose": [1] * n, "scale": 1.12},
    }

    def prop(p, y, prefab, scale):
        fwd, up = [], []
        for a in y:
            r = math.radians(a)
            fwd += [round(math.sin(r), 4), 0.0, round(math.cos(r), 4)]
            up += [0.0, 1.0, 0.0]
        return {"prefab": prefab, "scale": scale, "pos": p, "fwd": fwd, "up": up, "vis": [1] * len(y)}
    tr["props"] = {"horse_zelda": prop(zp, zy, "Lox", 0.62), "horse_ganon": prop(gp, gy, "Lox", 0.62)}
    os.makedirs(os.path.join(out, "cutscenes"), exist_ok=True)
    json.dump(tr, open(os.path.join(out, "cutscenes", "nightmare.json"), "w"), separators=(",", ":"))
    json.dump([{"id": "nightmare", "track": "cutscenes/nightmare.json"}],
              open(os.path.join(out, "cutscenes.json"), "w"), indent=1)
    # Besetzung (Valheim-Looks)
    cast = {
        "link": {"model": "male", "hair": "Hair3", "skin": [0.95, 0.8, 0.7], "hair_color": [0.85, 0.65, 0.25],
                 "chest": "ArmorTunic7", "chest_hue": 115, "chest_sat": 0.85, "chest_val": 0.62, "legs": "ArmorLeatherLegs", "helmet": "", "shoulder": "",
                 "left_back": "ShieldWood", "right_back": "SwordBronze", "back_scale": 0.6},
        "zelda": {"model": "female", "hair": "Hair18", "skin": [0.95, 0.82, 0.74], "hair_color": [0.85, 0.66, 0.3],
                  "chest": "ArmorDress4", "legs": "", "helmet": "", "shoulder": ""},
        "impa": {"model": "female", "hair": "Hair5", "skin": [0.78, 0.66, 0.6], "hair_color": [0.82, 0.82, 0.8],
                 "chest": "ArmorLeatherChest", "legs": "ArmorLeatherLegs", "helmet": "", "shoulder": ""},
        "ganondorf": {"model": "male", "hair": "Hair14", "beard": "Beard4", "skin": [0.55, 0.4, 0.3],
                      "hair_color": [0.6, 0.12, 0.04], "chest": "ArmorPaddedCuirass", "legs": "ArmorPaddedGreaves",
                      "helmet": "", "shoulder": "CapeWolf"},
    }
    json.dump(cast, open(os.path.join(out, "cast.json"), "w"), indent=1)
    # Aufnahme der ganzen Cutscene (Bild fuer Bild, 20 fps wie das Spiel) zum Abgleich mit der Original-Kamera
    os.makedirs(os.path.join(out, "shots"), exist_ok=True)
    json.dump({"cutscene": "nightmare", "from": 0.0, "dur": round(n / 20.0, 3), "fps": 20, "grass": False},
              open(os.path.join(out, "shots", "nm_all.json"), "w"), indent=1)
    start = V((-1, 0, 1348))
    pack = {"format": "gameimporter-pack", "version": 1, "id": "oot", "title": "The Legend of Zelda: Ocarina of Time",
            "engine": "oot-n64-f3dex2",
            "source": {"game_version": version, "extractor": "tools/oot (eigener F3DEX2-Leser) + zeldaret/oot", "scenes": ["spot00"]},
            "map": "oot.vmap",
            "transform": {"scale": S, "offset": [0, Y0, 0], "axes": "x=x, y=y, z=-z (OoT-Einheiten)"},
            "languages": ["de", "en"], "default_language": "de", "style": "valheim", "vc_shader": "Particles/Standard Unlit", "grass": False, "vegetation": False,
            "start": {"pos": [start[0], start[1] + 1.0, start[2]], "yaw": 0, "safe": 400},
            "stage2": {"done": False, "notes": ""}}
    json.dump(pack, open(os.path.join(out, "pack.json"), "w"), indent=1)
    report["cuts"] = tr["cuts"]; report["frames"] = n
    return report


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("root"); ap.add_argument("out"); ap.add_argument("--version", default="gc-eu")
    a = ap.parse_args()
    print(json.dumps(build(a.root, a.out, a.version)))
