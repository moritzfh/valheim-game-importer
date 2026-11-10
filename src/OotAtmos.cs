using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameImporter
{
    // Raumstimmung aus pack.json "oot_atmos" (Deku-Baum-Remaster): feste Teilchenquellen in der Welt.
    //   spores  leuchtende Sporen, die langsam treiben und aufsteigen (additiv, flackernd)
    //   flies   Gluehwuermchen (groesser, blinkend)
    //   dust    feiner Staub im Lichtschacht
    //   mist    Bodennebel: flache, weiche Schwaden knapp ueber dem Boden (liegen waagrecht, schneiden den Boden nicht)
    //   haze    Nebelschwaden im Raum (zur Kamera gedreht, blenden nahe der Kamera aus)
    //   rays    Lichtstrahlen: hohe, senkrechte Baender, die langsam auf- und abblenden
    //   {"kind": "...", "box": [x0, y0, z0, x1, y1, z1] (Welt), "n": 120, "color": [r, g, b, a], "size": [s0, s1],
    //    "life": [l0, l1], "speed": 0.1, "rise": 0.03, "near": 50}
    //   "tris": [x, y, z, ...] (Welt, je 9 Zahlen ein Dreieck): Teilchen entstehen auf diesen Flaechen (z. B. Bodennebel nur ueber
    //   echtem Boden), "lift": Abstand ueber der Flaeche (m), "jitter": Streuung (m)
    // Nur Quellen naeher als "near" an der Kamera laufen (beim Einschalten vorgewaermt -> sofort voll).
    public static class OotAtmos
    {
        class Src { public GameObject Go; public ParticleSystem Ps; public Bounds B; public float Near; public bool On; public string Kind; }
        static readonly List<Src> srcs = new List<Src>();
        static GameObject holder; static Pack forPack; static float tickT;
        static Material addMat, mistMat, rayMat;
        public static bool Enabled = true;

        public static void Setup(Pack pk)
        {
            forPack = pk;
            Clear();
            if (pk == null || pk.Meta == null) return;
            var list = Json.L(pk.Meta, "oot_atmos");
            if (list == null || list.Count == 0) return;
            holder = new GameObject("gi_oot_atmos"); UnityEngine.Object.DontDestroyOnLoad(holder);
            int made = 0;
            foreach (var e in list)
            {
                try { var s = Make(e); if (s != null) { srcs.Add(s); made++; } }
                catch (Exception ex) { Plugin.Quiet(ex, "OotAtmos.Make"); }
            }
            Plugin.Log("OoT-Stimmung: " + made + " Teilchenquellen");
        }

        static void Clear()
        {
            srcs.Clear();
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null;
        }

        public static void Tick()
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            if (srcs.Count == 0 || Time.time < tickT) return;
            tickT = Time.time + 0.4f;
            var cam = Camera.main; if (cam == null) return;
            var cp = cam.transform.position;
            foreach (var s in srcs)
            {
                if (s.Go == null) continue;
                bool want = Enabled && s.B.SqrDistance(cp) < s.Near * s.Near;
                if (want == s.On) continue;
                s.On = want;
                if (want) { s.Go.SetActive(true); s.Ps.Play(true); }
                else { s.Ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); s.Go.SetActive(false); }
            }
        }

        public static string Info()
        {
            int on = 0, live = 0; var kinds = new Dictionary<string, int>();
            foreach (var s in srcs) { if (s.On) { on++; live += s.Ps.particleCount; int c; kinds.TryGetValue(s.Kind, out c); kinds[s.Kind] = c + 1; } }
            var sb = new System.Text.StringBuilder("atmos " + srcs.Count + " quellen, " + on + " an, " + live + " teilchen");
            foreach (var kv in kinds) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.Append(" mat " + (mistMat != null ? mistMat.shader.name : "-") + " / " + (addMat != null ? addMat.shader.name : "-"));
            return sb.ToString();
        }

        // ---------- Bilder ----------
        static Texture2D Glow()
        {
            int n = 32; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "gi_atmos_glow" };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - r); a = a * a * a * 0.75f + Mathf.Clamp01(1f - r * 3.5f) * 0.6f;   // weicher Hof + heller Kern
                    a = Mathf.Clamp01(a); px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(); return t;
        }

        static Texture2D Cloud()
        {
            int n = 128; var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "gi_atmos_cloud" };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n, dx = u * 2f - 1f, dy = v * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float f = 0f, amp = 0.5f, fr = 3f;
                    for (int o = 0; o < 4; o++) { f += amp * Mathf.PerlinNoise(u * fr + 13.1f * o, v * fr + 7.7f * o); amp *= 0.5f; fr *= 2.1f; }
                    float fall = Mathf.Clamp01(1f - r); fall = fall * fall * (3f - 2f * fall);
                    float a = Mathf.Clamp01(fall * (0.35f + 1.1f * f) - 0.08f);
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            t.SetPixels(px); t.Apply(true); return t;
        }

        static Texture2D Ray()
        {
            int w = 32, h = 256; var t = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "gi_atmos_ray" };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, v = (y + 0.5f) / h;
                    float across = Mathf.Sin(Mathf.PI * u); across *= across;
                    float along = Mathf.Clamp01(v * 6f) * Mathf.Clamp01((1f - v) * 1.6f);   // v=1 oben: oben hell, nach unten auslaufend
                    along = Mathf.Pow(along, 0.8f) * (0.35f + 0.65f * v);
                    px[y * w + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(across * along));
                }
            t.SetPixels(px); t.Apply(true); return t;
        }

        // ---------- Materialien ----------
        static Shader FindSh(string name)
        {
            foreach (var sh in Resources.FindObjectsOfTypeAll<Shader>()) if (sh != null && sh.name == name) return sh;
            try { return Shader.Find(name); } catch { return null; }
        }

        // Particles/Standard Unlit: Fade (weich) oder Additiv, mit weichem Rand an Waenden und Ausblenden nahe der Kamera
        static Material StdParticle(Texture2D tex, bool additive, float camNear, float camFar)
        {
            var sh = FindSh("Particles/Standard Unlit");
            if (sh == null) return null;
            var m = new Material(sh) { name = additive ? "gi_atmos_add" : "gi_atmos_fade" };
            m.mainTexture = tex;
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.SetFloat("_Mode", additive ? 4f : 2f);
            m.SetFloat("_BlendOp", 0f);
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.DisableKeyword("_ALPHATEST_ON"); m.DisableKeyword("_ALPHAPREMULTIPLY_ON"); m.DisableKeyword("_ALPHAMODULATE_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.SetOverrideTag("RenderType", "Transparent");
            // weicher Rand, wo die Flaeche Boden/Wand schneidet
            m.SetFloat("_SoftParticlesEnabled", 1f); m.EnableKeyword("_SOFTPARTICLES_ON");
            m.SetVector("_SoftParticleFadeParams", new Vector4(0f, 1f / 1.2f, 0f, 0f));
            if (camFar > camNear)
            {
                m.SetFloat("_CameraFadingEnabled", 1f); m.EnableKeyword("_FADING_ON");
                m.SetVector("_CameraFadeParams", new Vector4(camNear, 1f / (camFar - camNear), 0f, 0f));
            }
            m.renderQueue = 3050;
            return m;
        }

        static Material LegacyFade(Texture2D tex)
        {
            var sh = FindSh("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null) return null;
            var m = new Material(sh) { name = "gi_atmos_fade_legacy" }; m.mainTexture = tex;
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (m.HasProperty("_InvFade")) m.SetFloat("_InvFade", 0.8f);
            m.renderQueue = 3050;
            return m;
        }

        static void Mats()
        {
            if (addMat == null) addMat = StdParticle(Glow(), true, 0.3f, 0.9f) ?? FireFx.Make(Glow());
            if (mistMat == null) mistMat = StdParticle(Cloud(), false, 1.5f, 5f) ?? LegacyFade(Cloud()) ?? FireFx.Make(Cloud());
            if (rayMat == null) rayMat = StdParticle(Ray(), true, 3f, 9f) ?? FireFx.Make(Ray());
        }

        // ---------- Quellen ----------
        static float[] Pair(object o, string k, float a, float b)
        {
            var l = Json.L(o, k);
            if (l != null && l.Count >= 2) return new[] { Convert.ToSingle(l[0]), Convert.ToSingle(l[1]) };
            if (l != null && l.Count == 1) return new[] { Convert.ToSingle(l[0]), Convert.ToSingle(l[0]) };
            return new[] { a, b };
        }

        static Src Make(object e)
        {
            var bx = Json.L(e, "box"); if (bx == null || bx.Count < 6) return null;
            var p0 = new Vector3(Convert.ToSingle(bx[0]), Convert.ToSingle(bx[1]), Convert.ToSingle(bx[2]));
            var p1 = new Vector3(Convert.ToSingle(bx[3]), Convert.ToSingle(bx[4]), Convert.ToSingle(bx[5]));
            var b = new Bounds((p0 + p1) * 0.5f, new Vector3(Mathf.Abs(p1.x - p0.x), Mathf.Abs(p1.y - p0.y), Mathf.Abs(p1.z - p0.z)));
            string kind = Json.S(e, "kind", "spores");
            int n = Mathf.Clamp((int)Json.F(e, "n", 60f), 1, 3000);
            var cl = Json.L(e, "color");
            var col = cl != null && cl.Count >= 4 ? new Color(Convert.ToSingle(cl[0]), Convert.ToSingle(cl[1]), Convert.ToSingle(cl[2]), Convert.ToSingle(cl[3])) : new Color(1f, 0.9f, 0.6f, 0.6f);
            Mats();

            var go = new GameObject("atmos_" + kind); go.transform.SetParent(holder.transform, false); go.transform.position = b.center;
            var ps = go.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var r = go.GetComponent<ParticleSystemRenderer>();
            var mn = ps.main; mn.loop = true; mn.duration = 10f; mn.prewarm = true; mn.maxParticles = n; mn.playOnAwake = false;
            mn.simulationSpace = ParticleSystemSimulationSpace.World; mn.scalingMode = ParticleSystemScalingMode.Hierarchy;
            mn.startColor = col;
            var life = Pair(e, "life", 5f, 9f); var size = Pair(e, "size", 0.03f, 0.07f);
            mn.startLifetime = new ParticleSystem.MinMaxCurve(life[0], life[1]);
            mn.startSize = new ParticleSystem.MinMaxCurve(size[0], size[1]);
            float speed = Json.F(e, "speed", 0.05f), rise = Json.F(e, "rise", 0f);
            mn.startSpeed = new ParticleSystem.MinMaxCurve(0f, speed);
            var em = ps.emission; em.rateOverTime = n / ((life[0] + life[1]) * 0.5f);
            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = b.size; sh.randomDirectionAmount = 1f;
            var tl = Json.L(e, "tris");
            if (tl != null && tl.Count >= 9)
            {
                // Flaechen als Netz (lokal zur Quelle), Normalen nach oben
                var vs = new List<Vector3>(); var ix = new List<int>();
                for (int i = 0; i + 8 < tl.Count; i += 9)
                {
                    var a0 = new Vector3(Convert.ToSingle(tl[i]), Convert.ToSingle(tl[i + 1]), Convert.ToSingle(tl[i + 2])) - b.center;
                    var a1 = new Vector3(Convert.ToSingle(tl[i + 3]), Convert.ToSingle(tl[i + 4]), Convert.ToSingle(tl[i + 5])) - b.center;
                    var a2 = new Vector3(Convert.ToSingle(tl[i + 6]), Convert.ToSingle(tl[i + 7]), Convert.ToSingle(tl[i + 8])) - b.center;
                    if (Vector3.Cross(a1 - a0, a2 - a0).y < 0f) { var t0 = a1; a1 = a2; a2 = t0; }
                    ix.Add(vs.Count); ix.Add(vs.Count + 1); ix.Add(vs.Count + 2); vs.Add(a0); vs.Add(a1); vs.Add(a2);
                }
                var mesh = new Mesh { name = "atmos_area" }; mesh.SetVertices(vs); mesh.SetTriangles(ix, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
                sh.shapeType = ParticleSystemShapeType.Mesh; sh.mesh = mesh; sh.meshShapeType = ParticleSystemMeshShapeType.Triangle;
                sh.scale = Vector3.one; sh.normalOffset = Json.F(e, "lift", 0.5f); sh.randomPositionAmount = Json.F(e, "jitter", 0.3f);
                sh.randomDirectionAmount = 1f;
            }
            var ca = ps.colorOverLifetime; ca.enabled = true;
            var gr = new Gradient();
            var white = new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) };
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            switch (kind)
            {
                case "flies":
                    gr.SetKeys(white, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0.1f, 0.3f), new GradientAlphaKey(1f, 0.45f),
                                              new GradientAlphaKey(0.15f, 0.62f), new GradientAlphaKey(1f, 0.8f), new GradientAlphaKey(0f, 1f) });
                    Noise(ps, Mathf.Max(0.2f, speed * 2f), 0.4f);
                    r.sharedMaterial = addMat; break;
                case "dust":
                    gr.SetKeys(white, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.25f), new GradientAlphaKey(0.6f, 0.5f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
                    Noise(ps, Mathf.Max(0.03f, speed), 0.2f);
                    r.sharedMaterial = addMat; break;
                case "mist":
                case "haze":
                    gr.SetKeys(white, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.3f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
                    mn.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                    var ro = ps.rotationOverLifetime; ro.enabled = true; ro.z = new ParticleSystem.MinMaxCurve(-0.08f, 0.08f);
                    Noise(ps, Mathf.Max(0.03f, speed), 0.08f);
                    if (kind == "mist") r.renderMode = ParticleSystemRenderMode.HorizontalBillboard;
                    r.sharedMaterial = mistMat; r.sortingFudge = 10f; break;
                case "rays":
                    gr.SetKeys(white, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.4f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
                    mn.startSpeed = 0f;
                    mn.startSize3D = true;
                    mn.startSizeX = new ParticleSystem.MinMaxCurve(size[0], size[1]);
                    mn.startSizeY = new ParticleSystem.MinMaxCurve(b.size.y * 0.9f, b.size.y);
                    mn.startSizeZ = 1f;
                    sh.scale = new Vector3(b.size.x, 0.01f, b.size.z);
                    r.renderMode = ParticleSystemRenderMode.VerticalBillboard;
                    r.sharedMaterial = rayMat; break;
                default:     // spores
                    gr.SetKeys(white, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0.35f, 0.45f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
                    Noise(ps, Mathf.Max(0.06f, speed * 1.5f), 0.25f);
                    r.sharedMaterial = addMat; break;
            }
            ca.color = gr;
            if (rise != 0f)
            {
                var vl = ps.velocityOverLifetime; vl.enabled = true; vl.space = ParticleSystemSimulationSpace.World;
                vl.x = new ParticleSystem.MinMaxCurve(0f, 0f); vl.y = new ParticleSystem.MinMaxCurve(rise * 0.5f, rise * 1.5f); vl.z = new ParticleSystem.MinMaxCurve(0f, 0f);
            }
            go.SetActive(false);
            return new Src { Go = go, Ps = ps, B = b, Near = Json.F(e, "near", 45f), Kind = kind };
        }

        static void Noise(ParticleSystem ps, float strength, float freq)
        {
            var nz = ps.noise; nz.enabled = true; nz.strength = strength; nz.frequency = freq; nz.scrollSpeed = 0.15f; nz.damping = true; nz.quality = ParticleSystemNoiseQuality.Medium;
        }
    }
}
