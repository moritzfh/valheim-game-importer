using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameImporter
{
    // Hexersinne (Taste V halten): Welt wird dunkler und entsaettigt mit Vignette, Gegner leuchten rot, Quest-Figuren und
    // das aktuelle Quest-Ziel orange (mit Entfernung) – wie im Original.
    public static class Senses
    {
        public static float K;               // 0..1, weich ein-/ausgeblendet
        public static bool Force;            // Fernsteuerung (Test)
        static Texture2D vignette, glow;
        static readonly List<KeyValuePair<Vector3, Color>> marks = new List<KeyValuePair<Vector3, Color>>();
        static float scanT;

        public static void Tick()
        {
            var p = Player.m_localPlayer;
            bool on = p != null && Pack.Current != null && Witcher.On && (Input.GetKey(KeyCode.V) && Horse.FreeInput() || W3Controls.SensesHeld || Force) && !StoryUi.Busy && !WitcherInv.Open && !FastTravel.Open;
            K = Mathf.MoveTowards(K, on ? 1f : 0f, Time.deltaTime * (on ? 2.5f : 4f));
            if (K <= 0f || Time.time < scanT) return;
            scanT = Time.time + 0.25f;
            marks.Clear();
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed()) continue;
                if ((c.transform.position - p.transform.position).sqrMagnitude > 45f * 45f) continue;
                marks.Add(new KeyValuePair<Vector3, Color>(c.transform.position + Vector3.up * Mathf.Max(1f, c.GetHeight() * 0.6f), new Color(1f, 0.15f, 0.08f)));
            }
            foreach (var sp in Interact.SensesSpots(p.transform.position, 35f)) marks.Add(new KeyValuePair<Vector3, Color>(sp, new Color(1f, 0.22f, 0.1f)));
            Vector3 t;
            if (Story.CurrentTarget(out t)) marks.Add(new KeyValuePair<Vector3, Color>(t, new Color(1f, 0.62f, 0.12f)));
        }

        static void Make()
        {
            const int n = 128;
            vignette = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            glow = new Texture2D(64, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    vignette.SetPixel(x, y, new Color(0.08f, 0.075f, 0.07f, Mathf.Lerp(0.22f, 0.9f, Mathf.SmoothStep(0.3f, 1.3f, d))));
                }
            for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    float dx = (x + 0.5f) / 64f * 2f - 1f, dy = (y + 0.5f) / 64f * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                    glow.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d)));
                }
            vignette.Apply(); glow.Apply();
        }

        public static void Gui()
        {
            if (K <= 0f || Player.m_localPlayer == null) return;
            if (vignette == null) Make();
            var cam = Camera.main; if (cam == null) return;
            GUI.color = new Color(1f, 1f, 1f, K);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), vignette, ScaleMode.StretchToFill);
            var me = Player.m_localPlayer.transform.position;
            foreach (var m in marks)
            {
                var sp = cam.WorldToScreenPoint(m.Key);
                if (sp.z <= 0.2f) continue;
                float dist = Vector3.Distance(me, m.Key);
                float r = Mathf.Clamp(260f / Mathf.Max(dist, 2f), 18f, 120f) * Screen.height / 1080f;
                GUI.color = new Color(m.Value.r, m.Value.g, m.Value.b, 0.85f * K);
                GUI.DrawTexture(new Rect(sp.x - r, Screen.height - sp.y - r, 2 * r, 2 * r), glow);
                if (m.Value.g > 0.5f)    // Quest-Ziel: Entfernung dazu
                    GUI.Label(new Rect(sp.x - 60, Screen.height - sp.y + r * 0.6f, 120, 24), "<color=#ffb04a><b>" + Mathf.RoundToInt(dist) + " m</b></color>");
            }
            GUI.color = Color.white;
        }
    }
}
