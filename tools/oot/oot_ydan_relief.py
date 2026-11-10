"""Deku-Baum-Remaster, Relief-Schicht (08.11.): ueber den Original-Waenden und -Decken liegt eine fein unterteilte, nach innen
gewoelbte Schicht aus Wurzeln, Holzfasern und Rindenfurchen (nur Bild, keine Kollision; Spielwelt bleibt 1:1).

  - Waende/Decken: Original-Dreiecke in ~15 Einheiten (0,5 m) unterteilt, entlang der (geglaetteten) Wandnormale versetzt:
      grosse Wurzelstraenge (gestauchtes "ridged" Rauschen -> ueberwiegend senkrecht maeandernde Wuelste),
      mittlere Wuelste, feine Fasern, leichte Beulen. Am Wandfuss flach (Link laeuft an der Wand entlang), nahe Ranken,
      Spinnweben, Tueren und Schaltern blendet das Relief aus.
  - Textur: Valheims Ygga-Spross-Holz (ShootTrunk, Mistlands) mit Normal-Map; in Furchen dunkler, unten an der Wand und auf
    nach oben zeigenden Wulsten Moos.
  - Boeden: unterteilt und an den Waenden abgedunkelt (Kontaktschatten).
Valheims Shader nutzt keine Vertexfarben -> Helligkeit in Stufen als Farbfaktor (eigenes Material je Stufe, gleiche Textur).
"""
import collections
import math
import os

import numpy as np

VMAT = os.environ.get("GI_VMAT", "/home/claude/work/vmat")
L_WALL, L_CEIL, L_FLOOR = 22.0, 30.0, 34.0
D_MIN = 1.6                                   # immer ein Stueck vor der Originalwand (kein Flimmern)
REP_U, REP_V = 100.0, 150.0                   # Holztextur: OoT-Einheiten je Kachel


# ---------------- Rauschen ----------------
_PERM = None
_GR = np.array([[1, 1, 0], [-1, 1, 0], [1, -1, 0], [-1, -1, 0], [1, 0, 1], [-1, 0, 1], [1, 0, -1], [-1, 0, -1],
                [0, 1, 1], [0, -1, 1], [0, 1, -1], [0, -1, -1]], float)


def _perm():
    global _PERM
    if _PERM is None:
        p = np.random.default_rng(1234).permutation(256)
        _PERM = np.concatenate([p, p]).astype(np.int64)
    return _PERM


def perlin3(x, y, z):
    """Klassisches Perlin-Rauschen (vektorisiert), Werte etwa -1..1."""
    P = _perm()
    x = np.asarray(x, float); y = np.asarray(y, float); z = np.asarray(z, float)
    xi0 = np.floor(x).astype(np.int64); yi0 = np.floor(y).astype(np.int64); zi0 = np.floor(z).astype(np.int64)
    xf, yf, zf = x - xi0, y - yi0, z - zi0
    xi, yi, zi = xi0 & 255, yi0 & 255, zi0 & 255
    fade = lambda t: t * t * t * (t * (t * 6 - 15) + 10)
    u, v, w = fade(xf), fade(yf), fade(zf)

    def g(h, dx, dy, dz):
        gg = _GR[h % 12]
        return gg[..., 0] * dx + gg[..., 1] * dy + gg[..., 2] * dz

    A = P[xi] + yi; AA = P[A] + zi; AB = P[A + 1] + zi
    B = P[xi + 1] + yi; BA = P[B] + zi; BB = P[B + 1] + zi
    l = lambda a, b, t: a + t * (b - a)
    x1 = l(g(P[AA], xf, yf, zf), g(P[BA], xf - 1, yf, zf), u)
    x2 = l(g(P[AB], xf, yf - 1, zf), g(P[BB], xf - 1, yf - 1, zf), u)
    y1 = l(x1, x2, v)
    x3 = l(g(P[AA + 1], xf, yf, zf - 1), g(P[BA + 1], xf - 1, yf, zf - 1), u)
    x4 = l(g(P[AB + 1], xf, yf - 1, zf - 1), g(P[BB + 1], xf - 1, yf - 1, zf - 1), u)
    y2 = l(x3, x4, v)
    return l(y1, y2, w)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def relief_height(P):
    """Relief (OoT-Einheiten, >= 0) an Weltpunkten P (N,3): Wurzeln, Wuelste, Fasern."""
    x, y, z = P[:, 0], P[:, 1], P[:, 2]
    # leichte Verzerrung -> organische Linien statt Rauschen-Raster
    wx = x + 45 * perlin3(x / 260 + 3.1, y / 260, z / 260)
    wz = z + 45 * perlin3(x / 260, y / 260 + 5.7, z / 260 + 1.3)
    n1 = perlin3(wx / 150, y / 760, wz / 150)
    roots = np.sqrt(np.clip(1 - np.abs(n1) / 0.2, 0, 1))                     # Halbzylinder-Profil um die Null-Linie
    env = smoothstep(-0.25, 0.25, perlin3(x / 330 + 7.3, y / 420, z / 330 + 2.2))   # Wurzeln nur stellenweise
    n2 = perlin3(wx / 85 + 3.3, y / 380, wz / 85 + 9.1)
    mid = np.sqrt(np.clip(1 - np.abs(n2) / 0.26, 0, 1))
    bump = perlin3(x / 70 + 11.0, y / 70, z / 70) * 0.5 + 0.5
    return roots * env * 21.0 + mid * 7.0 + bump * 3.0


# ---------------- Unterteilung ----------------
_TPL = {}


def _tpl(n):
    if n not in _TPL:
        pts = [(i, j) for i in range(n + 1) for j in range(n + 1 - i)]
        idx = {p: k for k, p in enumerate(pts)}
        tris = []
        for i in range(n):
            for j in range(n - i):
                tris.append((idx[(i, j)], idx[(i + 1, j)], idx[(i, j + 1)]))
                if i + j < n - 1:
                    tris.append((idx[(i + 1, j)], idx[(i + 1, j + 1)], idx[(i, j + 1)]))
        B = np.array([(1 - (i + j) / n, i / n, j / n) for i, j in pts])
        _TPL[n] = (B, np.array(tris, np.int64))
    return _TPL[n]


class Mesh:
    """Geschweisste Unterteilung: Punkte V, Dreiecke T, je Dreieck die Quelle (Index in src)."""

    def __init__(self):
        self.V, self.T, self.S = [], [], []
        self.key = {}

    def _id(self, room, p):
        k = (room, int(round(p[0] * 4)), int(round(p[1] * 4)), int(round(p[2] * 4)))
        i = self.key.get(k)
        if i is None:
            i = len(self.V); self.key[k] = i; self.V.append(np.asarray(p, float))
        return i

    def add(self, room, P, L, si):
        """Laengste Kante halbieren, bis alle Kanten <= L (auch lange, duenne Dreiecke werden so nicht zu fein)."""
        ids = [self._id(room, p) for p in P]
        stack = [tuple(ids)]
        L2 = L * L
        while stack:
            a, b, c = stack.pop()
            pa, pb, pc = self.V[a], self.V[b], self.V[c]
            e = (((pb - pa) ** 2).sum(), ((pc - pb) ** 2).sum(), ((pa - pc) ** 2).sum())
            k = int(np.argmax(e))
            if e[k] <= L2:
                self.T.append((a, b, c)); self.S.append(si)
                continue
            if k == 0:
                m = self._id(room, (pa + pb) * 0.5); stack += [(a, m, c), (m, b, c)]
            elif k == 1:
                m = self._id(room, (pb + pc) * 0.5); stack += [(a, b, m), (a, m, c)]
            else:
                m = self._id(room, (pc + pa) * 0.5); stack += [(a, b, m), (m, b, c)]

    def arrays(self):
        return np.array(self.V, float), np.array(self.T, np.int64), np.array(self.S, np.int64)


def vertex_normals(V, T, face_n=None):
    """Flaechengewichtete Eckennormalen (face_n: vorgegebene Richtung je Dreieck, sonst aus der Geometrie)."""
    a, b, c = V[T[:, 0]], V[T[:, 1]], V[T[:, 2]]
    fn = np.cross(b - a, c - a)
    if face_n is not None:
        ar = np.linalg.norm(fn, axis=1, keepdims=True)
        fn = face_n * ar
    N = np.zeros_like(V)
    for k in range(3):
        np.add.at(N, T[:, k], fn)
    N /= np.linalg.norm(N, axis=1, keepdims=True) + 1e-9
    return N


# ---------------- Bodenhoehen (fuer "Hoehe ueber dem Boden") ----------------
class FloorGrid:
    def __init__(self, floor_tris, cell=10.0):
        self.cell = cell
        self.g = collections.defaultdict(list)
        for P in floor_tris:
            x0, z0 = P[:, 0].min(), P[:, 2].min(); x1, z1 = P[:, 0].max(), P[:, 2].max()
            n = np.cross(P[1] - P[0], P[2] - P[0])
            if abs(n[1]) < 1e-6:
                continue
            for i in range(int(math.floor(x0 / cell)), int(math.floor(x1 / cell)) + 1):
                for j in range(int(math.floor(z0 / cell)), int(math.floor(z1 / cell)) + 1):
                    cx, cz = (i + 0.5) * cell, (j + 0.5) * cell
                    # Punkt im Dreieck (xz, mit Rand) -> Hoehe auf der Ebene
                    if not _in_tri_xz(P, cx, cz, cell * 0.75):
                        continue
                    y = P[0][1] - (n[0] * (cx - P[0][0]) + n[2] * (cz - P[0][2])) / n[1]
                    self.g[(i, j)].append(float(y))

    def below(self, x, z, y):
        """hoechster Boden unter (x, y, z) (bis 10 Einheiten darueber erlaubt), sonst None"""
        hs = self.g.get((int(math.floor(x / self.cell)), int(math.floor(z / self.cell))))
        if not hs:
            return None
        c = [h for h in hs if h <= y + 10]
        return max(c) if c else None


def _in_tri_xz(P, x, z, tol):
    a, b, c = P[:, [0, 2]]
    p = np.array([x, z])

    def d(u, v, w):
        return (v[0] - u[0]) * (w[1] - u[1]) - (v[1] - u[1]) * (w[0] - u[0])
    s = d(a, b, c)
    if abs(s) < 1e-9:
        return False
    l1, l2, l3 = d(b, c, p) / s, d(c, a, p) / s, d(a, b, p) / s
    if min(l1, l2, l3) >= 0:
        return True
    # Rand: Abstand zu den Kanten
    for u, v in ((a, b), (b, c), (c, a)):
        e = v - u; t = np.clip(np.dot(p - u, e) / (np.dot(e, e) + 1e-9), 0, 1)
        if np.linalg.norm(u + e * t - p) < tol:
            return True
    return False


# ---------------- Texturen ----------------
_CACHE = {}


def _img(name):
    from PIL import Image
    return np.asarray(Image.open(os.path.join(VMAT, name)), float)


def _srgb_to_lin(x):
    x = x / 255.0
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def _decode_normal(nm):
    nx = nm[:, :, 3] / 255.0 * 2 - 1
    ny = _srgb_to_lin(nm[:, :, 1]) * 2 - 1
    n = np.stack([nx, ny, np.sqrt(np.clip(1 - nx * nx - ny * ny, 0, 1))], 2)
    n /= np.linalg.norm(n, axis=2, keepdims=True) + 1e-9
    return n


def wood(moss=0.0, tint=(1.0, 0.93, 0.8)):
    """Ygga-Holz (Fasern) als Kachel, optional bemoost (Anteil moss). -> (rgba, w, h, normal_rgba)"""
    key = ("wood", round(moss, 2), tint)
    if key in _CACHE:
        return _CACHE[key]
    from PIL import Image
    from scipy import ndimage
    col = _img("mat_YggaShoot1_MainTex.png")[:, :, :3]
    n = _decode_normal(_img("mat_YggaShoot1_BumpMap.png"))
    H, W = col.shape[:2]
    # Ast-Knoten unten rechts (wiederholt sich sonst auf jeder Kachel als "Auge") mit Holz von weiter oben ueberdecken
    yy, xx = np.mgrid[0:H, 0:W]
    r = np.hypot(yy - 485, xx - 470)
    wk = smoothstep(50.0, 30.0, r)[:, :, None]
    col = col * (1 - wk) + np.roll(col, (180, 150), axis=(0, 1)) * wk
    n = n * (1 - wk) + np.roll(n, (180, 150), axis=(0, 1)) * wk
    n /= np.linalg.norm(n, axis=2, keepdims=True) + 1e-9
    col = col * np.array(tint)
    if moss > 0:
        mo = _img("mat_SwampTree1_MossTex.png")[:, :, :3]
        mo = np.asarray(Image.fromarray(mo.astype(np.uint8)).resize((W, H), Image.BICUBIC), float)
        mo = mo * np.array([0.72, 0.8, 0.58])          # gedeckter, nicht knallgruen
        rng = np.random.default_rng(9)
        nz = ndimage.gaussian_filter(rng.random((H, W)), 14.0, mode="wrap"); nz = (nz - nz.min()) / (np.ptp(nz) + 1e-9)
        # Moos sitzt in den hellen Faserkaemmen weniger, in Senken mehr: Helligkeit der Textur mit einrechnen
        lum = col.mean(2) / 255.0
        w = np.clip((nz * 0.8 + (1 - lum) * 0.5 - (1 - moss)) * 3.0, 0, 0.92)[:, :, None]
        col = col * (1 - w) + mo * w
        n = n * (1 - w * 0.6) + np.array([0, 0, 1.0]) * (w * 0.6)
        n /= np.linalg.norm(n, axis=2, keepdims=True) + 1e-9
    rgba = np.concatenate([np.clip(col, 0, 255).astype(np.uint8), np.full((H, W, 1), 255, np.uint8)], 2)
    nrgba = np.concatenate([((n * 0.5 + 0.5) * 255).astype(np.uint8), np.full((H, W, 1), 255, np.uint8)], 2)
    _CACHE[key] = (rgba.tobytes(), W, H, nrgba.tobytes())
    return _CACHE[key]


# ---------------- Aufbau ----------------
def _uv_wall(P, room_c, radial):
    """Holz-UVs wie die Grundschicht: zylindrisch um die Raummitte oder eben entlang der Wand."""
    cx, cz = room_c
    if radial is None:
        return [(p[0] / REP_U, p[2] / REP_U) for p in P]
    if radial is True:
        ang = [math.atan2(p[0] - cx, p[2] - cz) for p in P]
        for j in range(1, len(ang)):
            while ang[j] - ang[0] > math.pi: ang[j] -= 2 * math.pi
            while ang[j] - ang[0] < -math.pi: ang[j] += 2 * math.pi
        return [(a * max(60.0, math.hypot(p[0] - cx, p[2] - cz)) / REP_U, -p[1] / REP_V) for a, p in zip(ang, P)]
    t = radial
    return [(float(np.dot(p, t)) / REP_U, -p[1] / REP_V) for p in P]


def mapping(n, P, room_c):
    """-> None (Decke, eben xz) | True (zylindrisch) | Tangente (eben)"""
    if n[1] < -0.6:
        return None
    cx, cz = room_c
    d = P.mean(0) - np.array([cx, 0, cz]); d[1] = 0
    dl = np.linalg.norm(d)
    nh = np.array([n[0], 0, n[2]]); nl = np.linalg.norm(nh)
    if dl > 1 and nl > 1e-6 and abs(np.dot(d / dl, nh / nl)) > 0.55:
        return True
    t = np.array([-n[2], 0.0, n[0]]); t /= (np.linalg.norm(t) or 1.0)
    return t


def build(src, floors, decal_pts, avoid, room_c, tint_of, base_mat, make_mat):
    """src: [(room, kind, P(3x3), n, b)] Wand/Decke; floors: [(room, kind, P, n, b, tex, rep)];
    decal_pts: (N,3) Punkte von Ranken/Netzen/Rahmen; avoid: (M,4) x,y,z,Radius.
    -> Liste (key, [(p, uv, rgba, normal) x3]) fuer Relief + Boeden, dazu dm (Materialien)."""
    from scipy.spatial import cKDTree
    out, dm = [], {}
    white = (255, 255, 255, 255)
    fgrid = FloorGrid([f[2] for f in floors])

    # --- Relief ---
    mesh = Mesh()
    for si, (room, kind, P, n, b) in enumerate(src):
        mesh.add(room, P, L_CEIL if kind == "ceil" else L_WALL, si)
    V, T, S = mesh.arrays()
    srcn = np.array([s[3] for s in src])
    N0 = vertex_normals(V, T, srcn[S])                       # geglaettete Original-Normale (Versatz-Richtung)
    h = relief_height(V)
    # Maske: Ranken/Netze/Rahmen, Tueren/Schalter
    m = np.ones(len(V))
    if len(decal_pts):
        dd, _ = cKDTree(decal_pts).query(V, k=1, distance_upper_bound=80.0)
        m *= smoothstep(14.0, 55.0, np.minimum(dd, 80.0))
    if len(avoid):
        for x, y, z, r in avoid:
            dist = np.linalg.norm(V - np.array([x, y, z]), axis=1)
            m *= smoothstep(r * 0.6, r * 1.1, dist)
    # Hoehe ueber dem Boden (am Wandfuss flach, damit Link nicht in den Wurzeln steckt)
    hab = np.full(len(V), 999.0)
    for i, p in enumerate(V):
        nh = N0[i].copy(); nh[1] = 0; l = np.linalg.norm(nh)
        q = p + (nh / l * 22.0 if l > 0.2 else 0)
        fy = fgrid.below(q[0], q[2], p[1])
        if fy is not None:
            hab[i] = p[1] - fy
    cap = 6.5 + 18.0 * smoothstep(60.0, 190.0, hab)
    d = D_MIN + m * np.minimum(h, cap)
    Vd = V + N0 * d[:, None]
    Nd = vertex_normals(Vd, T)
    # Dreieck nur, wo das Relief wirklich etwas zeigt (sonst liegt es fast auf der Originalwand)
    keep = (m[T].max(1) > 0.03)
    cav = np.clip((d - D_MIN) / 14.0, 0, 1) ** 0.6           # 0 Furche .. 1 Wulst
    lev_c = np.round((0.6 + 0.4 * cav[T].mean(1) - 0.6) / 0.4 * 4) / 4 * 0.4 + 0.6
    up = Nd[T].mean(1)[:, 1]
    hab_t = hab[T].mean(1)
    cen_t = V[T].mean(1)
    mz = perlin3(cen_t[:, 0] / 90 + 2.0, cen_t[:, 1] / 90, cen_t[:, 2] / 90) * 0.5 + 0.5    # Moosgrenze am Wandfuss unruhig
    for ti in np.nonzero(keep)[0]:
        t = T[ti]; room, kind, P0, n0, b = src[S[ti]]
        mossy = kind == "moss" or up[ti] > 0.6 or hab_t[ti] < 2.0 + 24.0 * mz[ti]
        mp = mapping(n0, P0, room_c.get(room, (0.0, 0.0)))
        P = Vd[t]
        uv = _uv_wall(P, room_c.get(room, (0.0, 0.0)), mp)
        c = float(lev_c[ti])
        kk = ("rl", "moss" if mossy else "wood", b, c)
        if kk not in dm:
            base_tint = tint_of(b)
            dm[kk] = make_mat(("rl_tex", "moss" if mossy else "wood"), wood(0.5 if mossy else 0.0),
                              tuple(round(x * c, 3) for x in base_tint))
        out.append((kk, [(tuple(P[j]), uv[j], white, tuple(Nd[t[j]])) for j in range(3)]))
    n_relief = int(keep.sum())
    lights = []
    try:
        _MATS.clear()
        xo, xl = extras(V, Vd, N0, Nd, m, hab, d, fgrid, make_mat)
        out += xo; lights += xl; dm.update(_MATS)
    except Exception as e:                       # Deko darf das Relief nicht verhindern
        import traceback; traceback.print_exc()

    # --- Boeden: unterteilt, Kontaktschatten an den Waenden ---
    wallv = V[np.abs(N0[:, 1]) < 0.5]
    tree = cKDTree(np.c_[wallv[:, 0], wallv[:, 1] * 2.5, wallv[:, 2]]) if len(wallv) else None
    fm = Mesh(); finfo = []
    for fi, (room, kind, P, n, b, tex, rep) in enumerate(floors):
        fm.add(room, P, L_FLOOR, fi)
    FV, FT, FS = fm.arrays()
    if tree is not None:
        q = np.c_[FV[:, 0], (FV[:, 1] + 30.0) * 2.5, FV[:, 2]]
        dist, _ = tree.query(q, k=1, distance_upper_bound=400.0)
        dist = np.minimum(dist, 400.0)
    else:
        dist = np.full(len(FV), 400.0)
    ao = 0.58 + 0.42 * smoothstep(4.0, 85.0, dist)
    for ti in range(len(FT)):
        t = FT[ti]; room, kind, P0, n0, b, tex, rep = floors[FS[ti]]
        c = float(np.round((ao[t].mean() - 0.58) / 0.42 * 5) / 5 * 0.42 + 0.58)
        P = FV[t]
        uv = [(p[0] / rep, p[2] / rep) for p in P]
        kk = ("rf", kind, b, c)
        if kk not in dm:
            dm[kk] = make_mat(("rf_tex", kind), tex, tuple(round(x * c, 3) for x in tint_of(b)))
        out.append((kk, [(tuple(P[j]), uv[j], white) for j in range(3)]))
    print("Relief: %d Punkte, %d Dreiecke (von %d), Boden %d Dreiecke, %d Materialien, %d Lichter" % (len(V), n_relief, len(T), len(FT), len(dm), len(lights)))
    return out, dm, lights


# ---------------- Leuchtpilze (Baumschwaemme) und haengende Wurzeln ----------------
GLOW_WARM, GLOW_COOL = (1.0, 0.62, 0.3), (0.35, 0.85, 1.0)


def fungus_tex(cool):
    """Baumschwamm von oben: u = Winkel, v = 0 Wand .. 1 Rand; Ringe, Rand leuchtet."""
    key = ("fungus", cool)
    if key in _CACHE:
        return _CACHE[key]
    W = H = 128
    u = np.linspace(0, 1, W)[None, :]; v = np.linspace(0, 1, H)[:, None]
    rng = np.random.default_rng(3)
    if cool:
        c0, c1, c2 = np.array([18, 40, 52.0]), np.array([50, 140, 160.0]), np.array([170, 255, 240.0])
    else:
        c0, c1, c2 = np.array([55, 30, 16.0]), np.array([175, 100, 45.0]), np.array([255, 205, 120.0])
    t = v ** 0.8
    col = c0 * (1 - t[..., None]) + c1 * t[..., None]
    rings = 0.8 + 0.2 * np.sin(v * 30 + 2.0 * np.sin(u * 12)) + 0.06 * rng.standard_normal((H, W))
    col = col * rings[..., None]
    # helle Flecken (Sporen-Poren) auf der Oberseite
    spots = rng.random((H, W)) > 0.985
    from scipy import ndimage
    spots = ndimage.gaussian_filter(spots.astype(float), 1.2) * 6.0
    col = col + c2 * np.clip(spots, 0, 1)[..., None] * 0.5 * (v[..., None] > 0.2)
    rim = smoothstep(0.86, 0.98, v)
    col = col * (1 - rim[..., None]) + (c1 * 0.6 + c2 * 0.4) * rim[..., None]     # Rand nur maessig hell (sonst "Neon-Sicheln")
    # v < 0.1: Lamellen der Unterseite (leuchten), strahlenfoermig gestreift
    gills = c2[None, None, :] * (0.7 + 0.3 * np.sin(np.broadcast_to(u, (H, W)) * 140.0) ** 2)[..., None]
    col = np.where((np.broadcast_to(v, (H, W)) < 0.1)[..., None], gills, col)
    rgba = np.concatenate([np.clip(col, 0, 255).astype(np.uint8), np.full((H, W, 1), 255, np.uint8)], 2)
    _CACHE[key] = (rgba.tobytes(), W, H, None)
    return _CACHE[key]


def _shelf(c, nrm, R, rng):
    """Ein Baumschwamm an Punkt c (Wand), Wandnormale nrm -> Dreiecke [(p, uv)] (Ober-, Unterseite, Rand)."""
    nh = np.array([nrm[0], 0.0, nrm[2]]); nh /= (np.linalg.norm(nh) or 1.0)
    side = np.array([-nh[2], 0.0, nh[0]])
    up = np.array([0, 1.0, 0])
    tilt = rng.uniform(0.15, 0.4)
    A = 9
    rows_top, rows_bot = [], []
    for ri, r in enumerate((0.0, 0.5, 1.0)):
        rt, rb = [], []
        for ai in range(A):
            ph = -math.pi / 2 + math.pi * ai / (A - 1)
            dirv = nh * math.cos(ph) * 0.85 + side * math.sin(ph)
            base = c + dirv * (R * r) + up * (R * r * tilt)
            rt.append((base + up * (R * 0.42 * (1 - r * r) + R * 0.06), (ai / (A - 1), 0.15 + r * 0.83)))
            rb.append((base - up * (R * 0.3 * (1 - r)), (ai / (A - 1), 0.04 + 0.04 * r)))
        rows_top.append(rt); rows_bot.append(rb)
    tris = []
    for ri in range(2):
        for ai in range(A - 1):
            a, b_, c_, d_ = rows_top[ri][ai], rows_top[ri][ai + 1], rows_top[ri + 1][ai], rows_top[ri + 1][ai + 1]
            tris += [(a, c_, b_), (b_, c_, d_)]
            a, b_, c_, d_ = rows_bot[ri][ai], rows_bot[ri][ai + 1], rows_bot[ri + 1][ai], rows_bot[ri + 1][ai + 1]
            tris += [(a, b_, c_), (b_, d_, c_)]
    for ai in range(A - 1):                        # Rand (leuchtet)
        a, b_ = rows_top[2][ai], rows_top[2][ai + 1]; c_, d_ = rows_bot[2][ai], rows_bot[2][ai + 1]
        a = (a[0], (a[1][0], 0.99)); b_ = (b_[0], (b_[1][0], 0.99)); c_ = (c_[0], (c_[1][0], 0.06)); d_ = (d_[0], (d_[1][0], 0.06))
        tris += [(a, b_, c_), (b_, d_, c_)]
    return tris


def _root(top, length, r0, rng, steps=7, sides=6):
    """Haengende Wurzel: verjuengte Roehre von top nach unten, leicht gebogen -> Dreiecke [(p, uv)], Spitze."""
    drift = rng.normal(0, 1, 2) * length * 0.12
    pts = []
    for i in range(steps + 1):
        t = i / steps
        off = np.array([drift[0] * t * t, -length * t, drift[1] * t * t])
        pts.append((top + off, r0 * (1 - t) + 0.6 * t, t))
    tris = []
    for i in range(steps):
        (p0, ra, ta), (p1, rb, tb) = pts[i], pts[i + 1]
        for s in range(sides):
            a0, a1 = 2 * math.pi * s / sides, 2 * math.pi * (s + 1) / sides
            q00 = p0 + np.array([math.cos(a0), 0, math.sin(a0)]) * ra; q01 = p0 + np.array([math.cos(a1), 0, math.sin(a1)]) * ra
            q10 = p1 + np.array([math.cos(a0), 0, math.sin(a0)]) * rb; q11 = p1 + np.array([math.cos(a1), 0, math.sin(a1)]) * rb
            u0, u1 = s / sides * 0.5, (s + 1) / sides * 0.5
            v0, v1 = ta * length / REP_V, tb * length / REP_V
            tris += [((q00, (u0, v0)), (q10, (u0, v1)), (q11, (u1, v1))), ((q00, (u0, v0)), (q11, (u1, v1)), (q01, (u1, v0)))]
    return tris, pts[-1][0]


def _blob(c, r):
    """kleine leuchtende Knolle (Oktaeder)"""
    P = [c + np.array(v) * r for v in ((1, 0, 0), (-1, 0, 0), (0, 1.3, 0), (0, -1.3, 0), (0, 0, 1), (0, 0, -1))]
    F = [(0, 2, 4), (4, 2, 1), (1, 2, 5), (5, 2, 0), (0, 4, 3), (4, 1, 3), (1, 5, 3), (5, 0, 3)]
    return [tuple((P[i], (0.5, 1.0)) for i in f) for f in F]


def extras(V, Vd, N0, Nd, m, hab, d, fgrid, make_mat):
    """Leuchtende Baumschwaemme an Wurzelwuelsten, haengende Wurzeln unter Decken/Stegen. -> (tris, lights)"""
    import oot_ydan as Y
    rng = np.random.default_rng(77)
    out, lights = [], []
    white = (255, 255, 255, 255)

    def emit(key, tex, tint, lift, tris):
        kk = ("rx",) + key
        if kk not in _MATS:
            _MATS[kk] = make_mat(("rx_tex",) + key, tex, tint, lift)
        for tri in tris:
            out.append((kk, [(tuple(p), uv, white) for p, uv in tri]))

    # --- Baumschwaemme: auf hohen Wulsten, ueber Kopfhoehe, nicht an Ranken/Tueren ---
    wall = (np.abs(N0[:, 1]) < 0.35) & (m > 0.85) & (hab > 60) & (hab < 420) & (d > 8.0)
    idx = np.nonzero(wall)[0]
    rng.shuffle(idx)
    centers = []
    for i in idx:
        p = Vd[i]
        if any(np.linalg.norm(p - q) < 150 for q in centers):
            continue
        centers.append(p)
        if len(centers) >= 90:
            break
    from scipy.spatial import cKDTree
    tree = cKDTree(Vd)
    for ci_, c in enumerate(centers):
        cool = c[1] < -300
        _, ci = tree.query(c)
        nrm = Nd[ci]
        nh = np.array([nrm[0], 0, nrm[2]]); nh /= (np.linalg.norm(nh) or 1.0)
        side = np.array([-nh[2], 0, nh[0]])
        k = int(rng.integers(4, 8))
        for j in range(k):
            q = c + side * rng.normal(0, 26) + np.array([0, rng.normal(0, 30), 0])
            _, qi = tree.query(q)
            R = float(rng.uniform(10.0, 20.0)) * (1.35 if j == 0 else 1.0)
            emit(("fungus", cool), fungus_tex(cool), (1.0, 1.0, 1.0), 1.25, _shelf(Vd[qi] - nh * 2.0, Nd[qi], R, rng))
        col = GLOW_COOL if cool else GLOW_WARM
        if ci_ % 2 == 0:            # jede zweite Gruppe mit echtem Licht (Valheim: viele Punktlichter kosten)
            lights.append({"pos": Y.YV(tuple(c + nh * 35.0)), "range": 6.5, "intensity": 1.5, "color": list(col), "shadows": False})

    # --- haengende Wurzeln: Decken mit viel Luft darunter (Spitzen bleiben hoch ueber Link) ---
    ceil = (N0[:, 1] < -0.6) & (m > 0.6)
    idx = np.nonzero(ceil)[0]
    rng.shuffle(idx)
    groups = []
    for i in idx:
        p = Vd[i]
        fy = fgrid.below(p[0], p[2], p[1] - 5)
        clear = (p[1] - fy) if fy is not None else 600.0
        if clear < 280:
            continue
        if any(np.linalg.norm(p - q) < 110 for q, _ in groups):
            continue
        groups.append((p, clear))
        if len(groups) >= 90:
            break
    for p, clear in groups:
        cool = p[1] < -300
        for j in range(int(rng.integers(2, 6))):
            q = p + np.array([rng.normal(0, 22), 0, rng.normal(0, 22)])
            _, qi = tree.query(q)
            top = Vd[qi] + np.array([0, 3.0, 0])
            length = float(min(clear - 190.0, rng.uniform(45, 190)))
            if length < 30:
                continue
            tris, tip = _root(top, length, float(rng.uniform(2.5, 6.5)), rng)
            emit(("root",), wood(0.5), (0.9, 0.86, 0.78), 0.55, tris)
            if rng.random() < 0.35:
                emit(("bulb", cool), fungus_tex(cool), (1.0, 1.0, 1.0), 2.2, _blob(tip, float(rng.uniform(2.2, 3.6))))
    return out, lights


_MATS = {}
