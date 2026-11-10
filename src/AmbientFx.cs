using UnityEngine;

namespace GameImporter
{
    // Stimmung fuer Cutscenes ("env": {"motes": 1.0}): Gluehwuermchen und Schwebeteilchen um die Kamera
    // (wie im Kokiri-Wald), additiv, flackernd, langsam treibend. Folgt der Kamera, Teilchen bleiben in der Welt.
    public class AmbientFx : MonoBehaviour
    {
        static Material mat;
        ParticleSystem flies, motes;

        static Texture2D Dot()
        {
            int n = 32; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)); a = a * a * a;
                    px[y * n + x] = new Color(a, a, a, a);
                }
            t.SetPixels(px); t.Apply(); return t;
        }

        public static GameObject Spawn(float amount) { return Spawn(amount, amount, null); }
        // Spielgebiete (OotPlay): Gluehwuermchen und Schwebeteilchen getrennt, Teilchenfarbe je Raum
        public static GameObject Spawn(float flies, float motes, Color? moteCol)
        {
            if (mat == null) mat = FireFx.Make(Dot());
            var go = new GameObject("gi_ambient");
            var a = go.AddComponent<AmbientFx>();
            if (flies > 0f) a.flies = a.Make("fireflies", (int)(90 * flies), new Color(0.85f, 1f, 0.45f, 1f), 0.07f, 0.13f, 0.25f, 3f, 6f, 9f);
            if (motes > 0f) a.motes = a.Make("motes", (int)(160 * motes), moteCol ?? new Color(1f, 0.95f, 0.75f, 0.45f), 0.025f, 0.05f, 0.08f, 6f, 10f, 11f);
            return go;
        }

        ParticleSystem Make(string name, int max, Color col, float s0, float s1, float speed, float life0, float life1, float box)
        {
            var g = new GameObject(name); g.transform.SetParent(transform, false);
            var ps = g.AddComponent<ParticleSystem>(); ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var mn = ps.main; mn.loop = true; mn.duration = 5f; mn.prewarm = true; mn.maxParticles = Mathf.Max(8, max);
            mn.startLifetime = new ParticleSystem.MinMaxCurve(life0, life1); mn.startSpeed = new ParticleSystem.MinMaxCurve(0f, speed);
            mn.startSize = new ParticleSystem.MinMaxCurve(s0, s1); mn.startColor = col; mn.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission; em.rateOverTime = max / ((life0 + life1) * 0.5f);
            var sh = ps.shape; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(box * 2f, box * 0.7f, box * 2f);
            var nz = ps.noise; nz.enabled = true; nz.strength = speed * 1.5f; nz.frequency = 0.3f; nz.scrollSpeed = 0.2f;
            var ca = ps.colorOverLifetime; ca.enabled = true;
            var gr = new Gradient();
            gr.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                       new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.2f), new GradientAlphaKey(0.3f, 0.45f), new GradientAlphaKey(1f, 0.65f), new GradientAlphaKey(0f, 1f) });
            ca.color = gr;
            var r = g.GetComponent<ParticleSystemRenderer>(); r.sharedMaterial = mat; r.renderMode = ParticleSystemRenderMode.Billboard;
            ps.Play();
            return ps;
        }

        void LateUpdate()
        {
            var cam = Camera.main; if (cam == null) return;
            transform.position = cam.transform.position + cam.transform.forward * 4f;
        }
    }
}
