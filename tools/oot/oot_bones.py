"""OoT-Cutscene-Animationen -> Valheim-Knochendateien (GIB2, siehe src/CutsceneBones.cs).

Je Figur: Skelett + Animationsfolge wie im Spielcode (En_Viewer fuer den Albtraum), Weltlage aus den Cue-Positionen,
Uebertragung ueber Richtungen (Gelenkpositionen + gedrehte Koerper-Vorwaerts-/Hochrichtung relativ zu einer Stehpose).
"""
import os, sys, struct, math
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_anim as A
import oot_pack
from oot_gfx import Segs, Gfx

# Gelenk -> (Glied, Versatz im Glied-Raum, OoT-Einheiten). "tip" = am weitesten entfernter Eckpunkt des Glied-Modells.
CHARS = {
    "impa": {"obj": "object_im", "skel": "gImpaSkel", "rest": ("object_im", "gImpaIdleAnim", 0), "joints": {
        "Hips": (1, 0), "Spine": (8, (500, 0, 0)), "Chest": (8, (1000, 0, 0)), "UpperChest": (8, (1500, 0, 0)),
        "Neck": (15, 0), "Head": (15, (300, 0, 0)),
        "LeftUpperArm": (9, 0), "LeftLowerArm": (10, 0), "LeftHand": (11, 0),
        "RightUpperArm": (12, 0), "RightLowerArm": (13, 0), "RightHand": (14, 0),
        "LeftUpperLeg": (2, 0), "LeftLowerLeg": (3, 0), "LeftFoot": (4, 0), "LeftToes": (4, "tip"),
        "RightUpperLeg": (5, 0), "RightLowerLeg": (6, 0), "RightFoot": (7, 0), "RightToes": (7, "tip")}},
    "ganondorf": {"obj": "object_gndd", "skel": "gYoungGanondorfSkel", "rest": ("object_gndd", "gYoungGanondorfWalkAnim", 0),
                  "y_offset": -600, "joints": {
        "Hips": (15, 0), "Spine": (1, (800, 0, 0)), "Chest": (1, (1500, 0, 0)), "UpperChest": (1, (2000, 0, 0)),
        "Neck": (8, 0), "Head": (14, (300, 0, 0)),
        "LeftUpperArm": (2, 0), "LeftLowerArm": (3, 0), "LeftHand": (4, 0),
        "RightUpperArm": (5, 0), "RightLowerArm": (6, 0), "RightHand": (7, 0),
        "LeftUpperLeg": (16, 0), "LeftLowerLeg": (17, 0), "LeftFoot": (18, 0), "LeftToes": (18, "tip"),
        "RightUpperLeg": (20, 0), "RightLowerLeg": (21, 0), "RightFoot": (22, 0), "RightToes": (22, "tip")}},
    "zelda": {"obj": "object_zl4", "skel": "gChildZeldaSkel", "rest": ("object_zl4", "gChildZeldaAnim_000654", 0), "joints": {
        "Hips": (1, 0), "Spine": (9, (300, 0, 0)), "Chest": (9, (600, 0, 0)), "UpperChest": (9, (900, 0, 0)),
        "Neck": (16, 0), "Head": (16, (250, 0, 0)),
        "LeftUpperArm": (10, 0), "LeftLowerArm": (11, 0), "LeftHand": (12, 0),
        "RightUpperArm": (13, 0), "RightLowerArm": (14, 0), "RightHand": (15, 0),
        "LeftUpperLeg": (2, 0), "LeftLowerLeg": (3, 0), "LeftFoot": (3, (900, 0, 0)),
        "RightUpperLeg": (4, 0), "RightLowerLeg": (5, 0), "RightFoot": (5, (900, 0, 0))}},
    # Kind-Link (Spieler): Link-Animationen aus gameplay_keep/link_animetion, Wurzel-Verschiebung x0,64 (Player_OverrideLimbDraw)
    "link": {"obj": "object_link_child", "skel": "gLinkChildSkel", "rest": ("LINK", "gPlayerAnim_sude_nwait", 0),
             "root_scale": 0.64, "joints": {
        "Hips": (1, 0), "Spine": (9, (300, 0, 0)), "Chest": (9, (550, 0, 0)), "UpperChest": (9, (800, 0, 0)),
        "Neck": (10, 0), "Head": (10, (0, -350, 0)),
        "LeftUpperArm": (13, 0), "LeftLowerArm": (14, 0), "LeftHand": (15, 0),
        "RightUpperArm": (16, 0), "RightLowerArm": (17, 0), "RightHand": (18, 0),
        "LeftUpperLeg": (6, 0), "LeftLowerLeg": (7, 0), "LeftFoot": (8, 0), "LeftToes": (8, "tip"),
        "RightUpperLeg": (3, 0), "RightLowerLeg": (4, 0), "RightFoot": (5, 0), "RightToes": (5, "tip")}},
}
# Kokiri-Kinder (En_Ko): Jungen object_km1, Maedchen object_kw1, Animationen in object_os_anime. Modell schaut nach +z,
# +x = links. Rumpf-Glied 7 zeigt mit +x nach oben (Kopf 14 haengt bei 1350).
_KO_J = {"Hips": (0, 0), "Spine": (7, (350, 0, 0)), "Chest": (7, (650, 0, 0)), "UpperChest": (7, (950, 0, 0)),
         "Neck": (7, (1200, 0, 0)), "Head": (14, 0),
         "LeftUpperArm": (8, 0), "LeftLowerArm": (9, 0), "LeftHand": (10, 0),
         "RightUpperArm": (11, 0), "RightLowerArm": (12, 0), "RightHand": (13, 0),
         "LeftUpperLeg": (1, 0), "LeftLowerLeg": (2, 0), "LeftFoot": (3, 0),
         "RightUpperLeg": (4, 0), "RightLowerLeg": (5, 0), "RightFoot": (6, 0)}
CHARS["kokiri_boy"] = {"obj": "object_km1", "skel": "gKm1Skel", "rest": ("object_os_anime", "gKokiriStandingAnim", 0), "joints": _KO_J}
CHARS["kokiri_girl"] = {"obj": "object_kw1", "skel": "gKw1Skel", "rest": ("object_os_anime", "gKokiriStandingAnim", 0), "joints": _KO_J}
CHARS["link_raw"] = dict(CHARS["link"], root_scale=1.0)
# Erwachsener Link (Startbildschirm: reitet Epona): gleiche Glied-Reihenfolge wie das Kind
CHARS["link_adult"] = dict(CHARS["link"], obj="object_link_boy", skel="gLinkAdultSkel", root_scale=1.0,
                           rest=("LINK", "gPlayerAnim_link_uma_wait_1", 0))   # Spieler-Animationen mit DISABLE_CHILD_ROOT_ADJUSTMENT (z. B. im Bett)
TRUNK = ["Hips", "Spine", "Chest", "UpperChest", "Neck", "Head"]
NAT = {"Spine": "Hips", "Chest": "Spine", "UpperChest": "Chest", "Neck": "UpperChest", "Head": "Neck",
       "LeftUpperArm": "UpperChest", "LeftLowerArm": "LeftUpperArm", "LeftHand": "LeftLowerArm",
       "RightUpperArm": "UpperChest", "RightLowerArm": "RightUpperArm", "RightHand": "RightLowerArm",
       "LeftUpperLeg": "Hips", "LeftLowerLeg": "LeftUpperLeg", "LeftFoot": "LeftLowerLeg", "LeftToes": "LeftFoot",
       "RightUpperLeg": "Hips", "RightLowerLeg": "RightUpperLeg", "RightFoot": "RightLowerLeg", "RightToes": "RightFoot"}


def limb_tip(data, dl):
    """Am weitesten vom Glied-Ursprung entfernter Eckpunkt seines Modells (Glied-Raum)."""
    if not dl:
        return np.zeros(3)
    gk = A.obj("gameplay_keep")
    g = Gfx(Segs({6: data, 4: gk}))
    g.reset(); g.run(dl)
    pts = [np.array(p, float) for _, vs in g.tris for p, _, _ in vs]
    if not pts:
        return np.zeros(3)
    return max(pts, key=lambda p: np.linalg.norm(p))


class Char:
    def __init__(self, key):
        c = CHARS[key]
        self.key = key
        self.data = A.obj(c["obj"]); self.sym = A.symbols(c["obj"])
        self.skel = A.Skel(self.data, self.sym[c["skel"]])
        self.joints = c["joints"]; self.yoff = c.get("y_offset", 0); self.root_scale = c.get("root_scale", 1.0)
        self.tips = {}
        for nm, (li, off) in self.joints.items():
            if isinstance(off, str):
                self.tips[nm] = limb_tip(self.data, self.skel.dl[li])
        ro, ra, rf = c["rest"]
        self.rest_tab = self.anim(ro, ra).table(rf)

    def anim(self, objname, name):
        if objname == "LINK":
            return A.LinkAnim(name, self.skel.n)
        d = A.obj(objname); s = A.symbols(objname)
        return A.Anim(d, s[name], self.skel.n)

    def joint_frames(self, tab):
        """Modellraum: {gelenk: (pos, R)}"""
        if self.root_scale != 1.0:
            tab = np.array(tab, np.int64).copy(); tab[0] = np.round(tab[0] * self.root_scale)
        R, T = A.fk(self.skel, tab)
        out = {}
        for nm, (li, off) in self.joints.items():
            o = self.tips[nm] if isinstance(off, str) else (np.zeros(3) if off == 0 else np.array(off, float))
            out[nm] = (T[li] + R[li] @ o, R[li])
        return out


def to_world(p_model, actor_pos, yaw_bin, scale=0.01, yoff=0):
    a = yaw_bin * A.B2R
    Ry = np.array([[math.cos(a), 0, math.sin(a)], [0, 1, 0], [-math.sin(a), 0, math.cos(a)]])
    w = np.array(actor_pos, float) + Ry @ (np.asarray(p_model, float) * scale) + np.array([0, yoff * scale, 0])
    return w, Ry


def vpos(w):
    return np.array(oot_pack.V(w), float)


def vdir(d):
    d = np.array([d[0], d[1], -d[2]], float)
    n = np.linalg.norm(d)
    return d / n if n > 1e-9 else d


def bones(char, tabs, actor_pos, yaw, fps, hide=None, off=(0.0, 0.0, 0.0)):
    """tabs: Gelenktabelle je Bild; actor_pos/yaw je Bild (OoT-Welt). -> dict fuer write_bones."""
    n = len(tabs)
    names = list(char.joints.keys())
    rest = char.joint_frames(char.rest_tab)
    fwd_m, up_m = np.array([0.0, 0.0, 1.0]), np.array([0.0, 1.0, 0.0])
    # Hoehen in der Stehpose (Meter): Huefte und Kopf ueber den Fuessen
    feet = [rest[k][0] for k in ("LeftFoot", "RightFoot") if k in rest]
    fy = min(p[1] for p in feet) if feet else 0.0
    hip_h = (rest["Hips"][0][1] - fy) * 0.01 * oot_pack.S
    head_h = (rest["Head"][0][1] - fy) * 0.01 * oot_pack.S
    head = np.zeros((n, 4), np.float32); J = np.zeros((n, len(names), 12), np.int16)
    q = lambda v: np.clip(np.round(v * 32767.0), -32767, 32767).astype(np.int16)
    for i in range(n):
        jf = char.joint_frames(tabs[i])
        hipw, Ry = to_world(jf["Hips"][0], actor_pos[i], yaw[i], yoff=char.yoff)
        pel = vpos(hipw) + np.asarray(off, float)
        ground = vpos(np.array(actor_pos[i], float))[1] + off[1]
        if hide is not None and hide[i]:
            ground = pel[1] + 1.0                               # Huefte unter "Boden" -> CutsceneTrack blendet aus
        head[i] = [pel[0], pel[1], pel[2], ground]
        for k, nm in enumerate(names):
            p, R = jf[nm]
            Rr = rest[nm][1]
            D = Ry @ R @ Rr.T                                   # Drehung seit der Stehpose (Welt, OoT-Achsen)
            f = vdir(D @ fwd_m); u = vdir(D @ up_m)
            nat = NAT.get(nm)
            if nat and nat in rest:
                seg = rest[nm][0] - rest[nat][0]
            else:
                seg = up_m
            sn = np.linalg.norm(seg)
            l = vdir(D @ (seg / sn if sn > 1e-6 else up_m))
            w, _ = to_world(p, actor_pos[i], yaw[i], yoff=char.yoff)
            rel = np.clip(np.round((vpos(w) - pel) * 1000.0), -32767, 32767).astype(np.int16)
            J[i, k] = np.concatenate([rel, q(f), q(u), q(l)])
    return {"fps": fps, "n": n, "joints": names, "hip_h": float(hip_h), "head_h": float(head_h), "head": head, "J": J}


def write_bones(path, ba):
    with open(path, "wb") as fo:
        fo.write(b"GIB2")
        fo.write(struct.pack("<fiiff", ba["fps"], ba["n"], len(ba["joints"]), ba["hip_h"], ba["head_h"]))
        for hn in ba["joints"]:
            b = hn.encode("ascii"); fo.write(struct.pack("<B", len(b))); fo.write(b)
        for i in range(ba["n"]):
            fo.write(ba["head"][i].astype("<f4").tobytes()); fo.write(ba["J"][i].astype("<i2").tobytes())
    return {"frames": ba["n"], "joints": len(ba["joints"]), "hip_h": round(ba["hip_h"], 2), "head_h": round(ba["head_h"], 2)}


# ---------------- Animationsfolge wie im Spielcode ----------------
class Player:
    """SkelAnime-Nachbau: Loop/Once mit Geschwindigkeit (1 Animationsbild pro Spielbild bei Tempo 1), Morph (Ueberblenden)."""
    def __init__(self, char):
        self.c = char; self.anim = None; self.cur = 0.0; self.speed = 1.0; self.loop = True
        self.morph_from = None; self.morph_w = 1.0; self.morph_step = 0.0; self.last = None; self.name = None

    def play(self, objname, name, speed=1.0, loop=True, morph=0):
        self.morph_from = self.last if (morph and self.last is not None) else None
        self.morph_w = 0.0 if self.morph_from is not None else 1.0
        self.morph_step = 1.0 / abs(morph) if morph else 1.0
        self.anim = self.c.anim(objname, name); self.name = name
        self.cur = 0.0; self.speed = speed; self.loop = loop

    def step(self):
        """ein Spielbild weiter -> (Tabelle, Animation zu Ende?)"""
        tab = self.anim.table(int(self.cur))
        if self.morph_from is not None and self.morph_w < 1.0:
            tab = A.blend_tab(self.morph_from, tab, self.morph_w)
            self.morph_w = min(1.0, self.morph_w + self.morph_step)
        self.last = tab
        end = self.anim.frames - 1
        self.cur += self.speed
        done = False
        if self.loop:
            if self.cur >= self.anim.frames:
                self.cur -= self.anim.frames
        elif self.cur >= end:
            self.cur = end; done = True
        return tab, done


def cue_at(cues, f):
    c = None
    for q in cues:
        if q["start"] <= f < q["end"]:
            c = q
    return c


def nightmare_tabs(cs, n):
    """En_Viewer im Albtraum (Szenen-Ebene 4): Zelda/Impa (Kanal 0), Ganondorf + Pferd (Kanal 1, geteilter sTimer)."""
    ch0 = cs["actors"].get("ch0_2", []); ch1 = cs["actors"].get("ch1_1", [])
    zel, imp, gan = Char("zelda"), Char("impa"), Char("ganondorf")
    pz, pi, pg = Player(zel), Player(imp), Player(gan)
    pz.play("object_opening_demo1", "object_opening_demo1_Anim_000450", 3.0)
    pi.play("object_opening_demo1", "object_opening_demo1_Anim_0029CC", 3.0)
    pg.play("object_gndd", "gYoungGanondorfHorsebackIdleAnim", 1.0)
    tz, ti, tg, hstate = [], [], [], []
    timer, state = 0, 0
    horse = "gHorseGanonRearingAnim"; horse_cur = 0.0; horse_loop = False; horse_speed = 1.0
    for f in range(n):
        c1 = cue_at(ch1, f)
        fin = False
        # Ganondorf (Typ 3) - erst Animation weiterschalten, dann Logik wie EnViewer_UpdateImpl
        tab, fin = pg.step()
        tg.append(tab)
        if c1 is not None:
            cid = c1["id"]
            if cid == 2 and timer == 0:
                if pg.name != "gYoungGanondorfHorsebackIdleAnim":
                    pg.play("object_gndd", "gYoungGanondorfHorsebackIdleAnim", 1.0)
                if horse != "gHorseGanonIdleAnim":
                    horse, horse_cur, horse_loop, horse_speed = "gHorseGanonIdleAnim", 0.0, True, 1.0
            elif cid == 1 and f >= 381:
                if pg.name == "gYoungGanondorfHorsebackRearAnim":
                    pg.play("object_gndd", "gYoungGanondorfHorsebackIdleAnim", 1.0, morph=-5)
            elif cid == 1:
                timer = 100
                if pg.name != "gYoungGanondorfHorsebackRearAnim":
                    pg.play("object_gndd", "gYoungGanondorfHorsebackRearAnim", 1.0)
                if horse != "gHorseGanonRearingAnim":
                    horse, horse_cur, horse_loop, horse_speed = "gHorseGanonRearingAnim", 0.0, True, 1.0
            else:
                if state == 0 and f >= 381 and pg.name == "gYoungGanondorfHorsebackRearAnim":
                    pg.play("object_gndd", "gYoungGanondorfHorsebackIdleAnim", 1.0, morph=-5)   # Pferd ruhig ab 381
                if state == 0 and cid == 4:
                    pg.play("object_gndd", "gYoungGanondorfHorsebackLookSidewaysStartAnim", 1.0, loop=False, morph=-5); state = 1
                elif state == 1 and fin:
                    pg.play("object_gndd", "gYoungGanondorfHorsebackLookSidewaysLoopAnim", 1.0, morph=-5); state = 2
                elif state == 2 and cid == 5:
                    pg.play("object_gndd", "gYoungGanondorfHorsebackMagicChargeUpStartAnim", 1.0, loop=False, morph=-5); state = 3
                elif state == 3 and fin:
                    pg.play("object_gndd", "gYoungGanondorfHorsebackMagicChargeUpLoopAnim", 1.0, morph=-5); state = 4
        # sTimer: Ganondorf und Pferd zaehlen ihn beide herunter
        for _ in range(2):
            if timer > 0:
                timer -= 1
        tz.append(pz.step()[0]); ti.append(pi.step()[0])
        hstate.append((horse, horse_cur))
        horse_cur += horse_speed
    return {"zelda": (zel, tz), "impa": (imp, ti), "ganondorf": (gan, tg), "horse_ganon": hstate}


def nightmare_link_tabs(cs, n):
    """Spieler im Albtraum: Cue 5 -> CSACTION_8 (nwait, Tempo 2/3 * 1,5 = 1 Bild/Bild, Morph 4), Cue 1 -> CSACTION_3 (ein
    Bild Lauf-Logik, Animation laeuft weiter), Cue 6 -> CSACTION_9 (link_demo_furimuki einmal, Tempo 1,5, Morph 6).
    Position/Blickrichtung: bei Cue 5/6 sofort auf startPos/rot.y (func_808529D0), Cue 1 setzt nichts (Abstand 0)."""
    link = Char("link"); pl = Player(link)
    tabs, pos, yaw = [], [], []
    cur_id = None; p = list(cs["player"][0]["from"]); y = cs["player"][0]["rot"][1]
    for f in range(n):
        c = cue_at(cs["player"], f)
        if c is not None and c["id"] != cur_id:
            cid = c["id"]
            if cid in ("PLAYER_CUEID_5", "PLAYER_CUEID_6"):
                p = list(c["from"]); y = c["rot"][1]
            if cid == "PLAYER_CUEID_5":
                pl.play("LINK", "gPlayerAnim_sude_nwait", 1.0, loop=True, morph=-4 if pl.last is not None else 0)
            elif cid == "PLAYER_CUEID_6":
                pl.play("LINK", "gPlayerAnim_link_demo_furimuki", 1.5, loop=False, morph=-6)
            cur_id = cid
        tabs.append(pl.step()[0]); pos.append(list(p)); yaw.append(y)
    return link, tabs, pos, yaw
