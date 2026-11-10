using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // F7 im Zelda-Spiel (F8 = Cutscene): kleines Stimmungs-Pult zum Ausprobieren (Uhrzeit, Valheim-Wetter, Nebel-Boeen).
    // "Speichern" schreibt config/GameImporter/oot_mood.json; diese Werte gelten dann statt der Paket-Vorgabe.
    public static class OotMoodUi
    {
        public static bool Open;
        static Vector2 scroll;
        static string[] envNames;
        static string saved;
        static float savedT;
        static GUIStyle box, lab, btn, btnSel;

        public static string FilePath { get { return Path.Combine(GameImporter.Remote.Dir, "oot_mood.json"); } }

        public static void Tick()
        {
            if (!OotPlay.On) { Open = false; return; }
            if (Input.GetKeyDown(KeyCode.F7)) { Open = !Open; if (Open) envNames = null; }
            if (Open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        static void Styles()
        {
            if (box != null) return;
            box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
            lab = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            lab.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            btnSel = new GUIStyle(btn) { fontStyle = FontStyle.Bold };
            btnSel.normal.textColor = new Color(1f, 0.85f, 0.3f);
            btnSel.hover.textColor = btnSel.normal.textColor;
        }

        static string[] Envs()
        {
            if (envNames != null) return envNames;
            var l = new List<string>();
            try
            {
                if (EnvMan.instance != null)
                    foreach (var e in EnvMan.instance.m_environments) if (e != null && !string.IsNullOrEmpty(e.m_name) && !l.Contains(e.m_name)) l.Add(e.m_name);
            }
            catch (Exception e) { Plugin.Quiet(e, "OotMoodUi.Envs"); }
            l.Sort(StringComparer.OrdinalIgnoreCase);
            envNames = l.ToArray();
            return envNames;
        }

        static string Clock(float t)
        {
            int m = Mathf.RoundToInt(Mathf.Repeat(t, 1f) * 24f * 60f) % (24 * 60);
            return (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
        }

        public static void Gui()
        {
            if (!Open || !OotPlay.On) return;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            Styles();
            float w = 430f, h = Mathf.Min(Screen.height - 40f, 640f);
            var r = new Rect(20f, 20f, w, h);
            GUI.color = new Color(0f, 0f, 0f, 0.82f); GUI.Box(r, ""); GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(r.x + 12f, r.y + 10f, w - 24f, h - 20f));
            GUILayout.Label("Stimmung (F7 schliesst)", lab);

            // Uhrzeit
            bool fixedT = OotPlay.MoodTime >= 0f;
            bool nf = GUILayout.Toggle(fixedT, " Uhrzeit festhalten", btn);
            if (nf != fixedT) OotPlay.MoodTime = nf ? 0.35f : -1f;
            if (OotPlay.MoodTime >= 0f)
            {
                GUILayout.Label("Uhrzeit " + OotPlay.MoodTime.ToString("0.000") + "  (" + Clock(OotPlay.MoodTime) + " Uhr)", lab);
                OotPlay.MoodTime = GUILayout.HorizontalSlider(OotPlay.MoodTime, 0f, 1f);
                GUILayout.BeginHorizontal();
                foreach (var v in new[] { 0.25f, 0.3f, 0.35f, 0.4f, 0.5f, 0.7f, 0.75f, 0.97f })
                    if (GUILayout.Button(v.ToString("0.00"), Mathf.Abs(OotPlay.MoodTime - v) < 0.001f ? btnSel : btn)) OotPlay.MoodTime = v;
                GUILayout.EndHorizontal();
            }

            // Nebel-Boeen
            GUILayout.Space(8f);
            OotPlay.MoodGustOn = GUILayout.Toggle(OotPlay.MoodGustOn, " Nebel-Boeen ab und zu (" + (OotPlay.MoodGust ?? "-") + ")", btn);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Boee jetzt", btn)) OotPlay.GustNow();
            if (GUILayout.Button("Boee aus", btn)) OotPlay.GustStop();
            GUILayout.EndHorizontal();

            // Wetter
            GUILayout.Space(8f);
            GUILayout.Label("Wetter im Kokiri-Wald: " + (string.IsNullOrEmpty(OotPlay.MoodEnv) ? "Valheim normal" : OotPlay.MoodEnv) + "   |   linke Taste = Wetter, rechte = Boee", lab);
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(120f, h - 330f)));
            if (GUILayout.Button("(Valheim normal)", string.IsNullOrEmpty(OotPlay.MoodEnv) ? btnSel : btn)) OotPlay.MoodEnv = "";
            foreach (var n in Envs())
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(n, n == OotPlay.MoodEnv ? btnSel : btn, GUILayout.Width(w - 140f))) OotPlay.MoodEnv = n;
                if (GUILayout.Button("als Boee", n == OotPlay.MoodGust ? btnSel : btn)) OotPlay.MoodGust = n;
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Speichern (gilt ab jetzt immer)", btn)) { saved = Save(); savedT = Time.time; }
            if (GUILayout.Button("Paket-Vorgabe", btn)) { try { if (File.Exists(FilePath)) File.Move(FilePath, FilePath + ".alt_" + DateTime.Now.ToString("HHmmss")); } catch { } OotPlay.ReloadMood(); saved = "Vorgabe wieder aktiv"; savedT = Time.time; }
            GUILayout.EndHorizontal();
            if (saved != null && Time.time - savedT < 6f) GUILayout.Label(saved, lab);
            GUILayout.EndArea();
        }

        static string Save()
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var s = "{\"time\": " + OotPlay.MoodTime.ToString("0.###", inv) + ", \"env\": \"" + (OotPlay.MoodEnv ?? "") + "\", \"gust\": \"" + (OotPlay.MoodGust ?? "")
                        + "\", \"gust_on\": " + (OotPlay.MoodGustOn ? "true" : "false") + "}";
                File.WriteAllText(FilePath, s);
                Plugin.Log("Stimmung gespeichert: " + s);
                return "Gespeichert: " + s;
            }
            catch (Exception e) { return "Speichern ging nicht: " + e.Message; }
        }
    }
}
