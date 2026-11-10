"""Stimmungs-Gebiete fuer oot_play.areas (src/OotPlay.cs): Wetter/Umgebung, Uhrzeit, Hall, Schwebeteilchen, Licht
und Musik je Gebiet. Reihenfolge wichtig: Index 0 (Kokiri-Wald) beschreibt das F7-Pult (OotMoodUi).

Musik (Original, mit der eigenen ROM im Emulator mitgeschnitten, nahtlose Schleifen, siehe music_files()):
  Kokiri-Wald  NA_BGM_KOKIRI (seq 60)          -> music/kokiri_forest.ogg (Erkunden-Liste, kein Gebietseintrag noetig)
  Links Haus   NA_BGM_LINK_HOUSE (seq 31)      -> music/house.ogg
  Kokiri-Laden NA_BGM_SHOP (seq 85)            -> music/shop.ogg
  Deku-Baum    NA_BGM_INSIDE_DEKU_TREE (seq 28) -> music/deku_tree.ogg (alle ydan-Gebiete)
"""
import oot_ydan

KF = [
    {"id": "kf", "min": [540.0, -280.0], "max": [880.0, -20.0], "env": "Twilight_Clear", "gust": "Misty", "gust_every": [150, 300],
     "gust_len": [35, 70], "flies": 0.5, "motes": 0.45},
    {"id": "deku_meadow", "min": [738.0, -150.0], "max": [805.0, -70.0], "env": "Clear", "time": 0.42, "flies": 0.3,
     "motes": 0.9, "mote_color": [1.0, 0.9, 0.55, 0.55]},
    {"id": "link_home", "box": [-1506.0, 196.0, -1506.0, -1494.0, 214.0, -1494.0], "env": "Clear", "time": 0.3, "reverb": "Livingroom",
     "motes": 0.5, "mote_color": [1.0, 0.92, 0.7, 0.5], "music": "music/house.ogg"},
    {"id": "kf_shop", "box": [-1387.0, 198.0, -1728.0, -1373.0, 214.0, -1714.0], "env": "Clear", "time": 0.4, "reverb": "Room", "motes": 0.3,
     "music": "music/shop.ogg"},
]


def areas():
    out = [dict(a) for a in KF]
    import oot_kf
    b = oot_kf.MAZE_BOX_NEW       # verlegtes Schwert-Labyrinth: Stimmung wie im Wald
    out.insert(1, dict(KF[0], id="kf_maze", min=[b[0] - 30.0, b[2] - 30.0], max=[b[1] + 30.0, b[3] + 30.0]))
    for a in oot_ydan.mood_areas():
        a["music"] = "music/deku_tree.ogg"
        out.append(a)
    return out


# Mitschnitt: Test-ROM (oot_testrom.py, entrance 0x00 Deku-Baum / 0xBB Links Haus / 0xC1 Laden, cs 0), mupen64plus mit
# Audio-Plugin m64wav (GI_WAV=...wav, --nospeedlimit --testshots 500,6000 = ~6 min Ton). Schleife: Deku-Baum laeuft in
# Seq-Schleifen von 2688 Ticks (67,2 s) mit fast stillen Stellen -> dort schneiden; Haus (3072 Ticks, ~26,3 s im PAL-Ton)
# und Laden (~54,9 s) per Kreuzkorrelation, 0,4 s Ueberblendung mit dem naechsten Durchlauf, RMS auf 0,12.
