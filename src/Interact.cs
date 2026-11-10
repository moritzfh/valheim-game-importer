using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Interaktion wie in Witcher (Taste E): Tueren der festen Modelle und Stellen aus spots.json (Hinweise, Verstecke,
    // Questgegenstaende). Stellen mit "zone": true sind Ausloese-Bereiche (beim Betreten, z. B. Geralt kommentiert die Aussicht).
    //
    // spots.json: [{"id": "q001_key", "pos": [x, y, z], "radius": 1.6, "height": 2.5, "name": {...}, "verb": {...},
    //               "senses": true, "once": true, "if": [...], "do": [...], "zone": false}]
    //   benutzt -> Flag "used:<id>" (Quest-Ziel {"type": "use", "spot": "<id>"}), "senses": in den Hexersinnen rot markiert
    // Tueren: feste Modelle mit "door" im Namen (ohne Rahmen, Regal- und Schranktueren) drehen sich um die Angel auf und zu.
    //   pack.json "locks": {"<tag>": {"if": ["item:schluessel"], "text": {...}}}  verschlossen, bis die Bedingung gilt
    //   pack.json "doors_open": ["<teil des modellnamens>", ...]                   stehen anfangs offen
    public static class Interact
    {
        public class Spot
        {
            public string Id; public Vector3 P; public float R = 1.6f, H = 2.5f; public object Name, Verb;
            public bool Senses, Once = true, Zone, Inside; public List<object> If, Do;
        }
        static readonly List<Spot> spots = new List<Spot>();
        static readonly Dictionary<string, Spot> byId = new Dictionary<string, Spot>();
        static Pack forPack;
        public static bool Claim;            // E gehoert gerade uns (Valheims "Benutzen" ist dann aus)
        public static int Kind { get { return curSpot != null ? 1 : curDoor != null ? 2 : 0; } }     // fuer OoT-A-Knopf: 1 Stelle, 2 Tuer
        public static void Press()
        {
            var pl = Player.m_localPlayer; if (pl == null) return;
            if (curSpot != null) Use(curSpot);
            else if (curDoor != null) { var lt = curDoor.LockText(); if (lt != null) Say(lt); else curDoor.Toggle(pl.transform.position); }
        }
        static string prompt; static Spot curSpot; static Door curDoor;
        static float zoneT, msgUntil; static string msg;

        static void Load(Pack pk)
        {
            forPack = pk; spots.Clear(); byId.Clear();
            if (pk == null) return;
            var f = Path.Combine(pk.Dir, "spots.json");
            if (!File.Exists(f)) return;
            try
            {
                var arr = Json.Parse(File.ReadAllText(f)) as List<object>;
                if (arr == null) return;
                foreach (var o in arr)
                {
                    var p = Json.L(o, "pos"); if (p == null || p.Count < 3) continue;
                    var s = new Spot
                    {
                        Id = Json.S(o, "id"), R = Json.F(o, "radius", 1.6f), H = Json.F(o, "height", 2.5f),
                        Name = Json.V(o, "name"), Verb = Json.V(o, "verb"), Senses = Json.B(o, "senses"), Once = !Json.Has(o, "once") || Json.B(o, "once"),
                        Zone = Json.B(o, "zone"), If = Json.L(o, "if"), Do = Json.L(o, "do")
                    };
                    s.P = new Vector3(Num(p[0]), Num(p[1]), Num(p[2]));
                    if (string.IsNullOrEmpty(s.Id)) continue;
                    spots.Add(s); byId[s.Id] = s;
                }
                Plugin.Log("Interaktion: " + spots.Count + " Stellen");
            }
            catch (Exception e) { Plugin.Warn("spots.json: " + e.Message); }
        }

        static float Num(object o) { return o is double ? (float)(double)o : 0f; }

        public static bool SpotPos(string id, out Vector3 v)
        {
            if (Pack.Current != forPack) Load(Pack.Current);
            Spot s; v = Vector3.zero;
            if (id == null || !byId.TryGetValue(id, out s)) return false;
            v = s.P; return true;
        }

        static bool Available(Spot s) { return Story.Check(s.If) && !(s.Once && Story.Flags.Contains("used:" + s.Id)); }

        // fuer die Hexersinne: Stellen, die rot leuchten
        public static IEnumerable<Vector3> SensesSpots(Vector3 me, float range)
        {
            foreach (var s in spots)
                if (s.Senses && !s.Zone && Available(s) && (s.P - me).sqrMagnitude < range * range) yield return s.P;
        }

        static void Use(Spot s)
        {
            Story.Flags.Add("used:" + s.Id); Story.Save();
            Plugin.Log("Interaktion: " + s.Id);
            Story.Do(s.Do);
        }

        public static void Say(string text, float secs = 2.5f) { msg = text; msgUntil = Time.unscaledTime + secs; }

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) Load(pk);
            Claim = false; prompt = null; curSpot = null; curDoor = null;
            var pl = Player.m_localPlayer;
            if (pk == null || pl == null || StoryUi.Busy || pl.IsDead() || WitcherInv.Open || FastTravel.Open) return;
            var me = pl.transform.position;
            // Bereiche: beim Betreten
            if (Time.time > zoneT)
            {
                zoneT = Time.time + 0.2f;
                foreach (var s in spots)
                {
                    if (!s.Zone) continue;
                    bool inside = Story.Flat(me, s.P) < s.R && Mathf.Abs(me.y - s.P.y) < s.H;
                    if (inside && !s.Inside && Available(s)) Use(s);
                    s.Inside = inside;
                }
            }
            if (pl.InPlaceMode() || !Horse.FreeInput()) return;
            var cam = Camera.main;
            var eye = cam != null ? cam.transform.position : me + Vector3.up * 1.6f;
            var fwd = cam != null ? cam.transform.forward : pl.transform.forward;
            var chest = me + Vector3.up * 1.1f;
            float best = float.MaxValue;
            foreach (var s in spots)
            {
                if (s.Zone || !Available(s)) continue;
                float d = Vector3.Distance(chest, s.P);
                if (d > s.R + 0.9f) continue;
                float ang = Vector3.Angle(fwd, s.P - eye);
                if (ang > 45f) continue;
                float sc = d + ang * 0.04f;
                if (sc < best) { best = sc; curSpot = s; curDoor = null; }
            }
            foreach (var dr in Door.Live)
            {
                if (dr == null) continue;
                var c = dr.Center;
                if (Mathf.Abs(c.y - chest.y) > 2.2f) continue;
                float d = Story.Flat(me, c);
                if (d > 2.3f) continue;
                float ang = Vector3.Angle(fwd, c - eye);
                if (ang > 55f) continue;
                float sc = d + ang * 0.04f;
                if (sc < best) { best = sc; curDoor = dr; curSpot = null; }
            }
            if (curSpot != null)
            {
                var vb = Pack.T(curSpot.Verb); if (vb == "") vb = Story.L("Untersuchen", "Examine");
                prompt = "[E] " + vb + (curSpot.Name != null ? ": " + Pack.T(curSpot.Name) : "");
            }
            else if (curDoor != null)
                prompt = "[E] " + (curDoor.IsOpen ? Story.L("Schließen", "Close") : Story.L("Öffnen", "Open"));
            Claim = prompt != null;
            if (!Claim || !Input.GetKeyDown(KeyCode.E) || (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()) return;
            if (curSpot != null) Use(curSpot);
            else if (curDoor != null)
            {
                var lt = curDoor.LockText();
                if (lt != null) Say(lt); else curDoor.Toggle(me);
            }
        }

        // Fernsteuerung: was gerade angeboten wird; mit use=true wie Taste E
        public static string Debug(bool use)
        {
            var s = "interact: " + (prompt ?? "nichts") + " | Tueren " + Door.Live.Count + ", Stellen " + spots.Count;
            if (use && curSpot != null) { Use(curSpot); s += " -> benutzt " + curSpot.Id; }
            else if (use && curDoor != null) { var lt = curDoor.LockText(); if (lt != null) { Say(lt); s += " -> " + lt; } else { curDoor.Toggle(Player.m_localPlayer.transform.position); s += " -> Tuer " + (curDoor.IsOpen ? "auf" : "zu"); } }
            return s;
        }

        // Hinweis unten in der Mitte wie in Witcher ("[E] Untersuchen")
        static GUIStyle st;
        public static void Gui()
        {
            if (Player.m_localPlayer == null || StoryUi.Busy) return;
            string t = Time.unscaledTime < msgUntil ? msg : (OotPlay.On ? null : prompt);       // OoT: Aktion steht auf dem A-Knopf
            if (string.IsNullOrEmpty(t)) return;
            float k = Mathf.Max(0.75f, Screen.height / 1080f);
            if (st == null || st.fontSize != (int)(22 * k))
                st = new GUIStyle { fontSize = (int)(22 * k), alignment = TextAnchor.MiddleCenter, richText = true, normal = { textColor = new Color(0.96f, 0.93f, 0.86f) } };
            var r = new Rect(Screen.width / 2f - 300 * k, Screen.height * 0.70f, 600 * k, 34 * k);
            var sh = new GUIStyle(st) { normal = { textColor = new Color(0f, 0f, 0f, 0.85f) } };
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), t, sh);
            GUI.Label(r, t, st);
        }
    }

    // Tuer aus den festen Modellen: dreht sich um ihre Angel (Kante des Tuerblatts), weg vom Spieler
    public class Door : MonoBehaviour
    {
        public static readonly List<Door> Live = new List<Door>();
        string key; string[] tags;
        Vector3 hinge, pos0, center0, normal; Quaternion rot0;
        float angle, target; bool ready;
        public bool IsOpen { get { return target != 0f; } }
        public Vector3 Center { get { return hinge + Quaternion.AngleAxis(angle, Vector3.up) * (center0 - hinge); } }

        public static bool IsDoor(string model)
        {
            var n = Path.GetFileNameWithoutExtension(model ?? "").ToLowerInvariant();
            if (!n.Contains("door")) return false;
            foreach (var x in new[] { "frame", "bookshelf", "pile", "stairs", "trapdoor", "cabinet", "wardrobe", "closet", "_box", "chest" }) if (n.Contains(x)) return false;
            return true;
        }

        public static void Attach(GameObject go, string model, string[] tags, Vector3 p)
        {
            var d = go.AddComponent<Door>();
            d.key = "door:" + p.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "_" + p.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "_" + p.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            d.tags = tags;
            d.Setup(model);
        }

        void Setup(string model)
        {
            // Umriss im eigenen Raum des Modells
            var t = transform; bool has = false; var lb = new Bounds();
            foreach (var mf in GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = t.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!has) { lb = new Bounds(l, Vector3.zero); has = true; } else lb.Encapsulate(l);
                }
            }
            if (!has) { Destroy(this); return; }
            var sc = t.lossyScale;
            bool alongX = Mathf.Abs(lb.extents.x * sc.x) >= Mathf.Abs(lb.extents.z * sc.z);
            var axis = alongX ? Vector3.right : Vector3.forward;
            float ext = alongX ? lb.extents.x : lb.extents.z, cA = alongX ? lb.center.x : lb.center.z;
            var e0 = lb.center - axis * ext; var e1 = lb.center + axis * ext;
            // Angel: Modell-Ursprung liegt an einer Kante -> dort; sonst die Kante weg vom Nachbarfluegel (Doppeltuer)
            Vector3 hl;
            float d0 = Mathf.Abs(0f - (cA - ext)), d1 = Mathf.Abs(0f - (cA + ext));
            if (Mathf.Min(d0, d1) < 0.5f * ext) hl = d0 < d1 ? e0 : e1;
            else
            {
                Door partner = null; float bd = 3.5f;
                var cw = t.TransformPoint(lb.center);
                foreach (var o in Live) if (o != null && o != this) { float dd = Vector3.Distance(o.Center, cw); if (dd < bd) { bd = dd; partner = o; } }
                if (partner != null) hl = (t.TransformPoint(e0) - partner.Center).sqrMagnitude > (t.TransformPoint(e1) - partner.Center).sqrMagnitude ? e0 : e1;
                else hl = e0;
            }
            hinge = t.TransformPoint(new Vector3(hl.x, lb.center.y, hl.z));
            center0 = t.TransformPoint(lb.center);
            normal = t.TransformDirection(alongX ? Vector3.forward : Vector3.right); normal.y = 0f; normal.Normalize();
            pos0 = t.position; rot0 = t.rotation;
            ready = true;
            Live.Add(this);
            // Zustand: gespeichert (geoeffnet) oder laut Paket anfangs offen
            float a = 0f;
            foreach (var f in Story.Flags) if (f.StartsWith(key + ":")) { float.TryParse(f.Substring(key.Length + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out a); break; }
            if (a == 0f && !Story.Flags.Contains(key + ":closed") && StartsOpen(model)) a = 100f;
            angle = target = a; Apply();
            Plugin.Log("Tuer " + Path.GetFileNameWithoutExtension(model) + " bei " + pos0.ToString("F1") + ": Angel " + hinge.ToString("F1") + (a != 0f ? " offen" : ""));
        }

        static bool StartsOpen(string model)
        {
            var l = Json.L(Pack.Current != null ? Pack.Current.Meta : null, "doors_open");
            if (l == null) return false;
            var n = Path.GetFileNameWithoutExtension(model).ToLowerInvariant();
            foreach (var x in l) if (x is string && n.Contains(((string)x).ToLowerInvariant())) return true;
            return false;
        }

        // Text, wenn verschlossen (pack.json "locks"), sonst null
        public string LockText()
        {
            var locks = Json.O(Pack.Current != null ? Pack.Current.Meta : null, "locks");
            if (locks == null || tags == null) return null;
            foreach (var tg in tags)
            {
                object lk;
                if (tg == null || !locks.TryGetValue(tg, out lk)) continue;
                if (!Story.Check(Json.L(lk, "if"))) { var tx = Pack.T(Json.V(lk, "text")); return tx != "" ? tx : Story.L("Verschlossen.", "Locked."); }
            }
            return null;
        }

        public void Toggle(Vector3 from)
        {
            if (!ready) return;
            if (target != 0f) target = 0f;
            else
            {
                var r = center0 - hinge; r.y = 0f;
                float s = Mathf.Sign(Vector3.Dot(from - center0, normal));
                var r1 = Quaternion.AngleAxis(100f, Vector3.up) * r;
                target = Vector3.Dot(r1, normal) * s < 0f ? 100f : -100f;
            }
            Story.Flags.RemoveWhere(f => f.StartsWith(key + ":"));
            Story.Flags.Add(key + ":" + (target == 0f ? "closed" : target.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)));
            Story.Save();
        }

        public bool HasTag(string tg) { if (tags == null) return false; foreach (var x in tags) if (x == tg) return true; return false; }

        void Update()
        {
            if (!ready || angle == target) return;
            angle = Mathf.MoveTowards(angle, target, 150f * Time.deltaTime);
            Apply();
        }

        void Apply()
        {
            var r = Quaternion.AngleAxis(angle, Vector3.up);
            transform.SetPositionAndRotation(hinge + r * (pos0 - hinge), r * rot0);
        }

        void OnDestroy() { Live.Remove(this); }
    }
}
