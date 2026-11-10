"""Minimaler GLB-Schreiber fuer OoT-Geometrie: Position, Normale, UV, Vertexfarbe (COLOR_0), Texturen (PNG eingebettet)."""
import json, struct, math
from oot_gfx import png


def _pad(b, c=b"\0"):
    return b + c * ((4 - len(b) % 4) % 4)


# Bilder als eigene PNG-Dateien neben den GLBs (geteilt von allen Kacheln, Valheim laedt jede nur einmal):
# Unterordner relativ zur GLB, z. B. "tex" -> <glb-ordner>/tex/oot_<hash>.png. None = eingebettet.
EXT_IMAGES = None


def _image(view, images, path, nm, data_fn):
    import os
    if EXT_IMAGES:
        d = os.path.join(os.path.dirname(path), EXT_IMAGES)
        os.makedirs(d, exist_ok=True)
        f = os.path.join(d, nm + ".png")
        if not os.path.exists(f):
            with open(f, "wb") as fo:
                fo.write(data_fn())
        images.append({"uri": EXT_IMAGES + "/" + nm + ".png", "name": nm})
    else:
        images.append({"bufferView": view(data_fn()), "mimeType": "image/png", "name": nm})


def write(path, groups, mats, scale=1.0, offset=(0, 0, 0), name="oot"):
    """groups: {matkey: [ [(pos, uv, rgba) x3], ... ]}, mats: {matkey: Material}"""
    bin_ = bytearray()
    views, accs, meshes_prims, materials, textures, images, samplers = [], [], [], [], [], [], []
    samp_idx = {}
    tex_idx = {}

    def view(data, target=None):
        nonlocal bin_
        bin_ = bytearray(_pad(bytes(bin_)))
        v = {"buffer": 0, "byteOffset": len(bin_), "byteLength": len(data)}
        if target:
            v["target"] = target
        bin_ += data
        views.append(v)
        return len(views) - 1

    def acc(data, ctype, count, typ, mn=None, mx=None, norm=False, target=34962):
        a = {"bufferView": view(data, target), "componentType": ctype, "count": count, "type": typ}
        if mn is not None:
            a["min"], a["max"] = mn, mx
        if norm:
            a["normalized"] = True
        accs.append(a)
        return len(accs) - 1

    ox, oy, oz = offset
    for key, tris in groups.items():
        m = mats[key]
        P, N, U, C, I = bytearray(), bytearray(), bytearray(), bytearray(), bytearray()
        mn, mx = [1e30] * 3, [-1e30] * 3
        n = 0
        vid = {}; idx = []                      # gleiche Ecken (Lage, Normale, UV, Farbe) nur einmal
        F3 = struct.Struct("<fff"); F2 = struct.Struct("<ff"); B4 = struct.Struct("<BBBB")
        for tri in tris:
            ps = [((v[0][0] - ox) * scale, (v[0][1] - oy) * scale, (v[0][2] - oz) * scale) for v in tri]
            a, b, c = ps
            ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
            vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            l = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
            nx, ny, nz = nx / l, ny / l, nz / l
            for v, q in zip(tri, ps):
                p, uv, col = v[0], v[1], v[2]
                if len(v) > 3 and v[3] is not None:
                    # weiche Normale je Ecke (Relief-Schicht im Deku-Baum)
                    vn = v[3]; vl = math.sqrt(vn[0] * vn[0] + vn[1] * vn[1] + vn[2] * vn[2]) or 1.0
                    nv = (vn[0] / vl, vn[1] / vl, vn[2] / vl)
                else:
                    nv = (nx, ny, nz)
                kv = (q, nv, (uv[0], uv[1]), tuple(col))
                i = vid.get(kv)
                if i is None:
                    i = n; vid[kv] = i; n += 1
                    P += F3.pack(*q); N += F3.pack(*nv); U += F2.pack(uv[0], uv[1]); C += B4.pack(*col)
                    for k in range(3):
                        mn[k] = min(mn[k], q[k]); mx[k] = max(mx[k], q[k])
                idx.append(i)
        if n == 0:
            continue
        I = struct.pack("<%dI" % len(idx), *idx)
        attrs = {"POSITION": acc(bytes(P), 5126, n, "VEC3", mn, mx), "NORMAL": acc(bytes(N), 5126, n, "VEC3"),
                 "TEXCOORD_0": acc(bytes(U), 5126, n, "VEC2"), "COLOR_0": acc(bytes(C), 5121, n, "VEC4", norm=True)}
        ind = acc(I, 5125, len(idx), "SCALAR", target=34963)
        mat = {"name": "m%d" % len(materials), "doubleSided": True,
               "pbrMetallicRoughness": {"metallicFactor": 0.0, "roughnessFactor": 1.0}}
        if m.rgba is not None:
            sk = (m.wrap_s, m.wrap_t)
            if sk not in samp_idx:
                samplers.append({"magFilter": 9729, "minFilter": 9987, "wrapS": sk[0], "wrapT": sk[1]})
                samp_idx[sk] = len(samplers) - 1
            import hashlib
            nm = "oot_" + hashlib.sha1(repr(m.key).encode()).hexdigest()[:16]
            # gleiche Bilddatei (gleicher Schluessel, z. B. Helligkeitsstufen mit Farbfaktor) nur einmal einbetten
            if (nm, sk) not in tex_idx:
                _image(view, images, path, nm, lambda: png(m.rgba, m.w, m.h))
                textures.append({"source": len(images) - 1, "sampler": samp_idx[sk]})
                tex_idx[(nm, sk)] = len(textures) - 1
            mat["pbrMetallicRoughness"]["baseColorTexture"] = {"index": tex_idx[(nm, sk)]}
            nrm = getattr(m, "normal", None)
            if nrm is not None and nrm[0] is not None:
                nn = nm + "_n"
                if (nn, sk) not in tex_idx:
                    _image(view, images, path, nn, lambda: png(nrm[0], nrm[1], nrm[2]))
                    textures.append({"source": len(images) - 1, "sampler": samp_idx[sk]})
                    tex_idx[(nn, sk)] = len(textures) - 1
                mat["normalTexture"] = {"index": tex_idx[(nn, sk)], "scale": 1.0}
            if getattr(m, "tint", None):
                mat["pbrMetallicRoughness"]["baseColorFactor"] = list(m.tint) + [1.0]
            if m.alpha:
                mat["alphaMode"] = "BLEND" if m.xlu else "MASK"
                mat["alphaCutoff"] = float(getattr(m, "cutoff", 0.5) or 0.5)
        else:
            pr = m.prim
            mat["pbrMetallicRoughness"]["baseColorFactor"] = [pr[0] / 255, pr[1] / 255, pr[2] / 255, 1.0]
        if getattr(m, "valheim", None):
            mat["extras"] = {"valheim": m.valheim}
        if getattr(m, "matte", False):
            mat.setdefault("extras", {})["matte"] = True
        if getattr(m, "hidden", False):
            mat.setdefault("extras", {})["hidden"] = True
        if getattr(m, "glow", False):
            g_ = m.glow
            mat.setdefault("extras", {})["glow"] = (float(g_) if not isinstance(g_, bool) else True)   # weicher, additiver Schein (Glb.cs: FireFx-Material)
        if getattr(m, "lift", 0):
            mat.setdefault("extras", {})["lift"] = round(float(m.lift), 3)       # Eigenleuchten (gebackenes N64-Licht)
        if m.xlu and "alphaMode" not in mat:
            mat["alphaMode"] = "BLEND"
        # helle halbdurchsichtige Flaechen = Lichtschein (z. B. Sonnenstrahlen im Kokiri-Wald) -> additiv leuchten lassen
        if m.xlu and m.rgba is not None and not getattr(m, "valheim", None):
            import numpy as _np
            _a = _np.frombuffer(bytes(m.rgba), _np.uint8).reshape(-1, 4)
            _w = _a[:, 3] > 8
            if _w.any() and _a[_w, :3].mean() > 170:
                mat.setdefault("extras", {})["hidden"] = True     # Remaster: Valheims Licht/Nebel statt glitzernder Lichtflaechen
        materials.append(mat)
        meshes_prims.append({"attributes": attrs, "indices": ind, "material": len(materials) - 1})
    gj = {"asset": {"version": "2.0", "generator": "gameimporter tools/oot"},
          "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": name, "mesh": 0}],
          "meshes": [{"name": name, "primitives": meshes_prims}],
          "materials": materials, "accessors": accs, "bufferViews": views,
          "buffers": [{"byteLength": len(_pad(bytes(bin_)))}]}
    if textures:
        gj["textures"], gj["images"], gj["samplers"] = textures, images, samplers
    js = _pad(json.dumps(gj, separators=(",", ":")).encode(), b" ")
    bb = _pad(bytes(bin_))
    with open(path, "wb") as f:
        f.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(bb)))
        f.write(struct.pack("<II", len(js), 0x4E4F534A) + js)
        f.write(struct.pack("<II", len(bb), 0x004E4942) + bb)
    return len(meshes_prims)
