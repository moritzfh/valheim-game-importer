"""OoT-Skelette und -Animationen (SkelAnime) lesen und Weltlagen der Glieder Bild fuer Bild rechnen.

Skelett (Flex/Normal, Standard-Glieder): Kopf = [Segmentzeiger auf Gliederliste, Anzahl]; Glied = jointPos (s16 x3),
child (u8), sibling (u8), dList.
Animation: frameCount, frameData (s16[]), jointIndices (je Glied x,y,z), staticIndexMax.
  Wert = frameData[idx] wenn idx < staticIndexMax, sonst frameData[idx + Bild]. Eintrag 0 = Wurzel-Verschiebung, ab 1 Drehungen.
Zeichnen (SkelAnime_DrawLimbOpa): M_glied = M_eltern * T(jointPos) * Rz * Ry * Rx (Wurzel: T(Wurzel-Verschiebung)).
"""
import os, struct, math
import numpy as np
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

ROM = OOT_DECOMP + "/extracted/gc-eu/baserom"
XML = OOT_DECOMP + "/assets/xml/objects"


def obj(name):
    return open(os.path.join(ROM, name), "rb").read()


def symbols(objname):
    """Name -> Offset aus der Asset-XML der Decomp."""
    import re
    p = os.path.join(XML, objname + ".xml")
    out = {}
    for m in re.finditer(r'<(\w+) Name="([^"]+)"[^>]*\sOffset="(0x[0-9A-Fa-f]+)"', open(p).read()):
        out[m.group(2)] = int(m.group(3), 16)
    return out


class Skel:
    def __init__(self, data, off, seg=6):
        lp, n = struct.unpack(">IB", data[off:off + 5])
        lp &= 0xFFFFFF
        self.n = n
        self.pos, self.child, self.sib, self.dl = [], [], [], []
        for i in range(n):
            la = struct.unpack(">I", data[lp + i * 4:lp + i * 4 + 4])[0] & 0xFFFFFF
            x, y, z, c, s, d = struct.unpack(">hhhBBI", data[la:la + 12])
            self.pos.append((x, y, z)); self.child.append(c); self.sib.append(s); self.dl.append(d)
        self.parent = [-1] * n
        for i in range(n):
            c = self.child[i]
            while c != 0xFF:
                self.parent[c] = i
                c = self.sib[c]


class Anim:
    def __init__(self, data, off, nlimbs):
        fc, _, fd, ji, smax = struct.unpack(">hhIIH", data[off:off + 14])
        self.frames = fc
        fd &= 0xFFFFFF; ji &= 0xFFFFFF
        self.smax = smax
        n = nlimbs + 1
        self.idx = [struct.unpack(">HHH", data[ji + i * 6:ji + i * 6 + 6]) for i in range(n)]
        mx = max(max(t) for t in self.idx) + fc + 2
        cnt = min(mx, (len(data) - fd) // 2)
        self.fd = np.frombuffer(data[fd:fd + cnt * 2], ">i2").astype(np.int32)

    def table(self, f):
        """(limbs+1, 3) s16: [0] Wurzel-Verschiebung, [1..] Drehungen (Binaerwinkel)."""
        f = int(f) % max(self.frames, 1)
        out = np.zeros((len(self.idx), 3), np.int32)
        for i, t in enumerate(self.idx):
            for k in range(3):
                j = t[k]
                out[i, k] = self.fd[j] if j < self.smax else self.fd[j + f]
        return out


def _rz(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1.0]])


def _ry(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1.0, 0], [-s, 0, c]])


def _rx(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1.0, 0, 0], [0, c, -s], [0, s, c]])


B2R = 2 * math.pi / 65536.0


def limb_rot(r):
    return _rz(r[2] * B2R) @ _ry(r[1] * B2R) @ _rx(r[0] * B2R)


def fk(skel, tab):
    """Modellraum (OoT-Einheiten): je Glied (R 3x3, t 3)."""
    R = [None] * skel.n; T = [None] * skel.n

    def go(i, Rp, tp):
        while i != 0xFF:
            p = np.array(tab[0] if i == 0 else skel.pos[i], float)
            t = tp + Rp @ p
            Rl = Rp @ limb_rot(tab[i + 1])
            R[i], T[i] = Rl, t
            if skel.child[i] != 0xFF:
                go(skel.child[i], Rl, t)
            i = skel.sib[i]
    go(0, np.eye(3), np.zeros(3))
    return R, T


def blend_tab(a, b, w):
    """Ueberblenden zweier Gelenktabellen (Morph): Drehungen ueber den kuerzeren Winkel."""
    a = a.astype(np.int64); b = b.astype(np.int64)
    out = a.copy()
    out[0] = np.round(a[0] + (b[0] - a[0]) * w)
    d = ((b[1:] - a[1:] + 0x8000) % 0x10000) - 0x8000
    out[1:] = np.round(a[1:] + d * w)
    return out


class LinkAnim:
    """Link-Animation (gameplay_keep: LinkAnimationHeader = frameCount s16, pad, Segment 0x07 in link_animetion).
    Je Bild (limbs+1) Vec3s + 2 Byte (Gesicht): [0] Wurzel-Verschiebung, [1..] Drehungen."""
    _la = None

    def __init__(self, name, nlimbs=21):
        gk = obj("gameplay_keep"); sym = symbols("gameplay_keep")
        off = sym[name]
        fc, _, seg = struct.unpack(">hhI", gk[off:off + 8])
        if LinkAnim._la is None:
            LinkAnim._la = obj("link_animetion")
        self.frames = fc
        self.n = nlimbs + 1
        self.size = self.n * 6 + 2
        self.base = seg & 0xFFFFFF

    def table(self, f):
        f = int(f) % max(self.frames, 1)
        a = self.base + f * self.size
        v = np.frombuffer(LinkAnim._la[a:a + self.n * 6], ">i2").astype(np.int32)
        return v.reshape(self.n, 3)
