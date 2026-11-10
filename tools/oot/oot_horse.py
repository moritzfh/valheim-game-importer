"""OoT-Pferde (Skin-Skelett, z_skin.c) -> GLB mit Skelett, Skin-Gewichten und einem Animationsclip ueber die ganze Cutscene.

Skin-Glied (16 Bytes): jointPos, child, sibling, segmentType (4 = animiert, 11 = starr), segment.
Animiertes Glied: SkinAnimatedLimbData {totalVtxCount, modifCount, modifs*, dlist}; jede Modif = ein Punkt (aus 1..n Gliedern
gewichtet, scale/100) fuer mehrere Eckpunkte (Naehte). Die dlist liest ihre Eckpunkte aus Segment 8 (Puffer je Glied).
Starres Glied: dlist im Glied-Raum.
glTF: Knoten je Glied (lokal T*Rz*Ry*Rx wie SkelAnime), Bindpose = erstes Animationsbild, Animation als Knoten-Kurven.
"""
import os, sys, struct, math, json, collections
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_anim as A
import oot_glb
from oot_gfx import Segs, Gfx, png


class SkinSkel:
    def __init__(self, data, off):
        lp, n = struct.unpack(">IB", data[off:off + 5])
        lp &= 0xFFFFFF
        self.n = n
        self.pos, self.child, self.sib, self.type, self.seg = [], [], [], [], []
        for i in range(n):
            la = struct.unpack(">I", data[lp + i * 4:lp + i * 4 + 4])[0] & 0xFFFFFF
            x, y, z, c, s, t, sg = struct.unpack(">hhhBBiI", data[la:la + 16])
            self.pos.append((x, y, z)); self.child.append(c); self.sib.append(s); self.type.append(t); self.seg.append(sg)
        self.parent = [-1] * n
        for i in range(n):
            c = self.child[i]
            while c != 0xFF:
                self.parent[c] = i
                c = self.sib[c]


def mats(skel, tab):
    """Modellraum-Matrizen (4x4) je Glied."""
    R, T = A.fk(skel, tab)
    out = []
    for i in range(skel.n):
        M = np.eye(4); M[:3, :3] = R[i]; M[:3, 3] = T[i]
        out.append(M)
    return out


def local_trs(skel, tab):
    """lokale Translation + Rotationsmatrix je Glied."""
    out = []
    for i in range(skel.n):
        t = np.array(tab[0] if i == 0 else skel.pos[i], float)
        out.append((t, A.limb_rot(tab[i + 1])))
    return out


def quat(R):
    t = R[0, 0] + R[1, 1] + R[2, 2]
    if t > 0:
        s = math.sqrt(t + 1.0) * 2; w = 0.25 * s
        x = (R[2, 1] - R[1, 2]) / s; y = (R[0, 2] - R[2, 0]) / s; z = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = math.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2; w = (R[2, 1] - R[1, 2]) / s
        x = 0.25 * s; y = (R[0, 1] + R[1, 0]) / s; z = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = math.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2; w = (R[0, 2] - R[2, 0]) / s
        x = (R[0, 1] + R[1, 0]) / s; y = 0.25 * s; z = (R[1, 2] + R[2, 1]) / s
    else:
        s = math.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2; w = (R[1, 0] - R[0, 1]) / s
        x = (R[0, 2] + R[2, 0]) / s; y = (R[1, 2] + R[2, 1]) / s; z = 0.25 * s
    q = np.array([x, y, z, w]); return q / np.linalg.norm(q)


def build_mesh(data, skel, bind_tab):
    """-> tris [(matkey, [(pos, uv, col)]*3)], weights je Dreieckseckpunkt [[(glied, gewicht)...]*3], mats"""
    gk = A.obj("gameplay_keep")
    M = mats(skel, bind_tab)
    out_tris, out_w, allm = [], [], {}
    for li in range(skel.n):
        if skel.type[li] == 4 and skel.seg[li]:
            d = skel.seg[li] & 0xFFFFFF
            total, nmod, mp, dl = struct.unpack(">HHII", data[d:d + 12])
            mp &= 0xFFFFFF
            buf = bytearray(total * 16); wts = {}
            for mi in range(nmod):
                vc, tc, u4, _pad, sv, lt = struct.unpack(">HHHHII", data[mp + mi * 16:mp + mi * 16 + 16])
                sv &= 0xFFFFFF; lt &= 0xFFFFFF
                trs = []
                for k in range(tc):
                    e = data[lt + k * 10:lt + k * 10 + 10]
                    l, x, y, z, sc = e[0], *struct.unpack(">hhh", e[2:8]), e[8]
                    trs.append((l, np.array([x, y, z], float), sc))
                if tc == 1:
                    p = (M[trs[0][0]] @ np.append(trs[0][1], 1))[:3]; w = [(trs[0][0], 1.0)]
                else:
                    p = np.zeros(3); w = []
                    for l, v, sc in trs:
                        p += (M[l] @ np.append(v, 1))[:3] * sc * 0.01; w.append((l, sc * 0.01))
                for k in range(vc):
                    idx, s, t, nx, ny, nz, al = struct.unpack(">Hhhbbbb", data[sv + k * 10:sv + k * 10 + 10])
                    struct.pack_into(">hhhHhhBBBB", buf, idx * 16, int(round(p[0])), int(round(p[1])), int(round(p[2])), 0, s, t,
                                     255, 255, 255, al & 255)
                    wts[0x08000000 + idx * 16] = w
            g = Gfx(Segs({6: data, 4: gk, 8: bytes(buf)}))
            g.reset(); g.run(dl)
            for (k, vs), src in zip(g.tris, g.tri_src):
                out_tris.append((k, vs)); out_w.append([wts.get(a, [(li, 1.0)]) for a in src])
            allm.update(g.mats)
        elif skel.type[li] == 11 and skel.seg[li]:
            g = Gfx(Segs({6: data, 4: gk}))
            g.reset(); g.run(skel.seg[li])
            for k, vs in g.tris:
                vs2 = [(tuple((M[li] @ np.append(np.array(p, float), 1))[:3]), uv, col) for p, uv, col in vs]
                out_tris.append((k, vs2)); out_w.append([[(li, 1.0)]] * 3)
            allm.update(g.mats)
    return out_tris, out_w, allm


# ---------------- Valheimifizieren ----------------
VALHEIMIFY = True
VTEX = os.environ.get("GI_VTEX", "/home/claude/work/vtex")
_detail = None


def _fur_detail():
    """Helligkeits-Relief aus Valheims Hirschfell (Pixel-Atlas, brauner Fellbereich), Mittel 1."""
    global _detail
    if _detail is None:
        from PIL import Image
        p = os.path.join(VTEX, "tex_Deer.png")
        if not os.path.exists(p):
            _detail = False
            return _detail
        im = np.asarray(Image.open(p).convert("RGB"), float)[:128, :128]
        L = im.mean(2)
        d = L / max(L.mean(), 1e-3)
        _detail = np.clip(1.0 + (d - 1.0) * 1.4, 0.6, 1.4)
    return _detail


def valheimify(rgba, w, h, up=8):
    """OoT-Textur in Valheim-Optik: Pixel bleiben (x8 Naechster-Nachbar), Fellkorn aus Valheims Hirschtextur darueber,
    knallige Farben gedaempft: Lila/Blau (Sattelzeug) -> rotbraunes Leder, Gold -> Bronze, insgesamt entsaettigt."""
    import colorsys
    a = np.frombuffer(bytes(rgba), np.uint8).reshape(h, w, 4).astype(float) / 255.0
    rgb = a[..., :3]
    mx = rgb.max(2); mn = rgb.min(2); sat = np.where(mx > 1e-6, (mx - mn) / np.maximum(mx, 1e-6), 0)
    hsv = np.zeros_like(rgb)
    for y in range(h):
        for x in range(w):
            hsv[y, x] = colorsys.rgb_to_hsv(*rgb[y, x])
    H, S, Vv = hsv[..., 0] * 360, hsv[..., 1], hsv[..., 2]
    purple = (H > 190) & (H < 320) & (S > 0.25)
    gold = (H > 30) & (H < 70) & (S > 0.45)
    H2 = np.where(purple, 8.0, np.where(gold, 32.0, H))
    S2 = np.where(purple, np.minimum(S, 0.75) * 0.85, np.where(gold, S * 0.55, S * 0.6))
    V2 = np.where(purple, Vv * 0.62, np.where(gold, Vv * 0.7, Vv * 0.88))
    out = np.zeros_like(rgb)
    for y in range(h):
        for x in range(w):
            out[y, x] = colorsys.hsv_to_rgb(H2[y, x] / 360.0, S2[y, x], V2[y, x])
    W, Hh = w * up, h * up
    big = np.repeat(np.repeat(out, up, 0), up, 1)
    alpha = np.repeat(np.repeat(a[..., 3], up, 0), up, 1)
    det = _fur_detail()
    if det is not False:
        dy = np.arange(Hh) % det.shape[0]; dx = np.arange(W) % det.shape[1]
        big = big * det[dy][:, dx][..., None]
    big = np.clip(big, 0, 1)
    res = np.concatenate([big, alpha[..., None]], 2)
    return (res * 255 + 0.5).astype(np.uint8).tobytes(), W, Hh


def write_glb(path, skel, bind_tab, tabs, fps, tris, wts, mats_, name="horse", clips=None, valheimify_tex=None, nearest=False, normals=None):
    """Skin-GLB: Knoten je Glied, Mesh mit JOINTS_0/WEIGHTS_0, Animation 'cs' (je Bild Drehung aller Glieder + Wurzel-Verschiebung)."""
    bin_ = bytearray(); views, accs = [], []

    def view(data, target=None):
        nonlocal bin_
        while len(bin_) % 4:
            bin_ += b"\0"
        v = {"buffer": 0, "byteOffset": len(bin_), "byteLength": len(data)}
        if target: v["target"] = target
        bin_ += data; views.append(v); return len(views) - 1

    def acc(data, ct, count, typ, mn=None, mx=None, norm=False, target=None):
        a = {"bufferView": view(data, target), "componentType": ct, "count": count, "type": typ}
        if mn is not None: a["min"], a["max"] = mn, mx
        if norm: a["normalized"] = True
        accs.append(a); return len(accs) - 1

    groups = collections.OrderedDict()
    for ti, ((k, vs), w) in enumerate(zip(tris, wts)):
        groups.setdefault(k, []).append((vs, w, normals[ti] if normals is not None else None))
    materials, textures, images, samplers, prims = [], [], [], [], []
    samp = {}
    for k, lst in groups.items():
        m = mats_[k]
        P, N, U, J, W = bytearray(), bytearray(), bytearray(), bytearray(), bytearray()
        mn, mx = [1e30] * 3, [-1e30] * 3; n = 0
        for vs, ws, vn in lst:
            ps = [np.array(p, float) for p, _, _ in vs]
            nrm = np.cross(ps[1] - ps[0], ps[2] - ps[0]); ln = np.linalg.norm(nrm) or 1.0; nrm /= ln
            for q_, ((p, uv, col), w) in enumerate(zip(vs, ws)):
                nv = vn[q_] if vn is not None else nrm
                P += struct.pack("<fff", *p); N += struct.pack("<fff", *[float(x) for x in nv]); U += struct.pack("<ff", *uv)
                w = sorted(w, key=lambda t: -t[1])[:4]
                s = sum(t[1] for t in w) or 1.0
                jj = [t[0] for t in w] + [0] * (4 - len(w)); ww = [t[1] / s for t in w] + [0.0] * (4 - len(w))
                J += struct.pack("<4H", *jj); W += struct.pack("<4f", *ww)
                for q in range(3):
                    mn[q] = min(mn[q], p[q]); mx[q] = max(mx[q], p[q])
                n += 1
        attrs = {"POSITION": acc(bytes(P), 5126, n, "VEC3", mn, mx, target=34962), "NORMAL": acc(bytes(N), 5126, n, "VEC3", target=34962),
                 "TEXCOORD_0": acc(bytes(U), 5126, n, "VEC2", target=34962), "JOINTS_0": acc(bytes(J), 5123, n, "VEC4", target=34962),
                 "WEIGHTS_0": acc(bytes(W), 5126, n, "VEC4", target=34962)}
        mat = {"name": "m%d" % len(materials), "doubleSided": True, "pbrMetallicRoughness": {"metallicFactor": 0.0, "roughnessFactor": 1.0}}
        if m.rgba is not None:
            sk = (m.wrap_s, m.wrap_t)
            if sk not in samp:
                samplers.append({"magFilter": 9728 if nearest else 9729, "minFilter": 9984 if nearest else 9987, "wrapS": sk[0], "wrapT": sk[1]}); samp[sk] = len(samplers) - 1
            vf = VALHEIMIFY if valheimify_tex is None else valheimify_tex
            vr, vw, vh = valheimify(m.rgba, m.w, m.h) if vf else (m.rgba, m.w, m.h)
            images.append({"bufferView": view(png(vr, vw, vh)), "mimeType": "image/png"})
            textures.append({"source": len(images) - 1, "sampler": samp[sk]})
            mat["pbrMetallicRoughness"]["baseColorTexture"] = {"index": len(textures) - 1}
            if m.alpha:
                mat["alphaMode"] = "MASK"; mat["alphaCutoff"] = 0.5
            if getattr(m, "nrm", None) is not None:
                nb, nw, nh = m.nrm
                images.append({"bufferView": view(png(nb, nw, nh)), "mimeType": "image/png"})
                textures.append({"source": len(images) - 1, "sampler": samp[sk]})
                mat["normalTexture"] = {"index": len(textures) - 1, "scale": 1.0}
        if getattr(m, "gloss", None) is not None:
            mat["extras"] = {"gloss": float(m.gloss)}
        if getattr(m, "lift", None):
            mat.setdefault("extras", {})["lift"] = float(m.lift)        # Schatten aufhellen (Eigenleuchten aus der Textur)
        if getattr(m, "part", None):
            mat.setdefault("extras", {})["part"] = m.part               # zur Laufzeit ein-/ausblendbares Teil (Schwert, Scheide, Hand)
        else:
            mat["pbrMetallicRoughness"]["baseColorFactor"] = [m.prim[0] / 255, m.prim[1] / 255, m.prim[2] / 255, 1.0]
        materials.append(mat)
        prims.append({"attributes": attrs, "material": len(materials) - 1})
    # Knoten: 0 = Wurzel (Mesh + Skin), 1.. = Glieder
    lt = local_trs(skel, bind_tab)
    nodes = [{"name": name, "children": [1], "mesh": 0, "skin": 0}]
    for i in range(skel.n):
        t, R = lt[i]
        nd = {"name": "limb%d" % i, "translation": [float(v) for v in t], "rotation": [float(v) for v in quat(R)]}
        ch = []; c = skel.child[i]
        while c != 0xFF:
            ch.append(c + 1); c = skel.sib[c]
        if ch: nd["children"] = ch
        nodes.append(nd)
    M = mats(skel, bind_tab)
    ibm = b"".join(struct.pack("<16f", *np.linalg.inv(Mi).T.reshape(-1)) for Mi in M)
    skin = {"joints": list(range(1, skel.n + 1)), "inverseBindMatrices": acc(ibm, 5126, skel.n, "MAT4"), "skeleton": 1}
    # Animationen (je Clip: je Bild Drehung aller Glieder + Wurzel-Verschiebung)
    anims = []
    for cname, ctabs in (clips.items() if clips else [("cs", tabs)]):
        nfr = len(ctabs)
        times = acc(struct.pack("<%df" % nfr, *[i / fps for i in range(nfr)]), 5126, nfr, "SCALAR", [0.0], [(nfr - 1) / fps])
        samplers_a, channels = [], []
        for i in range(skel.n):
            rq = b"".join(struct.pack("<4f", *quat(A.limb_rot(ctabs[f][i + 1]))) for f in range(nfr))
            samplers_a.append({"input": times, "output": acc(rq, 5126, nfr, "VEC4"), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers_a) - 1, "target": {"node": i + 1, "path": "rotation"}})
        rt = b"".join(struct.pack("<3f", *[float(v) for v in ctabs[f][0]]) for f in range(nfr))
        samplers_a.append({"input": times, "output": acc(rt, 5126, nfr, "VEC3"), "interpolation": "LINEAR"})
        channels.append({"sampler": len(samplers_a) - 1, "target": {"node": 1, "path": "translation"}})
        anims.append({"name": cname, "samplers": samplers_a, "channels": channels})
    gj = {"asset": {"version": "2.0", "generator": "gameimporter tools/oot/oot_horse"}, "scene": 0, "scenes": [{"nodes": [0]}],
          "nodes": nodes, "meshes": [{"name": name, "primitives": prims}], "skins": [skin], "materials": materials,
          "animations": anims,
          "accessors": accs, "bufferViews": views, "buffers": [{"byteLength": 0}]}
    if textures:
        gj["textures"], gj["images"], gj["samplers"] = textures, images, samplers
    while len(bin_) % 4:
        bin_ += b"\0"
    gj["buffers"][0]["byteLength"] = len(bin_)
    js = json.dumps(gj, separators=(",", ":")).encode()
    while len(js) % 4:
        js += b" "
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bin_)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(bin_), 0x004E4942) + bytes(bin_))
    return {"tris": len(tris), "limbs": skel.n, "frames": sum(len(c) for c in (clips.values() if clips else [tabs])), "bytes": os.path.getsize(path)}


HORSES = {
    "horse_zelda": ("object_horse_zelda", "gHorseZeldaSkel"),
    "horse_ganon": ("object_horse_ganon", "gHorseGanonSkel"),
}


GANON_HORSE_CALM = 381


def nightmare_horse_tabs(key, cs, n):
    """En_Viewer Typ 0 (Zeldas Pferd: Galopp x3) / Typ 4 (Ganons Pferd: Aufbaeumen einmal, dann je Cue Stehen/Aufbaeumen)."""
    obj, sk = HORSES[key]
    d = A.obj(obj); S = A.symbols(obj); skel = SkinSkel(d, S[sk])

    def an(nm):
        return A.Anim(d, S[nm], skel.n)
    tabs = []
    if key == "horse_zelda":
        a = an("gHorseZeldaGallopingAnim"); cur = 0.0
        for f in range(n):
            tabs.append(a.table(int(cur))); cur = (cur + 3.0) % a.frames
        return skel, d, tabs
    ch1 = cs["actors"].get("ch1_1", [])
    a = an("gHorseGanonRearingAnim"); name = "rear_once"; cur = 0.0; timer = 0
    for f in range(n):
        tabs.append(a.table(int(cur)))
        c = None
        for q in ch1:
            if q["start"] <= f < q["end"]:
                c = q
        if f >= GANON_HORSE_CALM and name != "idle":
            # Wunsch: ab Sekunde 23 (Schnitt 381) nicht mehr aufbaeumen, sonst springt das Pferd dauernd in die Kamera
            a = an("gHorseGanonIdleAnim"); name = "idle"; cur = -1.0; c = None
        if c is not None and not (f >= GANON_HORSE_CALM):
            if c["id"] == 2 and timer == 0 and name != "idle":
                a = an("gHorseGanonIdleAnim"); name = "idle"; cur = -1.0
            elif c["id"] == 1:
                timer = 100
                if name != "rear":
                    a = an("gHorseGanonRearingAnim"); name = "rear"; cur = -1.0
        for _ in range(2):
            if timer > 0:
                timer -= 1
        cur += 1.0
        if name == "rear_once":
            cur = min(cur, a.frames - 1)
        else:
            cur %= a.frames
    return skel, d, tabs


def export(key, cs, n, fps, out):
    skel, d, tabs = nightmare_horse_tabs(key, cs, n)
    tris, w, m = build_mesh(d, skel, tabs[0])
    return write_glb(out, skel, tabs[0], tabs, fps, tris, w, m, name=key)
