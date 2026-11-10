using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameImporter
{
    // Vegetation aus dem Originalspiel (tools/witcher3/w3foliage.py -> foliage.bin): jeder Baum/Busch/Fels an seiner Originalstelle,
    // dargestellt mit Valheim-Prefabs (FirTree, Pinetree_01, Beech1 ...). Valheims eigene Zufallsvegetation und Orte (Starttempel usw.)
    // sind in solchen Welten aus (gilt fuer neu erzeugte Gegenden).
    //  - nah: echte Valheim-Baeume als lokale Objekte (ohne Netzwerk, mit Kollision) in allen 128-m-Feldern, die naeher als NearR sind
    //  - fern: Abbilder ("Billboards"): beim Laden wird jede Baumart von vorne, von der Seite und von oben in eine gemeinsame Textur
    //    gezeichnet; jeder ferne Baum ist dann ein Kreuz aus zwei senkrechten und einer waagrechten Flaeche mit diesem Bild.
    //    Feine Felder (128 m) bis CoarseD, dahinter grobe Bloecke (512 m) -> wenige Draw-Calls bis zum Horizont.
    //    Feld fuer Feld ist entweder echt oder Abbild -> keine Luecke und keine doppelten Baeume am Uebergang.
    public static class Foliage
    {
        public static float NearR = 200f, FarR = 3200f, CoarseD = 900f;
        const float Cell = 128f, Block = 512f;

        static Pack forPack;
        static string[] kinds = new string[0];
        static ushort[] kind; static Vector3[] pos; static float[] scl, yaw;
        static readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>(), blocks = new Dictionary<long, List<int>>();
        static GameObject holder;
        static readonly Dictionary<string, GameObject> proto = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, List<KeyValuePair<Mesh, Material[]>>> rockParts = new Dictionary<string, List<KeyValuePair<Mesh, Material[]>>>();
        static readonly Dictionary<int, GameObject> live = new Dictionary<int, GameObject>();
        static readonly Dictionary<Material, Material> noSnow = new Dictionary<Material, Material>();
        static readonly Dictionary<string, Stack<GameObject>> pool = new Dictionary<string, Stack<GameObject>>();
        static readonly Dictionary<long, GameObject> fine = new Dictionary<long, GameObject>(), coarse = new Dictionary<long, GameObject>();
        static readonly HashSet<long> nearCells = new HashSet<long>();
        static readonly List<int> todo = new List<int>(); static int todoI;
        static readonly List<KeyValuePair<long, bool>> buildTodo = new List<KeyValuePair<long, bool>>();
        static float tickT;
        public static bool Active { get { return kinds.Length > 0 && pos != null && pos.Length > 0; } }

        // Freiraeume (Einstellungen): keine nahen Baeume in diesen Schlaeuchen (a, b, Radius in x) -> Kamera steckt nicht im Baum
        static readonly List<Vector3[]> clearSegs = new List<Vector3[]>();
        public static void SetClear(List<Vector3[]> segs) { clearSegs.Clear(); if (segs != null) clearSegs.AddRange(segs); tickT = 0f; }
        static bool Cleared(Vector3 p)
        {
            foreach (var sg in clearSegs)
            {
                float ax = sg[0].x, az = sg[0].z, bx = sg[1].x - ax, bz = sg[1].z - az, px = p.x - ax, pz = p.z - az;
                float l2 = bx * bx + bz * bz, u = l2 > 1e-4f ? Mathf.Clamp01((px * bx + pz * bz) / l2) : 0f;
                float dx = px - bx * u, dz = pz - bz * u;
                if (dx * dx + dz * dz < sg[2].x * sg[2].x) return true;
            }
            return false;
        }

        static long Key(int x, int z) { return ((long)x << 32) ^ (uint)z; }
        static int KX(long k) { return (int)(k >> 32); }
        static int KZ(long k) { return (int)(uint)(k & 0xffffffff); }

        public static void Patch(Harmony h)
        {
            var st = AccessTools.Method(typeof(ZoneSystem), "Start");
            if (st != null) h.Patch(st, prefix: new HarmonyMethod(typeof(Foliage), nameof(ZoneStartPre)), postfix: new HarmonyMethod(typeof(Foliage), nameof(ZoneStartPre)));
            foreach (var n in new[] { "GenerateLocationsIfNeeded", "GenerateLocations" })
            {
                var m = AccessTools.Method(typeof(ZoneSystem), n, new Type[0]);
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(Foliage), nameof(ZoneStartPre)));
            }
        }

        // Welt eines Spielpakets mit eigener Vegetation: Valheims Zufallsbaeume, -felsen und -orte aus
        static void ZoneStartPre(ZoneSystem __instance)
        {
            try
            {
                var pk = Pack.Current;
                // eigene Vegetation (foliage.bin) oder pack.json "vegetation": false (z. B. OoT: Welt besteht nur aus Original-Modellen)
                bool off = pk != null && pk.Meta != null && Json.Has(pk.Meta, "vegetation") && !Json.B(pk.Meta, "vegetation");
                if (pk == null || (!off && !File.Exists(Path.Combine(pk.Dir, "foliage.bin")))) return;
                int v = 0, l = 0;
                foreach (var z in __instance.m_vegetation) if (z.m_enable) { z.m_enable = false; v++; }
                foreach (var z in __instance.m_locations) if (z.m_enable) { z.m_enable = false; l++; }
                if (v + l > 0) Plugin.Log("Vegetation: Valheims Zufallsvegetation (" + v + ") und Orte (" + l + ") in dieser Welt aus");
            }
            catch (Exception e) { Plugin.Warn("Vegetation aus: " + e.Message); }
        }

        static void Clear()
        {
            foreach (var g in live.Values) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in fine.Values) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in coarse.Values) if (g != null) UnityEngine.Object.Destroy(g);
            live.Clear(); fine.Clear(); coarse.Clear(); pool.Clear(); proto.Clear(); rockParts.Clear(); cells.Clear(); blocks.Clear(); nearCells.Clear();
            todo.Clear(); todoI = 0; buildTodo.Clear(); bb.Clear(); atlasReady = false;
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; kinds = new string[0]; pos = null;
        }

        static void Load(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            var f = Path.Combine(pk.Dir, "foliage.bin");
            if (!File.Exists(f)) return;
            try
            {
                using (var br = new BinaryReader(File.OpenRead(f)))
                {
                    if (new string(br.ReadChars(4)) != "GIF1") return;
                    int nk = br.ReadInt32(); kinds = new string[nk];
                    for (int i = 0; i < nk; i++) { int ln = br.ReadByte(); kinds[i] = new string(br.ReadChars(ln)); }
                    int n = br.ReadInt32();
                    kind = new ushort[n]; pos = new Vector3[n]; scl = new float[n]; yaw = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        kind[i] = br.ReadUInt16(); pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        scl[i] = br.ReadByte() / 40f; yaw[i] = br.ReadByte() * (360f / 256f);
                        List<int> l;
                        long k = Key(Mathf.FloorToInt(pos[i].x / Cell), Mathf.FloorToInt(pos[i].z / Cell));
                        if (!cells.TryGetValue(k, out l)) cells[k] = l = new List<int>(); l.Add(i);
                        k = Key(Mathf.FloorToInt(pos[i].x / Block), Mathf.FloorToInt(pos[i].z / Block));
                        if (!blocks.TryGetValue(k, out l)) blocks[k] = l = new List<int>(); l.Add(i);
                    }
                }
                holder = new GameObject("gi_foliage"); holder.SetActive(true);
                Plugin.Log("Vegetation: " + pos.Length + " Pflanzen, " + kinds.Length + " Arten (" + string.Join(", ", kinds) + ")");
            }
            catch (Exception e) { Plugin.Warn("Vegetation: " + e.Message); Clear(); }
        }

        // Valheim-Prefab nur als Bild (+Kollision): ohne Netzwerk, ohne Faellen/Schaden
        static GameObject Proto(string name)
        {
            GameObject p;
            if (proto.TryGetValue(name, out p)) return p;
            proto[name] = null;
            // "Prefab#nc": nur Deko, ohne Kollision (z. B. Wurzeln an Waenden im Deku-Baum, die sonst Wege versperren)
            // "Prefab#nl": ohne eigene Lichter (Mistlands-Pilze haben ein Licht je Pilz - bei vielen zu teuer)
            string pfName = name; bool noCol = false, noLight = false;
            {
                int hi = name.IndexOf('#');
                if (hi > 0) { var fl = name.Substring(hi); noCol = fl.Contains("nc"); noLight = fl.Contains("nl"); pfName = name.Substring(0, hi); }
            }
            var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(pfName) : null;
            if (pf == null) { Plugin.Warn("Vegetation: Prefab fehlt " + pfName); return null; }
            var hold = new GameObject("gi_foliage_proto_" + name); hold.SetActive(false); hold.transform.SetParent(holder.transform, false);
            var go = UnityEngine.Object.Instantiate(pf, hold.transform);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
            if (noCol) foreach (var cc in go.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(cc);
            if (noLight) foreach (var li in go.GetComponentsInChildren<Light>(true)) UnityEngine.Object.DestroyImmediate(li);
            go.name = name; go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var ms = r.sharedMaterials; bool ch = false;
                for (int i = 0; i < ms.Length; i++)
                {
                    if (ms[i] == null || !ms[i].HasProperty("_AddSnow") || (Pack.Current != null && Pack.Current.Snow)) continue;
                    Material c; if (!noSnow.TryGetValue(ms[i], out c)) { c = new Material(ms[i]) { name = ms[i].name + "_gi" }; Glb.NoSnow(c); noSnow[ms[i]] = c; }
                    ms[i] = c; ch = true;
                }
                if (ch) r.sharedMaterials = ms;
            }
            proto[name] = go;
            if (IsRock(name))
            {
                // Felsen: niedrigste Detailstufe als echtes Netz (wenige Ecken, sieht von weitem richtig aus)
                var parts = new List<KeyValuePair<Mesh, Material[]>>();
                var lg = go.GetComponentInChildren<LODGroup>(true);
                IEnumerable<Renderer> rs = go.GetComponentsInChildren<MeshRenderer>(true);
                if (lg != null) { var lods = lg.GetLODs(); for (int i = lods.Length - 1; i >= 0; i--) if (lods[i].renderers != null && lods[i].renderers.Length > 0) { rs = lods[i].renderers; break; } }
                foreach (var r in rs)
                {
                    var mf = r != null ? r.GetComponent<MeshFilter>() : null;
                    if (mf == null || mf.sharedMesh == null || !(r is MeshRenderer) || !mf.sharedMesh.isReadable) continue;
                    parts.Add(new KeyValuePair<Mesh, Material[]>(mf.sharedMesh, r.sharedMaterials));
                }
                rockParts[name] = parts;
            }
            return go;
        }

        static bool IsRock(string n) { return n.StartsWith("rock"); }

        static string Variant(string k, Vector3 p)
        {
            // Schnee in den Bergen: Valheims Schnee-Baeume, damit es zum verschneiten Boden passt
            try
            {
                if (WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(p) == Heightmap.Biome.Mountain)
                {
                    if (k == "FirTree" || k == "FirTree_big") return "SnowFirTree";
                    if (k == "Pinetree_01") return "Pinetree_Snow";
                }
            }
            catch { }
            return k;
        }

        static string KindAt(int i) { return Variant(kinds[kind[i]], pos[i]); }

        static Vector3 Ground(int i)
        {
            var p = pos[i];
            try { float h = MapImport.Height(p.x, p.z); if (Mathf.Abs(h - p.y) < 2.5f) p.y = h; } catch { }
            return p;
        }

        // naechster Punkt eines Quadrats (Feld/Block) zur Kamera, waagrecht
        static float SqDist(int x, int z, float size, Vector3 cam)
        {
            float x0 = x * size, z0 = z * size;
            float dx = cam.x < x0 ? x0 - cam.x : (cam.x > x0 + size ? cam.x - x0 - size : 0f);
            float dz = cam.z < z0 ? z0 - cam.z : (cam.z > z0 + size ? cam.z - z0 - size : 0f);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) Load(pk);
            if (!Active || ZNetScene.instance == null) return;
            if (!atlasReady) { try { BuildAtlas(); } catch (Exception e) { Plugin.Warn("Vegetation Abbilder: " + e); } atlasReady = true; }
            var cam = Camera.main != null ? Camera.main.transform.position : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
            if (MapImport.LookOn) cam = MapImport.LookPos;
            if (Time.time >= tickT)
            {
                tickT = Time.time + 0.5f;
                // nahe Felder: echte Baeume
                nearCells.Clear();
                int r = Mathf.CeilToInt(NearR / Cell) + 1, cx = Mathf.FloorToInt(cam.x / Cell), cz = Mathf.FloorToInt(cam.z / Cell);
                todo.Clear(); todoI = 0;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (SqDist(cx + dx, cz + dz, Cell, cam) > NearR) continue;
                        long k = Key(cx + dx, cz + dz);
                        List<int> l; if (!cells.TryGetValue(k, out l)) continue;
                        nearCells.Add(k);
                        foreach (var i in l) if (!live.ContainsKey(i) && (clearSegs.Count == 0 || !Cleared(pos[i]))) todo.Add(i);
                    }
                todo.Sort((a, b) => (pos[a] - cam).sqrMagnitude.CompareTo((pos[b] - cam).sqrMagnitude));
                var gone = new List<int>();
                foreach (var kv in live)
                    if (!nearCells.Contains(Key(Mathf.FloorToInt(pos[kv.Key].x / Cell), Mathf.FloorToInt(pos[kv.Key].z / Cell))) || (clearSegs.Count > 0 && Cleared(pos[kv.Key]))) gone.Add(kv.Key);
                foreach (var i in gone) { var g = live[i]; live.Remove(i); if (g != null) { g.SetActive(false); Pool(g.name).Push(g); } }
                // Abbilder: je Block grob (weit weg) oder fein (Feld fuer Feld, ausser den nahen Feldern)
                buildTodo.Clear();
                int per = Mathf.RoundToInt(Block / Cell);
                foreach (var kv in blocks)
                {
                    int bx = KX(kv.Key), bz = KZ(kv.Key);
                    float d = SqDist(bx, bz, Block, cam);
                    bool useCoarse = d > CoarseD, inRange = d < FarR;
                    GameObject cg; coarse.TryGetValue(kv.Key, out cg);
                    bool fineReady = true;
                    for (int j = 0; j < per; j++)
                        for (int i = 0; i < per; i++)
                        {
                            long fk = Key(bx * per + i, bz * per + j);
                            if (!cells.ContainsKey(fk)) continue;
                            GameObject fg; fine.TryGetValue(fk, out fg);
                            bool wantFine = inRange && !useCoarse && !nearCells.Contains(fk);
                            if (wantFine && !fine.ContainsKey(fk)) { buildTodo.Add(new KeyValuePair<long, bool>(fk, false)); fineReady = false; }
                            if (fg != null && fg.activeSelf != wantFine) fg.SetActive(wantFine);
                        }
                    // grober Block: weit weg; solange die feinen Felder noch fehlen, ersatzweise auch etwas naeher
                    bool showCoarse = inRange && (useCoarse || (!fineReady && d > NearR));
                    if (showCoarse && !coarse.ContainsKey(kv.Key)) buildTodo.Add(new KeyValuePair<long, bool>(kv.Key, true));
                    if (cg != null && cg.activeSelf != showCoarse) cg.SetActive(showCoarse);
                }
                buildTodo.Sort((a, b) => BDist(a, cam).CompareTo(BDist(b, cam)));
            }
            var sw = Stopwatch.StartNew();
            while (todoI < todo.Count && sw.ElapsedMilliseconds < 3)
            {
                int i = todo[todoI++];
                if (live.ContainsKey(i)) continue;
                var name = KindAt(i);
                var pr = Proto(name); if (pr == null) continue;
                var st = Pool(name);
                var g = st.Count > 0 ? st.Pop() : UnityEngine.Object.Instantiate(pr, holder.transform);
                g.name = name;
                g.transform.position = Ground(i); g.transform.rotation = Quaternion.Euler(0f, yaw[i], 0f); g.transform.localScale = Vector3.one * scl[i];
                g.SetActive(true);
                live[i] = g;
            }
            while (buildTodo.Count > 0 && sw.ElapsedMilliseconds < 6)
            {
                var t = buildTodo[0]; buildTodo.RemoveAt(0);
                var dict = t.Value ? coarse : fine;
                if (dict.ContainsKey(t.Key)) continue;
                List<int> l; if (!(t.Value ? blocks : cells).TryGetValue(t.Key, out l)) continue;
                try { var g = BuildCell(l, t.Value); dict[t.Key] = g; if (g != null) g.SetActive(false); }
                catch (Exception e) { dict[t.Key] = null; Plugin.Warn("Vegetation fern: " + e.Message); }
                tickT = Mathf.Min(tickT, Time.time + 0.1f);     // bald einblenden
            }
        }

        static float BDist(KeyValuePair<long, bool> t, Vector3 cam) { return SqDist(KX(t.Key), KZ(t.Key), t.Value ? Block : Cell, cam) + (t.Value ? 0f : -1f); }

        static Stack<GameObject> Pool(string name) { Stack<GameObject> s; if (!pool.TryGetValue(name, out s)) pool[name] = s = new Stack<GameObject>(); return s; }

        // ---------------- Abbilder ----------------
        const int TW = 128, TH = 256, AW = 2048, AH = 2048, SlotW = 384, SlotH = 256;
        class BB { public Rect Front, Side, Top; public float Hw, Cy, Hh, TopHw, TopY; }
        static readonly Dictionary<string, BB> bb = new Dictionary<string, BB>();
        static Texture2D atlas; static Material bbMat; static bool atlasReady, dbl = true;
        public static float BbTint = 1f;

        // Abbilder aller Arten (samt Schnee-Varianten) in eine Textur zeichnen, dann Mipmaps mit erhaltener Deckung
        static void BuildAtlas()
        {
            var names = new List<string>();
            foreach (var k in kinds)
            {
                if (IsRock(k) || k == "stubbe") continue;
                if (!names.Contains(k)) names.Add(k);
                var v = k == "FirTree" || k == "FirTree_big" ? "SnowFirTree" : (k == "Pinetree_01" ? "Pinetree_Snow" : null);
                if (v != null && !names.Contains(v)) names.Add(v);
            }
            var sw = Stopwatch.StartNew();
            if (atlas == null)
            {
                atlas = new Texture2D(AW, AH, TextureFormat.RGBA32, true, false) { name = "gi_foliage_atlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            }
            var clear = new Color32[AW * AH]; atlas.SetPixels32(clear); atlas.Apply(false, false);
            // Licht fuers Abbild: gleichmaessig hell (die Lichtrichtung kommt spaeter vom Spiel)
            var fog = RenderSettings.fog; var amode = RenderSettings.ambientMode; var acol = RenderSettings.ambientLight; var aint = RenderSettings.ambientIntensity; var probe = RenderSettings.ambientProbe;
            var lights = new List<Light>();
            foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>()) if (l.enabled) { lights.Add(l); l.enabled = false; }
            var lgo = new GameObject("gi_bb_light"); var L = lgo.AddComponent<Light>(); L.type = LightType.Directional; L.color = Color.white; L.intensity = 0.75f; L.shadows = LightShadows.None;
            var cgo = new GameObject("gi_bb_cam"); var cam = cgo.AddComponent<Camera>(); cam.enabled = false;
            cam.orthographic = true; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.allowHDR = false; cam.allowMSAA = false; cam.useOcclusionCulling = false; cam.cullingMask = ~0; cam.renderingPath = RenderingPath.Forward;
            var root = new GameObject("gi_bb_root"); root.transform.position = new Vector3(0f, 9000f, 0f);
            int slot = 0;
            try
            {
                RenderSettings.fog = false; RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.62f); RenderSettings.ambientIntensity = 1f;
                var sh = new SphericalHarmonicsL2(); sh.AddAmbientLight(new Color(0.62f, 0.62f, 0.62f)); RenderSettings.ambientProbe = sh;
                foreach (var n in names)
                {
                    if (slot >= (AW / SlotW) * (AH / SlotH)) break;
                    var pr = Proto(n); if (pr == null) continue;
                    var b = Capture(n, pr, root, cam, L, slot);
                    if (b != null) { bb[n] = b; slot++; }
                }
            }
            finally
            {
                RenderSettings.fog = fog; RenderSettings.ambientMode = amode; RenderSettings.ambientLight = acol; RenderSettings.ambientIntensity = aint; RenderSettings.ambientProbe = probe;
                foreach (var l in lights) if (l != null) l.enabled = true;
                UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cgo); UnityEngine.Object.DestroyImmediate(lgo);
            }
            MakeMips();
            BbMat();
            Plugin.Log("Vegetation: " + bb.Count + " Abbilder (" + string.Join(", ", new List<string>(bb.Keys).ToArray()) + ") in " + sw.ElapsedMilliseconds + " ms, Material " + (bbMat != null ? bbMat.shader.name : "-"));
        }

        static BB Capture(string name, GameObject pr, GameObject root, Camera cam, Light L, int slot)
        {
            var go = UnityEngine.Object.Instantiate(pr, root.transform);
            go.SetActive(true);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one;
            try
            {
                foreach (var lg in go.GetComponentsInChildren<LODGroup>(true)) lg.ForceLOD(0);
                foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true)) ps.gameObject.SetActive(false);
                var rs = new List<Renderer>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) if (r.enabled && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer)) rs.Add(r);
                // nur LOD0 zaehlt fuer den Umriss
                var lg0 = go.GetComponentInChildren<LODGroup>(true);
                if (lg0 != null) { var lods = lg0.GetLODs(); if (lods.Length > 0 && lods[0].renderers != null) { var only = new HashSet<Renderer>(lods[0].renderers); var keep = new List<Renderer>(); foreach (var r in rs) if (only.Contains(r)) keep.Add(r); if (keep.Count > 0) rs = keep; } }
                if (rs.Count == 0) return null;
                var o = root.transform.position;
                float top = 0f, bot = 0f, wmax = 0.3f;
                foreach (var r in rs)
                {
                    var bnd = r.bounds;
                    top = Mathf.Max(top, bnd.max.y - o.y); bot = Mathf.Min(bot, bnd.min.y - o.y);
                    wmax = Mathf.Max(wmax, Mathf.Abs(bnd.min.x - o.x), Mathf.Abs(bnd.max.x - o.x), Mathf.Abs(bnd.min.z - o.z), Mathf.Abs(bnd.max.z - o.z));
                }
                if (top < 0.5f) return null;
                var b = new BB();
                float a = (float)TW / TH;
                b.Cy = (top + bot) * 0.5f; b.Hh = (top - bot) * 0.5f * 1.04f; b.Hw = b.Hh * a;
                if (wmax * 1.04f > b.Hw) { b.Hw = wmax * 1.04f; b.Hh = b.Hw / a; }
                b.TopHw = wmax * 1.04f;
                bool conifer = name.Contains("Fir") || name.Contains("Pine");
                b.TopY = top * (conifer ? 0.3f : 0.55f);
                int ox = (slot % (AW / SlotW)) * SlotW, oy = (slot / (AW / SlotW)) * SlotH;
                var rtBig = RenderTexture.GetTemporary(TW * 2, TH * 2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var rt = RenderTexture.GetTemporary(TW, TH, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var rtBigT = RenderTexture.GetTemporary(TW * 2, TW * 2, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                var rtT = RenderTexture.GetTemporary(TW, TW, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                rtBig.filterMode = FilterMode.Bilinear; rtBigT.filterMode = FilterMode.Bilinear;
                try
                {
                    float D = 200f;
                    cam.nearClipPlane = 1f; cam.farClipPlane = D * 2f;
                    // vorne (Blick +z) und Seite (Blick +x): Licht von vorne oben, etwas seitlich
                    for (int v = 0; v < 2; v++)
                    {
                        var fwd = v == 0 ? Vector3.forward : Vector3.right;
                        cam.transform.position = o + new Vector3(0f, b.Cy, 0f) - fwd * D;
                        cam.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
                        cam.orthographicSize = b.Hh; cam.aspect = a;
                        L.transform.rotation = Quaternion.LookRotation(Quaternion.AngleAxis(25f, Vector3.up) * (fwd + Vector3.down * 0.7f).normalized);
                        Shoot(cam, rtBig, rt, TW, TH, ox + v * TW, oy, name);
                    }
                    // oben (Blick nach unten, Bild-oben = +z)
                    cam.transform.position = o + new Vector3(0f, top + D * 0.5f, 0f);
                    cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
                    cam.orthographicSize = b.TopHw; cam.aspect = 1f;
                    L.transform.rotation = Quaternion.LookRotation(new Vector3(0.3f, -1f, 0.4f).normalized);
                    Shoot(cam, rtBigT, rtT, TW, TW, ox + 2 * TW, oy, name);
                }
                finally
                {
                    cam.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rtBig); RenderTexture.ReleaseTemporary(rt); RenderTexture.ReleaseTemporary(rtBigT); RenderTexture.ReleaseTemporary(rtT);
                }
                b.Front = new Rect((float)ox / AW, (float)oy / AH, (float)TW / AW, (float)TH / AH);
                b.Side = new Rect((float)(ox + TW) / AW, (float)oy / AH, (float)TW / AW, (float)TH / AH);
                b.Top = new Rect((float)(ox + 2 * TW) / AW, (float)oy / AH, (float)TW / AW, (float)TW / AH);
                return b;
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        // Aufnahme zweimal (schwarzer und weisser Hintergrund): Deckung = 1 - Unterschied. Klappt unabhaengig davon, was der
        // Shader in den Alphakanal schreibt; Farbe = Bild auf Schwarz / Deckung
        static Texture2D tmpB, tmpW;
        static int shotsEmpty;
        static void Shoot(Camera cam, RenderTexture big, RenderTexture small, int w, int h, int dx, int dy, string name)
        {
            if (tmpB == null || tmpB.width != w || tmpB.height != h) { tmpB = new Texture2D(w, h, TextureFormat.RGBA32, false, false); tmpW = new Texture2D(w, h, TextureFormat.RGBA32, false, false); }
            var prev = RenderTexture.active;
            for (int pass = 0; pass < 2; pass++)
            {
                cam.backgroundColor = pass == 0 ? new Color(0f, 0f, 0f, 0f) : new Color(1f, 1f, 1f, 0f);
                cam.targetTexture = big; cam.Render();
                Graphics.Blit(big, small);
                RenderTexture.active = small;
                (pass == 0 ? tmpB : tmpW).ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            }
            RenderTexture.active = prev;
            var B = tmpB.GetPixels32(); var W = tmpW.GetPixels32(); var o = new Color32[w * h];
            int cov = 0;
            for (int i = 0; i < o.Length; i++)
            {
                int d = Math.Max(W[i].r - B[i].r, Math.Max(W[i].g - B[i].g, W[i].b - B[i].b));
                int al = Mathf.Clamp(255 - d, 0, 255);
                if (al < 6) { o[i] = new Color32(0, 0, 0, 0); continue; }
                float k = 255f / al;
                o[i] = new Color32((byte)Mathf.Min(255, B[i].r * k), (byte)Mathf.Min(255, B[i].g * k), (byte)Mathf.Min(255, B[i].b * k), (byte)al);
                if (al > 128) cov++;
            }
            atlas.SetPixels32(dx, dy, w, h, o, 0);
            if (cov == 0) shotsEmpty++;
            Plugin.Log("Vegetation: Abbild " + name + " " + w + "x" + h + " deckend " + cov + " Punkte");
        }

        // Ebene 0 nachbearbeiten (Farbe in die durchsichtigen Ränder, gegen dunkle Säume), dann Mipmaps selbst rechnen:
        // Farbe nach Deckung gewichtet, Deckung je Ebene so verstärkt, dass der Baum von weitem nicht ausdünnt
        static void MakeMips()
        {
            var p0 = atlas.GetPixels32(0);
            int slots = (AW / SlotW) * (AH / SlotH);
            for (int s = 0; s < slots; s++)
            {
                int ox = (s % (AW / SlotW)) * SlotW, oy = (s / (AW / SlotW)) * SlotH;
                long r = 0, g = 0, bl = 0, n = 0;
                for (int y = oy; y < oy + SlotH; y++) for (int x = ox; x < ox + SlotW; x++) { var c = p0[y * AW + x]; if (c.a > 128) { r += c.r; g += c.g; bl += c.b; n++; } }
                if (n == 0) continue;
                var avg = new Color32((byte)(r / n), (byte)(g / n), (byte)(bl / n), 0);
                for (int y = oy; y < oy + SlotH; y++) for (int x = ox; x < ox + SlotW; x++) { int i = y * AW + x; if (p0[i].a < 8) p0[i] = avg; }
            }
            atlas.SetPixels32(p0, 0);
            float cov0 = Coverage(p0, 1f);
            var prev = p0; int w = AW, h = AH;
            for (int m = 1; m < atlas.mipmapCount; m++)
            {
                int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
                var cur = new Color32[nw * nh];
                for (int y = 0; y < nh; y++)
                    for (int x = 0; x < nw; x++)
                    {
                        int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                        var a = prev[y0 * w + x0]; var b = prev[y0 * w + x1]; var c = prev[y1 * w + x0]; var d = prev[y1 * w + x1];
                        int wa = a.a + 1, wb = b.a + 1, wc = c.a + 1, wd = d.a + 1, ws = wa + wb + wc + wd;
                        cur[y * nw + x] = new Color32((byte)((a.r * wa + b.r * wb + c.r * wc + d.r * wd) / ws), (byte)((a.g * wa + b.g * wb + c.g * wc + d.g * wd) / ws),
                                                      (byte)((a.b * wa + b.b * wb + c.b * wc + d.b * wd) / ws), (byte)((a.a + b.a + c.a + d.a) / 4));
                    }
                // Deckung wie in Ebene 0: Faktor fuer Alpha suchen
                float lo = 1f, hi = 8f;
                for (int it = 0; it < 12; it++) { float mid = (lo + hi) * 0.5f; if (Coverage(cur, mid) < cov0) lo = mid; else hi = mid; }
                float k = (lo + hi) * 0.5f;
                for (int i = 0; i < cur.Length; i++) { int av = (int)(cur[i].a * k); cur[i].a = (byte)Math.Min(255, av); }
                atlas.SetPixels32(cur, m);
                prev = cur; w = nw; h = nh;
            }
            atlas.Apply(false, false);
        }

        static float Coverage(Color32[] px, float k)
        {
            int n = 0; float lim = 127.5f / k;
            for (int i = 0; i < px.Length; i++) if (px[i].a >= lim) n++;
            return (float)n / px.Length;
        }

        // Material: Valheims eigenes Blatt-Material (Licht, Nebel, Schnitt an der Kante wie bei echten Baeumen), Textur getauscht
        static Material BbMat()
        {
            if (bbMat != null) { bbMat.mainTexture = atlas; return bbMat; }
            Material src = null;
            foreach (var k in new[] { "FirTree", "Pinetree_01", "Beech1" })
            {
                var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(k) : null;
                if (pf == null) continue;
                foreach (var r in pf.GetComponentsInChildren<MeshRenderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (src == null && m != null && m.HasProperty("_Cutoff") && m.mainTexture != null) src = m;
                if (src != null) break;
            }
            bbMat = src != null ? new Material(src) : new Material(Glb.FindPrefabShader("Custom/Piece") ?? Shader.Find("Standard"));
            bbMat.name = "gi_foliage_bb";
            bbMat.mainTexture = atlas; bbMat.mainTextureScale = Vector2.one; bbMat.mainTextureOffset = Vector2.zero;
            if (bbMat.HasProperty("_Cutoff")) bbMat.SetFloat("_Cutoff", 0.5f);
            Glb.NoSnow(bbMat);
            if (bbMat.HasProperty("_Color")) bbMat.color = new Color(BbTint, BbTint, BbTint, 1f);
            var flat = new Texture2D(4, 4, TextureFormat.RGBA32, false, true); var fc = new Color32[16]; for (int i = 0; i < 16; i++) fc[i] = new Color32(255, 128, 255, 128); flat.SetPixels32(fc); flat.Apply();
            var sh = bbMat.shader; var log = new List<string>();
            for (int i = 0; i < sh.GetPropertyCount(); i++)
            {
                var pn = sh.GetPropertyName(i); var pt = sh.GetPropertyType(i); var ln = pn.ToLowerInvariant();
                if (pt == ShaderPropertyType.Texture && pn != "_MainTex")
                {
                    if (ln.Contains("bump") || ln.Contains("normal")) { bbMat.SetTexture(pn, flat); log.Add(pn + "=flach"); }
                    else if (bbMat.GetTexture(pn) != null) log.Add(pn + "=" + bbMat.GetTexture(pn).name);
                }
                else if ((pt == ShaderPropertyType.Float || pt == ShaderPropertyType.Range) && (ln.Contains("sway") || ln.Contains("wind") || ln.Contains("bend") || ln.Contains("wave") || ln.Contains("ripple") || ln.Contains("push") || ln.Contains("spherenormals")))
                { bbMat.SetFloat(pn, 0f); log.Add(pn + "=0"); }
            }
            dbl = !(bbMat.HasProperty("_Cull") && bbMat.GetFloat("_Cull") == 0f);
            Plugin.Log("Vegetation: Abbild-Material aus " + (src != null ? src.name + " [" + src.shader.name + "]" : "Ersatz") + " " + string.Join(" ", log.ToArray()));
            return bbMat;
        }

        // ein Baum als Kreuz: zwei senkrechte Flaechen (vorne/Seite) + eine waagrechte (oben); beide Seiten sichtbar
        static void AddTree(List<Vector3> V, List<Vector3> N, List<Vector2> UV, List<int> T, BB b, Vector3 p, float yw, float s, bool top)
        {
            var q = Quaternion.Euler(0f, yw, 0f);
            for (int v = 0; v < 2; v++)
            {
                var right = v == 0 ? Vector3.right : Vector3.back;           // Bild-rechts der jeweiligen Aufnahme
                var rc = v == 0 ? b.Front : b.Side;
                int i0 = V.Count;
                for (int c = 0; c < 4; c++)
                {
                    float sx = (c == 1 || c == 2) ? 1f : -1f, sy = c >= 2 ? 1f : -1f;
                    var lp = right * (sx * b.Hw) + Vector3.up * (b.Cy + sy * b.Hh);
                    V.Add(p + q * (lp * s));
                    N.Add((q * (Vector3.up + right * (sx * 0.35f))).normalized);
                    UV.Add(new Vector2(rc.x + (sx * 0.5f + 0.5f) * rc.width, rc.y + (sy * 0.5f + 0.5f) * rc.height));
                }
                T.Add(i0); T.Add(i0 + 2); T.Add(i0 + 1); T.Add(i0); T.Add(i0 + 3); T.Add(i0 + 2);
                if (dbl) { T.Add(i0); T.Add(i0 + 1); T.Add(i0 + 2); T.Add(i0); T.Add(i0 + 2); T.Add(i0 + 3); }
            }
            if (top)
            {
                int i0 = V.Count; var rc = b.Top;
                for (int c = 0; c < 4; c++)
                {
                    float sx = (c == 1 || c == 2) ? 1f : -1f, sz = c >= 2 ? 1f : -1f;
                    var lp = new Vector3(sx * b.TopHw, b.TopY, sz * b.TopHw);
                    V.Add(p + q * (lp * s)); N.Add(Vector3.up);
                    UV.Add(new Vector2(rc.x + (sx * 0.5f + 0.5f) * rc.width, rc.y + (sz * 0.5f + 0.5f) * rc.height));
                }
                T.Add(i0); T.Add(i0 + 2); T.Add(i0 + 1); T.Add(i0); T.Add(i0 + 3); T.Add(i0 + 2);
                if (dbl) { T.Add(i0); T.Add(i0 + 1); T.Add(i0 + 2); T.Add(i0); T.Add(i0 + 2); T.Add(i0 + 3); }
            }
        }

        static GameObject BuildCell(List<int> l, bool isCoarse)
        {
            if (bbMat == null) return null;
            var V = new List<Vector3>(); var N = new List<Vector3>(); var UV = new List<Vector2>(); var T = new List<int>();
            var rocks = new Dictionary<Material, List<CombineInstance>>();
            foreach (var i in l)
            {
                var k0 = kinds[kind[i]];
                if (k0 == "stubbe") continue;
                bool small = k0.StartsWith("shrub") || k0.StartsWith("Bush");
                if (small && (isCoarse || scl[i] < 0.45f)) continue;      // kleine Buesche sieht man von weitem nicht
                var name = KindAt(i);
                if (IsRock(name))
                {
                    if (Proto(name) == null) continue;
                    List<KeyValuePair<Mesh, Material[]>> parts; if (!rockParts.TryGetValue(name, out parts)) continue;
                    var m = Matrix4x4.TRS(Ground(i), Quaternion.Euler(0f, yaw[i], 0f), Vector3.one * scl[i]);
                    foreach (var pt in parts)
                        for (int s = 0; s < pt.Key.subMeshCount && s < pt.Value.Length; s++)
                        {
                            var mat = pt.Value[s]; if (mat == null) continue;
                            List<CombineInstance> ci; if (!rocks.TryGetValue(mat, out ci)) rocks[mat] = ci = new List<CombineInstance>();
                            ci.Add(new CombineInstance { mesh = pt.Key, subMeshIndex = s, transform = m });
                        }
                    continue;
                }
                BB b; if (!bb.TryGetValue(name, out b) && !bb.TryGetValue(k0, out b)) continue;
                AddTree(V, N, UV, T, b, Ground(i), yaw[i], scl[i], !isCoarse);
            }
            if (V.Count == 0 && rocks.Count == 0) return null;
            var go = new GameObject(isCoarse ? "gi_foliage_coarse" : "gi_foliage_fine"); go.transform.SetParent(holder.transform, false);
            if (V.Count > 0)
            {
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32, name = "gi_bb" };
                mesh.SetVertices(V); mesh.SetNormals(N); mesh.SetUVs(0, UV);
                var cols = new Color32[V.Count]; for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255); mesh.colors32 = cols;
                mesh.SetTriangles(T, 0); mesh.RecalculateBounds();
                var c = new GameObject("bb"); c.transform.SetParent(go.transform, false);
                c.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = c.AddComponent<MeshRenderer>(); mr.sharedMaterial = bbMat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = true;
            }
            foreach (var kv in rocks)
            {
                var c = new GameObject(kv.Key.name); c.transform.SetParent(go.transform, false);
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.CombineMeshes(kv.Value.ToArray(), true, true); mesh.RecalculateBounds();
                c.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = c.AddComponent<MeshRenderer>(); mr.sharedMaterial = kv.Key; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = true;
            }
            return go;
        }

        public static string DumpAtlas(string path)
        {
            if (atlas == null) return "kein Abbild";
            var t = new Texture2D(AW, AH, TextureFormat.RGBA32, false);
            t.SetPixels32(atlas.GetPixels32(0)); t.Apply();
            File.WriteAllBytes(path, t.EncodeToPNG());
            UnityEngine.Object.Destroy(t);
            return "Abbilder -> " + path;
        }

        // Abbilder neu aufnehmen (z. B. nach Aenderungen am Licht) und alle Fernfelder neu bauen
        public static void Rebuild()
        {
            foreach (var g in fine.Values) if (g != null) UnityEngine.Object.Destroy(g);
            foreach (var g in coarse.Values) if (g != null) UnityEngine.Object.Destroy(g);
            fine.Clear(); coarse.Clear(); bb.Clear(); atlasReady = false; tickT = 0f;
        }

        public static void Retint(float k)
        {
            BbTint = k; if (bbMat != null && bbMat.HasProperty("_Color")) bbMat.color = new Color(k, k, k, 1f);
        }

        public static string Info()
        {
            if (!Active) return "Vegetation: keine";
            int fa = 0, ca = 0; foreach (var g in fine.Values) if (g != null && g.activeSelf) fa++; foreach (var g in coarse.Values) if (g != null && g.activeSelf) ca++;
            return "Vegetation: " + pos.Length + " Pflanzen, nah " + live.Count + " (Warteschlange " + (todo.Count - todoI) + "), Abbilder fein " + fa + "/" + fine.Count +
                   ", grob " + ca + "/" + coarse.Count + " (offen " + buildTodo.Count + "), Arten " + bb.Count;
        }
    }
}
