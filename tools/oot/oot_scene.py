"""Szene/Raeume einer OoT-Szene als GLB exportieren.
  python oot_scene.py <baserom-dir> <szene> <out.glb> [--scale 0.01] [--keep field|dangeon]
"""
import sys, os, argparse, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from oot_gfx import Segs, Gfx, room_dlists
import oot_glb


def load_scene(rom, scene, keep="field"):
    sb = open(os.path.join(rom, scene + "_scene"), "rb").read()
    rooms = []
    i = 0
    while os.path.exists(os.path.join(rom, "%s_room_%d" % (scene, i))):
        rooms.append(open(os.path.join(rom, "%s_room_%d" % (scene, i)), "rb").read())
        i += 1
    gk = open(os.path.join(rom, "gameplay_keep"), "rb").read()
    kk = open(os.path.join(rom, "gameplay_%s_keep" % keep), "rb").read()
    return sb, rooms, gk, kk


def export(rom, scene, out, scale=0.01, keep="field", rooms_sel=None):
    sb, rooms, gk, kk = load_scene(rom, scene, keep)
    groups = collections.OrderedDict()
    mats = {}
    missing = set()
    for ri, rb in enumerate(rooms):
        if rooms_sel is not None and ri not in rooms_sel:
            continue
        g = Gfx(Segs({2: sb, 3: rb, 4: gk, 5: kk}))
        for opa, xlu in room_dlists(rb):
            if opa:
                g.reset(); g.run(opa, xlu=False)
            if xlu:
                g.reset(); g.run(xlu, xlu=True)
        for k, vs in g.tris:
            groups.setdefault(k, []).append(vs)
        mats.update(g.mats)
        missing |= g.missing
    n = oot_glb.write(out, groups, mats, scale=scale, name=scene)
    ntri = sum(len(v) for v in groups.values())
    return {"rooms": len(rooms), "materials": n, "tris": ntri, "missing_segs": sorted(missing)}


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("rom"); ap.add_argument("scene"); ap.add_argument("out")
    ap.add_argument("--scale", type=float, default=0.01)
    ap.add_argument("--keep", default="field")
    a = ap.parse_args()
    print(export(a.rom, a.scene, a.out, a.scale, a.keep))
