using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Im Deku-Baum (pack.json "oot_ydan", tools/oot/oot_ydan.py): Kletterflaechen (Ranken/Leitern aus den Oberflaechen-Typen der
    /// Original-Kollision), Wasser (Wasserkaesten), Spinnennetze (Bg_Ydan_Sp: Boden-Netz federt, reisst bei tiefem Sprung,
    /// brennt mit brennendem Deku-Stab; Wand-Netz brennt), Fackeln (Obj_Syokudai: brennende zuenden den Deku-Stab,
    /// ein brennender Stab zuendet erloschene an).
    /// </summary>
    public static class OotDungeon
    {
        const float S = 0.033f;
        static Pack forPack; static GameObject holder; static float modelScale = 0.00033f;

        public class Climb { public Vector3 A, B, C, N; public int T; public Vector3 Min, Max; }
        class Water { public float X0, Z0, X1, Z1, Y; }
        class Web
        {
            public Vector3 Pos; public bool Floor; public short Rx, Ry; public string Flag, Model;
            public GameObject Go; public float Amp, T, BurnT = -1f, BreakT = -1f; public int Timer = 14; public bool Gone;
        }
        class Torch { public Vector3 Pos; public int Type; public bool Lit; public GameObject Go, Fire; public string Flag; public int Sw = -1, N; public float Timer = -1f; }

        static readonly List<Climb> climbs = new List<Climb>();
        static readonly List<Water> waters = new List<Water>();
        static readonly List<Web> webs = new List<Web>();
        static readonly List<Torch> torches = new List<Torch>();
        // Navi-Hinweise (Elf_Msg): Zylinder/Quader; erzwungen = Navi spricht sofort, sonst wird sie gruen und C-oben (Q) ruft den Text
        class Hint { public Vector3 P; public float R, H; public bool Cuboid, Forced; public string Text, Flag; public int KillSw = -1; }
        static readonly List<Hint> hints = new List<Hint>();
        static readonly Dictionary<string, object> hintTexts = new Dictionary<string, object>();
        public static bool NaviCalls { get; private set; }
        static Hint pending;
        static string[] torchModels = new string[0];
        public static bool Active { get { return climbs.Count > 0 || waters.Count > 0 || webs.Count > 0; } }

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var o = Json.V(meta, "oot_ydan"); if (o == null) return;
            modelScale = Json.F(Json.V(meta, "oot_play"), "scale", 0.00033f);
            foreach (var c in Json.L(o, "climbs") ?? new List<object>())
            {
                var v = Json.L(c, "v"); if (v == null || v.Count < 3) continue;
                var cl = new Climb { A = V3((List<object>)v[0]), B = V3((List<object>)v[1]), C = V3((List<object>)v[2]), T = (int)Json.F(c, "t") };
                cl.N = Vector3.Cross(cl.B - cl.A, cl.C - cl.A).normalized;
                // OoT-Dreiecke im Gegen-Uhrzeigersinn; nach z-Spiegelung zeigt die Normale nach innen -> zur offenen Seite drehen
                cl.N = -cl.N;
                cl.Min = Vector3.Min(cl.A, Vector3.Min(cl.B, cl.C)) - Vector3.one * 0.4f; cl.Max = Vector3.Max(cl.A, Vector3.Max(cl.B, cl.C)) + Vector3.one * 0.4f;
                climbs.Add(cl);
            }
            foreach (var w in Json.L(o, "water") ?? new List<object>())
            {
                var a = Json.L(w, "min"); var b = Json.L(w, "max");
                waters.Add(new Water { X0 = Convert.ToSingle(a[0]), Z0 = Convert.ToSingle(a[1]), X1 = Convert.ToSingle(b[0]), Z1 = Convert.ToSingle(b[1]), Y = Json.F(w, "y") });
            }
            foreach (var w in Json.L(o, "webs") ?? new List<object>())
                webs.Add(new Web { Pos = V3(Json.L(w, "pos")), Floor = Json.S(w, "type") == "floor", Rx = (short)(int)Json.F(w, "rx"), Ry = (short)(int)Json.F(w, "ry"),
                                   Flag = Json.S(w, "flag"), Model = Json.S(w, "model") });
            var tm = Json.L(o, "torch_models"); if (tm != null) { torchModels = new string[tm.Count]; for (int i = 0; i < tm.Count; i++) torchModels[i] = tm[i].ToString(); }
            int ti = 0;
            foreach (var t in Json.L(o, "torches") ?? new List<object>())
                torches.Add(new Torch { Pos = V3(Json.L(t, "pos")), Type = (int)Json.F(t, "type"), Lit = Json.F(t, "lit") > 0.5f, Flag = "oot_ydan_torch_" + (ti++), Sw = (int)Json.F(t, "flag", -1f), N = (int)Json.F(t, "n", 0f) });
            foreach (var h in Json.L(o, "hints") ?? new List<object>())
                hints.Add(new Hint { P = V3(Json.L(h, "pos")), R = Json.F(h, "r", 1.32f), H = Json.F(h, "h", 1.32f), Cuboid = Json.B(h, "cuboid"), Forced = Json.B(h, "forced"), KillSw = (int)Json.F(h, "kill_sw", -1f),
                                     Text = Json.S(h, "text"), Flag = Json.S(h, "flag") });
            var ht = Json.V(o, "hint_texts") as Dictionary<string, object>;
            if (ht != null) foreach (var kv in ht) hintTexts[kv.Key] = kv.Value;
            foreach (var w in webs) if (Story.Flags.Contains(w.Flag)) w.Gone = true;
            foreach (var t in torches) if (Story.Flags.Contains(t.Flag)) t.Lit = true;
            Plugin.Log("OoT-Deku-Baum: " + climbs.Count + " Kletterflaechen, " + waters.Count + " Wasser, " + webs.Count + " Netze, " + torches.Count + " Fackeln");
        }

        public static void Clear()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; forPack = null; climbs.Clear(); waters.Clear(); webs.Clear(); torches.Clear(); hints.Clear(); hintTexts.Clear(); pending = null; NaviCalls = false;
        }

        static GameObject Model(string rel, Vector3 pos, Quaternion rot, float sc, bool collide)
        {
            if (string.IsNullOrEmpty(rel) || forPack == null) return null;
            var path = Path.Combine(forPack.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;
            var g = Glb.Instantiate(path);
            g.transform.SetParent(holder.transform, false);
            g.transform.position = pos; g.transform.rotation = rot; g.transform.localScale = Vector3.one * sc;
            foreach (var c in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            if (collide)
                foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mc = mf.gameObject.AddComponent<MeshCollider>(); mc.sharedMesh = mf.sharedMesh;
                }
            return g;
        }

        public static void Tick(Player p)
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if (!Active || p == null) return;
                if (holder == null) holder = new GameObject("oot_ydan");
                var me = p.transform.position;
                foreach (var w in webs)
                {
                    float d = (w.Pos - me).magnitude;
                    if (w.Gone) { if (w.Go != null) { UnityEngine.Object.Destroy(w.Go); w.Go = null; } continue; }
                    if (w.Go == null && d < 70f)
                    {
                        // Akteur-Massstab 0,1 (ICHAIN scale 100/1000)
                        w.Go = Model(w.Model, w.Pos, OotEnemies.Rot(w.Rx, w.Ry, 0), S * 0.1f, true);
                        if (w.Go != null)
                        {
                            w.Go.name = "oot_web";
                            // Netz-Textur ist fast weiss: im hellen Raumlicht nicht ueberstrahlen (Original: matt, leicht durchscheinend)
                            foreach (var r in w.Go.GetComponentsInChildren<Renderer>(true))
                                foreach (var m in r.materials) if (m != null && m.HasProperty("_Color")) { var c = m.color; m.color = new Color(c.r * 0.46f, c.g * 0.44f, c.b * 0.4f, c.a); }
                            // Remaster: seidiger Schimmer auf den Faeden (zweite, weich additive Schicht mit derselben Netz-Textur)
                            foreach (var mf in w.Go.GetComponentsInChildren<MeshFilter>(true))
                            {
                                var mr = mf.GetComponent<MeshRenderer>(); var tx = mr != null && mr.sharedMaterial != null ? mr.sharedMaterial.mainTexture as Texture2D : null;
                                if (tx == null) continue;
                                var g = new GameObject("web_glint"); g.transform.SetParent(mf.transform, false);
                                g.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                                var gm = FireFx.Make(tx); if (gm.HasProperty("_TintColor")) gm.SetColor("_TintColor", new Color(0.78f, 0.84f, 0.9f, 0.05f));
                                var gr = g.AddComponent<MeshRenderer>(); gr.sharedMaterial = gm; gr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                            }
                        }
                    }
                    if (w.Go == null) continue;
                    if (d > 90f) { UnityEngine.Object.Destroy(w.Go); w.Go = null; continue; }
                    WebUpdate(w, p);
                }
                HintTick(p);
                foreach (var t in torches)
                {
                    float d = (t.Pos - me).magnitude;
                    if (t.Go == null && d < 60f && torchModels.Length > 0)
                        t.Go = Model(torchModels[Mathf.Clamp(t.Type, 0, torchModels.Length - 1)], t.Pos, Quaternion.identity, modelScale, true);
                    if (t.Type != 2 && t.Sw >= 0 && OotYdanObj.Flag(t.Sw)) { if (!t.Lit) OotSfx.Play("NA_SE_EV_FLAME_IGNITION", 1f, t.Pos); t.Lit = true; t.Timer = -1f; }
                    else if (t.Lit && t.Timer > 0f) { t.Timer -= Time.deltaTime; if (t.Timer <= 0f) { t.Lit = false; t.Timer = 0f; } }      // Zeit-Fackel geht wieder aus
                    bool want = t.Lit && d < 60f;
                    if (want && t.Fire == null) { t.Fire = FireFx.Spawn(FlamePos(t), 0.32f, 26f, true); t.Fire.transform.SetParent(holder.transform, true); }
                    if (!want && t.Fire != null) { UnityEngine.Object.Destroy(t.Fire); t.Fire = null; }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotDungeon.Tick"); }
        }

        // Fackel: Flamme oben auf dem Pfosten (~ 56 Einheiten hoch)
        // ObjSyokudai: Holz-Fackel brennt dauerhaft; Zeit-Fackeln einer Gruppe (gleicher Merker, Anzahl N) muessen gleichzeitig brennen
        static void LightTorch(Torch t)
        {
            t.Lit = true; OotSfx.Play("NA_SE_EV_FLAME_IGNITION", 1f, t.Pos);
            if (t.Type == 2 || t.N == 0) { t.Timer = -1f; Story.Flags.Add(t.Flag); if (t.Type != 2 && t.Sw >= 0) OotYdanObj.SetFlag(t.Sw, true); return; }
            int lit = 0; foreach (var o in torches) if (o.Type == 1 && o.Sw == t.Sw && o.Lit) lit++;
            if (lit >= t.N) { OotYdanObj.SetFlag(t.Sw, true); foreach (var o in torches) if (o.Type == 1 && o.Sw == t.Sw) o.Timer = -1f; OotSfx.Play("NA_SE_SY_CORRECT_CHIME", 1f); }
            else t.Timer = (t.N * 50 + 110) / 20f;
        }
        static Vector3 FlamePos(Torch t) { return t.Pos + Vector3.up * (67f * S); }      // ObjSyokudai: tipToFlame.y -= 67
        // Fernsteuerung "oot torch" (Liste, naechste zuerst) / "oot torch light <i>" (anzuenden wie mit dem Stab, Gruppenlogik inklusive)
        public static string LastLand = "-";
        public static float BrokeUnder = -10f;          // Zeitpunkt: Netz unter Link gerissen (OotPlay: kein Auto-Sprung, Link faellt durch)
        public static string WebRemote()
        {
            var p = Player.m_localPlayer; var sb = new System.Text.StringBuilder("netze: ");
            foreach (var w in webs)
                sb.Append((w.Floor ? "boden " : "wand ") + w.Model + " " + (w.Gone ? "weg" : w.Go == null ? "ohne-go" : "da") + " merker " + (w.Flag ?? "-")
                          + (w.BreakT >= 0f ? " reisst" : "") + (w.BurnT >= 0f ? " brennt" : "") + (p != null ? " " + (w.Pos - p.transform.position).magnitude.ToString("F1") + " m" : "") + " | ");
            return sb.Append("letzte landung: " + LastLand).ToString();
        }

        public static string TorchRemote(string[] a)
        {
            if (a.Length > 3 && a[2] == "light") { int i = int.Parse(a[3]); if (i < 0 || i >= torches.Count) return "keine Fackel " + i; LightTorch(torches[i]); return "fackel " + i + " an"; }
            var p = Player.m_localPlayer; var sb = new System.Text.StringBuilder();
            var idx = new List<int>(); for (int i = 0; i < torches.Count; i++) idx.Add(i);
            if (p != null) idx.Sort((x, y) => (torches[x].Pos - p.transform.position).sqrMagnitude.CompareTo((torches[y].Pos - p.transform.position).sqrMagnitude));
            foreach (var i in idx) { var t = torches[i]; sb.Append(i + ": typ " + t.Type + (t.Lit ? " an" : " aus") + " merker " + t.Sw + " n " + t.N + " t " + t.Timer.ToString("F1") + (p != null ? " " + (t.Pos - p.transform.position).magnitude.ToString("F0") + " m" : "") + " | "); }
            return sb.ToString();
        }

        // ---------- Boden-Netz (BgYdanSp_FloorWebIdle): federt beim Drauflaufen, reisst bei Fall > 750 Einheiten ----------
        static void WebUpdate(Web w, Player p)
        {
            if (w.BurnT >= 0f)
            {
                w.BurnT += Time.deltaTime;
                float k = Mathf.Clamp01(w.BurnT / 1.5f);
                w.Go.transform.localScale = Vector3.one * (S * 0.1f) * (1f - k);
                if (w.BurnT > 1.5f) { w.Gone = true; return; }
                return;
            }
            if (!w.Floor) return;
            if (w.BreakT >= 0f)
            {
                w.BreakT += Time.deltaTime;
                // reisst nach unten durch (sin-Auslenkung 200 Einheiten), dann weg
                float dy = Mathf.Sin(Mathf.Min(w.BreakT * 1.5f, 1f) * Mathf.PI * 0.5f) * 200f * S;
                w.Go.transform.position = w.Pos - Vector3.up * dy;
                foreach (var c in w.Go.GetComponentsInChildren<Collider>()) c.enabled = false;
                if (w.BreakT > 2f) w.Gone = true;
                return;
            }
            // Federn (sin * Auslenkung, Bild 14er-Takt)
            w.T += Time.deltaTime * 20f;
            if (w.T >= 1f) { int n = (int)w.T; w.T -= n; w.Timer -= n; while (w.Timer <= 0) w.Timer += 14; w.Amp = Mathf.Max(0f, w.Amp - 0.8f * n); }
            w.Go.transform.position = w.Pos + Vector3.up * (Mathf.Sin(w.Timer * Mathf.PI / 7f) * w.Amp * S);
            var lp = p.transform.position;
            float dxz = new Vector2(lp.x - w.Pos.x, lp.z - w.Pos.z).magnitude;
            if (dxz < 140f * S && Mathf.Abs(lp.y - w.Pos.y) < 0.6f && p.IsOnGround() && OotPlay.Speed > 0.2f) { if (w.Amp < 0.1f) w.Timer = 14; w.Amp = Mathf.Max(w.Amp, 2f); }
        }

        /// <summary>Sprung vom 3. Stock aufs Netz im 1. Stock: Unser Laufsprung ist 1,5x so schnell wie in OoT (Tempo-Faktor in
        /// OotPlay), damit flog Link ueber das ganze Loch hinweg. Bei einem tiefen Fall (> 600 Einheiten) ueber einem heilen
        /// Boden-Netz lenkt ihn ein sanfter Zug zur Netzmitte, damit er wie im Original darauf landet.</summary>
        public static void WebPull(Vector3 pos, float vy, ref Vector3 airVel, float dt)
        {
            if (vy > 0.5f) return;
            foreach (var w in webs)
            {
                if (!w.Floor || w.Gone || w.Go == null || w.BreakT >= 0f || w.BurnT >= 0f) continue;
                float h = pos.y - w.Pos.y;
                if (h < 300f * S || h > 1000f * S) continue;
                var to = new Vector3(w.Pos.x - pos.x, 0f, w.Pos.z - pos.z);
                if (to.magnitude > 330f * S) continue;
                // bis zur Landung verbleibende Zeit grob (Valheim-Schwerkraft ~20 m/s^2), dahin die Mitte erreichen
                float tf = Mathf.Max(0.25f, (vy * -1f + Mathf.Sqrt(vy * vy + 40f * h)) / 20f);
                var want = to / tf;
                if (want.magnitude > 6f) want = want.normalized * 6f;
                airVel = Vector3.MoveTowards(airVel, new Vector3(want.x, airVel.y, want.z), 9f * dt);
                return;
            }
        }

        /// <summary>Link landet nach einem Fall (Hoehe in m). true = auf einem Netz, das jetzt reisst (kein Fallschaden).</summary>
        public static bool Land(Vector3 pos, float fall)
        {
            LastLand = "fall " + (fall / S).ToString("F0") + " bei " + pos.ToString("F1");
            foreach (var w in webs)
            {
                if (!w.Floor || w.Gone || w.Go == null || w.BreakT >= 0f || w.BurnT >= 0f) continue;
                float dxz = new Vector2(pos.x - w.Pos.x, pos.z - w.Pos.z).magnitude;
                if (dxz > 150f * S || Mathf.Abs(pos.y - w.Pos.y) > 1.2f) continue;
                float sq = Mathf.Sqrt(Mathf.Max(0f, fall / S));
                // Original: < 80 Einheiten von der Mitte. Unser Absprung vom 3. Stock fliegt im Laufen weiter (landet ~160 von der
                // Mitte) -> sonst federt Link nur und kommt nicht nach B1. Darum: ganzes Netz zaehlt.
                if (fall / S > 750f && dxz < 150f * S)
                {
                    w.BreakT = 0f; BrokeUnder = Time.time;
                    OotSfx.Play("NA_SE_EV_WEB_BROKEN", 1f, w.Pos);
                    Solved(w.Flag);
                    return true;
                }
                if (2f * sq > w.Amp && 2f * sq > 2f) { w.Amp = 2f * sq; w.Timer = 14; OotSfx.Play("NA_SE_EV_WEB_VIBRATION", 0.8f, w.Pos); }
                return true;
            }
            return false;
        }

        static void Solved(string flag)
        {
            if (!string.IsNullOrEmpty(flag)) { Story.Flags.Add(flag); Story.Save(); }
            OotSfx.Play("NA_SE_SY_CORRECT_CHIME", 0.9f);
        }

        /// <summary>Brennender Deku-Stab an dieser Stelle: Netze verbrennen, erloschene Fackeln gehen an.</summary>
        public static void Burn(Vector3 tip)
        {
            foreach (var w in webs)
            {
                if (w.Gone || w.Go == null || w.BurnT >= 0f || w.BreakT >= 0f) continue;
                bool hit;
                if (w.Floor)
                {
                    var c = w.Pos - Vector3.up * (50f * S);
                    hit = new Vector2(tip.x - c.x, tip.z - c.z).magnitude < 70f * S + 0.6f && Mathf.Abs(tip.y - c.y) < 50f * S + 0.6f;
                }
                else
                {
                    var loc = Quaternion.Inverse(OotEnemies.Rot(w.Rx, w.Ry, 0)) * (tip - w.Pos);
                    hit = Mathf.Abs(loc.x) < 100f * S + 0.3f && loc.y < 200f * S && loc.y > -0.4f && Mathf.Abs(loc.z) < 0.9f;
                }
                if (!hit) continue;
                w.BurnT = 0f;
                foreach (var col in w.Go.GetComponentsInChildren<Collider>()) col.enabled = false;
                var f = FireFx.Spawn(w.Floor ? w.Pos : w.Pos + Vector3.up * 1.6f, 0.9f, 60f, true);
                f.transform.SetParent(holder.transform, true);
                UnityEngine.Object.Destroy(f, 2.2f);
                OotSfx.Play("NA_SE_EV_FLAME_IGNITION", 1f, w.Pos);
                Solved(w.Flag);
            }
            foreach (var t in torches)
            {
                if (t.Lit || t.Type == 0) continue;               // goldene Fackel: nur per Schalter
                var dd = tip - FlamePos(t); if (new Vector2(dd.x, dd.z).magnitude < 1.0f && dd.y > -3.1f && dd.y < 0.6f) LightTorch(t);
            }
        }

        /// <summary>Steckt die Stab-Spitze in einer brennenden Fackel?</summary>
        static List<string> HintPages(string id)
        {
            var l = new List<string>(); int guard = 0;
            while (!string.IsNullOrEmpty(id) && guard++ < 8) { object t; if (!hintTexts.TryGetValue(id, out t)) break; foreach (var s in Json.L(t, "pages") ?? new List<object>()) l.Add(s.ToString()); id = Json.S(t, "next"); }
            return l;
        }
        static void HintTick(Player p)
        {
            var me = p.transform.position; Hint inside = null;
            foreach (var h in hints)
            {
                if (!string.IsNullOrEmpty(h.Flag) && Story.Flags.Contains(h.Flag)) continue;
                if (h.KillSw >= 0 && OotYdanObj.Flag(h.KillSw)) continue;          // Elf_Msg2: Schalter gesetzt (z. B. Fackel an)
                var d = me - h.P; if (d.y < -0.2f || d.y > h.H) continue;
                bool hit = h.Cuboid ? (Mathf.Abs(d.x) < h.R && Mathf.Abs(d.z) < h.R) : new Vector2(d.x, d.z).magnitude < h.R;
                if (hit) { inside = h; break; }
            }
            if (inside != pending && inside != null && !inside.Forced) OotSfx.Play("NA_SE_VO_NA_HELLO_" + UnityEngine.Random.Range(0, 3), 0.9f);
            pending = inside; NaviCalls = inside != null && !inside.Forced;
            if (inside == null || !OotPlay.Driving || OotPlay.InDialog || !OotPlay.Calm) return;
            if (inside.Forced || OotPlay.CUpDown)
            {
                var h = inside; pending = null; NaviCalls = false;
                if (!string.IsNullOrEmpty(h.Flag)) { Story.Flags.Add(h.Flag); Story.Save(); }
                // alle Hinweise mit demselben Schalter verschwinden (Elf_Msg: Flags_SetSwitch + KillCheck)
                if (h.Forced) OotSfx.Play("NA_SE_VO_NAVY_CALL", 0.9f);
                OotPlay.StartDialog(HintPages(h.Text), 0, null);
            }
        }

        public static bool LitTorchAt(Vector3 tip)
        {
            foreach (var t in torches) { if (!t.Lit) continue; var d = tip - FlamePos(t); if (new Vector2(d.x, d.z).magnitude < 1.0f && d.y > -3.1f && d.y < 0.6f) return true; }      // Kind-Link haelt den Stab hier tiefer als im Original
            return false;
        }

        /// <summary>Wasser an dieser Stelle: Oberflaeche y.</summary>
        // Bewegliche Objekte (OotYdanObj): Leiter als Kletterflaeche, Wasserspiegel (Bg_Ydan_Hasi)
        public static Climb AddClimb(Vector3 a, Vector3 b, Vector3 c, int type)
        {
            var cl = new Climb { A = a, B = b, C = c, T = type };
            cl.N = Vector3.Cross(b - a, c - a).normalized;
            cl.Min = Vector3.Min(a, Vector3.Min(b, c)) - Vector3.one * 0.4f; cl.Max = Vector3.Max(a, Vector3.Max(b, c)) + Vector3.one * 0.4f;
            climbs.Add(cl); return cl;
        }
        public static void RemoveClimb(Climb c) { climbs.Remove(c); }
        public static bool WaterY(int idx, out float y) { if (idx >= 0 && idx < waters.Count) { y = waters[idx].Y; return true; } y = 0f; return false; }
        public static void SetWaterY(int idx, float y) { if (idx >= 0 && idx < waters.Count) waters[idx].Y = y; }

        public static bool WaterAt(Vector3 pos, out float y)
        {
            foreach (var w in waters)
                if (pos.x >= w.X0 && pos.x <= w.X1 && pos.z >= w.Z0 && pos.z <= w.Z1 && pos.y < w.Y + 0.5f) { y = w.Y; return true; }
            y = 0f; return false;
        }

        /// <summary>Kletterflaeche (Ranken/Leiter) vor Link: Punkt auf der Flaeche und ihre Normale (zur offenen Seite).</summary>
        public static bool ClimbAt(Vector3 from, Vector3 dir, float reach, out Vector3 point, out Vector3 normal, out int type)
        {
            point = Vector3.zero; normal = Vector3.zero; type = 0;
            float best = reach;
            foreach (var c in climbs)
            {
                if (from.x < c.Min.x - reach || from.x > c.Max.x + reach || from.y < c.Min.y - reach || from.y > c.Max.y + reach || from.z < c.Min.z - reach || from.z > c.Max.z + reach) continue;
                var n = c.N;
                if (Vector3.Dot(n, dir) > 0f) n = -n;                 // Seite, von der Link kommt
                if (Vector3.Dot(n, -dir) < 0.5f) continue;
                float dist = Vector3.Dot(from - c.A, n);
                if (dist < -0.2f || dist > best) continue;
                var q = from - n * dist;
                if (!Inside(c, q, 0.25f)) continue;
                best = dist; point = q; normal = n; type = c.T;
            }
            return best < reach;
        }

        static bool Inside(Climb c, Vector3 p, float margin)
        {
            var v0 = c.C - c.A; var v1 = c.B - c.A; var v2 = p - c.A;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01; if (Mathf.Abs(den) < 1e-6f) return false;
            float u = (d11 * d20 - d01 * d21) / den, v = (d00 * d21 - d01 * d20) / den;
            float m = margin / Mathf.Sqrt(Mathf.Max(d00, d11));
            return u >= -m && v >= -m && u + v <= 1f + m;
        }
    }
}
