"""Intro vor/nach dem Albtraum: Links Haus (Schlaf, Erzaehler-Text des Deku-Baums).

Zeitachse = Spielbilder des Emulators (Bild e). Die Cutscene haelt an CS_TEXT-Enden an, bis der Text zu ist
(Halte-Punkte aus der Original-Aufnahme bestimmt), Figuren laufen dabei weiter.
"""
import os, json, math
import numpy as np
import oot_pack, oot_cutscene, oot_bones, oot_linkhome

S = oot_pack.S
OFF_LH = (-1500.0, 170.0, -1500.0)          # Links Haus: weit weg vom Feld, hoch in der Luft (Innenraum)
CSDIR = "/extracted/gc-eu/assets/scenes/indoors/link_home/"

# Halte-Punkte (cs-Bild, Spielbild an dem es weitergeht) und Textseiten (Spielbild von, bis, Text) aus der Aufnahme
SLEEP_START = 2                              # cs-Bild 0 = Spielbild 2
SLEEP_HOLDS = [(50, 268), (120, 552), (165, 715)]
SLEEP_SPANS = [(42, 120), (121, 268), (324, 444), (445, 552), (598, 715)]     # Spielbild von/bis je Textseite (aus der Aufnahme)
SLEEP_MSGS = (0x109D, 0x109E, 0x109F)                                         # Erzaehler (Texte kommen aus der eigenen ROM)


def _msg_pages(mids):
    """Textseiten der Nachrichten aus der eigenen ROM (message_data.h der Decomp), ohne Farb-Tags, Zeilen als \\N."""
    import re, oot_text
    out = []
    for m in mids:
        out += [re.sub(r"<[^>]+>", "", t).replace("\n", "\\N") for t in oot_text.pages(m)["pages"]]
    return out


def sleep_pages():
    return [(a, b, t) for (a, b), t in zip(SLEEP_SPANS, _msg_pages(SLEEP_MSGS))]
SLEEP_FRAMES = 850
SLEEP_CSCAM = 134                            # Schnitt aufs Bett (Spielbild 566 in der Aufnahme)


def cs_timeline(n, start, holds):
    """Spielbild -> cs-Bild"""
    out = []; c = -start; hi = 0
    for e in range(n):
        out.append(max(0, c))
        if hi < len(holds) and c >= holds[hi][0]:
            if e >= holds[hi][1]:
                hi += 1; c += 1
        else:
            c += 1
    return out


def V(p, off):
    v = oot_pack.V(p); return [round(v[i] + off[i], 3) for i in range(3)]


def link_sleep_tabs(cs, tl):
    link = oot_bones.Char("link_raw"); pl = oot_bones.Player(link)
    tabs = []; cur = None
    for e, c in enumerate(tl):
        q = oot_bones.cue_at(cs["player"], c)
        cid = q["id"] if q else cur
        if cid != cur:
            if cid == "PLAYER_CUEID_28":
                pl.play("LINK", "gPlayerAnim_clink_op3_wait1", 1.5, loop=True)
            elif cid == "PLAYER_CUEID_29":
                pl.play("LINK", "gPlayerAnim_clink_op3_negaeri", 1.5, loop=False)
            cur = cid
        tab, done = pl.step()
        if cur == "PLAYER_CUEID_29" and done and pl.name != "gPlayerAnim_clink_op3_wait2":
            pl.play("LINK", "gPlayerAnim_clink_op3_wait2", 1.5, loop=True)
        tabs.append(tab)
    return link, tabs


def ass_time(e, fps):
    t = e / fps
    return "%d:%02d:%05.2f" % (int(t // 3600), int(t % 3600 // 60), t % 60)


BOX_X, BOX_Y, BOX_W, BOX_H, BOX_R = 104, 333, 766, 171, 14
BOX_SHAPE = ("m {r} 0 l {a} 0 b {w} 0 {w} 0 {w} {r} l {w} {b} b {w} {h} {w} {h} {a} {h} l {r} {h} b 0 {h} 0 {h} 0 {b} l 0 {r} b 0 0 0 0 {r} 0"
             .format(r=BOX_R, a=BOX_W - BOX_R, b=BOX_H - BOX_R, w=BOX_W, h=BOX_H))


def write_ass(path, pages, fps=16.56, w=960, h=540, style="oot"):
    head = ("[Script Info]\nScriptType: v4.00+\nPlayResX: %d\nPlayResY: %d\n\n[V4+ Styles]\n"
            "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Italic, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV\n"
            "Style: oot,Arial,24,&H00FFFFFF,&H00000000,&H00000000,1,0,1,1.5,0,2,10,10,42\n"
            "Style: box,Arial,46,&H00FFFFFF,&H00000000,&H00000000,0,0,1,0.6,0,7,10,10,10\n"
            "Style: panel,Arial,20,&H50000000,&H00000000,&H00000000,0,0,1,0,0,7,0,0,0\n"
            "Style: mid,Arial,22,&H00101010,&H00000000,&H00000000,1,0,1,0,0,5,10,10,10\n\n[Events]\n"
            "Format: Layer, Start, End, Style, Text\n") % (w, h)
    with open(path, "w") as f:
        f.write(head)
        for a, b, txt in pages:
            stl = style(a) if callable(style) else style
            if stl == "box":
                # wie im Original: eine feste dunkle Textbox, der Text erscheint darin (nicht je Zeile ein eigener Kasten)
                f.write("Dialogue: 0,%s,%s,panel,{\\an7\\pos(%d,%d)\\p1}%s{\\p0}\n" % (ass_time(a, fps), ass_time(b, fps), BOX_X, BOX_Y, BOX_SHAPE))
            # wie im Spiel: Buchstabe fuer Buchstabe (ca. 1 Zeichen pro Spielbild), danach stehen bis Seitenende
            parts = txt.split("\\N"); n = sum(len(x) for x in parts)
            for k in range(1, n + 1):
                st = a + k - 1; en = (a + k) if k < n else b
                if st >= b:
                    break
                left = k; shown = []
                for x in parts:
                    shown.append(x[:max(0, left)]); left -= len(x)
                vis = "\\N".join(shown)
                pre = "{\\an7\\pos(%d,%d)}" % (BOX_X + 60, BOX_Y + 18) if stl == "box" else ""
                f.write("Dialogue: 1,%s,%s,%s,%s%s\n" % (ass_time(st, fps), ass_time(min(en, b), fps), stl, pre, vis))


def build_sleep(root, out):
    csp = root + CSDIR + "gLinkHouseIntroSleepCs.csdata.inc.c"
    cs = oot_cutscene.structure(oot_cutscene.parse(open(csp).read()))
    n = SLEEP_FRAMES
    tl = cs_timeline(n, SLEEP_START, SLEEP_HOLDS)
    cam = oot_cutscene.camera(cs, cs["frames"])
    first = next(i for i, c in enumerate(cam) if c)
    pos, rot, fov = [], [], []
    fwd, _, _ = oot_linkhome.cam_basis()
    bg = {"eye": list(oot_linkhome.CAM_EYE), "at": list(oot_linkhome.CAM_EYE + fwd * 100.0), "roll": 0.0, "fov": oot_linkhome.CAM_FOV}
    camf = []
    for e in range(n):
        c = cam[min(max(tl[e], first), len(cam) - 1)] or cam[first]
        if tl[e] < SLEEP_CSCAM:
            c = bg                                    # im Original: festes vorgerendertes Bild bis zum Schnitt
        camf.append(c)
        ey, at = c["eye"], c["at"]
        up = oot_pack.oot_up(ey, at, c["roll"])
        pos += V(ey, OFF_LH)
        rot += oot_pack.quat_look(oot_pack.vdir(np.array(at) - np.array(ey)), oot_pack.vdir(up))
        fov.append(round(c["fov"], 3))
    fps = 20.0
    # Blenden nach cs-Bild -> Spielbild
    def e_of(csf):
        return next((e for e in range(n) if tl[e] >= csf), n - 1)
    fades = [{"t": 0.0, "in": False, "dur": 0.05},
             {"t": round(e_of(55) / fps, 3), "in": True, "dur": round((e_of(81) - e_of(55)) / fps, 3)},
             {"t": round(e_of(230) / fps, 3), "in": False, "dur": round((e_of(301) - e_of(230)) / fps, 3)}]
    # Schnitte (Kamera-Spruenge)
    tr = {"fps": fps, "dur": round(n / fps, 3), "fov_kind": "v", "time": 0.5, "hide_player": True,
          "cam": {"pos": pos, "rot": rot, "fov": fov}, "cuts": [0] + [e for e in range(1, n) if (tl[e] >= SLEEP_CSCAM) != (tl[e - 1] >= SLEEP_CSCAM)],
          "fades": fades,
          "lines": [{"t": round(a / fps, 3), "speaker": "", "text": t.replace("\\N", "\n"), "dur": round((b - a) / fps, 3)} for a, b, t in sleep_pages()],
          "env": {"weather": "Clear", "ambient": [0.33, 0.27, 0.2], "fog_color": [0.08, 0.06, 0.04], "fog": 0.3, "sun": 0.15, "motes": 0.35,
                  "lights": [{"pos": V((0, 100, 30), OFF_LH), "color": [1.0, 0.82, 0.6], "range": 8, "intensity": 0.55},
                             {"pos": V((0, 70, 112), OFF_LH), "color": [0.8, 0.95, 0.9], "range": 3.0, "intensity": 0.3}]}}
    # Link
    link, tabs = link_sleep_tabs(cs, tl)
    lp = cs["player"][0]["from"]; ly = cs["player"][0]["rot"][1]
    ap = [list(lp)] * n
    ba = oot_bones.bones(link, tabs, ap, [ly] * n, fps, off=OFF_LH)
    os.makedirs(os.path.join(out, "cutscenes"), exist_ok=True)
    rep = {"link": oot_bones.write_bones(os.path.join(out, "cutscenes", "intro_sleep.link.bones"), ba)}
    vp = V(lp, OFF_LH)
    # Link: Original-Kind-Modell aus dem Spiel, Augen zu (schlaeft), Wurzel unveraendert (clink_op3_*: wie im Bett)
    import oot_link
    tr["actors"] = {}
    rep["link_cs"] = oot_link.export_cs(os.path.join(out, "cutscenes", "intro_sleep_link.glb"), tabs, fps, root_scale=link.root_scale,
                                        eyes="gLinkChildEyesClosedTex")
    tr["props"] = {"link": oot_link.cs_prop("intro_sleep_link.glb", vp * n, [ly] * n, n)}
    json.dump(tr, open(os.path.join(out, "cutscenes", "intro_sleep.json"), "w"), separators=(",", ":"))
    write_ass(os.path.join(out, "cutscenes", "intro_sleep.ass"), sleep_pages())
    # Raum
    st, rr = oot_linkhome.build(root, out, OFF_LH)
    json.dump(st, open(os.path.join(out, "statics_linkhome.json"), "w"), separators=(",", ":"))
    rep["room"] = rr
    # Aufnahme-Einstellung
    os.makedirs(os.path.join(out, "shots"), exist_ok=True)
    json.dump({"cutscene": "intro_sleep", "from": 0.0, "dur": round(n / fps, 2), "fps": 20, "grass": False,
               "player_at": V((0, 0, 300), OFF_LH)}, open(os.path.join(out, "shots", "intro_sleep.json"), "w"), indent=1)
    return rep, tl


if __name__ == "__main__":
    import sys
    rep, tl = build_sleep(sys.argv[1], sys.argv[2])
    print(json.dumps(rep), tl[::50])


# ---------------- Kokiri-Wald: Deku-Baum spricht, Navi fliegt los ----------------
import oot_kf
KF_START = 12                                    # cs-Bild 0 = Spielbild 12
KF_HOLDS = [(60, 184), (150, 1501)]
KF_FRAMES = 2300
KF_DEPART = 150                                  # cs-Bild: Navi fliegt los (Ende der Text-Haltezeit)
KF_BOX = [249, 371, 498, 637, 813, 934, 1069, 1192, 1296, 1391, 1501]


def kf_pages():
    """Deku-Baum ruft Navi (0x1099) und spricht (0x109A) - Texte aus der eigenen ROM."""
    first, rest = _msg_pages([0x1099]), _msg_pages([0x109A])
    out = [(62, 184, first[0])]
    for i, t in enumerate(rest):
        out.append((KF_BOX[i], KF_BOX[i + 1] - 1, t))
    return out


# Kokiri-Kinder aus der Akteur-Liste von spot04 Raum 0, Kopf 6 (= Szenen-Kopf mit gKokiriForestIntroNaviFlyingCs)
# (pos, rot.y, Typ): Typ 1/5/6 = Maedchen (z_en_ko.c), Mido = En_Md
KF_KIDS = [((-340, 0, -414), 0x0000, 0), ((62, 0, -258), 0x0666, 1), ((-641, 120, 454), 0x749F, 2), ((-1472, -80, -294), 0x4000, 3),
           ((669, 0, 521), -0x78E4, 4), ((853, 102, -311), 0x0000, 5), ((-638, 1, -165), 0x62D8, 6), ((1522, 0, 105), -0x4444, "mido")]
KF_GIRLS = (1, 5, 6)
KO_ANIM = {0: ("gKokiriLiftingRockAnim", 1.0, None), 1: ("gKokiriStandUpAnim", 0.0, 0), 2: ("gKokiriPunchingAnim", 1.0, None),
           3: ("gKokiriBlockingAnim", 0.0, 1), 4: ("gKokiriCuttingGrassAnim", 1.0, None), 5: ("gKokiriStandUpAnim", 0.0, 1),
           6: ("gKokiriStandUpAnim", 0.0, 2), "mido": ("gKokiriStandingHandsOnHipsAnim", 1.0, None)}


def kf_kokiri(out, n, fps):
    """Kinder stehen mit Kind-Links Ruhe-Animation (eigene Kokiri-Skelette folgen spaeter), versetzt im Takt."""
    cast_p = os.path.join(out, "cast.json")
    cast = json.load(open(cast_p)) if os.path.exists(cast_p) else {}
    acts = {}
    for i, (p, ry, typ) in enumerate(KF_KIDS):
        cid = "mido" if typ == "mido" else "kokiri%d" % i
        girl = typ in KF_GIRLS
        if girl:
            look = {"model": "female", "hair": "Hair18", "skin": [0.96, 0.82, 0.72], "hair_color": [0.8, 0.6, 0.3],
                    "chest": "ArmorTunic7", "chest_hue": 105, "chest_sat": 0.8, "chest_val": 0.75, "legs": "", "helmet": "", "shoulder": ""}
        else:
            look = {"model": "male", "hair": "Hair3", "skin": [0.95, 0.8, 0.7], "hair_color": [0.88, 0.62, 0.25] if typ != "mido" else [0.9, 0.42, 0.12],
                    "chest": "ArmorTunic7", "chest_hue": 130, "chest_sat": 0.9, "chest_val": 0.5, "legs": "ArmorLeatherLegs", "helmet": "", "shoulder": ""}
        cast[cid] = look
        # Original-Animation je Kokiri-Typ (z_en_ko.c, Spalte 0 = Kindheit vor dem Deku-Baum): (Animation, Tempo, festes Bild)
        anim, spd, hold = KO_ANIM.get(typ, ("gKokiriStandingHandsOnHipsAnim", 1.0, None))
        link = oot_bones.Char("kokiri_girl" if girl else "kokiri_boy"); pl = oot_bones.Player(link)
        pl.play("object_os_anime", anim, spd, loop=True)
        if hold is not None:
            tab0 = pl.anim.table(hold); tabs = [tab0] * n
        else:
            for _ in range(i * 7):
                pl.step()
            tabs = [pl.step()[0] for _ in range(n)]
        ba = oot_bones.bones(link, tabs, [list(p)] * n, [ry] * n, fps, off=oot_kf.OFF_KF)
        oot_bones.write_bones(os.path.join(out, "cutscenes", "intro_kf.%s.bones" % cid), ba)
        acts[cid] = {"step": 1, "pos": oot_kf.V(p) * n, "yaw": [180.0] * n, "pose": [0] * n,
                     "bones": "intro_kf.%s.bones" % cid, "scale": 1.0, "head_scale": 1.25}
    json.dump(cast, open(cast_p, "w"), indent=1)
    return acts


def build_kf(root, out):
    csp = root + "/extracted/gc-eu/assets/scenes/overworld/spot04/gKokiriForestIntroNaviFlyingCs.csdata.inc.c"
    cs = oot_cutscene.structure(oot_cutscene.parse(open(csp).read()))
    n = KF_FRAMES
    tl = cs_timeline(n, KF_START, KF_HOLDS)
    cam = oot_cutscene.camera(cs, cs["frames"])
    first = next(i for i, c in enumerate(cam) if c)
    pos, rot, fov, ats = [], [], [], []
    for e in range(n):
        c = cam[min(max(tl[e], first), len(cam) - 1)] or cam[first]
        ey, at = c["eye"], c["at"]
        up = oot_pack.oot_up(ey, at, c["roll"])
        pos += oot_kf.V(ey); ats.append(at)
        rot += oot_pack.quat_look(oot_pack.vdir(np.array(at) - np.array(ey)), oot_pack.vdir(up))
        fov.append(round(c["fov"], 3))
    fps = 20.0

    def e_of(csf):
        return next((e for e in range(n) if tl[e] >= csf), n - 1)
    grey = [0.627, 0.627, 0.627]
    fades = [{"t": 0.0, "in": False, "dur": 0.05, "col": grey},
             {"t": round(178 / fps, 3), "in": True, "dur": 0.3, "col": grey},
             {"t": round(e_of(195) / fps, 3), "in": False, "dur": round((e_of(198) - e_of(195)) / fps, 3), "col": [1, 1, 1]},
             {"t": round(e_of(200) / fps, 3), "in": True, "dur": round((e_of(216) - e_of(200)) / fps, 3), "col": [1, 1, 1]},
             {"t": round(e_of(925) / fps, 3), "in": False, "dur": round((e_of(940) - e_of(925)) / fps, 3)}]
    pages = kf_pages()
    tr = {"fps": fps, "dur": round(n / fps, 3), "fov_kind": "v", "time": 0.5, "hide_player": True,
          "cam": {"pos": pos, "rot": rot, "fov": fov},
          "cuts": [0] + [e for e in range(1, n) if np.linalg.norm(np.array(pos[e * 3:e * 3 + 3]) - np.array(pos[e * 3 - 3:e * 3])) > 3.0],
          "fades": fades,
          "lines": [{"t": round(a / fps, 3), "speaker": "", "text": t.replace("\\N", "\n")} for a, b, t in pages],
          "env": {"weather": "Clear", "ambient": [0.4, 0.47, 0.46], "fog_color": [0.42, 0.56, 0.54], "fog": 1.8, "sun": 0.75, "motes": 1.0}}      # Remaster: sonnig, gruener Dunst, Gluehwuermchen
    # Navi: Cue-Weg (En_Elf, Kanal 8), danach dorthin, wo die Kamera hinschaut
    navi, nvis = [], []
    nose, dep = None, None
    cues = cs["actors"].get("ch8_0") or next((v for k, v in cs["actors"].items() if k.startswith("ch8")), [])
    for e in range(n):
        c = oot_cutscene.cue_at(cues, tl[e])
        vis = 1
        if tl[e] <= KF_DEPART:
            # Dialog: wie im Original direkt vor der Nasenspitze (Wunsch), Abstand wie Cue-Start
            ey = np.array(cam[min(max(tl[e], first), len(cam) - 1)]["eye"]); at = np.array(ats[e])
            fw = at - ey; fw /= (np.linalg.norm(fw) or 1.0)
            dd = float(np.dot(np.array(cues[0]["from"], float) - ey, fw)) if cues else 110.0
            upv = np.array(oot_pack.oot_up(list(ey), list(at), 0.0), float); upv -= fw * np.dot(upv, fw); upv /= (np.linalg.norm(upv) or 1.0)
            p = list(ey + fw * dd - upv * dd * 0.01)
            nose = np.array(p)
        elif c is not None:
            p = oot_cutscene.lerp_pos(c, tl[e]); p = [p[0], p[1] + 30, p[2]]
            if nose is not None:           # sanft vom Nasen-Platz auf den Cue-Weg
                if dep is None:
                    dep = nose - np.array(p)
                w = max(0.0, 1.0 - (tl[e] - KF_DEPART) / 25.0); p = list(np.array(p) + dep * w)
        elif tl[e] < (cues[0]["start"] if cues else 0):
            # vor dem Abflug: wie im Original knapp ueber der Bildmitte vor dem Deku-Baum (Abstand wie Cue-Start)
            ey = np.array(cam[min(max(tl[e], first), len(cam) - 1)]["eye"]); at = np.array(ats[e])
            fw = at - ey; fw /= (np.linalg.norm(fw) or 1.0)
            dd = float(np.dot(np.array(cues[0]["from"], float) - ey, fw)) if cues else 110.0
            upv = np.array(oot_pack.oot_up(list(ey), list(at), 0.0), float); upv -= fw * np.dot(upv, fw); upv /= (np.linalg.norm(upv) or 1.0)
            p = list(ey + fw * dd - upv * dd * 0.01)      # Wunsch: direkt vor der Nasenspitze
        elif cues and tl[e] < max(q["end"] for q in cues) + 1:
            p = list(cues[-1]["to"]); p[1] += 30
        else:
            vis = 0       # Durchflug: Kamera = Navis Blick, sie selbst ist nicht im Bild
            ey = np.array(cam[min(max(tl[e], first), len(cam) - 1)]["eye"]); at = np.array(ats[e])
            d = at - ey; L = np.linalg.norm(d) or 1.0
            p = list(ey + d / L * min(L, 120.0))
        navi += [round(v, 3) for v in oot_kf.V(p)]; nvis.append(vis)
    tr["props"] = {"navi": {"prefab": "@navi", "glow": 0.8, "color": [0.78, 0.88, 1.0], "scale": 1.0, "pos": navi, "fwd": [0.0, 0.0, 1.0] * n,
                            "up": [0.0, 1.0, 0.0] * n, "vis": nvis}}
    os.makedirs(os.path.join(out, "cutscenes"), exist_ok=True)
    # Moritz 09.11.: die Kokiri stehen als echte OoT-Figuren in der Welt (OotNpcs) - die Valheim-Doubles der Cutscene standen
    # zusaetzlich daneben. Darum hier keine Darsteller mehr (kf_kokiri bleibt fuer die alte Valheim-Figuren-Fassung).
    tr["actors"] = kf_kokiri(out, n, fps) if os.environ.get("GI_KF_INTRO_VALHEIM_KIDS") == "1" else {}
    json.dump(tr, open(os.path.join(out, "cutscenes", "intro_kf.json"), "w"), separators=(",", ":"))
    write_ass(os.path.join(out, "cutscenes", "intro_kf.ass"), pages, style=lambda a: "box" if a > 200 else "mid")
    c0 = cam[first]["eye"]
    os.makedirs(os.path.join(out, "shots"), exist_ok=True)
    json.dump({"cutscene": "intro_kf", "from": 0.0, "dur": round(n / fps, 2), "fps": 20, "grass": True,
               "player_at": oot_kf.V((c0[0], c0[1], c0[2]))}, open(os.path.join(out, "shots", "intro_kf.json"), "w"), indent=1)
    return {"frames": n, "pages": len(pages), "navi_cues": len(cues), "cs_end": tl[-1]}, tl


# ---------------- Links Haus: Navi weckt Link (gLinkHouseIntroWakeUpCs), im Spiel als Ablauf (src/OotWake.cs) ----------------
WAKE_CLIPS = {"PLAYER_CUEID_28": "wait1", "PLAYER_CUEID_29": "negaeri", "PLAYER_CUEID_30": "okiagari", "PLAYER_CUEID_31": "tatiagari"}
WAKE_TEXT_SFX = {0x1095: "NA_SE_VO_NA_HELLO_3", 0x1096: "NA_SE_VO_NA_HELLO_2"}
WAKE_TEXT_TOP = {0x1095, 0x1096}                      # TEXTBOX_POS_TOP
WAKE_SFX = {"NA_SE_EV_FAIRY_DASH": 1, "NA_SE_VO_LI_GROAN": 1, "NA_SE_VO_LI_RELAX": 1, "NA_SE_PL_SLIPDOWN": 1,
            "NA_SE_PL_LAND_LADDER": 1, "NA_SE_PL_WALK_LADDER": 1}


def wake_cfg(root):
    """pack.json-Block "oot_wake": alles in OoT-Einheiten (Raum link_home), "origin" + "s" rechnen nach Valheim um."""
    import oot_text
    csp = root + CSDIR + "gLinkHouseIntroWakeUpCs.csdata.inc.c"
    src = open(csp).read()
    cs = oot_cutscene.structure(oot_cutscene.parse(src))
    eye = cs["eye"][0]["pts"][0]["pos"]
    ats = [(sp["start"], sp["pts"][0]["pos"]) for sp in cs["at"]]
    fov = cs["eye"][0]["pts"][0]["fov"]
    player = []
    for q in cs["player"]:
        c = WAKE_CLIPS.get(q["id"])
        if c and not (player and player[-1][2] == c):
            player.append([q["start"], q["end"], c])
    lp = cs["player"][0]["from"]; ly = cs["player"][0]["rot"][1]
    navi = [[q["id"], q["start"], q["end"], list(q["from"]), list(q["to"])] for q in cs["actors"]["ch8_0"]]
    texts = []
    for t in cs["text"]:
        if len(t) < 3:
            continue
        mid, a, b = t[0], t[1], t[2]
        texts.append({"id": "0x%04X" % mid, "start": a, "end": b, "pages": oot_text.pages(mid)["pages"],
                      "sfx": WAKE_TEXT_SFX.get(mid, ""), "top": mid in WAKE_TEXT_TOP})
    # Ende: PLAYER_CUEID_5 (Link steht vor dem Bett, Blick zur Tuer), dann Spielsteuerung (CS_MISC_STOP_CUTSCENE)
    import re
    m = re.search(r"CS_PLAYER_CUE\(PLAYER_CUEID_5, (\d+), (\d+), 0x[0-9A-F]+, (0x[0-9A-F]+), 0x[0-9A-F]+, (-?\d+), (-?\d+), (-?\d+)", src)
    end_f = int(m.group(1)); end_yaw = int(m.group(3), 16); end_p = [int(m.group(4)), int(m.group(5)), int(m.group(6))]
    stop = next(x["start"] for x in cs["misc"] if x["type"] == "CS_MISC_STOP_CUTSCENE")
    org = V((0, 0, 0), OFF_LH)
    import oot_link
    lens = {k: oot_link.A.LinkAnim("gPlayerAnim_clink_op3_" + k, 21).frames for k in ("negaeri", "okiagari", "tatiagari")}
    return {"flag": "oot_awake", "len": lens, "origin": org, "s": S, "eye": list(eye), "at": [[f, list(p)] for f, p in ats], "fov": fov,
            "link": list(lp), "link_yaw": ly, "player": player, "navi": navi, "texts": texts,
            "end": end_p, "end_yaw": end_yaw - 0x10000 if end_yaw >= 0x8000 else end_yaw, "end_frame": end_f, "stop": stop,
            # Original-Geraeusche: Navi saust zur Tuer herein (cs-Bild 55), Link stoehnt beim Umdrehen,
            # Aufsetzen: gaehnen (Bild 35), rutscht (236/256), Aufstehen: landet auf dem Holzboden (67), Schritte (84/90/96)
            "sfx": {"dash_frame": 55, "negaeri": [[0, "NA_SE_VO_LI_GROAN"]],
                    "okiagari": [[35, "NA_SE_VO_LI_RELAX"], [236, "NA_SE_PL_SLIPDOWN"], [256, "NA_SE_PL_SLIPDOWN"]],
                    "tatiagari": [[67, "NA_SE_PL_LAND_LADDER"], [84, "NA_SE_PL_WALK_LADDER"], [90, "NA_SE_PL_WALK_LADDER"], [96, "NA_SE_PL_WALK_LADDER"]]}}
