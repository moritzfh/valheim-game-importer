using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Feuer im Witcher-Stil: Flammen aus Witchers eigenen Feuer-Bildfolgen (fx/w3_*.png, aus den Spieldaten exportiert),
    // dazu Funken und flackerndes Licht. Einzelne Feuer an einem Punkt oder ganze Gebaeude: Flammen kommen dann aus der
    // Oberflaeche der Modelle (Waende, Dach), so dass das Haus wirklich brennt.
    //   {"pos": [x, y, z], "size": 2.5, "rate": 25}                       einzelnes Feuer
    //   {"burn": "<teil des Modellnamens>", "near": [x, y, z], "radius": 25, "size": 2, "rate": 80}   Gebaeude brennt
    public static class FireFx
    {
        static Material flameMat, sparkMat;
        static bool tried;

        static Texture2D Tex(string name)
        {
            var p = Path.Combine(Path.Combine(Remote.Dir, "fx"), name);
            if (!File.Exists(p)) return null;
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, true); t.LoadImage(File.ReadAllBytes(p));
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        // Additiver Partikel-Shader aus dem, was Valheim geladen hat (Witchers Feuerbilder haben schwarzen Grund -> additiv)
        static Shader FindShader()
        {
            var all = Resources.FindObjectsOfTypeAll<Shader>();
            var names = new List<string>();
            foreach (var sh in all) if (sh != null && sh.name.ToLowerInvariant().Contains("particle")) names.Add(sh.name);
            Plugin.Log("Feuer: Partikel-Shader da: " + string.Join(", ", names.ToArray()));
            foreach (var want in new[] { "Legacy Shaders/Particles/Additive", "Particles/Additive", "Mobile/Particles/Additive", "Legacy Shaders/Particles/Additive (Soft)",
                                         "Particles/Standard Unlit", "Legacy Shaders/Particles/Alpha Blended Premultiply" })
            {
                foreach (var sh in all) if (sh != null && sh.name == want) return sh;
                var f = Shader.Find(want); if (f != null) return f;
            }
            return null;
        }

        static Shader shader;
        internal static Material Make(Texture2D tex)
        {
            if (shader == null) shader = FindShader();
            Material mat;
            if (shader != null) mat = new Material(shader);
            else
            {
                // Notfall: Valheims Feuer-Material
                Material src = null;
                var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("fire_pit") : null;
                if (pf != null) foreach (var r in pf.GetComponentsInChildren<ParticleSystemRenderer>(true)) if (r.sharedMaterial != null) { src = r.sharedMaterial; break; }
                mat = src != null ? new Material(src) : new Material(Shader.Find("Sprites/Default"));
            }
            if (tex != null) mat.mainTexture = tex;
            if (mat.HasProperty("_TintColor")) mat.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            if (mat.shader.name == "Particles/Standard Unlit")
            {
                mat.SetFloat("_Mode", 4f); mat.SetFloat("_SrcBlend", 1f); mat.SetFloat("_DstBlend", 1f); mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_ALPHAPREMULTIPLY_ON"); mat.DisableKeyword("_ALPHABLEND_ON");
            }
            mat.renderQueue = 3100;
            Plugin.Log("Feuer: Material " + mat.shader.name + ", Bild " + (tex != null ? tex.width + "x" + tex.height : "keins"));
            return mat;
        }

        static void Init()
        {
            if (tried) return;
            tried = true;
            flameMat = Make(Tex("w3_fire_looped_8x4.png"));
            sparkMat = Make(Tex("w3_fire_embers.png"));
        }

        static ParticleSystem Flames(GameObject go, float size, float rate)
        {
            var ps = go.AddComponent<ParticleSystem>();
            var r = go.GetComponent<ParticleSystemRenderer>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 5f; main.loop = true; main.prewarm = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.3f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f * size, 0.9f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.9f * size, 1.6f * size);
            main.startRotation = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
            main.startColor = new Color(1f, 0.92f, 0.85f, 1f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.05f;
            main.maxParticles = 2000;
            var em = ps.emission; em.rateOverTime = rate;
            var ts = ps.textureSheetAnimation; ts.enabled = true; ts.numTilesX = 8; ts.numTilesY = 4;
            ts.animation = ParticleSystemAnimationType.WholeSheet; ts.cycleCount = 1;
            ts.startFrame = new ParticleSystem.MinMaxCurve(0f, 0.3f);
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.8f), 0f), new GradientColorKey(new Color(1f, 0.6f, 0.25f), 0.6f), new GradientColorKey(new Color(0.6f, 0.15f, 0.05f), 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.12f), new GradientAlphaKey(0.85f, 0.6f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var sz = ps.sizeOverLifetime; sz.enabled = true;
            sz.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.55f), new Keyframe(0.4f, 1f), new Keyframe(1f, 0.8f)));
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.25f * size; noise.frequency = 0.6f; noise.scrollSpeed = 0.4f;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = flameMat;
            r.sortingFudge = -2f;
            return ps;
        }

        static ParticleSystem Sparks(GameObject go, float size, float rate)
        {
            var ps = go.AddComponent<ParticleSystem>();
            var r = go.GetComponent<ParticleSystemRenderer>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 5f; main.loop = true; main.prewarm = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 2.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.0f * size, 2.2f * size);
            main.startSize = new ParticleSystem.MinMaxCurve(0.8f * size, 1.6f * size);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.28f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = -0.08f;
            main.maxParticles = 600;
            var em = ps.emission; em.rateOverTime = rate;
            var col = ps.colorOverLifetime; col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.8f, 0.5f), 0f), new GradientColorKey(new Color(1f, 0.4f, 0.1f), 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.15f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var noise = ps.noise; noise.enabled = true; noise.strength = 0.6f * size; noise.frequency = 0.5f;
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = sparkMat;
            return ps;
        }

        // einzelnes Feuer
        public static GameObject Spawn(Vector3 pos, float size, float rate, bool light = true)
        {
            Init();
            var root = new GameObject("gi_fire");
            root.transform.position = pos;
            var fl = new GameObject("flames"); fl.transform.SetParent(root.transform, false);
            var ps = Flames(fl, size, rate);
            var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.35f * size; sh.rotation = new Vector3(-90f, 0f, 0f);
            var sp = new GameObject("sparks"); sp.transform.SetParent(root.transform, false); sp.transform.localPosition = Vector3.up * size;
            var ps2 = Sparks(sp, size * 0.6f, Mathf.Max(1f, rate * 0.08f));
            var sh2 = ps2.shape; sh2.enabled = true; sh2.shapeType = ParticleSystemShapeType.Sphere; sh2.radius = 0.5f * size;
            if (light) AddLight(root, Vector3.up * size, size * 6f, 1.6f);
            ps.Play(); ps2.Play();
            return root;
        }

        // ganzes Gebaeude: Flammen aus der Oberflaeche aller passenden Modelle in der Naehe
        public static GameObject Burn(string part, Vector3 near, float radius, float size, float rate)
        {
            Init();
            var root = new GameObject("gi_fire_burn_" + part);
            var rends = new List<MeshRenderer>();
            foreach (var kv in Statics.LiveModels())
            {
                var go = kv.Value;
                if (go == null || kv.Key == null || !kv.Key.ToLowerInvariant().Contains(part.ToLowerInvariant())) continue;
                if ((go.transform.position - near).sqrMagnitude > radius * radius) continue;
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>(true)) if (r.enabled) rends.Add(r);
            }
            float area = 0f; foreach (var r in rends) { var b = r.bounds.size; area += b.x * b.y + b.y * b.z + b.x * b.z; }
            int n = 0;
            foreach (var r in rends)
            {
                var mf = r.GetComponent<MeshFilter>();
                var b = r.bounds.size; float share = area > 0f ? (b.x * b.y + b.y * b.z + b.x * b.z) / area : 1f / rends.Count;
                var go = new GameObject("flames"); go.transform.SetParent(root.transform, false);
                var ps = Flames(go, size, Mathf.Max(2f, rate * share));
                var sh = ps.shape; sh.enabled = true;
                if (mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable)
                {
                    sh.shapeType = ParticleSystemShapeType.MeshRenderer; sh.meshRenderer = r; sh.meshShapeType = ParticleSystemMeshShapeType.Triangle;
                    sh.normalOffset = 0.15f;
                }
                else
                {
                    go.transform.position = r.bounds.center; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = r.bounds.size;
                    sh.boxThickness = Vector3.one * 0.1f;
                }
                ps.Play(); n++;
            }
            // Funken und Licht ueber dem Ganzen
            var all = new Bounds(near, Vector3.zero); bool first = true;
            foreach (var r in rends) { if (first) { all = r.bounds; first = false; } else all.Encapsulate(r.bounds); }
            if (!first)
            {
                var sp = new GameObject("sparks"); sp.transform.SetParent(root.transform, false); sp.transform.position = all.center + Vector3.up * all.extents.y * 0.6f;
                var ps2 = Sparks(sp, size, Mathf.Max(3f, rate * 0.06f));
                var sh2 = ps2.shape; sh2.enabled = true; sh2.shapeType = ParticleSystemShapeType.Box; sh2.scale = new Vector3(all.size.x, 1f, all.size.z);
                ps2.Play();
                AddLight(root, all.center - root.transform.position, Mathf.Max(all.size.x, all.size.z) * 1.2f, 2.4f);
            }
            Plugin.Log("Feuer: " + part + " brennt (" + n + " Teile aus " + rends.Count + " Modellteilen)");
            return root;
        }

        static void AddLight(GameObject root, Vector3 local, float range, float intensity)
        {
            var lg = new GameObject("light"); lg.transform.SetParent(root.transform, false); lg.transform.localPosition = local;
            var li = lg.AddComponent<Light>(); li.type = LightType.Point; li.color = new Color(1f, 0.5f, 0.18f); li.range = range; li.intensity = intensity;
            li.shadows = LightShadows.None;
            lg.AddComponent<FireFlicker>().Base = intensity;
        }
    }

    public class FireFlicker : MonoBehaviour
    {
        public float Base = 1.5f;
        Light l; float seed;
        void Awake() { l = GetComponent<Light>(); seed = UnityEngine.Random.value * 100f; }
        void Update()
        {
            if (l == null) return;
            float t = Time.time * 7f + seed;
            l.intensity = Base * (0.8f + 0.25f * Mathf.PerlinNoise(t, seed) + 0.1f * Mathf.Sin(t * 2.3f));
        }
    }
}
