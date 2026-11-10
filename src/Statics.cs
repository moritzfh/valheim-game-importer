using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Feste Originalmodelle aus dem Spielpaket (Haeuser, Burgmauern, Zaeune, Felsen ...): statics.json im Paket.
    // Jeder Spieler baut sie lokal nach (kein Netzwerk-Objekt), nur in Sichtweite; mit Kollision, damit man drauf/rein kann.
    //   [{"m": "statics/meshes/x.glb", "p": [x, y, z], "q": [x, y, z, w], "s": [sx, sy, sz], "r": Radius}, ...]
    public static class Statics
    {
        class S { public string Model; public Vector3 P; public Quaternion Q; public Vector3 Sc; public float R, Vis; public bool Col = true; public GameObject Go; public string[] Tags; public List<object> Kit; }
        static readonly List<S> all = new List<S>();
        static readonly Dictionary<long, List<S>> grid = new Dictionary<long, List<S>>();
        static readonly HashSet<string> broken = new HashSet<string>();
        static readonly List<S> live = new List<S>();
        static Pack forPack;
        static float tickT;
        static int layer = -1;
        const float Cell = 128f, MaxVis = 900f;

        public static int Count { get { return all.Count; } }
        public static int Live { get { return live.Count; } }

        static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }

        static void Load(Pack pk)
        {
            Clear();
            forPack = pk;
            if (pk == null) return;
            // statics.json + Zusaetze (statics_*.json, z. B. Quest-Schauplaetze)
            var files = new List<string>();
            if (File.Exists(Path.Combine(pk.Dir, "statics.json"))) files.Add(Path.Combine(pk.Dir, "statics.json"));
            try { foreach (var x in Directory.GetFiles(pk.Dir, "statics_*.json")) files.Add(x); } catch (Exception e) { Plugin.Quiet(e, "Statics.files"); }
            foreach (var f in files) LoadFile(pk, f);
        }

        static void LoadFile(Pack pk, string f)
        {
            try
            {
                var arr = Json.Parse(File.ReadAllText(f)) as List<object>;
                if (arr == null) return;
                int n0 = all.Count;
                foreach (var o in arr)
                {
                    var p = Json.L(o, "p"); var q = Json.L(o, "q"); var s = Json.L(o, "s");
                    if (p == null || p.Count < 3) continue;
                    var kit = Json.L(o, "kit");
                    var it = new S
                    {
                        Kit = kit,
                        Model = kit != null ? "kit:" + Path.GetFileName(f) + "#" + all.Count : Path.Combine(pk.Dir, Json.S(o, "m", "").Replace('/', Path.DirectorySeparatorChar)),
                        P = new Vector3(F(p[0]), F(p[1]), F(p[2])),
                        Q = q != null && q.Count == 4 ? new Quaternion(F(q[0]), F(q[1]), F(q[2]), F(q[3])) : Quaternion.identity,
                        Sc = s != null && s.Count == 3 ? new Vector3(F(s[0]), F(s[1]), F(s[2])) : Vector3.one,
                        R = Json.F(o, "r", 2f),
                        Col = !Json.Has(o, "c") || Json.F(o, "c", 1f) > 0.5f
                    };
                    var tl = Json.L(o, "t");
                    if (tl != null && tl.Count > 0) { it.Tags = new string[tl.Count]; for (int k = 0; k < tl.Count; k++) it.Tags[k] = tl[k] as string ?? ""; }
                    // Sichtweite nach Groesse: Burgmauer weit, Becher nah
                    it.Vis = Json.Has(o, "vis") ? Json.F(o, "vis", 200f) : Mathf.Clamp(40f + it.R * 22f, 50f, MaxVis);
                    all.Add(it);
                    int cx = Mathf.FloorToInt(it.P.x / Cell), cz = Mathf.FloorToInt(it.P.z / Cell);
                    List<S> l; if (!grid.TryGetValue(Key(cx, cz), out l)) grid[Key(cx, cz)] = l = new List<S>();
                    l.Add(it);
                }
                Plugin.Log("Feste Modelle: " + (all.Count - n0) + " aus " + f);
            }
            catch (Exception e) { Plugin.Warn(Path.GetFileName(f) + ": " + e.Message); }
        }

        public static void Clear()
        {
            foreach (var it in live) if (it.Go != null) UnityEngine.Object.Destroy(it.Go);
            live.Clear(); all.Clear(); grid.Clear(); forPack = null; queue.Clear(); qi = 0;
        }

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) Load(pk);
            var pl = Player.m_localPlayer;
            if (pk == null || pl == null || all.Count == 0) return;
            // Liste alle 0,25 s neu, gebaut wird jeden Frame ein Stueck (mit Zeitbudget, damit es nicht ruckelt)
            if (Time.time >= tickT)
            {
                tickT = Time.time + 0.25f;
                var cam = Camera.main != null ? Camera.main.transform.position : pl.transform.position;
                var me = MapImport.LookOn ? MapImport.LookPos : cam;
                // Punkte, um die herum gebaut wird: Kamera + Extra (z. B. alle Kamerapositionen einer Cutscene, vorab)
                pts.Clear(); pts.Add(me); pts.AddRange(Extra);
                // weg, was zu weit ist
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    var it = live[i];
                    if (it.Go == null || Near(it.P) > it.Vis + 30f) { if (it.Go != null) UnityEngine.Object.Destroy(it.Go); it.Go = null; live.RemoveAt(i); }
                }
                // dazu, was nah genug ist: naechste zuerst
                int r = Mathf.CeilToInt(MaxVis / Cell);
                var cand = new List<KeyValuePair<float, S>>();
                var seenS = new HashSet<S>();
                foreach (var pt in pts)
                {
                    int mx = Mathf.FloorToInt(pt.x / Cell), mz = Mathf.FloorToInt(pt.z / Cell);
                    for (int dz = -r; dz <= r; dz++)
                        for (int dx = -r; dx <= r; dx++)
                        {
                            List<S> l;
                            if (!grid.TryGetValue(Key(mx + dx, mz + dz), out l)) continue;
                            foreach (var it in l)
                            {
                                if (it.Go != null || broken.Contains(it.Model) || seenS.Contains(it)) continue;
                                float d = Flat(pt, it.P);
                                if (d < it.Vis) { seenS.Add(it); cand.Add(new KeyValuePair<float, S>(Near(it.P) - it.R, it)); }
                            }
                        }
                }
                cand.Sort((a, b) => a.Key.CompareTo(b.Key));
                QueueBuiltT = Time.time;
                Glb.PurgePrefetch();
                queue.Clear(); qi = 0;
                foreach (var kv in cand) queue.Add(kv.Value);
                // die naechsten noch nie geladenen Modelle schon im Hintergrund lesen
                int pf = 0; var seen = new HashSet<string>();
                for (int i = 0; i < queue.Count && pf < 12; i++)
                {
                    var m = queue[i].Model;
                    if (!seen.Add(m) || Glb.IsCached(m)) continue;
                    Glb.Prefetch(m); pf++;
                }
            }
            var sw = Stopwatch.StartNew();
            bool first = true;
            while (qi < queue.Count && (first || sw.ElapsedMilliseconds < 6))
            {
                var it = queue[qi++];
                if (it.Go != null || broken.Contains(it.Model)) continue;
                Spawn(it); first = false;
            }
        }

        // zusaetzliche Punkte (Cutscene-Kameras), Zeitpunkt der letzten Liste
        public static readonly List<Vector3> Extra = new List<Vector3>();
        public static float QueueBuiltT;
        static readonly List<Vector3> pts = new List<Vector3>();
        static float Near(Vector3 p) { float m = float.MaxValue; foreach (var q in pts) m = Mathf.Min(m, Flat(q, p)); return m; }

        static readonly List<S> queue = new List<S>();
        static int qi;
        public static int Pending { get { return queue.Count - qi; } }

        static void Spawn(S it)
        {
            try
            {
                GameObject go;
                if (it.Kit != null) { go = Kit.Build(it.Kit, it.P); if (go == null) { broken.Add(it.Model); return; } }
                else
                {
                    if (!File.Exists(it.Model)) { broken.Add(it.Model); Plugin.Warn("Modell fehlt: " + it.Model); return; }
                    go = Glb.Instantiate(it.Model, true);
                }
                go.transform.position = it.P; if (it.Kit == null) { go.transform.rotation = it.Q; go.transform.localScale = it.Sc; }
                if (layer < 0) { layer = LayerMask.NameToLayer("static_solid"); if (layer < 0) layer = 0; }
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
                if (it.Col && it.R > 0.6f)
                    foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                        if (mf.sharedMesh != null && mf.GetComponent<Collider>() == null)
                        {
                            Mesh cm; if (!Glb.ColMesh.TryGetValue(mf.sharedMesh, out cm)) cm = mf.sharedMesh;      // ohne Wasser
                            if (cm.triangles.Length > 0) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = cm;
                        }
                it.Go = go;
                if (Path.GetFileName(it.Model).ToLowerInvariant().Contains("water_mesh")) WaterLook(go);
                if (it.Col && Door.IsDoor(it.Model)) { try { Door.Attach(go, it.Model, it.Tags, it.P); } catch (Exception e) { Plugin.Quiet(e, "Door.Attach"); } }
                if (Hidden(it)) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                // "t": ["shadow2"]: Schatten beidseitig (Innenraum mit nur nach innen sichtbarer Decke, z. B. Links Haus)
                if (it.Tags != null && Array.IndexOf(it.Tags, "shadow2") >= 0)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
                live.Add(it);
            }
            catch (Exception e) { broken.Add(it.Model); Plugin.Warn("Modell " + it.Model + ": " + e.Message); }
        }

        static Material waterMat;
        static void WaterLook(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (waterMat == null && r.sharedMaterial != null)
                {
                    waterMat = new Material(r.sharedMaterial) { name = "gi_still_water", mainTexture = null, color = new Color(0.09f, 0.13f, 0.12f) };
                    if (waterMat.HasProperty("_Glossiness")) waterMat.SetFloat("_Glossiness", 0.92f);
                    if (waterMat.HasProperty("_BumpMap")) waterMat.SetTexture("_BumpMap", null);
                }
                if (waterMat != null) { var ms = r.sharedMaterials; for (int i = 0; i < ms.Length; i++) ms[i] = waterMat; r.sharedMaterials = ms; }
            }
        }

        // Fehlersuche: Modelle mit <teil> im Namen aus-/einblenden ("statics hide hay", "statics show")
        static readonly List<string> hidden = new List<string>();
        public static string Hide(string part, bool on)
        {
            if (on && !string.IsNullOrEmpty(part)) hidden.Add(part.ToLowerInvariant()); else if (!on) hidden.Clear();
            int n = 0;
            foreach (var it in live) if (it.Go != null) { bool h = Hidden(it); if (h) n++; foreach (var r in it.Go.GetComponentsInChildren<Renderer>(true)) r.enabled = !h; }
            return "statics versteckt: " + string.Join(",", hidden.ToArray()) + " (" + n + " sichtbare Objekte)";
        }
        // Von einer Cutscene ausgeblendete Objekte (entToHideTags, z. B. der Badezuber in Yennefers Zimmer)
        static readonly HashSet<string> hiddenTags = new HashSet<string>();
        static bool TagHidden(S it) { if (hiddenTags.Count == 0 || it.Tags == null) return false; foreach (var t in it.Tags) if (hiddenTags.Contains(t)) return true; return false; }
        public static void HideTags(IEnumerable<string> tags)
        {
            hiddenTags.Clear(); if (tags != null) foreach (var t in tags) hiddenTags.Add(t);
            foreach (var it in live)
                if (it.Go != null && it.Tags != null)
                {
                    bool h = Hidden(it);
                    foreach (var r in it.Go.GetComponentsInChildren<Renderer>(true)) r.enabled = !h;
                    foreach (var c in it.Go.GetComponentsInChildren<Collider>(true)) c.enabled = !h;
                }
        }

        // Cutscene-Mover: alle geladenen Modelle mit diesem Tag auf Position/Drehung setzen (Zugbruecke faehrt runter)
        public static void Pose(string tag, Vector3 p, Quaternion q)
        {
            foreach (var it in live)
            {
                if (it.Go == null || it.Tags == null) continue;
                foreach (var t in it.Tags) if (t == tag) { it.Go.transform.SetPositionAndRotation(p, q); break; }
            }
        }

        // Einstellungen: Kleinkram in Kaesten ausblenden (Kisten, Leitern ...), grosse Bauteile (Radius > max) bleiben
        static readonly List<float[]> boxes = new List<float[]>();
        static bool BoxHidden(S it)
        {
            foreach (var b in boxes)
                if (it.R <= b[6] && it.P.x >= Mathf.Min(b[0], b[3]) && it.P.x <= Mathf.Max(b[0], b[3]) && it.P.y >= Mathf.Min(b[1], b[4]) && it.P.y <= Mathf.Max(b[1], b[4]) && it.P.z >= Mathf.Min(b[2], b[5]) && it.P.z <= Mathf.Max(b[2], b[5])) return true;
            return false;
        }
        public static void HideBoxes(List<float[]> l)
        {
            if (boxes.Count == 0 && (l == null || l.Count == 0)) return;
            boxes.Clear(); if (l != null) boxes.AddRange(l);
            foreach (var it in live)
                if (it.Go != null) { bool h = Hidden(it); foreach (var r in it.Go.GetComponentsInChildren<Renderer>(true)) r.enabled = !h; }
        }
        // fuer Effekte (Feuer an Gebaeuden): Modellname -> Objekt aller gerade geladenen festen Modelle
        public static IEnumerable<KeyValuePair<string, GameObject>> LiveModels()
        {
            foreach (var it in live) if (it.Go != null) yield return new KeyValuePair<string, GameObject>(it.Model, it.Go);
        }

        static bool Hidden(S it) { return IsHidden(it.Model) || TagHidden(it) || BoxHidden(it); }

        static bool IsHidden(string model) { if (hidden.Count == 0) return false; var m = model.ToLowerInvariant(); foreach (var h in hidden) if (m.Contains(h)) return true; return false; }

        // Fehlersuche: Modelle um einen Punkt mit ihren Materialien/Texturen ("statics near [x y z] [r]"), grosse zuerst
        public static string Near(Vector3 p, float r)
        {
            var hits = new List<KeyValuePair<float, string>>();
            foreach (var it in live)
            {
                if (it.Go == null) continue;
                foreach (var rd in it.Go.GetComponentsInChildren<Renderer>(true))
                {
                    if (!rd.enabled) continue;
                    var b = rd.bounds;
                    if ((b.ClosestPoint(p) - p).sqrMagnitude > r * r) continue;
                    var mats = new List<string>();
                    foreach (var m in rd.sharedMaterials)
                    {
                        if (m == null) { mats.Add("null"); continue; }
                        var tx = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
                        var col = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white;
                        mats.Add(m.name + "=" + (tx != null ? tx.name + "(" + tx.width + ")" : "OHNE") + (col != Color.white ? " c" + ColorUtility.ToHtmlStringRGB(col) : ""));
                    }
                    hits.Add(new KeyValuePair<float, string>(b.size.magnitude, System.IO.Path.GetFileNameWithoutExtension(it.Model) + "/" + rd.name + " " + b.size.ToString("F1") + " [" + string.Join("; ", mats.ToArray()) + "]"));
                }
            }
            hits.Sort((x, y) => y.Key.CompareTo(x.Key));
            var sb = new System.Text.StringBuilder("statics near " + p.ToString("F1") + " r" + r + ": " + hits.Count);
            for (int i = 0; i < hits.Count && i < 40; i++) sb.Append("\n  " + hits[i].Value);
            return sb.ToString();
        }

        // Fehlersuche: sichtbare Modelle, die in der Luft haengen oder riesig sind ("statics float [hoehe]")
        public static string Floating(float minGap)
        {
            var sb = new System.Text.StringBuilder(); int n = 0;
            foreach (var it in live)
            {
                if (it.Go == null) continue;
                var rs = it.Go.GetComponentsInChildren<Renderer>(true);
                if (rs.Length == 0) continue;
                var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds);
                float g = Pack.Ground(b.center.x, b.center.z);
                float gap = b.min.y - g, size = b.size.magnitude;
                if (gap < minGap && size < 400f) continue;
                n++;
                if (n <= 40) sb.Append("\n  " + Path.GetFileNameWithoutExtension(it.Model) + " p " + it.P.ToString("F1") + " min " + b.min.ToString("F0") + " max " + b.max.ToString("F0") + " boden " + g.ToString("F0") + " luecke " + gap.ToString("F0"));
            }
            return "schwebend/riesig: " + n + sb;
        }

        public static string Info()
        {
            return "statics " + all.Count + " gesamt, " + live.Count + " sichtbar, " + Pending + " wartend, " + Glb.PrefetchCount + " vorgeladen, " + broken.Count + " kaputt";
        }

        static float Flat(Vector3 a, Vector3 b) { float x = a.x - b.x, z = a.z - b.z; return Mathf.Sqrt(x * x + z * z); }
        static float F(object o) { return o is double ? (float)(double)o : Convert.ToSingle(o); }
    }
}
