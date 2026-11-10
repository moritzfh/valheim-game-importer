using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace GameImporter
{
    // Laedt Figuren aus .glb-Dateien (glTF 2.0, binaer) zur Laufzeit: Meshes, Skelett (Skinning), Texturen und Animationen.
    // So koennen Originalfiguren mit ihrem eigenen Skelett und ihren Original-Animationen in Valheim stehen und sich bewegen.
    // Animationen werden zu "Legacy"-Clips (Animation-Komponente) - die lassen sich zur Laufzeit erzeugen.
    // glTF ist rechtshaendig, Unity linkshaendig: gespiegelt wird an der X-Achse.
    public static class Glb
    {
        static readonly Dictionary<string, GameObject> cache = new Dictionary<string, GameObject>();
        static readonly Dictionary<string, Texture2D> fileTex = new Dictionary<string, Texture2D>();       // Bilder per uri, von vielen Modellen geteilt
        static readonly Dictionary<string, Material> sharedMat = new Dictionary<string, Material>();     // gleiche Textur/Farbe -> gleiches Material
        static Shader shader, staticShader, cutoutShader;

        // ---------- Vorladen im Hintergrund ----------
        // Datei lesen + JSON zerlegen + Bilddateien lesen kostet bei grossen Gebaeuden viel Zeit. Das laeuft vorab in einem
        // Hintergrund-Thread; im Hauptthread bleibt nur das Bauen von Mesh/Textur (geht nur dort).
        class Pre { public object J; public byte[] Bin; public Exception Err; }
        static readonly object preLock = new object();
        static readonly Dictionary<string, Pre> pre = new Dictionary<string, Pre>();
        static readonly HashSet<string> preBusy = new HashSet<string>();
        static readonly Dictionary<string, byte[]> preTex = new Dictionary<string, byte[]>();
        static readonly HashSet<string> texDone = new HashSet<string>();
        public static int PrefetchCount { get { lock (preLock) return preBusy.Count; } }

        // Vorgeladenes, das inzwischen anders geladen wurde, wegwerfen (sonst bleibt es ewig liegen)
        public static void PurgePrefetch()
        {
            lock (preLock)
            {
                if (pre.Count == 0) return;
                var drop = new List<string>();
                foreach (var k in pre.Keys) if (IsCached(k)) drop.Add(k);
                foreach (var k in drop) pre.Remove(k);
            }
        }

        public static bool IsCached(string path) { GameObject g; return cache.TryGetValue(path, out g) && g != null; }

        public static void Prefetch(string path)
        {
            lock (preLock)
            {
                if (pre.ContainsKey(path) || preBusy.Contains(path) || preBusy.Count >= 4 || pre.Count >= 48) return;
                preBusy.Add(path);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var p = new Pre();
                try
                {
                    ParseGlb(File.ReadAllBytes(path), Path.GetFileName(path), out p.J, out p.Bin);
                    var imgs = Json.L(p.J, "images");
                    var dir = Path.GetDirectoryName(path);
                    if (imgs != null) foreach (var img in imgs)
                        {
                            var u = Json.S(img, "uri");
                            if (u == null) continue;
                            var fp = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(u)));
                            bool need; lock (preLock) need = !preTex.ContainsKey(fp) && !texDone.Contains(fp);
                            if (!need || !File.Exists(fp)) continue;
                            var bytes = File.ReadAllBytes(fp);
                            lock (preLock) if (!texDone.Contains(fp)) preTex[fp] = bytes;
                        }
                }
                catch (Exception e) { p.Err = e; }
                lock (preLock) { pre[path] = p; preBusy.Remove(path); }
            });
        }

        static void ParseGlb(byte[] data, string name, out object J, out byte[] Bin)
        {
            J = null; Bin = null;
            if (data.Length < 20 || BitConverter.ToUInt32(data, 0) != 0x46546C67) throw new Exception(name + ": keine .glb-Datei");
            int pos = 12;
            while (pos + 8 <= data.Length)
            {
                int len = BitConverter.ToInt32(data, pos); uint type = BitConverter.ToUInt32(data, pos + 4);
                if (type == 0x4E4F534A) J = Json.Parse(Encoding.UTF8.GetString(data, pos + 8, len));
                else if (type == 0x004E4942) { Bin = new byte[len]; Buffer.BlockCopy(data, pos + 8, Bin, 0, len); }
                pos += 8 + len;
            }
            if (J == null) throw new Exception("glb ohne JSON");
        }

        // Vorlagen von Modellen (nicht von Gebaeuden) vergessen, damit neu gebaute .glb-Dateien beim naechsten Mal frisch geladen werden
        public static int ForgetModels()
        {
            var keys = new List<string>();
            foreach (var kv in cache) if (kv.Key.IndexOf(Path.DirectorySeparatorChar + "models" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0) keys.Add(kv.Key);
            foreach (var k in keys) { if (cache[k] != null) UnityEngine.Object.Destroy(cache[k]); cache.Remove(k); }
            return keys.Count;
        }

        // Vorlage einmal laden (unsichtbar), danach pro Figur kopieren. isStatic: Gebaeude/Objekte (Valheims Bau-Shader)
        public static GameObject Instantiate(string path, bool isStatic = false)
        {
            GameObject tpl;
            if (!cache.TryGetValue(path, out tpl) || tpl == null)
            {
                tpl = Load(path, isStatic);
                tpl.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(tpl);
                cache[path] = tpl;
            }
            var go = UnityEngine.Object.Instantiate(tpl);
            go.SetActive(true);
            return go;
        }

        class Ctx
        {
            public object J; public byte[] Bin; public string Dir; public bool Static, Vc;
            public List<object> Accessors, Views, Nodes;
            public Transform[] T;
            public readonly Dictionary<int, Texture2D> Tex = new Dictionary<int, Texture2D>();
            public readonly Dictionary<int, Material> Mat = new Dictionary<int, Material>();
        }

        public static GameObject Load(string path, bool isStatic = false)
        {
            var c = new Ctx { Dir = Path.GetDirectoryName(path), Static = isStatic };
            Pre pr = null;
            lock (preLock) { if (pre.TryGetValue(path, out pr)) pre.Remove(path); }
            if (pr != null && pr.Err == null && pr.J != null) { c.J = pr.J; c.Bin = pr.Bin; }
            else ParseGlb(File.ReadAllBytes(path), Path.GetFileName(path), out c.J, out c.Bin);
            c.Accessors = Json.L(c.J, "accessors") ?? new List<object>();
            c.Views = Json.L(c.J, "bufferViews") ?? new List<object>();
            c.Nodes = Json.L(c.J, "nodes") ?? new List<object>();

            var root = new GameObject("glb_" + Path.GetFileNameWithoutExtension(path));
            // Knoten anlegen
            c.T = new Transform[c.Nodes.Count];
            for (int i = 0; i < c.Nodes.Count; i++)
            {
                var n = c.Nodes[i];
                var go = new GameObject(Json.S(n, "name", "node" + i));
                c.T[i] = go.transform;
            }
            var isChild = new bool[c.Nodes.Count];
            for (int i = 0; i < c.Nodes.Count; i++)
            {
                var ch = Json.L(c.Nodes[i], "children");
                if (ch != null) foreach (var x in ch) { int k = I(x); c.T[k].SetParent(c.T[i], false); isChild[k] = true; }
            }
            for (int i = 0; i < c.Nodes.Count; i++)
            {
                if (!isChild[i]) c.T[i].SetParent(root.transform, false);
                SetTrs(c.T[i], c.Nodes[i]);
            }
            // Meshes erst, wenn alle Knoten stehen (Skins verweisen auf Knoten)
            for (int i = 0; i < c.Nodes.Count; i++)
            {
                var n = c.Nodes[i];
                if (!Json.Has(n, "mesh")) continue;
                BuildMesh(c, c.T[i].gameObject, I(Json.V(n, "mesh")), Json.Has(n, "skin") ? I(Json.V(n, "skin")) : -1);
            }
            // Animationen
            var anims = Json.L(c.J, "animations");
            if (anims != null && anims.Count > 0)
            {
                var an = root.AddComponent<Animation>();
                for (int a = 0; a < anims.Count; a++)
                {
                    var clip = BuildClip(c, root.transform, anims[a], a);
                    if (clip == null) continue;
                    an.AddClip(clip, clip.name);
                    if (an.clip == null) an.clip = clip;
                }
                an.playAutomatically = false;
                an.cullingType = AnimationCullingType.AlwaysAnimate;
            }
            return root;
        }

        // ---------- Knoten ----------
        static void SetTrs(Transform t, object n)
        {
            var m = Json.L(n, "matrix");
            if (m != null && m.Count == 16)
            {
                var mx = new Matrix4x4();
                for (int i = 0; i < 16; i++) mx[i % 4, i / 4] = F(m[i]);     // glTF ist spaltenweise
                mx = Flip(mx);
                t.localPosition = mx.GetColumn(3);
                t.localRotation = mx.rotation;
                t.localScale = mx.lossyScale;
                return;
            }
            var tr = Json.L(n, "translation"); var r = Json.L(n, "rotation"); var s = Json.L(n, "scale");
            if (tr != null) t.localPosition = new Vector3(-F(tr[0]), F(tr[1]), F(tr[2]));
            if (r != null) t.localRotation = new Quaternion(F(r[0]), -F(r[1]), -F(r[2]), F(r[3]));
            if (s != null) t.localScale = new Vector3(F(s[0]), F(s[1]), F(s[2]));
        }

        static Matrix4x4 Flip(Matrix4x4 m)
        {
            var s = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            return s * m * s;
        }

        // ---------- Meshes ----------
        static void BuildMesh(Ctx c, GameObject go, int meshIdx, int skinIdx)
        {
            var mesh = Json.L(c.J, "meshes")[meshIdx];
            var prims = Json.L(mesh, "primitives");
            if (prims == null) return;
            var verts = new List<Vector3>(); var norms = new List<Vector3>(); var uvs = new List<Vector2>(); var bw = new List<BoneWeight>();
            var cols = new List<Color>(); bool anyCol = false;
            var subs = new List<int[]>(); var mats = new List<Material>(); var noCol = new List<bool>(); bool needTan = false;
            foreach (var p in prims)
            {
                var at = Json.O(p, "attributes");
                if (at == null || !at.ContainsKey("POSITION")) continue;
                int mode = Json.Has(p, "mode") ? I(Json.V(p, "mode")) : 4;
                if (mode != 4) continue;                                   // nur Dreiecke
                int baseV = verts.Count;
                var P = Floats(c, I(at["POSITION"]), 3);
                int nv = P.Length / 3;
                for (int i = 0; i < nv; i++) verts.Add(new Vector3(-P[i * 3], P[i * 3 + 1], P[i * 3 + 2]));
                object a;
                if (at.TryGetValue("NORMAL", out a)) { var N = Floats(c, I(a), 3); for (int i = 0; i < nv; i++) norms.Add(new Vector3(-N[i * 3], N[i * 3 + 1], N[i * 3 + 2])); }
                else for (int i = 0; i < nv; i++) norms.Add(Vector3.up);
                if (at.TryGetValue("TEXCOORD_0", out a)) { var U = Floats(c, I(a), 2); for (int i = 0; i < nv; i++) uvs.Add(new Vector2(U[i * 2], 1f - U[i * 2 + 1])); }
                else for (int i = 0; i < nv; i++) uvs.Add(Vector2.zero);
                // Vertexfarben (N64: Licht steckt in den Farben), nur wenn das Paket einen Vertexfarben-Shader nennt
                if (VcShaderName != null && at.TryGetValue("COLOR_0", out a))
                {
                    var acc = c.Accessors[I(a)]; int nc = Json.S(acc, "type") == "VEC3" ? 3 : 4;
                    var Cc = Floats(c, I(a), nc); anyCol = true; c.Vc = true;
                    for (int i = 0; i < nv; i++) cols.Add(new Color(Cc[i * nc], Cc[i * nc + 1], Cc[i * nc + 2], nc == 4 ? Cc[i * nc + 3] : 1f));
                }
                else for (int i = 0; i < nv; i++) cols.Add(Color.white);
                if (skinIdx >= 0 && at.ContainsKey("JOINTS_0") && at.ContainsKey("WEIGHTS_0"))
                {
                    var Jn = Floats(c, I(at["JOINTS_0"]), 4); var W = Floats(c, I(at["WEIGHTS_0"]), 4);
                    for (int i = 0; i < nv; i++)
                    {
                        float s = W[i * 4] + W[i * 4 + 1] + W[i * 4 + 2] + W[i * 4 + 3]; if (s <= 0f) s = 1f;
                        bw.Add(new BoneWeight
                        {
                            boneIndex0 = (int)Jn[i * 4], weight0 = W[i * 4] / s, boneIndex1 = (int)Jn[i * 4 + 1], weight1 = W[i * 4 + 1] / s,
                            boneIndex2 = (int)Jn[i * 4 + 2], weight2 = W[i * 4 + 2] / s, boneIndex3 = (int)Jn[i * 4 + 3], weight3 = W[i * 4 + 3] / s
                        });
                    }
                }
                else if (skinIdx >= 0) for (int i = 0; i < nv; i++) bw.Add(new BoneWeight { weight0 = 1f });
                int[] idx;
                if (Json.Has(p, "indices")) { var X = Floats(c, I(Json.V(p, "indices")), 1); idx = new int[X.Length]; for (int i = 0; i < X.Length; i++) idx[i] = (int)X[i] + baseV; }
                else { idx = new int[nv]; for (int i = 0; i < nv; i++) idx[i] = baseV + i; }
                for (int i = 0; i + 2 < idx.Length; i += 3) { int t0 = idx[i + 1]; idx[i + 1] = idx[i + 2]; idx[i + 2] = t0; }   // Spiegelung dreht die Wicklung
                subs.Add(idx);
                mats.Add(Json.Has(p, "material") ? Mat(c, I(Json.V(p, "material"))) : Mat(c, -1));
                {
                    // Wasserflaechen (Bach): ohne Kollision, sonst laeuft man auf dem Wasser statt hindurch
                    var mo_ = Json.Has(p, "material") ? Json.L(c.J, "materials")[I(Json.V(p, "material"))] : null;
                    var ex_ = Json.O(mo_, "extras");
                    noCol.Add(Json.B(ex_, "nocol") || Json.S(ex_, "valheim", "") == "@water");
                    if (Json.O(mo_, "normalTexture") != null) needTan = true;
                }
            }
            if (verts.Count == 0) return;
            var m = new Mesh { name = Json.S(mesh, "name", "mesh" + meshIdx) };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts); m.SetNormals(norms); m.SetUVs(0, uvs);
            if (anyCol) m.SetColors(cols);
            m.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) m.SetTriangles(subs[i], i);
            // Normal-Maps brauchen Tangenten (sonst wirkt die Rinde flach bzw. falsch beleuchtet)
            if (needTan && skinIdx < 0) { try { m.RecalculateTangents(); } catch (Exception e) { Plugin.Quiet(e, "Glb.Tangenten"); } }
            if (skinIdx < 0 && noCol.Contains(true))
            {
                var cm = new Mesh { name = m.name + "_col" };
                if (verts.Count > 65000) cm.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                cm.SetVertices(verts);
                var tl = new List<int>();
                for (int i = 0; i < subs.Count; i++) if (!noCol[i]) tl.AddRange(subs[i]);
                cm.SetTriangles(tl, 0); cm.RecalculateBounds();
                ColMesh[m] = cm;
            }
            if (skinIdx < 0)
            {
                m.RecalculateBounds();
                go.AddComponent<MeshFilter>().sharedMesh = m;
                go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
                return;
            }
            var skin = Json.L(c.J, "skins")[skinIdx];
            var joints = Json.L(skin, "joints");
            var bones = new Transform[joints.Count];
            for (int i = 0; i < bones.Length; i++) bones[i] = c.T[I(joints[i])];
            var bind = new Matrix4x4[bones.Length];
            if (Json.Has(skin, "inverseBindMatrices"))
            {
                var B = Floats(c, I(Json.V(skin, "inverseBindMatrices")), 16);
                for (int b = 0; b < bones.Length; b++)
                {
                    var mx = new Matrix4x4();
                    for (int k = 0; k < 16; k++) mx[k % 4, k / 4] = B[b * 16 + k];
                    bind[b] = Flip(mx);
                }
            }
            else for (int b = 0; b < bones.Length; b++) bind[b] = Matrix4x4.identity;
            m.boneWeights = bw.ToArray();
            m.bindposes = bind;
            m.RecalculateBounds();
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = m; smr.bones = bones; smr.sharedMaterials = mats.ToArray();
            smr.rootBone = Json.Has(skin, "skeleton") ? c.T[I(Json.V(skin, "skeleton"))] : bones[0];
            smr.updateWhenOffscreen = true;
        }

        // ---------- Materialien ----------
        static Material Mat(Ctx c, int i)
        {
            Material m;
            if (c.Mat.TryGetValue(i, out m)) return m;
            var mo = i >= 0 ? Json.L(c.J, "materials")[i] : null;
            var pbr = Json.O(mo, "pbrMetallicRoughness");
            var col = Json.L(pbr, "baseColorFactor");
            var bt = Json.O(pbr, "baseColorTexture");
            // Valheim-Material statt eigener Textur: "extras": {"valheim": "stone_wall_2x1"} (Prefab, optional ":<n>" = n-tes Material)
            var vname = Json.S(Json.O(mo, "extras"), "valheim", "");
            if (!string.IsNullOrEmpty(vname))
            {
                var vm = ValheimMat(vname);
                if (vm != null) { c.Mat[i] = vm; return vm; }
            }
            // feste Modelle teilen sich Materialien mit derselben Bilddatei/Farbe (weniger Draw-Calls)
            string key = null;
            // eingebettete Bilder ohne Namen: nicht teilen (sonst bekaemen alle dieselbe Textur)
            // unsichtbar, aber mit Kollision (z. B. aufgemalte Baumwaende am Rand, Kollisionsnetz im Deku-Baum) – vor dem geteilten
            // Material pruefen, sonst bekommt ein unsichtbares Netz mit derselben Textur das sichtbare Material
            if (Json.B(Json.O(mo, "extras"), "hidden")) { m = HiddenMat(); c.Mat[i] = m; return m; }
            if (c.Static && (bt == null || TexKey(c, bt) != null))
            {
                key = TexKey(c, bt) ?? (col != null ? "c" + F(col[0]) + "," + F(col[1]) + "," + F(col[2]) : "default");
                if (Json.S(mo, "alphaMode") == "MASK") key += "|mask";
                if (c.Vc) key += "|vc";
                // Farbfaktor zur Textur (z. B. gebackene Helligkeitsstufen im Deku-Baum): eigenes Material je Faktor
                if (bt != null && col != null) key += "|c" + F(col[0]) + "," + F(col[1]) + "," + F(col[2]);
                if (Json.O(mo, "normalTexture") != null) key += "|n";
                { float lf = Json.F(Json.O(mo, "extras"), "lift", 0f); if (lf > 0f) key += "|l" + lf; }
                if (sharedMat.TryGetValue(key, out m) && m != null) { c.Mat[i] = m; return m; }
            }
            // leuchtende, halbdurchsichtige Flaechen (Lichtstrahlen, Schein): weich additiv statt fester Flaeche
            var glowV = Json.V(Json.O(mo, "extras"), "glow");
            if (glowV is bool ? (bool)glowV : glowV is double)
            {
                // "glow": true = sehr dezent (Lichtschein), Zahl = Staerke (z. B. Lichtschacht 0.25); Farbe aus baseColorFactor
                var gt = bt != null ? Tex(c, I(Json.V(bt, "index"))) : null;
                m = FireFx.Make(gt);
                float ga = glowV is double ? (float)(double)glowV : 0.07f;
                var gc = col != null ? new Color(F(col[0]), F(col[1]), F(col[2])) : new Color(1f, 0.93f, 0.72f);
                if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(gc.r, gc.g, gc.b, ga));
                m.renderQueue = 3000;
                c.Mat[i] = m; return m;
            }
            bool mask = Json.S(mo, "alphaMode") == "MASK";
            if (c.Static && c.Vc && VcShader() != null)
            {
                // N64-Optik: Textur * Vertexfarbe (Licht ist in die Farben gebacken)
                m = new Material(VcShader());
                if (mask) { if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", Json.F(mo, "alphaCutoff", 0.5f)); m.EnableKeyword("_ALPHATEST_ON"); m.renderQueue = 2450; }
                if (col != null) m.color = new Color(F(col[0]), F(col[1]), F(col[2]), F(col[3])); else m.color = Color.white;
                if (bt != null) { var t = Tex(c, I(Json.V(bt, "index"))); if (t != null) m.mainTexture = t; }
                NoSnow(m);
                c.Mat[i] = m; if (key != null) sharedMat[key] = m; vcMats.Add(m);
                return m;
            }
            if (c.Static && mask) { if (cutoutShader == null) cutoutShader = FindCutoutShader(); m = new Material(cutoutShader); }
            else if (c.Static) { if (staticShader == null) staticShader = FindStaticShader(); m = new Material(staticShader); }
            else { if (shader == null) shader = FindShader(); m = new Material(shader); }
            if (mask)
            {
                // ausgestanzte Flaechen (Seile, Zaunlatten, Stroh): Pixel unter der Schwelle werden nicht gezeichnet
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", Json.F(mo, "alphaCutoff", 0.5f));
                m.EnableKeyword("_ALPHATEST_ON");
                if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 1f);
                m.renderQueue = 2450;
            }
            if (c.Static) NoSnow(m);
            if (Json.B(Json.O(mo, "extras"), "matte"))
            {
                // matt (z. B. Rinde): kein Glanz in der Sonne
                foreach (var pn in new[] { "_Glossiness", "_Smoothness", "_Metallic", "_GlossMapScale", "_SpecularHighlights", "_GlossyReflections" })
                    if (m.HasProperty(pn)) m.SetFloat(pn, 0f);
                if (m.HasProperty("_SpecColor")) m.SetColor("_SpecColor", Color.black);
                m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF"); m.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
            }
            // Glanz: Figuren matt wie Valheims eigene (Shader-Standard 0,5 wirkt nass); "extras": {"gloss": x} setzt ihn genau
            {
                float gl = Json.F(Json.O(mo, "extras"), "gloss", c.Static ? -1f : 0.1f);
                if (gl >= 0f && m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gl);
            }
            // Normal-Map (Stoff-/Lederstruktur)
            var nt = Json.O(mo, "normalTexture");
            if (nt != null && m.HasProperty("_BumpMap"))
            {
                var ntex = Tex(c, I(Json.V(nt, "index")), true);
                if (ntex != null) { m.SetTexture("_BumpMap", ntex); m.EnableKeyword("_NORMALMAP"); if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", Json.F(nt, "scale", 1f)); }
            }
            if (col != null) m.color = new Color(F(col[0]), F(col[1]), F(col[2]), F(col[3]));
            if (bt != null) { var t = Tex(c, I(Json.V(bt, "index"))); if (t != null) { Wrap(c, t, I(Json.V(bt, "index"))); m.mainTexture = t; if (col == null) m.color = Color.white; } }
            // "extras": {"lift": x}: Schattenseite aufhellen (Eigenleuchten = Textur * x), sonst wird die Figur im Schatten fast schwarz
            {
                float lift = Json.F(Json.O(mo, "extras"), "lift", 0f);
                if (lift > 0f && m.HasProperty("_EmissionColor"))
                {
                    if (m.mainTexture != null && m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", m.mainTexture);
                    var lc = col != null ? new Color(F(col[0]), F(col[1]), F(col[2])) : Color.white;       // Eigenleuchten folgt dem Farbfaktor
                    m.SetColor("_EmissionColor", new Color(lift * lc.r, lift * lc.g, lift * lc.b, 1f));
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                }
            }
            c.Mat[i] = m;
            if (key != null) sharedMat[key] = m;
            var part = Json.S(Json.O(mo, "extras"), "part", "");
            if (!string.IsNullOrEmpty(part)) PartOf[m] = part;
            return m;
        }

        // Valheims Shader legen ab einer gewissen Hoehe Schnee auf alles, was nach oben zeigt (Daecher, Baeume). Importierte Welten
        // liegen oft hoch (Kaer Morhen ~200 m) und haben ihren eigenen Schnee in den Texturen -> dort aus (Paket: "snow": true = an)
        static System.Reflection.MethodInfo setInteger;
        public static void NoSnow(Material m)
        {
            if (m == null || !m.HasProperty("_AddSnow")) return;
            if (Pack.Current != null && Pack.Current.Snow) return;
            try
            {
                if (setInteger == null) setInteger = typeof(Material).GetMethod("SetInteger", new[] { typeof(string), typeof(int) });
                if (setInteger != null) setInteger.Invoke(m, new object[] { "_AddSnow", 0 });
                m.SetFloat("_AddSnow", 0f);
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.NoSnow"); }
        }

        static string TexKey(Ctx c, Dictionary<string, object> bt)
        {
            if (bt == null) return null;
            try
            {
                var tx = Json.L(c.J, "textures")[I(Json.V(bt, "index"))];
                var img = Json.L(c.J, "images")[I(Json.V(tx, "source"))];
                var u = Json.S(img, "uri");
                if (u != null) return Path.GetFullPath(Path.Combine(c.Dir, Uri.UnescapeDataString(u)));
                // eingebettetes Bild: nur mit eindeutigem Namen teilbar (z. B. OoT "oot_<textur-hash>"), sonst eigenes Material
                var nm = Json.S(img, "name");
                return !string.IsNullOrEmpty(nm) ? "emb:" + nm : null;
            }
            catch { return null; }
        }

        // Shader nach Namen: Shader.Find kennt viele Valheim-Shader nicht (stecken in Asset-Bundles) -> in den Materialien der Prefabs suchen
        public static Shader FindPrefabShader(string name)
        {
            Shader sh = null;
            try { sh = Shader.Find(name); } catch { }
            if (sh != null) return sh;
            try
            {
                foreach (var pf in ZNetScene.instance.m_prefabs)
                {
                    if (pf == null) continue;
                    foreach (var r in pf.GetComponentsInChildren<Renderer>(true))
                        foreach (var mat in r.sharedMaterials)
                            if (mat != null && mat.shader != null && mat.shader.name == name) return mat.shader;
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.FindPrefabShader"); }
            return null;
        }

        // Vertexfarben-Shader fuer feste Modelle: pack.json "vc_shader" (z. B. OoT). Ohne Angabe bleibt alles wie bisher.
        public static string VcShaderName
        {
            get
            {
                if (vcOverride != null) return vcOverride;
                var pk = Pack.Current; if (pk == null || pk.Meta == null) return null;
                var n = Json.S(pk.Meta, "vc_shader", ""); return string.IsNullOrEmpty(n) ? null : n;
            }
        }
        static string vcOverride, vcFoundFor; static Shader vcShader;
        static readonly List<Material> vcMats = new List<Material>();
        static Shader VcShader()
        {
            var n = VcShaderName; if (n == null) return null;
            if (vcFoundFor == n) return vcShader;
            vcFoundFor = n; vcShader = FindPrefabShader(n);
            Plugin.Log("Vertexfarben-Shader " + n + (vcShader != null ? " gefunden" : " FEHLT"));
            return vcShader;
        }
        // Material eines Valheim-Prefabs (Kopie ohne Hoehen-Schnee), z. B. "stone_wall_2x1" oder "wood_wall_roof:1"
        // Teile einer Figur (GLB "extras": {"part": "sword_hand"}): Material -> Teilname, zum Ein-/Ausblenden (OotPlay)
        public static readonly Dictionary<Material, string> PartOf = new Dictionary<Material, string>();
        static Material hiddenMat;
        public static Material HiddenMat()
        {
            if (hiddenMat != null) return hiddenMat;
            if (cutoutShader == null) cutoutShader = FindCutoutShader();
            hiddenMat = new Material(cutoutShader) { name = "gi_hidden" }; hiddenMat.color = new Color(0f, 0f, 0f, 0f);
            if (hiddenMat.HasProperty("_Cutoff")) hiddenMat.SetFloat("_Cutoff", 1f);
            hiddenMat.EnableKeyword("_ALPHATEST_ON"); hiddenMat.renderQueue = 2450;
            return hiddenMat;
        }
        // Kollisionsnetz ohne Wasserflaechen (nur fuer Netze mit Wasser), fuer Statics.Spawn
        public static readonly Dictionary<Mesh, Mesh> ColMesh = new Dictionary<Mesh, Mesh>();
        static readonly Dictionary<string, Material> vMats = new Dictionary<string, Material>();
        public static Material ValheimMat(string spec)
        {
            Material m;
            if (vMats.TryGetValue(spec, out m)) return m;
            if (spec == "@water")
            {
                // Valheims Wasser (Meer) als Material fuer Fluesse/Baeche eines Pakets
                try
                {
                    foreach (var wv in UnityEngine.Object.FindObjectsOfType<WaterVolume>())
                        if (wv.m_waterSurface != null && wv.m_waterSurface.sharedMaterial != null)
                        { m = new Material(wv.m_waterSurface.sharedMaterial); m.name = "gi_v_water"; vMats[spec] = m; Plugin.Log("Wasser-Material: " + m.shader.name); return m; }
                }
                catch (Exception e) { Plugin.Quiet(e, "Glb.Wasser"); }
                return null;      // noch kein Wasser geladen: spaeter erneut versuchen (nicht merken)
            }
            string pf = spec; int idx = 0;
            int k = spec.LastIndexOf(':'); if (k > 0) { int.TryParse(spec.Substring(k + 1), out idx); pf = spec.Substring(0, k); }
            try
            {
                var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(pf) : null;
                if (go == null) { Plugin.Warn("Valheim-Material: Prefab fehlt " + pf); vMats[spec] = null; return null; }
                var all = new List<Material>();
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer) continue;
                    var n = r.gameObject.name.ToLowerInvariant();
                    if (n.Contains("collider") || n.Contains("lod1") || n.Contains("lod2") || n.Contains("worn") || n.Contains("broken") || n.Contains("destruction") || n.Contains("snow")) continue;
                    foreach (var mt in r.sharedMaterials) if (mt != null && !all.Contains(mt) && mt.mainTexture != null && (mt.shader == null || !mt.shader.name.Contains("Snow"))) all.Add(mt);
                }
                if (all.Count == 0) { vMats[spec] = null; return null; }
                m = new Material(all[Mathf.Clamp(idx, 0, all.Count - 1)]); m.name = "gi_v_" + spec;
                NoSnow(m);
                // Valheims Vegetations-Shader wiegt Ecken im Wind: auf festen Modellen (Stamm, Krone eines Pakets) still halten
                foreach (var pn in new[] { "_SwaySpeed", "_SwayDistance", "_RippleSpeed", "_RippleDistance", "_PushDistance" })
                    if (m.HasProperty(pn)) m.SetFloat(pn, 0f);
                vMats[spec] = m;
                return m;
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.ValheimMat"); vMats[spec] = null; return null; }
        }

        // Fehlersuche: wie legt Valheim die Textur auf ein Bauteil? ("statics matinfo <prefab>")
        public static string MatInfo(string pf)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(pf) : null;
            if (go == null) return "fehlt: " + pf;
            var sb = new System.Text.StringBuilder(pf + ":");
            foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>(); var me = mf != null ? mf.sharedMesh : null;
                sb.Append(" || " + r.gameObject.name + " act=" + r.gameObject.activeSelf);
                if (me != null)
                {
                    var b = me.bounds; Vector2 umin = Vector2.one * 1e9f, umax = -umin;
                    try { foreach (var u in me.uv) { umin = Vector2.Min(umin, u); umax = Vector2.Max(umax, u); } } catch { }
                    sb.Append(" mesh=" + me.name + " size=" + b.size.ToString("F2") + " ls=" + r.transform.lossyScale.ToString("F2") + " uv=" + umin.ToString("F2") + ".." + umax.ToString("F2") + " v=" + me.vertexCount);
                }
                foreach (var mt in r.sharedMaterials)
                {
                    if (mt == null) continue;
                    var t = mt.HasProperty("_MainTex") ? mt.mainTexture : null;
                    sb.Append(" [" + mt.name + " sh=" + (mt.shader != null ? mt.shader.name : "?") + " tex=" + (t != null ? t.name + " " + t.width + "x" + t.height : "-") + " st=" + (mt.HasProperty("_MainTex") ? mt.mainTextureScale.ToString("F2") : "-") + "]");
                }
            }
            return sb.ToString();
        }

        // Textur eines Valheim-Materials als PNG speichern ("statics dumptex stone_wall_4x2 [n]") - fuer gebackene Mischtexturen
        public static string DumpTex(string spec)
        {
            var m = ValheimMat(spec);
            if (m == null || m.mainTexture == null) return "keine Textur: " + spec;
            var t = m.mainTexture;
            var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(t, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0); tex.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var path = System.IO.Path.Combine(Remote.Dir, "tex_" + spec.Replace(':', '_') + ".png");
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex);
            return "Textur " + t.name + " " + t.width + "x" + t.height + " -> " + path;
        }

        // Sauberer Weg (tools/oot/oot_build.py): alle Valheim-Texturen, die der OoT-Paketbauer braucht, aus der EIGENEN Valheim-
        // Installation holen -> config/GameImporter/vtex und vmat ("statics dumpvtex", im Spiel in einer Welt ausfuehren)
        public static string DumpVTex()
        {
            string vt = System.IO.Path.Combine(Remote.Dir, "vtex"), vm = System.IO.Path.Combine(Remote.Dir, "vmat");
            System.IO.Directory.CreateDirectory(vt); System.IO.Directory.CreateDirectory(vm);
            var sb = new System.Text.StringBuilder(); int n = 0;
            foreach (var s in new[] { "Oak1:0", "Oak1:1", "Beech1:0", "FirTree:0", "rock4_forest", "stone_wall_4x2", "wood_floor", "Deer" })
            {
                sb.AppendLine(DumpTex(s));
                var f = System.IO.Path.Combine(Remote.Dir, "tex_" + s.Replace(':', '_') + ".png");
                if (System.IO.File.Exists(f)) { System.IO.File.Copy(f, System.IO.Path.Combine(vt, System.IO.Path.GetFileName(f)), true); n++; }
            }
            // Kurznamen, die die Exporter erwarten
            foreach (var kv in new[] { new[] { "tex_stone_wall_4x2.png", "stone.png" }, new[] { "tex_wood_floor.png", "wood.png" } })
            {
                var f = System.IO.Path.Combine(vt, kv[0]);
                if (System.IO.File.Exists(f)) System.IO.File.Copy(f, System.IO.Path.Combine(vt, kv[1]), true);
            }
            foreach (var s in new[] { "terrain", "Oak1", "Oak1:1", "Beech1", "YggaShoot1", "SwampTree1", "HugeRoot1", "YggdrasilRoot", "rock4_forest", "vines" })
            {
                sb.AppendLine(DumpMat(s, null));
                foreach (var f in System.IO.Directory.GetFiles(Remote.Dir, "mat_" + s.Replace(':', '_') + "_*.png"))
                { System.IO.File.Copy(f, System.IO.Path.Combine(vm, System.IO.Path.GetFileName(f)), true); n++; }
            }
            return n + " Dateien -> " + vt + " / " + vm + "\n" + sb;
        }

        // Alle Texturen eines Materials als PNG ("statics dumpmat <prefab[:n]|terrain> [filter]"): Remaster-Vorlagen
        // (terrain = Valheims Gelaende-Material mit Wald-/Gras-/Fels-Texturen)
        public static string DumpMat(string spec, string filter)
        {
            Material m = null;
            if (spec == "terrain")
            {
                foreach (var hm in UnityEngine.Object.FindObjectsOfType<Heightmap>())
                {
                    var r = hm.GetComponent<MeshRenderer>(); if (r == null) r = hm.GetComponentInChildren<MeshRenderer>();
                    if (r != null && r.sharedMaterial != null) { m = r.sharedMaterial; break; }
                }
            }
            else m = ValheimMat(spec);
            if (m == null) return "kein Material: " + spec;
            var sb = new System.Text.StringBuilder("material " + m.name + " shader " + (m.shader != null ? m.shader.name : "?") + ":");
            foreach (var pn in m.GetTexturePropertyNames())
            {
                Texture t = null; try { t = m.GetTexture(pn); } catch { }
                if (t == null) continue;
                sb.Append(" " + pn + "=" + t.name + " " + t.width + "x" + t.height + (t is Texture2DArray ? " (array " + ((Texture2DArray)t).depth + ")" : ""));
                if (filter != null && pn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    int layers = t is Texture2DArray ? ((Texture2DArray)t).depth : 1;
                    for (int li = 0; li < layers; li++)
                    {
                        var rt = RenderTexture.GetTemporary(t.width, t.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                        if (t is Texture2DArray) Graphics.Blit(t, rt, li, 0); else Graphics.Blit(t, rt);
                        var prev = RenderTexture.active; RenderTexture.active = rt;
                        var tex = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                        tex.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0); tex.Apply();
                        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                        var path = System.IO.Path.Combine(Remote.Dir, "mat_" + spec.Replace(':', '_') + "_" + pn.TrimStart('_') + (layers > 1 ? "_" + li : "") + ".png");
                        System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
                        UnityEngine.Object.Destroy(tex);
                    }
                }
                catch (Exception e) { sb.Append(" (Fehler " + e.Message + ")"); }
            }
            return sb.ToString();
        }

        // Fehlersuche: alle Shader-Namen in Valheims Prefabs ("statics shaders")
        public static string ListShaders()
        {
            var seen = new SortedSet<string>();
            try
            {
                foreach (var pf in ZNetScene.instance.m_prefabs)
                {
                    if (pf == null) continue;
                    foreach (var r in pf.GetComponentsInChildren<Renderer>(true))
                        foreach (var mat in r.sharedMaterials) if (mat != null && mat.shader != null) seen.Add(mat.shader.name);
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.ListShaders"); }
            foreach (var n in new[] { "Sprites/Default", "Particles/Standard Unlit", "Particles/Standard Surface", "Legacy Shaders/Particles/Alpha Blended", "Unlit/Texture", "Standard" })
                try { if (Shader.Find(n) != null) seen.Add(n + " (Find)"); } catch { }
            return string.Join(" | ", seen);
        }

        // Fehlersuche: "statics vcshader <name>" tauscht den Shader aller Vertexfarben-Materialien (zum Ausprobieren)
        public static string SetVcShader(string name)
        {
            vcOverride = name; vcFoundFor = null;
            var sh = VcShader(); if (sh == null) return "Shader fehlt: " + name;
            int n = 0; foreach (var m in vcMats) if (m != null) { var t = m.mainTexture; var col = m.color; m.shader = sh; m.mainTexture = t; m.color = col; n++; }
            return "Vertexfarben: Shader " + sh.name + " (" + n + " Materialien)";
        }

        // Fehlersuche: Shader aller festen Modelle tauschen ("statics shader Custom/Piece")
        public static string SetStaticShader(string name)
        {
            var sh = FindPrefabShader(name);
            if (sh == null) return "Shader fehlt: " + name;
            staticShader = sh; int n = 0;
            foreach (var kv in sharedMat) if (kv.Value != null && !kv.Key.EndsWith("|mask")) { kv.Value.shader = sh; n++; }
            return "feste Modelle: Shader " + sh.name + " (" + n + " Materialien)";
        }

        // Shader von Valheims Bauteilen (Holzwand) - passt zu Licht, Nebel und Schnee der Welt
        static Shader FindStaticShader()
        {
            // "Valheim/Snow Mesh" (Steinwand, Dach) verschiebt Ecken im Shader -> fremde Modelle zerfallen zu Streifen, die zum
            // Modell-Ursprung laufen (Burg Kaer Trolde). Darum Valheims normaler Bauteil-Shader. Shader.Find kennt ihn nicht
            // (steckt im Asset-Bundle), also ueber die Materialien der Prefabs suchen.
            var sp = FindPrefabShader("Custom/Piece");
            if (sp != null) { Plugin.Log("Feste Modelle: Shader Custom/Piece"); return sp; }
            try
            {
                foreach (var n in new[] { "stone_wall_2x1", "woodwall", "wood_floor", "wood_wall_roof" })
                {
                    var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(n) : null;
                    var r = pf != null ? pf.GetComponentInChildren<MeshRenderer>(true) : null;
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name != "Valheim/Snow Mesh")
                    {
                        Plugin.Log("Feste Modelle: Shader " + r.sharedMaterial.shader.name + " (von " + n + ")");
                        return r.sharedMaterial.shader;
                    }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.staticShader"); }
            Plugin.Warn("Feste Modelle: Custom/Piece nicht gefunden");
            return FindPrefabShader("Standard") ?? FindShader();
        }

        // Shader mit Ausstanzen (_Cutoff): erst der Bau-Shader selbst, sonst ein passender aus Valheims Prefabs
        static Shader FindCutoutShader()
        {
            if (staticShader == null) staticShader = FindStaticShader();
            if (staticShader != null && new Material(staticShader).HasProperty("_Cutoff")) { Plugin.Log("Ausstanz-Shader: " + staticShader.name); return staticShader; }
            Shader best = null; int bestScore = -1;
            var seen = new HashSet<string>();
            try
            {
                foreach (var pf in ZNetScene.instance.m_prefabs)
                {
                    if (pf == null) continue;
                    foreach (var r in pf.GetComponentsInChildren<MeshRenderer>(true))
                        foreach (var mat in r.sharedMaterials)
                        {
                            if (mat == null || mat.shader == null || !mat.HasProperty("_Cutoff") || !mat.HasProperty("_MainTex")) continue;
                            var n = mat.shader.name;
                            if (!seen.Add(n)) continue;
                            var l = n.ToLowerInvariant();
                            if (l.Contains("particle") || l.Contains("ui") || l.Contains("sprite") || l.Contains("hidden") || l.Contains("water") || l.Contains("grass") || l.Contains("billboard")) continue;
                            int score = l.Contains("piece") ? 5 : l.Contains("snow") ? 4 : l.Contains("standard") ? 3 : l.Contains("vegetation") ? 1 : 2;
                            Plugin.Log("Ausstanz-Shader Kandidat: " + n + " (" + pf.name + ")");
                            if (score > bestScore) { bestScore = score; best = mat.shader; }
                        }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.cutout"); }
            if (best == null) best = Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse") ?? Shader.Find("Standard") ?? staticShader;
            Plugin.Log("Ausstanz-Shader: " + (best != null ? best.name : "keiner"));
            return best;
        }

        // Valheims eigener Figuren-Shader sieht im Spiel-Licht richtig aus; sonst Standard
        static Shader FindShader()
        {
            // Valheims Kreaturen-Shader: normale Textur + Licht, ohne die Haut-/Ruestungsfarben-Logik des Spieler-Shaders
            try
            {
                foreach (var n in new[] { "Greydwarf", "Skeleton", "Draugr", "Boar", "Deer" })
                {
                    var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(n) : null;
                    var r = pf != null ? pf.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                    if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null)
                    {
                        Plugin.Log("Figuren-Shader: " + r.sharedMaterial.shader.name + " (von " + n + ")");
                        return r.sharedMaterial.shader;
                    }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.shader2"); }
            try
            {
                var pl = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                var r = pl != null ? pl.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
                if (r != null && r.sharedMaterial != null && r.sharedMaterial.shader != null) return r.sharedMaterial.shader;
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.shader"); }
            return Shader.Find("Standard") ?? Shader.Find("Unlit/Texture");
        }

        // Kachelung wie im glTF-Sampler (OoT: Clamp an Gesichtern/Augen, sonst wiederholt sich die Zeichnung am Kinn)
        static void Wrap(Ctx c, Texture2D t, int ti)
        {
            try
            {
                var tx = Json.L(c.J, "textures")[ti];
                if (!Json.Has(tx, "sampler")) return;
                var sm = Json.L(c.J, "samplers")[I(Json.V(tx, "sampler"))];
                Func<string, TextureWrapMode> W = k => { int v = (int)Json.F(sm, k, 10497f); return v == 33071 ? TextureWrapMode.Clamp : v == 33648 ? TextureWrapMode.Mirror : TextureWrapMode.Repeat; };
                t.wrapModeU = W("wrapS"); t.wrapModeV = W("wrapT");
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.wrap"); }
        }

        static Texture2D Tex(Ctx c, int ti, bool linear = false)
        {
            Texture2D t;
            int ck = linear ? ti + 1000000 : ti;
            if (c.Tex.TryGetValue(ck, out t)) return t;
            t = null;
            try
            {
                var tx = Json.L(c.J, "textures")[ti];
                var img = Json.L(c.J, "images")[I(Json.V(tx, "source"))];
                byte[] bytes = null;
                string fileKey = null;
                if (Json.Has(img, "bufferView")) bytes = View(c, I(Json.V(img, "bufferView")));
                else if (Json.S(img, "uri") != null)
                {
                    var p = Path.GetFullPath(Path.Combine(c.Dir, Uri.UnescapeDataString(Json.S(img, "uri"))));
                    fileKey = linear ? p + "#lin" : p;        // Normal-Maps (linear) getrennt merken
                    if (fileTex.TryGetValue(fileKey, out t) && t != null) { c.Tex[ck] = t; return t; }
                    lock (preLock) { if (preTex.TryGetValue(p, out bytes)) preTex.Remove(p); texDone.Add(p); }
                    if (bytes == null && File.Exists(p)) bytes = File.ReadAllBytes(p);
                }
                if (bytes != null)
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear); t.LoadImage(bytes); t.wrapMode = TextureWrapMode.Repeat;
                    if (c.Static) { t.anisoLevel = 4; t.Compress(true); }
                    if (fileKey != null) fileTex[fileKey] = t;
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Glb.tex"); }
            c.Tex[ck] = t;
            return t;
        }

        // ---------- Animationen ----------
        static AnimationClip BuildClip(Ctx c, Transform root, object anim, int ai)
        {
            var samplers = Json.L(anim, "samplers"); var channels = Json.L(anim, "channels");
            if (samplers == null || channels == null) return null;
            var clip = new AnimationClip { legacy = true, name = Json.S(anim, "name", "anim" + ai), wrapMode = WrapMode.Loop };
            foreach (var ch in channels)
            {
                var tg = Json.O(ch, "target");
                if (tg == null || !tg.ContainsKey("node")) continue;
                var node = c.T[I(tg["node"])];
                var pathStr = RelPath(root, node);
                var s = samplers[I(Json.V(ch, "sampler"))];
                var times = Floats(c, I(Json.V(s, "input")), 1);
                bool step = Json.S(s, "interpolation") == "STEP";
                bool cubic = Json.S(s, "interpolation") == "CUBICSPLINE";
                switch (Json.S(tg, "path"))
                {
                    case "translation":
                        {
                            var v = Floats(c, I(Json.V(s, "output")), 3); int st = cubic ? 9 : 3, o = cubic ? 3 : 0;
                            Curves(clip, pathStr, times, step, new[] { "localPosition.x", "localPosition.y", "localPosition.z" },
                                (i, k) => k == 0 ? -v[i * st + o] : v[i * st + o + k]);
                        }
                        break;
                    case "rotation":
                        {
                            var v = Floats(c, I(Json.V(s, "output")), 4); int st = cubic ? 12 : 4, o = cubic ? 4 : 0;
                            // Vorzeichen angleichen, damit die Interpolation nicht den langen Weg nimmt
                            for (int i = 1; i < times.Length; i++)
                            {
                                float d = 0f; for (int k = 0; k < 4; k++) d += v[i * st + o + k] * v[(i - 1) * st + o + k];
                                if (d < 0f) for (int k = 0; k < 4; k++) v[i * st + o + k] = -v[i * st + o + k];
                            }
                            Curves(clip, pathStr, times, step, new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w" },
                                (i, k) => (k == 1 || k == 2) ? -v[i * st + o + k] : v[i * st + o + k]);
                        }
                        break;
                    case "scale":
                        {
                            var v = Floats(c, I(Json.V(s, "output")), 3); int st = cubic ? 9 : 3, o = cubic ? 3 : 0;
                            Curves(clip, pathStr, times, step, new[] { "localScale.x", "localScale.y", "localScale.z" }, (i, k) => v[i * st + o + k]);
                        }
                        break;
                }
            }
            clip.EnsureQuaternionContinuity();
            return clip;
        }

        static void Curves(AnimationClip clip, string path, float[] times, bool step, string[] props, Func<int, int, float> val)
        {
            for (int k = 0; k < props.Length; k++)
            {
                var keys = new Keyframe[times.Length];
                for (int i = 0; i < times.Length; i++) keys[i] = new Keyframe(times[i], val(i, k));
                var cv = new AnimationCurve(keys);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (step) { var ks = cv.keys[i]; ks.inTangent = float.PositiveInfinity; ks.outTangent = float.PositiveInfinity; cv.MoveKey(i, ks); continue; }
                    // linear zwischen den Schluesseln (wie glTF LINEAR)
                    float inT = i > 0 ? (keys[i].value - keys[i - 1].value) / Mathf.Max(1e-5f, keys[i].time - keys[i - 1].time) : 0f;
                    float outT = i + 1 < keys.Length ? (keys[i + 1].value - keys[i].value) / Mathf.Max(1e-5f, keys[i + 1].time - keys[i].time) : 0f;
                    var kf = cv.keys[i]; kf.inTangent = inT; kf.outTangent = outT; cv.MoveKey(i, kf);
                }
                clip.SetCurve(path, typeof(Transform), props[k], cv);
            }
        }

        static string RelPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            while (t != null && t != root) { parts.Insert(0, t.name); t = t.parent; }
            return string.Join("/", parts.ToArray());
        }

        // ---------- Rohdaten ----------
        static byte[] View(Ctx c, int vi)
        {
            var v = c.Views[vi];
            int off = Json.Has(v, "byteOffset") ? I(Json.V(v, "byteOffset")) : 0, len = I(Json.V(v, "byteLength"));
            var r = new byte[len]; Buffer.BlockCopy(c.Bin, off, r, 0, len); return r;
        }

        // Accessor -> Floats (Ganzzahlen werden umgerechnet, normalisierte auf 0..1)
        static float[] Floats(Ctx c, int ai, int comps)
        {
            var a = c.Accessors[ai];
            int count = I(Json.V(a, "count")), ct = I(Json.V(a, "componentType"));
            bool norm = Json.B(a, "normalized");
            var r = new float[count * comps];
            if (!Json.Has(a, "bufferView")) return r;
            var v = c.Views[I(Json.V(a, "bufferView"))];
            int baseOff = (Json.Has(v, "byteOffset") ? I(Json.V(v, "byteOffset")) : 0) + (Json.Has(a, "byteOffset") ? I(Json.V(a, "byteOffset")) : 0);
            int csize = ct == 5126 || ct == 5125 ? 4 : ct == 5123 || ct == 5122 ? 2 : 1;
            int stride = Json.Has(v, "byteStride") ? I(Json.V(v, "byteStride")) : csize * comps;
            var b = c.Bin;
            for (int i = 0; i < count; i++)
                for (int k = 0; k < comps; k++)
                {
                    int p = baseOff + i * stride + k * csize;
                    float f;
                    switch (ct)
                    {
                        case 5126: f = BitConverter.ToSingle(b, p); break;
                        case 5125: f = BitConverter.ToUInt32(b, p); break;
                        case 5123: f = BitConverter.ToUInt16(b, p); if (norm) f /= 65535f; break;
                        case 5122: f = BitConverter.ToInt16(b, p); if (norm) f = Mathf.Max(f / 32767f, -1f); break;
                        case 5121: f = b[p]; if (norm) f /= 255f; break;
                        case 5120: f = (sbyte)b[p]; if (norm) f = Mathf.Max(f / 127f, -1f); break;
                        default: f = 0f; break;
                    }
                    r[i * comps + k] = f;
                }
            return r;
        }

        static int I(object o) { return o is double ? (int)(double)o : Convert.ToInt32(o); }
        static float F(object o) { return o is double ? (float)(double)o : Convert.ToSingle(o); }
    }
}
