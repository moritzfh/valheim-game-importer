"""Bewohner des Kokiri-Walds als Original-Modelle (GLB mit Skelett + Original-Animationen) und ihre Plaetze/Texte.

  Kokiri (En_Ko): Koerper-Skelett gKm1Skel (Junge) / gKw1Skel (Maedchen), Kopf an Glied 15 aus object_km1/kw1/fa,
                  Kleidung per Umgebungsfarbe (Segment 8 = Tunika, 9 = Stiefel), Animationen aus object_os_anime.
  Saria (En_Sa):  object_sa, Augen Segment 8/9, Mund 0x0A.
  Mido (En_Md):   object_md, Augen Segment 8.

  export(out) -> pack.json-Block "oot_npcs"
"""
import os
import struct
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_anim as A
import oot_horse
import oot_items
import oot_kf
import oot_link
import oot_text
from oot_gfx import Segs, Gfx

FPS = 30.0
KO_ANIMS = {   # Clip -> (Animation, Startbild, Endbild oder -1)
    "idle": ("gKokiriIdleAnim", 0, -1), "laughing": ("gKokiriLaughingAnim", 0, -1),
    "lifting_rock": ("gKokiriLiftingRockAnim", 0, -1), "reclined_standing": ("gKokiriRecliningStandingAnim", 0, -1),
    "punching": ("gKokiriPunchingAnim", 0, -1), "hand_on_chest": ("gKokiriStandingHandOnChestAnim", 0, -1),
    "hands_on_hips": ("gKokiriStandingHandsOnHipsAnim", 0, -1), "sitting": ("gKokiriSittingAnim", 0, -1),
    "apprehensive": ("gKokiriStandingApprehensiveAnim", 0, -1), "blocking": ("gKokiriBlockingAnim", 2, 14),
    "cutting_grass": ("gKokiriCuttingGrassAnim", 0, -1), "backflip": ("gKokiriBackflipAnim", 0, -1),
    "standup1": ("gKokiriStandUpAnim", 0, 0), "standup2": ("gKokiriStandUpAnim", 1, 1), "standup3": ("gKokiriStandUpAnim", 2, 2),
    "right_arm_up": ("gKokiriStandingRightArmUpAnim", 0, -1), "arms_behind_back": ("gKokiriStandingArmsBehindBackAnim", 0, -1),
    "standing": ("gKokiriStandingAnim", 0, -1), "sitting_arms_up": ("gKokiriSittingArmsUpAnim", 0, -1),
    "wiping_forehead": ("gKokiriWipingForeheadAnim", 0, -1),
}
# En_Ko: Typ -> (Modell, Clip im Anfangszustand (sOsAnimeLookup Spalte 0), Text erstes Mal, Text danach)
# Maedchen 1/5/6: im Original am Anfang ein Standbild (StandUp-Bild, Tempo 0, nur der Kopf folgt Link). Wirkt in Valheim
# eingefroren (Moritz 09.11.). Ihre spaeteren Posen (Hand auf Brust, Haende in Hueften) sind auch nur 2 Bilder ohne Bewegung,
# darum Schleifen, die sich wirklich bewegen: Lachen (schaut dem Stein-Jungen zu), Warten (Fado), Arme hinten (wiegt sich).
KO_TYPES = {
    0: ("boy", "lifting_rock", 0x1004, None), 1: ("girl", "laughing", 0x1005, 0x1006), 2: ("boy", "punching", 0x1007, None),
    3: ("boy", "blocking", 0x1008, 0x1009), 4: ("boy", "cutting_grass", 0x100A, 0x100B), 5: ("girl", "idle", 0x100C, 0x100D),
    6: ("girl", "arms_behind_back", 0x100E, 0x1019), 12: ("fado", "idle", 0x10D7, 0x10D8),
}
KO_MODELS = {  # Kopf-Objekt, Kopf-DL, Augen, Skelett-Objekt, Tunika, Stiefel
    "boy": ("object_km1", "gKm1DL", None, "object_km1", "gKm1Skel", (0, 130, 70), (110, 170, 20)),
    "girl": ("object_kw1", "object_kw1_DL_002C10", "gKw1EyeOpenTex", "object_kw1", "gKw1Skel", (70, 190, 60), (100, 30, 0)),
    "fado": ("object_fa", "gFaDL", "gFaEyeOpenTex", "object_kw1", "gKw1Skel", (70, 190, 60), (100, 30, 0)),
}
SA_ANIMS = {"idle": ("gSariaWaitArmsToSideAnim", 0, -1), "wave": ("gSariaWaveAnim", 0, -1), "run": ("gSariaRunAnim", 0, -1),
            "hands_behind_back": ("gSariaHandsBehindBackWaitAnim", 0, -1), "hands_on_hips": ("gSariaStandHandsOnHipsAnim", 0, -1),
            "sitting": ("gSariaSitting1Anim", 0, -1)}
MD_ANIMS = {"idle": ("gMidoIdleAnim", 0, -1), "annoyed": ("gMidoAnnoyedAnim", 0, -1), "halt": ("gMidoHaltAnim", 0, -1),
            "curious": ("gMidoCuriousAnim", 0, -1), "walk": ("gMidoWalkAnim", 0, -1), "idle_to_halt": ("gMidoIdleToHaltAnim", 0, -1)}


def env_dl(rgb):
    """Kleine Display-Liste: gDPSetEnvColor + gSPEndDisplayList (wie EnKo_SetEnvColor)."""
    r, g, b = rgb
    return struct.pack(">IIII", 0xFB000000, (r << 24) | (g << 16) | (b << 8) | 255, 0xDF000000, 0)


def clip_tabs(adata, asym, spec, nlimbs):
    nm, f0, f1 = spec
    an = A.Anim(adata, asym[nm], nlimbs)
    f1 = an.frames - 1 if f1 < 0 else f1
    return [np.array(an.table(f), np.int64) for f in range(f0, f1 + 1)] or [np.array(an.table(0), np.int64)]


def build_mesh(skel, bind, limb_segs, head=None):
    """limb_segs(li) -> Segment-Dict; head = (Glied, Segmente, DL-Adresse) ersetzt dessen Display-Liste."""
    M = oot_horse.mats(skel, bind)
    tris, wts, allm = [], [], {}
    last_env = (255, 255, 255, 255)
    mlimb = [li for li in range(skel.n) if skel.seg[li] or (head and li == head[0])]
    for li in range(skel.n):
        dl = skel.seg[li]
        segs = limb_segs(li)
        if head and li == head[0]:
            segs, dl = head[1], head[2]
        if not dl:
            continue
        g = Gfx(Segs(segs), placeholder_segs=(11, 12, 13), track_env=True)
        g.reset(); g.env = last_env      # RDP-Zustand (Umgebungsfarbe) laeuft wie im Spiel von Glied zu Glied weiter
        g.run(dl)
        last_env = g.env
        for (k, vs), tm in zip(g.tris, g.tri_mtx):
            vs2, ww = [], []
            for (p, uv, col), mk in zip(vs, tm):
                l = mlimb[mk] if mk is not None and mk < len(mlimb) else li
                vs2.append((tuple((M[l] @ np.append(np.array(p, float), 1))[:3]), uv, col)); ww.append([(l, 1.0)])
            tris.append((k, vs2)); wts.append(ww)
        allm.update(g.mats)
    # Farben: Textur * Primfarbe, Kleidung * Umgebungsfarbe (Kombinierer)
    for m in allm.values():
        env = getattr(m, "env", None)
        mul = np.array([255.0, 255.0, 255.0])
        if tuple(m.prim[:3]) != (255, 255, 255):
            mul = mul * np.array(m.prim[:3], float) / 255.0
        if env is not None:
            mul = mul * np.array(env[:3], float) / 255.0
        if m.rgba is None:
            if env is not None:
                m.prim = (int(mul[0]), int(mul[1]), int(mul[2]), 255)
            continue
        if np.allclose(mul, 255.0):
            continue
        a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
        a[:, :3] = a[:, :3] * mul / 255.0
        m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
    return tris, wts, allm


def write(path, name, skel, clips, tris, wts, mats):
    bind = clips["idle"][0] if "idle" in clips else list(clips.values())[0][0]
    tris, wts = oot_link.subdivide(tris, wts)
    normals = oot_link.smooth_normals(tris)
    rep = oot_horse.write_glb(path, skel, bind, None, FPS, tris, wts, mats, name=name, clips=clips, valheimify_tex=True,
                              nearest=False, normals=normals)
    rep["clips"] = {k: len(v) for k, v in clips.items()}
    return rep


def kokiri_glb(kind, out):
    hobj, hdl, eyes, sobj, ssym, tunic, boots = KO_MODELS[kind]
    sdata, ssy = A.obj(sobj), A.symbols(sobj)
    hdata, hsy = A.obj(hobj), A.symbols(hobj)
    skel = oot_link.StdSkel(sdata, ssy[ssym])
    adata, asy = A.obj("object_os_anime"), A.symbols("object_os_anime")
    clips = {k: clip_tabs(adata, asy, v, skel.n) for k, v in KO_ANIMS.items()}
    gk = A.obj("gameplay_keep")
    base = {4: gk, 8: env_dl(tunic), 9: env_dl(boots)}
    seg_body = dict(base); seg_body[6] = sdata
    seg_head = dict(base); seg_head[6] = hdata
    if eyes:
        seg_head[0x0A] = hdata[hsy[eyes]:hsy[eyes] + 0x1000]
    tris, wts, mats = build_mesh(skel, clips["idle"][0], lambda li: seg_body, head=(14, seg_head, 0x06000000 | hsy[hdl]))
    return write(os.path.join(out, "npc", "kokiri_%s.glb" % kind), "kokiri_" + kind, skel, clips, tris, wts, mats)


def char_glb(obj, skel_sym, anims, segs_extra, name, out):
    data, sy = A.obj(obj), A.symbols(obj)
    skel = oot_link.StdSkel(data, sy[skel_sym])
    clips = {k: clip_tabs(data, sy, v, skel.n) for k, v in anims.items() if v[0] in sy}
    segs = {4: A.obj("gameplay_keep"), 6: data}
    for sg, tex in segs_extra.items():
        segs[sg] = data[sy[tex]:sy[tex] + 0x1000]
    tris, wts, mats = build_mesh(skel, clips["idle"][0], lambda li: segs)
    return write(os.path.join(out, "npc", name + ".glb"), name, skel, clips, tris, wts, mats)


def export(out):
    os.makedirs(os.path.join(out, "npc"), exist_ok=True)
    rep = {"models": {}}
    for kind in KO_MODELS:
        rep["models"]["kokiri_" + kind] = kokiri_glb(kind, out)
    rep["models"]["saria"] = char_glb("object_sa", "gSariaSkel", SA_ANIMS,
                                      {8: "gSariaEyeOpenTex", 9: "gSariaEyeOpenTex", 0x0A: "gSariaMouthClosedTex"}, "saria", out)
    rep["models"]["mido"] = char_glb("object_md", "gMidoSkel", MD_ANIMS, {8: "gMidoEyeOpenTex"}, "mido", out)
    # Plaetze aus den Raum-Akteurlisten von Kokiri-Wald (spot04)
    ids = oot_items.actor_ids()
    seen, npcs, texts = set(), [], set()
    for a in oot_items.room_actors():
        aid = a[0] & 0xFFFF; key = (aid, a[1], a[2], a[3])
        if key in seen:
            continue
        seen.add(key)
        pos = [round(v, 3) for v in oot_kf.V((a[1], a[2], a[3]))]
        yaw = round(180.0 - (a[5] & 0xFFFF) / 65536.0 * 360.0, 2)
        if aid == ids["ACTOR_EN_KO"]:
            t = a[7] & 0xFF
            if t not in KO_TYPES:
                continue
            kind, clip, t1, t2 = KO_TYPES[t]
            npcs.append({"id": "ko%d" % t, "model": "npc/kokiri_%s.glb" % kind, "pos": pos, "yaw": yaw, "clip": clip,
                         "talk": [{"ids": [t1] + ([t2] if t2 else [])}], "radius": 0.45})
            texts.update([t1] + ([t2] if t2 else []))
        elif aid == ids["ACTOR_EN_SA"]:
            # EnSa_GetTextId: Begruessung 0x1001 -> Fee bemerkt 0x1002 -> 0x1003; nachdem Mido Link abgewiesen hat 0x1031/0x1032
            npcs.append({"id": "saria", "model": "npc/saria.glb", "pos": pos, "yaw": yaw, "clip": "hands_behind_back",
                         "talk": [{"if": "oot_mido_denied", "ids": [0x1031, 0x1032]}, {"ids": [0x1001, 0x1002, 0x1003]}], "radius": 0.45})
            texts.update([0x1001, 0x1002, 0x1003, 0x1031, 0x1032])
        elif aid == ids["ACTOR_EN_MD"]:
            # EnMd_GetTextIdKokiriForest: 0x102F / 0x1030 ohne Ausruestung; Schwert + Deku-Schild: 0x1033 (geht zur Seite), danach 0x1034
            npcs.append({"id": "mido", "model": "npc/mido.glb", "pos": pos, "yaw": yaw, "clip": "idle",
                         "talk": [{"if": "oot_mido_moved", "ids": [0x1034]},
                                  {"if": "oot_sword+oot_shield", "ids": [0x1033], "set": "oot_mido_moved"},
                                  {"ids": [0x102F, 0x1030], "set": "oot_mido_denied"}],
                         "radius": 0.5, "block": {"radius": 2.2, "unless": "oot_mido_moved", "aside": [1.8, 0.0, 1.2]}})
            texts.update([0x102F, 0x1030, 0x1033, 0x1034])
    rep["npcs"] = npcs
    rep["texts"] = oot_text.export(sorted(texts))
    return rep
