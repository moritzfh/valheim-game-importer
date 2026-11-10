using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Zelda-HUD mit den Original-Grafiken aus dem ROM (tools/oot/oot_hud.py -> player/hud/*.png).
    // Lage wie im Spiel (320x240, include/interface.h): Herzen links oben, Rubine links unten, B/C/A rechts oben.
    // Breitbild: linke Elemente am linken, rechte am rechten Rand verankert. Farben wie N64 (A blau, B gruen, C gelb).
    public static class OotHud
    {
        static readonly Dictionary<string, Texture2D> tex = new Dictionary<string, Texture2D>();
        static string dir;

        public static void Load(string d)
        {
            dir = d; tex.Clear();
            if (!Directory.Exists(d)) { Plugin.Warn("OoT-HUD fehlt: " + d); return; }
            foreach (var f in Directory.GetFiles(d, "*.png"))
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!t.LoadImage(File.ReadAllBytes(f))) continue;
                t.filterMode = FilterMode.Bilinear; t.wrapMode = TextureWrapMode.Clamp; t.hideFlags = HideFlags.HideAndDontSave;
                tex[Path.GetFileNameWithoutExtension(f)] = t;
            }
            Plugin.Log("OoT-HUD: " + tex.Count + " Grafiken");
        }

        static Texture2D T(string n) { Texture2D t; return tex.TryGetValue(n, out t) ? t : null; }

        static float k, sw;
        static Rect L(float x, float y, float w, float h) { return new Rect(x * k, y * k, w * k, h * k); }
        static Rect R(float x, float y, float w, float h) { return new Rect(sw - (320f - x) * k, y * k, w * k, h * k); }

        static void Draw(Rect r, string name, Color c)
        {
            var t = T(name); if (t == null) return;
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        static readonly Color A_COL = new Color(90 / 255f, 90 / 255f, 1f), B_COL = new Color(0f, 150 / 255f, 0f), C_COL = new Color(1f, 160 / 255f, 0f);
        static readonly Color HEART = new Color(1f, 70 / 255f, 50 / 255f), RUPEE = new Color(200 / 255f, 1f, 100 / 255f);
        public static int Rupees;

        // Ortsname beim Betreten (TitleCard_InitPlaceName: 20 Bilder warten, Alpha +10/Bild, 80 Bilder, dann -30/Bild; Mitte 160/120)
        static string titleName; static float titleT0 = -99f;
        public static void ShowTitle(string name) { titleName = name; titleT0 = Time.time; }
        static void DrawTitle()
        {
            if (titleName == null) return;
            var t = T(titleName); if (t == null) { titleName = null; return; }
            float f = (Time.time - titleT0) * 20f - 20f;
            if (f < 0f) return;
            float a, it;
            if (f < 80f) { a = Mathf.Min(255f, f * 10f); it = Mathf.Min(255f, f * 20f); }
            else { a = Mathf.Max(0f, 255f - (f - 80f) * 30f); it = Mathf.Max(0f, 255f - (f - 80f) * 70f); if (a <= 0f) { titleName = null; return; } }
            var old = GUI.color; GUI.color = new Color(it / 255f, it / 255f, it / 255f, a / 255f);
            GUI.DrawTexture(new Rect(sw * 0.5f - 72f * k, (120f - 12f) * k, 144f * k, 24f * k), t, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        static Texture2D blackTex;
        public static bool Hidden;         // "oot hud off": fuer Aufnahmen ohne Anzeige
        public static void Gui()
        {
            try { if (!Hidden) { if (OotPlay.On && Event.current.type == EventType.Repaint) { k = Screen.height / 240f; sw = Screen.width; DrawTitle(); } DrawHud(); OotMoodUi.Gui(); } }
            finally
            {
                // Ortswechsel: Schwarzblende ueber allem (auch dem HUD), wie in OoT
                float fade = Mathf.Max(OotPlay.Fade, OotWake.Fade);
                if (fade > 0.001f && OotPlay.On && Event.current.type == EventType.Repaint)
                {
                    if (blackTex == null) { blackTex = new Texture2D(1, 1); blackTex.SetPixel(0, 0, Color.black); blackTex.Apply(); blackTex.hideFlags = HideFlags.HideAndDontSave; }
                    var o = GUI.color; GUI.color = new Color(0f, 0f, 0f, fade);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), blackTex); GUI.color = o;
                }
                try { OotDev.Gui(); } catch (Exception e) { Plugin.Quiet(e, "OotDev.Gui"); }      // Dev-Pult (F9) ueber allem
            }
        }

        static void DrawHud()
        {
            if (!OotPlay.Driving || tex.Count == 0 || Event.current.type != EventType.Repaint) return;
            var p = Player.m_localPlayer; if (p == null) return;
            k = Screen.height / 240f; sw = Screen.width;
            if (!OotWake.Active) {          // Cutscene (Aufwachen): keine Anzeigen, nur die Textbox
            // Herzen aus OotCombat (0x10 = 1 Herz); das letzte gefuellte Herz schlaegt (Health_UpdateBeatingHeart)
            int hearts = OotCombat.Hearts, last = -1;
            for (int i = 0; i < hearts; i++) if (OotCombat.Quarters(i) > 0) last = i;
            for (int i = 0; i < hearts; i++)
            {
                int f = OotCombat.Quarters(i);
                string n = f == 4 ? "gHeartFullTex" : f == 3 ? "gHeartThreeQuarterTex" : f == 2 ? "gHeartHalfTex" : f == 1 ? "gHeartQuarterTex" : "gHeartEmptyTex";
                float cx = 30f + 10f * (i % 10), cy = 26f + 10f * (i / 10), s = 16f * 0.68f * (i == last ? OotCombat.BeatScale : 1f);
                Draw(L(cx - s / 2f, cy - s / 2f, s, s), n, HEART);
            }
            // Rubine
            Draw(L(26, 206, 16, 16), "gRupeeCounterIconTex", RUPEE);
            int rv = Mathf.Clamp(Rupees, 0, 99);
            Draw(L(42, 206, 8, 16), "gCounterDigit" + (rv / 10) + "Tex", Color.white);
            Draw(L(50, 206, 8, 16), "gCounterDigit" + (rv % 10) + "Tex", Color.white);
            // B (noch kein Schwert: leer), C links/unten/rechts (leer mit Pfeilen), C oben "Navi"
            Draw(R(160, 17, 29, 29), "gButtonBackgroundTex", new Color(B_COL.r, B_COL.g, B_COL.b, 0.85f));
            if (OotItems.HasSword) Draw(R(163.5f, 20.5f, 22, 22), "gItemIconSwordKokiriTex", Color.white);
            Draw(R(227, 18, 27, 27), "gButtonBackgroundTex", new Color(C_COL.r, C_COL.g, C_COL.b, 0.85f));
            Draw(R(227, 18, 27, 27), "gEmptyCLeftArrowTex", C_COL);
            Draw(R(249, 34, 27, 27), "gButtonBackgroundTex", new Color(C_COL.r, C_COL.g, C_COL.b, 0.85f));
            Draw(R(249, 34, 27, 27), "gEmptyCDownArrowTex", C_COL);
            Draw(R(271, 18, 27, 27), "gButtonBackgroundTex", new Color(C_COL.r, C_COL.g, C_COL.b, 0.85f));
            Draw(R(271, 18, 27, 27), "gEmptyCRightArrowTex", C_COL);
            Draw(R(254, 16, 16, 16), "gButtonBackgroundTex", new Color(C_COL.r, C_COL.g, C_COL.b, 0.85f));
            Draw(R(246, 6, 32, 8), "gNaviCUpENGTex", OotDungeon.NaviCalls && ((int)(Time.time * 4f) % 2 == 0) ? new Color(0.5f, 1f, 0.5f) : Color.white);   // Navi will etwas sagen: blinkt gruen
            // A mit Aktion
            Draw(R(186, 9, 29, 29), "gButtonBackgroundTex", new Color(A_COL.r, A_COL.g, A_COL.b, 0.9f));
            var lab = OotPlay.ALabel;
            if (!string.IsNullOrEmpty(lab)) Draw(R(186 + 14.5f - 17.5f, 9 + 14.5f - 6f, 35, 12), "g" + lab + "DoActionENGTex", Color.white);
            OotCombat.Gui(k);
            // Schleuder: Fadenkreuz in der Mitte, Samen-Zahl
            if (OotPlay.Aiming && Event.current.type == EventType.Repaint)
            {
                // Moritz 09.11.: kein rotes Fadenkreuz mehr - kleiner, weicher Ring mit Punkt (OoT selbst zeigt beim Zielen nichts)
                float cx = Screen.width / 2f, cy = Screen.height / 2f, r = 7f * k / 3f;
                var oc = GUI.color; GUI.color = new Color(1f, 1f, 0.9f, 0.7f);
                GUI.DrawTexture(new Rect(cx - r, cy - r, r * 2f, r * 2f), OotPlay.RingTex());
                GUI.color = oc;
            }
            if (Story.Flags.Contains("oot_slingshot"))
            {
                var st = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(8f * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                st.normal.textColor = OotItems.Seeds > 0 ? Color.white : new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(R(282, 36, 20, 10), "3:" + OotItems.Seeds, st);
            }
            }
            // Textbox wie in OoT: dunkel, halbdurchsichtig, unten mittig (TEXTBOX_POS_TOP: oben); Gegenstandsnamen rot
            if (!string.IsNullOrEmpty(OotPlay.Text))
            {
                if (textStyle == null) { textStyle = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, alignment = TextAnchor.UpperLeft }; textStyle.normal.textColor = Color.white; }
                textStyle.fontSize = Mathf.RoundToInt(11.5f * k);       // 4 Zeilen wie im Original (Auswahl Ja/Nein in Zeile 3-4)
                float bw = 256f, bh = 64f; var box = new Rect(Screen.width * 0.5f - bw * k * 0.5f, (OotWake.TextOpen && OotWake.TextTop ? 18f : 240f - 20f - bh) * k, bw * k, bh * k);
                if (blackTex == null) { blackTex = new Texture2D(1, 1); blackTex.SetPixel(0, 0, Color.black); blackTex.Apply(); blackTex.hideFlags = HideFlags.HideAndDontSave; }
                var o = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.72f); GUI.DrawTexture(box, blackTex); GUI.color = o;
                var t = OotPlay.Text.Replace("Kokiri Sword", "<color=#ff4b3c>Kokiri Sword</color>");
                GUI.Label(new Rect(box.x + 12f * k, box.y + 4f * k, box.width - 24f * k, box.height - 4f * k), t, textStyle);
            }
        }
        static GUIStyle textStyle;
    }
}
