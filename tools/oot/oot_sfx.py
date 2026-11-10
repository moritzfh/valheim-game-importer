"""Original-Soundeffekte aus dem OoT-ROM rendern (Sequenz 0 = SFX-Sequenz, Soundfonts 0/1, extrahierte Samples).

Kleiner Nachbau des Audio-Sequenzers (src/audio/internal/seqplayer.c): Ein Kanal startet im Bank-Handler wie im Spiel
(IO-Port 0 = Start, IO-Port 4/5 = Index), springt per dyncall in das SFX-Skript, Layer spielen Noten mit Instrumenten /
Effekten / Drums. Selbstmodifizierender Code (stseq, stptrtoseq) laeuft auf echten Bytes.
Synthese: Sample-Resampling mit Tuning, Portamento, Huellkurve (ADSR-Punkte, Release), Lautstaerke = Velocity^2.

  render(name, seed=0) -> (float32 Mono, 32000 Hz)
  export(out_dir, names) -> {name: "sfx/<name>.ogg"}
"""
import os
import random
import re
import struct
import subprocess
import wave
import xml.etree.ElementTree as ET

import numpy as np
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

OOT = OOT_DECOMP
AUD = OOT + "/extracted/gc-eu/assets/audio"
SEQ0 = OOT + "/assets/audio/sequences/seq_0.prg.seq"
SFXTAB = OOT + "/include/tables/sfx"
RATE = 32000
TICKS_PER_SEC = 120 * 48 / 60.0          # seq_0: tempo 120, 48 Ticks pro Schlag
UPDATES_PER_SEC = 180.0                    # 3 Audio-Updates pro Frame
ENV_UNIT = 1.0 / 240.0                     # Huellkurven-Verzoegerung in Sekunden (delay * ticksPerUpdate/4 Updates)

BANKS = {  # Bank -> (ID-Basis, Tabellen-Offset, Handler-Offset)
    "playerbank": (0x0800, 0x00E1, 0x008E),
    "itembank": (0x1800, 0x0EE3, 0x0EA0),
    "environmentbank": (0x2800, 0x16D5, 0x1680),
    "enemybank": (0x3800, 0x315E, 0x30F0),
    "systembank": (0x4800, 0x5729, 0x56F0),
    "ocarinabank": (0x5800, 0x5EE5, 0x5E9D),
    "voicebank": (0x6800, 0x5FF2, 0x5FA0),
}

PITCH = (["A0", "BF0", "B0"] + [n + str(o) for o in range(1, 11) for n in
                                ("C", "DF", "D", "EF", "E", "F", "GF", "G", "AF", "A", "BF", "B")])
PITCH_IDX = {n: i for i, n in enumerate(PITCH)}


def pitch_freq(n):
    return 2.0 ** ((n - 39) / 12.0)


# ---------------------------------------------------------------- Sequenz-Bytes
_seq_cache = None


def parse_seq(path):
    """Bytes einer disassemblierten Sequenz (Kommentare + .array/.envelope-Abschnitte) und ihre Soundfonts."""
    src = open(path).read().splitlines()
    fonts = [int(m) for m in re.findall(r'#include "Soundfont_(\d+)\.h"', "\n".join(src))]
    data = _parse_lines(src)
    return data, fonts


def load_seq0():
    global _seq_cache
    if _seq_cache:
        return _seq_cache
    src = open(SEQ0).read().splitlines()
    data = _parse_lines(src)
    names = {}
    for bank, (base, toff, _h) in BANKS.items():
        i = 0
        for l in open(os.path.join(SFXTAB, bank + "_table.h")):
            mm = re.search(r"DEFINE_SFX\(CHAN_([0-9A-F]+),\s*(\w+)", l)
            if mm:
                data[toff + 2 * i:toff + 2 * i + 2] = struct.pack(">H", int(mm.group(1), 16))
                names[mm.group(2)] = (bank, i)
                i += 1
    _seq_cache = (data, names)
    return _seq_cache


def _parse_lines(src):
    pat = re.compile(r'/\* 0x([0-9A-F]+) \[((?:0x[0-9A-F]{2} ?)+)\s*\] \*/')
    data = bytearray(0x10000)
    mode, off = None, 0
    for l in src:
        s = l.strip()
        m = pat.search(l)
        if m:
            o = int(m.group(1), 16)
            for i, b in enumerate(m.group(2).split()):
                data[o + i] = int(b, 16)
            mode = None
            continue
        if s.startswith(".array ARRAY_"):
            mode, off = "a", int(s.split("_")[-1], 16)
            continue
        if s.startswith(".envelope ENVELOPE_"):
            mode, off = "e", int(s.split("_")[-1], 16)
            continue
        if mode == "a" and s.startswith(".byte"):
            for v in s[5:].split("//")[0].split(","):
                v = v.strip()
                mm = re.match(r"SF\d+_(?:EFFECT|INST)_(\d+)", v)
                data[off] = (int(mm.group(1)) if mm else int(v, 0)) & 0xFF
                off += 1
        elif s.startswith(".") or s.startswith("#") or (s and s.endswith(":")):
            mode = None
        elif mode == "e" and s and not s.startswith("//"):
            p = s.split()
            if p[0] == "point":
                d, a = [int(x.strip(), 0) for x in s[5:].split(",")]
            elif p[0] == "hang":
                d, a = -1, 0
            elif p[0] == "goto":
                d, a = -2, int(p[1], 0)
            elif p[0] == "restart":
                d, a = -3, 0
            else:
                d, a = 0, 0
            data[off:off + 4] = struct.pack(">hh", d, a)
            off += 4
    return data


# ---------------------------------------------------------------- Soundfonts / Samples
_wav_cache, _font_cache, _bank_paths = {}, {}, {}


def sample_path(name, bank_xml):
    if bank_xml not in _bank_paths:
        r = ET.parse(bank_xml).getroot()
        _bank_paths[bank_xml] = {s.get("Name"): s.get("Path") for s in r.iter("Sample")}
    p = _bank_paths[bank_xml][name]
    p = p.replace("$(BUILD_DIR)", OOT + "/extracted/gc-eu").replace(".aifc", ".wav")
    return p


def load_wav(path):
    if path in _wav_cache:
        return _wav_cache[path]
    b = open(path, "rb").read()
    i, rate, data, unity, loop = 12, 16000, None, 60, None
    while i + 8 <= len(b):
        cid = b[i:i + 4]; sz = struct.unpack("<I", b[i + 4:i + 8])[0]; c = b[i + 8:i + 8 + sz]
        if cid == b"fmt ":
            rate = struct.unpack("<I", c[4:8])[0]
        elif cid == b"data":
            data = np.frombuffer(c, dtype="<i2").astype(np.float32) / 32768.0
        elif cid == b"smpl":
            unity = struct.unpack("<I", c[12:16])[0]
            nl = struct.unpack("<I", c[28:32])[0]
            if nl:
                _id, _t, ls, le, _f, cnt = struct.unpack("<6I", c[36:60])
                loop = (ls, le + 1, cnt)
        i += 8 + sz + (sz & 1)
    _wav_cache[path] = (data, rate, unity, loop)
    return _wav_cache[path]


class Tuned:
    def __init__(self, name, bank_xml, rate=None, base=None):
        self.path = sample_path(name, bank_xml)
        self.data, wr, unity, self.loop = load_wav(self.path)
        r = rate or wr
        b = PITCH_IDX[base] if base else unity - 21
        self.tuning = (r / RATE) * 2.0 ** ((39 - b) / 12.0)


class Font:
    def __init__(self, idx):
        r = ET.parse("%s/soundfonts/Soundfont_%d.xml" % (AUD, idx)).getroot()
        bank = r.get("SampleBank").replace("$(BUILD_DIR)", OOT + "/extracted/gc-eu")
        self.env = {}
        for e in r.iter("Envelope"):
            pts = []
            for p in e:
                if p.tag == "Point":
                    pts.append((int(p.get("Delay")), int(p.get("Arg"))))
                elif p.tag == "Disable":
                    pts.append((0, 0))
                elif p.tag == "Hang":
                    pts.append((-1, 0))
                elif p.tag == "Goto":
                    pts.append((-2, int(p.get("Index", "0"))))
                elif p.tag == "Restart":
                    pts.append((-3, 0))
            self.env[e.get("Name")] = (pts, int(e.get("Release", "0")))
        self.inst = {}
        for e in r.iter("Instrument"):
            g = e.get
            ins = {"env": self.env[g("Envelope")],
                   "lo": PITCH_IDX[g("RangeLo")] if g("RangeLo") else 0,
                   "hi": PITCH_IDX[g("RangeHi")] if g("RangeHi") else 127,
                   "s": Tuned(g("Sample"), bank, int(g("SampleRate")) if g("SampleRate") else None, g("BaseNote")),
                   "sl": Tuned(g("SampleLo"), bank, int(g("SampleRateLo")) if g("SampleRateLo") else None, g("BaseNoteLo")) if g("SampleLo") else None,
                   "sh": Tuned(g("SampleHi"), bank, int(g("SampleRateHi")) if g("SampleRateHi") else None, g("BaseNoteHi")) if g("SampleHi") else None}
            self.inst[int(g("ProgramNumber"))] = ins
        self.fx = [Tuned(e.get("Sample"), bank, int(e.get("SampleRate")) if e.get("SampleRate") else None, e.get("BaseNote"))
                   if e.get("Sample") else None for e in r.iter("Effect")]
        self.drum = {}
        for e in r.iter("Drum"):
            g = e.get
            if not g("Sample"):
                continue
            t = Tuned(g("Sample"), bank, None, g("BaseNote"))
            n0 = PITCH_IDX[g("Note") or g("NoteStart")]
            n1 = PITCH_IDX[g("NoteEnd")] if g("NoteEnd") else n0
            for n in range(n0, n1 + 1):
                self.drum[n] = (t, self.env[g("Envelope")])


def font(i):
    if i not in _font_cache:
        _font_cache[i] = Font(i)
    return _font_cache[i]


DEFAULT_ENV = ([(1, 32700), (1, 32700), (-1, 0)], 0)

# Synthetische Wellen (instr 0x80..): 64-Sample-Perioden aus src/audio/internal/data.c, Tuning 1.0465 (C4 -> ~523 Hz)
_WAVES = None


class Wave:
    def __init__(self, tab):
        self.data = tab; self.loop = (0, len(tab), 0xFFFFFFFF); self.tuning = 1.0465; self.path = "wave"


def synth_wave(iw):
    global _WAVES
    if _WAVES is None:
        src = open(OOT + "/src/audio/internal/data.c").read()
        names = ["gSawtoothWaveSample", "gTriangleWaveSample", "gSineWaveSample", "gSquareWaveSample", "gWhiteNoiseSample",
                 "D_8012EA90", "gEighthPulseWaveSample", "gQuarterPulseWaveSample", "gQuarterPulseWaveSample"]
        _WAVES = []
        for nm in names:
            m = re.search(r"s16 %s\[\] = \{(.*?)\};" % nm, src, re.S)
            vals = [int(v) for v in re.findall(r"-?\d+", re.sub(r"//[^\n]*", "", m.group(1)))]
            _WAVES.append(Wave(np.array(vals[:64], dtype=np.float32) / 32768.0))
    k = iw - 0x80
    return _WAVES[k] if 0 <= k < len(_WAVES) else _WAVES[0]


def decay_seconds(idx):
    if idx == 0:
        return 1e9
    if idx >= 251:
        s = {255: 0.25, 254: 0.33, 253: 0.5, 252: 0.66, 251: 0.75}[idx]
    elif idx >= 128:
        s = 251 - idx
    elif idx >= 16:
        s = 4 * (143 - idx)
    else:
        s = 60 * (23 - idx)
    return s / 60.0


# ---------------------------------------------------------------- Sequenzer
ARGS = {}  # 0xB0..0xFF: Liste ('u8'|'s16')
_spec = ["s16", "", "s16", "u8", "", "", "", "s16", "u8", "u8", "u8", "u8 s16", "s16", "s16 s16", "", "",
         "", "u8", "s16", "", "", "", "u8", "u8 s16", "u8", "u8", "u8", "s16", "u8", "u8", "s16", "s16",
         "u8", "u8", "u8", "u8", "u8", "u8", "u8", "u8", "u8", "u8", "s16", "u8", "u8", "u8", "s16", "u8",
         "u8", "u8 u8 u8", "u8 u8 u8", "u8", "", "u8", "u8", "s16", "u8 u8 u8", "u8", "", "u8 u8", "", "u8", "u8", "s16 u8",
         "", "u8", "u8", "u8", "u8", "s16", "", "", "u8", "s16", "s16", "s16", "s16", "", "", ""]
for k, v in enumerate(_spec):
    ARGS[0xB0 + k] = v.split()


def s8(v):
    v &= 0xFF
    return v - 256 if v >= 128 else v


class St:
    def __init__(self, pc):
        self.pc, self.stack, self.loops, self.value = pc, [], [], 0


class Note:
    pass


class Layer:
    def __init__(self, ch, pc):
        self.ch, self.st = ch, St(pc)
        self.env = ch.env
        self.decay_idx = 0
        self.enabled, self.finished, self.muted = True, False, False
        self.legato = False
        self.porta_mode, self.porta_target, self.porta_time = 0, 0, 0
        self.gate, self.trans, self.delay, self.gate_delay = 0x80, 0, 0, 0
        self.inst_or_wave, self.inst = 0xFF, None
        self.bend, self.vel = 1.0, 1.0
        self.short_delay, self.last_delay = 0, 0
        self.note = None


class Chan:
    def __init__(self, sim, pc, io):
        self.sim, self.st = sim, St(pc)
        self.io = list(io)
        self.font, self.inst_or_wave, self.inst, self.has_inst = sim.font0, 0xFF, None, False
        self.env, self.decay_idx = DEFAULT_ENV, 0xF0
        self.trans, self.large, self.vol, self.vol_scale, self.freq = 0, False, 1.0, 1.0, 1.0
        self.delay, self.enabled = 0, True
        self.layers = [None] * 4
        self.dyn, self.ptr = 0, 0


SHORT_VEL = [12, 25, 38, 51, 57, 64, 71, 76, 83, 89, 96, 102, 109, 115, 121, 127]
SHORT_GATE = [229, 203, 177, 151, 139, 126, 113, 100, 87, 74, 61, 48, 36, 23, 10, 0]


class Sim:
    def __init__(self, data, seed, font0=0):
        self.d = bytearray(data)
        self.rng = random.Random(seed)
        self.notes = []
        self.tick = 0
        self.time = 0.0
        self.tps = TICKS_PER_SEC
        self.seq_trans, self.seq_vol = 0, 1.0
        self.short_vel, self.short_gate = list(SHORT_VEL), list(SHORT_GATE)
        self.font0 = font0
        self.chans = [None] * 16
        self.others = [[-1] * 8 for _ in range(16)]

    # --- Lesen
    def u8(self, st):
        v = self.d[st.pc]; st.pc += 1
        return v

    def s16(self, st):
        v = struct.unpack(">h", bytes(self.d[st.pc:st.pc + 2]))[0]; st.pc += 2
        return v

    def var(self, st):
        v = self.u8(st)
        if v & 0x80:
            v = ((v << 8) & 0x7F00) | self.u8(st)
        return v

    def flow(self, st, cmd, arg):
        if cmd == 0xFF:
            if not st.stack:
                return -1
            st.pc = st.stack.pop(); st.loops.pop()
        elif cmd == 0xFD:
            return self.var(st)
        elif cmd == 0xFE:
            return 1
        elif cmd == 0xFC:
            st.stack.append(st.pc); st.loops.append(0); st.pc = arg & 0xFFFF
        elif cmd == 0xF8:
            st.stack.append(st.pc); st.loops.append(arg)
        elif cmd == 0xF7:
            st.loops[-1] -= 1
            if st.loops[-1] != 0:
                st.pc = st.stack[-1]
            else:
                st.stack.pop(); st.loops.pop()
        elif cmd == 0xF6:
            st.stack.pop(); st.loops.pop()
        elif cmd in (0xF5, 0xF9, 0xFA, 0xFB):
            if cmd == 0xFB and getattr(st, "seq_main", False) and getattr(self, "jumps", None) is not None:
                self.jumps.append((self.time, arg & 0xFFFF))          # Schleife der Musik (render_loop)
            if cmd == 0xFA and st.value != 0:
                return 0
            if cmd == 0xF9 and st.value >= 0:
                return 0
            if cmd == 0xF5 and st.value < 0:
                return 0
            st.pc = arg & 0xFFFF
        elif cmd in (0xF2, 0xF3, 0xF4):
            if cmd == 0xF3 and st.value != 0:
                return 0
            if cmd == 0xF2 and st.value >= 0:
                return 0
            st.pc += s8(arg)
        return 0

    def flow_arg(self, st, cmd):
        a = ARGS[cmd]
        if not a:
            return 0
        return self.u8(st) if a[0] == "u8" else self.s16(st)

    # --- Noten
    def release(self, ly):
        n = ly.note
        if n is not None and n.t_rel is None:
            n.t_rel = self.time
        ly.note = None

    def layer_disable(self, ly):
        self.release(ly)
        ly.enabled, ly.finished = False, True

    def layer_tick(self, ly):
        if not ly.enabled:
            return
        if ly.delay > 1:
            ly.delay -= 1
            if not ly.muted and ly.delay <= ly.gate_delay:
                self.release(ly); ly.muted = True
            return
        if not ly.legato:
            self.release(ly)
        if ly.porta_mode & 0x7F in (1, 2):
            ly.porta_mode = 0
        ch, st = ly.ch, ly.st
        while True:   # Step 2
            cmd = self.u8(st)
            if cmd <= 0xC0:
                break
            if cmd >= 0xF2:
                if self.flow(st, cmd, self.flow_arg(st, cmd)) != 0:
                    self.layer_disable(ly)
                    return
                continue
            if cmd in (0xC1, 0xCA):
                v = self.u8(st)
                if cmd == 0xC1:
                    ly.vel = v * v / (127.0 * 127.0)
            elif cmd in (0xC9, 0xC2):
                v = self.u8(st)
                if cmd == 0xC9:
                    ly.gate = v
                else:
                    ly.trans = s8(v)
            elif cmd in (0xC4, 0xC5):
                ly.legato = cmd == 0xC4
                self.release(ly)
            elif cmd == 0xC3:
                ly.short_delay = self.var(st)
            elif cmd == 0xC6:
                v = self.u8(st)
                if v >= 0x7E:
                    ly.inst_or_wave = 1 if v == 0x7E else (0 if v == 0x7F else v)
                    ly.inst = None
                else:
                    ins = font(ch.font).inst.get(v)
                    if ins is None:
                        ly.inst_or_wave = 0xFF
                    else:
                        ly.inst, ly.inst_or_wave = ins, v + 2
                        ly.env, ly.decay_idx = ins["env"], ins["env"][1]
            elif cmd == 0xC7:
                ly.porta_mode = self.u8(st)
                t = self.u8(st) + ch.trans + ly.trans + self.seq_trans
                ly.porta_target = t if t < 0x80 else 0
                ly.porta_time = self.u8(st) if ly.porta_mode & 0x80 else self.var(st)
            elif cmd == 0xC8:
                ly.porta_mode = 0
            elif cmd == 0xCB:
                o = self.s16(st)
                ly.env = (self.read_env(o), ly.env[1])
                ly.decay_idx = self.u8(st)
            elif cmd == 0xCF:
                ly.decay_idx = self.u8(st)
            elif cmd == 0xCC:
                pass
            elif cmd == 0xCD:
                self.u8(st)
            elif cmd == 0xCE:
                v = self.u8(st)
                ly.bend = 2.0 ** (s8(v) / 768.0)
            elif cmd & 0xF0 == 0xD0:
                v = self.short_vel[cmd & 0xF]
                ly.vel = v * v / (127.0 * 127.0)
            elif cmd & 0xF0 == 0xE0:
                ly.gate = self.short_gate[cmd & 0xF]
        # Step 3
        if cmd == 0xC0:
            ly.delay = self.var(st); ly.muted = True
            return
        ly.muted = False
        if ch.large:
            k = cmd & 0xC0
            if k == 0x00:
                d = self.var(st); v = self.u8(st); ly.gate = self.u8(st); ly.last_delay = d
            elif k == 0x40:
                d = self.var(st); v = self.u8(st); ly.gate = 0; ly.last_delay = d
            else:
                d = ly.last_delay; v = self.u8(st); ly.gate = self.u8(st)
            v = min(max(v, 0), 127)
            ly.vel = v * v / (127.0 * 127.0)
        else:
            k = cmd & 0xC0
            d = self.var(st) if k == 0x00 else (ly.short_delay if k == 0x40 else ly.last_delay)
            if k == 0x00:
                ly.last_delay = d
        semi = cmd - (cmd & 0xC0)
        ly.delay = d
        ly.gate_delay = (ly.gate * d) >> 8
        # Step 4: Klang bestimmen
        iw = ly.inst_or_wave
        if iw == 0xFF:
            if not ch.has_inst:
                ly.muted = True; return
            iw = ch.inst_or_wave
        f = font(ch.font)
        env, dec = ly.env, ly.decay_idx
        porta = None
        if iw == 0:
            semi += ch.trans + ly.trans
            dr = f.drum.get(semi)
            if dr is None:
                ly.muted = True; return
            ts, env = dr[0], dr[1]
            dec = dr[1][1]
            fs = ts.tuning
        elif iw == 1:
            sid = (ly.trans << 6) + semi
            if sid < 0 or sid >= len(f.fx) or f.fx[sid] is None:
                ly.muted = True; return
            ts = f.fx[sid]; fs = ts.tuning
        else:
            semi += ch.trans + ly.trans + self.seq_trans
            if semi >= 0x80 or semi < 0:
                ly.muted = True; return
            ins = ch.inst if ly.inst_or_wave == 0xFF else ly.inst
            if ins is None and iw >= 0x80:
                w = synth_wave(iw)
                ins = {"lo": 0, "hi": 127, "s": w, "sl": None, "sh": None}
            if ins is None:
                ly.muted = True; return

            def pick(n):
                if n < ins["lo"] and ins["sl"]:
                    return ins["sl"]
                if n > ins["hi"] and ins["sh"]:
                    return ins["sh"]
                return ins["s"]
            if ly.porta_mode != 0:
                ts = pick(max(semi, ly.porta_target))
                a = pitch_freq(semi) * ts.tuning; b = pitch_freq(ly.porta_target) * ts.tuning
                m = ly.porta_mode & 0x7F
                f0, f1 = (b, a) if m in (1, 3, 5) else ((a, b) if m in (2, 4) else (a, a))
                if ly.porta_mode & 0x80:
                    dur = (max(d, 1) * ly.porta_time / 128.0) / self.tps
                else:
                    dur = ly.porta_time / 120.0
                porta = (f1 / f0, max(dur, 1e-3))
                fs = f0
                if m == 5:
                    ly.porta_target = semi
            else:
                ts = pick(semi); fs = pitch_freq(semi) * ts.tuning
        fs *= ly.bend
        if ly.delay == 0:
            ln = (ts.loop[1] if ts.loop else len(ts.data))
            ly.delay = int(ln / (RATE * fs * ch.freq) * self.tps) + 1
            ly.gate_delay = 0
        # Step 5: Note starten
        n = Note()
        n.t0, n.t_rel, n.ts, n.fs, n.porta = self.time, None, ts, fs * ch.freq, porta
        n.amp = ly.vel * ch.vol * ch.vol_scale * self.seq_vol
        n.env, n.decay = env, (dec if dec else ch.decay_idx)
        self.notes.append(n)
        ly.note = n

    def read_env(self, o):
        pts = []
        for i in range(32):
            d, a = struct.unpack(">hh", bytes(self.d[o + 4 * i:o + 4 * i + 4]))
            pts.append((d, a))
            if d in (0, -1, -2, -3):
                break
        return pts

    def chan_tick(self, ch):
        if not ch.enabled:
            return
        if ch.delay >= 2:
            ch.delay -= 1
        else:
            st = ch.st
            for _guard in range(4000):
                cmd = self.u8(st)
                if cmd >= 0xB0:
                    a = [self.u8(st) if t == "u8" else self.s16(st) for t in ARGS[cmd]]
                    if cmd >= 0xF2:
                        r = self.flow(st, cmd, a[0] if a else 0)
                        if r != 0:
                            if r == -1:
                                ch.enabled = False
                                for ly in ch.layers:
                                    if ly:
                                        self.layer_disable(ly)
                            else:
                                ch.delay = r
                            break
                        continue
                    self.chan_cmd(ch, cmd, a)
                    if not ch.enabled:
                        break
                    continue
                if cmd >= 0x70:
                    lo = cmd & 7
                    k = cmd & 0xF8
                    if k != 0x70 and lo >= 4:
                        lo = 0
                    if k == 0x80:
                        ly = ch.layers[lo]
                        st.value = (1 if ly.finished else 0) if ly else -1
                    elif k == 0x88:
                        o = self.s16(st) & 0xFFFF
                        self.set_layer(ch, lo, o)
                    elif k == 0x90:
                        if ch.layers[lo]:
                            self.layer_disable(ch.layers[lo]); ch.layers[lo] = None
                    elif k == 0x98:
                        if st.value != -1:
                            e = ch.dyn + 2 * st.value
                            self.set_layer(ch, lo, (self.d[e] << 8) | self.d[e + 1])
                    elif k == 0x70:
                        ch.io[lo] = st.value
                    elif k == 0x78:
                        t = self.s16(st)
                        self.set_layer(ch, lo, st.pc + t)
                    continue
                lo, k = cmd & 0xF, cmd & 0xF0
                if k == 0x00:
                    ch.delay = lo
                    break
                elif k == 0x10:
                    pass
                elif k == 0x60:
                    st.value = s8(ch.io[lo])
                    if lo < 2:
                        ch.io[lo] = -1
                elif k == 0x50:
                    st.value = s8(st.value - ch.io[lo])
                elif k == 0x20:
                    o = self.s16(st) & 0xFFFF
                    self.enable_chan(lo, o)
                elif k == 0x30:
                    c = self.u8(st); self.others[lo][c & 7] = st.value
                elif k == 0x40:
                    c = self.u8(st); st.value = s8(self.others[lo][c & 7])
        for ly in ch.layers:
            if ly:
                self.layer_tick(ly)

    def enable_chan(self, i, pc):
        # wie AudioSeq_SequenceChannelEnable: ein schon vorhandener Kanal behaelt Instrument,
        # Lautstaerke, Transposition usw. - nur Skript, Verzoegerung und Ebenen werden zurueckgesetzt
        c = self.chans[i]
        if c is None:
            c = Chan(self, pc, [-1] * 8)
            self.chans[i] = c
            self.others[i] = c.io
            return c
        for ly in c.layers:
            if ly:
                self.layer_disable(ly)
        c.layers = [None] * 4
        c.st = St(pc)
        c.delay, c.enabled = 0, True
        return c

    # --- Sequenz-Ebene (Musik / Fanfaren)
    def seq_tick(self, st):
        if self.seq_delay > 1:
            self.seq_delay -= 1
            return
        for _guard in range(2000):
            cmd = self.u8(st)
            if cmd >= 0xF2:
                r = self.flow(st, cmd, self.flow_arg(st, cmd))
                if r != 0:
                    if r == -1:
                        self.seq_on = False
                    else:
                        self.seq_delay = r
                    return
                continue
            if cmd >= 0xC0:
                if cmd == 0xF1 or cmd in (0xD5, 0xD3, 0xD0, 0xCE):
                    v = self.u8(st)
                    if cmd == 0xCE:
                        st.value = s8(self.rng.getrandbits(8) % (v or 256))
                elif cmd == 0xDF:
                    self.seq_trans = s8(self.u8(st))
                elif cmd == 0xDE:
                    self.seq_trans += s8(self.u8(st))
                elif cmd == 0xDD:
                    self.tps = max(1, self.u8(st)) * 48 / 60.0
                elif cmd == 0xDC:
                    self.u8(st)
                elif cmd == 0xDA:
                    self.u8(st); self.s16(st)
                elif cmd == 0xDB:
                    self.seq_vol = self.u8(st) / 127.0
                elif cmd == 0xD9:
                    self.u8(st)
                elif cmd in (0xD7, 0xD6, 0xC5):
                    self.s16(st)
                elif cmd in (0xD2, 0xD1):
                    o = self.s16(st) & 0xFFFF
                    tbl = list(self.d[o:o + 16])
                    if cmd == 0xD2:
                        self.short_vel = tbl
                    else:
                        self.short_gate = tbl
                elif cmd == 0xCD:
                    o = self.s16(st) & 0xFFFF
                    if st.value != -1:
                        e = o + 2 * st.value
                        st.stack.append(st.pc); st.loops.append(0)
                        st.pc = (self.d[e] << 8) | self.d[e + 1]
                elif cmd == 0xCC:
                    st.value = s8(self.u8(st))
                elif cmd == 0xC9:
                    st.value = s8(st.value & self.u8(st))
                elif cmd == 0xC8:
                    st.value = s8(st.value - self.u8(st))
                elif cmd == 0xC7:
                    v = self.u8(st); o = self.s16(st) & 0xFFFF
                    self.d[o] = (st.value + v) & 0xFF
                elif cmd == 0xC6:
                    self.seq_on = False
                    return
                elif cmd == 0xEF:
                    self.s16(st); self.u8(st)
                elif cmd == 0xC4:
                    self.u8(st); self.u8(st)
                continue
            lo, k = cmd & 0xF, cmd & 0xF0
            if k == 0x00:
                c = self.chans[lo]
                st.value = 0 if (c and c.enabled) else 1
            elif k == 0x40:
                c = self.chans[lo]
                if c:
                    c.enabled = False
                    for ly in c.layers:
                        if ly:
                            self.layer_disable(ly)
            elif k == 0x90:
                self.enable_chan(lo, self.s16(st) & 0xFFFF)
            elif k == 0xA0:
                t = self.s16(st)
                self.enable_chan(lo, st.pc + t)
            elif k == 0xB0:
                self.u8(st); self.s16(st)
            elif k == 0x60:
                self.u8(st); self.u8(st)

    def set_layer(self, ch, i, pc):
        if ch.layers[i]:
            self.release(ch.layers[i])
        ly = Layer(ch, pc)
        ch.layers[i] = ly

    def chan_cmd(self, ch, cmd, a):
        st = ch.st
        if cmd == 0xEA:
            ch.enabled = False
        elif cmd == 0xC2:
            ch.dyn = a[0] & 0xFFFF
        elif cmd == 0xC5:
            if st.value != -1:
                e = ch.dyn + 2 * st.value
                ch.dyn = (self.d[e] << 8) | self.d[e + 1]
        elif cmd in (0xEB, 0xC1):
            if cmd == 0xEB:
                ch.font = a[0]; a = a[1:]
            v = a[0] & 0xFF
            if v >= 0x80:
                ch.inst_or_wave, ch.inst = v, None
            elif v == 0x7F:
                ch.inst_or_wave, ch.inst = 0, None
            elif v == 0x7E:
                ch.inst_or_wave, ch.inst = 1, None
            else:
                ins = font(ch.font).inst.get(v)
                if ins is None:
                    ch.has_inst = False
                    return
                ch.inst, ch.inst_or_wave = ins, v + 2
                ch.env, ch.decay_idx = ins["env"], ins["env"][1]
            ch.has_inst = True
        elif cmd == 0xC3:
            ch.large = False
        elif cmd == 0xC4:
            ch.large = True
        elif cmd == 0xDF:
            ch.vol = (a[0] & 0xFF) / 127.0
        elif cmd == 0xE0:
            ch.vol_scale = (a[0] & 0xFF) / 128.0
        elif cmd == 0xDE:
            ch.freq = (a[0] & 0xFFFF) / 32768.0
        elif cmd == 0xD3:
            ch.freq = 2.0 ** (s8(a[0]) / 128.0)
        elif cmd == 0xEE:
            ch.freq = 2.0 ** (s8(a[0]) / 768.0)
        elif cmd == 0xDB:
            ch.trans = s8(a[0])
        elif cmd == 0xDA:
            ch.env = (self.read_env(a[0] & 0xFFFF), ch.env[1])
        elif cmd == 0xD9:
            ch.decay_idx = a[0] & 0xFF
        elif cmd == 0xC6:
            ch.font = a[0] & 0xFF
        elif cmd == 0xC7:
            o = a[1] & 0xFFFF
            self.d[o] = (st.value + a[0]) & 0xFF
        elif cmd in (0xC8, 0xCC, 0xC9):
            v = s8(a[0])
            st.value = s8(st.value - v) if cmd == 0xC8 else (v if cmd == 0xCC else s8(st.value & v))
        elif cmd == 0xCB:
            st.value = s8(self.d[(a[0] & 0xFFFF) + st.value])
        elif cmd == 0xCE:
            ch.ptr = a[0] & 0xFFFF
        elif cmd == 0xCF:
            o = a[0] & 0xFFFF
            self.d[o] = (ch.ptr >> 8) & 0xFF; self.d[o + 1] = ch.ptr & 0xFF
        elif cmd == 0xE4:
            if st.value != -1:
                e = ch.dyn + 2 * st.value
                st.stack.append(st.pc); st.loops.append(0)
                st.pc = (self.d[e] << 8) | self.d[e + 1]
        elif cmd == 0xE7:
            o = a[0] & 0xFFFF
            ch.trans = s8(self.d[o + 3])
        elif cmd == 0xE8:
            ch.trans = s8(self.u8(st))
            for _ in range(4):
                self.u8(st)
        elif cmd == 0xEC:
            ch.freq = 1.0
        elif cmd == 0xB2:
            o = (a[0] & 0xFFFF) + st.value * 2
            ch.ptr = (self.d[o] << 8) | self.d[o + 1]
        elif cmd == 0xB4:
            ch.dyn = ch.ptr
        elif cmd == 0xB5:
            o = ch.dyn + 2 * st.value
            ch.ptr = (self.d[o] << 8) | self.d[o + 1]
        elif cmd == 0xB6:
            st.value = s8(self.d[ch.dyn + st.value])
        elif cmd == 0xB7:
            r = self.rng.getrandbits(16)
            ch.ptr = r if a[0] == 0 else r % a[0]
        elif cmd == 0xB8:
            r = self.rng.getrandbits(16)
            st.value = s8(r if a[0] == 0 else r % a[0])
        elif cmd == 0xBD:
            r = self.rng.getrandbits(32)
            p = (r & 0xFFFF) if a[0] == 0 else r % (a[0] & 0xFFFF)
            p += a[1]
            ch.ptr = ((((p // 0x100) + 0x80) & 0xFF) << 8) | (p % 0x100)
        elif cmd == 0xBC:
            ch.ptr = (ch.ptr + a[0]) & 0xFFFF


# ---------------------------------------------------------------- Synthese
def env_curve(env, n_samp, t_rel_s, decay_s):
    """Huellkurve (Amplitude^2-Ziele wie im Spiel), Release linear auf 0."""
    pts = env[0] if isinstance(env, tuple) else env
    out = np.zeros(n_samp, dtype=np.float32)
    cur, i, t, idx = 0.0, 0, 0, 0
    seg = []
    guard = 0
    while t < n_samp and guard < 64:
        guard += 1
        if idx >= len(pts):
            break
        d, a = pts[idx]
        if d == 0:
            break
        if d == -1:
            break
        if d == -2:
            idx = a; continue
        if d == -3:
            idx = 0; continue
        tgt = (a / 32767.0) ** 2
        ln = max(1, int(d * ENV_UNIT * RATE))
        seg.append((t, ln, cur, tgt))
        t += ln; cur = tgt; idx += 1
    for (s, ln, c0, c1) in seg:
        e = min(n_samp, s + ln)
        if e > s:
            out[s:e] = np.linspace(c0, c1, ln, endpoint=False, dtype=np.float32)[:e - s]
    if t < n_samp:
        out[t:] = cur
    if t_rel_s is not None:
        r0 = int(t_rel_s * RATE)
        if r0 < n_samp:
            lvl = out[r0] if r0 < len(out) else cur
            fall = max(1, int(lvl * decay_s * RATE))
            ramp = lvl * np.clip(1.0 - np.arange(n_samp - r0) / fall, 0, 1)
            out[r0:] = np.minimum(out[r0:], ramp.astype(np.float32))
    return out


def synth(notes, total_s):
    n_out = int(total_s * RATE) + 1
    mix = np.zeros(n_out, dtype=np.float32)
    for n in notes:
        t0 = int(n.t0 * RATE)
        trel = None if n.t_rel is None else (n.t_rel - n.t0)
        dec = decay_seconds(n.decay)
        data, loop = n.ts.data, n.ts.loop
        maxlen = n_out - t0
        if trel is not None:                         # nur so lang rechnen, wie die Note klingt (lange Musikstuecke: viel schneller)
            maxlen = min(maxlen, int((trel + dec * 1.05) * RATE) + 1)
        if maxlen <= 0:
            continue
        step = np.full(maxlen, n.fs, dtype=np.float64)
        if n.porta:
            ratio, dur = n.porta
            k = np.clip(np.arange(maxlen) / (dur * RATE), 0, 1)
            step *= 1.0 + (ratio - 1.0) * k
        pos = np.cumsum(step) - step[0]
        if loop and loop[1] > loop[0]:
            ls, le = loop[0], loop[1]
            over = pos >= le
            pos[over] = ls + np.mod(pos[over] - ls, le - ls)
            valid = np.ones(maxlen, dtype=bool)
        else:
            valid = pos < len(data) - 1
        L = int(valid.sum()) if not (loop and loop[1] > loop[0]) else maxlen
        if trel is not None:
            L = min(L, int((trel + dec * 1.05) * RATE) + 1)
        L = min(L, maxlen)
        if L <= 0:
            continue
        p = pos[:L]
        i0 = np.floor(p).astype(np.int64); fr = (p - i0).astype(np.float32)
        i1 = np.minimum(i0 + 1, len(data) - 1)
        i0 = np.minimum(i0, len(data) - 1)
        s = data[i0] * (1 - fr) + data[i1] * fr
        env = env_curve(n.env, L, trel, dec)
        mix[t0:t0 + L] += s * env * n.amp
    return mix


def render(name, seed=0, max_s=6.0):
    data, names = load_seq0()
    if name not in names:
        raise KeyError(name)
    bank, idx = names[name]
    base, toff, handler = BANKS[bank]
    sim = Sim(data, seed)
    io = [-1] * 8
    io[0] = 1
    io[4] = s8(idx & 0xFF)
    io[5] = idx >> 8
    io[2], io[3], io[6], io[7] = -1, 0, 0, 0
    ch = Chan(sim, handler, io)
    sim.others[{"playerbank": 0, "itembank": 3, "environmentbank": 5, "enemybank": 8, "systembank": 11,
                "ocarinabank": 13, "voicebank": 14}[bank]] = ch.io
    end_tick = int(max_s * TICKS_PER_SEC)
    started = False
    for t in range(end_tick):
        sim.tick = t
        sim.time = t / sim.tps
        sim.chan_tick(ch)
        if sim.notes:
            started = True
        if started and t > 2 and all(not (ly and ly.enabled) for ly in ch.layers):
            break
    last = sim.time
    for n in sim.notes:
        if n.t_rel is None:
            n.t_rel = last
    tail = max([0.05] + [decay_seconds(n.decay) for n in sim.notes])
    total = min(max_s, last + min(tail, 1.5))
    out = synth(sim.notes, total)
    nz = np.nonzero(np.abs(out) > 1e-4)[0]
    if len(nz):
        out = out[:nz[-1] + 1 + int(0.02 * RATE)]
    return out, sim.notes


def render_seq(num, seed=0, max_s=30.0, loop_s=None, jumps=None, stop_on_loop=False):
    """Musik-Sequenz (z.B. 34 = Gegenstand-Fanfare) rendern. jumps: Liste fuer (Zeit, Ziel) der Sprungbefehle der Sequenz."""
    data, fonts = parse_seq("%s/sequences/seq_%d.seq" % (AUD, num))
    sim = Sim(data, seed, font0=fonts[-1] if fonts else 0)
    sim.jumps = jumps
    st = St(0); st.seq_main = True
    sim.seq_on, sim.seq_delay = True, 0
    t = 0
    seen = {}
    while sim.time < max_s:
        sim.tick = t
        if sim.seq_on:
            sim.seq_tick(st)
            if jumps and stop_on_loop and len(jumps) != sum(seen.values()):
                for _, tgt in jumps[sum(seen.values()):]:
                    seen[tgt] = seen.get(tgt, 0) + 1
                if max(seen.values()) >= 2:
                    max_s = sim.time + 0.02          # zweiter Durchlauf der Hauptschleife fertig: reicht fuer render_loop
        for c in sim.chans:
            if c and c.enabled:
                sim.chan_tick(c)
        t += 1
        sim.time += 1.0 / sim.tps
        if not sim.seq_on and not any(c and c.enabled and any(ly and ly.enabled for ly in c.layers) for c in sim.chans):
            break
    last = sim.time
    for n in sim.notes:
        if n.t_rel is None:
            n.t_rel = last
    tail = max([0.05] + [min(decay_seconds(n.decay), 3.0) for n in sim.notes])
    out = synth(sim.notes, min(max_s, last + tail))
    nz = np.nonzero(np.abs(out) > 1e-4)[0]
    if len(nz):
        out = out[:nz[-1] + 1 + int(0.05 * RATE)]
    return out, sim.notes


def render_loop(num, max_s=260.0):
    """Hintergrundmusik als nahtlose Schleife: Sequenz rendern, Hauptschleife (Sprung zurueck) erkennen und genau einen
    Durchlauf ab dem zweiten Mal ausschneiden (dort klingen die Noten des vorigen Durchlaufs schon mit -> naht los).
    -> (Ton, Schleifenlaenge s, Start s)"""
    jumps = []
    x, _ = render_seq(num, max_s=max_s, jumps=jumps, stop_on_loop=True)
    by = {}
    for t, tgt in jumps:
        by.setdefault(tgt, []).append(t)
    cand = [(ts[1] - ts[0], ts) for ts in by.values() if len(ts) >= 2]
    if not cand:
        return x, None, 0.0
    L, ts = max(cand)                          # laengste wiederkehrende Schleife = Hauptschleife
    a = int(round(ts[0] * RATE)); b = int(round(ts[1] * RATE))
    if b > len(x):
        return x, None, 0.0
    return x[a:b], L, ts[0]


def write_wav(path, x):
    x = np.clip(x, -1, 1)
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE)
        w.writeframes((x * 32767).astype("<i2").tobytes())


def export(out_dir, names, seeds=1, gain=1.0):
    d = os.path.join(out_dir, "sfx"); os.makedirs(d, exist_ok=True)
    res = {}
    for nm in names:
        files = []
        for s in range(seeds):
            x, _ = render(nm, seed=s)
            if len(x) < 10:
                continue
            w = os.path.join(d, "%s_%d.wav" % (nm, s))
            write_wav(w, x * gain)
            o = w[:-4] + ".ogg"
            subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", w, "-c:a", "libvorbis", "-q:a", "5", o], check=True)
            os.remove(w)
            files.append("sfx/" + os.path.basename(o))
        if files:
            res[nm] = files
    return res


# Effekte fuer den Kokiri-Wald (Name -> Anzahl Varianten)
KF_SFX = {
    "NA_SE_IT_SWORD_SWING": 1, "NA_SE_IT_SWORD_PUTAWAY": 1, "NA_SE_IT_SWORD_PICKOUT": 1,
    "NA_SE_VO_LI_SWORD_N_KID": 4, "NA_SE_VO_LI_AUTO_JUMP_KID": 2, "NA_SE_VO_LI_DAMAGE_S_KID": 3, "NA_SE_VO_LI_FALL_S_KID": 2,
    "NA_SE_PL_WALK_GRASS": 1, "NA_SE_PL_WALK_GROUND": 1, "NA_SE_PL_JUMP": 1, "NA_SE_PL_LAND": 1, "NA_SE_PL_ROLL": 1,
    "NA_SE_EV_PLANT_BROKEN": 1, "NA_SE_EV_TBOX_UNLOCK": 1, "NA_SE_EV_TBOX_OPEN": 1, "NA_SE_EV_DOOR_OPEN": 1,
    "NA_SE_SY_GET_RUPY": 1, "NA_SE_SY_GET_ITEM": 1, "NA_SE_SY_MESSAGE_PASS": 1, "NA_SE_SY_MESSAGE_END": 1,
    "NA_SE_SY_LOCK_ON": 1, "NA_SE_SY_LOCK_OFF": 1, "NA_SE_SY_CURSOR": 1,
    "NA_SE_VO_NA_HELLO_0": 1, "NA_SE_VO_NA_HELLO_1": 1, "NA_SE_VO_NA_HELLO_2": 1, "NA_SE_VO_NA_HELLO_3": 1, "NA_SE_VO_NAVY_CALL": 3,
}
KF_SEQS = {"BGM_ITEM_GET": 34, "BGM_SMALL_ITEM_GET": 57}
# Teil 2: Kampf, Gegner, Laden
KF2_SFX = {
    "NA_SE_SY_LOCK_ON_HUMAN": 1, "NA_SE_SY_HITPOINT_ALARM": 1, "NA_SE_SY_DECIDE": 1, "NA_SE_SY_ERROR": 1,
    "NA_SE_PL_DAMAGE": 1, "NA_SE_VO_LI_DOWN_KID": 1, "NA_SE_IT_SHIELD_BOUND": 1, "NA_SE_IT_SWORD_STRIKE": 1,
    "NA_SE_IT_SHIELD_REFLECT_SW": 1, "NA_SE_EN_DEKU_MOUTH": 1, "NA_SE_EN_DEKU_JR_MOUTH": 2, "NA_SE_EN_DEKU_ATTACK": 1,
    "NA_SE_EN_DEKU_JR_ATTACK": 1, "NA_SE_EN_DEKU_SCRAPE": 1, "NA_SE_EN_DEKU_DAMAGE": 1, "NA_SE_EN_DEKU_DEAD": 1,
    "NA_SE_EN_DEKU_JR_DEAD": 1, "NA_SE_EN_DUMMY482": 1, "NA_SE_EN_DODO_M_GND": 1,
}


YDAN_SFX = {
    "NA_SE_EN_STALTU_LAUGH": 1, "NA_SE_EN_STALTU_DOWN": 1, "NA_SE_EN_STALTU_DOWN_SET": 1, "NA_SE_EN_STALTU_UP": 1,
    "NA_SE_EN_STALTU_ROLL": 1, "NA_SE_EN_STALTU_WAVE": 1, "NA_SE_EN_STALTU_DAMAGE": 1, "NA_SE_EN_STALWALL_DEAD": 1,
    "NA_SE_PL_BODY_HIT": 1, "NA_SE_EN_NUTS_UP": 1, "NA_SE_EN_NUTS_DOWN": 1, "NA_SE_EN_NUTS_THROW": 1, "NA_SE_EN_NUTS_DAMAGE": 1,
    "NA_SE_EN_NUTS_WALK": 1, "NA_SE_EN_NUTS_FAINT": 1, "NA_SE_EN_OCTAROCK_ROCK": 1,
}


def export_names(out_dir, names):
    """Nur diese Sounds rendern (Ergaenzung zum vorhandenen Paket) -> {name: [dateien]}"""
    d = os.path.join(out_dir, "sfx"); os.makedirs(d, exist_ok=True)
    clips = {}
    for nm, k in names.items():
        files, seen = [], set()
        for sd in range(k * 3):
            if len(files) >= k:
                break
            try:
                x, notes = render(nm, seed=sd)
            except Exception as e:
                print("sfx", nm, e); break
            sig = tuple(n.ts.path for n in notes)
            if len(x) < 10 or sig in seen:
                continue
            seen.add(sig)
            o = os.path.join(d, "%s_%d.ogg" % (nm, len(files)))
            _ogg(o, x)
            files.append("sfx/" + os.path.basename(o))
        if files:
            clips[nm] = files
    return clips


def export_pack(out_dir):
    """Alle KF-Sounds + Fanfaren ins Pack schreiben -> pack.json-Block "oot_sfx"."""
    d = os.path.join(out_dir, "sfx"); os.makedirs(d, exist_ok=True)
    clips = {}
    for nm, k in KF_SFX.items():
        files, seen = [], set()
        for sd in range(k * 3):
            if len(files) >= k:
                break
            x, notes = render(nm, seed=sd)
            sig = tuple(n.ts.path for n in notes)
            if len(x) < 10 or sig in seen:
                continue
            seen.add(sig)
            o = os.path.join(d, "%s_%d.ogg" % (nm, len(files)))
            _ogg(o, x)
            files.append("sfx/" + os.path.basename(o))
        if files:
            clips[nm] = files
    for nm, num in KF_SEQS.items():
        x, _ = render_seq(num)
        if len(x) < 10:
            continue
        x = x / max(1e-6, float(np.abs(x).max())) * 0.8
        o = os.path.join(d, nm + ".ogg")
        _ogg(o, x)
        clips[nm] = ["sfx/" + nm + ".ogg"]
    return {"clips": clips, "volume": 0.8}


def _ogg(path, x):
    w = path[:-4] + ".wav"
    write_wav(w, x)
    subprocess.run(["ffmpeg", "-y", "-v", "error", "-i", w, "-c:a", "libvorbis", "-q:a", "5", path], check=True)
    os.remove(w)
