"""OoT (N64, F3DEX2) Display-List-Interpreter: liest Raum-Geometrie direkt aus den ROM-Dateien,
die die Decomp extrahiert (extracted/<ver>/baserom/<name>), und gibt Dreiecke mit Position, UV,
Vertexfarbe und Material (dekodierte Textur) zurueck.

Segmente: 2 = Szene, 3 = Raum, 4 = gameplay_keep, 5 = field/dangeon_keep, 6 = Objekt.
Unaufgeloeste Segmente (8..D: Szenen-Animationen wie scrollendes Wasser) bekommen eine Platzhalter-Textur.
"""
import struct, zlib

G_VTX, G_TRI1, G_TRI2, G_QUAD = 0x01, 0x05, 0x06, 0x07
G_CULLDL, G_BRANCH_Z = 0x03, 0x04
G_DL, G_ENDDL, G_TEXTURE, G_GEOMETRYMODE = 0xDE, 0xDF, 0xD7, 0xD9
G_SETTIMG, G_SETTILE, G_SETTILESIZE = 0xFD, 0xF5, 0xF2
G_LOADBLOCK, G_LOADTILE, G_LOADTLUT = 0xF3, 0xF4, 0xF0
G_SETCOMBINE, G_SETPRIMCOLOR, G_SETENVCOLOR, G_RDPHALF_1 = 0xFC, 0xFA, 0xFB, 0xE1
G_SETOTHERMODE_L, G_SETOTHERMODE_H = 0xE2, 0xE3
G_LIGHTING = 0x00020000

FMT_RGBA, FMT_YUV, FMT_CI, FMT_IA, FMT_I = 0, 1, 2, 3, 4


class Segs:
    def __init__(self, m=None):
        self.m = dict(m or {})

    def get(self, addr, n):
        seg, off = (addr >> 24) & 0xFF, addr & 0xFFFFFF
        b = self.m.get(seg)
        if b is None or off + n > len(b):
            return None
        return b[off:off + n]

    def has(self, addr):
        return ((addr >> 24) & 0xFF) in self.m


# ---------------- Texturen ----------------
def _c5(v):
    return (v * 255 + 15) // 31


def decode(data, fmt, siz, w, h, tlut=None, stride=None):
    """N64-Textur -> RGBA-Bytes (w*h*4). stride = Bytes pro Zeile im Speicher."""
    bpp = (4, 8, 16, 32)[siz]
    if stride is None:
        stride = (w * bpp + 7) // 8
    out = bytearray(w * h * 4)
    pal = None
    if fmt == FMT_CI and tlut is not None:
        pal = []
        for i in range(len(tlut) // 2):
            v = (tlut[i * 2] << 8) | tlut[i * 2 + 1]
            pal.append((_c5(v >> 11 & 31), _c5(v >> 6 & 31), _c5(v >> 1 & 31), 255 if v & 1 else 0))
    for y in range(h):
        row = y * stride
        for x in range(w):
            if bpp == 4:
                i = row + (x >> 1)
                if i >= len(data):
                    continue
                v = (data[i] >> 4) if (x & 1) == 0 else (data[i] & 15)
            elif bpp == 8:
                i = row + x
                if i >= len(data):
                    continue
                v = data[i]
            elif bpp == 16:
                i = row + x * 2
                if i + 1 >= len(data):
                    continue
                v = (data[i] << 8) | data[i + 1]
            else:
                i = row + x * 4
                if i + 3 >= len(data):
                    continue
                v = data[i:i + 4]
            o = (y * w + x) * 4
            if fmt == FMT_RGBA and bpp == 16:
                px = (_c5(v >> 11 & 31), _c5(v >> 6 & 31), _c5(v >> 1 & 31), 255 if v & 1 else 0)
            elif fmt == FMT_RGBA and bpp == 32:
                px = tuple(v)
            elif fmt == FMT_CI:
                if pal is None:
                    px = (v * 17 if bpp == 4 else v,) * 3 + (255,)
                else:
                    px = pal[v] if v < len(pal) else (255, 0, 255, 255)
            elif fmt == FMT_IA:
                if bpp == 4:
                    iv = (v >> 1) * 255 // 7
                    px = (iv, iv, iv, 255 if v & 1 else 0)
                elif bpp == 8:
                    iv = (v >> 4) * 17
                    px = (iv, iv, iv, (v & 15) * 17)
                else:
                    px = (v >> 8, v >> 8, v >> 8, v & 255)
            elif fmt == FMT_I:
                iv = v * 17 if bpp == 4 else v & 255
                px = (iv, iv, iv, iv)
            else:
                px = (255, 0, 255, 255)
            out[o:o + 4] = bytes(px)
    return bytes(out)


def png(rgba, w, h):
    raw = b"".join(b"\0" + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def mirror_rgba(rgba, w, h, mx, my):
    """Gespiegelte Wiederholung (N64 'mirror') ins Bild backen: Breite/Hoehe verdoppeln."""
    if mx:
        nb = bytearray()
        for y in range(h):
            r = rgba[y * w * 4:(y + 1) * w * 4]
            px = [r[i:i + 4] for i in range(0, len(r), 4)]
            nb += r + b"".join(reversed(px))
        rgba, w = bytes(nb), w * 2
    if my:
        rows = [rgba[y * w * 4:(y + 1) * w * 4] for y in range(h)]
        rgba, h = b"".join(rows + rows[::-1]), h * 2
    return rgba, w, h


# ---------------- Interpreter ----------------
class Tile:
    __slots__ = ("fmt", "siz", "line", "tmem", "pal", "cmt", "maskt", "shiftt", "cms", "masks", "shifts",
                 "uls", "ult", "lrs", "lrt")

    def __init__(self):
        for k in self.__slots__:
            setattr(self, k, 0)


class Material:
    def __init__(self, key, rgba, w, h, wrap_s, wrap_t, alpha, xlu, prim, combine):
        self.key, self.rgba, self.w, self.h = key, rgba, w, h
        self.wrap_s, self.wrap_t, self.alpha, self.xlu, self.prim, self.combine = wrap_s, wrap_t, alpha, xlu, prim, combine


class Gfx:
    def __init__(self, segs, placeholder_segs=(8, 9, 10, 11, 12, 13), track_env=False, bake=False):
        self.segs = segs
        self.track_env = track_env      # Umgebungsfarbe (Kombinierer ENV) ins Material uebernehmen (NPC-Kleidung)
        self.bake = bake                # Prim-/Umgebungsfarbe + Kombinierer je Material getrennt (bake_combiner)
        self.ph = set(placeholder_segs)
        self.reset()
        self.mats = {}          # key -> Material
        self.tris = []          # (matkey, [(pos, uv, rgba) x3])
        self.tri_src = []       # je Dreieck die Quelladressen der 3 Eckpunkte (fuer Skin-Gewichte)
        self.tri_mtx = []       # je Dreieck die Gelenk-Matrix (Segment 0x0D, Index) der 3 Eckpunkte oder None (Flex-Skelette)
        self.missing = set()

    def reset(self):
        self.vbuf = [None] * 64
        self.vsrc = [None] * 64
        self.vmtx = [None] * 64
        self.cur_mtx = None
        self.tiles = [Tile() for _ in range(8)]
        self.timg = (0, 0, 0, 0)   # addr, fmt, siz, width
        self.tmem = {}             # tmem-Adresse (in 64-bit-Worten) -> (timg, art, Parameter)
        self.tlut = None
        self.tex_on, self.tex_tile, self.tex_s, self.tex_t = 1, 0, 0xFFFF, 0xFFFF
        self.geom = 0
        self.prim = (255, 255, 255, 255)
        self.env = (255, 255, 255, 255)
        self.combine = 0
        self.othermode_l = 0
        self.cur_mat = None
        self.xlu = False

    def tex_alpha_used(self):
        """Nutzt der Farbkombinierer (Alpha-Teil) TEXEL0/TEXEL1-Alpha?"""
        c = self.combine
        w0, w1 = (c >> 32) & 0xFFFFFF, c & 0xFFFFFFFF
        aa0, ac0 = (w0 >> 12) & 7, (w0 >> 9) & 7
        aa1, ac1 = (w1 >> 21) & 7, (w1 >> 18) & 7
        ab0, ad0 = (w1 >> 12) & 7, (w1 >> 9) & 7
        ab1, ad1 = (w1 >> 3) & 7, w1 & 7
        ins = (aa0, ab0, ac0, ad0, aa1, ab1, ac1, ad1)
        return any(x in (1, 2) for x in ins)

    def uses_env(self):
        """Nutzt der Farbkombinierer (RGB) die Umgebungsfarbe?"""
        c = self.combine
        w0, w1 = (c >> 32) & 0xFFFFFF, c & 0xFFFFFFFF
        ins = ((w0 >> 20) & 0xF, (w1 >> 28) & 0xF, (w0 >> 15) & 0x1F, (w1 >> 15) & 7,
               (w0 >> 5) & 0xF, (w1 >> 24) & 0xF, w0 & 0x1F, (w1 >> 6) & 7)
        return any(x == 5 for x in ins)

    def cutout(self):
        L = self.othermode_l
        # G_AC_THRESHOLD oder Coverage aus Alpha (TEX_EDGE-Modi: CVG_X_ALPHA + ALPHA_CVG_SEL)
        return (L & 3) == 1 or (L & 0x1000 and L & 0x2000)

    # ---- Material aus aktuellem Zustand ----
    def material(self):
        if self.cur_mat is not None:
            return self.cur_mat
        t = self.tiles[self.tex_tile & 7]
        load = self.tmem.get(t.tmem)
        envk = (("env",) + tuple(self.env),) if self.track_env and self.uses_env() else ()
        if self.bake:
            envk = (("bake",) + tuple(self.prim) + tuple(self.env) + (self.combine,),)
        if not self.tex_on or load is None:
            key = ("none", self.prim, self.xlu) + envk
            if key not in self.mats:
                self.mats[key] = Material(key, None, 0, 0, 0, 0, False, self.xlu, self.prim, self.combine)
                self.mats[key].env = self.env if envk else None
                if self.bake:
                    self.mats[key].env = self.env
            self.cur_mat = self.mats[key]
            return self.cur_mat
        (addr, ifmt, isiz, iw), kind, lp = load
        w = (t.lrs - t.uls) // 4 + 1
        h = (t.lrt - t.ult) // 4 + 1
        if t.masks:
            w = min(w, 1 << t.masks) if w > 0 else 1 << t.masks
        if t.maskt:
            h = min(h, 1 << t.maskt) if h > 0 else 1 << t.maskt
        w, h = max(1, w), max(1, h)
        bpp = (4, 8, 16, 32)[t.siz]
        pal = None
        if t.fmt == FMT_CI:
            if self.tlut is not None:
                pa, eb, n = self.tlut
                start = t.pal * 16 if t.siz == 0 else 0
                cnt = 16 if t.siz == 0 else 256
                a = pa + (start - eb) * 2
                pal = self.segs.get(a, cnt * 2) if self.segs.has(pa) else None
        cut = self.cutout() and self.tex_alpha_used()
        if self.xlu and not self.tex_alpha_used():
            cut = "xlu-noalpha"
        key = (addr, t.fmt, t.siz, w, h, t.cms & 1, t.cmt & 1, pal is not None and (self.tlut[0], t.pal), self.xlu, cut) + envk
        m = self.mats.get(key)
        if m is None:
            if kind == "tile":
                stride = iw * ((4, 8, 16, 32)[isiz]) // 8
                uls, ult = lp
                start = addr + ult * stride + uls * bpp // 8
            else:
                stride = t.line * 8 if t.line else (w * bpp + 7) // 8
                start = addr
            seg = (addr >> 24) & 0xFF
            data = self.segs.get(start, stride * h) if self.segs.has(addr) else None
            if data is None:
                self.missing.add(seg)
                rgba = bytes((128, 128, 128, 255)) * (w * h)
            else:
                rgba = decode(data, t.fmt, t.siz, w, h, pal, stride)
            # Alpha zaehlt nur, wenn der Render-Modus es nutzt: halbdurchsichtig (xlu) oder Ausstanzen (Alpha-Compare/Coverage)
            alpha = (cut is True or (self.xlu and cut != "xlu-noalpha")) and any(rgba[i] < 128 for i in range(3, len(rgba), 4))
            mx, my = bool(t.cms & 1) and t.masks, bool(t.cmt & 1) and t.maskt
            rgba2, w2, h2 = mirror_rgba(rgba, w, h, mx, my)
            wrap_s = 33071 if (t.cms & 2) else 10497
            wrap_t = 33071 if (t.cmt & 2) else 10497
            m = Material(key, rgba2, w2, h2, wrap_s, wrap_t, alpha, self.xlu, self.prim, self.combine)
            m.tw, m.th, m.mx, m.my = w, h, mx, my
            m.env = self.env if envk else None
            if self.bake:
                m.env = self.env
            self.mats[key] = m
        self.cur_mat = m
        return m

    def uv(self, s, t_):
        m = self.cur_mat
        if m is None or m.rgba is None:
            return (0.0, 0.0)
        tile = self.tiles[self.tex_tile & 7]
        ss = s / 32.0 * (self.tex_s / 65536.0)
        tt = t_ / 32.0 * (self.tex_t / 65536.0)

        def sh(v, k):
            if k == 0:
                return v
            return v / (1 << k) if k <= 10 else v * (1 << (16 - k))
        ss, tt = sh(ss, tile.shifts), sh(tt, tile.shiftt)
        ss -= tile.uls / 4.0
        tt -= tile.ult / 4.0
        return (ss / (m.tw * (2 if m.mx else 1)), tt / (m.th * (2 if m.my else 1)))

    # ---- Ausfuehren ----
    def run(self, addr, depth=0, xlu=False):
        if depth == 0:
            self.xlu = xlu
        if depth > 32:
            return
        pc = addr
        for _ in range(200000):
            c = self.segs.get(pc, 8)
            if c is None:
                self.missing.add((pc >> 24) & 0xFF)
                return
            w0, w1 = struct.unpack(">II", c)
            op = w0 >> 24
            pc += 8
            if op == G_VTX:
                n = (w0 >> 12) & 0xFF
                v0 = ((w0 >> 1) & 0x7F) - n
                d = self.segs.get(w1, n * 16)
                if d is None:
                    self.missing.add((w1 >> 24) & 0xFF)
                    continue
                for i in range(n):
                    x, y, z, _f, s, t, r, g, b, a = struct.unpack(">hhhHhhBBBB", d[i * 16:i * 16 + 16])
                    if 0 <= v0 + i < 64:
                        self.vbuf[v0 + i] = (x, y, z, s, t, r, g, b, a)
                        self.vsrc[v0 + i] = w1 + i * 16
                        self.vmtx[v0 + i] = self.cur_mtx
            elif op in (G_TRI1, G_TRI2, G_QUAD):
                idx = [((w0 >> 16) & 0xFF) // 2, ((w0 >> 8) & 0xFF) // 2, (w0 & 0xFF) // 2]
                tris = [idx]
                if op == G_TRI2:
                    tris.append([((w1 >> 16) & 0xFF) // 2, ((w1 >> 8) & 0xFF) // 2, (w1 & 0xFF) // 2])
                elif op == G_QUAD:
                    tris.append([((w1 >> 16) & 0xFF) // 2, ((w1 >> 8) & 0xFF) // 2, (w1 & 0xFF) // 2])
                m = self.material()
                lit = bool(self.geom & G_LIGHTING)
                for tri in tris:
                    vs = []
                    for k in tri:
                        v = self.vbuf[k] if k < 64 else None
                        if v is None:
                            break
                        col = (255, 255, 255, v[8]) if lit else (v[5], v[6], v[7], v[8])
                        vs.append(((v[0], v[1], v[2]), self.uv(v[3], v[4]), col))
                    if len(vs) == 3:
                        self.tris.append((m.key, vs))
                        self.tri_src.append([self.vsrc[k] for k in tri])
                        self.tri_mtx.append([self.vmtx[k] for k in tri])
            elif op == 0xDA:              # G_MTX: Gelenk-Matrix eines Flex-Skeletts (Segment 0x0D, 0x40 je Glied mit Modell)
                self.cur_mtx = (w1 & 0xFFFFFF) // 0x40 if (w1 >> 24) == 0x0D else None
            elif op == G_DL:
                if (w0 >> 16) & 0xFF == 0:
                    self.run(w1, depth + 1)
                else:
                    pc = w1
            elif op == G_ENDDL:
                return
            elif op == G_RDPHALF_1:
                self._half = w1
            elif op == G_BRANCH_Z:
                pc = getattr(self, "_half", pc)
            elif op == G_TEXTURE:
                self.tex_s, self.tex_t = w1 >> 16, w1 & 0xFFFF
                self.tex_tile = (w0 >> 8) & 7
                self.tex_on = w0 & 0xFF
                self.cur_mat = None
            elif op == G_GEOMETRYMODE:
                self.geom = (self.geom & (w0 & 0xFFFFFF)) | w1
            elif op == G_SETTIMG:
                self.timg = (w1, (w0 >> 21) & 7, (w0 >> 19) & 3, (w0 & 0xFFF) + 1)
            elif op == G_SETTILE:
                t = self.tiles[(w1 >> 24) & 7]
                t.fmt, t.siz, t.line, t.tmem = (w0 >> 21) & 7, (w0 >> 19) & 3, (w0 >> 9) & 0x1FF, w0 & 0x1FF
                t.pal, t.cmt, t.maskt, t.shiftt = (w1 >> 20) & 15, (w1 >> 18) & 3, (w1 >> 14) & 15, (w1 >> 10) & 15
                t.cms, t.masks, t.shifts = (w1 >> 8) & 3, (w1 >> 4) & 15, w1 & 15
                self.cur_mat = None
            elif op == G_SETTILESIZE:
                t = self.tiles[(w1 >> 24) & 7]
                t.uls, t.ult, t.lrs, t.lrt = (w0 >> 12) & 0xFFF, w0 & 0xFFF, (w1 >> 12) & 0xFFF, w1 & 0xFFF
                self.cur_mat = None
            elif op == G_LOADBLOCK:
                t = self.tiles[(w1 >> 24) & 7]
                self.tmem[t.tmem] = (self.timg, "block", None)
                self.cur_mat = None
            elif op == G_LOADTILE:
                t = self.tiles[(w1 >> 24) & 7]
                self.tmem[t.tmem] = (self.timg, "tile", (((w0 >> 12) & 0xFFF) // 4, (w0 & 0xFFF) // 4))
                self.cur_mat = None
            elif op == G_LOADTLUT:
                n = ((w1 >> 14) & 0x3FF) + 1
                t = self.tiles[(w1 >> 24) & 7]
                self.tlut = (self.timg[0], max(0, t.tmem - 256), n)
                self.cur_mat = None
            elif op == G_SETPRIMCOLOR:
                self.prim = ((w1 >> 24) & 255, (w1 >> 16) & 255, (w1 >> 8) & 255, w1 & 255)
                self.cur_mat = None
            elif op == G_SETENVCOLOR:
                self.env = ((w1 >> 24) & 255, (w1 >> 16) & 255, (w1 >> 8) & 255, w1 & 255)
                if self.track_env:
                    self.cur_mat = None
            elif op == G_SETCOMBINE:
                self.combine = ((w0 & 0xFFFFFF) << 32) | w1
                self.cur_mat = None
            elif op == G_SETOTHERMODE_L:
                ln = (w0 & 0xFF) + 1
                sft = 32 - ((w0 >> 8) & 0xFF) - ln
                mask = ((1 << ln) - 1) << sft
                self.othermode_l = (self.othermode_l & ~mask) | (w1 & mask)
                self.cur_mat = None
            elif op == 0xEF:                      # G_RDPSETOTHERMODE: H und L komplett
                self.othermode_l = w1
                self.cur_mat = None
        return


# ---------------- Raum-Header ----------------
def room_dlists(room_bytes):
    """Liste (opa, xlu) Segment-Adressen aus dem Raum-Header (Hauptkopf)."""
    out = []
    off = 0
    while off + 8 <= len(room_bytes):
        cmd, d1, _, d2 = struct.unpack(">BBHI", room_bytes[off:off + 8])
        off += 8
        if cmd == 0x14:
            break
        if cmd == 0x0A:
            so = d2 & 0xFFFFFF
            typ, num = room_bytes[so], room_bytes[so + 1]
            ents = struct.unpack(">I", room_bytes[so + 4:so + 8])[0] & 0xFFFFFF
            if typ == 0:
                for i in range(num):
                    out.append(struct.unpack(">II", room_bytes[ents + i * 8:ents + i * 8 + 8]))
            elif typ == 2:
                for i in range(num):
                    e = ents + i * 16
                    out.append(struct.unpack(">II", room_bytes[e + 8:e + 16]))
            elif typ == 1:
                out.append(struct.unpack(">II", room_bytes[ents:ents + 8]))
    return out


def scene_collision(scene_bytes):
    """Kollisions-Mesh der Szene: (verts, tris, surface-ids)."""
    off = 0
    col = None
    while off + 8 <= len(scene_bytes):
        cmd, d1, _, d2 = struct.unpack(">BBHI", scene_bytes[off:off + 8])
        off += 8
        if cmd == 0x14:
            break
        if cmd == 0x03:
            col = d2 & 0xFFFFFF
    if col is None:
        return [], []
    h = scene_bytes[col:col + 0x2C]
    nv = struct.unpack(">H", h[0x0C:0x0E])[0]
    va = struct.unpack(">I", h[0x10:0x14])[0] & 0xFFFFFF
    npoly = struct.unpack(">H", h[0x14:0x16])[0]
    pa = struct.unpack(">I", h[0x18:0x1C])[0] & 0xFFFFFF
    verts = [struct.unpack(">hhh", scene_bytes[va + i * 6:va + i * 6 + 6]) for i in range(nv)]
    tris = []
    for i in range(npoly):
        p = scene_bytes[pa + i * 16:pa + i * 16 + 16]
        typ, a, b, c = struct.unpack(">HHHH", p[:8])
        tris.append((a & 0x1FFF, b & 0x1FFF, c & 0x1FFF, typ))
    return verts, tris


def bake_combiner(m):
    """Farbkombinierer (Zyklus 1, dann Zyklus 2 mit Schattierung = weiss) in die Textur rechnen: TEXEL0, PRIM, ENV.
    Damit bekommen Intensitaets-Texturen (GI-Modelle) ihre Original-Farben."""
    import numpy as _np
    c = m.combine
    w0, w1 = (c >> 32) & 0xFFFFFF, c & 0xFFFFFFFF
    prim = _np.array(m.prim, float) / 255.0
    env = _np.array(getattr(m, "env", None) or (255, 255, 255, 255), float) / 255.0
    if m.rgba is None:
        tex = _np.ones((1, 4))
    else:
        tex = _np.frombuffer(bytes(m.rgba), _np.uint8).reshape(-1, 4).astype(float) / 255.0
    n = len(tex)
    one, zero = _np.ones((n, 4)), _np.zeros((n, 4))
    P, E = _np.tile(prim, (n, 1)), _np.tile(env, (n, 1))

    def src(v, comb, kind):
        if kind == "c":
            tab = {0: comb, 1: tex, 2: tex, 3: P, 4: one, 5: E,
                   7: _np.repeat(comb[:, 3:4], 4, 1), 8: _np.repeat(tex[:, 3:4], 4, 1), 9: _np.repeat(tex[:, 3:4], 4, 1),
                   10: _np.repeat(P[:, 3:4], 4, 1), 11: one, 12: _np.repeat(E[:, 3:4], 4, 1)}
        elif kind == "b":
            tab = {0: comb, 1: tex, 2: tex, 3: P, 4: one, 5: E}
        else:
            tab = {0: comb, 1: tex, 2: tex, 3: P, 4: one, 5: E, 6: one}
        return tab.get(v, zero)

    def cyc(a, b, cc, d, aa, ab, ac, ad, comb):
        rgb = (src(a, comb, "a") - src(b, comb, "b")) * src(cc, comb, "c") + src(d, comb, "d")
        acs = {0: one, 1: tex, 2: tex, 3: P, 4: one, 5: E, 6: one}
        al = (src(aa, comb, "x") - src(ab, comb, "x")) * acs.get(ac, zero) + src(ad, comb, "x")
        o = rgb.copy(); o[:, 3] = al[:, 3]
        return _np.clip(o, 0, 1)
    a0, c0 = (w0 >> 20) & 0xF, (w0 >> 15) & 0x1F
    aa0, ac0 = (w0 >> 12) & 7, (w0 >> 9) & 7
    a1, c1 = (w0 >> 5) & 0xF, w0 & 0x1F
    b0, b1 = (w1 >> 28) & 0xF, (w1 >> 24) & 0xF
    aa1, ac1 = (w1 >> 21) & 7, (w1 >> 18) & 7
    d0, ab0, ad0 = (w1 >> 15) & 7, (w1 >> 12) & 7, (w1 >> 9) & 7
    d1, ab1, ad1 = (w1 >> 6) & 7, (w1 >> 3) & 7, w1 & 7
    r = cyc(a0, b0, c0, d0, aa0, ab0, ac0, ad0, one)
    r = cyc(a1, b1, c1, d1, aa1, ab1, ac1, ad1, r)
    if m.rgba is None:
        m.prim = tuple(int(round(v * 255)) for v in r[0])
    else:
        m.rgba = (r * 255 + 0.5).astype(_np.uint8).tobytes()
    return m
