"""Spielfigur: Kind-Link (Original-N64-Modell object_link_child) als Skin-GLB mit Original-Spieler-Animationen
(gameplay_keep/link_animetion) als einzelne Clips. Laeuft in Valheim ueber Avatar.cs (pack.json "player_model").

  python oot_link.py out_dir

Wurzel-Verschiebung wie im Spiel x0,64 (Kind, Player_OverrideLimbDraw); Bewegung in X/Z kommt aus der Steuerung (Clips stehen
auf der Stelle), nur die Hoehe der Wurzel bleibt (Ducken, Landen).
"""
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import oot_anim as A
import oot_horse
from oot_gfx import Segs, Gfx

ROOT_SCALE = 0.64
SHEATH_LIMB = 19          # Schwertscheide auf dem Ruecken: am Anfang hat Link noch kein Schwert
FPS = 30.0              # OoT spielt Animationen mit 1,5 Bildern je Spielbild (R_UPDATE_RATE 3 * 0,5) bei 20 Spielbildern/s

# Clip-Name -> Original-Animation (gPlayerAnim_...), PLAYER_ANIMTYPE_0 (Haende frei)
CLIPS = {
    "idle": "link_normal_wait_free",
    "idle_a": "link_normal_waitF_typeA_20f", "idle_b": "link_normal_waitF_typeB_20f", "idle_c": "link_normal_waitF_typeC_20f",
    "walk": "link_normal_walk_free",
    "run": "link_normal_run_free",
    "back_walk": "link_normal_back_walk",
    "side_l": "link_normal_side_walkL_free", "side_r": "link_normal_side_walkR_free",
    "turn": "link_normal_45_turn_free",
    "roll": "link_normal_landing_roll_free",
    "jump": "link_normal_jump",
    "run_jump": "link_normal_run_jump", "run_jump_end": "link_normal_run_jump_end",
    "fall": "link_normal_fall",
    "fall_wait": "link_normal_fall_wait",
    "land": "link_normal_landing_free",
    "land_short": "link_normal_short_landing_free",
    "backflip": "link_fighter_backturn_jump", "backflip_end": "link_fighter_backturn_jump_end",
    "hop_l": "link_fighter_Lside_jump", "hop_r": "link_fighter_Rside_jump",
    "talk": "link_normal_talk_free", "talk_wait": "link_normal_talk_free_wait",
    "push": "link_normal_pushing", "push_wait": "link_normal_push_wait",
    "climb_up": "link_normal_jump_climb_up_free", "hang": "link_normal_jump_climb_hold_free",
    "step_up": "link_normal_100step_up",          # Kante bis 47 Einheiten (Kind) hochsteigen
    "slash": "link_fighter_Lnormal_kiru", "slash_end": "link_fighter_Lnormal_kiru_end",
    "slash2": "link_fighter_Lside_kiru", "slash2_end": "link_fighter_Lside_kiru_end",
    "chest_open": "link_demo_Tbox_open", "get_item": "link_demo_get_itemB",
    # Kampf: Schild (R), getroffen von vorn/hinten, umfallen
    "shield_in": "link_normal_defense", "shield": "link_normal_defense_wait",
    "bow_ready": "link_bow_bow_ready", "bow_wait": "link_bow_bow_wait", "bow_shoot": "link_bow_bow_shoot",
    "damage_front": "link_normal_front_shit", "damage_back": "link_normal_back_shit", "die": "link_normal_front_downA",
    # Deku-Baum: Ranken/Leitern klettern, schwimmen
    # Klettern wie z_player.c (unk_AC): Schritt fuer Schritt upL, upR, upL ... (runter = dieselben rueckwaerts). Als eine Schleife
    # L+R mit herausgerechnetem Wurzel-Anstieg (die Hoehe macht die Physik), Uebergang R->L weich (Moritz 09.11.: Schleife ruckte)
    "climb_u": ("clink_normal_climb_upL", "clink_normal_climb_upR"), "climb_d": ("rev", "clink_normal_climb_upL", "clink_normal_climb_upR"),
    "climb_w": "clink_normal_climb_upL",
    "climb_sl": "link_normal_Fclimb_sideL", "climb_sr": "link_normal_Fclimb_sideR", "climb_e": "clink_normal_climb_endAL",
    "swim": "link_swimer_swim", "swim_wait": "link_swimer_swim_wait",
    # Aufwachen in Links Haus (gLinkHouseIntroWakeUpCs, PLAYER_CUEID_28..31): schlafen, umdrehen, aufsetzen, aufstehen
    "op3_wait1": "clink_op3_wait1", "op3_negaeri": "clink_op3_negaeri", "op3_wait2": "clink_op3_wait2",
    "op3_okiagari": "clink_op3_okiagari", "op3_wait3": "clink_op3_wait3", "op3_tatiagari": "clink_op3_tatiagari",
}
# Cutscene-Clips mit ANIM_FLAG_DISABLE_CHILD_ROOT_ADJUSTMENT + OVERRIDE_MOVEMENT: Wurzel unskaliert, Bewegung bleibt im Skelett
RAW_ROOT = {"clink_op3_wait1", "clink_op3_negaeri", "clink_op3_wait2", "clink_op3_okiagari", "clink_op3_wait3", "clink_op3_tatiagari"}
# Zusatzteile (eigene Teil-Materialien, zur Laufzeit ein-/ausblendbar): Schwert in der Faust, Scheide mit/ohne Schwert
PARTS = [("sword_hand", 15, "gLinkChildLeftFistAndKokiriSwordNearDL"), ("sheath_sword", 19, "gLinkChildBackKokiriSwordInSheathNearDL"),
         ("sheath_empty", 19, "gLinkChildBackKokiriSheathNearDL"),
         # Deku-Schild: in der rechten Faust (Schwert gezogen / R) oder auf dem Ruecken (mit/ohne Schwert in der Scheide)
         ("shield_hand", 18, "gLinkChildRightFistAndDekuShieldNearDL"),
         ("sheath_shield_sword", 19, "gLinkChildBackDekuShieldKokiriSwordInSheathNearDL"),
         ("sheath_shield_empty", 19, "gLinkChildBackDekuShieldKokiriSheathNearDL"),
         ("back_shield", 19, "gLinkChildBackDekuShieldDL"),
         # Deku-Stab in der linken Faust (Player_PostLimbDrawGameplay: Translate(-428.26, 267.2, -33.82), RotateZYX(-0x8000, 0, 0x4000))
         ("stick_hand", 15, [("gLinkChildLeftFistNearDL", None), ("gLinkChildLinkDekuStickDL", "stick")]),
         # Feen-Schleuder in der rechten Hand (Zielen mit C-rechts)
         ("sling_hand", 18, "gLinkChildRightHandHoldingSlingshotNearDL")]
HAND_L_LIMB = 15
HAND_R_LIMB = 18
KEEP_TEX = {"gLinkChildDekuShieldBackTex", "gLinkChildDekuShieldFrontTex", "gDekuStickTex"}


AIR_FIXED_ROOT = {"link_normal_fall", "link_normal_fall_wait"}


def stick_matrix():
    """Lage des Deku-Stabs im Glied der linken Hand (Einheiten des Glieds)."""
    import math as _m
    def rx(a):
        c, s_ = _m.cos(a), _m.sin(a); return np.array([[1, 0, 0, 0], [0, c, -s_, 0], [0, s_, c, 0], [0, 0, 0, 1]])
    def rz(a):
        c, s_ = _m.cos(a), _m.sin(a); return np.array([[c, -s_, 0, 0], [s_, c, 0, 0], [0, 0, 1, 0], [0, 0, 0, 1]])
    T = np.eye(4); T[:3, 3] = (-428.26, 267.2, -33.82)
    return T @ rz(_m.pi / 2) @ rx(-_m.pi)


def stick_tip():
    """Spitze des Deku-Stabs im Glied der linken Hand (fuer das Anzuenden an Fackeln)."""
    data, sym = A.obj("object_link_child"), A.symbols("object_link_child")
    g = Gfx(Segs({6: data, 4: A.obj("gameplay_keep")}))
    g.reset(); g.run(0x06000000 | sym["gLinkChildLinkDekuStickDL"])
    P = np.array([p for _, vs in g.tris for p, _, _ in vs], float)
    v = P[np.argmax(P[:, 1])]
    t = stick_matrix() @ np.append(v, 1)
    return [round(float(x), 1) for x in t[:3]]


class StdSkel:
    """Standard-Skelett (Glied mit festem Display-List) im Format, das oot_horse.write_glb erwartet"""
    def __init__(self, data, off):
        s = A.Skel(data, off)
        self.n, self.pos, self.parent, self.child, self.sib = s.n, s.pos, s.parent, s.child, s.sib
        self.type = [11 if d else 0 for d in s.dl]
        self.seg = list(s.dl)


def clip_tabs(name, nlimbs):
    if isinstance(name, tuple):
        return _clip_loop(name, nlimbs)
    an = A.LinkAnim("gPlayerAnim_" + name, nlimbs)
    out = []
    for f in range(an.frames):
        t = np.array(an.table(f), np.int64).copy()
        if name in RAW_ROOT:
            out.append(t); continue
        t[0] = np.round(t[0] * ROOT_SCALE)
        t[0][0] = 0; t[0][2] = 0                   # auf der Stelle
        if name in AIR_FIXED_ROOT:
            t[0][1] = round(3420 * ROOT_SCALE)       # Fallen: im Spiel traegt die Figur selbst die Hoehe (sonst Huefte 2 m unter dem Boden)
        out.append(t)
    return out


def _clip_loop(spec, nlimbs, blend=4):
    """Mehrere Clips hintereinander als nahtlose Schleife: Wurzel-Hoehe ohne Gesamtanstieg, letzte Bilder zum ersten ueberblenden."""
    rev = spec[0] == "rev"
    names = spec[1:] if rev else spec
    tabs = []
    for nm in names:
        tabs += clip_tabs(nm, nlimbs)
    T = np.array(tabs, np.float64)
    n = len(T)
    rise = T[-1][0][1] - T[0][0][1]
    for f in range(n):
        T[f][0][1] -= rise * f / n                 # Schleife: Bild n waere wieder Bild 0
    for j in range(blend):                          # weich zum Anfang (Drehungen kuerzester Weg)
        f = n - blend + j; w = (j + 1.0) / (blend + 1.0)
        d = ((T[0][1:] - T[f][1:] + 32768) % 65536) - 32768
        T[f][1:] += d * w
        T[f][0][1] += (T[0][0][1] - T[f][0][1]) * w
    out = [np.round(t).astype(np.int64) for t in T]
    return out[::-1] if rev else out


def build_mesh(data, skel, bind_tab, sym, eyes="gLinkChildEyesOpenTex", mouth="gLinkChildMouthClosedTex", parts=None):
    gk = A.obj("gameplay_keep")
    M = oot_horse.mats(skel, bind_tab)
    segs = {6: data, 4: gk, 8: data[sym[eyes]:sym[eyes] + 0x1000], 9: data[sym[mouth]:sym[mouth] + 0x1000]}
    tris, wts, allm = [], [], {}
    mlimb = [li for li in range(skel.n) if skel.seg[li]]      # Matrix k (Segment 0x0D) = k-tes Glied mit Modell
    for li in range(skel.n):
        if not skel.seg[li] or li == SHEATH_LIMB:
            continue
        g = Gfx(Segs(segs))
        g.reset(); g.run(skel.seg[li])
        for (k, vs), tm in zip(g.tris, g.tri_mtx):
            vs2, ww = [], []
            for (p, uv, col), mk in zip(vs, tm):
                l = mlimb[mk] if mk is not None and mk < len(mlimb) else li
                vs2.append((tuple((M[l] @ np.append(np.array(p, float), 1))[:3]), uv, col)); ww.append([(l, 1.0)])
            tris.append((k, vs2)); wts.append(ww)
        allm.update(g.mats)
    # linke Hand als eigenes Teil (wird mit Schwert in der Faust ausgeblendet)
    import copy as _copy
    def retag(k, part):
        k2 = ("part", part) + (tuple(k) if isinstance(k, tuple) else (k,))
        if k2 not in allm:
            m2 = _copy.copy(allm[k]); m2.part = part; m2.key = k2; allm[k2] = m2
        return k2
    for i, ((k, vs), ww) in enumerate(zip(tris, wts)):
        if all(w[0][0] == HAND_L_LIMB for w in ww):
            tris[i] = (retag(k, "hand_l"), vs)
        elif all(w[0][0] == HAND_R_LIMB for w in ww):
            tris[i] = (retag(k, "hand_r"), vs)
    for part, li, dls in (PARTS if parts is None else parts):
      for dl, loc in (dls if isinstance(dls, list) else [(dls, None)]):
        g = Gfx(Segs({6: data, 4: gk, 8: bytes(0x1000)}))
        g.reset(); g.run(0x06000000 | sym[dl])
        L = stick_matrix() if loc == "stick" else np.eye(4)
        for k, vs in g.tris:
            if k not in allm and k in g.mats:
                allm[k] = g.mats[k]
            m0 = g.mats.get(k)
            a = k[0] if isinstance(k, tuple) else k
            if isinstance(a, int) and (a >> 24) & 0xFF == 8 and m0 is not None:
                m0 = _copy.copy(m0); m0.metal = True; allm[k] = m0       # Klinge: Spiegel-Textur (Segment 8) -> Stahl
            vs2 = [(tuple((M[li] @ L @ np.append(np.array(p, float), 1))[:3]), uv, col) for p, uv, col in vs]
            tris.append((retag(k, part), vs2)); wts.append([[(li, 1.0)]] * 3)
    # Texturen mit Primfarbe einfaerben (Combiner TEXEL0 * PRIM), wie bei Epona
    for m in allm.values():
        if m.rgba is None or tuple(m.prim[:3]) == (255, 255, 255):
            continue
        a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(-1, 4).astype(float)
        a[:, :3] = a[:, :3] * np.array(m.prim[:3], float) / 255.0
        m.rgba = np.clip(a, 0, 255).astype(np.uint8).tobytes()
    return tris, wts, allm


# ---------------- Remaster: Valheim-Optik ----------------
# Valheims Kleidung ist handgemalt: gefilzte Wolle, gedeckte Farben, Leder mit Maserung, weich schattiert.
# Die Original-Texturen liefern Falten/Muster (Helligkeit), Farben + Struktur kommen von hier. Weiche Normalen statt Facetten.
UP = 8
PAL = {   # (dunkel, hell) in Valheim-typisch gedeckten Toenen; dunkle Enden angehoben (Wunsch: keine schwarzen Stellen), helle einen Tick dunkler
    "cloth": ((50, 76, 38), (98, 137, 58)),
    "leather": ((70, 46, 30), (130, 86, 51)),
    "linen": ((120, 112, 94), (228, 220, 198)),
    "skin": ((160, 106, 70), (232, 192, 148)),
    "hair": ((142, 87, 27), (236, 193, 98)),
    "bronze": ((86, 62, 34), (201, 162, 90)),
    "metal": ((92, 98, 108), (226, 230, 236)),
}
GLOSS = {"cloth": 0.04, "leather": 0.14, "skin": 0.1, "hair": 0.1, "bronze": 0.35, "metal": 0.55}
NRM = {"cloth": 0.45, "leather": 1.0, "skin": 0.25, "hair": 0.6, "bronze": 0.5, "metal": 0.2}
# Erwachsener Link (Startbildschirm): Tunika/Muetze gruen, weisse Hose + Unterhemd, braune Stulpen/Stiefel; Schild,
# Scheide und Schwert behalten ihre Original-Farben (KEEP_TEX)
KIND_ADULT = {"gLinkAdultTunicTex": "cloth", "gLinkAdultSkirtAndHatTex": "cloth", "gLinkAdultArmOutSleeveTex": "cloth",
              "gLinkAdultCollarTex": "linen", "gLinkAdultLegTex": "linen", "gLinkAdultNeckTex": "skin",
              "gLinkAdultBootTex": "leather", "gLinkAdultBoot2Tex": "leather", "gLinkAdultBootFarTex": "leather",
              "gLinkAdultBootBuckleTex": "bronze", "gLinkAdultBeltTex": "leather", "gLinkAdultBeltClaspTex": "bronze",
              "gLinkAdultDefaultGauntlet1Tex": "leather", "gLinkAdultDefaultGauntlet2Tex": "leather",
              "gLinkAdultArmGauntletTex": "leather", "gLinkAdultArmOutUpperGauntletTex": "leather",
              "gLinkAdultGauntletPlate1Tex": "metal", "gLinkAdultGauntletPlate2Tex": "metal",
              "gLinkAdultHandTex": "skin", "gLinkAdultClosedHandThumbTex": "skin", "gLinkAdultClosedHandSideTex": "skin",
              "gLinkAdultEarTex": "skin", "gLinkAdultNoseTex": "skin", "gLinkAdultNose2Tex": "skin", "gLinkAdultEarringTex": "metal"}
KEEP_ADULT = {"gLinkAdultHylianShieldBackTex", "gLinkAdultShieldHandleTex", "gLinkAdultMasterSheathTex", "gLinkAdultMasterSheathBandTex",
              "gLinkAdultSwordPommelTex", "gLinkAdultSwordGuardTex", "gLinkAdultSwordEmblemTex"}
ADULT_PARTS = [("back", 19, "gLinkAdultBackHylianShieldSwordInSheathNearDL")]     # Master-Schwert + Hylia-Schild auf dem Ruecken

KIND = {"gLinkChildWaistTex": "cloth", "gLinkTunic1Tex": "cloth", "gLinkTunic2Tex": "cloth", "gLinkTunic3Tex": "cloth",
        "gLinkChildBeltTex": "leather", "gLinkChildBeltClaspTex": "bronze", "gLinkChildBootTex": "leather",
        "gLinkChildLowerBootTex": "leather", "gLinkChildEarTex": "skin", "gLinkChildHandTex": "skin", "gLinkChildNoseTex": "skin",
        "gLinkHairTex": "hair", "none": "cloth", "gLinkChildKokiriSwordSheathTex": "leather", "gLinkChildSwordJewelTex": "bronze"}


def _noise(n, beta, seed, aniso=1.0):
    """kachelbares Rauschen (1/f^beta), Mittel 0, Std 1; aniso > 1 = Fasern in x-Richtung"""
    rng = np.random.default_rng(seed)
    f = np.fft.fft2(rng.standard_normal((n, n)))
    ky = np.fft.fftfreq(n)[:, None]; kx = np.fft.fftfreq(n)[None, :] / aniso
    k = np.sqrt(kx * kx + ky * ky); k[0, 0] = 1.0
    r = np.real(np.fft.ifft2(f / k ** beta))
    return (r - r.mean()) / (r.std() + 1e-9)


def _detail(kind, n):
    """Helligkeits-Relief um 1: Wolle (Filz + feine Fasern), Leder (Narben + Kratzer), Haut (fast glatt), Haar (Straehnen)"""
    if kind == "cloth":
        d = 0.10 * _noise(n, 1.0, 1) + 0.06 * _noise(n, 0.4, 2) + 0.05 * _noise(n, 0.6, 3, aniso=6.0)
        yy, xx = np.mgrid[0:n, 0:n]
        d += 0.025 * np.sin(xx * 2 * np.pi / 4.0) * np.sin(yy * 2 * np.pi / 4.0)     # Webmuster
    elif kind == "cloth_n":         # Relief ohne das feine Webmuster (das flimmert aus der Entfernung als Querstreifen)
        d = 0.10 * _noise(n, 1.0, 1) + 0.06 * _noise(n, 0.4, 2)
    elif kind == "leather":
        d = 0.12 * _noise(n, 1.1, 4) + 0.08 * np.abs(_noise(n, 0.5, 5)) - 0.05 + 0.05 * _noise(n, 0.5, 6, aniso=8.0)
    elif kind == "hair":
        d = 0.10 * _noise(n, 0.7, 7, aniso=0.08) + 0.03 * _noise(n, 1.0, 8)
    elif kind == "bronze":
        d = 0.08 * _noise(n, 1.2, 9)
    else:
        d = 0.035 * _noise(n, 1.4, 10)
    return 1.0 + d


def _smooth_up(img, up):
    from PIL import Image
    im = Image.fromarray(np.clip(img * 255, 0, 255).astype(np.uint8))
    return np.asarray(im.resize((im.width * up, im.height * up), Image.BICUBIC), float) / 255.0


LIFT = 0.17      # Schattenseite nie schwarz (Valheims Figuren haben weiches Umgebungslicht): Eigenleuchten = Textur * LIFT


def remaster_tex(m, kind):
    """Material m (oot_gfx.Material) -> Valheim-Optik (in place); groesser, weich, gedeckte Farben, Struktur"""
    w, h = m.w, m.h
    if m.rgba is None:
        w = h = 8
        a = np.ones((h, w, 4)); a[..., :3] = 0.6
        m.wrap_s = m.wrap_t = 10497
    else:
        a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(h, w, 4).astype(float) / 255.0
    L = a[..., :3].mean(2)
    lo, hi = np.percentile(L, 3), np.percentile(L, 97)
    L = np.clip((L - lo) / max(hi - lo, 0.08), 0, 1)
    if kind == "skin":
        L = 0.55 + 0.45 * L                       # Haut: kaum Muster, nur sanfte Schattierung
    if kind == "hair":
        L = 0.4 + 0.6 * L                         # Haar: hell-blond, Straehnen statt dunkler Flecken
    if m.rgba is None:
        L = np.full_like(L, 0.95)                 # Kragen/Unterhemd: heller (im Original hellgruen)
    Lb = _smooth_up(L, UP)
    n = Lb.shape[0]
    det = _detail(kind, max(Lb.shape))[:Lb.shape[0], :Lb.shape[1]]
    dk, lt = (np.array(c, float) / 255.0 for c in PAL[kind])
    t = np.clip(Lb ** 0.9, 0, 1)[..., None]
    col = dk * (1 - t) + lt * t
    col = np.clip(col * det[..., None], 0, 1)
    al = _smooth_up(a[..., 3], UP) if m.rgba is not None else np.ones(Lb.shape)
    out = np.concatenate([col, al[..., None]], 2)
    m.rgba = (out * 255 + 0.5).astype(np.uint8).tobytes(); m.w, m.h = Lb.shape[1], Lb.shape[0]
    m.prim = (255, 255, 255, 255)
    # matt wie Valheim-Kleidung (Shader-Standard glaenzt nass) + Normal-Map aus der Struktur (Licht bricht im Filz/Leder)
    m.gloss = GLOSS.get(kind, 0.08)
    m.lift = LIFT
    det_n = _detail(kind + "_n", max(Lb.shape))[:Lb.shape[0], :Lb.shape[1]] if kind == "cloth" else det
    hgt = (det_n - 1.0) * NRM.get(kind, 1.0) + Lb * 0.15
    gy, gx = np.gradient(hgt)
    s_ = 6.0
    nv = np.dstack([-gx * s_, gy * s_, np.ones_like(hgt)])
    nv /= np.linalg.norm(nv, axis=2, keepdims=True)
    nrm = np.concatenate([nv * 0.5 + 0.5, np.ones(hgt.shape)[..., None]], 2)
    m.nrm = ((nrm * 255 + 0.5).astype(np.uint8).tobytes(), hgt.shape[1], hgt.shape[0])


def remaster_face(m):
    """Augen/Mund/Nase: Original-Zeichnung behalten, nur weich vergroessert und Farben leicht gedeckt"""
    a = np.frombuffer(bytes(m.rgba), np.uint8).reshape(m.h, m.w, 4).astype(float) / 255.0
    rgb = a[..., :3]; g = rgb.mean(2, keepdims=True)
    rgb = g + (rgb - g) * 0.85
    # Hautflaechen (hell, warm, wenig blau) auf denselben Hautton wie Arme/Beine ziehen
    r_, g_, b_ = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    skin = (r_ > 0.55) & (r_ > b_ + 0.12) & (g_ > 0.45)
    if skin.any():
        tgt = np.array(PAL["skin"][1], float) / 255.0 * 0.93
        med = np.median(rgb[skin], axis=0)
        k = tgt / np.maximum(med, 1e-3)
        rgb = np.where(skin[..., None], np.clip(rgb * k, 0, 1), rgb)
    big = _smooth_up(np.concatenate([rgb, a[..., 3:]], 2), 4)
    m.rgba = (np.clip(big, 0, 1) * 255 + 0.5).astype(np.uint8).tobytes(); m.w, m.h = big.shape[1], big.shape[0]
    m.gloss = 0.1
    m.lift = LIFT


def subdivide(tris, wts, k=0.6, sharp=()):
    """Einmal unterteilen (jedes Dreieck -> 4) mit Loop-Glaettung (Anteil k, sonst linear) auf der verschweissten Form:
    runder (Kopf, Muetze, Arme), Silhouette bleibt; Gelenkgewichte an Kanten gemischt -> weichere Gelenke."""
    import collections
    key = lambda p: tuple(np.round(np.asarray(p, float), 0))
    pos = {}; nb = collections.defaultdict(set); edges = collections.defaultdict(list); wsum = collections.defaultdict(lambda: collections.Counter())
    for (_, vs), ww in zip(tris, wts):
        ks = [key(p) for p, _, _ in vs]
        for i in range(3):
            pos[ks[i]] = np.asarray(vs[i][0], float)
            for l, x in ww[i]:
                wsum[ks[i]][l] += x
            a, b = ks[i], ks[(i + 1) % 3]
            if a != b:
                nb[a].add(b); nb[b].add(a)
                edges[tuple(sorted((a, b)))].append(ks[(i + 2) % 3])
    bnd = {e for e, opp in edges.items() if len(opp) == 1}
    hard = set()                                   # Ecken der "scharfen" Materialien (Haar-Spitzen) bleiben, wo sie sind
    for (mk, vs) in tris:
        if mk in sharp:
            hard.update(key(p) for p, _, _ in vs)
    bvert = collections.defaultdict(list)
    for a, b in bnd:
        bvert[a].append(b); bvert[b].append(a)
    newv = {}
    for v, p in pos.items():
        if v in bvert:
            q = 0.75 * p + 0.125 * sum(pos[x] for x in bvert[v][:2]) if len(bvert[v]) >= 2 else p
        else:
            n = len(nb[v])
            if n < 3:
                q = p
            else:
                beta = (5.0 / 8 - (3.0 / 8 + 0.25 * np.cos(2 * np.pi / n)) ** 2) / n
                q = (1 - n * beta) * p + beta * sum(pos[x] for x in nb[v])
        newv[v] = p if v in hard else (1 - k) * p + k * q
    def emid(a, b):
        e = tuple(sorted((a, b))); lin = (pos[a] + pos[b]) / 2
        opp = edges.get(e, [])
        if e in bnd or len(opp) != 2 or (a in hard and b in hard):
            return lin
        loop = 0.375 * (pos[a] + pos[b]) + 0.125 * (pos[opp[0]] + pos[opp[1]])
        return (1 - k) * lin + k * loop
    def wmix(a, b):
        c = collections.Counter()
        for kk in (a, b):
            t = sum(wsum[kk].values()) or 1.0
            for l, x in wsum[kk].items():
                c[l] += x / t * 0.5
        return list(c.items())
    def wone(a):
        t = sum(wsum[a].values()) or 1.0
        return [(l, x / t) for l, x in wsum[a].items()]
    out, ow = [], []
    for (mk, vs), ww in zip(tris, wts):
        ks = [key(p) for p, _, _ in vs]
        P = [newv[x] for x in ks]; U = [np.array(uv, float) for _, uv, _ in vs]; C = [c for _, _, c in vs]
        M = [emid(ks[i], ks[(i + 1) % 3]) for i in range(3)]
        UM = [(U[i] + U[(i + 1) % 3]) / 2 for i in range(3)]
        WV = [ww[i] for i in range(3)]
        WM = [wmix(ks[i], ks[(i + 1) % 3]) if len(set(l for l, _ in ww[i]) | set(l for l, _ in ww[(i + 1) % 3])) > 1 else ww[i] for i in range(3)]
        V = lambda p, u, c: (tuple(float(x) for x in p), tuple(float(x) for x in u), c)
        for tri, wt in (((V(P[0], U[0], C[0]), V(M[0], UM[0], C[0]), V(M[2], UM[2], C[0])), (WV[0], WM[0], WM[2])),
                        ((V(M[0], UM[0], C[0]), V(P[1], U[1], C[1]), V(M[1], UM[1], C[1])), (WM[0], WV[1], WM[1])),
                        ((V(M[2], UM[2], C[2]), V(M[1], UM[1], C[1]), V(P[2], U[2], C[2])), (WM[2], WM[1], WV[2])),
                        ((V(M[0], UM[0], C[0]), V(M[1], UM[1], C[1]), V(M[2], UM[2], C[2])), (WM[0], WM[1], WM[2]))):
            out.append((mk, list(tri))); ow.append(list(wt))
    return out, ow


def smooth_normals(tris, crease=75.0):
    """Normalen je Eckpunkt (gemittelt ueber gleiche Position, auch ueber Gliedgrenzen), harte Kanten ab crease Grad bleiben"""
    import collections
    acc = collections.defaultdict(lambda: np.zeros(3))
    fn = []
    for _, vs in tris:
        P = [np.array(p, float) for p, _, _ in vs]
        n = np.cross(P[1] - P[0], P[2] - P[0]); a = np.linalg.norm(n)
        n = n / a if a > 1e-9 else np.array([0.0, 1.0, 0.0])
        fn.append(n)
        for p in P:
            acc[tuple(np.round(p, 0))] += n * max(a, 1e-6)
    cosc = np.cos(np.radians(crease))
    out = []
    for (_, vs), n in zip(tris, fn):
        row = []
        for p, _, _ in vs:
            v = acc[tuple(np.round(np.array(p, float), 0))]
            v = v / (np.linalg.norm(v) or 1.0)
            row.append(v if v @ n > cosc else n)
        out.append(row)
    return out


def remaster(tris, mats, sym):
    """Materialien umfaerben/strukturieren; Dreiecke ohne Textur (Kragen) bekommen Box-UVs"""
    inv = {}
    for k, v in sym.items():
        inv.setdefault(("o", v), k)
    gk = {v: k for k, v in A.symbols("gameplay_keep").items()}
    names = {}
    for key, m in mats.items():
        a = key[0] if isinstance(key, tuple) else key
        if isinstance(key, tuple) and len(key) > 2 and key[0] == "part":
            a = key[2]
        if getattr(m, "metal", False):
            if m.rgba is None:
                m.rgba = bytes([200, 200, 205, 255] * 64); m.w = m.h = 8
            names[key] = "metal"; remaster_tex(m, "metal"); continue
        if isinstance(a, int):
            seg, off = (a >> 24) & 0xFF, a & 0xFFFFFF
            nm = gk.get(off) if seg == 4 else inv.get(("o", off), "seg%d" % seg) if seg == 6 else "seg%d" % seg
        else:
            nm = str(a)
        names[key] = nm
        if seg_face(nm):
            remaster_face(m)
        elif nm in KEEP_TEX or nm in KEEP_ADULT:
            m.gloss = 0.06                     # Deku-Schild: Original-Holz mit Spirale behalten (nur matt)
        else:
            remaster_tex(m, KIND.get(nm, KIND_ADULT.get(nm, "cloth")))
    # Kragen ohne Textur: Box-Projektion
    for i, (k, vs) in enumerate(tris):
        if names.get(k) == "none":
            P = np.array([p for p, _, _ in vs], float)
            n = np.cross(P[1] - P[0], P[2] - P[0]); ax = int(np.argmax(np.abs(n)))
            a_, b_ = [(1, 2), (0, 2), (0, 1)][ax]
            tris[i] = (k, [(p, (p[a_] / 400.0, p[b_] / 400.0), c) for p, _, c in vs])
    return names


def seg_face(nm):
    return nm in ("seg8", "seg9", "gLinkChildEyesOpenTex", "gLinkChildMouthClosedTex")


def export(out_dir, remastered=True):
    os.makedirs(out_dir, exist_ok=True)
    data = A.obj("object_link_child"); sym = A.symbols("object_link_child")
    skel = StdSkel(data, sym["gLinkChildSkel"])
    clips = {k: clip_tabs(v, skel.n) for k, v in CLIPS.items()}
    bind = clips["idle"][0]
    tris, wts, m = build_mesh(data, skel, bind, sym)
    normals = None
    if remastered:
        names = remaster(tris, m, sym)
        tris, wts = subdivide(tris, wts, sharp={k for k, n in names.items() if n == "gLinkHairTex" or (isinstance(k, tuple) and k[:1] == ("part",) and k[1] not in ("hand_l", "hand_r"))})
        normals = smooth_normals(tris)
    path = os.path.join(out_dir, "link_child.glb")
    rep = oot_horse.write_glb(path, skel, bind, None, FPS, tris, wts, m, name="link", clips=clips, valheimify_tex=False, nearest=False,
                              normals=normals)
    rep["clips"] = {k: len(v) for k, v in clips.items()}
    return rep


def export_cs(path, tabs, fps, root_scale=ROOT_SCALE, eyes="gLinkChildEyesOpenTex", keep_parts=("hand_l", "hand_r")):
    """Kind-Link (dasselbe Remaster-Modell wie im Spiel) als Cutscene-Requisite: ein Clip "cs" mit einer Tabelle je
    Cutscene-Bild (oot_bones.Player-Tabellen, Wurzel wie im Spiel x root_scale). Nur die Teile aus keep_parts (am Anfang des
    Spiels: leere Haende, kein Schwert/Schild/Scheide)."""
    data = A.obj("object_link_child"); sym = A.symbols("object_link_child")
    skel = StdSkel(data, sym["gLinkChildSkel"])
    bind = clip_tabs(CLIPS["idle"], skel.n)[0]
    tris, wts, m = build_mesh(data, skel, bind, sym, eyes=eyes)
    names = remaster(tris, m, sym)
    tris, wts = subdivide(tris, wts, sharp={k for k, n in names.items() if n == "gLinkHairTex" or (isinstance(k, tuple) and k[:1] == ("part",) and k[1] not in ("hand_l", "hand_r"))})
    for mm in m.values():
        if getattr(mm, "lift", None):
            mm.lift = mm.lift * 0.35                     # Cutscenes haben eigenes Licht: kaum Eigenleuchten (sonst ueberstrahlt nachts)
    keep = [i for i, (k, _) in enumerate(tris) if not (isinstance(k, tuple) and k[:1] == ("part",) and k[1] not in keep_parts)]
    tris = [tris[i] for i in keep]; wts = [wts[i] for i in keep]
    normals = smooth_normals(tris)
    ct = []
    for t in tabs:
        t = np.array(t, np.int64).copy()
        t[0] = np.round(t[0] * root_scale)
        ct.append(t)
    rep = oot_horse.write_glb(path, skel, bind, ct, fps, tris, wts, m, name="link", valheimify_tex=False, nearest=False, normals=normals)
    return rep


def export_cs_adult(path, tabs, fps):
    """Erwachsener Link (Startbildschirm, reitet Epona) aus object_link_boy: gleiche Remaster-Optik wie das Kind,
    Master-Schwert + Hylia-Schild auf dem Ruecken, Clip "cs" (Wurzel unveraendert: die Reit-Animation hebt die Huefte in den Sattel)."""
    data = A.obj("object_link_boy"); sym = A.symbols("object_link_boy")
    skel = StdSkel(data, sym["gLinkAdultSkel"])
    bind = np.array(A.LinkAnim("gPlayerAnim_" + CLIPS["idle"], skel.n).table(0), np.int64)
    tris, wts, m = build_mesh(data, skel, bind, sym, eyes="gLinkAdultEyesOpenTex", mouth="gLinkAdultMouthClosedTex", parts=ADULT_PARTS)
    names = remaster(tris, m, sym)
    tris, wts = subdivide(tris, wts, sharp={k for k, n in names.items() if n == "gLinkHairTex" or (isinstance(k, tuple) and k[:1] == ("part",) and k[1] not in ("hand_l", "hand_r"))})
    for mm in m.values():
        if getattr(mm, "lift", None):
            mm.lift = mm.lift * 0.35
    normals = smooth_normals(tris)
    ct = [np.array(t, np.int64).copy() for t in tabs]
    return oot_horse.write_glb(path, skel, bind, ct, fps, tris, wts, m, name="link_adult", valheimify_tex=False, nearest=False, normals=normals)


def cs_prop(model, pos_v, yaw_oot, n, vis=None):
    """Requisiten-Eintrag fuer eine Cutscene-Spur (src/CutsceneTrack.cs): pos_v = Valheim-Positionen je Bild,
    yaw_oot = OoT-Blickrichtung (Binaerwinkel) je Bild -> fwd (OoT (sin, 0, cos) -> Valheim (sin, 0, -cos))."""
    import math
    import oot_pack
    fwd = []
    for y in yaw_oot:
        a = y / 65536.0 * 2 * math.pi
        fwd += [round(math.sin(a), 5), 0.0, round(-math.cos(a), 5)]
    return {"model": model, "clip": "cs", "scale": round(0.01 * oot_pack.S, 6), "pos": [round(float(v), 4) for v in pos_v],
            "fwd": fwd, "up": [0.0, 1.0, 0.0] * n, "vis": vis if vis is not None else [1] * n}


if __name__ == "__main__":
    print(json.dumps(export(sys.argv[1]), indent=1))
