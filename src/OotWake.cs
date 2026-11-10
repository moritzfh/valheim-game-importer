using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Links Haus: Navi weckt Link (gLinkHouseIntroWakeUpCs) - im Spiel als Ablauf nach dem Original-Zeitplan (cs-Bilder, 20 je Sekunde).
    /// Texte wie CutsceneCmd_Text: ab Start+1 offen, am Ende haelt die Szene, bis der Text zu ist (Figuren laufen weiter).
    /// Navi wie En_Elf im Cue-Kanal 8 (Bewegungsarten 0/6/9/10, Geschwindigkeit wie func_80A02C98), Link mit clink_op3_* im Bett
    /// (nur das Modell liegt dort, die Spielfigur steht schon am Endpunkt PLAYER_CUEID_5), feste Kamera aus den Kamera-Splines.
    /// Laeuft einmal (Merker "oot_awake"), sobald Link in seinem Haus steht. Fernsteuerung: "oot wake" (nochmal), "oot wake skip".
    /// pack.json "oot_wake" (oot_intro.wake_cfg): alles in OoT-Einheiten, "origin" + "s" rechnen nach Valheim um (x, y, -z).
    /// </summary>
    public static class OotWake
    {
        static Pack forPack; static object cfg;
        static Vector3 org; static float S = 0.033f;
        static bool active, pending; static int f; static float acc, beginT;
        // Link
        static string cue; static float clipT0; static List<object> clipSfx; static int clipSfxIdx;
        // Text
        static int curText = -1, page; static bool textOpen; static float textT; static readonly List<object> texts = new List<object>();
        // Navi (OoT-Einheiten, wie EnElf)
        static Vector3 np, nv; static int mode = -1, a2AA, a2AC, s2AE, s2B0; static float r2B4, r2B8; static bool dashDown;

        public static bool Active { get { return active; } }
        public static string Clip { get; private set; }
        public static string Text { get; private set; }
        public static bool TextTop { get; private set; }
        public static float Fade { get; private set; }
        public static Vector3 ModelPos { get; private set; }
        public static float ModelYaw { get; private set; }

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }
        static Vector3 W(Vector3 o) { return org + new Vector3(o.x * S, o.y * S, -o.z * S); }
        public static float YawV(float ootYaw) { return Mathf.Repeat(180f - ootYaw / 65536f * 360f, 360f); }
        static float SinS(int a) { return Mathf.Sin(a * (Mathf.PI * 2f / 65536f)); }
        static float CosS(int a) { return Mathf.Cos(a * (Mathf.PI * 2f / 65536f)); }
        static float StepTo(float v, float t, float step) { return v < t ? Mathf.Min(v + step, t) : Mathf.Max(v - step, t); }
        static int I(object o) { return Convert.ToInt32(o); }

        static void Setup(Pack pk)
        {
            forPack = pk; cfg = null; texts.Clear();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            cfg = Json.V(meta, "oot_wake"); if (cfg == null) return;
            org = V3(Json.L(cfg, "origin")); S = Json.F(cfg, "s", 0.033f);
            foreach (var t in Json.L(cfg, "texts") ?? new List<object>()) texts.Add(t);
        }

        public static Vector3 EndPos { get { return W(V3(Json.L(cfg, "end"))); } }
        /// <summary>Endpunkt der Aufwach-Szene (Links Haus, neben dem Bett) - auch bevor die Szene einmal lief.</summary>
        public static bool TryEnd(out Vector3 pos, out float yaw)
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            pos = Vector3.zero; yaw = 0f;
            if (cfg == null) return false;
            pos = EndPos; yaw = EndYaw; return true;
        }
        public static float EndYaw { get { return YawV(Json.F(cfg, "end_yaw", -32768f)); } }
        static string Flag { get { return Json.S(cfg, "flag", "oot_awake"); } }

        /// <summary>jedes Bild aus OotPlay.Tick (vor den Aktionen): startet die Szene oder spielt sie weiter.</summary>
        public static void Tick(Player p)
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            if (cfg == null || p == null) return;
            if (active) { Update(); return; }
            Fade = 0f;
            if (!pending && Story.Flags.Contains(Flag)) return;
            if (OotTitle.Active || OotTitle.Pending) return;                               // erst nach Startbildschirm + Intro
            var end = EndPos;
            if ((p.transform.position - end).sqrMagnitude > 8f * 8f) return;           // nur in Links Haus
            if (!Physics.Raycast(end + Vector3.up, Vector3.down, 4f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore)) return;
            bool tele = false; try { tele = p.IsTeleporting(); } catch { }
            if (OotPlay.Warping || tele) return;                                           // erst nach dem Ortswechsel (sonst setzt WarpCo den Zustand zurueck)
            Begin(p);
        }

        public static string Remote(string arg)
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            if (cfg == null) return "wake: kein oot_wake im Paket";
            if (arg == "skip") { if (active) End(); return "wake: uebersprungen"; }
            if (arg == "info") return Info();
            if (active) return "wake: laeuft (Bild " + f + ")";
            pending = true;
            var p = Player.m_localPlayer;
            if (p != null && (p.transform.position - EndPos).sqrMagnitude > 64f) OotPlay.WarpTo(EndPos, EndYaw);
            return "wake: startet in Links Haus";
        }

        public static string Info()
        {
            return "wake " + (active ? "an" : "aus") + " bild " + f + " clip " + Clip + " text " + (curText >= 0 ? Json.S(texts[curText], "id") + "/" + page + (textOpen ? " offen" : " zu") : "-")
                   + " navi " + np.ToString("F0") + " art " + mode + (Story.Flags.Contains(Flag) ? " (gesehen)" : "");
        }

        static void Begin(Player p)
        {
            active = true; pending = false; f = 0; acc = 0f; beginT = Time.unscaledTime; Fade = 1f;
            cue = null; Clip = null; curText = -1; page = 0; textOpen = false; Text = null; TextTop = false;
            mode = -1; a2AA = a2AC = 0; nv = Vector3.zero; dashDown = false;
            var nc = Json.L(cfg, "navi"); np = Vector3.zero;
            if (nc != null && nc.Count > 0) np = V3((nc[0] as List<object>)[3] as List<object>);
            ModelPos = W(V3(Json.L(cfg, "link"))); ModelYaw = YawV(Json.F(cfg, "link_yaw", -32768f));
            OotPlay.BeginWake(EndPos, EndYaw);
            Frame(false);
            Plugin.Log("OoT: Aufwach-Szene startet");
        }

        static void End()
        {
            active = false; Text = null; TextTop = false; Fade = 0f; Clip = null;
            OotNavi.Override = null;
            Story.Flags.Add(Flag); Story.Save();
            OotPlay.EndWake(EndPos, EndYaw);
            Plugin.Log("OoT: Aufwach-Szene fertig");
        }

        static void Update()
        {
            float since = Time.unscaledTime - beginT;
            Fade = since < 0.5f ? 1f : Mathf.Clamp01(1f - (since - 0.5f) / 0.6f);
            acc += Mathf.Min(Time.deltaTime, 0.1f) * 20f;
            while (acc >= 1f && active) { acc -= 1f; Frame(true); }
            if (!active) return;
            ClipTick();
            OotNavi.Override = W(np);
            Text = textOpen && curText >= 0 ? PageText() : null;
            if (textOpen) textT += Time.deltaTime;
        }

        // ein cs-Bild (Cutscene_ProcessScript: curFrame++ und dann die Befehle in Skript-Reihenfolge)
        static void Frame(bool advance)
        {
            if (advance) f++;
            PlayerCue();
            NaviStep();
            TextCmd();
            if (f >= Json.F(cfg, "end_frame", 645f)) End();
        }

        static void PlayerCue()
        {
            string want = null;
            foreach (var o in Json.L(cfg, "player") ?? new List<object>())
            {
                var l = o as List<object>; if (l == null || l.Count < 3) continue;
                if (f >= I(l[0])) want = l[2].ToString();
            }
            if (want == null || want == cue) return;
            cue = want; StartClip("op3_" + want);
        }

        static void StartClip(string n)
        {
            Clip = n; clipT0 = Time.time; clipSfxIdx = 0;
            clipSfx = Json.L(Json.V(cfg, "sfx"), n.Substring(4));
        }

        // Link: einmal-Clips gehen in ihre Schleife ueber (func_80851ECC / func_80851FB0), Original-Geraeusche nach Animationsbild
        static void ClipTick()
        {
            if (Clip == null) return;
            float af = (Time.time - clipT0) * 30f;
            while (clipSfx != null && clipSfxIdx < clipSfx.Count)
            {
                var e = clipSfx[clipSfxIdx] as List<object>;
                if (e == null || e.Count < 2) { clipSfxIdx++; continue; }
                if (af < Convert.ToSingle(e[0])) break;
                OotSfx.Play(e[1].ToString(), 0.9f, ModelPos + Vector3.up * 0.5f);
                clipSfxIdx++;
            }
            var len = Json.V(cfg, "len");
            if (Clip == "op3_negaeri" && af >= Json.F(len, "negaeri", 55f) - 1f) { Clip = "op3_wait2"; clipT0 = Time.time; clipSfx = null; }
            else if (Clip == "op3_okiagari" && af >= Json.F(len, "okiagari", 365f) - 1f) { Clip = "op3_wait3"; clipT0 = Time.time; clipSfx = null; }
        }

        // CutsceneCmd_Text
        static void TextCmd()
        {
            for (int i = 0; i < texts.Count; i++)
            {
                var t = texts[i]; int a = (int)Json.F(t, "start"), b = (int)Json.F(t, "end");
                if (f <= a || f > b) continue;
                if (curText != i) { OpenText(i); return; }
                if (f >= b && textOpen) f--;
            }
        }

        static void OpenText(int i)
        {
            curText = i; page = 0; textOpen = true; textT = 0f;
            TextTop = Json.B(texts[i], "top");
            var sfx = Json.S(texts[i], "sfx", "");
            if (!string.IsNullOrEmpty(sfx)) OotSfx.Play(sfx, 1f, W(np));
            OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.6f);
        }

        static string PageText()
        {
            var pg = Json.L(texts[curText], "pages");
            return pg != null && page < pg.Count ? pg[page].ToString() : null;
        }

        /// <summary>A/B in der Szene: Textseite weiter / Text zu.</summary>
        public static void Press()
        {
            if (!active || !textOpen || textT < 0.3f) return;
            var pg = Json.L(texts[curText], "pages");
            page++; textT = 0f;
            if (pg == null || page >= pg.Count) { textOpen = false; Text = null; OotSfx.Play("NA_SE_SY_MESSAGE_END", 0.8f); }
            else OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.8f);
        }
        public static bool TextOpen { get { return active && textOpen; } }

        // ---------- Navi (En_Elf: func_80A0461C Bewegungsart, func_80A01C38 Werte, func_80A02A20 Versatz, func_80A02C98 Flug) ----------
        static void SetMode(int m)
        {
            mode = m;
            switch (m)
            {
                case 10: s2AE = 0x400; s2B0 = 0; r2B8 = 0f; r2B4 = 5f; break;
                case 9: s2AE = 0x1000; s2B0 = 0x200; r2B4 = 3f; r2B8 = 10f; break;
                case 6: s2AE = 0x1000; s2B0 = 0x800; r2B4 = 60f; r2B8 = 20f; break;
                default: s2AE = 0x400; s2B0 = 0x200; r2B4 = 5f; r2B8 = 20f; break;
            }
        }

        static void NaviStep()
        {
            List<object> c = null;
            var cues = Json.L(cfg, "navi") ?? new List<object>();
            foreach (var o in cues)
            {
                var l = o as List<object>; if (l == null || l.Count < 5) continue;
                if (f >= I(l[1]) && f < I(l[2])) { c = l; break; }
            }
            if (c == null && cues.Count > 0) { var last = cues[cues.Count - 1] as List<object>; if (last != null && f >= I(last[2])) c = last; }
            int id = c != null ? I(c[0]) : 0;
            int want = id == 4 ? 9 : id == 3 ? 6 : id == 1 ? 10 : 0;
            if (want != mode) SetMode(want);
            var off = new Vector3(SinS(a2AC) * r2B8, SinS(a2AA) * r2B4, CosS(a2AC) * r2B8);
            a2AC = (a2AC + s2B0) & 0xFFFF; a2AA = (a2AA + s2AE) & 0xFFFF;
            if (c == null) return;
            int s = I(c[1]), e = I(c[2]);
            float w = e != s ? 1f - (e - f) / (float)(e - s) : 1f; if (!(w < 1f)) w = 1f;
            var tg = Vector3.Lerp(V3(c[3] as List<object>), V3(c[4] as List<object>), w);
            float prevY = np.y;
            nv.y = StepTo(nv.y, Mathf.Clamp((tg.y + off.y - np.y) * 0.2f, -20f, 20f), 32f);
            if (mode == 10) { nv.x = nv.z = 0f; np.y += nv.y * 1.5f; np.x = tg.x + off.x; np.z = tg.z + off.z; }
            else
            {
                nv.x = StepTo(nv.x, Mathf.Clamp((tg.x + off.x - np.x) * 0.2f, -20f, 20f), 1.5f);
                nv.z = StepTo(nv.z, Mathf.Clamp((tg.z + off.z - np.z) * 0.2f, -20f, 20f), 1.5f);
                np += nv * 1.5f;
            }
            // Original: Sausen beim Hereinfliegen (Bild 55) und bei jedem Abwaertsstoss beim Wecken (Art 6)
            if (f == (int)Json.F(Json.V(cfg, "sfx"), "dash_frame", 55f) && f > 0) OotSfx.Play("NA_SE_EV_FAIRY_DASH", 0.8f, W(np));
            if (mode == 6)
            {
                if (dashDown) { if (prevY < np.y) dashDown = false; }
                else if (np.y < prevY) { dashDown = true; OotSfx.Play("NA_SE_EV_FAIRY_DASH", 0.7f, W(np)); }
            }
        }

        /// <summary>feste Kamera aus den Splines (Augpunkt + Zielpunkt, zweiter Zielpunkt ab Bild "at"[1][0]+1).</summary>
        public static void Camera(Camera c)
        {
            var eye = W(V3(Json.L(cfg, "eye")));
            Vector3 at = eye + Vector3.forward;
            foreach (var o in Json.L(cfg, "at") ?? new List<object>())
            {
                var l = o as List<object>; if (l == null || l.Count < 2) continue;
                if (I(l[0]) == 0 || f > I(l[0])) at = W(V3(l[1] as List<object>));
            }
            c.transform.position = eye; c.transform.rotation = Quaternion.LookRotation(at - eye);
            c.fieldOfView = Json.F(cfg, "fov", 60f);
        }
    }
}
