"""Im Deku-Baum (Szene ydan): Raeume als Modelle, Original-Kollision (unsichtbar), Kletterflaechen, Wasser, Netze, Fackeln,
Gegner, Truhen -> pack.json "oot_ydan" (src/OotDungeon.cs) + statics_ydan.json.

  Lage: weit weg vom Wald, hoch in der Luft (OFF_YDAN), wie Links Haus und der Laden.
  export(out) -> {"cfg": ..., "statics": [...], "spots": [...], "rep": ...}
"""
import collections
import copy
import math
import os
import struct

import numpy as np

import oot_anim as A
import oot_glb
import oot_items as I
import oot_kf
import oot_pack
from oot_gfx import Gfx, Segs, scene_collision, bake_combiner
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

ROM = I.ROM
S = oot_pack.S
OFF_YDAN = (-1750.0, 230.0, -1050.0)
LIFT = 0.62


def YV(p):
    v = oot_pack.V(p)
    return [round(v[i] + OFF_YDAN[i], 3) for i in range(3)]


def yaw_v(r):
    return round((180.0 - (r & 0xFFFF) / 65536.0 * 360.0) % 360.0, 2)


def scene_header(name):
    sb = open(os.path.join(ROM, name + "_scene"), "rb").read()
    off, h = 0, {}
    while True:
        cmd, n, _, d = struct.unpack(">BBHI", sb[off:off + 8]); off += 8
        if cmd == 0x14:
            break
        h[cmd] = (n, d & 0xFFFFFF)
    return sb, h


def collision_full(sb, h):
    """Kollision mit Oberflaechen-Typen: Ecken, Dreiecke (a, b, c, data0, data1), Wasserkaesten."""
    col = h[0x03][1]
    hd = sb[col:col + 0x2C]
    nv = struct.unpack(">H", hd[0x0C:0x0E])[0]; va = struct.unpack(">I", hd[0x10:0x14])[0] & 0xFFFFFF
    npoly = struct.unpack(">H", hd[0x14:0x16])[0]; pa = struct.unpack(">I", hd[0x18:0x1C])[0] & 0xFFFFFF
    sa = struct.unpack(">I", hd[0x1C:0x20])[0] & 0xFFFFFF
    nw = struct.unpack(">H", hd[0x24:0x26])[0]; wa = struct.unpack(">I", hd[0x28:0x2C])[0] & 0xFFFFFF
    V = np.array([struct.unpack(">hhh", sb[va + i * 6:va + i * 6 + 6]) for i in range(nv)], float)
    T = []
    for i in range(npoly):
        p = sb[pa + i * 16:pa + i * 16 + 16]
        typ, a, b, c = struct.unpack(">HHHH", p[:8])
        d0, d1 = struct.unpack(">II", sb[sa + typ * 8:sa + typ * 8 + 8])
        T.append((a & 0x1FFF, b & 0x1FFF, c & 0x1FFF, d0, d1, (a >> 13) & 7))
    W = [struct.unpack(">hhhhhhI", sb[wa + i * 16:wa + i * 16 + 16]) for i in range(nw)]
    return V, T, W


def room_actors(scene, nrooms):
    out = []
    for r in range(nrooms):
        rb = open(os.path.join(ROM, "%s_room_%d" % (scene, r)), "rb").read()
        o = 0
        while True:
            cmd, n, _, d = struct.unpack(">BBHI", rb[o:o + 8]); o += 8
            if cmd == 0x14:
                break
            if cmd == 0x01:
                a = d & 0xFFFFFF
                for k in range(n):
                    out.append((r,) + struct.unpack(">hhhhhhhh", rb[a + k * 16:a + k * 16 + 16]))
    return out


def dl_raw(path, obj, sym, name, scale=1.0, matte=True, cutout=None):
    data = A.obj(obj); sy = A.symbols(obj)
    g = Gfx(Segs({6: data, 4: A.obj("gameplay_keep")}), bake=True)
    g.reset(); g.run(0x06000000 | sy[sym])
    groups = {}
    for k, vs in g.tris:
        groups.setdefault(k, []).append(vs)
    dm = {}
    for k, mt in g.mats.items():
        mt = copy.copy(mt); mt.matte = matte; bake_combiner(mt)
        if cutout is not None:
            mt.alpha = True; mt.xlu = False; mt.cutoff = cutout      # Netz: Loecher durchsichtig (OoT zeichnet es XLU)
        dm[k] = mt
    os.makedirs(os.path.dirname(path), exist_ok=True)
    oot_glb.write(path, groups, dm, scale=scale, offset=(0, 0, 0), name=name)
    return len(g.tris)


def geometry(out):
    """Sichtbare Raeume (ohne Kollision) + unsichtbare Original-Kollision (mit Kollision), Kletterflaechen, Wasser."""
    if os.environ.get("GI_YDAN_REMASTER", "1") != "0":
        # Remaster (08.11.): Valheim-Rinde/-Waldboden/-Wasser, Original-Licht als Farbfaktor (oot_ydan_remaster.py)
        import oot_ydan_remaster
        _sb, tris, dm = oot_ydan_remaster.remaster(ROM)
    else:
        tris, dm = _n64_look()
    rel = "statics/ydan"
    import oot_glb
    oot_glb.EXT_IMAGES = "tex"           # Texturen einmal als PNG neben den Kacheln (statt in jeder Kachel)
    try:
        st = oot_pack.write_tiles(tris, dm, os.path.join(out, rel), rel, "ydan", tile=640)
    finally:
        oot_glb.EXT_IMAGES = None
    r = _geometry_rest(out, rel, st, dm)
    # Lichtschacht: seit 08.11. ~09:00 als Teilchen (oot_atmos "rays"/"dust", src/OotAtmos.cs) statt Kegel-Modell
    # (der Kegel sah von innen wie eine weisse Wand aus); oot_ydan_remaster.shaft() bleibt fuer Vergleiche
    return r


def _n64_look():
    """Bisheriger Look: Original-Texturen, N64-Licht in 6 Stufen in die Textur gerechnet."""
    sb, tris, mats = oot_pack.scene_tris(ROM, "ydan", "dangeon")
    # N64-Licht steckt in den Vertexfarben: je Dreieck die mittlere Helligkeit in 6 Stufen in die Textur rechnen (Valheim-Shader
    # nutzt keine Vertexfarben), so bleiben die dunklen Ecken und hellen Lichtflecken des Originals erhalten
    dm, tris2 = {}, []
    for k, vs in tris:
        c = np.mean([v[2][:3] for v in vs], axis=0) / 255.0
        lum = float(0.3 * c[0] + 0.59 * c[1] + 0.11 * c[2])
        b = max(0.18, min(1.0, round(lum * 6) / 6.0))
        kk = (k, "vc", b) if not isinstance(k, tuple) else k + ("vc", b)
        if kk not in dm:
            m = copy.copy(mats[k]); m.matte = True
            m.lift = LIFT                     # wie im Original: Licht steckt in der Textur, Valheim-Licht kommt nur dazu
            if m.rgba is not None:
                a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
                a[:, :3] *= b * np.array([1.0, 0.95, 0.86])
                m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
            else:
                m.prim = tuple(int(x * b) for x in m.prim[:3]) + (m.prim[3],)
            dm[kk] = m
        tris2.append((kk, [(p, uv, (255, 255, 255, 255)) for p, uv, _ in vs]))
    return tris2, dm


def _geometry_rest(out, rel, st, dm):
    for e in st:
        e["p"] = [round(e["p"][i] + OFF_YDAN[i], 3) for i in range(3)]
        e["c"] = 0
        e["vis"] = 300
    sb, h = scene_header("ydan")
    V, T, W = collision_full(sb, h)
    # Kollision in Kacheln, unsichtbares Material
    class HM:
        pass
    hm = copy.copy(list(dm.values())[0]); hm.hidden = True; hm.matte = True; hm.normal = None; hm.tint = None; hm.valheim = None; hm.lift = 0
    cells = collections.defaultdict(list)
    climbs = []
    for a, b, c, d0, d1, fl in T:
        P = V[[a, b, c]]
        n = np.cross(P[1] - P[0], P[2] - P[0]); nn = np.linalg.norm(n)
        if nn < 1e-6:
            continue
        cen = P.mean(0)
        cells[(int(math.floor(cen[0] / 640)), int(math.floor(cen[2] / 640)))].append([(tuple(p), (0.0, 0.0), (255, 255, 255, 255)) for p in P])
        wt = (d0 >> 21) & 0x1F
        if wt in (2, 3, 4):           # Leiter, Leiter-Ende, Ranken (z_bgcheck.c D_80119D90: WALL_FLAG_1/2/3)
            climbs.append({"t": wt, "v": [YV(tuple(p)) for p in P]})
    for (i, j), lst in sorted(cells.items()):
        cc = ((i + 0.5) * 640, 0.0, (j + 0.5) * 640)
        name = "ydancol_%d_%d.glb" % (i, j)
        oot_glb.write(os.path.join(out, rel, name), {"col": lst}, {"col": hm}, scale=S, offset=cc, name=name[:-4])
        p = oot_pack.V(cc)
        st.append({"m": rel + "/" + name, "p": [round(p[k] + OFF_YDAN[k], 3) for k in range(3)], "q": oot_pack.Q180, "s": [1, 1, 1],
                   "r": 40.0, "c": 1, "vis": 300})
    water = []
    for xmin, ys, zmin, xl, zl, _, prop in W:
        a, b = YV((xmin, ys, zmin)), YV((xmin + xl, ys, zmin + zl))
        water.append({"min": [min(a[0], b[0]), min(a[2], b[2])], "max": [max(a[0], b[0]), max(a[2], b[2])], "y": a[1]})
    return st, climbs, water


def actors(out):
    ids = I.actor_ids(); inv = {v: k for k, v in ids.items()}
    acts = room_actors("ydan", 12)
    enemies, webs, torches, chests, hints = [], [], [], [], []
    d = os.path.join(out, "ydan")
    # Moritz 09.11.: Wand-Netz vor der Leiter (Raum 2) war undurchsichtig, das Ziel dahinter nicht zu sehen
    dl_raw(os.path.join(d, "web_floor.glb"), "object_ydan_objects", "gDTWebFloorDL", "web_floor", cutout=0.5)
    dl_raw(os.path.join(d, "web_wall.glb"), "object_ydan_objects", "gDTWebWallDL", "web_wall", cutout=0.62)
    for r, aid, x, y, z, rx, ry, rz, prm in acts:
        nm = inv.get(aid & 0xFFFF, "?")
        p = YV((x, y, z))
        if nm == "ACTOR_EN_DEKUBABA":
            enemies.append({"type": "dekubaba", "pos": p, "yaw": ry, "big": 1 if (prm & 0xFFFF) == 1 else 0, "room": r})
        elif nm == "ACTOR_EN_KAREBABA":
            enemies.append({"type": "karebaba", "pos": p, "yaw": ry, "room": r})
        elif nm == "ACTOR_ELF_MSG":
            sx = rx * 0.04 if rx else 0.4
            sy_ = rz * 0.04 if rz else 0.4
            hints.append({"pos": p, "r": round(sx * 100 * S, 3), "h": round(sy_ * 100 * S, 3), "cuboid": bool(prm & 0x4000),
                          "forced": not (prm & 0x8000), "text": "0x%04X" % ((prm & 0xFF) + 0x100), "flag": "oot_ydan_sw_%d" % ((prm >> 8) & 0x3F)})
        elif nm == "ACTOR_ELF_MSG2":
            # Navi-Hinweis per C-oben (z. B. Raum 10, Hinweis zur brennenden Fackel), verschwindet
            # nach dem Lesen (rot.z != 1) bzw. wenn Schalter rot.y-1 gesetzt ist (Fackel an)
            q = YV((x, y - 60, z))
            kill = (ry - 1) if 0 < ry < 0x41 else -1
            hints.append({"pos": q, "r": round(170 * S, 3), "h": round(260 * S, 3), "cuboid": False, "forced": False,
                          "text": "0x%04X" % ((prm & 0xFF) + 0x100), "flag": "oot_ydan_msg2_%d_%d" % (r, len(hints)), "kill_sw": kill})
        elif nm == "ACTOR_EN_HINTNUTS" and (prm & 0xFFFF) != 0xA:
            hi = (prm >> 8) & 0xFF
            enemies.append({"type": "scrub", "pos": p, "yaw": ry, "num": prm & 0xFF, "text": ("0x%04X" % (0x1000 | hi)) if hi else "",
                            "flag": "oot_ydan_scrub_%d_%d" % (r, len([e for e in enemies if e["type"] == "scrub"])), "room": r})
        elif nm == "ACTOR_EN_SW":
            gold = bool(prm & 0x8000)
            enemies.append({"type": "wallst", "pos": p, "yaw": ry, "gold": 1 if gold else 0,
                            "flag": ("oot_gs_ydan_%d" % (prm & 0xFF)) if gold else "", "room": r})
        elif nm == "ACTOR_EN_GOMA" and (prm & 0xFFFF) in (6, 7, 8, 9):
            enemies.append({"type": "goma", "pos": p, "room": r})          # Gohma-Larve als Ei (8/9 an der Decke)
        elif nm == "ACTOR_EN_ST":
            enemies.append({"type": "st", "pos": p, "yaw": ry, "big": 1 if (prm & 0xFFFF) == 1 else 0, "room": r})
        elif nm == "ACTOR_BG_YDAN_SP":
            typ = (prm >> 12) & 0xF
            webs.append({"type": "floor" if typ == 0 else "wall", "pos": p, "yaw": yaw_v(ry), "rx": rx, "ry": ry, "flag": "oot_ydan_web_%d" % (prm & 0x3F),
                         "model": "ydan/web_floor.glb" if typ == 0 else "ydan/web_wall.glb", "room": r})
        elif nm == "ACTOR_OBJ_SYOKUDAI":
            torches.append({"pos": p, "type": (prm >> 12) & 3, "lit": 1 if (prm & 0x400) else 0, "flag": prm & 0x3F, "n": (prm >> 6) & 0xF, "room": r})
        elif nm == "ACTOR_EN_BOX":
            chests.append({"pos": p, "yaw": ry, "gi": (prm >> 5) & 0x7F, "flag": prm & 0x1F, "room": r})
    import oot_text
    hint_texts = oot_text.export(sorted({int(h["text"], 16) for h in hints}))
    return {"enemies": enemies, "webs": webs, "torches": torches, "chests": chests, "hints": hints, "hint_texts": hint_texts}


def goma(out):
    """Gohma-Larve (object_gol): Skelett + Original-Animationen, Ei (gObjectGolEggDL, Segment 8 = Textur-Rollen -> leer)."""
    import oot_npc, oot_kf2
    os.makedirs(os.path.join(out, "npc"), exist_ok=True); os.makedirs(os.path.join(out, "enemy"), exist_ok=True)
    an = {"idle": "Stand", "run": "Running", "jump": "JumpHeadbutt", "prep": "PrepareJump", "land": "LandFromJump", "hurt": "Damaged",
          "die": "Death", "dead": "DeadTwitching"}
    oot_npc.char_glb("object_gol", "gObjectGolSkel", {k: ("gObjectGol%sAnim" % v, 0, -1) for k, v in an.items()}, {}, "goma_larva", out)
    oot_kf2.dl_raw(os.path.join(out, "enemy", "goma_egg.glb"), "object_gol", "gObjectGolEggDL", "goma_egg", bake=True,
                   segs={8: bytes.fromhex("DF00000000000000")}, dark=0.8)
    return {"model": "npc/goma_larva.glb", "egg": "enemy/goma_egg.glb"}


def skulltula(out):
    """Skulltula (object_st): Skelett + Original-Animationen (Ruhe/Landen/Drehen)."""
    import oot_npc
    os.makedirs(os.path.join(out, "npc"), exist_ok=True)
    import oot_kf2
    oot_kf2.dl_raw(os.path.join(out, "enemy", "skull_token.glb"), "object_st", "gSkulltulaTokenDL", "skull_token", bake=True, matte=False)
    oot_kf2.dl_raw(os.path.join(out, "items", "gi_token.glb"), "object_gi_sutaru", "gGiSkulltulaTokenDL", "gi_token", scale=oot_kf2.GI_SCALE, bake=True, matte=False)
    oot_npc.char_glb("object_st", "object_st_Skel_005298", {"idle": ("object_st_Anim_000304", 0, -1), "land": ("object_st_Anim_0055A8", 0, -1),
                                                           "spin": ("object_st_Anim_005B98", 0, -1)}, {}, "skulltula", out)
    return {"model": "npc/skulltula.glb", "token": "enemy/skull_token.glb"}


def scrub(out, texts):
    """Deku-Schrubbe (object_hintnuts): Skelett + alle Original-Animationen, Blume, Nuss; Hinweistexte."""
    import oot_npc, oot_kf2, oot_text
    an = {"idle": "Stand", "up": "Up", "stand": "Stand", "spit": "Spit", "burrow": "Burrow", "unburrow": "Unburrow", "run": "Run", "talk": "Talk",
          "freeze": "Freeze", "look": "LookAround"}
    oot_npc.char_glb("object_hintnuts", "gHintNutsSkel", {k: ("gHintNuts%sAnim" % v, 0, -1) for k, v in an.items()}, {}, "deku_scrub", out)
    oot_kf2.dl_raw(os.path.join(out, "enemy", "scrub_flower.glb"), "object_hintnuts", "gHintNutsFlowerDL", "scrub_flower", bake=True)
    oot_kf2.dl_raw(os.path.join(out, "enemy", "scrub_nut.glb"), "object_hintnuts", "gHintNutsNutDL", "scrub_nut", bake=True)
    ids = sorted({int(t, 16) for t in texts if t})
    return {"model": "npc/deku_scrub.glb", "flower": "enemy/scrub_flower.glb", "nut": "enemy/scrub_nut.glb"}, oot_text.export(ids)


def torch_model(out):
    d = os.path.join(out, "ydan")
    names = ["gGoldenTorchDL", "gTimedTorchDL", "gWoodenTorchDL"]          # ObjSyokudai_Draw displayLists[Typ]
    for k in names:
        dl_raw(os.path.join(d, "torch_%s.glb" % k), "object_syokudai", k, "torch_" + k)
    return names


def deku_tree(out):
    """Mund des Deku-Baums (Bg_Treemouth, Massstab 1) + Ablauf-Daten fuer src/OotDekuTree.cs"""
    import oot_text
    d = os.path.join(out, "ydan"); os.makedirs(d, exist_ok=True)
    data = A.obj("object_spot04_objects"); sy = A.symbols("object_spot04_objects")
    g = Gfx(Segs({6: data, 4: A.obj("gameplay_keep")}), bake=True)
    g.reset(); g.run(0x06000000 | sy["gDekuTreeMouthDL"])
    groups, dm = {}, {}
    for k, vs in g.tris:
        groups.setdefault(k, []).append(vs)
    for k, m in g.mats.items():
        m = copy.copy(m); m.matte = True; bake_combiner(m); dm[k] = m
    oot_glb.write(os.path.join(d, "deku_mouth.glb"), groups, dm, scale=S, offset=(0, 0, 0), name="deku_mouth")
    KF = lambda p: [round(float(v), 3) for v in oot_kf.V(p)]
    sb, h = scene_header("ydan")
    n, a = h[0x00]
    s0 = struct.unpack(">hhhhhhhh", sb[a:a + 16])
    return {"mouth": "ydan/deku_mouth.glb", "closed": KF((4029, 136, -1255)), "open": KF((3869, -263, -1163)),
            "trigger": KF((3882, -171, -1161)), "radius": round(1658 * S, 2),
            "enter": KF((3995, -171, -1238)), "warp": YV((s0[1], s0[2], s0[3] - 40)), "warp_yaw": yaw_v(s0[5]),
            "cam": KF((3420, -40, -860)), "look": KF((3980, 200, -1230)),
            "texts": oot_text.export([0x107D, 0x1015, 0x1016, 0x1017, 0x1018])}


CHEST_GI = {0xB4FF: ("token", "object_gi_sutaru", "gGiSkulltulaTokenDL", "GI_SKULL_TOKEN"),
            0x41: ("map", "object_gi_map", "gGiDungeonMapDL", "GI_DUNGEON_MAP"),
            0x40: ("compass", "object_gi_compass", "gGiCompassDL", "GI_COMPASS"),
            0x05: ("slingshot", "object_gi_pachinko", "gGiSlingshotDL", "GI_SLINGSHOT"),
            0x48: ("heart", "object_gi_heart", "gGiRecoveryHeartDL", "GI_RECOVERY_HEART")}


def chests_cfg(out, chests):
    """Truhen im Deku-Baum -> oot_items-Ergaenzung (chests, gi_models, gi_texts), GI-Modelle mit Original-Farben."""
    import oot_kf2, oot_text
    base = oot_kf2.items(out)
    gm, gt = dict(base["gi_models"]), dict(base["gi_texts"])
    for gi, (key, obj, dl, gname) in CHEST_GI.items():
        oot_kf2.dl_raw(os.path.join(out, "items", "gi_%s.glb" % key), obj, dl, "gi_" + key, scale=oot_kf2.GI_SCALE, matte=False, bake=True)
        gm[key] = "items/gi_%s.glb" % key
        tid = oot_kf2.gi_text_id(gname)
        p = oot_text.pages(tid) if tid else None
        if p:
            gt[key] = p["pages"]
    lst = []
    for c in chests:
        e = CHEST_GI.get(c["gi"])
        if not e:
            continue
        lst.append({"pos": c["pos"], "yaw": round((c["yaw"] & 0xFFFF) / 65536.0 * 360.0, 2), "key": e[0],
                    "flag": "oot_ydan_chest_%d" % c["flag"], "text": gt.get(e[0], [])})
    return {"chests": lst, "gi_models": gm, "gi_texts": gt, "pickups": base["pickups"]}


def export(out):
    st, climbs, water = geometry(out)
    ac = actors(out)
    tn = torch_model(out)
    sb, h = scene_header("ydan")
    n, a = h[0x00]
    spawns = [struct.unpack(">hhhhhhhh", sb[a + i * 16:a + i * 16 + 16]) for i in range(n)]
    s0 = spawns[0]
    KF = lambda p: [round(float(v), 3) for v in oot_kf.V(p)]
    allx = [c["v"][i][0] for c in climbs for i in range(3)]
    cfg = {"climbs": climbs, "water": water, "webs": ac["webs"], "hints": ac["hints"], "hint_texts": ac["hint_texts"], "torches": ac["torches"], "chests": ac["chests"],
           "torch_models": ["ydan/torch_%s.glb" % k for k in tn], "spawn": YV(s0[1:4]), "spawn_yaw": yaw_v(s0[5])}
    spots = [
        # Mund des Deku-Baums (Bg_Treemouth, spot04 (3882, -171, -1161)) -> Eingang des Dungeons (Spawn 0)
        {"id": "ydan_enter", "pos": KF((3882, -171, -1110)), "radius": 0.0, "height": 0.0, "zone": True, "once": False,      # jetzt OotDekuTree
         "do": [{"warp": YV((s0[1], s0[2], s0[3] - 40)), "yaw": yaw_v(s0[5])}]},
        # zurueck: Eingangsgang hinten -> vor den Deku-Baum (spot04 Spawn 1)
        {"id": "ydan_exit", "pos": YV((-4, 0, 660)), "radius": 1.4, "height": 3.0, "zone": True, "once": False,
         "do": [{"warp": KF((3896, -153, -1184 + 60)), "yaw": yaw_v(-9102 + 0x8000)}]},
    ]
    sc, sct = scrub(out, [e["text"] for e in ac["enemies"] if e["type"] == "scrub"])
    return {"cfg": cfg, "deku": deku_tree(out), "enemies": ac["enemies"], "st": skulltula(out), "scrub": sc, "enemy_texts": sct, "statics": st, "spots": spots, "items": chests_cfg(out, ac["chests"]),
            "rep": {"statics": len(st), "climbs": len(climbs), "water": water, "webs": len(ac["webs"]), "torches": len(ac["torches"]),
                    "chests": ac["chests"], "enemies": len(ac["enemies"]), "torch_models": tn}}


if __name__ == "__main__":
    import json
    import sys
    out = sys.argv[1]
    r = export(out)
    json.dump(r, open(os.path.join(out, "ydan.json"), "w"), indent=1)
    print(json.dumps(r["rep"], indent=1))


# ---------------- Bewegliche Objekte (src/OotYdanObj.cs) ----------------
def obj_glb(path, obj, sym, name, seg=6, segs=None, env=None, scale=1.0):
    """DL eines Objekts als GLB (OoT-Einheiten * scale), Kombinierer eingerechnet, optional Umgebungsfarbe (gDPSetEnvColor vor dem DL)."""
    data = A.obj(obj); sy = A.symbols(obj)
    m = {seg: data, 4: A.obj("gameplay_keep")}
    m.update(segs or {})
    g = Gfx(Segs(m), bake=True)
    g.reset()
    if env is not None:
        g.env = tuple(env)
    g.run((seg << 24) | sy[sym])
    groups = {}
    for k, vs in g.tris:
        groups.setdefault(k, []).append(vs)
    dm = {}
    for k, mt in g.mats.items():
        mt = copy.copy(mt); mt.matte = True; bake_combiner(mt); dm[k] = mt
    os.makedirs(os.path.dirname(path), exist_ok=True)
    oot_glb.write(path, groups, dm, scale=scale, offset=(0, 0, 0), name=name)
    return len(g.tris)


# Obj_Makeoshihiki (z_obj_makeoshihiki.c sBlocks[1], Deku-Baum B1): Startplaetze, unk_24 (1 = Klang, 2 = fest), Typ/Farbe
MAKEOSHI_YDAN = {"pos": [(-605, -820, -290), (-365, -905, -290), (-365, -905, -290)], "bits": [0, 3, 0], "flags": [[0, 0], [1, 0], [0, 1]],
                 "type": 0, "color": 3, "yaw": 0}


def objects(out):
    """Lift, Leiter/Stachelstamm, Wasser-Bloecke, Schalter, Schiebebloecke -> pack.json "oot_ydan_obj"."""
    d = os.path.join(out, "ydan")
    sc = 0.1 * S
    dk = A.obj("gameplay_dangeon_keep"); dks = A.symbols("gameplay_dangeon_keep")
    rep = {
        "lift": obj_glb(os.path.join(d, "lift.glb"), "object_d_lift", "gCollapsingPlatformDL", "lift", scale=sc),
        "ladder": obj_glb(os.path.join(d, "ladder.glb"), "object_ydan_objects", "gDTFallingLadderDL", "ladder", scale=sc),
        "spikelog": obj_glb(os.path.join(d, "spikelog.glb"), "object_ydan_objects", "gDTRollingSpikeTrapDL", "spikelog", scale=sc),
        "hasi_block": obj_glb(os.path.join(d, "hasi_block.glb"), "object_ydan_objects", "gDTSlidingPlatformDL", "hasi_block", scale=sc),
        "hasi_3": obj_glb(os.path.join(d, "hasi_3blocks.glb"), "object_ydan_objects", "gDTRisingPlatformsDL", "hasi_3blocks", scale=sc),
        "water": obj_glb(os.path.join(d, "hasi_water.glb"), "object_ydan_objects", "gDTWaterPlaneDL", "hasi_water", scale=sc),
        "switch": obj_glb(os.path.join(d, "switch.glb"), "gameplay_dangeon_keep", "gFloorSwitch1DL", "switch", seg=5, scale=sc),
        "block": obj_glb(os.path.join(d, "pushblock.glb"), "gameplay_dangeon_keep", "gPushBlockDL", "pushblock", seg=5,
                         segs={8: dk[dks["gPushBlockSilverTex"]:]}, env=(110, 86, 40, 255), scale=sc),
    }
    # Augen-Schalter (Obj_Switch Typ 2, ObjSwitch_DrawEye): je Untertyp ein DL, Bild in Segment 8 wechselt (offen .. zu)
    eye_tex = {0: ["gEyeSwitchGoldOpenTex", "gEyeSwitchGoldOpeningTex", "gEyeSwitchGoldClosingTex", "gEyeSwitchGoldClosedTex"],
               1: ["gEyeSwitchSilverOpenTex", "gEyeSwitchSilverHalfTex", "gEyeSwitchSilverClosedTex", "gEyeSwitchSilverClosedTex"]}
    for sub, texs in eye_tex.items():
        for i, tx in enumerate(texs):
            rep["eye%d_%d" % (sub, i)] = obj_glb(os.path.join(d, "eye%d_%d.glb" % (sub, i)), "gameplay_dangeon_keep", "gEyeSwitch%dDL" % (sub + 1),
                                                 "eye%d_%d" % (sub, i), seg=5, segs={8: dk[dks[tx]:]}, scale=sc)
    ids = I.actor_ids(); inv = {v: k for k, v in ids.items()}
    lst = []
    seen = set()
    for r, aid, x, y, z, rx, ry, rz, prm in room_actors("ydan", 12):
        nm = inv.get(aid & 0xFFFF, "?"); prm &= 0xFFFF
        key = (nm, x, y, z)
        if key in seen:
            continue
        seen.add(key)
        e = {"room": r, "pos": YV((x, y, z)), "oot": [x, y, z], "rot": [rx, ry, rz]}
        if nm == "ACTOR_OBJ_LIFT":
            e.update(type="lift", small=(prm >> 1) & 1, flag=(prm >> 2) & 0x3F, timer=(prm >> 8) & 7)
        elif nm == "ACTOR_BG_YDAN_MARUTA":
            e.update(type="ladder" if (prm >> 8) & 0xFF else "spikelog", flag=prm & 0xFF)
        elif nm == "ACTOR_BG_YDAN_HASI":
            kind = prm & 0xFF; flag = (prm >> 8) & 0x3F
            if kind == 2 and flag == 0x3F:
                continue               # zweite Liste derselben drei Bloecke (Raum 3, Schalter-Merker 0x3F wird nie gesetzt)
            e.update(type=["hasi_block", "hasi_water", "hasi_3"][kind], flag=flag)
            if kind == 1:
                e["water"] = 1          # colHeader->waterBoxes[1]
        elif nm == "ACTOR_OBJ_SWITCH":
            if prm & 7 == 2:            # Augen-Schalter: mit der Schleuder treffen
                e.update(type="eye", sub=(prm >> 4) & 7, flag=(prm >> 8) & 0x3F)
            elif prm & 7 not in (0, 1):
                continue
            else:
                e.update(type="switch", sub=(prm >> 4) & 7, flag=(prm >> 8) & 0x3F, rusty=prm & 7)
        elif nm == "ACTOR_OBJ_OSHIHIKI":
            e.update(type="block", kind=prm & 0xF, flag=(prm >> 8) & 0xFF)
        elif nm == "ACTOR_OBJ_MAKEOSHIHIKI":
            m = MAKEOSHI_YDAN
            e.update(type="makeblock", flag1=prm & 0x3F, use1=0 if prm & 0x40 else 1, flag2=(prm >> 8) & 0x3F, use2=0 if prm & 0x4000 else 1,
                     spots=[YV(p) for p in m["pos"]], spots_oot=[list(p) for p in m["pos"]], bits=m["bits"], flags=m["flags"], kind=m["type"])
        else:
            continue
        lst.append(e)
    # Tueren (Door_Shutter, Uebergangs-Akteure): Schiebetuer gDTDungeonDoor1/2DL, Gitter gDoorMetalBarsDL (gameplay_keep 0x4B0D0)
    rep["door1"] = obj_glb(os.path.join(d, "door1.glb"), "object_ydan_objects", "gDTDungeonDoor1DL", "door1", scale=S)
    rep["door2"] = obj_glb(os.path.join(d, "door2.glb"), "object_ydan_objects", "gDTDungeonDoor2DL", "door2", scale=S)
    gk = A.obj("gameplay_keep")
    g = Gfx(Segs({4: gk}), bake=True); g.reset(); g.run(0x04000000 | 0x4B0D0)
    groups = {}
    for k, vs in g.tris:
        groups.setdefault(k, []).append(vs)
    dm = {}
    for k, mt in g.mats.items():
        mt = copy.copy(mt); mt.matte = True; bake_combiner(mt); dm[k] = mt
    oot_glb.write(os.path.join(d, "bars.glb"), groups, dm, scale=S, offset=(0, 0, 0), name="bars")
    sb, h = scene_header("ydan")
    n, a = h[0x0E]
    # Raum "frei" (Flags_GetClear): Schrubbe besiegt bzw. 2-3-1-Raetsel geloest (OotEnemies)
    scr = collections.defaultdict(list)
    for r, aid, x, y, z, rx, ry, rz, prm in room_actors("ydan", 12):
        if inv.get(aid & 0xFFFF) == "ACTOR_EN_HINTNUTS" and (prm & 0xFFFF) != 0xA:
            scr[r].append("oot_ydan_scrub_%d_%d" % (r, sum(len(v) for v in scr.values())))
    clear = {str(r): (["oot_ydan_scrubs_231"] if len(v) >= 3 else v) for r, v in scr.items()}
    doors = []
    for i in range(n):
        fr, fc, br, bc, aid, x, y, z, ry, prm = struct.unpack(">bbbbhhhhhH", sb[a + i * 16:a + i * 16 + 16])
        if inv.get(aid & 0xFFFF) != "ACTOR_DOOR_SHUTTER":
            continue
        doors.append({"type": "door", "pos": YV((x, y, z)), "oot": [x, y, z], "rot": [0, ry, 0], "front": fr, "back": br,
                      "kind": (prm >> 6) & 0xF, "flag": prm & 0x3F})
    lst += doors
    cfg = {"models": {"lift": "ydan/lift.glb", "ladder": "ydan/ladder.glb", "spikelog": "ydan/spikelog.glb", "hasi_block": "ydan/hasi_block.glb",
                      "hasi_3": "ydan/hasi_3blocks.glb", "hasi_water": "ydan/hasi_water.glb", "switch": "ydan/switch.glb", "block": "ydan/pushblock.glb",
                      "door1": "ydan/door1.glb", "door2": "ydan/door2.glb", "bars": "ydan/bars.glb",
                      **{"eye%d_%d" % (b, i): "ydan/eye%d_%d.glb" % (b, i) for b in (0, 1) for i in range(4)}},
           "origin": YV((0, 0, 0)), "s": S, "list": lst, "clear": clear}
    return cfg, rep


# ---------------- Stimmung je Raum (OotPlay "areas": Valheim-Umgebung, Hall, Teilchen) ----------------
def _box(a, b):
    p, q = YV(a), YV(b)
    return [round(min(p[0], q[0]), 2), round(min(p[1], q[1]), 2), round(min(p[2], q[2]), 2),
            round(max(p[0], q[0]), 2), round(max(p[1], q[1]), 2), round(max(p[2], q[2]), 2)]


def mood_areas():
    """Deku-Baum: Echo der Boeden (SurfaceType_GetEcho) -> Hall; Licht als Remaster je Ebene (im Original ueberall Lichteinstellung 0)."""
    X0, X1, Z0, Z1 = -2850, 520, -2950, 1760
    env = lambda base, fog, d, amb: {"base": base, "fog": fog, "fog_density": d, "amb": amb, "no_particles": True}
    out = [
        {"id": "ydan_main", "box": _box((X0, -300, Z0), (X1, 1250, Z1)), "env": "OoT_DekuTree",
         "envmod": env("Crypt", [0.30, 0.20, 0.10], 0.006, [1.0, 0.86, 0.62]), "reverb": "Cave", "motes": 0.6, "mote_color": [1.0, 0.88, 0.58, 0.5]},
        {"id": "ydan_side1", "box": _box((-1011, 380, 380), (-380, 720, 1011)), "env": "OoT_DekuSide",
         "envmod": env("Crypt", [0.32, 0.22, 0.12], 0.005, [1.2, 1.05, 0.8]), "reverb": "StoneCorridor", "motes": 0.4, "mote_color": [1.0, 0.9, 0.6, 0.45]},
        {"id": "ydan_side2", "box": _box((-1544, 260, 901), (-901, 860, 1714)), "env": "OoT_DekuSide", "reverb": "StoneCorridor", "motes": 0.4,
         "mote_color": [1.0, 0.9, 0.6, 0.45]},
        {"id": "ydan_side10", "box": _box((-1580, 700, -300), (-560, 1160, 506)), "env": "OoT_DekuSide", "reverb": "StoneCorridor", "motes": 0.4,
         "mote_color": [1.0, 0.9, 0.6, 0.45]},
        {"id": "ydan_b1", "box": _box((X0, -1100, Z0), (X1, -300, Z1)), "env": "OoT_DekuB1",
         "envmod": env("Crypt", [0.10, 0.16, 0.12], 0.009, [0.85, 0.97, 0.85]), "reverb": "Cave", "motes": 0.35, "mote_color": [0.8, 1.0, 0.85, 0.35]},
        {"id": "ydan_b2", "box": _box((X0, -2050, Z0), (X1, -1100, Z1)), "env": "OoT_DekuB2",
         "envmod": env("Crypt", [0.12, 0.06, 0.10], 0.012, [0.78, 0.64, 0.72]), "reverb": "Cave"},
        {"id": "ydan_boss", "box": _box((-2156, -1960, -2928), (-630, -1250, -952)), "env": "OoT_DekuBoss",
         "envmod": env("Crypt", [0.16, 0.04, 0.03], 0.012, [0.82, 0.56, 0.5]), "reverb": "Stoneroom"},
    ]
    # Raumlicht (im Spiel mit "oot amb" abgestimmt, 08.11.): Grundlicht + Nebel [r, g, b, dichte]
    # 08.11. ~09:00 (Wunsch: "richtig neblig", dichter als der Dunst im Dorf): Nebel 0,035-0,05, Farbe passend zum Licht
    light = {"ydan_main": ([0.66, 0.6, 0.46], [0.46, 0.4, 0.25, 0.04]), "ydan_side1": ([0.72, 0.64, 0.5], [0.44, 0.38, 0.24, 0.035]),
             "ydan_side2": ([0.72, 0.64, 0.5], [0.44, 0.38, 0.24, 0.035]), "ydan_side10": ([0.72, 0.64, 0.5], [0.44, 0.38, 0.24, 0.035]),
             "ydan_b1": ([0.56, 0.7, 0.65], [0.13, 0.26, 0.24, 0.05]), "ydan_b2": ([0.55, 0.49, 0.6], [0.14, 0.1, 0.17, 0.05]),
             "ydan_boss": ([0.64, 0.48, 0.44], [0.2, 0.07, 0.06, 0.04])}
    for a in out:
        a["ambient"], a["fog"] = light[a["id"]]
        a["title"], a["title_group"] = "title_ydan", "ydan"          # "Inside the Deku Tree" beim Betreten (Titelkarte g_pn_06)
    return out


def intro_track():
    """gDekuTreeIntroCs (beim ersten Betreten, z_demo.c EVENTCHKINF_A8): Kamera schaut hoch und faehrt dann hinab;
    CS_MISC_STOP_CUTSCENE bei Bild 165 -> Spur bis dahin (20 Bilder/s), Lage im Deku-Baum (YV)."""
    import oot_cutscene
    path = OOT_DECOMP + "/extracted/gc-eu/assets/scenes/dungeons/ydan/gDekuTreeIntroCs.csdata.inc.c"
    cs = oot_cutscene.structure(oot_cutscene.parse(open(path).read()))
    end = 166
    cam = oot_cutscene.camera(cs, end)
    first = next(i for i, c in enumerate(cam) if c)
    for i in range(first):
        cam[i] = cam[first]
    pos, rot, fov = [], [], []
    for c in cam:
        e, a = c["eye"], c["at"]
        up = oot_pack.oot_up(e, a, c["roll"])
        pos += YV(tuple(e))
        rot += oot_pack.quat_look(oot_pack.vdir(np.array(a) - np.array(e)), oot_pack.vdir(up))
        fov.append(round(c["fov"], 3))
    return {"fps": 20.0, "dur": round(end / 20.0, 3), "fov_kind": "v", "time": 0.5, "hide_player": False,
            "cam": {"pos": pos, "rot": rot, "fov": fov}, "cuts": oot_cutscene.cuts(cam),
            "fades": [{"t": 0.0, "in": True, "dur": 0.6}], "actors": {}, "lines": [], "hide_terrain": [], "clip": []}
