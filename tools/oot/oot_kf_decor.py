"""Kokiri-Wald "Remaster": Valheim-Deko (Buesche, Blumen, Pilze, Farn-artige Straeucher, alte Baeume am Rand, Moosfelsen)
als foliage.bin (src/Foliage.cs setzt echte Valheim-Prefabs). Nicht auf Wegen, nicht in Haeusern/Waenden, nicht vor der
Cutscene-Kamera. Dazu die Original-Akteure, die die Szene erkennbar machen: Steinkreis/Buschkreise (Obj_Mure2), Schilder.

  write(out, keep_tris_v, floor_tris_v, vmap_path, cam_positions)
"""
import gzip
import json
import math
import os
import struct

import numpy as np

import oot_kf

# Arten (Valheim-Prefabs) und Groesse
SHRUBS = [("Bush02_en", 0.75), ("shrub_2", 0.8), ("Bush01", 1.0), ("BlueberryBush", 0.72), ("RaspberryBush", 0.72), ("shrub_2_heath", 1.0)]   # Bush02/shrub_2 kleiner: gross wirken sie wie junge Baeumchen
FLOWERS = [("Pickable_Dandelion", 1.0), ("Pickable_Thistle", 1.0)]
SHROOMS = [("Pickable_Mushroom", 1.0), ("Pickable_Mushroom_yellow", 1.0), ("Pickable_Mushroom_blue", 1.0)]
TREES = [("Oak1", 1.0), ("Beech1", 1.1), ("FirTree", 1.0)]          # ausgewachsen (Wunsch: keine Baeumchen)
ROCKS = [("rock4_forest", 0.22)]          # nur kleine Moosfelsen (Wunsch: keine Felsen im Weg)
SMALL_ROCK = ("Pickable_Stone", 1.5)       # En_Ishi: kleine Steine (kniehoch fuer Kinder)

# Obj_Mure2 im Kopf 6 (Intro): Steinkreis vor dem Haus von Kind 0, zwei Buschgruppen
MURE2 = [((-340, 0, -414), 2), ((797, 0, 643), 1), ((-761, 120, 947), 0)]
SCATTER = [(40, 0x0666), (40, 0x2CCC), (40, 0x5999), (40, 0x8666), (20, 0xC000), (80, 0x1333),
           (80, 0x4000), (80, 0x6CCC), (80, 0x9333), (80, 0xACCC), (80, 0xC666), (60, 0xE000)]
KANBAN = [(-423, 380, -1354), (-713, 121, 262), (-809, 120, 647), (857, 0, 282), (949, 0, -223), (49, -80, 967),
          (-1432, -66, -426), (-494, 120, 598), (-748, 120, 903), (-520, 0, -476), (-1008, 120, 479), (1070, 0, 402),
          (439, 0, 564), (670, 0, -158)]


def read_vmap(path):
    b = gzip.open(path).read()
    W, H = struct.unpack("<ii", b[8:16]); x0, z0, cell, hmin, hs = struct.unpack("<fffff", b[16:36])
    o = 36 + W * H * 2
    h = np.frombuffer(b[36:o], "<u2").reshape(H, W).astype(float) * hs + hmin
    BW, BH = struct.unpack("<ii", b[o:o + 8]); o += 20 + 2 * BW * BH
    paint = None
    while o + 4 <= len(b):
        tag = b[o:o + 4]; o += 4
        if tag == b"PNT1":
            paint = np.frombuffer(b[o:o + W * H], np.uint8).reshape(H, W) / 255.0; o += W * H
        elif tag == b"COLR":
            o += BW * BH * 3
        else:
            break
    return h, paint, (x0, z0, cell, W, H)


def raster(tris, grid, fn):
    """fn(mask_sub, X, Z, Y) fuer jedes Dreieck (Valheim-Koordinaten) im Raster"""
    x0, z0, cell, W, H = grid
    for T in tris:
        T = np.asarray(T, float)
        i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
        j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
        if i1 < i0 or j1 < j0:
            continue
        (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
        d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
        if abs(d) < 1e-9:
            # senkrechte Flaeche: Kanten abtasten
            X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
            fn(j0, i0, np.ones_like(X, bool), X, Z, np.full_like(X, T[:, 1].max()), T)
            continue
        X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
        l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
        l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
        l3 = 1 - l1 - l2
        ins = (l1 >= -0.05) & (l2 >= -0.05) & (l3 >= -0.05)
        Y = l1 * T[0, 1] + l2 * T[1, 1] + l3 * T[2, 1]
        fn(j0, i0, ins, X, Z, Y, T)


# Schwert-Labyrinth (Trainingsplatz hinter dem Kriechgang): im Original offen, ohne Baeume. Der Wald-Generator hielt es fuer
# Waldboden -> Kamera steckte im Laub (Moritz 09.11.). Baeume dort raus (Valheim-Koordinaten x, z).
MAZE = oot_kf.MAZE_BOX_NEW          # verlegtes Labyrinth (oot_kf.MAZE_SHIFT); am alten Ort wieder Wald
MAZE_WALL = 7.5          # Waende mind. so hoch ueber dem Gang (Original 4-6 m; Moritz 09.11.: "immer noch zu niedrig")
MAZE_TREES = ("Beech1", "FirTree", "Oak1", "Birch1", "Beech_small1", "Pinetree_01", "YggaShoot_small1")


def in_maze(r):
    """Im Labyrinth (Gaenge, unten) keine Wald-Deko; die Hochflaeche drumherum (Wald hinter den Waenden) behaelt ihre."""
    x0, x1, z0, z1 = MAZE
    return x0 <= r[1] <= x1 and z0 <= r[3] <= z1 and r[2] < MAZE_FLOOR_MAX


MAZE_FLOOR_MAX = 46.0      # Gaenge liegen bei ~42-44 m, die Hochflaeche hinter den Waenden bei ~49-51 m


def maze_decor(rng, add, ground):
    """Labyrinth verzieren (Moritz): kleine Moosfelsen in den Waenden ueber Kopfhoehe, Pilze und Farne am Wandfuss.
    Alles ohne Kollision (#nc), nur auf der Gang-Seite der Original-Waende (oot_kf.LAST_MAZE_V)."""
    import oot_kf
    walls = [np.asarray(T, float) for T in oot_kf.LAST_MAZE_V]
    # Gang-Seite: dort liegt begehbarer Rasen (oot_kf.LAST_GROUND_V)
    gp = []
    for T in oot_kf.LAST_GROUND_V:
        T = np.asarray(T, float)
        if MAZE[0] - 2 <= T[:, 0].mean() <= MAZE[1] + 2 and MAZE[2] - 2 <= T[:, 2].mean() <= MAZE[3] + 2:
            for u in np.linspace(0, 1, 5):
                for v in np.linspace(0, 1 - u, 5):
                    gp.append(T[0] + u * (T[1] - T[0]) + v * (T[2] - T[0]))
    gp = np.array(gp) if gp else np.zeros((0, 3))
    from scipy.spatial import cKDTree
    kd = cKDTree(gp[:, [0, 2]]) if len(gp) else None
    n_rock = n_shroom = n_fern = 0
    for T in walls:
        nrm = np.cross(T[1] - T[0], T[2] - T[0]); area = np.linalg.norm(nrm) / 2
        if area < 0.6 or abs(nrm[1]) > 0.3 * np.linalg.norm(nrm):
            continue
        nh = np.array([nrm[0], 0.0, nrm[2]]); nh /= (np.linalg.norm(nh) or 1.0)
        c = T.mean(0)
        if kd is None:
            continue
        da, _ = kd.query([c[0] + nh[0] * 0.8, c[2] + nh[2] * 0.8]); db, _ = kd.query([c[0] - nh[0] * 0.8, c[2] - nh[2] * 0.8])
        if min(da, db) > 1.5:
            continue                                     # Aussenwand ohne Gang davor
        if db < da:
            nh = -nh                                     # zur Gang-Seite zeigen
        y1 = T[:, 1].max()
        g0 = ground(c[0] + nh[0] * 0.6, c[2] + nh[2] * 0.6)            # Gangboden vor der Wand (Waende reichen teils unter den Boden)
        y0 = max(T[:, 1].min(), g0)
        for _ in range(int(area / 4.0) + 1):
            u, v = rng.random(), rng.random()
            if u + v > 1:
                u, v = 1 - u, 1 - v
            q = T[0] + u * (T[1] - T[0]) + v * (T[2] - T[0])
            # Moosfelsen ueber Kopfhoehe (Link ~1,3 m, Kamera ~2,5 m): ab 2,4 m bis 1 m unter der Oberkante, ein Stueck in der Wand
            if q[1] - y0 > 2.4 and y1 - q[1] > 0.6 and rng.random() < 0.35:
                add("rock4_forest#nc", q[0] - nh[0] * 0.15, q[1] - 0.3, q[2] - nh[2] * 0.15, rng.uniform(0.07, 0.16)); n_rock += 1
        # Wandfuss: Pilze und Farne (dicht an der Wand, nicht im Weg)
        bot = sorted(T, key=lambda p: p[1])[:2]
        if T[:, 1].min() < g0 + 0.5:
            a, b = bot[0], bot[1]; L = np.linalg.norm((b - a)[[0, 2]])
            for t in np.linspace(0.1, 0.9, max(1, int(L / 1.6))):
                q = a + (b - a) * t + nh * rng.uniform(0.2, 0.4)
                gy = ground(q[0], q[2])
                r = rng.random()
                if r < 0.22:
                    k, s = SHROOMS[rng.integers(len(SHROOMS))]; add(k, q[0], gy, q[2], s * rng.uniform(0.8, 1.3)); n_shroom += 1
                elif r < 0.36:
                    add("Pickable_Fiddlehead#nc", q[0], gy, q[2], rng.uniform(0.8, 1.2)); n_fern += 1
    return {"felsen": n_rock, "pilze": n_shroom, "farne": n_fern}


def write(out, keep_tris_v, floor_tris_v, vmap_path, cams, seed=7, steppe=None):
    from scipy import ndimage
    h, paint, grid = read_vmap(vmap_path)
    x0, z0, cell, W, H = grid
    # Gebiet: KF-Boden
    kf = np.zeros((H, W), bool)

    def mark_floor(j0, i0, ins, X, Z, Y, T):
        sub = kf[j0:j0 + ins.shape[0], i0:i0 + ins.shape[1]]; sub |= ins
    raster(floor_tris_v, grid, mark_floor)
    # Hindernisse: alle festen Modelle bis 4 m ueber Boden (Haeuser, Waende, Zaeune, Plattform-Stuetzen)
    occ = np.zeros((H, W), bool)

    def mark_occ(j0, i0, ins, X, Z, Y, T):
        sh = ins.shape
        g = h[j0:j0 + sh[0], i0:i0 + sh[1]]
        if T[:, 1].min() > g.max() + 4.0:
            return
        sub = occ[j0:j0 + sh[0], i0:i0 + sh[1]]; sub |= ins
    raster(keep_tris_v, grid, mark_occ)
    dist_occ = ndimage.distance_transform_edt(~occ) * cell
    dist_edge = ndimage.distance_transform_edt(kf) * cell             # Abstand zum Rand des Waldbodens
    path = paint if paint is not None else np.zeros_like(h)
    camp = np.asarray(cams, float).reshape(-1, 3)[::3]
    camgrid = np.zeros((H, W), bool)
    for c in camp:
        i, j = int((c[0] - x0) / cell), int((c[2] - z0) / cell)
        if 0 <= i < W and 0 <= j < H:
            camgrid[j, i] = True
    dist_cam = ndimage.distance_transform_edt(~camgrid) * cell
    gy, gx = np.gradient(h, cell); slope = np.hypot(gx, gy)
    rng = np.random.default_rng(seed)
    rows = []

    def add(kind, x, y, z, s, yaw=None):
        rows.append((kind, float(x), float(y), float(z), float(s), float(rng.uniform(0, 360) if yaw is None else yaw)))

    # Freiraum um Kinder und Steinkreise (Original-Akteure sollen sichtbar bleiben)
    free = np.zeros((H, W), bool)
    import oot_intro
    for p_ in [q[0] for q in oot_intro.KF_KIDS] + [q[0] for q in MURE2]:
        v = oot_kf.V(p_); i, j = int((v[0] - x0) / cell), int((v[2] - z0) / cell)
        if 0 <= i < W and 0 <= j < H:
            free[j, i] = True
    dist_free = ndimage.distance_transform_edt(~free) * cell

    def ok(i, j, occ_m, cam_m, path_max=0.25):
        return (kf[j, i] and dist_occ[j, i] > occ_m and dist_cam[j, i] > cam_m and path[j, i] < path_max and dist_free[j, i] > 3.6)
    # Streu-Deko auf einem Zitterraster
    step = 1.6
    xs = np.arange(x0, x0 + W * cell, step); zs = np.arange(z0, z0 + H * cell, step)
    kfj, kfi = np.nonzero(kf)
    xmin, xmax = x0 + kfi.min() * cell, x0 + kfi.max() * cell
    zmin, zmax = z0 + kfj.min() * cell, z0 + kfj.max() * cell
    noise = ndimage.gaussian_filter(rng.random((H, W)), 6.0); noise = (noise - noise.min()) / (np.ptp(noise) + 1e-9)
    for x in xs[(xs >= xmin) & (xs <= xmax)]:
        for z in zs[(zs >= zmin) & (zs <= zmax)]:
            px, pz = x + rng.uniform(-0.7, 0.7), z + rng.uniform(-0.7, 0.7)
            i, j = int((px - x0) / cell), int((pz - z0) / cell)
            if not (0 <= i < W and 0 <= j < H) or not kf[j, i]:
                continue
            y = h[j, i]; r = rng.random(); nz = noise[j, i]; edge = dist_edge[j, i]
            # alte Baeume nur am Waldrand (hinter den Haeusern, vor den Grenzwaenden)
            if edge < 5.0 and r < 0.07 and ok(i, j, 3.5, 6.0):
                k, s = TREES[rng.integers(len(TREES))]; add(k, px, y - 0.2, pz, s * rng.uniform(1.05, 1.45)); continue
            # Strauecher in Gruppen (Rauschen), dichter am Rand und an Waenden
            dens = 0.10 + 0.25 * nz + (0.25 if edge < 6 else 0) + (0.15 if dist_occ[j, i] < 3 else 0) + (0.3 if slope[j, i] > 0.6 else 0)
            if r < dens and ok(i, j, 0.9, 2.2):
                k, s = SHRUBS[rng.integers(len(SHRUBS))]; add(k, px, y, pz, s * rng.uniform(0.7, 1.25)); continue
            if r < dens + 0.08 and ok(i, j, 0.5, 1.5, 0.4):
                k, s = FLOWERS[rng.integers(len(FLOWERS))]; add(k, px, y, pz, s * rng.uniform(0.8, 1.2)); continue
            if dist_occ[j, i] < 2.5 and r < dens + 0.14 and ok(i, j, 0.4, 1.5):
                k, s = SHROOMS[rng.integers(len(SHROOMS))]; add(k, px, y, pz, s * rng.uniform(0.9, 1.6)); continue
            if r < dens + 0.148 and dist_occ[j, i] < 4 and ok(i, j, 1.5, 4.0, 0.1):
                k, s = ROCKS[rng.integers(len(ROCKS))]; add(k, px, y - 0.25, pz, s * rng.uniform(0.7, 1.3)); continue
    # Original-Akteure: Kreise/Gruppen aus Obj_Mure2, Schilder
    for (p, typ) in MURE2:
        pts = []
        if typ == 2:
            pts = [(p[0] + 80 * math.sin(i * 0x2000 / 65536 * 2 * math.pi), p[2] + 80 * math.cos(i * 0x2000 / 65536 * 2 * math.pi)) for i in range(8)]
        elif typ == 0:
            pts = [(p[0], p[2])] + [(p[0] + 80 * math.sin((i - 1) * 0x2000 / 65536 * 2 * math.pi), p[2] + 80 * math.cos((i - 1) * 0x2000 / 65536 * 2 * math.pi)) for i in range(1, 9)]
        else:
            pts = [(p[0] + r_ * math.cos(a / 65536 * 2 * math.pi), p[2] - r_ * math.sin(a / 65536 * 2 * math.pi)) for r_, a in SCATTER]
        for (ox, oz) in pts:
            v = oot_kf.V((ox, p[1], oz)); i, j = int((v[0] - x0) / cell), int((v[2] - z0) / cell)
            y = h[j, i] if (0 <= i < W and 0 <= j < H) else v[1]
            if typ == 2:
                add(SMALL_ROCK[0], v[0], y - 0.1, v[2], SMALL_ROCK[1] * rng.uniform(0.9, 1.1))
            else:
                add("Bush02_en", v[0], y, v[2], rng.uniform(0.6, 0.8))
    # der Stein, den Kokiri 0 hochzuheben versucht (direkt vor ihm, er schaut nach +z)
    v = oot_kf.V((-340, 0, -414 + 16)); i, j = int((v[0] - x0) / cell), int((v[2] - z0) / cell)
    add(SMALL_ROCK[0], v[0], h[j, i] - 0.05, v[2], SMALL_ROCK[1] * 1.1)
    for p in KANBAN:
        v = oot_kf.V(p); i, j = int((v[0] - x0) / cell), int((v[2] - z0) / cell)
        y = h[j, i] if (0 <= i < W and 0 <= j < H) else v[1]
        add("sign", v[0], y, v[2], 1.0)
    fr = forest_rows(floor_tris_v, h, grid, rng, kf)
    rows += fr
    rows += plateau_forest(h, grid, kf, dist_cam, rng, [(r[1], r[3]) for r in fr if r[0] in [f[0] for f in FOREST]])
    if steppe is not None:
        rows += steppe_rows(out, h, grid, kf, steppe, rng)
    rows = [r for r in rows if not in_maze(r)]
    try:
        def ground(x, z):
            i, j = int((x - x0) / cell), int((z - z0) / cell)
            return float(h[j, i]) if 0 <= i < W and 0 <= j < H else 42.0
        print("Labyrinth-Deko:", maze_decor(rng, add, ground))
    except Exception as ex:
        print("Labyrinth-Deko:", ex)
    kinds = sorted(set(r[0] for r in rows))
    with open(os.path.join(out, "foliage.bin"), "wb") as fo:
        fo.write(b"GIF1"); fo.write(struct.pack("<I", len(kinds)))
        for k in kinds:
            b = k.encode("ascii"); fo.write(struct.pack("<B", len(b))); fo.write(b)
        fo.write(struct.pack("<I", len(rows)))
        for k, x, y, z, s, yaw in rows:
            fo.write(struct.pack("<HfffBB", kinds.index(k), x, y, z, int(round(s * 40)) & 255, int((yaw % 360) / 360 * 256) & 255))
    from collections import Counter
    return {"n": len(rows), "kinds": dict(Counter(r[0] for r in rows))}


FOREST = [("Beech1", 1.0, 0.55), ("FirTree", 1.0, 0.27), ("Oak1", 0.9, 0.18)]


def forest_rows(floor_tris_v, h, grid, rng, kf=None):
    """Waldrand statt aufgemalter Baumwand: 4 Reihen echter Valheim-Baeume hinter der (unsichtbaren) Randwand, Unterholz davor"""
    x0, z0, cell, W, H = grid
    F = np.asarray(floor_tris_v, float).reshape(-1, 3)
    c = F.mean(0)
    rows = []
    probs = np.array([f[2] for f in FOREST]); probs /= probs.sum()
    for a, b in oot_kf.LAST_PERIM:
        a = np.array(a, float); b = np.array(b, float)
        d = b - a; L = math.hypot(d[0], d[2])
        if L < 0.5:
            continue
        t = np.array([d[0], 0.0, d[2]]) / L
        n = np.array([-t[2], 0.0, t[0]])
        mid = (a + b) / 2
        if (mid - c)[[0, 2]] @ n[[0, 2]] < 0:
            n = -n                                       # nach aussen
        if kf is not None:
            # genauer: aussen = die Seite ohne Waldboden (sonst stehen in Gaengen wie zur Schwert-Truhe Baeume mitten im Weg)
            def isfloor(q):
                i, j = int((q[0] - x0) / cell), int((q[2] - z0) / cell)
                return 0 <= i < W and 0 <= j < H and bool(kf[j, i])
            fa, fb = isfloor(mid + n * 3.0), isfloor(mid - n * 3.0)
            if fa and not fb:
                n = -n
            elif fa and fb:
                continue
        base = min(a[1], b[1])

        def gy(q, fb):
            i, j = int((q[0] - x0) / cell), int((q[2] - z0) / cell)
            return float(h[j, i]) if 0 <= i < W and 0 <= j < H else fb
        for u in np.arange(0.0, L, 3.0):
            for dist in (1.8, 5.0, 8.5, 12.5):
                q = a + t * (u + rng.uniform(-1.2, 1.2)) + n * (dist + rng.uniform(-0.8, 0.8))
                k = FOREST[rng.choice(len(FOREST), p=probs)]
                rows.append((k[0], float(q[0]), gy(q, base) - 0.4, float(q[2]), float(k[1] * rng.uniform(1.35, 2.0) * (1.0 + 0.08 * dist / 4)), float(rng.uniform(0, 360))))
            if rng.random() < 0.7:                       # Unterholz direkt am Rand
                q = a + t * (u + rng.uniform(0, 3.0)) + n * rng.uniform(0.3, 1.2)
                kk, s_ = SHRUBS[rng.integers(len(SHRUBS))]
                rows.append((kk, float(q[0]), gy(q, base), float(q[2]), float(s_ * rng.uniform(1.0, 1.6)), float(rng.uniform(0, 360))))
    return rows


def plateau_forest(h, grid, kf, dist_cam, rng, taken):
    """Remaster: auf dem Gelaende ueber den (jetzt als Gelaende gebauten) Felswaenden ausgewachsener Wald, vom Dorf aus sichtbar.
    Nicht auf dem Dorfboden, nicht am Steilhang (Fels bleibt frei), nicht vor Cutscene-Kameras."""
    from scipy import ndimage
    x0, z0, cell, W, H = grid
    dkf = ndimage.distance_transform_edt(~kf) * cell
    gy_, gx_ = np.gradient(ndimage.gaussian_filter(h, 1.0), cell); slope = np.hypot(gx_, gy_)
    # Hoehe des naechsten Dorfbodens: nur Baeume, die wirklich ueber dem Dorf stehen (oben auf dem Hang)
    _, fi = ndimage.distance_transform_edt(~kf, return_indices=True)
    above = h - h[fi[0], fi[1]]
    tk = np.zeros((H, W), bool)
    for (x, z) in taken:
        i, j = int((x - x0) / cell), int((z - z0) / cell)
        if 0 <= i < W and 0 <= j < H:
            tk[j, i] = True
    dtk = ndimage.distance_transform_edt(~tk) * cell
    probs = np.array([f[2] for f in FOREST]); probs /= probs.sum()
    rows = []
    kj, ki = np.nonzero(kf)
    xa, xb = x0 + ki.min() * cell - 35, x0 + ki.max() * cell + 35
    za, zb = z0 + kj.min() * cell - 35, z0 + kj.max() * cell + 35
    step = 3.6
    for z in np.arange(za, zb, step):
        for x in np.arange(xa, xb, step):
            px, pz = x + rng.uniform(-1.3, 1.3), z + rng.uniform(-1.3, 1.3)
            i, j = int((px - x0) / cell), int((pz - z0) / cell)
            if not (0 <= i < W and 0 <= j < H) or kf[j, i]:
                continue
            if not (3.0 < dkf[j, i] < 40.0) or slope[j, i] > 0.95 or above[j, i] < 2.0 or h[j, i] < 31.5:
                continue
            if dist_cam[j, i] < 7.0 or dtk[j, i] < 2.6 or rng.random() > 0.62:
                continue
            k = FOREST[rng.choice(len(FOREST), p=probs)]
            rows.append((k[0], float(px), float(h[j, i] - 0.35), float(pz), float(k[1] * rng.uniform(1.15, 1.75)), float(rng.uniform(0, 360))))
            if rng.random() < 0.35:
                kk, s_ = SHRUBS[rng.integers(len(SHRUBS))]
                qx, qz = px + rng.uniform(-1.8, 1.8), pz + rng.uniform(-1.8, 1.8)
                ii, jj = int((qx - x0) / cell), int((qz - z0) / cell)
                if 0 <= ii < W and 0 <= jj < H and not kf[jj, ii]:
                    rows.append((kk, float(qx), float(h[jj, ii]), float(qz), float(s_ * rng.uniform(0.9, 1.3)), float(rng.uniform(0, 360))))
    return rows


def steppe_rows(out, h, grid, kf, avoid_pts, rng):
    """Remaster-Deko der Steppe (Albtraum, Startbildschirm): Buschgruppen, Blumen, wenige alte Baeume, kleine Felsen.
    Nicht an Burg/Bruecke (Bauteile), nicht im Wasser, nicht auf Kamera-/Reiterwegen."""
    from scipy import ndimage
    x0, z0, cell, W, H = grid
    occ = np.zeros((H, W), bool)
    for fn in ("statics_blocks.json",):
        p = os.path.join(out, fn)
        if not os.path.exists(p):
            continue
        for e in json.load(open(p)):
            for k in e.get("kit", []):
                i, j = int((k[1] - x0) / cell), int((k[3] - z0) / cell)
                if 0 <= i < W and 0 <= j < H:
                    occ[j, i] = True
    dist_occ = ndimage.distance_transform_edt(~occ) * cell
    av = np.zeros((H, W), bool)
    for c in np.asarray(avoid_pts, float).reshape(-1, 3)[::2]:
        i, j = int((c[0] - x0) / cell), int((c[2] - z0) / cell)
        if 0 <= i < W and 0 <= j < H:
            av[j, i] = True
    dist_av = ndimage.distance_transform_edt(~av) * cell
    gy, gx = np.gradient(h, cell); slope = np.hypot(gx, gy)
    noise = ndimage.gaussian_filter(rng.random((H, W)), 10.0); noise = (noise - noise.min()) / (np.ptp(noise) + 1e-9)
    rows = []
    step = 2.6
    for z in np.arange(z0, z0 + H * cell, step):
        for x in np.arange(x0, x0 + W * cell, step):
            px, pz = x + rng.uniform(-1.1, 1.1), z + rng.uniform(-1.1, 1.1)
            i, j = int((px - x0) / cell), int((pz - z0) / cell)
            if not (0 <= i < W and 0 <= j < H) or kf[j, i] or px > 560:
                continue
            y = h[j, i]
            if y < 30.7 or slope[j, i] > 1.1 or dist_occ[j, i] < 5 or dist_av[j, i] < 3.5:
                continue
            r = rng.random(); nz = noise[j, i] ** 2
            if r < 0.0015 + 0.006 * nz and dist_av[j, i] > 8:
                k, s_ = TREES[rng.integers(2)]; rows.append((k, px, y - 0.2, pz, s_ * rng.uniform(0.8, 1.2), rng.uniform(0, 360))); continue
            if r < 0.03 + 0.22 * nz:
                k, s_ = SHRUBS[rng.integers(len(SHRUBS))]; rows.append((k, px, y, pz, s_ * rng.uniform(0.7, 1.2), rng.uniform(0, 360))); continue
            if r < 0.06 + 0.25 * nz:
                k, s_ = FLOWERS[rng.integers(len(FLOWERS))]; rows.append((k, px, y, pz, s_, rng.uniform(0, 360))); continue
            if r < 0.068 + 0.25 * nz:
                k, s_ = ROCKS[0]; rows.append((k, px, y - 0.25, pz, s_ * rng.uniform(0.6, 1.2), rng.uniform(0, 360)))
    return rows
