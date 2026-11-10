"""Spielbare Original-Objekte im Kokiri-Wald (src/OotItems.cs):
  - Schatztruhe mit dem Kokiri-Schwert (En_Box, Raum 2) als Original-Modell (Truhe/Deckel getrennt, Deckel klappt auf)
  - Busch-Stellen (En_Kusa) -> abschlagbare Buesche, Rubine (En_Item00) an den Original-Stellen
  - Schwert-Symbol fuer den B-Knopf (icon_item_static)

  export(out) -> pack.json-Block "oot_items"
"""
import copy
import math
import os
import struct

import numpy as np
import re
from PIL import Image

import oot_anim as A
import oot_glb
import oot_hud
import oot_kf
import oot_pack
from oot_gfx import Gfx, Segs
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

ROM = OOT_DECOMP + "/extracted/gc-eu/baserom"
EN_BOX, EN_KUSA, EN_ITEM00 = 0x000A, 0x0125, 0x0015



def sword_text():
    """Get-Item-Text des Kokiri-Schwerts (Nachricht 0xA4) aus der eigenen ROM: erste Zeile + Hinweis-Seite in Saetzen
    (der Rest der ersten Seite verweist auf das Ausruestungs-Menue, das es hier nicht gibt)."""
    import oot_text
    p = [re.sub(r"<[^>]+>", "", t) for t in oot_text.pages(0xA4)["pages"]]
    first = re.sub(r"\s+!", "!", p[0].split("\n")[0]).strip()
    rest = re.sub(r"\s+", " ", p[1].replace("\n", " ")).strip()
    return [first] + re.split(r"(?<=\.) ", rest)

def actor_ids():
    import re
    ids = {}
    for l in open(OOT_DECOMP + "/include/tables/actor_table.h"):
        m = re.search(r'/\* (0x[0-9A-F]+) \*/ DEFINE_ACTOR\w*\((\w+),\s*(ACTOR_\w+)', l)
        if m:
            ids[m.group(3)] = int(m.group(1), 16)
    return ids


def room_actors(scene="spot04"):
    out, i = [], 0
    while os.path.exists(os.path.join(ROM, "%s_room_%d" % (scene, i))):
        rb = open(os.path.join(ROM, "%s_room_%d" % (scene, i)), "rb").read()
        off = 0
        while off + 8 <= len(rb):
            cmd, n, _, d = struct.unpack(">BBHI", rb[off:off + 8]); off += 8
            if cmd == 0x14:
                break
            if cmd == 0x01:
                a = d & 0xFFFFFF
                for k in range(n):
                    out.append(struct.unpack(">hhhhhhhh", rb[a + k * 16:a + k * 16 + 16]))
        i += 1
    return out


def dl_tris(obj, off):
    data = A.obj(obj); gk = A.obj("gameplay_keep")
    g = Gfx(Segs({6: data, 4: gk}))
    g.reset(); g.run(0x06000000 | off)
    return g.tris, g.mats


def write_part(path, tris, mats, name, both=False):
    groups = {}
    for k, vs in tris:
        groups.setdefault(k, []).append(vs)
        if both:
            groups[k].append(list(vs)[::-1])          # Rueckseite (Innenseite der offenen Truhe), Valheims Shader blenden sie sonst aus
    dm = {}
    for k, m in mats.items():
        m = copy.copy(m); m.matte = True; dm[k] = m
    oot_glb.write(path, groups, dm, scale=oot_pack.S * 0.01, offset=(0, 0, 0), name=name)


def export(out):
    d = os.path.join(out, "items"); os.makedirs(d, exist_ok=True)
    ids = actor_ids()
    acts = room_actors()
    rep = {}
    # Truhe wie EnBox_PostLimbDraw (limbIndex zaehlt ab 1): Kasten = ChestFrontDL am Wurzelglied (Glied 0, Boden = Ursprung),
    # Deckel = ChestSideAndLidDL an Glied 2 (Scharnier hinten oben, Lage aus Bild 0 der Oeffnen-Animation = zu).
    # Vorderseite (Schloss) = Modell -z: Link steht in OoT bei pos - vorwaerts * 29 (z_player.c).
    sym = A.symbols("object_box")
    data = A.obj("object_box")
    sk = A.Skel(data, sym["gTreasureChestSkel"])
    an = A.Anim(data, sym["gTreasureChestAnim_000128"], sk.n)
    R0, T0 = A.fk(sk, an.table(0)); R1, _ = A.fk(sk, an.table(an.frames - 1))
    base_t, base_m = dl_tris("object_box", sym["gTreasureChestChestFrontDL"])
    lid_t, lid_m = dl_tris("object_box", sym["gTreasureChestChestSideAndLidDL"])
    base_t = [(k, [(tuple(R0[0] @ np.array(p, float)), uv, c) for p, uv, c in vs]) for k, vs in base_t]
    lid_t = [(k, [(tuple(R0[2] @ np.array(p, float)), uv, c) for p, uv, c in vs]) for k, vs in lid_t]
    D = R1[2] @ R0[2].T                                    # Oeffnen: Drehung um die Modell-x-Achse (Scharnier)
    lid_deg = math.degrees(math.atan2(D[2, 1], D[1, 1]))
    write_part(os.path.join(d, "chest_lid.glb"), lid_t, lid_m, "chest_lid", both=True)
    write_part(os.path.join(d, "chest_base.glb"), base_t, base_m, "chest_base", both=True)
    # Gegenstand zum Hochhalten (GI-Modell wie im Original ueber Links Kopf): Kokiri-Schwert, silbern
    gi_t, gi_m = dl_tris("object_gi_sword_1", A.symbols("object_gi_sword_1")["gGiKokiriSwordDL"])
    for k, m in gi_m.items():
        px = []
        for y in range(8):
            v = int(150 + 95 * (1 - abs(y - 2.5) / 5.5))
            px += [v, min(255, v + 4), min(255, v + 12), 255] * 8
        m.rgba = bytes(px); m.w = m.h = 8; m.alpha = False; m.xlu = False
    groups = {}
    for k, vs in gi_t:
        groups.setdefault(k, []).append(vs)
    oot_glb.write(os.path.join(d, "gi_sword.glb"), groups, gi_m, scale=0.0075, offset=(0, 0, 0), name="gi_sword")
    box = [a for a in acts if (a[0] & 0xFFFF) == ids["ACTOR_EN_BOX"]]
    chest = None
    if box:
        b = box[0]
        p = oot_kf.maze_pos(oot_kf.V((b[1], b[2], b[3])))
        sc = oot_pack.S * 0.01
        piv = lambda li: [round(-T0[li][0] * sc, 5), round(T0[li][1] * sc, 5), round(T0[li][2] * sc, 5)]   # GLB-Raum (Lader spiegelt x)
        chest = {"pos": [round(v, 3) for v in p], "yaw": round(b[5] / 65536 * 360.0, 2), "item": "sword", "gi": "items/gi_sword.glb", "gi_h": 1.66,
                 "base": "items/chest_base.glb", "lid": "items/chest_lid.glb", "base_off": piv(0), "lid_off": piv(2),
                 "lid_open": round(-lid_deg, 1), "front": "-z",
                 "valheim": "piece_chest", "valheim_width": 1.5, "valheim_yaw": 180.0,    # Moritz 09.11.: Valheim-Truhe (OoT-Modell = Ersatz)
                 "text": sword_text()}
    # Buesche und Rubine an den Original-Stellen (doppelte Eintraege mehrerer Raeume nur einmal)
    seen, bushes, rupees = set(), [], []
    for a in acts:
        aid = a[0] & 0xFFFF; key = (aid, a[1], a[2], a[3])
        if key in seen:
            continue
        seen.add(key)
        if aid == ids["ACTOR_EN_KUSA"]:
            bushes.append([round(v, 3) for v in oot_kf.maze_pos(oot_kf.V((a[1], a[2], a[3])))])
        elif aid == ids["ACTOR_EN_ITEM00"] and (a[7] & 0xFF) in (0, 1, 2):
            rupees.append({"pos": [round(v, 3) for v in oot_kf.maze_pos(oot_kf.V((a[1], a[2], a[3])))], "kind": ["green", "blue", "red"][a[7] & 0xFF]})
    # B-Knopf-Symbol
    hud = os.path.join(out, "player", "hud"); os.makedirs(hud, exist_ok=True)
    for nm, im in oot_hud.textures("icon_item_static", lambda s: s == "gItemIconSwordKokiriTex", xml_name="icon_item_static_pal"):
        im.save(os.path.join(hud, nm + ".png"))
    rep = {"chest": chest, "bushes": bushes, "rupees": rupees}
    return rep
