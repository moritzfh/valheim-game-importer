"""Kokiri-Wald, Teil 2 (bis zum Deku-Baum): Gegner, Laden, Aufsammel-Dinge, Texte.

  - Deku-Ranha / welke Deku-Ranha (object_dekubaba): Kopf mit Skelett + Original-Animationen, Stiel-Stuecke, Laub, Deku-Stab
    -> pack.json "oot_enemies" (src/OotEnemies.cs), Plaetze aus den Raum-Akteurlisten (En_Karebaba im Kokiri-Wald)
  - Kokiri-Laden (Szene kokiri_shop) als Innenraum weit weg vom Wald, Verkaeufer (gKm1Skel + object_masterkokirihead),
    Regal-Gegenstaende (En_GirlA: GI-Modelle, Preise, Texte) -> "oot_shop" (src/OotShop.cs), Tueren als Ortswechsel
  - GI-/Aufsammel-Modelle (Deku-Nuss, Deku-Stab, Deku-Schild, Herz) und die Original-Texte dazu -> "oot_items"-Ergaenzung

  export(out) -> {"oot_enemies": ..., "oot_shop": ..., "items_add": ..., "statics": [...], "spots": [...]}
"""
import copy
import math
import os
import struct

import numpy as np

import oot_anim as A
import oot_glb
import oot_items as I
import oot_kf
import oot_npc
import oot_pack
import oot_text
from oot_gfx import Gfx, Segs, bake_combiner
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

ROM = I.ROM
S = oot_pack.S
OFF_SHOP = (-1380.0, 170.0, -1720.0)        # Laden-Innenraum: weit weg vom Feld, hoch in der Luft (wie Links Haus)
GI_SCALE = 0.0075                            # gehaltene Gegenstaende (wie das Kokiri-Schwert)


def SV(p):
    v = oot_pack.V(p)
    return [round(v[i] + OFF_SHOP[i], 3) for i in range(3)]


def dl_raw(path, obj, sym, name, scale=1.0, segs=None, matte=True, dark=1.0, bake=False):
    """Display-Liste eines Objekts als GLB (OoT-Einheiten * scale, lokal +z = OoT vorn)."""
    data = A.obj(obj); sy = A.symbols(obj)
    m = {6: data, 4: A.obj("gameplay_keep")}
    m.update(segs or {})
    g = Gfx(Segs(m), bake=bake)
    g.reset(); g.run(0x06000000 | sy[sym])
    groups = {}
    for k, vs in g.tris:
        groups.setdefault(k, []).append(vs)
    dm = {}
    for k, mt in g.mats.items():
        mt = copy.copy(mt); mt.matte = matte
        if bake:
            bake_combiner(mt)
        if dark != 1.0 and mt.rgba is not None:
            a = np.frombuffer(bytes(mt.rgba), np.uint8).reshape(-1, 4).astype(float)
            a[:, :3] *= dark
            mt.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
        dm[k] = mt
    os.makedirs(os.path.dirname(path), exist_ok=True)
    oot_glb.write(path, groups, dm, scale=scale, offset=(0, 0, 0), name=name)
    return len(g.tris)


# ---------------- Gegner ----------------
def enemies(out):
    d = os.path.join(out, "enemy")
    rep = {"head": oot_npc.char_glb("object_dekubaba", "gDekubabaHeadSkel",
                                    {"chomp": ("gDekubabaChompAnim", 0, -1), "idle": ("gDekubabaIdleAnim", 0, -1)},
                                    {}, "dekubaba_head", out)}
    for nm in ("Stem0", "Stem1", "Stem2", "BaseLeaves", "DekuStick"):
        rep[nm] = dl_raw(os.path.join(d, "dekubaba_%s.glb" % nm.lower()), "object_dekubaba", "gDekubaba%sDL" % nm, "dekubaba_" + nm.lower(), bake=True)
    cfg = {"dekubaba": {"head": "npc/dekubaba_head.glb", "leaves": "enemy/dekubaba_baseleaves.glb", "stick": "enemy/dekubaba_dekustick.glb",
                        "stems": ["enemy/dekubaba_stem0.glb", "enemy/dekubaba_stem1.glb", "enemy/dekubaba_stem2.glb"]},
           "list": []}
    ids = I.actor_ids()
    seen = set()
    for a in I.room_actors("spot04"):
        aid = a[0] & 0xFFFF; key = (aid, a[1], a[2], a[3])
        if key in seen:
            continue
        seen.add(key)
        if aid == ids["ACTOR_EN_KAREBABA"]:
            cfg["list"].append({"type": "karebaba", "pos": [round(v, 3) for v in oot_kf.V((a[1], a[2], a[3]))], "yaw": a[5]})
        elif aid == ids["ACTOR_EN_DEKUBABA"]:
            cfg["list"].append({"type": "dekubaba", "pos": [round(v, 3) for v in oot_kf.V((a[1], a[2], a[3]))], "yaw": a[5],
                                "big": 1 if a[7] == 1 else 0})
    return cfg, rep


# ---------------- Gegenstaende ----------------
GI = {   # Schluessel -> (Objekt, DL, GI-Text)
    "nuts": ("object_gi_nuts", "gGiNutDL", 0x34),
    "stick": ("object_gi_stick", "gGiStickDL", 0x37),
    "shield": ("object_gi_shield_1", "gGiDekuShieldDL", None),
    "heart": ("object_gi_heart", "gGiRecoveryHeartDL", None),
    "seeds": ("object_gi_seed", "gGiSeedDL", None),
    "arrows10": ("object_gi_arrow", "gGiArrowMediumDL", None),
    "arrows30": ("object_gi_arrow", "gGiArrowLargeDL", None),
}


def gi_text_id(gi_name):
    """Text-ID eines GI_* aus sGetItemTable (z_player.c)."""
    src = open(OOT_DECOMP + "/src/overlays/actors/ovl_player_actor/z_player.c", encoding="utf-8").read()
    i = src.find("// " + gi_name + "\n")
    if i < 0:
        return None
    line = src[i:src.find("\n", src.find("GET_ITEM", i))]
    parts = [p.strip() for p in line.split("GET_ITEM(")[1].split(",")]
    return int(parts[3], 16)


def items(out):
    d = os.path.join(out, "items")
    gi_models, pickups, texts = {}, {}, {}
    for k, (obj, dl, tid) in GI.items():
        dl_raw(os.path.join(d, "gi_%s.glb" % k), obj, dl, "gi_" + k, scale=GI_SCALE, matte=False, bake=True)
        gi_models[k] = "items/gi_%s.glb" % k
        dl_raw(os.path.join(d, "drop_%s.glb" % k), obj, dl, "drop_" + k, scale=GI_SCALE * 0.62, matte=False, bake=True)
        if k in ("nuts", "stick", "heart"):
            pickups[k] = "items/drop_%s.glb" % k
    # Get-Item-Texte (Original) fuers erste Aufsammeln / Kaufen mit Fanfare
    ids = {"nuts": gi_text_id("GI_DEKU_NUTS_5"), "stick": gi_text_id("GI_DEKU_STICKS_1"), "shield": gi_text_id("GI_SHIELD_DEKU")}
    for k, tid in ids.items():
        if tid is None:
            continue
        p = oot_text.pages(tid)
        if p:
            texts[k] = p["pages"]
    return {"gi_models": gi_models, "pickups": pickups, "gi_texts": texts, "gi_text_ids": {k: "0x%04X" % v for k, v in ids.items() if v}}


# ---------------- Laden ----------------
SHOP_ITEMS = [   # sShopkeeperStores[OSSAN_TYPE_KOKIRI] + sShopItemEntries (Preis, Anzahl, Beschreibung, Kaufen?-Frage)
    ("shield", 50, 52, -20, 40, 1, 0x009F, 0x0089),
    ("nuts5", 50, 76, -20, 15, 5, 0x00B2, 0x007F),
    ("nuts10", 80, 52, -3, 30, 10, 0x00A2, 0x0087),
    ("stick", 80, 76, -3, 10, 1, 0x00A1, 0x0088),
    ("seeds", -50, 52, -20, 30, 30, 0x00DF, 0x00DE),
    ("arrows10", -50, 76, -20, 20, 10, 0x00A0, 0x008A),
    ("arrows30", -80, 52, -3, 60, 30, 0x00C1, 0x009B),
    ("heart", -80, 76, -3, 10, 16, 0x00AC, 0x0095),
]
SHELF_ROT = [0xEAAC] * 4 + [0x1554] * 4
GI_KEY = {"nuts5": "nuts", "nuts10": "nuts"}


def keeper_glb(out):
    """Verkaeufer: Kokiri-Koerper (gKm1Skel), Kopf aus object_masterkokirihead an Glied 15, Animation aus object_masterkokiri."""
    sdata, ssy = A.obj("object_km1"), A.symbols("object_km1")
    hdata, hsy = A.obj("object_masterkokirihead"), A.symbols("object_masterkokirihead")
    adata, asy = A.obj("object_masterkokiri"), A.symbols("object_masterkokiri")
    import oot_link
    skel = oot_link.StdSkel(sdata, ssy["gKm1Skel"])
    an = [k for k in asy if "Anim" in k][0]
    clips = {"idle": oot_npc.clip_tabs(adata, asy, (an, 0, -1), skel.n)}
    gk = A.obj("gameplay_keep")
    base = {4: gk, 8: oot_npc.env_dl((0, 130, 70)), 9: oot_npc.env_dl((110, 170, 20)), 0x0C: struct.pack(">II", 0xDF000000, 0)}
    seg_body = dict(base); seg_body[6] = sdata
    seg_head = dict(base); seg_head[6] = hdata
    eye = hsy["gKokiriShopkeeperEyeDefaultTex"]
    seg_head[0x0A] = hdata[eye:eye + 0x1000]
    tris, wts, mats = oot_npc.build_mesh(skel, clips["idle"][0], lambda li: seg_body, head=(14, seg_head, 0x06000000 | hsy["gKokiriShopkeeperHeadDL"]))
    return oot_npc.write(os.path.join(out, "npc", "kokiri_shopkeeper.glb"), "kokiri_shopkeeper", skel, clips, tris, wts, mats)


SHOP_CAM = {"eye": (-100.0, 100.0, 260.0), "rot": (2367, 28945), "fov": 50.0}     # BgCam (CAM_SET_PREREND_FIXED) der Szene


def shop_room(out):
    """Kokiri-Laden: im Original ein vorgerendertes Bild (320x240) + flache Tiefen-Geometrie. Wie Links Haus: Tiefen-Geometrie
    als 3D-Raum, von der Original-Kamera gesehene Flaechen bekommen das Bild (Valheim-Holz-Relief), Waende aus der Kollision
    (Achteck) in Valheim-Eichenrinde, Kuppel als Decke, Eingang vorne offen mit Licht von draussen."""
    from PIL import Image
    import io, collections
    from scipy.spatial import ConvexHull
    import oot_linkhome as LH, oot_vpack
    from oot_gfx import scene_collision
    sb, tris, mats = oot_pack.scene_tris(ROM, "kokiri_shop", "dangeon")
    rb = open(os.path.join(ROM, "kokiri_shop_room_0"), "rb").read()
    i = rb.find(b"\xff\xd8\xff"); j = rb.find(b"\xff\xd9", i)
    jpg = Image.open(io.BytesIO(rb[i:j + 2])).convert("RGB")
    b2r = 2 * math.pi / 65536
    pitch, yawc = -SHOP_CAM["rot"][0] * b2r, SHOP_CAM["rot"][1] * b2r
    fwd = np.array([math.cos(pitch) * math.sin(yawc), math.sin(pitch), math.cos(pitch) * math.cos(yawc)])
    right = np.cross(fwd, [0, 1.0, 0]); right /= np.linalg.norm(right); up = np.cross(right, fwd)
    eye = np.array(SHOP_CAM["eye"])
    tz = math.tan(math.radians(SHOP_CAM["fov"] / 2))

    def project(p):
        d = np.asarray(p, float) - eye; z = d @ fwd
        x = (d @ right) / (z * tz * 4 / 3); y = (d @ up) / (z * tz)
        return (x * 0.5 + 0.5, 0.5 - y * 0.5), z
    m0 = LH._img_mat(jpg, "ks_jpg")
    m0.rgba, m0.w, m0.h = oot_vpack.stone_blend(m0, (32.0, 32.0), base="wood", strong=False, f=2)
    m0.wrap_s = m0.wrap_t = 33071
    import oot_kf as KFm
    rbk, rw, rh = KFm.bark_tile(False)
    mb = LH.M(rbk, rw, rh, "ks_bark"); mb.wrap_s = mb.wrap_t = 10497; mb.matte = True
    lightm = LH.M(bytes([235, 240, 215, 255]) * 4, 2, 2, "ks_out"); lightm.matte = True
    groups = collections.OrderedDict([("jpg", []), ("bark", []), ("out", [])])
    col = (255, 255, 255, 255)

    def wall_uv(P, n):
        if abs(n[1]) > 0.7:
            return [(tuple(p), (p[0] * S / 2.0, p[2] * S / 2.0), col) for p in P]
        t = np.array([-n[2], 0.0, n[0]]); t /= np.linalg.norm(t) + 1e-9          # waagrechte Richtung entlang der Wand
        return [(tuple(p), ((p[0] * t[0] + p[2] * t[2]) * S / 2.0, -p[1] * S / 2.75), col) for p in P]

    def add_seen_or_bark(P):
        n = np.cross(P[1] - P[0], P[2] - P[0]); nn = np.linalg.norm(n)
        if nn < 1e-6:
            return
        n /= nn
        c = sum(P) / 3
        view = (c - eye); view /= np.linalg.norm(view)
        uvs = [project(p)[0] for p in P]
        inside = all(-0.02 <= u <= 1.02 and -0.02 <= v <= 1.02 for u, v in uvs)
        if (n @ view) < -0.2 and inside and n[1] > 0.5:
            groups["jpg"].append([(tuple(p), uv, col) for p, uv in zip(P, uvs)])
            return
        groups["bark"].append(wall_uv(P, n))
    for k, vs in tris:
        add_seen_or_bark([np.array(p, float) for p, _, _ in vs])
    # Waende aus der Kollision (das Bild zeigt sie, die Tiefen-Geometrie hat sie nicht), hochgezogen bis zur Kuppel
    V, T = scene_collision(sb)
    V = np.array(V, float)
    WALL_TOP = 200.0
    for a, b, c, t in T:
        P = V[[a, b, c]].copy()
        n = np.cross(P[1] - P[0], P[2] - P[0]); n /= np.linalg.norm(n) + 1e-9
        if abs(n[1]) > 0.3 or max(np.hypot(P[:, 0], P[:, 2])) < 150:       # nur die Aussenwand (keine Pfosten/Boeden)
            continue
        P[P[:, 1] > 100, 1] = WALL_TOP
        tri = wall_uv(P, n)
        groups["bark"].append(tri)
        groups["bark"].append([tri[0], tri[2], tri[1]])                   # beidseitig
    # Eingang vorne (z = 176): kurzer Gang bis z = 220, dahinter helles Licht von draussen
    def quad(g, a, b, c_, d):
        g.append([(a, (0, 0), col), (b, (1, 0), col), (c_, (1, 1), col)]); g.append([(a, (0, 0), col), (c_, (1, 1), col), (d, (0, 1), col)])
        g.append([(a, (0, 0), col), (c_, (1, 1), col), (b, (1, 0), col)]); g.append([(a, (0, 0), col), (d, (0, 1), col), (c_, (1, 1), col)])
    for x0 in (-73.0, 73.0):
        quad(groups["bark"], (x0, 0, 176), (x0, 0, 222), (x0, WALL_TOP, 222), (x0, WALL_TOP, 176))
    quad(groups["bark"], (-73, 110, 176), (73, 110, 176), (73, WALL_TOP, 176), (-73, WALL_TOP, 176))   # Sturz
    quad(groups["bark"], (-73, 110, 222), (73, 110, 222), (73, 110, 176), (-73, 110, 176))            # Decke im Gang
    quad(groups["bark"], (-73, 0, 176), (73, 0, 176), (73, 0, 222), (-73, 0, 222))                  # Boden im Gang
    quad(groups["out"], (-73, 0, 220), (73, 0, 220), (73, 110, 220), (-73, 110, 220))
    # Decke: Kuppel ueber dem Achteck
    ring = [np.array(v) for v in V if v[1] < 1 and np.hypot(v[0], v[2]) > 150]
    ring = np.array(ring)
    hull = ConvexHull(ring[:, [0, 2]])
    rr = [ring[ii] for ii in hull.vertices]
    apex = (0.0, WALL_TOP + 60.0, 0.0)
    for ii in range(len(rr)):
        a, b = rr[ii], rr[(ii + 1) % len(rr)]
        pa, pb = (float(a[0]) * 1.02, WALL_TOP - 1, float(a[2]) * 1.02), (float(b[0]) * 1.02, WALL_TOP - 1, float(b[2]) * 1.02)
        uv = lambda q: (q[0] * S / 2.0, q[2] * S / 2.0)
        groups["bark"].append([(pa, uv(pa), col), (pb, uv(pb), col), (apex, uv(apex), col)])
        groups["bark"].append([(pa, uv(pa), col), (apex, uv(apex), col), (pb, uv(pb), col)])
    allmats = {"jpg": m0, "bark": mb, "out": lightm}
    ids = I.actor_ids()
    tana = [a for a in I.room_actors("kokiri_shop") if (a[0] & 0xFFFF) == ids["ACTOR_EN_TANA"]][0]
    data = A.obj("object_shop_dungen"); sy = A.symbols("object_shop_dungen")
    g = Gfx(Segs({6: data, 4: A.obj("gameplay_keep")}), bake=True)
    g.reset(); g.run(0x06000000 | sy["gShopDungenWoodenShelvesDL"])
    cy, sy_ = math.cos(tana[5] * b2r), math.sin(tana[5] * b2r)
    for k, vs in g.tris:
        kk = ("shelf",) + (k if isinstance(k, tuple) else (k,))
        if kk not in allmats:
            m = copy.copy(g.mats[k]); bake_combiner(m); m.matte = True; allmats[kk] = m
        tri = []
        for p, uv, c in vs:
            x, y, z = p
            tri.append(((tana[1] + x * cy + z * sy_, tana[2] + y, tana[3] - x * sy_ + z * cy), uv, c))
        groups.setdefault(kk, []).append(tri)
    sd = os.path.join(out, "statics", "shop"); os.makedirs(sd, exist_ok=True)
    oot_glb.write(os.path.join(sd, "kokiri_shop.glb"), groups, allmats, scale=S, offset=(0, 0, 0), name="kokiri_shop")
    allp = np.array([p for g in groups.values() for t in g for p, _, _ in t], float)
    rmax = float(np.linalg.norm(allp, axis=1).max()) * S
    return [{"m": "statics/shop/kokiri_shop.glb", "p": SV((0, 0, 0)), "q": oot_pack.Q180, "s": [1, 1, 1], "r": round(rmax, 2), "c": 1, "vis": 400}], \
        {"jpg": len(groups["jpg"]), "bark": len(groups["bark"]), "bounds": [allp.min(0).tolist(), allp.max(0).tolist()]}


def shop(out):
    statics, room = shop_room(out)
    bounds = {"min": room["bounds"][0], "max": room["bounds"][1]}
    # Verkaeufer und Regal (Akteure der Szene)
    ids = I.actor_ids()
    acts = I.room_actors("kokiri_shop")
    oss = [a for a in acts if (a[0] & 0xFFFF) == ids["ACTOR_EN_OSSAN"]][0]
    tana = [a for a in acts if (a[0] & 0xFFFF) == ids["ACTOR_EN_TANA"]][0]
    yaw = lambda r: round((180.0 - r / 65536.0 * 360.0) % 360.0, 2)
    kr = keeper_glb(out)

    def gi_ymin(rel):
        """Unterkante des Gegenstand-Modells (GLB ist um die Mitte gebaut, haelt man es ueber den Kopf)."""
        try:
            import json as _j
            b = open(os.path.join(out, rel), "rb").read(); n = struct.unpack("<I", b[12:16])[0]; j = _j.loads(b[20:20 + n])
            return min(j["accessors"][pr["attributes"]["POSITION"]]["min"][1] for m in j["meshes"] for pr in m["primitives"])
        except Exception:
            return -0.2
    items_ = []
    for i, (key, x, y, z, price, cnt, desc, prompt) in enumerate(SHOP_ITEMS):
        p = (tana[1] + x, tana[2] + y, tana[3] + z)
        shelf = 0 if x > 0 else 1                       # vom Eingang (+z) aus gesehen: +x = links im Bild? -> per Kamera unten festgelegt
        row = 1 if y > 60 else 0
        col = 1 if abs(x) > 60 else 0
        model = "items/gi_%s.glb" % GI_KEY.get(key, key)
        pos = SV(p)
        pos[1] = round(pos[1] - S + (-gi_ymin(model)) + 0.01, 3)    # auf dem Regalbrett (Oberkante 1 Einheit unter p) statt halb darin
        items_.append({"key": key, "price": price, "count": cnt, "desc": "0x%04X" % desc, "prompt": "0x%04X" % prompt,
                       "model": model, "pos": pos, "yaw": yaw((tana[5] + SHELF_ROT[i]) & 0xFFFF),
                       "shelf": shelf, "slot": row * 2 + col})
    # Kamera: hinter Link am Tresen (Spawn 0 der Szene liegt bei z=109), Blick auf Verkaeufer bzw. die Regale (+-30 Grad)
    kp = (oss[1], oss[2], oss[3])
    cam = {"pos": SV((0, 62, 40)), "look_k": SV((kp[0], kp[1] + 40, kp[2])),
           "look_l": SV((tana[1] + 65, tana[2] + 60, tana[3] - 10)), "look_r": SV((tana[1] - 65, tana[2] + 60, tana[3] - 10))}
    texts = set([0x10BA, 0x0084, 0x0085, 0x0086])
    for it in SHOP_ITEMS:
        texts.update([it[6], it[7]])
    cfg = {"keeper": {"model": "npc/kokiri_shopkeeper.glb", "pos": SV(kp), "yaw": yaw(oss[5] & 0xFFFF), "clip": "idle"},
           "cam": cam, "items": items_, "texts": oot_text.export(sorted(texts))}
    # Tueren: Wald (vor Spawn 4, ENTR_KOKIRI_FOREST_4) <-> Laden (Spawn 0)
    KF = lambda p: [round(float(v), 3) for v in oot_kf.V(p)]
    zmax = bounds["max"][2]
    spots = [
        {"id": "shop_enter", "pos": KF((854, 0, -300)), "radius": 0.7, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": SV((0, 0, 80)), "yaw": yaw(0x8000)}]},
        {"id": "shop_exit", "pos": SV((0, 0, 200)), "radius": 1.3, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": KF((854, 0, -250)), "yaw": yaw(0)}]},
    ]
    return cfg, statics, spots, {"room": {k: v for k, v in room.items() if k != "bounds"}, "keeper": kr.get("clips") if isinstance(kr, dict) else None}


def export(out):
    en, er = enemies(out)
    it = items(out)
    sh, st, sp, sr = shop(out)
    return {"oot_enemies": en, "oot_shop": sh, "items_add": it, "statics": st, "spots": sp,
            "rep": {"enemies": er, "babas": len(en["list"]), "shop": sr}}


if __name__ == "__main__":
    import json
    import sys
    out = sys.argv[1]
    r = export(out)
    json.dump(r, open(os.path.join(out, "kf2.json"), "w"), indent=1)
    print(json.dumps(r["rep"], indent=1))


# ---------------- Rollender Felsbrocken (En_Goroiwa) im Schwert-Labyrinth ----------------
def scene_paths(scene="spot04", folder="overworld"):
    """Pfade des Szenen-Hauptkopfs aus den extrahierten Assets: [[(x, y, z), ...], ...]"""
    import re
    d = OOT_DECOMP + "/extracted/gc-eu/assets/scenes/%s/%s/" % (folder, scene)
    sc = open(d + scene + "_scene.c").read()
    m = re.search(r"Path (%s_scenePathList_[0-9A-F]+)\[\]" % scene, sc)
    lst = open(d + m.group(1) + ".inc.c").read()
    out = []
    for nm in re.findall(r"ARRAY_COUNT\((\w+)\)", lst):
        pts = re.findall(r"(-?\d+), // x\s+(-?\d+), // y\s+(-?\d+), // z", open(d + nm + ".inc.c").read())
        out.append([tuple(int(v) for v in p) for p in pts])
    return out


def boulder_paths():
    """nur die Wege der Felsbrocken (Valheim-Koordinaten), ohne Modell - fuers Gelaende (Labyrinth-Gaenge frei halten)"""
    ids = I.actor_ids(); paths = scene_paths(); out = []
    for a in I.room_actors("spot04"):
        if (a[0] & 0xFFFF) == ids["ACTOR_EN_GOROIWA"]:
            out.append([oot_kf.maze_pos(list(oot_kf.V(p))) for p in paths[(a[7] & 0xFFFF) & 0xFF]])
    return out


def boulders(out):
    """Felsbrocken aus der Akteur-Liste (params: Pfad, Schleife Bits 8-9, Boden Bit 10; rot.z & 1) + Modell gRollingRockDL."""
    import oot_pack
    n = dl_raw(os.path.join(out, "enemy", "goroiwa.glb"), "object_goroiwa", "gRollingRockDL", "goroiwa", scale=0.1 * oot_pack.S, bake=True)
    ids = I.actor_ids(); paths = scene_paths()
    lst, seen = [], set()
    for a in I.room_actors("spot04"):
        if (a[0] & 0xFFFF) != ids["ACTOR_EN_GOROIWA"]:
            continue
        prm = a[7] & 0xFFFF; key = (a[1], a[2], a[3])
        if key in seen:
            continue
        seen.add(key)
        path = paths[prm & 0xFF]
        lst.append({"type": "boulder", "pos": [round(v, 3) for v in oot_kf.maze_pos(oot_kf.V(path[0]))],
                    "path": [[round(v, 3) for v in oot_kf.maze_pos(oot_kf.V(p))] for p in path], "model": "enemy/goroiwa.glb",
                    "loop": (prm >> 8) & 3, "fall": (prm >> 10) & 1, "rotz": a[6] & 1, "speed": 9.2})
    return lst, n
