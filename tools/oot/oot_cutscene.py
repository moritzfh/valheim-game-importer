"""OoT-Cutscene (Decomp-Quelltext *.csdata.inc.c) lesen und Bild fuer Bild auswerten.

Kamera wird exakt wie im Spiel gerechnet (z_demo.c + z_camera.c Camera_Demo1 + z_cutscene_spline.c):
  - ein Eye-/At-Spline-Paar gilt ab curFrame > startFrame (strikt) und wird beim Wechsel neu gestartet
  - pro Spielbild wird der Fortschritt ZWEIMAL weitergezaehlt (erst mit den Eye-, dann mit den At-Punkten),
    weil beide Aufrufe denselben keyframe/curFrame-Zaehler teilen
Ausgabe in OoT-Einheiten; die Umrechnung nach Valheim macht oot_track.py.

  python oot_cutscene.py <datei.csdata.inc.c> [--json out.json]
"""
import re, sys, json, math

_TOK = re.compile(r"(CS_[A-Z0-9_]+)\(")


def _args(s, i):
    """Argumente ab Position i (nach '(') bis zur passenden ')'; verschachtelte CS_FLOAT(hex, wert) -> wert."""
    depth, cur, out = 0, "", []
    while i < len(s):
        ch = s[i]
        if ch == "(":
            depth += 1; cur += ch
        elif ch == ")":
            if depth == 0:
                out.append(cur.strip()); return out, i + 1
            depth -= 1; cur += ch
        elif ch == "," and depth == 0:
            out.append(cur.strip()); cur = ""
        else:
            cur += ch
        i += 1
    raise ValueError("Klammer offen")


def _val(a):
    a = a.strip()
    m = re.match(r"CS_FLOAT\(\s*([^,]+),\s*([^)]+)\)", a)
    if m:
        a = m.group(2)
    a = a.rstrip("f") if re.match(r"^-?[\d.]+(e-?\d+)?f$", a) else a
    try:
        if a.lower().startswith(("0x", "-0x")):
            return int(a, 16)
        if re.match(r"^-?\d+$", a):
            return int(a)
        return float(a)
    except ValueError:
        return a          # Symbol (z. B. PLAYER_CUEID_5, CS_TRANS_BLACK_FILL_IN)


def _s16(v):
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def parse(text):
    """Liste von (Befehl, [Argumente]) in Skript-Reihenfolge."""
    out, i = [], 0
    while True:
        m = _TOK.search(text, i)
        if not m:
            break
        name = m.group(1)
        args, i = _args(text, m.end())
        if name == "CS_FLOAT":
            continue
        out.append((name, [_val(a) for a in args]))
    return out


def structure(cmds):
    cs = {"frames": 0, "eye": [], "at": [], "player": [], "actors": {}, "transitions": [], "misc": [],
          "seq": [], "destination": None, "text": []}
    cur, cur_list = None, None
    for name, a in cmds:
        if name == "CS_HEADER":
            cs["frames"] = a[1]
        elif name in ("CS_CAM_EYE_SPLINE", "CS_CAM_AT_SPLINE", "CS_CAM_EYE_SPLINE_REL_TO_PLAYER", "CS_CAM_AT_SPLINE_REL_TO_PLAYER"):
            cur = {"start": a[0], "end": a[1], "rel": "REL" in name, "pts": []}
            cs["eye" if "EYE" in name else "at"].append(cur)
            cs.setdefault("order", []).append(("eye" if "EYE" in name else "at", cur))
        elif name == "CS_CAM_POINT":
            flag = a[0]
            stop = (flag == "CS_CAM_STOP") or (isinstance(flag, int) and (flag & 0xFF) == 0xFF)
            roll = a[1] if isinstance(a[1], int) else 0
            if roll > 127:
                roll -= 256
            cur["pts"].append({"stop": stop, "roll": roll, "frame": a[2], "fov": float(a[3]),
                               "pos": (a[4], a[5], a[6])})
        elif name == "CS_PLAYER_CUE_LIST":
            cur_list = cs["player"]
        elif name == "CS_ACTOR_CUE_LIST":
            ch = str(a[0])
            m = re.match(r"CS_CMD_ACTOR_CUE_(\d+)_(\d+)", ch)
            key = "ch%s_%s" % (m.group(1), m.group(2)) if m else ch
            cur_list = cs["actors"].setdefault(key, [])
        elif name in ("CS_ACTOR_CUE", "CS_PLAYER_CUE"):
            cur_list.append({"id": a[0], "start": a[1], "end": a[2], "rot": (_s16(a[3]), _s16(a[4]), _s16(a[5])),
                             "from": (a[6], a[7], a[8]), "to": (a[9], a[10], a[11])})
        elif name == "CS_TRANSITION":
            cs["transitions"].append({"type": a[0], "start": a[1], "end": a[2]})
        elif name == "CS_MISC":
            cs["misc"].append({"type": a[0], "start": a[1], "end": a[2]})
        elif name == "CS_START_SEQ":
            cs["seq"].append({"seq": a[0], "start": a[1]})
        elif name == "CS_DESTINATION":
            cs["destination"] = {"dest": a[0], "start": a[1]}
        elif name in ("CS_TEXT", "CS_TEXT_NONE"):
            cs["text"].append(a)
    return cs


# ---------- Kamera ----------
def _spline_eval(u, p):
    u = min(u, 1.0)
    c0 = (1 - u) ** 3 / 6.0
    c1 = u ** 3 / 2.0 - u * u + 2.0 / 3.0
    c2 = -u ** 3 / 2.0 + u * u / 2.0 + u / 2.0 + 1.0 / 6.0
    c3 = u ** 3 / 6.0
    return [c0 * p[0][k] + c1 * p[1][k] + c2 * p[2][k] + c3 * p[3][k] for k in range(5)]


def _step(pts, st):
    """func_800BB2B4: st = [keyframe, curFrame]; gibt (pos, roll, fov, fertig) zurueck."""
    key, prog = st
    if key < 0:
        prog = 0.0
    n = len(pts)

    def P(i):
        return pts[i] if i < n else pts[-1]
    if P(key)["stop"] or P(key + 1)["stop"] or P(key + 2)["stop"]:
        return None, True
    data = [[P(key + i)["pos"][0], P(key + i)["pos"][1], P(key + i)["pos"][2], P(key + i)["roll"], P(key + i)["fov"]]
            for i in range(4)]
    r = _spline_eval(prog, data)
    sp1 = 1.0 / P(key + 1)["frame"] if P(key + 1)["frame"] else 0.0
    sp2 = 1.0 / P(key + 2)["frame"] if P(key + 2)["frame"] else 0.0
    adv = st[1] * (sp2 - sp1) + sp1
    if adv < 0:
        adv = 0.0
    st[1] += adv
    done = False
    if st[1] >= 1:
        st[0] += 1
        if P(st[0] + 3)["stop"]:
            st[0] = 0
            done = True
        st[1] -= 1
    return r, done


def camera(cs, nframes=None):
    """Kamera fuer jedes Spielbild 0..n-1: dict(eye, at, roll_deg, fov, seg) oder None (keine Cutscene-Kamera)."""
    n = nframes or cs["frames"]
    eye_pts = at_pts = None
    eye_ready = at_ready = False
    eye_applied = at_applied = 0xFFFF
    anim = None            # [keyframe, curFrame] oder None = neu starten
    state_done = False
    seg = -1
    out = []
    last = None
    for f in range(n):
        for kind, sp in cs["order"]:
            if not (f > sp["start"] and f < sp["end"]):
                continue
            if kind == "eye":
                if sp["start"] > eye_applied or eye_applied >= 0xF000:
                    eye_ready = True; eye_pts = sp["pts"]
                    if at_ready:
                        eye_applied = sp["start"]; anim = [0, 0.0]; state_done = False; seg += 1
            else:
                if sp["start"] > at_applied or at_applied >= 0xF000:
                    at_ready = True; at_pts = sp["pts"]
                    if eye_ready:
                        at_applied = sp["start"]; anim = [0, 0.0]; state_done = False; seg += 1
        if anim is not None and not state_done:
            e, d1 = _step(eye_pts, anim)
            a = None
            if not d1:
                a, d2 = _step(at_pts, anim)
            else:
                d2 = False
            if d1 or d2:
                state_done = True
            if e is not None and a is not None:
                # Camera_Demo1: roll/fov kommen vom letzten Aufruf (At), Rolle = newRoll*256 (Binaerwinkel)
                last = {"eye": e[:3], "at": a[:3], "roll": a[3] * 256 * 360.0 / 65536.0, "fov": a[4], "seg": seg}
        out.append(dict(last) if last else None)
    return out


def cuts(cam):
    """Bildnummern, an denen ein neues Spline-Paar startet (= harter Schnitt)."""
    res, prev = [], None
    for i, c in enumerate(cam):
        if c and c["seg"] != prev:
            res.append(i); prev = c["seg"]
    return res


def cue_at(cues, f):
    """aktueller Cue (wie csCtx.actorCues[n]): der, dessen [start, end) f enthaelt."""
    for c in cues:
        if c["start"] <= f < c["end"]:
            return c
    return None


def lerp_pos(c, f):
    w = 0.0 if c["end"] == c["start"] else (f - c["start"]) / float(c["end"] - c["start"])
    w = max(0.0, min(1.0, w))
    return [c["from"][k] + (c["to"][k] - c["from"][k]) * w for k in range(3)]


if __name__ == "__main__":
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("src"); ap.add_argument("--json")
    a = ap.parse_args()
    cs = structure(parse(open(a.src).read()))
    end = cs["destination"]["start"] if cs["destination"] else cs["frames"]
    cam = camera(cs, end + 1)
    print("Bilder:", end, "Schnitte:", cuts(cam))
    for f in cuts(cam):
        c = cam[f]
        print(f, [round(x) for x in c["eye"]], [round(x) for x in c["at"]], round(c["fov"], 1), round(c["roll"], 1))
    if a.json:
        json.dump({"cs": {k: v for k, v in cs.items() if k != "order"}, "cam": cam}, open(a.json, "w"))
