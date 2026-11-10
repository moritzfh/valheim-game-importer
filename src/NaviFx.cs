using UnityEngine;

namespace GameImporter
{
    // Fee (Navi) fuer Cutscene-Requisiten: Prop mit "prefab": "@navi" (optional "color": [r,g,b], "glow": Groesse in m).
    // Leuchtkugel (Billboard, additiv), zwei Fluegelpaare die schlagen, Punktlicht und ein leichter Funkenschweif.
    public class NaviFx : MonoBehaviour
    {
        static Texture2D glowTex, wingTex;
        static Material glowMat, wingMat, coreMat;
        static Mesh quadMesh;
        Transform glow, core, wings;
        Transform[] wing = new Transform[4];
        Light lt;
        float seed;
        public Color Col = new Color(0.78f, 0.88f, 1f);
        public float Glow = 0.8f;

        static Texture2D Radial(int n, float pow)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)); a = Mathf.Pow(a, pow);
                    px[y * n + x] = new Color(a, a, a, a);
                }
            t.SetPixels(px); t.Apply(); return t;
        }

        static Texture2D Wing(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h;          // v: 0 = Ansatz, 1 = Spitze
                    float r = 0.5f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, v * 1.05f));  // Tropfenform
                    float d = Mathf.Abs(u) / Mathf.Max(0.01f, r);
                    float a = d < 1f ? (0.35f + 0.65f * Mathf.Pow(d, 3f)) * (1f - 0.5f * v) : 0f;   // heller Rand
                    float vein = Mathf.Abs(Mathf.Sin(v * 18f)) < 0.12f && d < 1f ? 0.25f : 0f;
                    a = Mathf.Clamp01(a + vein);
                    px[y * w + x] = new Color(a, a, a, a);
                }
            t.SetPixels(px); t.Apply(); return t;
        }

        static void Init()
        {
            if (glowMat != null) return;
            glowTex = Radial(64, 1.6f); wingTex = Wing(32, 64);
            glowMat = FireFx.Make(glowTex); wingMat = FireFx.Make(wingTex); coreMat = new Material(Shader.Find("Sprites/Default")); coreMat.mainTexture = Radial(64, 0.6f); coreMat.color = Color.white; coreMat.renderQueue = 3100;
        }

        static Transform Quad(Transform parent, Material m, string name)
        {
            // eigenes Quad MIT Vertexfarben: die Partikel-Shader multiplizieren damit (Unity-Quad hat keine -> unsichtbar)
            if (quadMesh == null)
            {
                quadMesh = new Mesh();
                quadMesh.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
                quadMesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
                quadMesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
                quadMesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
                quadMesh.RecalculateBounds();
            }
            var q = new GameObject(name);
            q.transform.SetParent(parent, false);
            q.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var r = q.AddComponent<MeshRenderer>(); r.sharedMaterial = m;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return q.transform;
        }

        public static GameObject Spawn(Color col, float glowSize)
        {
            Init();
            var go = new GameObject("gi_navi");
            var n = go.AddComponent<NaviFx>(); n.Col = col; n.Glow = glowSize; n.seed = Random.value * 10f;
            n.Build();
            return go;
        }

        void Build()
        {
            glow = Quad(transform, glowMat, "glow"); glow.localScale = Vector3.one * Glow;
            core = Quad(transform, glowMat, "core"); core.localScale = Vector3.one * Glow * 0.22f;
            // Legacy Additive: Farbe = 2 * Tint * Bild (Alpha zaehlt mit) -> kraeftig setzen, sonst verschwindet die Fee im Tageslicht
            glow.GetComponent<Renderer>().material.SetColor("_TintColor", new Color(Col.r * 0.42f, Col.g * 0.46f, Col.b * 0.55f, 0.75f));
            core.GetComponent<Renderer>().material = coreMat;

            wings = new GameObject("wings").transform; wings.SetParent(transform, false);
            for (int i = 0; i < 4; i++)
            {
                var piv = new GameObject("wingpivot" + i).transform; piv.SetParent(wings, false);
                var q = Quad(piv, wingMat, "wing" + i);
                float len = Glow * (i < 2 ? 0.48f : 0.32f);
                q.localScale = new Vector3(len * 0.42f, len, 1f); q.localPosition = new Vector3(0f, len * 0.5f, 0f);
                var r = q.GetComponent<Renderer>(); r.material.SetColor("_TintColor", new Color(0.55f, 0.6f, 0.75f, 0.8f));
                wing[i] = piv;
            }
            // zusaetzlich als Partikel (Billboard wie beim Feuer, das sicher gezeichnet wird)
            var pg = new GameObject("glowps"); pg.transform.SetParent(transform, false);
            var ps = pg.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var mn = ps.main; mn.loop = true; mn.duration = 1f; mn.startLifetime = 0.25f; mn.startSpeed = 0f; mn.maxParticles = 8;
            mn.startSize = Glow * 0.5f; mn.startColor = new Color(Col.r, Col.g, Col.b, 0.35f); mn.simulationSpace = ParticleSystemSimulationSpace.Local; mn.playOnAwake = true;
            var em = ps.emission; em.rateOverTime = 24f;
            var sh = ps.shape; sh.enabled = false;
            var pr = pg.GetComponent<ParticleSystemRenderer>(); pr.sharedMaterial = glowMat; pr.renderMode = ParticleSystemRenderMode.Billboard;
            ps.Play();
            var lg = new GameObject("light"); lg.transform.SetParent(transform, false);
            lt = lg.AddComponent<Light>(); lt.type = LightType.Point; lt.color = Col; lt.range = 3.5f; lt.intensity = 1.6f; lt.shadows = LightShadows.None;
        }

        int dbg;
        void LateUpdate()
        {
            var cam = Camera.main;
            if (dbg++ % 200 == 40 && cam != null)
                Plugin.Log("Navi: pos " + transform.position + " aktiv " + gameObject.activeInHierarchy + " cam " + (cam != null ? cam.name + " " + cam.transform.position + " mask " + cam.cullingMask : "keine")
                           + " vp " + cam.WorldToViewportPoint(transform.position) + " sichtbar " + glow.GetComponent<Renderer>().isVisible + "/" + core.GetComponent<Renderer>().isVisible + " tint " + glow.GetComponent<Renderer>().material.GetColor("_TintColor") + " tex " + (glow.GetComponent<Renderer>().material.mainTexture != null) + " fog " + RenderSettings.fog + " " + RenderSettings.fogMode + " " + RenderSettings.fogDensity + " shader " + glow.GetComponent<Renderer>().sharedMaterial.shader.name + " layer " + gameObject.layer);
            if (cam == null) return;
            float t = Time.time + seed;
            // Billboard zur Kamera, leichtes Pulsieren wie im Original
            var rot = Quaternion.LookRotation(transform.position - cam.transform.position, cam.transform.up);
            float pulse = 1f + 0.08f * Mathf.Sin(t * 9f);
            glow.rotation = rot; glow.localScale = Vector3.one * Glow * 1.1f * pulse;
            core.rotation = rot;
            wings.rotation = rot;
            float flap = Mathf.Sin(t * 38f) * 0.45f;   // schnelles Flattern
            float[] baseAng = { 20f, -20f, 150f, -150f };
            for (int i = 0; i < 4; i++)
            {
                float s = i % 2 == 0 ? 1f : -1f;
                wing[i].localRotation = Quaternion.Euler(0f, s * flap * 50f, baseAng[i] + s * flap * 8f);
            }
            if (lt != null) lt.intensity = 1.6f * pulse;
        }
    }
}
