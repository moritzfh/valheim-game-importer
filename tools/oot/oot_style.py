"""Valheim-Optik fuer OoT-Szenen: jede Original-Textur bekommt eine Kategorie.

  ground / path / cliff / rock -> Valheim-Gelaende (Heightmap), nicht als Modell
  water                        -> Valheim-Wasser (Hoehe so gewaehlt, dass der Graben bei 30 m liegt)
  stone / wood / roof / metal  -> Bauwerk in Valheim-Material (Variante "retex": Originalform; "blocks": Valheim-Bloecke)
  drop                         -> faellt weg (gemalte Kulissen, Schatten, Gras-Fransen, Schaum)
  bush                         -> spaeter Valheim-Bueschee

Schluessel = Texturadresse (Segment 2 = Szenendatei) als Hex ohne Segment, wie in mats_spot00.png.
"""

SPOT00 = {
    "1ab98": "ground", "25898": "ground", "18b98": "path", "15d98": "path",
    "24818": "cliff", "1d998": "cliff", "19b98": "cliff", "16b98": "cliff", "15e98": "cliff", "17b98": "rock",
    "20818": "water", "25018": "drop", "23818": "drop", "24018": "drop", "1f198": "drop", "1e998": "drop",
    "26998": "drop", "14d98": "drop", "20018": "drop", "1f818": "bush",
    "29f98": "stone", "29d98": "stone", "29598": "stone", "27d98": "stone", "27998": "stone", "22018": "stone",
    "1bd98": "stone", "16a98": "stone", "26898": "stone", "28598": "stone", "25818": "roof", "1bb98": "roof",
    "21818": "wood", "14598": "wood", "23018": "wood", "13d98": "wood", "17398": "wood", "19398": "wood",
    "1c198": "wood", "28198": "wood", "22818": "wood", "1c998": "wood", "1f618": "wood", "14998": "wood",
    "1d198": "wood", "21018": "wood", "16698": "cloth",
    "26098": "drop", "1f598": "drop",
}

# Valheim-Material je Kategorie (Prefab[:Materialnummer]); Kachelgroesse der Textur in Metern (Box-Projektion)
VALHEIM = {
    # Kategorie: (Prefab, Kachel Breite x Hoehe in m, Atlas-Ausschnitt u0, u1, v0, v1 in Unity-UV)
    "stone": ("stone_wall_4x2", (4.0, 2.0), (0.0, 0.72, 0.01, 0.40)),
    "roof": ("stone_wall_4x2", (4.0, 2.0), (0.0, 0.72, 0.01, 0.40)),
    "wood": ("wood_floor", (2.0, 2.0), (0.0, 1.0, 0.0, 1.0)),
    "cloth": ("wood_floor", (2.0, 2.0), (0.0, 1.0, 0.0, 1.0)),
}

NATURAL = {"ground", "path", "cliff", "rock"}

# Zelda-Details: bleiben als Original-Textur und liegen als Platte AUSSEN auf den Valheim-Bauteilen
# (Wappen mit Triforce, Banner, Fenster/Torboegen, Eisenringe). Dahinter wird trotzdem in Valheim-Bloecken gebaut.
DETAIL = {"28598", "16698", "16a98", "1bd98", "26898"}
# Details, die in Valheim-Stein "gemeisselt" werden (Steintextur x Relief des Originals); der Rest (Stoff) nur abgedunkelt
STONE_DETAIL = {"28598", "16a98", "1bd98", "26898"}


def is_detail(mat_key):
    k = mat_key[0]
    return isinstance(k, int) and ("%x" % (k & 0xFFFFFF)) in DETAIL


def category(mat_key, table=SPOT00):
    k = mat_key[0]
    if not isinstance(k, int):
        return "stone"          # reine Farbe (z. B. Kanten) -> wie Mauer
    return table.get("%x" % (k & 0xFFFFFF), "stone")
