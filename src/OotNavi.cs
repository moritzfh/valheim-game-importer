using UnityEngine;

namespace GameImporter
{
    // Navi (OoT): kleine leuchtende Fee mit vier Fluegeln, schwirrt neben Links Schulter, traegt das Licht (OotPlay.naviLight).
    // Leuchtkugel + Schein = Billboards (additiv), Fluegel = flache, schnell schlagende Flaechen.
    public static class OotNavi
    {
        static GameObject root, core, halo;
        static readonly Transform[] wings = new Transform[4];
        static Material coreMat, haloMat, wingMat;
        static Vector3 pos, vel;
        public static Vector3? Override;          // Ablauf (Aufwach-Szene): Navi genau hier
        static float phase, dart, dartT;
        public static Transform T { get { return root != null ? root.transform : null; } }

        static Texture2D Radial(int n, float pow)
        {
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)); a = Mathf.Pow(a, pow);
                    byte v = (byte)(255 * a); px[y * n + x] = new Color32(v, v, v, v);
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        static Texture2D WingTex()
        {
            const int w = 32, h = 64;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w * 2f - 1f, v = (y + 0.5f) / h;          // Tropfenform, Wurzel unten
                    float rad = Mathf.Sin(v * Mathf.PI) * (0.35f + 0.65f * v);
                    float d = Mathf.Abs(u) / Mathf.Max(0.01f, rad);
                    float a = d < 1f ? (1f - d) * 0.8f + 0.2f * (1f - v) : 0f;
                    byte c = (byte)(255 * Mathf.Clamp01(a));
                    px[y * w + x] = new Color32(c, c, c, c);
                }
            t.SetPixels32(px); t.Apply();
            return t;
        }

        static GameObject Quad(string name, Transform parent, Material m, float size)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Quad);
            g.name = name; Object.Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false); g.transform.localScale = Vector3.one * size;
            var r = g.GetComponent<MeshRenderer>(); r.sharedMaterial = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            return g;
        }

        public static void Ensure(Player owner)
        {
            if (root != null || owner == null) return;
            root = new GameObject("gi_navi");
            coreMat = FireFx.Make(Radial(64, 2.2f)); Tint(coreMat, new Color(1f, 1f, 1f, 1f));
            haloMat = FireFx.Make(Radial(64, 1.6f)); Tint(haloMat, new Color(0.55f, 0.75f, 1f, 0.55f));
            wingMat = FireFx.Make(WingTex()); Tint(wingMat, new Color(0.75f, 0.9f, 1f, 0.45f));
            halo = Quad("halo", root.transform, haloMat, 0.55f);
            core = Quad("core", root.transform, coreMat, 0.16f);
            var wr = new GameObject("wings"); wr.transform.SetParent(root.transform, false);
            for (int i = 0; i < 4; i++)
            {
                var pivot = new GameObject("w" + i).transform; pivot.SetParent(wr.transform, false);
                var q = Quad("q", pivot, wingMat, 1f);
                q.transform.localScale = new Vector3(i < 2 ? 0.1f : 0.075f, i < 2 ? 0.17f : 0.12f, 1f);
                q.transform.localPosition = new Vector3(0f, (i < 2 ? 0.17f : 0.12f) * 0.5f, 0f);
                wings[i] = pivot;
            }
            pos = owner.transform.position + Vector3.up * 1.4f; vel = Vector3.zero;
        }

        static void Tint(Material m, Color c)
        {
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static void Destroy()
        {
            if (root != null) Object.Destroy(root);
            root = null;
        }

        // jedes Bild: Ziel neben Links Schulter, weich hinterher, leicht auf und ab; manchmal kurzer Ausflug wie in OoT
        public static void Tick(Player owner, float dark)
        {
            if (owner == null) return;
            Ensure(owner);
            float dt = Time.deltaTime; phase += dt;
            var t = owner.transform;
            if (Time.time > dartT) { dartT = Time.time + Random.Range(4f, 9f); dart = Random.Range(-1f, 1f); }
            var side = t.right * (0.55f + 0.25f * Mathf.Sin(phase * 0.7f)) * (dart >= 0f ? 1f : -1f);
            var target = t.position + Vector3.up * (1.25f + 0.12f * Mathf.Sin(phase * 2.3f)) + side - t.forward * 0.25f
                         + new Vector3(Mathf.Sin(phase * 1.3f), 0f, Mathf.Cos(phase * 1.1f)) * 0.12f;
            if ((pos - target).sqrMagnitude > 25f) pos = target;                                      // nach Tuer/Warp gleich mit
            pos = Vector3.SmoothDamp(pos, target, ref vel, 0.28f, 12f, dt);
            if (Override.HasValue) { pos = Override.Value; vel = Vector3.zero; }
            root.transform.position = pos;
            float flap = Mathf.Sin(phase * 38f) * 35f;
            for (int i = 0; i < 4; i++)
            {
                if (wings[i] == null) continue;
                float side_ = (i % 2 == 0) ? 1f : -1f, up = i < 2 ? 1f : -1f;
                wings[i].localRotation = Quaternion.Euler(0f, 0f, side_ * (i < 2 ? 35f : 140f)) * Quaternion.Euler(flap * up, 0f, 0f);
            }
            // tags heller Kern, nachts staerkerer Schein
            Tint(haloMat, new Color(0.55f, 0.75f, 1f, 0.35f + 0.35f * dark));
        }

        // nach der Kamera: Kugel/Schein zur Kamera drehen
        public static void Face(Camera c)
        {
            if (root == null || c == null) return;
            var rot = Quaternion.LookRotation(root.transform.position - c.transform.position, c.transform.up);
            core.transform.rotation = rot; halo.transform.rotation = rot;
            var wr = root.transform.Find("wings");
            if (wr != null) wr.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(root.transform.position - c.transform.position, Vector3.up).normalized + Vector3.up * 0.0001f, Vector3.up);
        }
    }
}
