"""OoT -> Valheim-Paket in VALHEIM-OPTIK (Variante "blocks" oder "retex").

  python oot_vpack.py <decomp-root> <pack-out> [--style blocks|retex|both]

- Natur (Gras, Weg, Fels, Klippen) -> Valheim-Gelaende (Heightmap aus den Original-Dreiecken, hoechste Flaeche)
- Wasser -> Valheim-Wasser: Y0 so, dass die Wasserflaechen (OoT y=-60) genau auf Valheims 30 m liegen
- Bauwerke (Stein/Holz/Dach) -> "blocks": echte Valheim-Bauteile im 1-m-Raster (Statics-"kit")
                              -> "retex": Originalform mit Valheim-Material (extras.valheim)
- gemalte Kulissen, Schatten, Gras-Fransen fallen weg
"""
import os, sys, json, math, gzip, struct, argparse, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_pack, oot_style, oot_glb, oot_cutscene
from oot_gfx import scene_collision
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

WATER_Y = -60          # OoT-Hoehe der Wasserflaechen in der Steppe
oot_pack.Y0 = 30.0 + (-WATER_Y) * oot_pack.S    # -> 31.98
V = oot_pack.V
S = oot_pack.S


def classify(tris):
    out = collections.defaultdict(list)
    for k, vs in tris:
        out[oot_style.category(k)].append((k, vs))
    return out


# ---------------- Gelaende ----------------
def make_vmap_natural(nat_tris, water_boxes, out, cell=1.0, margin=60.0, extra_P=None, extra_water=None, paint=None, ceilings=None, supports=None, water_tris=None,
                      walls=None, wall_slope=2.2, plateau=30.0, tubes=None, berm=8.0, berm_slope=0.75, bare=None, steep_boxes=None, ground_tris=None, keep_paths=None, flat_boxes=None):
    P = np.array([[V(p) for p, _, _ in vs] for _, vs in nat_tris], float)      # (n,3,3) Valheim
    n_nat = len(P)
    if extra_P is not None and len(extra_P):
        P = np.concatenate([P, np.asarray(extra_P, float)], 0)               # weitere Gebiete (z. B. Kokiri-Wald), schon in Valheim-Koordinaten
    x0, x1 = P[:, :, 0].min() - margin, P[:, :, 0].max() + margin
    z0, z1 = P[:, :, 2].min() - margin, P[:, :, 2].max() + margin
    W = int((x1 - x0) / cell) + 1; H = int((z1 - z0) / cell) + 1
    hm = np.full((H, W), np.nan)
    hmin = np.full((H, W), np.nan)          # niedrigster Boden der weiteren Gebiete
    ceil = np.full((H, W), np.nan)          # Decken (Tunnel): unterste nach unten zeigende Flaeche
    for T in (np.asarray(ceilings, float) if ceilings is not None and len(ceilings) else []):
        i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
        j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
        if i1 < i0 or j1 < j0:
            continue
        sub = ceil[j0:j1 + 1, i0:i1 + 1]; y = T[:, 1].min()
        sub[np.isnan(sub) | (sub > y)] = y
    for ti, T in enumerate(P):
        i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
        j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
        (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
        d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
        if abs(d) < 1e-6:
            continue
        X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
        l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
        l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
        l3 = 1 - l1 - l2
        ins = (l1 >= -0.05) & (l2 >= -0.05) & (l3 >= -0.05)
        Y = l1 * T[0, 1] + l2 * T[1, 1] + l3 * T[2, 1]
        sub = hm[j0:j1 + 1, i0:i1 + 1]
        upd = ins & (np.isnan(sub) | (Y > sub))
        sub[upd] = Y[upd]
        if ti >= n_nat:
            sm = hmin[j0:j1 + 1, i0:i1 + 1]; u2 = ins & (np.isnan(sm) | (Y < sm)); sm[u2] = Y[u2]
    # Tunnel (Kokiri-Wald): liegt eine Decke zwischen unterstem und oberstem Boden, gilt der unterste
    tun = ~np.isnan(hmin) & ~np.isnan(ceil) & (ceil > hmin + 0.5) & (ceil < hm - 0.3)
    hm = np.where(tun, hmin, hm)
    # Stuetzpunkte (Zaunpfosten u. a. auf Absaetzen): Gelaende darunter mindestens bis zum Fuss, sanft auslaufend
    for (sx, sz, sy) in (supports or []):
        i0 = max(0, int((sx - 4 - x0) / cell)); i1 = min(W - 1, int((sx + 4 - x0) / cell) + 1)
        j0 = max(0, int((sz - 4 - z0) / cell)); j1 = min(H - 1, int((sz + 4 - z0) / cell) + 1)
        X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
        want = sy + 0.25 - np.clip(np.hypot(X - sx, Z - sz) - 1.8, 0, None) * 0.9
        sub = hm[j0:j1 + 1, i0:i1 + 1]
        upd = ~np.isnan(sub) & (want > sub)
        sub[upd] = want[upd]
    from scipy import ndimage
    if (steep_boxes or flat_boxes) and ground_tris is not None and len(ground_tris):
        # Schwert-Labyrinth: im Original liegt ein grosser Kollisionsboden unter allem, die Waende stehen nur darauf.
        # Boden ist dort nur, wo begehbarer Rasen liegt (Gaenge); der Rest wird Wand bis zur Original-Oberkante.
        gm = np.zeros((H, W), bool)
        for T in np.asarray(ground_tris, float):
            i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
            j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
            if i1 < i0 or j1 < j0:
                continue
            (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
            d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
            if abs(d) < 1e-9:
                continue
            X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
            l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
            l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
            gm[j0:j1 + 1, i0:i1 + 1] |= (l1 >= -0.05) & (l2 >= -0.05) & (1 - l1 - l2 >= -0.05)
        for path in (keep_paths or []):
            # Wege der rollenden Felsbrocken: Gang mindestens 2,4 m breit (sonst rollt der Fels durch eine Wandecke)
            P2 = np.asarray(path, float)
            for a, b in zip(P2[:-1], P2[1:]):
                for t in np.linspace(0, 1, max(2, int(np.linalg.norm(b - a) / (cell * 0.3)))):
                    q = a + (b - a) * t
                    ci, cj = (q[0] - x0) / cell, (q[2] - z0) / cell
                    for dj in range(-2, 3):
                        for di in range(-2, 3):
                            ii, jj = int(round(ci)) + di, int(round(cj)) + dj
                            if 0 <= ii < W and 0 <= jj < H and np.hypot(ii - ci, jj - cj) * cell <= 1.2:
                                gm[jj, ii] = True
        for (sx0, sx1, sz0, sz1, *_) in list(steep_boxes or []) + list(flat_boxes or []):
            a0, a1 = max(0, int((sx0 - x0) / cell)), min(W, int((sx1 - x0) / cell) + 1)
            b0, b1 = max(0, int((sz0 - z0) / cell)), min(H, int((sz1 - z0) / cell) + 1)
            clr = np.zeros((H, W), bool); clr[b0:b1, a0:a1] = True
            clr &= ~gm & ~np.isnan(hm)
            hm[clr] = np.nan; hmin[clr] = np.nan
    def sample_tris(Ts, out_max, out_min=None):
        for T in np.asarray(Ts, float):
            e1, e2 = T[1] - T[0], T[2] - T[0]
            n1 = max(1, int(np.linalg.norm(e1) / (cell * 0.4)) + 1); n2 = max(1, int(np.linalg.norm(e2) / (cell * 0.4)) + 1)
            U, Wm = np.broadcast_arrays(np.linspace(0, 1, n1 + 1)[:, None], np.linspace(0, 1, n2 + 1)[None, :])
            msk = (U + Wm) <= 1.0001
            pts = T[0] + U[msk][:, None] * e1 + Wm[msk][:, None] * e2
            ii = np.round((pts[:, 0] - x0) / cell).astype(int); jj = np.round((pts[:, 2] - z0) / cell).astype(int)
            ok = (ii >= 0) & (ii < W) & (jj >= 0) & (jj < H)
            np.fmax.at(out_max, (jj[ok], ii[ok]), pts[ok, 1])
            if out_min is not None:
                np.fmin.at(out_min, (jj[ok], ii[ok]), pts[ok, 1])
    # hohle Baumstaemme, die in den Hang fuehren: dort kein Boden-Gelaende (der Stamm hat seinen eigenen Boden)
    tmask = None
    if tubes is not None and len(tubes):
        ttop = np.full((H, W), np.nan); tbot = np.full((H, W), np.nan)
        sample_tris(tubes, ttop, tbot)
        tmask = ~np.isnan(ttop)
        hm = np.where(tmask, np.nan, hm)
    kfk = ~np.isnan(hmin) & ~np.isnan(hm)          # Boeden des Kokiri-Walds (ohne Staemme)
    fk = ~np.isnan(hm)                     # echte Boeden (vor den Waenden)
    nwall = 0
    if walls is not None and len(walls):
        # Remaster: Fels-/Erdwaende werden Gelaende. Jede Wand hebt das Gelaende hinter sich bis zu ihrer Oberkante
        # (dichte Abtastung, auch senkrechte Flaechen); Boeden behalten Vorrang (Wandfuss, Ueberhaenge, Tunnel bleiben frei).
        wt = np.full((H, W), np.nan)
        WA = np.asarray(walls, float)
        nrm = np.cross(WA[:, 1] - WA[:, 0], WA[:, 2] - WA[:, 0]); nl = np.linalg.norm(nrm, axis=1)
        steep = np.abs(nrm[:, 1]) < 0.3 * np.maximum(nl, 1e-9)
        sample_tris(WA[steep], wt)                      # (fast) senkrechte Waende: Oberkante je Zelle
        for T in WA[~steep]:                            # Haenge: genaue Flaechenhoehe in der Zellmitte (sonst bis zu 1 m zu hoch)
            i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
            j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
            if i1 < i0 or j1 < j0:
                continue
            (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
            d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
            if abs(d) < 1e-9:
                continue
            X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
            l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
            l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
            l3 = 1 - l1 - l2
            ins = (l1 >= -0.05) & (l2 >= -0.05) & (l3 >= -0.05)
            Y = l1 * T[0, 1] + l2 * T[1, 1] + l3 * T[2, 1]
            sub = wt[j0:j1 + 1, i0:i1 + 1]
            upd = ins & (np.isnan(sub) | (Y > sub))
            sub[upd] = Y[upd]
        if tmask is not None:
            wt = np.where(tmask, np.nan, wt)
        for (sx0, sx1, sz0, sz1, *_) in (flat_boxes or []):
            # flaches Gebiet (Labyrinth mit Original-Waenden als Modell): die Waende heben dort kein Gelaende an
            a0, a1 = max(0, int((sx0 - x0) / cell)), min(W, int((sx1 - x0) / cell) + 1)
            b0, b1 = max(0, int((sz0 - z0) / cell)), min(H, int((sz1 - z0) / cell) + 1)
            wt[b0:b1, a0:a1] = np.nan
        add = ~fk & ~np.isnan(wt)
        hm = np.where(add, wt, hm); nwall = int(add.sum())
    known = ~np.isnan(hm)
    idx = ndimage.distance_transform_edt(~known, return_distances=False, return_indices=True)
    dist = ndimage.distance_transform_edt(~known) * cell
    h = hm[idx[0], idx[1]]
    if nwall:
        # Valheim-Gelaende kann nicht senkrecht: vom Wandfuss (naechster Boden) steigt der Hang mit wall_slope (~65 Grad)
        # nach aussen bis zur Oberkante -> Valheim malt dort seinen Fels; oben ein Plateau fuer den Wald
        dfl, fidx = ndimage.distance_transform_edt(~fk, return_indices=True)
        hf = hm[fidx[0], fidx[1]]
        slope = np.full(h.shape, wall_slope)
        for (sx0, sx1, sz0, sz1, sv, *_) in (steep_boxes or []):
            # steilere Waende in einem Gebiet (Schwert-Labyrinth: schmale 4-6 m hohe Waende kaemen sonst nur auf Link-Hoehe)
            a0, a1 = max(0, int((sx0 - x0) / cell)), min(W, int((sx1 - x0) / cell) + 1)
            b0, b1 = max(0, int((sz0 - z0) / cell)), min(H, int((sz1 - z0) / cell) + 1)
            slope[b0:b1, a0:a1] = sv
        h = np.where(fk, h, np.minimum(h, hf + slope * dfl * cell))
        for (sx0, sx1, sz0, sz1, sv, *mh) in (steep_boxes or []):
            # Mindesthoehe der Waende ueber dem naechsten Gang (Labyrinth: Moritz will sie deutlich hoeher als Link)
            if not mh:
                continue
            a0, a1 = max(0, int((sx0 - x0) / cell)), min(W, int((sx1 - x0) / cell) + 1)
            b0, b1 = max(0, int((sz0 - z0) / cell)), min(H, int((sz1 - z0) / cell) + 1)
            box = np.zeros(h.shape, bool); box[b0:b1, a0:a1] = True
            want = hf + np.minimum(mh[0], sv * dfl * cell)
            h = np.where(box & ~fk, np.maximum(h, want), h)
        if ceilings is not None and len(ceilings):
            # unter Baumstaemmen/Stegen, die durch den Hang gehen: Gelaende nicht durch das Modell stossen lassen
            cut = ~fk & ~np.isnan(ceil) & (h > ceil - 0.2) & (h - ceil < 4.0)
            h = np.where(cut, ceil - 0.2, h)
    # Wasser: in den Wasserkaesten liegt der Grund mind. 1,5 m unter Valheims Wasser (30 m)
    wmask = np.zeros_like(known)
    for (wx, wy, wz, xl, zl) in water_boxes:
        if wy != WATER_Y:
            continue
        a, b = V((wx, wy, wz)), V((wx + xl, wy, wz + zl))
        i0, i1 = int((min(a[0], b[0]) - x0) / cell), int((max(a[0], b[0]) - x0) / cell)
        j0, j1 = int((min(a[2], b[2]) - z0) / cell), int((max(a[2], b[2]) - z0) / cell)
        sl = (slice(max(0, j0), j1 + 1), slice(max(0, i0), i1 + 1))
        wmask[sl] |= known[sl] & (h[sl] < 30.3)        # nur wo der Boden wirklich unter dem Wasser liegt (Bachbett), nicht der ganze Kasten
    if water_tris is not None and len(water_tris):
        # nur wo im Original wirklich eine Wasserflaeche liegt (Kaesten sind groesser; trockene Senken bleiben Land)
        wt = np.zeros_like(wmask)
        for T in np.asarray(water_tris, float):
            i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
            j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
            (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
            d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
            if abs(d) < 1e-9:
                continue
            X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
            l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
            l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
            wt[j0:j1 + 1, i0:i1 + 1] |= (l1 >= -0.1) & (l2 >= -0.1) & (1 - l1 - l2 >= -0.1)
        wt = ndimage.binary_dilation(wt, iterations=2)
        wmask &= wt
    for (ax, az, bx, bz) in (extra_water or []):              # Wasser weiterer Gebiete (Valheim-Koordinaten): nur wo der Boden unter Wasser liegt
        i0, i1 = int((min(ax, bx) - x0) / cell), int((max(ax, bx) - x0) / cell)
        j0, j1 = int((min(az, bz) - z0) / cell), int((max(az, bz) - z0) / cell)
        sl = (slice(max(0, j0), j1 + 1), slice(max(0, i0), i1 + 1))
        wmask[sl] |= known[sl] & (h[sl] < 30.0)
    h = np.where(wmask & (h > 28.5), 28.5, h)
    h = np.where(known & ~wmask & (h < 30.45), 30.45, h)     # Land knapp am Meeresspiegel nicht unter Valheims Wasser (Startbildschirm-Steppe)
    h = h - 0.05
    # Rand: sanft abfallen bis knapp ueber Wasser (kein Meer rundherum); hinter den Waenden erst ein Plateau (Wald)
    start = np.full_like(h, 5.0)
    if nwall:
        # Plateau nur dort, wo das naechste bekannte Gelaende eine Wand-Oberkante ist (weich ueberblendet, keine Kante)
        start = ndimage.gaussian_filter(np.where(add[idx[0], idx[1]], plateau, 5.0), 4.0)
    edge = np.maximum(h - np.clip(dist - start, 0, None) * 0.4, 31.0)
    h = np.where(dist > 0, edge, h)          # nur ausserhalb der Steppe nicht unter Wasser sinken lassen
    h = np.where(wmask, np.minimum(h, 28.5), h)
    h = ndimage.gaussian_filter(h, 0.6)
    if nwall and berm > 0 and kfk.any():
        # Rund um den begehbaren Wald steigt das Gelaende zu einem bewaldeten Hang an (im Original: aufgemalte Baumkulisse,
        # dahinter nichts). Sonst bleiben neben Felsbloecken/Staemmen tiefe Senken, durch die man ins Leere schaut.
        inner = ndimage.binary_fill_holes(kfk)                                   # Haeuser, Plattformen, Teich zaehlen als innen
        dkf, ki = ndimage.distance_transform_edt(~kfk, return_indices=True)
        hfk = ndimage.gaussian_filter(hm[ki[0], ki[1]], 5.0)                     # Bezugshoehe weich (keine Kanten zwischen Boden-Bereichen)
        dd = dkf * cell
        prof = np.minimum(berm, berm_slope * dd) - np.clip(dd - 28.0, 0, None) * 0.25     # Hang, Kamm, dahinter sanft abwaerts
        h = np.where(~inner, np.maximum(h, hfk + prof), h)
    if extra_P is not None and len(extra_P):
        # Remaster Kokiri-Wald: weichere Haenge (Valheim malt steile Gelaendekanten als Fels)
        ex = np.asarray(extra_P, float); kx0, kx1 = ex[:, :, 0].min() - 4, ex[:, :, 0].max() + 4; kz0, kz1 = ex[:, :, 2].min() - 4, ex[:, :, 2].max() + 4
        Xg = x0 + np.arange(W) * cell; Zg = z0 + np.arange(H) * cell
        km = ((Zg[:, None] >= kz0) & (Zg[:, None] <= kz1)) & ((Xg[None, :] >= kx0) & (Xg[None, :] <= kx1))
        hs = np.where(km, np.minimum(h, ndimage.gaussian_filter(h, 1.6)), h)     # nur Kanten abrunden, Boeden (Tunnel!) nie anheben
        for (sx0, sx1, sz0, sz1, *_) in list(steep_boxes or []) + list(flat_boxes or []):
            # Labyrinth: schmale Waende nicht wegrunden (sonst nur Link-hoch)
            a0, a1 = max(0, int((sx0 - x0) / cell)), min(W, int((sx1 - x0) / cell) + 1)
            b0, b1 = max(0, int((sz0 - z0) / cell)), min(H, int((sz1 - z0) / cell) + 1)
            hs[b0:b1, a0:a1] = h[b0:b1, a0:a1]
            # Gangboeden exakt (die leichte Glaettung hebt schmale Gaenge zwischen hohen Waenden sonst an)
            fb = np.zeros(h.shape, bool); fb[b0:b1, a0:a1] = True
            fb &= fk & ~np.isnan(hm)
            hs[fb] = hm[fb] - 0.05
        h = hs
    if tmask is not None:
        # Baumstamm im Hang: wo der Hang hoeher als der Stamm ist, geht das Gelaende ganz darueber (Stamm steckt im Fels);
        # wo er davor frei liegt, bleibt das Gelaende darunter
        dfk = ndimage.distance_transform_edt(~fk) * cell
        cov = tmask & ((h > tbot + 0.5) | (dfk > 2.5))          # nur die Oeffnung direkt am Boden davor bleibt frei
        tc = np.where(cov, ttop + 0.6, np.nan)
        dcv, ci = ndimage.distance_transform_edt(~cov, return_indices=True)
        mound = tc[ci[0], ci[1]] - 0.6 * dcv * cell                # Huegel um den Stamm (fuellt Senken dahinter, kein Durchblick)
        h = np.where(~fk & ~(tmask & ~cov) & (dcv * cell < 16), np.fmax(h, mound), h)
        h = np.where(cov, np.maximum(h, ttop + 0.6), h)
        h = np.where(tmask & ~cov, np.minimum(h, tbot - 0.15), h)
    hmin = float(h.min()); hstep = float(max(h.max() - hmin, 1.0) / 65535.0)
    q = np.clip(np.round((h - hmin) / hstep), 0, 65535).astype("<u2")
    bcell = 8.0
    BW, BH = int((x1 - x0) / bcell) + 1, int((z1 - z0) / bcell) + 1
    bio = np.full((BH, BW), 1, np.uint8)
    forest = np.full((BH, BW), 255, np.uint8)
    buf = bytearray(b"VMAP" + struct.pack("<iiifffff", 1, W, H, x0, z0, cell, hmin, hstep))
    buf += q.tobytes() + struct.pack("<iifff", BW, BH, x0, z0, bcell) + bio.tobytes() + forest.tobytes()
    npaint = 0
    if paint:
        # Erd-Farbe in Valheims Boden (Wege): je Dreieck die Alpha des Original-Flecks abtasten
        pm = np.zeros((H, W), float)
        for P3, UV, A in paint:
            P3 = np.asarray(P3, float); UV = np.asarray(UV, float); th, tw = A.shape
            i0 = max(0, int((P3[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((P3[:, 0].max() - x0) / cell) + 1)
            j0 = max(0, int((P3[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((P3[:, 2].max() - z0) / cell) + 1)
            (ax, az), (bx, bz), (cx, cz) = P3[0, [0, 2]], P3[1, [0, 2]], P3[2, [0, 2]]
            d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
            if abs(d) < 1e-9:
                continue
            X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
            l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
            l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
            l3 = 1 - l1 - l2
            ins = (l1 >= -0.02) & (l2 >= -0.02) & (l3 >= -0.02)
            u = l1 * UV[0, 0] + l2 * UV[1, 0] + l3 * UV[2, 0]; v = l1 * UV[0, 1] + l2 * UV[1, 1] + l3 * UV[2, 1]
            tx = np.mod(np.floor(u * tw), tw).astype(int); ty = np.mod(np.floor(v * th), th).astype(int)
            val = np.where(ins, A[ty, tx], 0.0)
            sub = pm[j0:j1 + 1, i0:i1 + 1]; np.maximum(sub, val, out=sub); npaint += int(ins.sum())
        pm = ndimage.gaussian_filter(pm, 0.7)
        pm = np.clip(pm * 1.1, 0, 0.7)        # Remaster: Wege weicher, Gras schaut durch
        if bare is not None and len(bare):
            # unter Wasserflaechen (Teich, Bach): Erde statt Valheim-Gras (sonst waechst Gras durchs Wasser)
            bm = np.zeros((H, W), bool)
            for T in np.asarray(bare, float):
                i0 = max(0, int((T[:, 0].min() - x0) / cell)); i1 = min(W - 1, int((T[:, 0].max() - x0) / cell) + 1)
                j0 = max(0, int((T[:, 2].min() - z0) / cell)); j1 = min(H - 1, int((T[:, 2].max() - z0) / cell) + 1)
                (ax, az), (bx, bz), (cx, cz) = T[0, [0, 2]], T[1, [0, 2]], T[2, [0, 2]]
                d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz)
                if abs(d) < 1e-9:
                    continue
                X, Z = np.meshgrid(x0 + np.arange(i0, i1 + 1) * cell, z0 + np.arange(j0, j1 + 1) * cell)
                l1 = ((bz - cz) * (X - cx) + (cx - bx) * (Z - cz)) / d
                l2 = ((cz - az) * (X - cx) + (ax - cx) * (Z - cz)) / d
                bm[j0:j1 + 1, i0:i1 + 1] |= (l1 >= -0.1) & (l2 >= -0.1) & (1 - l1 - l2 >= -0.1)
            bm = ndimage.binary_dilation(bm, iterations=1)
            pm = np.maximum(pm, ndimage.gaussian_filter(bm.astype(float), 0.6) * 0.9)
        buf += b"PNT1" + (pm * 255).astype(np.uint8).tobytes()
    with gzip.open(out, "wb", compresslevel=6) as f:
        f.write(bytes(buf))
    return {"W": W, "H": H, "paint_cells": npaint, "h": [round(hmin, 1), round(float(h.max()), 1)], "covered": round(float(known.mean()), 3), "wall_cells": nwall}


def water_boxes(sb):
    off, col = 0, None
    while off + 8 <= len(sb):
        cmd, _, _, d2 = struct.unpack(">BBHI", sb[off:off + 8]); off += 8
        if cmd == 0x14:
            break
        if cmd == 3:
            col = d2 & 0xFFFFFF
    h = sb[col:col + 0x2C]
    nw = struct.unpack(">H", h[0x24:0x26])[0]; wa = struct.unpack(">I", h[0x28:0x2C])[0] & 0xFFFFFF
    return [struct.unpack(">hhhhh", sb[wa + i * 16:wa + i * 16 + 10]) for i in range(nw)]


# ---------------- Bloecke ----------------
def voxelize(tris, vox=1.0, step=0.25):
    """Oberflaechen-Voxel (Valheim-Meter, Raster vox) -> {(i,j,k): Kategorie}.
    Gerundet statt abgeschnitten: Flaechen, die entlang einer Achse nach aussen zeigen, liegen auf der Zellgrenze round(p)
    (Zelle dahinter); sonst wird die Zelle auf den gerundeten Bereich des Dreiecks begrenzt. So wird ein 1,3-m-Kasten
    1 Block hoch statt 3 (vorher floor -> jede angeschnittene Zelle voll)."""
    out = {}
    for cat, k, vs in tris:
        A, B, C = [np.array(V(p), float) / vox for p, _, _ in vs]
        e1, e2 = B - A, C - A
        n = -np.cross(e1, e2)                                    # z gespiegelt -> Umlaufsinn umgekehrt
        nn = np.linalg.norm(n)
        n = n / nn if nn > 1e-9 else np.zeros(3)
        lo = np.round(np.minimum(np.minimum(A, B), C)).astype(int)
        hi = np.round(np.maximum(np.maximum(A, B), C)).astype(int)
        n1 = max(1, int(np.linalg.norm(e1) * vox / step) + 1); n2 = max(1, int(np.linalg.norm(e2) * vox / step) + 1)
        u = np.linspace(0, 1, n1 + 1)[:, None]; w = np.linspace(0, 1, n2 + 1)[None, :]
        U, Wm = np.broadcast_arrays(u, w)
        m = (U + Wm) <= 1.0001
        pts = A + U[m][:, None] * e1 + Wm[m][:, None] * e2
        cells = np.floor(pts).astype(int)
        for ax in range(3):
            if abs(n[ax]) > 0.7:
                cells[:, ax] = np.round(pts[:, ax]).astype(int) - (1 if n[ax] > 0 else 0)
            else:
                cells[:, ax] = np.clip(cells[:, ax], lo[ax], max(lo[ax], hi[ax] - 1))
        for c in np.unique(cells, axis=0):
            out.setdefault(tuple(c), cat)
    return out


PIECES = {  # Kategorie -> (4x2x1, 2x1x1, 1x1x1)
    "stone": ("stone_wall_4x2", "stone_wall_2x1", "stone_wall_1x1"),
    "roof": ("stone_wall_4x2", "stone_wall_2x1", "stone_wall_1x1"),
    "wood": (None, "wood_wall_half", "wood_floor_1x1"),
    "cloth": (None, None, "wood_floor_1x1"),
}


def greedy(vox, vsize=1.0):
    left = dict(vox)
    kit = []

    def has(c, cat):
        return left.get(c) == cat

    def dirpref(c, cat):
        i, j, k = c
        nx = sum(has((i + d, j, k), cat) for d in (-2, -1, 1, 2))
        nz = sum(has((i, j, k + d), cat) for d in (-2, -1, 1, 2))
        return ("x", "z") if nx >= nz else ("z", "x")

    for c in sorted(vox.keys(), key=lambda t: (t[1], t[0], t[2])):
        if c not in left:
            continue
        cat = left[c]
        big, mid, small = PIECES.get(cat, PIECES["stone"])
        i, j, k = c
        placed = False
        for d in dirpref(c, cat):
            if big:
                cells = [((i + a, j + b, k) if d == "x" else (i, j + b, k + a)) for a in range(4) for b in range(2)]
                if all(has(x, cat) for x in cells):
                    cx = (i + 2, j + 1, k + 0.5) if d == "x" else (i + 0.5, j + 1, k + 2)
                    kit.append([big, cx[0] * vsize, cx[1] * vsize, cx[2] * vsize, 0 if d == "x" else 90])
                    for x in cells:
                        left.pop(x)
                    placed = True
                    break
        if placed:
            continue
        for d in dirpref(c, cat):
            if mid:
                cells = [(i + a, j, k) if d == "x" else (i, j, k + a) for a in range(2)]
                if all(has(x, cat) for x in cells):
                    cx = (i + 1, j + 0.5, k + 0.5) if d == "x" else (i + 0.5, j + 0.5, k + 1)
                    kit.append([mid, cx[0] * vsize, cx[1] * vsize, cx[2] * vsize, 0 if d == "x" else 90])
                    for x in cells:
                        left.pop(x)
                    placed = True
                    break
        if placed:
            continue
        kit.append([small, (i + 0.5) * vsize, (j + 0.5) * vsize, (k + 0.5) * vsize, 0])
        left.pop(c)
    return kit


def tilt_camera(tr, f0, f1, deg):
    """Kamera in [f0, f1) um ihre eigene Querachse neigen (negativ = nach unten)."""
    rot = tr["cam"]["rot"]
    a = math.radians(deg) / 2.0
    for f in range(f0, min(f1, len(rot) // 4)):
        x, y, z, w = rot[f * 4:f * 4 + 4]
        # q * qx(-deg): lokale x-Achse (Unity: positive Drehung um x = nach unten), daher Vorzeichen umdrehen
        qx, qw = math.sin(-a), math.cos(-a)
        nx = w * qx + x * qw
        ny = y * qw + z * qx
        nz = z * qw - y * qx
        nw = w * qw - x * qx
        rot[f * 4:f * 4 + 4] = [round(nx, 6), round(ny, 6), round(nz, 6), round(nw, 6)]


def drawbridge_kit():
    """Zugbruecke als geschlossenes Deck aus Valheim-Bodenbrettern (2x2) oben und unten + Randbalken.
    (Vorher Voxel -> aufrecht stehende Halbwaende mit Luecken: beim Hochklappen sah man durch die Bretter.)
    Original: x -180..180, Dicke 20, Laenge 420 ab dem Scharnier (0, -10, 670)."""
    kit = []
    x0, x1 = -180 * S, 180 * S
    ztop = oot_pack.Y0 + 0.0 * S
    zb = oot_pack.Y0 - 20 * S
    zs, ze = -(670 - 10) * S, -(670 + 410) * S          # Valheim-z: Scharnier ... Ende
    nx = int(math.ceil((x1 - x0) / 2.0)); nz = int(math.ceil(abs(ze - zs) / 2.0))
    sx = (x1 - x0) / nx; sz = (zs - ze) / nz
    for i in range(nx):
        for j in range(nz):
            cx = x0 + (i + 0.5) * sx; cz = zs - (j + 0.5) * sz
            kit.append(["wood_floor", round(cx, 3), round(ztop - 0.06, 3), round(cz, 3), 0])
            kit.append(["wood_floor", round(cx, 3), round(zb + 0.04, 3), round(cz, 3), 0])
    return kit


def kits_by_tile(kit, tile=32.0):
    t = collections.defaultdict(list)
    for e in kit:
        t[(int(math.floor(e[1] / tile)), int(math.floor(e[3] / tile)))].append([e[0]] + [round(v, 3) for v in e[1:]])
    st = []
    for (a, b), l in sorted(t.items()):
        P = np.array([e[1:4] for e in l], float)
        c = P.mean(0)
        st.append({"kit": l, "p": [round(v, 3) for v in c], "r": round(float(np.linalg.norm(P - c, axis=1).max()) + 3, 2),
                   "c": 1, "vis": 900})
    return st


# ---------------- Originalform mit Valheim-Material ----------------
def _clip(poly, axis, c, keep_ge):
    """Sutherland-Hodgman an der Ebene coord[axis] = c; poly = [(P(3), uv(2)), ...]"""
    out = []
    n = len(poly)
    for i in range(n):
        P, Q = poly[i], poly[(i + 1) % n]
        pin = (P[1][axis] >= c) if keep_ge else (P[1][axis] <= c)
        qin = (Q[1][axis] >= c) if keep_ge else (Q[1][axis] <= c)
        if pin:
            out.append(P)
        if pin != qin:
            t = (c - P[1][axis]) / (Q[1][axis] - P[1][axis])
            out.append((P[0] + (Q[0] - P[0]) * t, P[1] + (Q[1] - P[1]) * t))
    return out


def retex_statics(built, out_dir, rel_dir, tile=640):
    """Originalform, Valheim-Material: Box-Projektion in Atlas-Kacheln; Dreiecke am Kachelraster geschnitten,
    jedes Stueck bekommt den Atlas-Ausschnitt des Valheim-Bauteils (sonst sieht man den ganzen Atlas)."""
    class M:
        pass
    mats, tris = {}, []
    for cat, k, vs in built:
        pf, (tw, th), (u0, u1, v0, v1) = oot_style.VALHEIM.get(cat, oot_style.VALHEIM["stone"])
        mk = ("v", cat)
        if mk not in mats:
            m = M(); m.key = mk; m.rgba = None; m.prim = (200, 200, 200, 255); m.xlu = False; m.alpha = False
            m.valheim = pf
            mats[mk] = m
        P3 = [np.array(p, float) for p, _, _ in vs]
        n = np.abs(np.cross(P3[1] - P3[0], P3[2] - P3[0]))
        ax = int(np.argmax(n))
        poly = []
        for p in P3:
            q = p * S
            u, v = ((q[2], q[1]) if ax == 0 else (q[0], q[2]) if ax == 1 else (q[0], q[1]))
            poly.append((p, np.array([u / tw, v / th])))
        uv_ = np.array([x[1] for x in poly])
        for iu in range(int(math.floor(uv_[:, 0].min())), int(math.floor(uv_[:, 0].max())) + 1):
            pu = _clip(_clip(poly, 0, iu, True), 0, iu + 1, False)
            if len(pu) < 3:
                continue
            for iv in range(int(math.floor(uv_[:, 1].min())), int(math.floor(uv_[:, 1].max())) + 1):
                pv = _clip(_clip(pu, 1, iv, True), 1, iv + 1, False)
                if len(pv) < 3:
                    continue
                pts = []
                for p, t in pv:
                    fu, fv = t[0] - iu, t[1] - iv
                    uu = u0 + (u1 - u0) * fu
                    vv = v0 + (v1 - v0) * fv                 # Unity-UV
                    pts.append((tuple(p), (uu, 1.0 - vv), (255, 255, 255, 255)))   # glTF: v gespiegelt
                for j in range(1, len(pts) - 1):
                    tris.append((mk, [pts[0], pts[j], pts[j + 1]]))
    return oot_pack.write_tiles(tris, mats, out_dir, rel_dir, "rt")


VTEX = os.environ.get("GI_VTEX", "/home/claude/work/vtex")   # aus dem Spiel geholt: statics dumptex stone_wall_4x2


ALL_DETAIL = True


def stone_blend(m, dens, vtex=VTEX, f=3, dark=1.0, base="stone", strong=True):
    """Original-Detailtextur -> Valheim-Stein (oder -Holz) mit Relief/Farbe des Originals (wie in den Stein gemeisselt).
    dens = (Pixel pro Meter in u, in v)."""
    from PIL import Image
    if base == "wood":
        patch = np.array(Image.open(os.path.join(vtex, "wood.png")).convert("RGB"), float)
        tw, th = 2.0, 2.0
    elif base == "bark":
        patch = np.array(Image.open(os.path.join(vtex, "tex_Oak1_0.png")).convert("RGB").crop((80, 40, 300, 330)), float)
        tw, th = 2.0, 2.6
    elif base == "leaf":
        patch = np.array(Image.open(os.path.join(vtex, "tex_Beech1_0.png")).convert("RGB").crop((30, 30, 230, 150)), float)
        tw, th = 2.0, 1.2
    else:
        st = np.array(Image.open(os.path.join(vtex, "stone.png")).convert("RGB"), float)
        H0 = st.shape[0]
        patch = st[int((1 - 0.40) * H0):int((1 - 0.01) * H0), 0:int(0.72 * st.shape[1])]   # Flaeche der 4x2-Steinwand
        tw, th = 4.0, 2.0
    ph, pw = patch.shape[:2]
    det = np.array(Image.frombytes("RGBA", (m.w, m.h), m.rgba).resize((m.w * f, m.h * f), Image.BILINEAR), float)
    h, w = det.shape[:2]
    ys, xs = np.mgrid[0:h, 0:w]
    sx = ((xs / (dens[0] * f)) % tw / tw * pw).astype(int) % pw
    sy = ((ys / (dens[1] * f)) % th / th * ph).astype(int) % ph
    stone = patch[sy, sx]
    # Valheims Steinmuster gedaempft, sonst geht das feine Relief (Fluegel) im Steinrauschen unter
    sm = stone.reshape(-1, 3).mean(0)
    stone = sm + 0.45 * (stone - sm)
    rgb = det[..., :3]
    L = (rgb @ [0.299, 0.587, 0.114]) / 255.0
    vis = det[..., 3] > 127
    # Kontrast auf den hellen Bereich (Wandflaeche vs. Fluegel) gespreizt; Fenster/Rillen fallen ins Dunkle
    lo, hi = (np.percentile(L[vis], 22), np.percentile(L[vis], 99)) if vis.any() else (0, 1)
    Ln = np.clip((L - lo) / max(hi - lo, 0.05), 0, 1)
    # Wappen deutlich sichtbar: starkes Relief (Fluegel hell wie frisch behauener Stein, Rillen tief dunkel),
    # Gold-Triforce kraeftig und heller als der Stein
    # Wappen: starkes Relief; normale Mauern: weicher, sonst wirkt das Fugenmuster wie Tarnfleck
    if strong:
        relief = (0.42 + 1.0 * Ln ** 1.3)[..., None]
    else:
        # Mauern wie im Original: Kantenschatten/Fugen (feine Helligkeitswechsel) bleiben deutlich, grosse helle/dunkle
        # Flecken innen nur ganz dezent -> Hochpass auf der Helligkeit + wenig Tiefpass
        from scipy.ndimage import gaussian_filter
        low = gaussian_filter(L, sigma=2.5 * f, mode="wrap")
        hp = L - low
        sd = max((np.percentile(hp[vis], 84) - np.percentile(hp[vis], 16)) / 2.0, 0.02) if vis.any() else 0.05
        hpn = np.clip(hp / sd, -2.8, 1.6)
        lmed = float(np.median(low[vis])) if vis.any() else 0.5
        relief = np.clip(0.92 + 0.11 * hpn + 0.18 * (low - lmed), 0.45, 1.2)
        # grosse wirklich dunkle Stellen (Fenster, Oeffnungen) bleiben dunkel
        darkm = np.clip((0.3 - low) / 0.2, 0, 1)
        relief = (relief * (1 - 0.7 * darkm))[..., None]
        stone = sm + 0.6 * (stone - sm)                        # Valheim-Stein noch ruhiger
    mx, mn = rgb.max(-1), rgb.min(-1)
    satur = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1), 0)[..., None]
    hue = rgb / np.maximum(L[..., None] * 255.0, 1.0)
    k = np.clip(0.2 + 1.8 * satur, 0, 1)                        # bunte Stellen (Gold-Triforce) behalten Farbe
    gold = 1.0 + 0.6 * np.clip(satur * 2.5 - 0.3, 0, 1)            # Gold leuchtet etwas
    out = stone * relief * ((1 - k) + k * hue) * gold * dark
    o = np.concatenate([np.clip(out, 0, 255), det[..., 3:4]], -1).astype(np.uint8)
    return o.tobytes(), w, h


def detail_statics(tris, mats, vox, out_dir, rel_dir, cats=None):
    """Original-Dreiecke (Zelda-Details) entlang ihrer Hauptachse nach aussen auf die Block-Oberflaeche schieben."""
    moved = []
    for k, vs in tris:
        P = [np.array(p, float) for p, _, _ in vs]
        n = np.cross(P[1] - P[0], P[2] - P[0])                 # OoT: gegen den Uhrzeigersinn = Vorderseite
        if np.linalg.norm(n) == 0:
            continue
        nv = np.array([n[0], n[1], -n[2]])                       # Valheim-Achsen (z gespiegelt)
        ax = int(np.argmax(np.abs(nv))); sg = 1.0 if nv[ax] > 0 else -1.0
        d = 0.0; d0 = 0.0
        for p in P:
            q = V(p)[ax]
            bound = round(q)                                     # Blockgrenze wie in voxelize (gerundet)
            d0 = max(d0, abs(bound - q))
            # Block ragt ggf. eine Zelle weiter (Nachbarvoxel in Normalenrichtung belegt?)
            idx = [int(math.floor(c)) for c in V(p)]
            idx[ax] = bound - 1 if sg > 0 else bound
            for step in range(1, 3):
                idx2 = list(idx); idx2[ax] += int(sg) * step
                if tuple(idx2) in vox:
                    bound = (idx2[ax] + 1) if sg > 0 else idx2[ax]
            d = max(d, abs(bound - q))
        if d > 0.6:
            d = d0                                               # "Nachbar" ist ein anderes Bauteil -> an der eigenen Grenze bleiben
        d += 0.13                                                # Valheims Steine sind etwas groesser als 1 m
        off = [0.0, 0.0, 0.0]; off[ax] = sg * d / S
        if ax == 2:
            off[2] = -off[2]                                     # zurueck in OoT-z
        moved.append((k, [((p[0] + off[0], p[1] + off[1], p[2] + off[2]), uv, (255, 255, 255, 255)) for p, uv, col in vs]))
    dm = {}
    acc = collections.defaultdict(list)
    for k, vs in tris:
        for _, _, col in vs:
            acc[k].append(col[:3])
    import copy
    for k, _ in moved:
        if k in dm:
            continue
        m = copy.copy(mats[k]); m.xlu = False
        hx = "%x" % (k[0] & 0xFFFFFF) if isinstance(k[0], int) else ""
        cat = (cats or {}).get(k, "stone" if hx in oot_style.STONE_DETAIL else "cloth")
        if cat in ("stone", "roof", "wood") and m.rgba is not None:
            # Texeldichte (Pixel pro Meter) getrennt fuer u und v: Jacobi der Abbildung Ebene -> Texturpixel
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
                    A = np.linalg.solve(X, Y).T                     # uv = A @ xy
                except np.linalg.LinAlgError:
                    continue
                du.append(np.linalg.norm(A[0])); dv.append(np.linalg.norm(A[1]))
            m.rgba, m.w, m.h = stone_blend(m, (float(np.median(du)) if du else 10.0, float(np.median(dv)) if dv else 10.0),
                                           base="wood" if cat == "wood" else "stone", strong=hx in oot_style.STONE_DETAIL)
            m.key = ("stoneblend",) + tuple(k)
        else:
            m.tint = [0.62, 0.6, 0.58]                     # Stoff: nur dunkler
        dm[k] = m
    return oot_pack.write_tiles(moved, dm, out_dir, rel_dir, "dt")


def build(root, out, style="both", version="gc-eu"):
    rom = os.path.join(root, "extracted", version, "baserom")
    assets = os.path.join(root, "extracted", version, "assets")
    os.makedirs(out, exist_ok=True)
    rep = {"Y0": round(oot_pack.Y0, 2)}
    sb, tris, mats = oot_pack.scene_tris(rom, "spot00", "field")
    cl = classify(tris)
    rep["cats"] = {k: len(v) for k, v in cl.items()}
    nat = [t for c in oot_style.NATURAL for t in cl.get(c, [])]
    # Kokiri-Wald als zweites Gebiet (eigene Statics, Boden in dieselbe vmap)
    import oot_kf, oot_kf_decor
    kf_floor, kf_water, rep["kf"] = oot_kf.build(root, out)
    kw = []
    for (wx, wy, wz, xl, zl) in kf_water:
        a, b = oot_kf.V((wx, wy, wz)), oot_kf.V((wx + xl, wy, wz + zl))
        kw.append((a[0], a[2], b[0], b[2]))
    rep["vmap"] = make_vmap_natural(nat, water_boxes(sb), os.path.join(out, "oot.vmap"), extra_P=kf_floor, extra_water=kw, paint=oot_kf.LAST_PAINT, ceilings=oot_kf.LAST_CEIL_V, supports=oot_kf.LAST_SUPPORTS,
                                    water_tris=[[V(p) for p, _, _ in vs] for _, vs in cl.get("water", [])], walls=oot_kf.LAST_WALL_V, tubes=oot_kf.LAST_TUBE_V, bare=oot_kf.LAST_WATER_V,
                                    flat_boxes=[oot_kf_decor.MAZE], ground_tris=oot_kf.LAST_GROUND_V,
                                    keep_paths=__import__("oot_kf2").boulder_paths())
    rep["kf_retile"] = oot_kf.retile(out, os.path.join(out, "oot.vmap"))       # Stufenwaende, die das Gelaende zudeckt, weg
    built = [(c, k, vs) for c in ("stone", "wood", "roof", "cloth") for k, vs in cl.get(c, [])]
    bt, _bm = oot_pack.object_tris(rom, "object_spot00_objects", 0x06000430)
    bridge = [("wood", k, [((p[0], p[1] - 10, p[2] + 670), uv, col) for p, uv, col in vs]) for k, vs in bt]   # Zugbruecke (unten)
    if style in ("blocks", "both"):
        vox = voxelize(built)
        kit = greedy(vox)
        st = kits_by_tile(kit)
        # Zugbruecke: eigenes Kit, Ursprung = Scharnier (Akteur Bg_Spot00_Hanebasi bei 0, -10, 670), Tag fuer den Cutscene-Mover
        bk = drawbridge_kit()
        st.append({"kit": [[e[0]] + [round(v, 3) for v in e[1:]] for e in bk], "p": V((0, -10, 670)), "r": 16, "c": 1, "vis": 900, "t": ["drawbridge"]})
        # Zelda-Details auf die Bloecke + Zugbrueckenketten (Original-Modell)
        # Original-Texturen ueberall auf die Bloecke legen (wie beim Wappen: Relief/Farbe des Originals in Valheim-Stein/-Holz)
        det = [(k, vs) for c in ("stone", "wood", "roof", "cloth") for k, vs in cl.get(c, []) if ALL_DETAIL or oot_style.is_detail(k)]
        cats = {k: c for c in ("stone", "wood", "roof", "cloth") for k, vs in cl.get(c, [])}
        for k in list(cats):
            if oot_style.is_detail(k) and "%x" % (k[0] & 0xFFFFFF) not in oot_style.STONE_DETAIL:
                cats[k] = "cloth"
        sd = detail_statics(det, mats, vox, os.path.join(out, "statics", "detail"), "statics/detail", cats)
        ct, cm = oot_pack.object_tris(rom, "object_spot00_objects", 0x060000C0)
        for x in (158, -158):     # Kettenanker: Brueckenende (Hanebasi_Draw: Bruecke * (+-158, 10, 400))
            sd.append(oot_pack.write_single(ct, cm, os.path.join(out, "statics", "detail"), "statics/detail/chain_%s.glb" % ("r" if x > 0 else "l"),
                                            (x, 0, 1070), "chain", {"c": 0, "t": ["chain_r" if x > 0 else "chain_l"]}))
        json.dump(sd, open(os.path.join(out, "statics_detail.json"), "w"), separators=(",", ":"))
        rep["detail_tris"] = len(det)
        json.dump(st, open(os.path.join(out, "statics_blocks.json"), "w"), separators=(",", ":"))
        rep["voxels"] = len(vox); rep["pieces"] = len(kit)
        rep["piece_types"] = dict(collections.Counter(e[0] for e in kit))
    if style in ("retex", "both"):
        st2 = retex_statics(built, os.path.join(out, "statics", "retex"), "statics/retex")
        # Valheim-Material in die GLBs schreiben (extras) - write_tiles kennt nur Texturen, daher hier nachtragen
        rep["retex_tiles"] = len(st2)
        json.dump(st2, open(os.path.join(out, "statics_retex.json" if style == "retex" else "statics_retex.json.off"), "w"),
                  separators=(",", ":"))
    # Cutscene + Rest wie gehabt
    csp = os.path.join(assets, "scenes", "overworld", "spot00", "gHyruleFieldIntroNightmareCs.csdata.inc.c")
    return rep, csp


def _rotx(th):
    c, s_ = math.cos(th), math.sin(th)
    return np.array([[1, 0, 0], [0, c, -s_], [0, s_, c]])        # OoT Matrix_RotateX: y -> (0,c,s), z -> (0,-s,c)


def _q(R):
    return oot_pack.quat_look(R[:, 2], R[:, 1])


def drawbridge_movers(n, flag_frame=10):
    """Bg_Spot00_Hanebasi: Bruecke von -0x4000 auf 0 (Schritt 80 * R_UPDATE_RATE/2 = 120 pro Spielbild),
    Ketten ab Bruecke >= -0x27D8 von 0xF020 auf 0 (32 * 1.5 = 48 pro Bild). Ketten haengen am Brueckenende."""
    M = np.diag([1.0, 1.0, -1.0]); F = np.diag([-1.0, 1.0, 1.0])
    rb, rc = -0x4000, -0x0FE0
    out = {"drawbridge": {"pos": [], "rot": []}, "chain_r": {"pos": [], "rot": []}, "chain_l": {"pos": [], "rot": []}}
    hinge = np.array([0.0, -10.0, 670.0])
    for f in range(n):
        if f > flag_frame:
            rb = min(0, rb + 120)
            if rb >= -0x27D8:
                rc = min(0, rc + 48)
        Rb = _rotx(rb * 2 * math.pi / 65536.0); Rc = _rotx(rc * 2 * math.pi / 65536.0)
        out["drawbridge"]["pos"] += V(hinge); out["drawbridge"]["rot"] += _q(M @ Rb @ M)
        for tag, x in (("chain_r", 158.0), ("chain_l", -158.0)):
            anchor = hinge + Rb @ np.array([x, 10.0, 400.0])
            out[tag]["pos"] += V(anchor); out[tag]["rot"] += _q(M @ Rc @ F)
    return out


def nightmare_env(cs, fps):
    """Nacht, Regen, Fackeln am Tor (Hanebasi_DrawTorches: +-260, 128, 690), Blitze aus CS_MISC_LIGHTNING."""
    fl = [[round(m["start"] / fps, 3), 0.18, 0.55] for m in cs["misc"] if str(m["type"]).endswith("LIGHTNING")]
    return {"weather": "Rain", "ambient": [0.13, 0.18, 0.34], "fog_color": [0.05, 0.08, 0.18], "fog": 2.5, "sun": 0.25,
            "fire": [{"pos": V((x, 128, 690)), "size": 0.9} for x in (260, -260)],
            "lights": [{"pos": V((x, 140, 700)), "color": [1.0, 0.55, 0.25], "range": 9, "intensity": 2.2} for x in (260, -260)],
            "flashes": sorted(fl)}


def build_all(root, out, style):
    # Grundpaket (Cutscene, Besetzung, pack.json) mit der neuen Hoehe, dann Optik ersetzen
    base = oot_pack.build(root, out)
    os.replace(os.path.join(out, "statics_spot00.json"), os.path.join(out, "statics_spot00.json.off"))
    rep, _ = build(root, out, style)
    pk = json.load(open(os.path.join(out, "pack.json")))
    pk["grass"] = True; pk["vegetation"] = False; pk.pop("vc_shader", None)
    pk["source"]["style"] = "valheim-" + style
    json.dump(pk, open(os.path.join(out, "pack.json"), "w"), indent=1)
    rep["cuts"] = base["cuts"]
    tp = os.path.join(out, "cutscenes", "nightmare.json")
    tr = json.load(open(tp))
    n = len(tr["cam"]["fov"])
    csp = os.path.join(root, "extracted", "gc-eu", "assets", "scenes", "overworld", "spot00", "gHyruleFieldIntroNightmareCs.csdata.inc.c")
    cs = oot_cutscene.structure(oot_cutscene.parse(open(csp).read()))
    tr["movers"] = drawbridge_movers(n)
    # Wunsch: in den Ganondorf-Nahaufnahmen (Schnitte 381-441) Kamera etwas weiter nach unten auf Ganondorf
    tilt_camera(tr, 381, 411, 0.0)
    tilt_camera(tr, 411, 441, 0.0)
    # Original-Animationen (En_Viewer-Logik) als Knochendateien fuer Zelda, Impa, Ganondorf
    import oot_bones
    tabs = oot_bones.nightmare_tabs(cs, n)

    def opath(cues, spawn):
        out, cur = [], list(spawn)
        for f in range(n):
            c = oot_cutscene.cue_at(cues, f)
            if c is not None:
                cur = oot_cutscene.lerp_pos(c, f)
            out.append(list(cur))
        return out
    paths = {"zelda": opath(cs["actors"].get("ch0_2", []), (2313, -11, 2013)),
             "impa": opath(cs["actors"].get("ch0_2", []), (2313, -11, 2013)),
             "ganondorf": opath(cs["actors"].get("ch1_1", []), (2361, -9, 2062))}
    rep["bones"] = {}
    # Sichtbarkeit wie EnViewer_Draw: erst ab dem ersten Cue ihres Kanals (actorCues[n] bleibt danach gesetzt)
    first0 = min(c["start"] for c in cs["actors"].get("ch0_2", [])) if cs["actors"].get("ch0_2") else 0
    first1 = min(c["start"] for c in cs["actors"].get("ch1_1", [])) if cs["actors"].get("ch1_1") else 0
    shown = {"zelda": first0, "impa": first0, "ganondorf": first1, "horse_zelda": first0, "horse_ganon": first1}
    hide = {k: [f < v for f in range(n)] for k, v in shown.items()}
    # Zelda-Nahaufnahme (Schnitt 281-321): Valheims Impa-Koerper ist viel breiter als im Original und verdeckt Zelda
    # komplett (im Original sieht man von Impa nur einen Arm) -> Impa in dieser Einstellung ausblenden
    # (Impa bleibt sichtbar und haelt Zelda; in der Nahaufnahme nur etwas zur Seite/runter, damit Zelda frei ist)
    for k in ("zelda", "impa", "ganondorf"):
        ch, tl = tabs[k]
        ba = oot_bones.bones(ch, tl, paths[k], [0] * n, tr["fps"], hide=hide[k])
        rep["bones"][k] = oot_bones.write_bones(os.path.join(out, "cutscenes", "nightmare.%s.bones" % k), ba)
        a = tr["actors"].setdefault(k, {})
        a.update({"step": 1, "pos": [v for p_ in paths[k] for v in V(p_)], "yaw": [180.0] * n, "pose": [0] * n,
                  "bones": "nightmare.%s.bones" % k, "scale": 1.0, "head_scale": 1.25 if k == "zelda" else 1.0})
        a["exact_hip"] = True          # Reiter: Sattelhoehe wie im Original (sonst sinkt Impa hinter Zelda ein)
        if k == "impa":
            # Zelda-Nahaufnahme: im Original steckt die Kamera praktisch in Impa (N64 zeichnet ihre Rueckseiten nicht,
            # man sieht nur ihre Arme um Zelda). Valheims Impa ist breiter -> ein Stueck nach hinten/oben, damit Zelda frei ist
            # Moritz 09.11.: Impa sass so zu weit hinten -> naeher an Zelda (0,75 -> 0,4 m nach hinten)
            a["shift"] = [[round(281 / tr["fps"], 3), round(321 / tr["fps"], 3), -0.15, 0.05, 0.4]]
    # Link (Kind): Spieler-Cues 5/1/6 wie z_player.c
    lk, ltabs, lpos, lyaw = oot_bones.nightmare_link_tabs(cs, n)
    ba = oot_bones.bones(lk, ltabs, lpos, lyaw, tr["fps"])
    rep["bones"]["link"] = oot_bones.write_bones(os.path.join(out, "cutscenes", "nightmare.link.bones"), ba)
    # Navi: Valheims Wisp-Licht (demister_ball) kreist ueber Links Kopf, leicht wippend
    hi = ba["joints"].index("Head")
    navi = []
    for f in range(n):
        hx, hy, hz = [float(ba["head"][f][q]) + ba["J"][f, hi, q] / 1000.0 for q in range(3)]
        t = f / tr["fps"]
        ang = t * 2 * math.pi / 4.5
        navi += [round(hx + 0.38 * math.cos(ang), 3), round(hy + 0.32 + 0.07 * math.sin(t * 2 * math.pi * 1.3), 3), round(hz + 0.38 * math.sin(ang), 3)]
    link_navi = {"prefab": "demister_ball", "scale": 0.35, "pos": navi, "fwd": [0.0, 0.0, 1.0] * n, "up": [0.0, 1.0, 0.0] * n, "vis": [1] * n}
    # Link selbst: das Original-Kind-Modell aus dem Spiel (Moritz: auch im Albtraum Link, keine Valheim-Figur)
    import oot_link
    tr["actors"].pop("link", None)
    rep["link_cs"] = oot_link.export_cs(os.path.join(out, "cutscenes", "nightmare_link.glb"), ltabs, tr["fps"])
    link_prop = oot_link.cs_prop("nightmare_link.glb", [v for p_ in lpos for v in V(p_)], lyaw, n)
    # Pferde: Original-OoT-Pferde (Skin + Animationsfolge wie En_Viewer) als GLB-Requisiten
    import oot_horse
    tr["props"] = {}
    for hk, path_key in (("horse_zelda", "zelda"), ("horse_ganon", "ganondorf")):
        rep.setdefault("horses", {})[hk] = oot_horse.export(hk, cs, n, tr["fps"], os.path.join(out, "cutscenes", hk + ".glb"))
        pos = [v for p_ in paths[path_key] for v in V(p_)]
        tr["props"][hk] = {"model": hk + ".glb", "clip": "cs", "scale": round(0.01 * S, 6), "pos": pos,
                           "fwd": [0.0, 0.0, -1.0] * n, "up": [0.0, 1.0, 0.0] * n, "vis": [0 if h else 1 for h in hide[hk]]}
    tr["props"]["navi"] = link_navi
    tr["props"]["link"] = link_prop
    tr["env"] = nightmare_env(cs, tr["fps"])
    tr["time"] = 0.97
    tr["hide_player"] = True
    json.dump(tr, open(tp, "w"), separators=(",", ":"))
    rep["flashes"] = len(tr["env"]["flashes"])
    # Intro: Links Haus (Schlaf + Erzaehler)
    import oot_intro
    rep["intro_sleep"], _ = oot_intro.build_sleep(root, out)
    rep["intro_kf"], _ = oot_intro.build_kf(root, out)
    cl = json.load(open(os.path.join(out, "cutscenes.json")))
    cl = [c for c in cl if c["id"] not in ("intro_sleep", "intro_kf")] + [{"id": "intro_sleep", "track": "cutscenes/intro_sleep.json"},
                                                                          {"id": "intro_kf", "track": "cutscenes/intro_kf.json"}]
    json.dump(cl, open(os.path.join(out, "cutscenes.json"), "w"), indent=1)
    # Startbildschirm (Link auf Epona in der Steppe)
    import oot_title
    rep["title"] = oot_title.build_title(root, out)
    # Remaster-Deko im Kokiri-Wald (Valheim-Pflanzen, Felsen, Original-Steinkreis, Schilder)
    import oot_kf_decor, oot_kf
    cams = json.load(open(os.path.join(out, "cutscenes", "intro_kf.json")))["cam"]["pos"]
    av = []
    for tn in ("nightmare", "title"):
        t_ = json.load(open(os.path.join(out, "cutscenes", tn + ".json")))
        av += t_["cam"]["pos"]
        for pr in (t_.get("props") or {}).values():
            av += pr.get("pos", [])
            # freie Sicht: Punkte auf der Linie Kamera -> Reiter (sonst steht ein Busch vor Link)
            C = np.array(t_["cam"]["pos"], float).reshape(-1, 3); Q = np.array(pr.get("pos", []), float).reshape(-1, 3)
            vis = pr.get("vis") or [1] * len(Q)
            for e in range(0, min(len(C), len(Q)), 3):
                if not vis[e]:
                    continue
                d = np.linalg.norm(Q[e] - C[e])
                if d > 80:
                    continue
                for t in np.linspace(0, 1, max(2, int(d / 1.5))):
                    av += list(C[e] + (Q[e] - C[e]) * t)
    rep["kf_decor"] = oot_kf_decor.write(out, oot_kf.LAST_KEEP_V, oot_kf.LAST_FLOOR_V, os.path.join(out, "oot.vmap"), cams, steppe=av)
    rep["play"] = build_play(out)
    return rep


def build_play(out):
    """Spielbar: Original-Link als Spielfigur + Zelda-HUD (src/OotPlay.cs, src/OotHud.cs), Start in Links Haus (nach dem Aufwachen),
    Tuer Haus <-> Kokiri-Wald als Ortswechsel (Tuergang link_home z -150..-214 / spot04 Eingang 3, ENTR_KOKIRI_FOREST_3)."""
    import oot_link, oot_hud, oot_intro, oot_kf
    pdir = os.path.join(out, "player")
    rep = {"link": oot_link.export(pdir), **oot_hud.export(pdir)}
    LH = lambda p: [float(v) for v in oot_intro.V(p, oot_intro.OFF_LH)]
    KF = lambda p: [round(float(v), 3) for v in oot_kf.V(p)]
    yaw = lambda r: (180.0 - r / 65536.0 * 360.0) % 360.0          # OoT-Drehung -> Valheim-Gierwinkel (z gespiegelt)
    pk = json.load(open(os.path.join(out, "pack.json")))
    pk["oot_play"] = {"model": "player/link_child.glb", "scale": round(0.01 * oot_pack.S, 6), "hud": "player/hud",
                      # Links Haus: warmes Licht von oben + Fensterlicht (wie in der Aufwach-Szene)
                      "lights": [{"pos": LH((0, 130, 0)), "color": [1.0, 0.84, 0.62], "range": 9.0, "intensity": 0.75},
                                 {"pos": LH((-60, 90, -40)), "color": [1.0, 0.8, 0.6], "range": 6.0, "intensity": 0.45},
                                 {"pos": LH((0, 70, 112)), "color": [0.85, 0.95, 0.9], "range": 4.0, "intensity": 0.4}],
                      # Stimmung (Wunsch Moritz): morgens (0.35), Kokiri-Wald in Valheims Daemmerlicht, ab und zu eine Nebel-Boee
                      # (im Spiel mit F7 ausprobieren/speichern -> config/GameImporter/oot_mood.json geht vor)
                      "time": 0.35,
                      "areas": [{"min": [540.0, -280.0], "max": [880.0, -20.0], "env": "Twilight_Clear", "gust": "Misty",
                                 "gust_every": [150, 300], "gust_len": [35, 70]}]}
    pk["interior_light"] = [0.5, 0.42, 0.34]
    # Spielbare Original-Objekte: Truhe mit Kokiri-Schwert, Buesche, Rubine (src/OotItems.cs)
    import oot_items
    pk["oot_items"] = oot_items.export(out)
    try:
        import oot_sfx
        pk["oot_sfx"] = oot_sfx.export_pack(out)
    except Exception as e:
        print("oot_sfx:", e)
    # Bewohner: Kokiri, Saria, Mido mit Original-Modellen, Animationen und Texten (src/OotNpcs.cs)
    import oot_npc
    pk["oot_npcs"] = oot_npc.export(out)
    # Original-Musik (aus dem Emulator mit der eigenen ROM mitgeschnitten, nahtlose Schleife): Kokiri-Wald
    src = os.path.join(os.path.dirname(os.path.abspath(out)), "..", "oot", "gi_audio", "kokiri_forest_loop.ogg")
    src = OOT_DECOMP + "/gi_audio/kokiri_forest_loop.ogg" if not os.path.exists(src) else src
    if os.path.exists(src):
        os.makedirs(os.path.join(out, "music"), exist_ok=True)
        import shutil
        shutil.copy2(src, os.path.join(out, "music", "kokiri_forest.ogg"))
        pk["music"] = {"explore": ["music/kokiri_forest.ogg"], "volume": 0.55, "loop": True}
    pk["start"] = {"pos": LH((-70, 0, -4)), "yaw": yaw(0x4000), "safe": 400}           # neben dem Bett (Aufwach-Stelle)
    json.dump(pk, open(os.path.join(out, "pack.json"), "w"), indent=1)
    spots = [
        {"id": "lh_exit", "pos": LH((0, 0, -132)), "radius": 1.6, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": KF((-31, 100, 1073)), "yaw": yaw(-0x8000)}]},
        {"id": "lh_enter", "pos": KF((-31, 100, 1102)), "radius": 0.6, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": LH((-4, 0, -60)), "yaw": yaw(0)}]},            # Eingang 1 (-4, 0, -114), etwas weiter im Raum (nicht gleich wieder im Ausgang)
    ]
    sp = os.path.join(out, "spots.json")
    old = [s for s in (json.load(open(sp)) if os.path.exists(sp) else []) if s.get("id") not in ("lh_exit", "lh_enter")]
    json.dump(old + spots, open(sp, "w"), indent=1)
    rep["start"] = pk["start"]
    return rep


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("root"); ap.add_argument("out"); ap.add_argument("--style", default="both")
    a = ap.parse_args()
    print(json.dumps(build_all(a.root, a.out, a.style)))
