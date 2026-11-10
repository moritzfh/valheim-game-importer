"""Zelda-HUD-Grafiken aus dem ROM (parameter_static: Herzen, Knoepfe, Rubin, Ziffern; do_action_static: englische
Aktions-Schriftzuege auf dem A-Knopf) -> PNG-Dateien fuer src/OotHud.cs. Graustufen/IA-Texturen bleiben weiss mit Alpha,
die Farben setzt das HUD wie im Spiel (Herzen rot, A blau, B gruen, C gelb).

  python oot_hud.py out_dir
"""
import os
import re
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_anim as A
from oot_gfx import decode
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

TEXXML = OOT_DECOMP + "/assets/xml/textures"
FMTS = {"ia4": (3, 0), "ia8": (3, 1), "ia16": (3, 2), "i4": (4, 0), "i8": (4, 1), "rgba16": (0, 2), "rgba32": (0, 3)}


def textures(name, want=None, xml_name=None):
    xml = open(os.path.join(TEXXML, (xml_name or name) + ".xml")).read()
    data = A.obj(name)
    for m in re.finditer(r'<Texture Name="(\w+)" Format="(\w+)" Width="(\d+)" Height="(\d+)" Offset="(0x[0-9A-Fa-f]+)"', xml):
        nm, f, w, h, off = m.group(1), m.group(2), int(m.group(3)), int(m.group(4)), int(m.group(5), 16)
        if f not in FMTS or (want and not want(nm)):
            continue
        fmt, siz = FMTS[f]
        n = (w * h * (4, 8, 16, 32)[siz] + 7) // 8
        yield nm, Image.frombytes("RGBA", (w, h), bytes(decode(data[off:off + n], fmt, siz, w, h)))


def export(out_dir):
    d = os.path.join(out_dir, "hud"); os.makedirs(d, exist_ok=True)
    n = 0
    for nm, im in textures("parameter_static"):
        im.save(os.path.join(d, nm + ".png")); n += 1
    # EU-ROM: PAL-Reihenfolge (ENG zuerst, dann GER, FRA)
    for nm, im in textures("do_action_static", lambda s: s.endswith("ENGTex"), xml_name="do_action_static_pal"):
        im.save(os.path.join(d, nm + ".png")); n += 1
    return {"hud_textures": n}


if __name__ == "__main__":
    print(export(sys.argv[1]))
