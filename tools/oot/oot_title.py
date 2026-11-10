"""Startbildschirm (Remaster): gHyruleFieldTitleScreenCs (spot00) – Link reitet auf Epona durch die Steppe,
Kamera 1:1 aus der Cutscene. Logo + "Press Start" kommen im Video als Ebene darueber (oot_logo.py), wie im Original
ab Spielbild ~250 bzw. ~450.

Zeitachse: Spielbild e = cs-Bild + 2 (aus den Schnitten der Aufnahme bestimmt), eine Runde = 2014 Bilder.
"""
import json
import math
import os

import numpy as np

import oot_anim as A
import oot_bones
import oot_cutscene
import oot_horse
import oot_pack

V = oot_pack.V
TITLE_FRAMES = 2014
TITLE_START = 2
LOGO_AT = 250
PRESS_AT = 450
CSP = "/extracted/gc-eu/assets/scenes/overworld/spot00/gHyruleFieldTitleScreenCs.csdata.inc.c"


class StdSkel:
    """Standard-Skelett (Glieder mit festem Display-List) im Format, das oot_horse.build_mesh/write_glb erwartet"""
    def __init__(self, data, off):
        s = A.Skel(data, off)
        self.n, self.pos, self.parent, self.child, self.sib = s.n, s.pos, s.parent, s.child, s.sib
        self.type = [11 if d else 0 for d in s.dl]
        self.seg = list(s.dl)


def player_cues(cs):
    q = []
    for k, v in cs.items():
        pass
    lst = cs["player"]
    return sorted(lst, key=lambda c: c["start"])


HORSE_ACT = {"PLAYER_CUEID_36": 1, "PLAYER_CUEID_37": 2, "PLAYER_CUEID_38": 3, "PLAYER_CUEID_64": 4, "PLAYER_CUEID_65": 5}


def rider_track(cs, n):
    """je Spielbild: Position (OoT), sichtbar, bewegt?, Gierwinkel (binang), Zustand – nachgebaut wie EnHorse_CutsceneUpdate:
    Epona galoppiert mit 8 * 1,5 Einheiten/Bild aufs Cue-Ziel zu und lenkt hoechstens 400/Bild (Horse_RotateToPoint),
    Warp-Cues (64/65) setzen sie an den Startpunkt, 65 = Aufbaeumen. Keine lineare Interpolation (die stimmt nicht)."""
    cues = player_cues(cs)
    pos, vis, moving, yaw, state = [], [], [], [], []
    act = 0; P = None; rot = 0; speed = 0.0
    for e in range(n):
        f = max(0, e - TITLE_START)
        act_c = [q for q in cues if q["start"] <= f < q["end"]]
        c = max(act_c, key=lambda q: q["start"]) if act_c else None
        if c is not None:
            a = HORSE_ACT.get(c["id"], 0)
            if a and a != act:
                if act == 0 or a in (4, 5):
                    P = np.array(c["from"], float); rot = c["rot"][1]
                act = a
            if act in (1, 4):
                end = np.array(c["to"], float)
                if np.linalg.norm(end - P) > 8.0:
                    want = int(round(math.atan2(end[0] - P[0], end[2] - P[2]) / (2 * math.pi) * 65536))
                    dlt = ((want - rot + 32768) & 0xFFFF) - 32768
                    rot = (rot + max(-400, min(400, dlt))) & 0xFFFF
                    speed = 8.0
                    P = P.copy()
                    P[1] += (end[1] - P[1]) * 0.15        # Hoehe macht im Spiel die Schwerkraft/der Boden (wird spaeter aufs Gelaende gesetzt)
                else:
                    P = end.copy(); speed = 0.0
            elif act in (3, 5):
                speed = 0.0
        if P is None:
            P = np.array(cues[0]["from"], float); rot = cues[0]["rot"][1]
        r = rot / 65536.0 * 2 * math.pi
        P = P + np.array([math.sin(r), 0.0, math.cos(r)]) * speed * 1.5
        pos.append(list(P)); vis.append(1); moving.append(speed > 0); yaw.append(rot)
        state.append("rear" if act in (3, 5) else ("gallop" if speed > 0 else "idle"))
    return pos, vis, moving, yaw, state


def epona_export(n, state, out):
    d = A.obj("object_horse"); S = A.symbols("object_horse")
    skel = oot_horse.SkinSkel(d, S["gEponaSkel"])      # Epona ist ein Skin-Skelett wie die anderen Pferde
    gal = A.Anim(d, S["gEponaGallopingAnim"], skel.n); idle = A.Anim(d, S["gEponaIdleAnim"], skel.n)
    rear = A.Anim(d, S["gEponaRearingAnim"], skel.n)
    tabs = []; cg = ci = 0.0; cr = None
    for e in range(n):
        st = state[e]
        if st == "rear" and (cr is None or cr < rear.frames - 1):
            cr = 0.0 if cr is None else cr + 1.0
            tabs.append(rear.table(int(cr))); continue
        if st != "rear":
            cr = None
        if st == "gallop":
            tabs.append(gal.table(int(cg))); cg = (cg + 2.4) % gal.frames      # playSpeed = speed * 0.3
        else:
            tabs.append(idle.table(int(ci))); ci = (ci + 1.0) % idle.frames
    tris, w, m = oot_horse.build_mesh(d, skel, tabs[0])
    # Graustufen-Texturen faerbt das Original mit der PRIM-Farbe (Eponas braunes Fell)
    for k, mm in m.items():
        if mm.rgba is None or tuple(mm.prim[:3]) == (255, 255, 255):
            continue
        a = np.frombuffer(bytes(mm.rgba), np.uint8).reshape(-1, 4).astype(float)
        if np.abs(a[:, 0] - a[:, 1]).mean() < 3:
            a[:, :3] = a[:, :3] * np.array(mm.prim[:3], float) / 255.0 * 1.0
            a[:, 3] = 255          # I-Format: Alpha = Helligkeit, im Original aber deckend (Combiner-Alpha 1) -> sonst durchsichtige Beine
            mm.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
    return oot_horse.write_glb(out, skel, tabs[0], tabs, 20.0, tris, w, m, name="epona")


def build_title(root, out):
    cs = oot_cutscene.structure(oot_cutscene.parse(open(root + CSP).read()))
    n = TITLE_FRAMES
    cam = oot_cutscene.camera(cs, cs["frames"])
    first = next(i for i, c in enumerate(cam) if c)
    pos, rot, fov = [], [], []
    for e in range(n):
        f = min(max(e - TITLE_START, first), len(cam) - 1)
        c = cam[f] or cam[first]
        ey, at = c["eye"], c["at"]
        up = oot_pack.oot_up(ey, at, c["roll"])
        pos += V(ey); rot += oot_pack.quat_look(oot_pack.vdir(np.array(at) - np.array(ey)), oot_pack.vdir(up)); fov.append(round(c["fov"], 3))
    fps = 20.0
    DY_R = np.zeros(n)
    rp, rvis, moving, ryaw, rstate = rider_track(cs, n)
    # Reiter auf das Valheim-Gelaende setzen (Remaster-Boden liegt oft hoeher als der Original-Boden -> sonst unter der Erde)
    try:
        import oot_kf_decor
        from scipy import ndimage
        hgt, _, (gx0, gz0, gc, gW, gH) = oot_kf_decor.read_vmap(os.path.join(out, "oot.vmap"))
        dy = np.zeros(n)
        for e in range(n):
            x, y, z = V(rp[e])
            i, j = int((x - gx0) / gc), int((z - gz0) / gc)
            if 0 <= i < gW and 0 <= j < gH:
                g = float(hgt[j, i])
                dy[e] = (g - y) if abs(g - y) < 4.0 else max(0.0, g - y)
        dy = ndimage.gaussian_filter1d(dy, 2.0, mode="nearest")
        DY_R[:] = dy
        rp = [[p[0], p[1] + dy[e] / oot_pack.S, p[2]] for e, p in enumerate(rp)]
        print("Reiter an Gelaende angepasst: max hoch %.2f m, max runter %.2f m" % (dy.max(), -dy.min()))
    except Exception as ex:
        print("Reiter-Hoehe:", ex)
    # Schnitte aus der Original-Kamera (vor dem Anheben)
    P0 = np.array(pos, float).reshape(-1, 3)
    cuts = [0] + [e for e in range(1, n) if np.linalg.norm(P0[e] - P0[e - 1]) > 3.0]
    # Kamera nie unter dem (Valheim-)Gelaende bzw. dem Meer – aber ruhig: Anhebung pro Schnitt geglaettet,
    # sonst huepft die Kamera ueber jeden kleinen Huegel
    try:
        import oot_kf_decor
        from scipy import ndimage
        hgt, _, (gx0, gz0, gc, gW, gH) = oot_kf_decor.read_vmap(os.path.join(out, "oot.vmap"))
        hs = ndimage.gaussian_filter(hgt, 1.0)       # Boden ohne Mini-Huckel
        lift = np.zeros(n)
        for e in range(n):
            x, y, z = P0[e]
            i, j = int((x - gx0) / gc), int((z - gz0) / gc)
            need = 30.8
            if 0 <= i < gW and 0 <= j < gH:
                need = max(need, hs[j, i] + 1.3)          # knapp ueber den Valheim-Grashalmen (Original: dicht am Boden)
            lift[e] = max(0.0, need - y)
        bounds = cuts + [n]
        for a, b in zip(bounds[:-1], bounds[1:]):
            seg = ndimage.maximum_filter1d(lift[a:b], size=11, mode="nearest")
            lift[a:b] = ndimage.gaussian_filter1d(seg, 6.0, mode="nearest")
        for e in range(n):
            pos[e * 3 + 1] = round(float(P0[e, 1] + lift[e]), 3)
    except Exception as ex:
        print("Kamera-Hoehe:", ex)
    vp = [V(p) for p in rp]
    fwd = []
    for e in range(n):       # Blickrichtung = Gierwinkel des Pferdes (Valheim-z gespiegelt)
        r = ryaw[e] / 65536.0 * 2 * math.pi
        fwd += [round(math.sin(r), 4), 0.0, round(-math.cos(r), 4)]
    os.makedirs(os.path.join(out, "cutscenes"), exist_ok=True)
    rep = {"epona": epona_export(n, rstate, os.path.join(out, "cutscenes", "epona.glb"))}
    tr = {"fps": fps, "dur": round(n / fps, 3), "fov_kind": "v", "time": 0.69, "hide_player": True,
          "cam": {"pos": pos, "rot": rot, "fov": fov},
          "cuts": cuts,
          "fades": [{"t": 0.0, "in": True, "dur": 1.5}],
          "lines": [],
          "env": {"weather": "Clear", "ambient": [0.5, 0.45, 0.54], "fog_color": [0.6, 0.52, 0.62], "fog": 1.1, "motes": 0.5}}
    tr["props"] = {"epona": {"model": "epona.glb", "clip": "cs", "scale": round(0.01 * oot_pack.S, 6), "pos": [v for p in vp for v in p],
                             "fwd": fwd, "up": [0.0, 1.0, 0.0] * n, "vis": rvis}}
    # Link (erwachsen) im Sattel: Original-Reit-Animationen aus link_animetion
    link = oot_bones.Char("link_adult"); pl = oot_bones.Player(link)
    tabs = []; cur = None
    for e in range(n):
        want = "gPlayerAnim_link_uma_anim_fastrun" if moving[e] else "gPlayerAnim_link_uma_wait_1"
        if want != cur:
            pl.play("LINK", want, 1.5 if moving[e] else 1.0, loop=True, morph=-4 if cur else 0); cur = want
        tabs.append(pl.step()[0])
    yaw_bin = []
    for e in range(n):
        f = fwd[e * 3:e * 3 + 3]
        yaw_bin.append(int(round(math.atan2(f[0], -f[2]) / (2 * math.pi) * 65536)) & 0xFFFF)   # Valheim-z ist gespiegelt
    saddle = [list(np.array(p, float) + np.array([0.0, SADDLE_Y, 0.0])) for p in rp]
    # Link: Original-Modell (erwachsen) als Requisite wie Epona (Moritz: keine Valheim-Figur)
    import oot_link
    rep["link"] = oot_link.export_cs_adult(os.path.join(out, "cutscenes", "title_link.glb"), tabs, fps)
    tr["actors"] = {}
    tr["props"]["link"] = oot_link.cs_prop("title_link.glb", [v for p in saddle for v in V(p)], ryaw, n, vis=list(rvis))
    json.dump(tr, open(os.path.join(out, "cutscenes", "title.json"), "w"), separators=(",", ":"))
    cast_p = os.path.join(out, "cast.json")
    cast = json.load(open(cast_p)) if os.path.exists(cast_p) else {}
    cast["link_adult"] = {"model": "male", "hair": "Hair3", "skin": [0.95, 0.8, 0.7], "hair_color": [0.88, 0.66, 0.28],
                          "chest": "ArmorTunic7", "chest_hue": 115, "chest_sat": 0.85, "chest_val": 0.6, "legs": "ArmorLeatherLegs",
                          "helmet": "", "shoulder": "", "left_back": "ShieldWood", "right_back": "SwordBronze", "back_scale": 0.8}
    json.dump(cast, open(cast_p, "w"), indent=1)
    c0 = cam[first]["eye"]
    os.makedirs(os.path.join(out, "shots"), exist_ok=True)
    # Kamera fliegt ueber die ganze Steppe: Spieler in die Mitte, Valheim laedt das Gelaende im weiten Umkreis (zone_area)
    C = np.array(pos, float).reshape(-1, 3); mid = (C.min(0) + C.max(0)) / 2
    json.dump({"cutscene": "title", "from": 0.0, "dur": round(n / fps, 2), "fps": 60, "grass": True,
               "player_at": [round(float(mid[0]), 2), 45.0, round(float(mid[2]), 2)], "follow": True, "preload": 20},
              open(os.path.join(out, "shots", "title.json"), "w"), indent=1)
    cl = json.load(open(os.path.join(out, "cutscenes.json")))
    cl = [c for c in cl if c["id"] != "title"] + [{"id": "title", "track": "cutscenes/title.json"}]
    json.dump(cl, open(os.path.join(out, "cutscenes.json"), "w"), indent=1)
    return {"frames": n, "rider_frames": int(sum(rvis)), **rep}


SADDLE_Y = -6.0       # Huefte auf den Sattel (-18 war zu tief: Link sass im Pferd)
