"""Original-Texte (Englisch) aus message_data.h der Decomp.

  messages() -> {id: Rohtext}
  pages(id) -> Liste von Textseiten (Unity Rich Text: Farben, Zeilenumbrueche), Folgetext (TEXTID) und Auswahl
  export(ids) -> {"0x1004": {"pages": [...], "next": "0x...", "choice": 0}}
"""
import re
OOT_DECOMP = __import__("os").environ.get("OOT_DECOMP", "/home/claude/oot")      # Decomp mit eigener ROM (oot_setup_decomp.sh)

MSG = OOT_DECOMP + "/extracted/gc-eu/text/message_data.h"
COLORS = {"RED": "#ff3c3c", "ADJUSTABLE": "#ff3c3c", "GREEN": "#46ff50", "BLUE": "#5a78ff", "LIGHTBLUE": "#64b4ff",
          "PURPLE": "#d264ff", "YELLOW": "#ffff32", "BLACK": "#000000", "DEFAULT": None}
_cache = None


def messages():
    global _cache
    if _cache is not None:
        return _cache
    src = open(MSG, encoding="utf-8").read()
    out = {}
    for chunk in src.split("DEFINE_MESSAGE(")[1:]:
        m = re.match(r"(0x[0-9A-Fa-f]+),", chunk)
        if not m:
            continue
        parts = _split_msgs(chunk[chunk.find("\n"):])
        if len(parts) >= 2:
            out[int(m.group(1), 16)] = parts[1]          # 0 = Japanisch (fehlt), 1 = Englisch, 2 = Deutsch, 3 = Franzoesisch
    _cache = out
    return out


def _split_msgs(body):
    """Die MSG(...)-Bloecke (Klammern zaehlen, Strings beachten)."""
    res, i = [], 0
    while True:
        j = body.find("MSG(", i)
        if j < 0:
            break
        k, depth, q = j + 4, 1, False
        while k < len(body) and depth:
            c = body[k]
            if c == '"' and body[k - 1] != "\\":
                q = not q
            elif not q:
                if c == "(":
                    depth += 1
                elif c == ")":
                    depth -= 1
            k += 1
        res.append(body[j + 4:k - 1])
        i = k
    return res


def pages(mid, name="Link"):
    raw = messages().get(mid)
    if raw is None:
        return None
    toks = re.findall(r'"((?:[^"\\]|\\.)*)"|([A-Z_]+)(?:\(([^)]*)\))?', raw)
    pg, cur, color, nxt, choice = [], "", None, None, 0

    def close_color():
        nonlocal cur, color
        if color:
            cur += "</color>"
        color = None
    for s, cmd, arg in toks:
        if s or (not cmd and s == ""):
            if s:
                t = s.encode("utf-8").decode("unicode_escape").encode("latin-1", "ignore").decode("utf-8", "ignore") if "\\x" in s else s.replace("\\n", "\n").replace('\\"', '"')
                for a_, b_ in (("[A]", "<color=#5a78ff>A</color>"), ("[B]", "<color=#46ff50>B</color>"), ("[L]", "Z"), ("[Z]", "Z"),
                               ("[R]", "R"), ("[C-Up]", "C-Up"), ("[C]", "C")):
                    t = t.replace(a_, b_)
                cur += t
            continue
        if cmd == "COLOR":
            close_color()
            c = COLORS.get(arg.strip())
            if c:
                cur += "<color=%s>" % c; color = c
        elif cmd == "BOX_BREAK" or cmd == "BOX_BREAK_DELAYED":
            close_color(); pg.append(cur.strip("\n")); cur = ""
        elif cmd == "NAME":
            cur += name
        elif cmd == "TEXTID" and arg and arg.strip():
            try:
                nxt = int(arg.strip().strip('"'), 0)
            except ValueError:
                pass
        elif cmd == "TWO_CHOICE":
            choice = 2
        elif cmd == "THREE_CHOICE":
            choice = 3
        elif cmd in ("NEWLINE",):
            cur += "\n"
    close_color()
    if cur.strip():
        pg.append(cur.strip("\n"))
    return {"pages": [p for p in pg if p.strip()], "next": nxt, "choice": choice}


def export(ids):
    out, todo, done = {}, list(ids), set()
    while todo:
        i = todo.pop()
        if i in done:
            continue
        done.add(i)
        p = pages(i)
        if p is None:
            continue
        out["0x%04X" % i] = {"pages": p["pages"], "next": ("0x%04X" % p["next"]) if p["next"] else None, "choice": p["choice"]}
        if p["next"]:
            todo.append(p["next"])
    return out
