using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Kampf wie in Ocarina of Time: Herzen (0x10 = 1 Herz), Treffer mit Rueckstoss und Unverwundbarkeit, Deku-Schild,
    /// Z-Anvisieren (z_actor.c Attention: Reichweiten, Farben je Kategorie, Fadenkreuz aus 4 Dreiecken, das einzoomt und kreist).
    /// Gegner/Figuren melden sich mit Add() an; OotPlay fragt Lock/Hover ab und ruft Tick/Damage.
    /// </summary>
    public static class OotCombat
    {
        public class Target
        {
            public Transform T;                 // Wurzel des Ziels
            public float Focus = 0.9f;          // Hoehe des Blickpunkts (actor->focus) ueber T
            public float Arrow = 0.5f;          // Pfeil/Navi ueber dem Blickpunkt
            public bool Enemy;                  // ACTORCAT_ENEMY (gelb) sonst NPC (hellblau)
            public float Range = 9.24f, Leash = 17.3f;   // ATTENTION_RANGE_3: 350 / 525 Einheiten
            public Func<bool> Alive;
        }
        // Reichweiten aus sAttentionRanges (Einheiten * 0,033 m)
        public static float RangeM(int type, out float leash)
        {
            float[] r = { 70, 170, 280, 350, 700, 1000, 100, 140, 240, 280 };
            float[] l = { 140, 255, 5600, 525, 1050, 1500, 105.37f, 163.33f, 576, 280000 };
            type = Mathf.Clamp(type, 0, 9); leash = Mathf.Min(l[type], 2000f) * 0.033f; return r[type] * 0.033f;
        }

        static readonly List<Target> targets = new List<Target>();
        public static void Add(Target t) { if (t != null && !targets.Contains(t)) targets.Add(t); }
        public static void Remove(Target t) { targets.Remove(t); if (Lock == t) Lock = null; if (Hover == t) Hover = null; }
        public static Target Lock, Hover;
        public static bool Hostile { get { return Lock != null && Lock.Enemy; } }

        // Leben (gSaveContext health): 3 Herzen zu Beginn
        public static int MaxHp = 0x30, Hp = 0x30;
        public static float Invuln;
        public static bool Dead { get { return Hp <= 0; } }
        public static Action<Vector3, int> OnHurt;     // Richtung, Staerke (OotPlay: Rueckstoss + Animation)
        public static Action OnDie;
        public static Action OnBlock;
        public static Func<Vector3, bool> ShieldFaces;  // OotPlay: haelt Link gerade den Schild in diese Richtung?
        static float beat; static int beatDir;

        public static void Reset() { Hp = MaxHp; Invuln = 0f; Lock = null; }

        public static bool Valid(Target t)
        {
            return t != null && t.T != null && t.T.gameObject.activeInHierarchy && (t.Alive == null || t.Alive());
        }
        public static Vector3 FocusPos(Target t) { return t.T.position + Vector3.up * t.Focus; }

        // Wie Attention_FindActor: das naechste Ziel in Reichweite vor Link (Winkelabstand zur Blickrichtung zaehlt mit)
        static Target Best(Vector3 me, Vector3 fw)
        {
            Target best = null; float bs = float.MaxValue;
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                var t = targets[i];
                if (t == null || t.T == null) { targets.RemoveAt(i); continue; }
                if (!Valid(t)) continue;
                var dv = FocusPos(t) - (me + Vector3.up * 1f);
                float d = dv.magnitude; if (d > t.Range) continue;
                var h = new Vector3(dv.x, 0f, dv.z);
                float ang = h.sqrMagnitude > 0.0001f ? Vector3.Angle(fw, h) : 0f;
                if (ang > 75f) continue;
                float score = d * (1f + ang / 60f);
                if (score < bs) { bs = score; best = t; }
            }
            return best;
        }

        static float reticleR = 500f; static int spin; static float frameAcc;
        static Vector3 reticleWorld; static bool reticleEnemy; static float reticleFade;

        /// <summary>Jeden Frame aus OotPlay.</summary>
        public static void Tick(Player p, float linkYaw, bool zHeld, bool zDown)
        {
            var me = p.transform.position;
            var fw = Quaternion.Euler(0f, linkYaw, 0f) * Vector3.forward;
            Hover = Lock ?? Best(me, fw);
            if (zDown && Lock == null && Hover != null)
            {
                Lock = Hover; reticleR = 500f; spin = 0;
                OotSfx.Play(Lock.Enemy ? "NA_SE_SY_LOCK_ON" : "NA_SE_SY_LOCK_ON_HUMAN", 0.7f);
            }
            if (Lock != null)
            {
                bool ok = zHeld && Valid(Lock) && (FocusPos(Lock) - me).magnitude < Lock.Leash;
                if (!ok) { Lock = null; OotSfx.Play("NA_SE_SY_LOCK_OFF", 0.5f); }
            }
            // Fadenkreuz im 20-Bilder-Takt wie das Original
            frameAcc += Time.deltaTime * 20f;
            while (frameAcc >= 1f)
            {
                frameAcc -= 1f;
                if (Lock != null)
                {
                    if (spin == 0)
                    {
                        float step = Mathf.Clamp((500f - reticleR) * 3f, 30f, 100f);
                        reticleR = Mathf.MoveTowards(reticleR, 80f, step);
                        if (reticleR <= 80f) spin = 1;
                    }
                    else { spin = (spin + 3) | 0x80; reticleR = 120f; }
                }
                else reticleR = Mathf.MoveTowards(reticleR, 500f, 80f);
                // schlagendes letztes Herz + Warnton (Health_UpdateBeatingHeart)
                if (beatDir != 0) { beat -= 1f; if (beat <= 0f) { beat = 0f; beatDir = 0; if (Hp > 0 && Hp <= 0x10 && OotPlay.Driving) OotSfx.Play("NA_SE_SY_HITPOINT_ALARM", 0.55f); } }
                else { beat += 1f; if (beat >= 10f) { beat = 10f; beatDir = 1; } }
            }
            if (Lock != null) { reticleWorld = FocusPos(Lock); reticleEnemy = Lock.Enemy; reticleFade = 1f; }
            else reticleFade = Mathf.Max(0f, reticleFade - Time.deltaTime * 6f);
            if (Invuln > 0f) Invuln -= Time.deltaTime;
        }

        /// <summary>Link wird getroffen. amount in 1/16 Herz, from = Ort des Angreifers. false = nichts passiert (Schild/unverwundbar).</summary>
        // Wegstossen ohne Schaden (Actor_SetPlayerKnockbackLargeNoDamage, z. B. Stachelstamm): Richtung fest vom Objekt
        public static Action<Vector3, Vector3> OnKnock;
        public static void Knockback(Vector3 from, Vector3 dir, float speed, float yVel)
        {
            if (Hp <= 0 || Invuln > 0f || !OotPlay.On) return;
            Invuln = 0.6f;
            OotSfx.Play("NA_SE_PL_BODY_HIT", 0.8f);
            if (OnKnock != null) OnKnock(from, dir);
        }

        public static string LastHit = "-";       // Fernsteuerung "oot hp": wer zuletzt getroffen hat
        public static bool Damage(int amount, Vector3 from, bool unblockable = false)
        {
            if (Hp <= 0 || Invuln > 0f || !OotPlay.On || OotDev.God) return false;
            if (!unblockable && ShieldFaces != null && ShieldFaces(from))
            {
                Invuln = 0.35f; OotSfx.Play("NA_SE_IT_SHIELD_BOUND", 0.8f);
                if (OnBlock != null) OnBlock();
                return false;
            }
            Hp = Mathf.Max(0, Hp - amount);
            Invuln = 1.2f;
            try { var m = new System.Diagnostics.StackTrace(1, false).GetFrame(0).GetMethod(); LastHit = m.DeclaringType.Name + "." + m.Name + " " + amount + " von " + from.ToString("F1"); } catch { }
            OotSfx.Play("NA_SE_VO_LI_DAMAGE_S_KID", 1f);
            OotSfx.Play("NA_SE_PL_DAMAGE", 0.7f);
            if (Hp <= 0)
            {
                OotSfx.Play("NA_SE_VO_LI_DOWN_KID", 1f);
                if (OnDie != null) OnDie();
            }
            else if (OnHurt != null) OnHurt(from, amount);
            return true;
        }

        /// <summary>Schwert-Treffer: Gegner melden eine Pruef-Funktion an (Link-Position, Blickrichtung) -> getroffen?</summary>
        public static readonly List<Func<Vector3, float, bool>> Hittables = new List<Func<Vector3, float, bool>>();
        public static bool SwordHit(Vector3 pos, float yaw)
        {
            bool any = false;
            foreach (var h in Hittables.ToArray())
            {
                try { if (h(pos, yaw)) any = true; } catch (Exception e) { Plugin.Quiet(e, "OotCombat.Hit"); }
            }
            return any;
        }

        /// <summary>Schleuder: Ziele melden (Strahl, Reichweite) -> Abstand des Treffers oder -1, und was beim Treffer passiert.</summary>
        public class Shootable { public Func<Ray, float, float> Test; public Action<Vector3, Vector3> Hit; }
        public static readonly List<Shootable> Shootables = new List<Shootable>();
        public static void Heal(int amount)
        {
            if (Hp <= 0) return;
            Hp = Mathf.Min(MaxHp, Hp + amount);
        }

        /// <summary>Herz-Fuellung fuer das HUD: Viertel je Herz (0..4) und ob das Herz gerade schlaegt.</summary>
        public static int Quarters(int heart)
        {
            int q = Mathf.CeilToInt(Hp / 4f);
            return Mathf.Clamp(q - heart * 4, 0, 4);
        }
        public static float BeatScale { get { return 1f + beat * 0.017f; } }   // letztes volles Herz pulsiert
        public static int Hearts { get { return Mathf.Max(1, MaxHp / 0x10); } }

        // ---------- Zeichnen (OnGUI aus OotHud) ----------
        static Texture2D tri;
        static Texture2D Tri()
        {
            if (tri != null) return tri;
            const int N = 32;
            tri = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    // Spitze unten (zeigt nach unten), weicher Rand
                    float fy = (y + 0.5f) / N;               // 0 unten .. 1 oben
                    float half = fy * 0.5f;                  // halbe Breite waechst nach oben
                    float dx = Mathf.Abs((x + 0.5f) / N - 0.5f);
                    float a = Mathf.Clamp01((half - dx) * N * 0.9f) * Mathf.Clamp01((1f - fy) * N * 0.9f + 0.3f);
                    tri.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tri.Apply();
            return tri;
        }

        public static void Gui(float k)
        {
            if (Event.current.type != EventType.Repaint) return;
            var cam = Camera.main; if (cam == null) return;
            var t = Tri();
            var old = GUI.matrix; var oc = GUI.color;
            // Pfeil ueber dem Ziel, das Navi gerade anzeigt (gZTargetArrowDL, dreht sich)
            var hv = Lock ?? Hover;
            if (hv != null && Valid(hv) && OotPlay.Driving)
            {
                var c = hv.Enemy ? new Color(1f, 1f, 0f) : new Color(150f / 255f, 150f / 255f, 1f);
                var wp = FocusPos(hv) + Vector3.up * (hv.Arrow + 0.08f * Mathf.Sin(Time.time * 4f));
                var sp = cam.WorldToScreenPoint(wp);
                if (sp.z > 0.1f)
                {
                    float s = Mathf.Clamp(260f / sp.z, 9f, 26f) * k / 3f;
                    float w = s * Mathf.Abs(Mathf.Cos(Time.time * 3.2f)) + s * 0.12f;
                    var r = new Rect(sp.x - w / 2f, Screen.height - sp.y - s, w, s);
                    GUI.color = new Color(c.r, c.g, c.b, 0.95f);
                    GUI.DrawTexture(r, t);
                }
            }
            // Fadenkreuz beim Anvisieren: 4 Dreiecke, die von aussen einzoomen und dann kreisen
            if (reticleFade > 0.01f && reticleR < 500f)
            {
                var sp = cam.WorldToScreenPoint(reticleWorld);
                if (sp.z > 0.1f)
                {
                    var c = reticleEnemy ? new Color(1f, 1f, 0f) : new Color(150f / 255f, 150f / 255f, 1f);
                    var center = new Vector2(sp.x, Screen.height - sp.y);
                    float px = Screen.height / 240f;
                    float rad = reticleR * 0.15f * px * 0.5f + 6f * px;         // Radius in 320x240-Punkten (0,15 Skalierung)
                    float sx = (reticleR <= 120f ? 0.15f : (reticleR - 120f) * 0.001f + 0.15f) / 0.15f;
                    float size = 10f * px;
                    float rot = spin != 0 ? (spin & 0x7F) * (180f / 64f) : 0f;
                    GUI.color = new Color(c.r, c.g, c.b, reticleFade);
                    for (int i = 0; i < 4; i++)
                    {
                        float a = rot + 45f + i * 90f;
                        var dir = new Vector2(Mathf.Cos(a * Mathf.Deg2Rad) * sx, Mathf.Sin(a * Mathf.Deg2Rad));
                        var pos = center + dir * rad;
                        GUI.matrix = old;
                        GUIUtility.RotateAroundPivot(a + 90f, pos);           // Spitze zeigt zur Mitte
                        GUI.DrawTexture(new Rect(pos.x - size / 2f, pos.y - size / 2f, size, size), t);
                    }
                    GUI.matrix = old;
                }
            }
            GUI.color = oc; GUI.matrix = old;
        }
    }
}
