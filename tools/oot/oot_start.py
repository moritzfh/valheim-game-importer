"""Startbildschirm + Intro im Spiel (src/OotTitle.cs): Konfiguration, Logo, Musik und die Spur-Einstellungen der Intro-Cutscenes.

Ablauf im Spiel wie OoT: Titel-Cutscene (Link auf Epona) in Schleife + Logo + PRESS START -> Taste -> neues Spiel: Schlaf ->
Albtraum -> Kokiri-Wald -> Aufwachen (OotWake); sonst weiter, wo man war.

Tempo: die Intro-Spuren sind in Spielbildern der PAL-Fassung gebaut (Emulator-Mitschnitt, 16,56 Bilder/s gemessen), laufen
also mit rate = 16,56/20, damit Original-Tonspuren (eigener Mitschnitt) synchron bleiben.
Tonspuren (optional, eigener Emulator-Mitschnitt mit der eigenen ROM): cutscenes/nightmare_audio.ogg (ab Spielbild 1 = cs 0),
cutscenes/intro_kf_audio.ogg (ab Spielbild 0). Fehlen sie, laufen die Szenen stumm.
Titelmusik: music/title_intro.ogg + music/title_loop.ogg (Vorspiel, dann nahtlose Schleife); im sauberen Weg aus Sequenz 30
(NA_BGM_TITLE) synthetisiert, auf dem Test-PC aus dem Emulator-Mitschnitt geschnitten.
"""
import json
import os

PAL_FPS = 16.56
RATE = round(PAL_FPS / 20.0, 4)
INTRO = ["intro_sleep", "nightmare", "intro_kf"]


def config():
    return {"cutscene": "title", "logo": "player/hud/title_logo.png", "press": "player/hud/title_press.png",
            "music": ["music/title_intro.ogg", "music/title_loop.ogg"],
            "logo_at": round(250 / PAL_FPS, 2), "press_at": round(450 / PAL_FPS, 2),      # wie im Startbildschirm-Video
            "intro": INTRO, "new_flag": "oot_awake"}


def tracks(cs_dir):
    """Spur-Einstellungen fuer Startbildschirm und Intro (in die vorhandenen JSON-Dateien schreiben)."""
    want = {
        "title": {"loop": True, "follow": True, "rate": RATE, "bars": False},
        "nightmare": {"rate": RATE, "follow": True, "audio": [{"t": 0.0, "file": "nightmare_audio.ogg", "kind": "music"}],
                      "grass": False, "cam_light": 0.4},                       # wie die Aufnahme fuers Video: Nacht, kein Valheim-Gras
        "intro_sleep": {"rate": RATE, "grass": False},
        "intro_kf": {"rate": RATE, "follow": True, "audio": [{"t": 0.0, "file": "intro_kf_audio.ogg", "kind": "music"}]},
    }
    done = {}
    for nm, kv in want.items():
        p = os.path.join(cs_dir, nm + ".json")
        if not os.path.exists(p):
            continue
        tr = json.load(open(p))
        tr.update(kv)
        if nm == "nightmare" and "navi" in tr.get("props", {}):
            tr["props"]["navi"]["light"] = 0.3                                 # Wisp-Licht ueberstrahlt sonst Links Gesicht
        json.dump(tr, open(p, "w"), separators=(",", ":"))
        done[nm] = sorted(kv)
    return done


def logo(out):
    import oot_logo
    d = os.path.join(out, "player", "hud"); os.makedirs(d, exist_ok=True)
    oot_logo.make(d)
    return [f for f in ("title_logo.png", "title_press.png") if os.path.exists(os.path.join(d, f))]


def music_synth(out, pre=5.0):
    """Titelmusik aus der ROM (Sequenz 30): Vorspiel bis kurz nach Schleifenanfang, dann genau eine Schleife (nahtlos)."""
    import numpy as np
    import oot_sfx
    import oot_build
    jumps = []
    x, _ = oot_sfx.render_seq(30, max_s=200.0, jumps=jumps, stop_on_loop=True)
    by = {}
    for t, tgt in jumps:
        by.setdefault(tgt, []).append(t)
    ts = max((v for v in by.values() if len(v) >= 2), key=lambda v: v[1] - v[0])
    L = ts[1] - ts[0]; start = ts[0] - L                      # Schleifenanfang im ersten Durchlauf
    R = oot_sfx.RATE
    a = int(round((start + pre) * R)); b = a + int(round(L * R))
    d = os.path.join(out, "music"); os.makedirs(d, exist_ok=True)
    hall = oot_build._hall(np.concatenate([x[:a], x[a:b]]))     # gleicher Hall/Pegel fuer beide Teile
    oot_sfx._ogg(os.path.join(d, "title_intro.ogg"), hall[:a])
    oot_sfx._ogg(os.path.join(d, "title_loop.ogg"), hall[a:])
    return {"intro_s": round(a / R, 2), "loop_s": round(L, 2)}
