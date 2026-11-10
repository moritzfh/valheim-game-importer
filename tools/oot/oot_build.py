"""Sauberer Weg: das ganze OoT-Paket (packs/oot) aus der EIGENEN ROM bauen.

Die Mod (src/, GameImporter.dll) enthaelt keine Nintendo-Daten. Alles, was aus dem Spiel stammt (Modelle, Texturen, Animationen,
Sounds, Musik, Texte, Kollision, Cutscene-Kameras ...), erzeugt dieses Skript beim Spieler aus seiner ROM:

  1. zeldaret/oot (Decomp, CC0) an einem festen Stand holen, ROM hineinlegen, entpacken + Szenen/Audio/Texte extrahieren
     (tools/oot/oot_setup_decomp.sh)
  2. Valheim-Texturen (Rinde, Gelaende, Mistlands-Holz ...) aus der eigenen Valheim-Installation: im Spiel einmal
     "statics dumpvtex" (src/Glb.cs) -> config/GameImporter/vtex|vmat
  3. python3 oot_build.py <decomp> <out> [--vtex DIR] [--vmat DIR]  -> fertiges Paket in <out>

Eigene Werte (Lichter, Stimmung, Teleport-Punkte, Kamera-Wuensche ...) stehen hier im Code; sie stammen nicht aus dem Spiel.

Abgleich mit dem Test-PC: python3 oot_pack_diff.py <out>/pack.json <pc>/pack.json --files <out> <pc_dateiliste.json>
"""
import argparse
import json
import os
import shutil
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# Sounds, die das Spiel benutzt (Name -> Varianten); gerendert aus Sequenz 0 + Soundfonts der ROM (oot_sfx)
SFX = {
    # Kokiri-Wald, Link, Menue (oot_sfx.KF_SFX) kommen schon mit dem Grundpaket
    "NA_SE_SY_LOCK_ON_HUMAN": 1, "NA_SE_SY_HITPOINT_ALARM": 1, "NA_SE_SY_DECIDE": 1, "NA_SE_SY_ERROR": 1,
    "NA_SE_PL_DAMAGE": 1, "NA_SE_VO_LI_DOWN_KID": 1, "NA_SE_IT_SHIELD_BOUND": 1, "NA_SE_IT_SWORD_STRIKE": 1,
    "NA_SE_IT_SHIELD_REFLECT_SW": 1, "NA_SE_EN_DEKU_MOUTH": 1, "NA_SE_EN_DEKU_JR_MOUTH": 1, "NA_SE_EN_DEKU_ATTACK": 1,
    "NA_SE_EN_DEKU_JR_ATTACK": 1, "NA_SE_EN_DEKU_SCRAPE": 1, "NA_SE_EN_DEKU_DAMAGE": 1, "NA_SE_EN_DEKU_DEAD": 1,
    "NA_SE_EN_DEKU_JR_DEAD": 1, "NA_SE_EN_DUMMY482": 1, "NA_SE_EN_DODO_M_GND": 1,
    "NA_SE_EN_STALTU_LAUGH": 1, "NA_SE_EN_STALTU_DOWN": 1, "NA_SE_EN_STALTU_DOWN_SET": 1, "NA_SE_EN_STALTU_UP": 1,
    "NA_SE_EN_STALTU_ROLL": 1, "NA_SE_EN_STALTU_WAVE": 1, "NA_SE_EN_STALTU_DAMAGE": 1, "NA_SE_EN_STALWALL_DEAD": 1,
    "NA_SE_PL_BODY_HIT": 1, "NA_SE_EN_NUTS_UP": 1, "NA_SE_EN_NUTS_DOWN": 1, "NA_SE_EN_NUTS_THROW": 1, "NA_SE_EN_NUTS_DAMAGE": 1,
    "NA_SE_EN_NUTS_WALK": 1, "NA_SE_EN_NUTS_FAINT": 1, "NA_SE_EN_OCTAROCK_ROCK": 1,
    "NA_SE_EN_STALWALL_ROLL": 1, "NA_SE_EN_STALGOLD_ROLL": 1, "NA_SE_EN_STALGOLD_UP_CRY": 1, "NA_SE_EN_EXTINCT": 1,
    "NA_SE_EV_WEB_BROKEN": 1, "NA_SE_EV_WEB_VIBRATION": 1, "NA_SE_EV_FLAME_IGNITION": 1, "NA_SE_SY_CORRECT_CHIME": 1,
    "NA_SE_EV_FAIRY_DASH": 1, "NA_SE_IT_SLING_SHOT": 1, "NA_SE_IT_SLING_REFLECT": 1, "NA_SE_IT_SLING_DRAW": 1, "NA_SE_SY_TRE_BOX_APPEAR": 1,
    "NA_SE_VO_LI_GROAN": 1, "NA_SE_VO_LI_RELAX": 1, "NA_SE_PL_SLIPDOWN": 1, "NA_SE_PL_LAND_LADDER": 1, "NA_SE_PL_WALK_LADDER": 1,
    "NA_SE_EV_BIGBALL_ROLL": 1, "NA_SE_EV_STONE_BOUND": 1, "NA_SE_EV_ELEVATOR_MOVE": 1, "NA_SE_EV_LADDER_DOUND": 1,
    "NA_SE_EV_TOGE_STICK_ROLLING": 1, "NA_SE_EV_FOOT_SWITCH": 1, "NA_SE_EV_BLOCK_BOUND": 1, "NA_SE_EV_BLOCK_SHAKE": 1,
    "NA_SE_EV_ROCK_SLIDE": 1, "NA_SE_EV_TRAP_OBJ_SLIDE": 1, "NA_SE_EV_WATER_LEVEL_DOWN": 1, "NA_SE_EV_DIVE_WATER": 1,
    "NA_SE_EV_METALDOOR_OPEN": 1, "NA_SE_EV_METALDOOR_CLOSE": 1, "NA_SE_EV_SLIDE_DOOR_OPEN": 1, "NA_SE_EV_SLIDE_DOOR_CLOSE": 1,
    "NA_SE_EV_BOX_BREAK": 1, "NA_SE_SY_TIMER": 1, "NA_SE_PL_CLIMB_CLIFF": 1, "NA_SE_VO_LI_CLIMB_END_KID": 1,
    "NA_SE_VO_LI_HANG_KID": 1, "NA_SE_PL_SWIM": 1, "NA_SE_PL_DIVE_BUBBLE": 1, "NA_SE_PL_FACE_UP": 1,
    "NA_SE_EN_GOMA_EGG1": 1, "NA_SE_EN_GOMA_JR_CRY": 1, "NA_SE_EN_GOMA_JR_DAM1": 1, "NA_SE_EN_GOMA_JR_DAM2": 1,
    "NA_SE_EN_GOMA_JR_DEAD": 1, "NA_SE_EN_GOMA_JR_LAND": 1, "NA_SE_EN_GOMA_JR_LAND2": 1, "NA_SE_EN_GOMA_JR_WALK": 1,
    "NA_SE_SY_PIECE_OF_HEART": 1,                       # Startbildschirm: Taste (En_Mag)
}

# Eigene Werte: Licht im Kokiri-Laden (Moritz' Wunsch: warm, Theke hell)
SHOP_LIGHTS = [
    {"pos": [-1380.0, 206.6, -1720.0], "color": [1.0, 0.85, 0.65], "range": 9.0, "intensity": 0.8},
    {"pos": [-1380.0, 204.95, -1718.68], "color": [1.0, 0.8, 0.6], "range": 6.0, "intensity": 0.5},
    {"pos": [-1380.0, 205.94, -1718.02], "color": [1.0, 0.82, 0.55], "range": 12.0, "intensity": 1.1},
    {"pos": [-1380.0, 205.28, -1723.96], "color": [1.0, 0.85, 0.6], "range": 10.0, "intensity": 0.8},
]


def log(*a):
    print("[%s]" % time.strftime("%H:%M:%S"), *a, flush=True)


def load(path, default=None):
    if not os.path.exists(path):
        return default
    return json.load(open(path, encoding="utf-8"))


def save(path, data, indent=1):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=indent)


def build(root, out, style="blocks"):
    t0 = time.time()
    os.makedirs(out, exist_ok=True)
    rep = {}
    # ---- 1. Grundpaket: Steppe, Albtraum-Cutscene, Links Haus, Kokiri-Wald (Remaster), Startbild, Link, HUD, Kokiri, Sounds
    import oot_vpack
    log("Grundpaket ...")
    rep["base"] = oot_vpack.build_all(root, out, style)
    pk = load(os.path.join(out, "pack.json"))
    spots = load(os.path.join(out, "spots.json"), [])

    def put_spots(new):
        ids = {s["id"] for s in new}
        spots[:] = [s for s in spots if s["id"] not in ids] + new

    # ---- 2. Kokiri-Wald Teil 2: Deku-Ranhas, Laden, Get-Item-Modelle, Felsbrocken
    import oot_kf2
    log("Kokiri-Wald Teil 2 ...")
    r = oot_kf2.export(out)
    pk["oot_enemies"] = r["oot_enemies"]
    pk["oot_shop"] = r["oot_shop"]
    for k in ("gi_models", "pickups", "gi_texts"):
        pk["oot_items"].setdefault(k, {}).update(r["items_add"].get(k, {}))
    save(os.path.join(out, "statics_shop.json"), r["statics"])
    put_spots(r["spots"])
    pk["oot_play"]["lights"] += SHOP_LIGHTS
    bl, _ = oot_kf2.boulders(out)

    # ---- 3. Deku-Baum
    import oot_ydan, oot_ydan_remaster as R, oot_ydan_decor as D, oot_mood
    log("Deku-Baum ...")
    y = oot_ydan.export(out)
    pk["oot_ydan"] = y["cfg"]
    pk["oot_deku"] = y["deku"]
    # Reihenfolge wie auf dem Test-PC: Wald, Deku-Baum, Felsbrocken, Gohma-Larven
    pk["oot_enemies"]["list"] += [e for e in y["enemies"] if e["type"] != "goma"] + bl + [e for e in y["enemies"] if e["type"] == "goma"]
    pk["oot_enemies"]["st"] = y["st"]
    pk["oot_enemies"]["scrub"] = y["scrub"]
    pk["oot_enemies"].setdefault("texts", {}).update(y["enemy_texts"])
    pk["oot_enemies"]["goma"] = oot_ydan.goma(out)
    save(os.path.join(out, "statics_ydan.json"), y["statics"])
    put_spots(y["spots"])
    it = y["items"]
    pk["oot_items"]["chests"] = it.get("chests", [])
    for k in ("gi_models", "gi_texts"):
        pk["oot_items"].setdefault(k, {}).update(it.get(k, {}))
    pk["oot_items"]["bushes"] += ydan_bushes()
    pk["oot_ydan_obj"] = oot_ydan.objects(out)[0]
    pk["oot_play"]["lights"] += R.lights()
    pk["oot_play"]["areas"] = oot_mood.areas()
    pk["oot_atmos"] = R.atmos()
    # Deko im Baum (an die Wald-Deko angehaengt)
    fol = os.path.join(out, "foliage.bin")
    if os.path.exists(fol):
        D.merge(fol, fol + ".tmp", D.rows()); os.replace(fol + ".tmp", fol)
    # Intro-Kamera beim ersten Betreten
    os.makedirs(os.path.join(out, "cutscenes"), exist_ok=True)
    save(os.path.join(out, "cutscenes", "ydan_intro.json"), oot_ydan.intro_track(), indent=None)
    cl = load(os.path.join(out, "cutscenes.json"), [])
    cl = [c for c in cl if c["id"] != "ydan_intro"] + [{"id": "ydan_intro", "track": "cutscenes/ydan_intro.json"}]
    save(os.path.join(out, "cutscenes.json"), cl)
    put_spots([{"id": "ydan_intro", "pos": oot_ydan.YV((-4, 0, 555)), "radius": 1.2, "height": 3.0, "zone": True, "once": True,
                "do": [{"cutscene": "ydan_intro"}]}])

    # ---- 4. Aufwach-Szene, Kriechgang, Sounds
    import oot_intro, oot_sfx, oot_link
    log("Aufwachen, Sounds ...")
    pk["oot_wake"] = oot_intro.wake_cfg(root)
    pk["oot_play"]["stick_tip"] = oot_link.stick_tip()
    put_spots(crawl_spots())
    clips = oot_sfx.export_names(out, {k: v for k, v in SFX.items() if k not in pk["oot_sfx"]["clips"]})
    pk["oot_sfx"]["clips"].update(clips)
    pk["oot_sfx"]["clips"]["NA_SE_VO_NAVY_CALL"] = pk["oot_sfx"]["clips"].get("NA_SE_VO_NAVY_CALL", [])[:1]   # Navi: immer derselbe Ruf
    # Titelkarte "Inside the Deku Tree"
    title_card(out)
    # Musik je Gebiet
    rep["music"] = music(out)
    # Startbildschirm + Intro im Spiel (OotTitle): Logo, Titelmusik, Spur-Einstellungen
    import oot_start
    rep["start"] = {"tracks": oot_start.tracks(os.path.join(out, "cutscenes")), "logo": oot_start.logo(out)}
    cap = os.path.join(root, "gi_audio")
    if os.path.exists(os.path.join(cap, "title_intro.ogg")) and os.path.exists(os.path.join(cap, "title_loop.ogg")):
        for f in ("title_intro.ogg", "title_loop.ogg"):
            shutil.copy2(os.path.join(cap, f), os.path.join(out, "music", f))
        rep["start"]["music"] = "mitschnitt"
    else:
        rep["start"]["music"] = oot_start.music_synth(out)
    for f, dst in (("nightmare_audio.ogg", "cutscenes"), ("intro_kf_audio.ogg", "cutscenes")):
        if os.path.exists(os.path.join(cap, f)):
            shutil.copy2(os.path.join(cap, f), os.path.join(out, dst, f))
    pk["oot_title"] = oot_start.config()
    pk["music"] = {"explore": ["music/kokiri_forest.ogg"], "volume": 0.55, "loop": True}

    save(os.path.join(out, "pack.json"), pk)
    save(os.path.join(out, "spots.json"), spots)
    rep["time_s"] = round(time.time() - t0, 1)
    log("fertig", rep["time_s"], "s")
    return rep


# Musik je Gebiet: Sequenz der ROM -> Datei (oot_mood ordnet die Dateien den Gebieten zu)
MUSIC = [(60, "kokiri_forest"), (31, "house"), (85, "shop"), (28, "deku_tree"), (75, "deku_tree_cs")]     # 75 = NA_BGM_DEKU_TREE_CS (Gespraech mit dem Deku-Baum)


def title_card(out):
    """Ortsname "Inside the Deku Tree" (g_pn_06, PAL-Fassung, englisch), 3x vergroessert wie die uebrigen HUD-Bilder."""
    import oot_hud
    from PIL import Image
    d = os.path.join(out, "player", "hud"); os.makedirs(d, exist_ok=True)
    for nm, im in oot_hud.textures("g_pn_06", lambda s: s == "gDekuTreeTitleCardENGTex", xml_name="place_title_cards_pal"):
        im.resize((im.width * 3, im.height * 3), Image.LANCZOS).save(os.path.join(d, "title_ydan.png"))
        return True
    return False


def _hall(x, wet=0.2, t60=1.5, seed=7):
    """Etwas Raumhall wie der N64-Hall der Sequenzen. Kreisfoermig gefaltet: der Nachhall vom Ende der Schleife klingt an
    ihrem Anfang aus, die Schleife bleibt also nahtlos."""
    import numpy as np
    import oot_sfx
    R = oot_sfx.RATE; n = int(t60 * R); N = len(x)
    rng = np.random.default_rng(seed)
    ir = rng.standard_normal(n) * np.exp(-6.9 * np.arange(n) / n)
    ir = np.convolve(ir, np.ones(12) / 12.0, mode="same")              # dumpfer, wie der Hall der Konsole
    ir[: int(0.012 * R)] = 0.0                                         # kurze Vorverzoegerung
    ir /= np.sqrt((ir ** 2).sum())
    h = np.zeros(N)
    for i in range(0, n, N):                                           # IR laenger als die Schleife: um die Schleife falten
        seg = ir[i:i + N]; h[:len(seg)] += seg
    y = np.fft.irfft(np.fft.rfft(x) * np.fft.rfft(h), N)
    z = x + wet * y * (np.abs(x).max() / max(1e-9, np.abs(y).max()))
    return z * (0.9 / max(1e-9, np.abs(z).max()))


def music(out, decomp=None):
    """Hintergrundmusik als nahtlose Schleifen. Liegt ein eigener Mitschnitt aus dem Emulator vor
    (<decomp>/gi_audio/<name>_loop.ogg, z.B. mit der eigenen ROM in einem Emulator aufgenommen und auf eine Schleife
    geschnitten), wird der genommen; sonst wird die Sequenz der ROM mit ihren Soundfonts synthetisiert (oot_sfx)."""
    import oot_sfx
    decomp = decomp or os.environ.get("OOT_DECOMP", "/home/claude/oot")
    d = os.path.join(out, "music"); os.makedirs(d, exist_ok=True)
    rep = {}
    for seq, name in MUSIC:
        dst = os.path.join(d, name + ".ogg")
        cap = os.path.join(decomp, "gi_audio", name + "_loop.ogg")
        if os.path.exists(cap):
            shutil.copy2(cap, dst); rep[name] = "mitschnitt"; continue
        x, L, start = oot_sfx.render_loop(seq)
        if L is None:
            log("Musik", name, ": keine Schleife gefunden, ganze Sequenz")
        oot_sfx._ogg(dst, _hall(x))
        rep[name] = round(L if L else len(x) / oot_sfx.RATE, 2)
        log("Musik", name, rep[name], "s")
    return rep


def crawl_spots():
    """Kriechgang zum Schwert-Labyrinth (spot04): Ortswechsel-Zonen vor und hinter dem Loch im Hang (eigene Werte; der Tunnel
    steckt im Valheim-Gelaende, darum Ortswechsel statt Kriechen)."""
    import oot_kf
    sh = oot_kf.shift_v           # Labyrinth steht an eigenem Ort (oot_kf.MAZE_SHIFT)
    r3 = lambda v: [round(x, 3) for x in v]
    return [
        {"id": "kf_crawl_in", "pos": [624.095, 41.94, -184.584], "radius": 0.6, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": r3(sh([624.095, 42.105, -196.86])), "yaw": 180.0}]},
        {"id": "kf_crawl_out", "pos": r3(sh([624.095, 41.94, -195.804])), "radius": 0.6, "height": 2.0, "zone": True, "once": False,
         "do": [{"warp": [624.095, 42.105, -183.66], "yaw": 0.0}]},
    ]


def ydan_bushes():
    """Gras im Deku-Baum (En_Kusa der Raeume) -> oot_items.bushes (wie im Wald: abschneidbar, Herzen/Rubine)."""
    import oot_ydan as Y
    ids = Y.I.actor_ids()
    out, seen = [], set()
    for r, aid, x, y, z, rx, ry, rz, prm in Y.room_actors("ydan", 12):
        if (aid & 0xFFFF) != ids["ACTOR_EN_KUSA"] or (x, y, z) in seen:
            continue
        seen.add((x, y, z))
        out.append([round(v, 3) for v in Y.YV((x, y, z))])
    return out


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("root", help="Decomp-Ordner mit eigener ROM (oot_setup_decomp.sh)")
    ap.add_argument("out", help="Ausgabe: fertiges Paket (Inhalt von config/GameImporter/packs/oot)")
    ap.add_argument("--style", default="blocks")
    ap.add_argument("--vtex", help="Valheim-Texturen (im Spiel: statics dumpvtex) -> GI_VTEX")
    ap.add_argument("--vmat", help="Valheim-Materialien (im Spiel: statics dumpvtex) -> GI_VMAT")
    a = ap.parse_args()
    # Pfade fuer die Exporter (vor deren Import setzen)
    os.environ["OOT_DECOMP"] = os.path.abspath(a.root)
    if a.vtex:
        os.environ["GI_VTEX"] = os.path.abspath(a.vtex)
    if a.vmat:
        os.environ["GI_VMAT"] = os.path.abspath(a.vmat)
    print(json.dumps(build(a.root, a.out, a.style), default=str)[:3000])
