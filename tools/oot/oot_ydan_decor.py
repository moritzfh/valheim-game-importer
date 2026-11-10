"""Deku-Baum innen: Valheim-Deko (Pilze, Disteln, kleine Straeucher) entlang der Waende, als Zeilen fuer foliage.bin
(src/Foliage.cs setzt echte Valheim-Prefabs). Nicht in der Raummitte, nicht an Tueren, Schaltern, Truhen, Fackeln, Netzen,
Leitern/Ranken; Gruppen von 2-4. Oben (1.-3. Stock) warme Pilze, unten (B1/B2) leuchtende blaue Pilze und Disteln.

  rows(seed) -> [(prefab, x, y, z, scale, yaw)]  (Valheim-Weltkoordinaten)
  merge(foliage_in, foliage_out, rows)          haengt an eine vorhandene foliage.bin an (alte Deku-Baum-Zeilen raus)
"""
import math
import struct

import numpy as np

import oot_ydan as Y

# 08.11. ~09:00 mehr und fantasievoller: Mistlands-Leuchtpilze (Magierkappe, Jotunbovist; "#nl" = ohne deren Einzellicht),
# Farne (Fiddlehead), Wurzeln ohne Kollision ("#nc", ROOTS), wenige kleine Ygga-Sproesslinge
UPPER = [("Pickable_Mushroom", 1.0, 0.22), ("Pickable_Mushroom_yellow", 1.0, 0.16), ("Pickable_Mushroom_Magecap#nl", 1.25, 0.2),
         ("Pickable_Fiddlehead#nc", 1.0, 0.22), ("Pickable_Thistle", 0.9, 0.06),
         ("shrub_2#nc", 0.45, 0.08)]
LOWER = [("Pickable_Mushroom_blue", 1.15, 0.4), ("Pickable_Mushroom_Magecap#nl", 1.3, 0.3),
         ("Pickable_Thistle", 0.9, 0.12), ("Pickable_Fiddlehead#nc", 0.9, 0.12)]
ROOTS = [("root07#nc", 0.55), ("root08#nc", 0.55), ("root11#nc", 0.5), ("root12#nc", 0.5)]
SHOOTS = [("YggaShoot_small1#nc", 0.6)]
YDAN_BOX = (-1860.0, 180.0, -1120.0, -1720.0, 320.0, -940.0)       # Weltbereich des Deku-Baums (zum Ersetzen alter Zeilen)


def _tri_arrays():
    sb, h = Y.scene_header("ydan")
    V, T, W = Y.collision_full(sb, h)
    A = np.array([V[t[0]] for t in T]); B = np.array([V[t[1]] for t in T]); C = np.array([V[t[2]] for t in T])
    N = np.cross(B - A, C - A); L = np.linalg.norm(N, axis=1); ok = L > 1e-6
    N[ok] /= L[ok, None]
    climb = np.array([((t[3] >> 21) & 0x1F) in (2, 3, 4) for t in T])
    return A[ok], B[ok], C[ok], N[ok], climb[ok], W


def _ray_tris(o, d, A, B, C):
    """Abstand entlang d bis zum naechsten Dreieck (Moeller-Trumbore, vektorisiert), inf wenn keins."""
    e1 = B - A; e2 = C - A
    p = np.cross(d, e2); det = np.einsum("ij,ij->i", e1, p)
    ok = np.abs(det) > 1e-9
    inv = np.zeros_like(det); inv[ok] = 1.0 / det[ok]
    t0 = o - A
    u = np.einsum("ij,ij->i", t0, p) * inv
    q = np.cross(t0, e1)
    v = np.einsum("j,ij->i", d, q) * inv
    t = np.einsum("ij,ij->i", e2, q) * inv
    hit = ok & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 0.5)
    return float(t[hit].min()) if hit.any() else math.inf


def _avoid_points():
    """Akteure, an denen keine Deko stehen soll (OoT-Einheiten, Radius)."""
    ids = Y.I.actor_ids(); inv = {v: k for k, v in ids.items()}
    pts = []
    for r, aid, x, y, z, rx, ry, rz, prm in Y.room_actors("ydan", 12):
        nm = inv.get(aid & 0xFFFF, "?")
        if nm in ("ACTOR_EN_KUSA",):
            continue
        rad = 110.0 if nm in ("ACTOR_OBJ_OSHIHIKI", "ACTOR_OBJ_MAKEOSHIHIKI", "ACTOR_BG_YDAN_HASI", "ACTOR_BG_YDAN_MARUTA", "ACTOR_OBJ_LIFT") else 75.0
        pts.append((x, y, z, rad))
    sb, h = Y.scene_header("ydan")
    n, a = h[0x0E]
    for i in range(n):
        fr, fc, br, bc, aid, x, y, z, ry, prm = struct.unpack(">bbbbhhhhhH", sb[a + i * 16:a + i * 16 + 16])
        pts.append((x, y, z, 120.0))                     # Tueren
    n, a = h[0x00]
    for i in range(n):
        s = struct.unpack(">hhhhhhhh", sb[a + i * 16:a + i * 16 + 16])
        pts.append((s[1], s[2], s[3], 150.0))            # Eingang
    return np.array(pts, float)


def rows(seed=21, target=420, roots=34, shoots=8):
    rng = np.random.default_rng(seed)
    A, B, C, N, climb, W = _tri_arrays()
    floor = N[:, 1] > 0.85
    wall = np.abs(N[:, 1]) < 0.35
    WA, WB, WC = A[wall], B[wall], C[wall]
    ClA, ClB, ClC = A[climb], B[climb], C[climb]
    FA, FB, FC = A[floor], B[floor], C[floor]
    area = 0.5 * np.linalg.norm(np.cross(FB - FA, FC - FA), axis=1)
    prob = area / area.sum()
    avoid = _avoid_points()
    water = [(x0, x0 + xl, z0, z0 + zl, ys) for x0, ys, z0, xl, zl, _, _ in W]
    out = []
    tries = 0
    n_roots = n_shoots = 0
    dirs = [np.array([math.sin(a), 0.0, math.cos(a)]) for a in np.linspace(0, 2 * math.pi, 12, endpoint=False)]
    while len(out) < target and tries < 12000:
        tries += 1
        i = rng.choice(len(FA), p=prob)
        r1, r2 = rng.random(), rng.random()
        if r1 + r2 > 1:
            r1, r2 = 1 - r1, 1 - r2
        p = FA[i] + (FB[i] - FA[i]) * r1 + (FC[i] - FA[i]) * r2
        # Boden darunter wirklich der oberste (nicht unter einer Plattform) und frei nach oben
        o = p + np.array([0, 2.0, 0])
        if _ray_tris(o, np.array([0, 1.0, 0]), A[~floor], B[~floor], C[~floor]) < 60:
            continue
        # unter Wasser nicht
        if any(x0 <= p[0] <= x1 and z0 <= p[2] <= z1 and p[1] < ys + 5 for x0, x1, z0, z1, ys in water):
            continue
        if len(avoid) and (np.linalg.norm(avoid[:, :3] - p, axis=1) < avoid[:, 3]).any():
            continue
        # nah an einer Wand (0,5-2,6 m), nicht an Ranken/Leitern
        oh = p + np.array([0, 25.0, 0])
        dw = min(_ray_tris(oh, d, WA, WB, WC) for d in dirs)
        if not (12 < dw < 80):
            continue
        if len(ClA) and min(_ray_tris(oh, d, ClA, ClB, ClC) for d in dirs) < 70:
            continue
        # nicht am Rand einer Kante (Boden ringsum vorhanden)
        edge = False
        for d in dirs[::3]:
            q = p + d * 22 + np.array([0, 10.0, 0])
            if _ray_tris(q, np.array([0, -1.0, 0]), FA, FB, FC) > 25:
                edge = True; break
        if edge:
            continue
        lower = p[1] < -300
        kinds = LOWER if lower else UPPER
        w = np.array([k[2] for k in kinds]); w /= w.sum()
        kname, ks, _ = kinds[rng.choice(len(kinds), p=w)]
        if n_roots < roots and rng.random() < 0.12:
            kname, ks = ROOTS[int(rng.integers(len(ROOTS)))]; n_roots += 1
        elif n_shoots < shoots and rng.random() < 0.03 and dw > 30:
            kname, ks = SHOOTS[0]; n_shoots += 1
        # kleine Gruppe
        n = 1 if kname.startswith(("shrub_2", "root", "YggaShoot")) else int(rng.integers(2, 6))
        for j in range(n):
            off = np.array([rng.normal(0, 14), 0, rng.normal(0, 14)]) if j else np.zeros(3)
            q = p + off
            hy = _ray_tris(q + np.array([0, 15.0, 0]), np.array([0, -1.0, 0]), FA, FB, FC)
            if hy > 30:
                continue
            q[1] = q[1] + 15 - hy
            v = Y.YV(tuple(q))
            sink = 0.18 if kname.startswith("root") else 0.02         # Wurzeln stecken etwas im Boden
            out.append((kname, v[0], v[1] - sink, v[2], ks * float(rng.uniform(0.8, 1.2)), float(rng.uniform(0, 360))))
    return out


def merge(fin, fout, new_rows, box=YDAN_BOX):
    b = open(fin, "rb").read()
    assert b[:4] == b"GIF1"
    o = 4; nk = struct.unpack("<I", b[o:o + 4])[0]; o += 4
    kinds = []
    for _ in range(nk):
        ln = b[o]; o += 1; kinds.append(b[o:o + ln].decode()); o += ln
    n = struct.unpack("<I", b[o:o + 4])[0]; o += 4
    old = []
    for i in range(n):
        k, x, y, z, s, yw = struct.unpack("<HfffBB", b[o:o + 16]); o += 16
        if box[0] <= x <= box[3] and box[1] <= y <= box[4] and box[2] <= z <= box[5]:
            continue                                       # alte Deku-Baum-Deko ersetzen
        old.append((kinds[k], x, y, z, s / 40.0, yw * 360.0 / 256.0))
    allr = old + list(new_rows)
    ks = sorted(set(r[0] for r in allr))
    with open(fout, "wb") as fo:
        fo.write(b"GIF1"); fo.write(struct.pack("<I", len(ks)))
        for k in ks:
            e = k.encode("ascii"); fo.write(struct.pack("<B", len(e))); fo.write(e)
        fo.write(struct.pack("<I", len(allr)))
        for k, x, y, z, s, yw in allr:
            fo.write(struct.pack("<HfffBB", ks.index(k), x, y, z, int(round(s * 40)) & 255, int((yw % 360) / 360 * 256) & 255))
    return len(old), len(new_rows)
