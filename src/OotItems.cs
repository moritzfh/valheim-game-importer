using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Spielbare Original-Objekte im Kokiri-Wald (pack.json "oot_items", tools/oot/oot_items.py):
    //   Truhe mit dem Kokiri-Schwert (A = oeffnen), Buesche an den Original-Stellen (mit dem Schwert abschlagen -> Rubine),
    //   Rubine zum Einsammeln. Alles lokal (kein Netzwerk), wie die Deko.
    public static class OotItems
    {
        static Pack forPack;
        static object cfg;
        static bool built;
        static GameObject holder;
        // Truhe
        // Truhen (Kokiri-Schwert im Wald + Truhen im Deku-Baum); "cur" = die, vor der Link steht bzw. die gerade aufgeht
        class ChestE { public GameObject Base, Lid, OpenG, ClosedG; public Vector3 Pos, Front; public Quaternion LidClosed; public float LidT = -1f; public string[] Text; public string Key, Flag; }
        static readonly List<ChestE> chests = new List<ChestE>();
        static ChestE cur; static float lidOpen = 75f;
        public static string[] ChestText { get { return cur != null ? cur.Text : null; } }
        public static string ChestKey { get { return cur != null ? cur.Key : "sword"; } }
        public static string ChestFlag { get { return cur != null ? cur.Flag : SwordFlag; } }
        // Buesche / Rubine
        class Bush { public GameObject G; public Vector3 P; public float Respawn; }
        static readonly List<Bush> bushes = new List<Bush>();
        class Rupee { public GameObject G; public int Value; public float T0; public Vector3 P; }
        static readonly List<Rupee> rupees = new List<Rupee>();
        static GameObject bushProto; static EffectList bushFx;
        static Material rupeeMat, rupeeGlow; static Mesh rupeeMesh;
        public const string SwordFlag = "oot_sword";
        // Aufsammel-Dinge (En_Item00): Deku-Nuesse, Deku-Stab, Herz; Inventar wie gSaveContext (Anzahl je Gegenstand)
        class Pick { public GameObject G; public string Kind; public float T0; public Vector3 P; }
        static readonly List<Pick> picks = new List<Pick>();
        public static int Sticks, Nuts;
        public const int MAX_STICKS = 10, MAX_NUTS = 20;
        // Gegenstand hochhalten (Truhe oder erstes Aufsammeln): welches GI-Modell, welche Textseiten, welches Flag danach
        public static string GiKey = "sword"; public static string[] GiText; public static string GiFlag = SwordFlag;
        public static Action<string> FirstGet;          // OotPlay: Get-Item-Szene fuer diesen Gegenstand starten
        public static bool HasSword { get { return Story.Flags.Contains(SwordFlag); } }

        public static void Tick(Player p)
        {
            var pk = Pack.Current;
            if (pk != forPack) { Clear(); forPack = pk; cfg = pk != null ? Json.V(pk.Meta, "oot_items") : null; }
            if (cfg == null || p == null || ZNetScene.instance == null) return;
            if (!built) { try { Build(pk); } catch (Exception e) { Plugin.Warn("OoT-Objekte: " + e); } built = true; }
            CountsTick(p);
            // Deckel
            foreach (var ch in chests)
            {
                if (ch.LidT < 0f || ch.LidT > 1f) continue;
                ch.LidT += Time.deltaTime;
                if (ch.Lid == null) { ShowOpen(ch, true); continue; }                 // Valheim-Truhe: offen/zu wie in Valheim
                float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(ch.LidT / 0.9f));
                ch.Lid.transform.localRotation = ch.LidClosed * Quaternion.Euler(-lidOpen * k, 0f, 0f);
            }
            // Rubine: drehen, einsammeln
            var pp = p.transform.position;
            PickTick(pp);
            for (int i = rupees.Count - 1; i >= 0; i--)
            {
                var r = rupees[i];
                if (r.G == null) { rupees.RemoveAt(i); continue; }
                float t = Time.time - r.T0;
                r.G.transform.position = r.P + Vector3.up * (0.25f + 0.06f * Mathf.Sin(t * 3f) + (t < 0.5f ? Mathf.Sin(t / 0.5f * Mathf.PI) * 0.6f : 0f));
                r.G.transform.rotation = Quaternion.Euler(0f, t * 200f, 0f);
                var d = r.G.transform.position - pp; float dy = d.y; d.y = 0f;
                if (t > 0.45f && d.magnitude < 1.05f && dy > -0.6f && dy < 1.9f)
                {
                    OotHud.Rupees = Mathf.Min(99, OotHud.Rupees + r.Value);
                    OotSfx.Play("NA_SE_SY_GET_RUPY", 0.9f);
                    UnityEngine.Object.Destroy(r.G); rupees.RemoveAt(i);
                }
            }
        }

        static void PickTick(Vector3 pp)
        {
            for (int i = picks.Count - 1; i >= 0; i--)
            {
                var k = picks[i];
                if (k.G == null) { picks.RemoveAt(i); continue; }
                float t = Time.time - k.T0;
                // Herz schwebt pendelnd herab, Nuesse/Stab liegen (Item_DropCollectible: kleiner Hopser)
                float hop = t < 0.5f ? Mathf.Sin(t / 0.5f * Mathf.PI) * 0.5f : 0f;
                k.G.transform.position = k.P + Vector3.up * (k.Kind == "heart" ? 0.35f + 0.05f * Mathf.Sin(t * 2.5f) : 0.08f) + Vector3.up * hop;
                k.G.transform.rotation = Quaternion.Euler(0f, k.Kind == "heart" ? Mathf.Sin(t * 1.6f) * 50f : t * 120f, 0f);
                // nach 15 s blinken, nach 18 s weg (EnItem00 despawn)
                if (t > 18f) { UnityEngine.Object.Destroy(k.G); picks.RemoveAt(i); continue; }
                if (t > 15f) k.G.SetActive(((int)(t * 10f)) % 2 == 0); else if (!k.G.activeSelf) k.G.SetActive(true);
                var d = k.G.transform.position - pp; float dy = d.y; d.y = 0f;
                if (t < 0.4f || d.magnitude > 0.95f || dy < -0.6f || dy > 1.9f) continue;
                bool took = Take(k.Kind);
                if (!took) continue;
                UnityEngine.Object.Destroy(k.G); picks.RemoveAt(i);
            }
        }

        /// <summary>Aufheben: true wenn genommen (volle Taschen = liegen lassen). Erstes Mal: Get-Item-Szene mit Original-Text.</summary>
        public static bool Take(string kind)
        {
            switch (kind)
            {
                case "heart":
                    OotCombat.Heal(0x10); OotSfx.Play("NA_SE_SY_GET_ITEM", 0.8f); return true;
                case "nuts":
                {
                    bool first = !Story.Flags.Contains("oot_has_nuts");
                    if (!first && Nuts >= MAX_NUTS) return false;
                    Nuts = Mathf.Min(MAX_NUTS, Nuts + 5);
                    if (first && FirstGet != null) { Story.Flags.Add("oot_has_nuts"); Story.Save(); FirstGet("nuts"); }
                    else OotSfx.Play("NA_SE_SY_GET_ITEM", 0.8f);
                    return true;
                }
                case "stick":
                {
                    bool first = !Story.Flags.Contains("oot_has_sticks");
                    if (!first && Sticks >= MAX_STICKS) return false;
                    Sticks = Mathf.Min(MAX_STICKS, Sticks + 1);
                    if (first && FirstGet != null) { Story.Flags.Add("oot_has_sticks"); Story.Save(); FirstGet("stick"); }
                    else OotSfx.Play("NA_SE_SY_GET_ITEM", 0.8f);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Wirkung eines Truhen-Gegenstands nach dem Hochhalten (Herz heilt, Schleuder + 30 Samen, Karte/Kompass merken).</summary>
        public static int Seeds;
        public static void ApplyGet(string key)
        {
            switch (key)
            {
                case "heart": OotCombat.Heal(0x10); break;
                case "slingshot": Story.Flags.Add("oot_slingshot"); Seeds = Mathf.Max(Seeds, 30); break;
                case "map": Story.Flags.Add("oot_ydan_map"); break;
                case "compass": Story.Flags.Add("oot_ydan_compass"); break;
            }
        }

        /// <summary>Etwas fallen lassen (Gegner, Busch): "nuts", "stick", "heart", "rupee".</summary>
        public static void DropAt(Vector3 p, string kind)
        {
            if (holder == null) return;
            if (kind == "rupee") { Drop(p, 1, true); return; }
            GameObject g = null;
            var rel = cfg != null ? Json.S(Json.V(cfg, "pickups"), kind) : null;
            if (!string.IsNullOrEmpty(rel)) g = Part(Pack.Current, rel, p, Quaternion.identity);
            if (g == null)
            {
                g = GameObject.CreatePrimitive(kind == "stick" ? PrimitiveType.Capsule : PrimitiveType.Sphere);
                UnityEngine.Object.Destroy(g.GetComponent<Collider>());
                g.transform.localScale = kind == "stick" ? new Vector3(0.06f, 0.35f, 0.06f) : Vector3.one * 0.18f;
                var r = g.GetComponent<Renderer>();
                r.material.color = kind == "heart" ? new Color(1f, 0.2f, 0.2f) : new Color(0.55f, 0.36f, 0.18f);
                g.transform.SetParent(holder.transform, true);
            }
            foreach (var c in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            try { float h = MapImport.Height(p.x, p.z); if (Mathf.Abs(h - p.y) < 3f) p.y = Mathf.Max(p.y, h); } catch { }
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 1f, Vector3.down, out hit, 4f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore)) p.y = hit.point.y;
            picks.Add(new Pick { G = g, Kind = kind, T0 = Time.time, P = p });
        }

        /// <summary>Textseiten zum Gegenstand (pack.json oot_items.gi_texts, Original-Texte aus dem ROM).</summary>
        public static string[] Pages(string key)
        {
            var l = cfg != null ? Json.L(Json.V(cfg, "gi_texts"), key) : null;
            if (l == null) return key == "sword" ? ChestText : null;
            var a = new string[l.Count]; for (int i = 0; i < l.Count; i++) a[i] = l[i].ToString();
            return a;
        }

        static void Clear()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; built = false; chests.Clear(); cur = null; bushes.Clear(); rupees.Clear(); picks.Clear(); gis.Clear(); gi = null;
        }

        static void Build(Pack pk)
        {
            holder = new GameObject("gi_oot_items");
            // Truhe
            var c = Json.V(cfg, "chest");
            if (c != null) MakeChest(pk, c, c, "sword", SwordFlag);
            // weitere Truhen (Deku-Baum): {"pos", "yaw", "key", "flag", "text": [...]}; Modell/Versatz wie die Schwert-Truhe
            foreach (var e in Json.L(cfg, "chests") ?? new List<object>())
                if (c != null) MakeChest(pk, e, c, Json.S(e, "key", "heart"), Json.S(e, "flag", ""));
            if (chests.Count > 0) cur = chests[0];
            // Buesche: Valheims runder Busch als Bild, ohne Netzwerk/Kollision
            var pf = ZNetScene.instance.GetPrefab("Bush01");
            if (pf != null)
            {
                var de = pf.GetComponent<Destructible>(); if (de != null) bushFx = de.m_destroyedEffect;
                var hold = new GameObject("gi_bush_proto"); hold.SetActive(false); hold.transform.SetParent(holder.transform, false);
                bushProto = UnityEngine.Object.Instantiate(pf, hold.transform);
                foreach (var mb in bushProto.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var col in bushProto.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(col);
                foreach (var rb in bushProto.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
            }
            foreach (var b in Json.L(cfg, "bushes") ?? new List<object>())
            {
                var v = V3((List<object>)b);
                try { float h = MapImport.Height(v.x, v.z); if (Mathf.Abs(h - v.y) < 3f) v.y = h; } catch { }
                var bu = new Bush { P = v }; SpawnBush(bu); bushes.Add(bu);
            }
            foreach (var r in Json.L(cfg, "rupees") ?? new List<object>())
            {
                var v = V3(Json.L(r, "pos")); var kind = Json.S(r, "kind", "green");
                Drop(v, kind == "red" ? 20 : kind == "blue" ? 5 : 1, false);
            }
            Plugin.Log("OoT-Objekte: " + chests.Count + " Truhen, " + bushes.Count + " Buesche, " + rupees.Count + " Rubine");
        }

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }

        static GameObject Part(Pack pk, string rel, Vector3 pos, Quaternion rot)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            var f = Path.Combine(pk.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(f)) { Plugin.Warn("OoT-Objekt fehlt: " + f); return null; }
            var g = Glb.Instantiate(f, true);
            g.transform.SetParent(holder.transform, true); g.transform.position = pos; g.transform.rotation = rot;
            return g;
        }

        static void SpawnBush(Bush b)
        {
            if (bushProto == null) return;
            b.G = UnityEngine.Object.Instantiate(bushProto, holder.transform);
            b.G.transform.position = b.P; b.G.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            b.G.transform.localScale = Vector3.one * 0.9f;
            b.G.SetActive(true);
        }

        // Schwertschlag: Buesche vor Link abschlagen (wie En_Kusa: Blaetter fliegen, manchmal ein Rubin)
        public static int Hit(Vector3 pos, float yaw)
        {
            int n = 0; var fw = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            foreach (var b in bushes)
            {
                if (b.G == null || !b.G.activeSelf) continue;
                var d = b.P - pos; d.y = 0f;
                if (d.magnitude > 1.9f || (d.magnitude > 0.5f && Vector3.Dot(d.normalized, fw) < 0.1f)) continue;
                b.G.SetActive(false); b.Respawn = Time.time + 60f; n++;
                try { if (bushFx != null && !OotSfx.Has("NA_SE_EV_PLANT_BROKEN")) BrightLeaves(bushFx.Create(b.P + Vector3.up * 0.4f, Quaternion.identity)); } catch { }
                try { if (OotSfx.Has("NA_SE_EV_PLANT_BROKEN")) { BrightLeaves(BushFxNoSound(b.P + Vector3.up * 0.4f)); OotSfx.Play("NA_SE_EV_PLANT_BROKEN", 1f, b.P); } } catch { }
                // Herz, wenn Link angeschlagen ist (wie die Zufalls-Drops von En_Kusa), sonst meist ein Rubin
                if (OotCombat.Hp < OotCombat.MaxHp && UnityEngine.Random.value < 0.35f) DropAt(b.P, "heart");
                else if (UnityEngine.Random.value < 0.8f) Drop(b.P, UnityEngine.Random.value < 0.85f ? 1 : 5, true);
            }
            return n;
        }

        static GameObject[] BushFxNoSound(Vector3 pos)
        {
            if (bushFx == null || bushFx.m_effectPrefabs == null) return null;
            var res = new List<GameObject>();
            foreach (var e in bushFx.m_effectPrefabs)
            {
                if (e == null || !e.m_enabled || e.m_prefab == null) continue;
                if (e.m_prefab.GetComponentInChildren<ZSFX>() != null || e.m_prefab.GetComponentInChildren<AudioSource>() != null) continue;   // Valheim-Geraeusch weglassen
                res.Add(UnityEngine.Object.Instantiate(e.m_prefab, pos, Quaternion.identity));
            }
            return res.ToArray();
        }

        // Valheims Blatt-Partikel sind beleuchtet -> von hinten pechschwarz. Wie in OoT: unbeleuchtete, helle Blaetter.
        static bool leafLogged;
        static void BrightLeaves(GameObject[] gos)
        {
            if (gos == null) return;
            var sh = Shader.Find("Sprites/Default");
            foreach (var g in gos)
            {
                if (g == null) continue;
                foreach (var pr in g.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    var m = pr.material; if (m == null || m.shader == null) continue;
                    string sn = m.shader.name;
                    if (!leafLogged) Plugin.Log("OoT-Busch-Partikel: " + pr.name + " / " + sn + " / " + m.name);
                    if (sh == null || sn.IndexOf("unlit", StringComparison.OrdinalIgnoreCase) >= 0 || sn.IndexOf("additive", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var tex = m.mainTexture; m.shader = sh; if (tex != null) m.mainTexture = tex;
                    m.color = new Color(0.78f, 0.86f, 0.7f, 1f);
                }
            }
            leafLogged = true;
        }

        static void Drop(Vector3 p, int value, bool pop)
        {
            if (rupeeMesh == null) MakeRupee();
            var g = new GameObject("gi_rupee"); g.transform.SetParent(holder.transform, false);
            var mf = g.AddComponent<MeshFilter>(); mf.sharedMesh = rupeeMesh;
            var mr = g.AddComponent<MeshRenderer>();
            var m = new Material(rupeeMat);
            var col = value >= 20 ? new Color(0.9f, 0.15f, 0.12f) : value >= 5 ? new Color(0.2f, 0.35f, 1f) : new Color(0.15f, 0.85f, 0.2f);
            m.color = col; if (m.HasProperty("_EmissionColor")) { m.SetColor("_EmissionColor", col * 0.35f); m.EnableKeyword("_EMISSION"); }
            mr.sharedMaterial = m;
            if (rupeeGlow != null)
            {
                var q = GameObject.CreatePrimitive(PrimitiveType.Quad); UnityEngine.Object.Destroy(q.GetComponent<Collider>());
                q.transform.SetParent(g.transform, false); q.transform.localScale = Vector3.one * 0.7f;
                var gm = new Material(rupeeGlow); if (gm.HasProperty("_TintColor")) gm.SetColor("_TintColor", new Color(col.r, col.g, col.b, 0.45f));
                q.GetComponent<MeshRenderer>().sharedMaterial = gm;
                q.AddComponent<OotFaceCam>();
            }
            rupees.Add(new Rupee { G = g, Value = value, T0 = pop ? Time.time : Time.time - 1f, P = p });
        }

        static void MakeRupee()
        {
            // OoT-Rubin: laenglicher Sechskant-Kristall
            var vs = new List<Vector3>(); var tr = new List<int>();
            float r = 0.075f, h = 0.11f, tip = 0.2f;
            for (int i = 0; i < 6; i++) { float a = i * Mathf.PI / 3f; vs.Add(new Vector3(Mathf.Cos(a) * r, h, Mathf.Sin(a) * r * 0.45f)); }
            for (int i = 0; i < 6; i++) { float a = i * Mathf.PI / 3f; vs.Add(new Vector3(Mathf.Cos(a) * r, -h, Mathf.Sin(a) * r * 0.45f)); }
            vs.Add(new Vector3(0f, tip, 0f)); vs.Add(new Vector3(0f, -tip, 0f));
            for (int i = 0; i < 6; i++)
            {
                int j = (i + 1) % 6;
                tr.AddRange(new[] { 12, j, i }); tr.AddRange(new[] { 13, 6 + i, 6 + j });
                tr.AddRange(new[] { i, j, 6 + j }); tr.AddRange(new[] { i, 6 + j, 6 + i });
            }
            // flache Facetten: Ecken je Dreieck einzeln
            var v2 = new List<Vector3>(); var t2 = new List<int>();
            for (int i = 0; i < tr.Count; i++) { v2.Add(vs[tr[i]]); t2.Add(i); }
            rupeeMesh = new Mesh { name = "gi_rupee" }; rupeeMesh.SetVertices(v2); rupeeMesh.SetTriangles(t2, 0); rupeeMesh.RecalculateNormals(); rupeeMesh.RecalculateBounds();
            var sh = Glb.FindPrefabShader("Custom/Piece");
            rupeeMat = new Material(sh != null ? sh : Shader.Find("Standard")) { name = "gi_rupee" };
            if (rupeeMat.HasProperty("_Glossiness")) rupeeMat.SetFloat("_Glossiness", 0.8f);
            var tex = new Texture2D(32, 32, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[32 * 32];
            for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++) { float dx = x / 15.5f - 1f, dy = y / 15.5f - 1f; byte v = (byte)(255 * Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy))); px[y * 32 + x] = new Color32(v, v, v, v); }
            tex.SetPixels32(px); tex.Apply();
            rupeeGlow = FireFx.Make(tex);
        }

        // ---------- Truhe ----------
        static void MakeChest(Pack pk, object c, object model, string key, string flag)
        {
            var pl = Json.L(c, "pos"); if (pl == null || pl.Count < 3) return;
            var p = new Vector3(Convert.ToSingle(pl[0]), Convert.ToSingle(pl[1]), Convert.ToSingle(pl[2]));
            var rot = Quaternion.Euler(0f, 180f - Json.F(c, "yaw", 0f), 0f);                 // wie die festen Modelle (Q180) + Drehung des Akteurs
            // Pack ab 09.11.: Kasten mit Boden im Ursprung, Deckel am Scharnier (hinten oben), Schloss = Modell -z ("front": "-z")
            bool posed = Json.S(model, "front", "") == "-z";
            var ch = new ChestE { Pos = p, Front = rot * (posed ? Vector3.back : Vector3.forward), Key = key, Flag = flag };
            if (Json.Has(model, "lid_open")) lidOpen = Json.F(model, "lid_open", lidOpen);
            // Moritz 09.11.: Valheim-Truhe statt OoT-Modell ("valheim": Prefab); OoT-Modell nur, wenn das Prefab fehlt
            var vh = Json.S(c, "valheim", Json.S(model, "valheim", ""));
            if (!string.IsNullOrEmpty(vh) && MakeValheimChest(ch, vh, p, rot, model))
            {
                if (Opened(ch)) ShowOpen(ch, true);
                var tx0 = Json.L(c, "text"); if (tx0 != null) { ch.Text = new string[tx0.Count]; for (int i = 0; i < tx0.Count; i++) ch.Text[i] = (string)tx0[i]; }
                chests.Add(ch);
                return;
            }
            ch.Base = Part(pk, Json.S(model, "base"), p + rot * V3(Json.L(model, "base_off")), rot);
            ch.Lid = Part(pk, Json.S(model, "lid"), p + rot * V3(Json.L(model, "lid_off")), rot);
            if (ch.Base != null)
            {
                var bc = ch.Base.AddComponent<BoxCollider>();
                if (posed) { bc.center = new Vector3(0f, 0.74f, 0f); bc.size = new Vector3(1.66f, 1.48f, 1.29f); }
                else { bc.center = new Vector3(0f, -0.45f, 0f); bc.size = new Vector3(1.6f, 0.9f, 1.1f); }
            }
            if (ch.Lid != null) ch.LidClosed = ch.Lid.transform.localRotation;
            if (Opened(ch) && ch.Lid != null) ch.Lid.transform.localRotation = ch.LidClosed * Quaternion.Euler(-lidOpen, 0f, 0f);
            var tx = Json.L(c, "text"); if (tx != null) { ch.Text = new string[tx.Count]; for (int i = 0; i < tx.Count; i++) ch.Text[i] = (string)tx[i]; }
            chests.Add(ch);
        }
        // Valheim-Truhe als Bild + Kollision (ohne Netzwerk/Inventar): Container.m_open / m_closed = offenes / geschlossenes Modell
        static bool MakeValheimChest(ChestE ch, string name, Vector3 p, Quaternion rot, object model)
        {
            var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (pf == null) { Plugin.Warn("OoT-Truhe: Prefab fehlt " + name); return false; }
            var hold = new GameObject("gi_chest_proto"); hold.SetActive(false); hold.transform.SetParent(holder.transform, false);
            var go = UnityEngine.Object.Instantiate(pf, hold.transform);             // inaktiv: kein ZNetView.Awake
            var con = go.GetComponentInChildren<Container>(true);
            if (con != null)
            {
                var fo = HarmonyLib.AccessTools.Field(typeof(Container), "m_open"); var fc = HarmonyLib.AccessTools.Field(typeof(Container), "m_closed");
                if (fo != null) ch.OpenG = fo.GetValue(con) as GameObject;
                if (fc != null) ch.ClosedG = fc.GetValue(con) as GameObject;
            }
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
            go.name = "gi_chest_" + name;
            go.transform.SetParent(holder.transform, false);
            UnityEngine.Object.Destroy(hold);
            go.transform.position = p; go.transform.rotation = Quaternion.identity;      // erst ungedreht messen
            ch.Base = go;
            ShowOpen(ch, false);
            // Groesse: Breite wie die Original-Truhe (Standard 1,6 m)
            var b = new Bounds(p, Vector3.zero); bool any = false;
            foreach (var r in go.GetComponentsInChildren<Renderer>()) { if (r is ParticleSystemRenderer) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            float w = Mathf.Max(b.size.x, b.size.z), want = Json.F(model, "valheim_width", 1.6f);
            float sc = any && w > 0.1f ? Mathf.Clamp(want / w, 0.5f, 3f) : 1f;
            go.transform.localScale = Vector3.one * sc;
            go.transform.rotation = rot * Quaternion.Euler(0f, Json.F(model, "valheim_yaw", 0f), 0f);
            Plugin.Log("OoT-Truhe: " + name + " Breite " + w.ToString("0.00") + " m -> x" + sc.ToString("0.00") + ", offen/zu " + (ch.OpenG != null) + "/" + (ch.ClosedG != null));
            return true;
        }
        static void ShowOpen(ChestE ch, bool open)
        {
            if (ch.OpenG != null && ch.OpenG.activeSelf != open) ch.OpenG.SetActive(open);
            if (ch.ClosedG != null && ch.ClosedG.activeSelf == open) ch.ClosedG.SetActive(!open);
        }
        static bool Opened(ChestE ch) { return !string.IsNullOrEmpty(ch.Flag) && Story.Flags.Contains(ch.Flag); }
        public static bool NearChest(Player p)
        {
            var me = p.transform.position;
            foreach (var ch in chests)
            {
                if (ch.Base == null || ch.LidT >= 0f || Opened(ch)) continue;
                var d = me - (ch.Pos + ch.Front * 1.15f); d.y = 0f;
                if (d.magnitude < 1.1f && Mathf.Abs(me.y - ch.Pos.y) < 1.5f) { cur = ch; return true; }
            }
            return false;
        }
        public static float ChestFacingYaw() { if (cur == null) return 0f; var f = -cur.Front; return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }

        // Gegenstand hochhalten: GI-Modell schwebt ueber Links Kopf und dreht sich (wie Player_DrawGetItem)
        static GameObject gi; static float giH = 1.66f;
        static readonly Dictionary<string, GameObject> gis = new Dictionary<string, GameObject>();
        static GameObject GiModel(string key, Player p)
        {
            GameObject g;
            if (gis.TryGetValue(key, out g)) return g;
            gis[key] = null;
            try
            {
                var c = Json.V(cfg, "chest");
                string rel = key == "sword" ? (c != null ? Json.S(c, "gi") : null) : Json.S(Json.V(cfg, "gi_models"), key);
                if (c != null) giH = Json.F(c, "gi_h", 1.66f);
                if (string.IsNullOrEmpty(rel) || holder == null) return null;
                g = Part(Pack.Current, rel, p.transform.position, Quaternion.identity);
                if (g == null) return null;
                foreach (var col in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(col);
                if (key == "sword")
                    foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                        foreach (var m in r.materials)
                        {
                            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.85f);
                            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0.6f);
                            if (m.HasProperty("_EmissionColor")) { m.SetColor("_EmissionColor", new Color(0.32f, 0.34f, 0.38f)); m.EnableKeyword("_EMISSION"); }
                        }
                g.SetActive(false);
                gis[key] = g;
            }
            catch (Exception e) { Plugin.Warn("OoT-GI: " + e); }
            return g;
        }
        public static void GetItemTick(Player p, bool on, float t)
        {
            if (!on) { foreach (var kv in gis) if (kv.Value != null && kv.Value.activeSelf) kv.Value.SetActive(false); return; }
            if (cfg == null) return;
            var want = GiModel(GiKey ?? "sword", p);
            if (gi != want && gi != null) gi.SetActive(false);
            gi = want;
            if (gi == null) return;
            if (!gi.activeSelf) gi.SetActive(true);
            float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / 0.25f));
            gi.transform.position = p.transform.position + Vector3.up * (giH - 0.25f * (1f - k));
            gi.transform.rotation = Quaternion.Euler(0f, Time.time * 110f, 0f);
            gi.transform.localScale = Vector3.one * Mathf.Max(0.01f, k);
        }
        public static Vector3 ChestStand() { return cur != null ? cur.Pos + cur.Front * 1.15f : Vector3.zero; }
        public static void OpenLid() { if (cur != null && cur.LidT < 0f) cur.LidT = 0f; }

        /// <summary>Alles neu aufbauen (Truhen, Buesche, Aufsammel-Dinge) nach geaenderten Merkern (Dev-Pult: Neustart).</summary>
        public static void Rebuild() { forPack = null; }

        public static void ResetAll()
        {
            Story.Flags.Remove(SwordFlag); Story.Save();
            foreach (var ch in chests)
            {
                if (!string.IsNullOrEmpty(ch.Flag)) Story.Flags.Remove(ch.Flag);
                if (ch.Lid != null) ch.Lid.transform.localRotation = ch.LidClosed;
                ShowOpen(ch, false);
                ch.LidT = -1f;
            }
            Story.Save();
            if (chests.Count > 0) cur = chests[0];
            foreach (var b in bushes) if (b.G != null) b.G.SetActive(true);
            OotHud.Rupees = 0;
        }

        // Rubine, Staebe, Nuesse, Samen bleiben im Spielstand (wie in OoT): als Merker "oot_n_<name>=<zahl>"
        static Player countsFor; static float countsT; static string countsLast;
        static readonly string[] countNames = { "rupees", "sticks", "nuts", "seeds" };
        static int GetCount(int i) { return i == 0 ? OotHud.Rupees : i == 1 ? Sticks : i == 2 ? Nuts : Seeds; }
        static void SetCount(int i, int v) { if (i == 0) OotHud.Rupees = v; else if (i == 1) Sticks = v; else if (i == 2) Nuts = v; else Seeds = v; }
        static void CountsTick(Player p)
        {
            if (p != countsFor)
            {
                countsFor = p;
                for (int i = 0; i < countNames.Length; i++)
                {
                    var pre = "oot_n_" + countNames[i] + "=";
                    foreach (var f in Story.Flags) { int v; if (f.StartsWith(pre) && int.TryParse(f.Substring(pre.Length), out v)) { SetCount(i, v); break; } }
                }
                countsLast = null; countsT = Time.time + 2f; return;
            }
            if (Time.time < countsT) return;
            countsT = Time.time + 2f;
            var sig = OotHud.Rupees + "/" + Sticks + "/" + Nuts + "/" + Seeds;
            if (sig == countsLast) return;
            if (countsLast != null)
            {
                for (int i = 0; i < countNames.Length; i++)
                {
                    var pre = "oot_n_" + countNames[i] + "=";
                    Story.Flags.RemoveWhere(f => f.StartsWith(pre));
                    Story.Flags.Add(pre + GetCount(i));
                }
                Story.Save();
            }
            countsLast = sig;
        }

        // Test/Aufnahme: eine Reihe Buesche an einer Stelle (oot items bushes x z [n] [richtung])
        public static int SpawnRow(Vector3 start, Vector3 dir, int n, float step)
        {
            if (holder == null || bushProto == null) return 0;
            for (int i = 0; i < n; i++)
            {
                var v = start + dir.normalized * (i * step) + Vector3.Cross(Vector3.up, dir.normalized) * ((i % 2 == 0) ? 0.55f : -0.55f);
                try { v.y = MapImport.Height(v.x, v.z); } catch { }
                var b = new Bush { P = v }; SpawnBush(b); bushes.Add(b);
            }
            return n;
        }

        public static void RespawnTick()
        {
            foreach (var b in bushes) if (b.G != null && !b.G.activeSelf && Time.time > b.Respawn) b.G.SetActive(true);
        }
    }

    // kleine Flaeche, die immer zur Kamera schaut (Rubin-Glanz)
    public class OotFaceCam : MonoBehaviour
    {
        void LateUpdate()
        {
            var c = Camera.main; if (c == null) return;
            transform.rotation = Quaternion.LookRotation(transform.position - c.transform.position, c.transform.up);
        }
    }
}
