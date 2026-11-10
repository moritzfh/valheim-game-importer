using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace GameImporter
{
    // Dev-Werkzeuge im Zelda-Spiel (fuer Aufnahmen und schnelles Testen):
    //   F9  Pult: Teleport (Kokiri-Wald, jeder Raum im Deku-Baum, eigene Punkte), Unverwundbar, Zeitlupe, Nebel/Teilchen aus,
    //       alle Items, Herzen voll, Raetsel zuruecksetzen, Position kopieren
    //   F10 freie Kamera: Maus schauen, WASD fliegen, E/Leertaste hoch, Q/Strg runter, Umschalt schnell, Alt langsam,
    //       Mausrad Tempo. K = Kamerapunkt setzen, P = Kamerafahrt abspielen/stoppen, L = letzten Punkt loeschen
    //   F11 Anzeige (Herzen, Knoepfe) an/aus
    // Eigene Punkte: config/GameImporter/oot_dev_spots.json, Kamerafahrt: config/GameImporter/fly_user.txt
    public static class OotDev
    {
        public static bool Open, FreeCam, God, NoFog;
        public static float Slow = 1f;
        static Vector2 scroll;
        static GUIStyle box, lab, head, btn, btnOn, small;
        static Texture2D bg;
        static string toast; static float toastT;
        static string newName = "";

        class Spot { public string Name; public Vector3 P; public float Yaw; public Spot(string n, Vector3 p, float y) { Name = n; P = p; Yaw = y; } }
        static List<Spot> mine;

        // Deku-Baum: OoT-Einheiten -> Welt (wie tools/oot/oot_ydan.py YV: Versatz -1750/230/-1050, Skala 0,033, z gespiegelt)
        static Vector3 YV(float x, float y, float z) { return new Vector3(-1750f + x * 0.033f, 261.98f + y * 0.033f, -1050f - z * 0.033f); }
        public static Vector3 YdanEntrance { get { return YV(0f, 0f, 450f); } }   // Eingang 1. Stock (Rettung bei kaputtem Halt)
        static readonly Spot[] Kf =
        {
            new Spot("Links Haus (innen)", new Vector3(-1502.31f, 201.98f, -1499.87f), 90f),
            new Spot("Vor Links Haus", new Vector3(648.98f, 41.28f, -185.41f), 0f),
            new Spot("Dorfmitte", new Vector3(710f, float.NaN, -150f), 0f),
            new Spot("Vor dem Laden", new Vector3(678.18f, 37.98f, -141.75f), 180f),
            new Spot("Laden (innen)", new Vector3(-1380f, 201.98f, -1722.64f), 0f),
            new Spot("Kriechgang / Trainingsplatz", new Vector3(624.1f, 42.11f, -196.86f), 180f),
            new Spot("Vor dem Deku-Baum", new Vector3(778.57f, 32.93f, -112.91f), 50f),
        };
        static readonly Spot[] Ydan =
        {
            new Spot("Eingang (1. Stock)", YV(0f, 0f, 450f), 0f),
            new Spot("Hauptraum 1. Stock, am Netz", YV(-80f, 0f, -338f), 0f),
            new Spot("Hauptraum 2. Stock", YV(-345f, 400f, 386f), 0f),
            new Spot("Hauptraum 3. Stock", YV(-326f, 800f, -234f), 0f),
            new Spot("3. Stock, Plattform Ost", YV(341f, 800f, -220f), 0f),
            new Spot("Schrubber-Raum (2. Stock)", YV(-839f, 400f, 740f), 0f),
            new Spot("Schleuder-Raum (Lift)", YV(-1218f, 280f, 1115f), 0f),
            new Spot("Kompass-Raum (3. Stock)", YV(-1262f, 720f, 142f), 0f),
            new Spot("B1 unter dem Netz", YV(-101f, -880f, 155f), 0f),
            new Spot("B1 Fackeln + Block", YV(-743f, -820f, 241f), 0f),
            new Spot("B1 Schrubber + Schalter", YV(-75f, -880f, 1173f), 0f),
            new Spot("B1 Wasserraum (Ostufer)", YV(-470f, -880f, 1050f), 270f),
            new Spot("B1 Fackelraum West", YV(-1828f, -760f, 1116f), 0f),
            new Spot("Gohma-Larven", YV(-1961f, -760f, -268f), 0f),
            new Spot("Gang zu B2", YV(-2564f, -760f, -644f), 0f),
            new Spot("B2 Halle", YV(-901f, -1906f, -315f), 0f),
            new Spot("B2 Deku-Raetsel (2-3-1)", YV(-368f, -1880f, -677f), 0f),
            new Spot("Gohma (Boss)", YV(-1869f, -1868f, -2631f), 0f),
        };

        static string SpotFile { get { return Path.Combine(GameImporter.Remote.Dir, "oot_dev_spots.json"); } }
        static string FlyFile { get { return Path.Combine(GameImporter.Remote.Dir, "fly_user.txt"); } }

        // ---------- jedes Bild ----------
        public static void Tick()
        {
            if (!OotPlay.On) { Open = false; if (FreeCam) FreeCam = false; return; }
            bool typing = (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible();
            if (!typing)
            {
                if (Input.GetKeyDown(KeyCode.F9)) Open = !Open;
                if (Input.GetKeyDown(KeyCode.F10)) SetFreeCam(!FreeCam);
                if (Input.GetKeyDown(KeyCode.F11)) { OotHud.Hidden = !OotHud.Hidden; }
                if (FreeCam && !Open)
                {
                    if (Input.GetKeyDown(KeyCode.K)) AddKey();
                    if (Input.GetKeyDown(KeyCode.L) && keys.Count > 0) { keys.RemoveAt(keys.Count - 1); Toast("Kamerapunkt geloescht (" + keys.Count + " uebrig)"); }
                    if (Input.GetKeyDown(KeyCode.P)) PlayFly();
                }
                else if (!FreeCam && flying && Input.GetKeyDown(KeyCode.P)) StopFly();
            }
            if (Open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Slow != 1f && Time.timeScale > 0.001f) Time.timeScale = Slow;
            if (NoFog) RenderSettings.fog = false;
        }

        static void Toast(string s) { toast = s; toastT = Time.unscaledTime; }

        // Fernsteuerung zum Pruefen: oot dev open | tp <k|y><nr> | free on|off | god on|off
        public static string Remote(string[] a)
        {
            string c = a.Length > 2 ? a[2] : "";
            switch (c)
            {
                case "open": Open = !Open; return "pult " + Open;
                case "free": SetFreeCam(a.Length > 3 && a[3] == "on"); return "freie kamera " + FreeCam;
                case "god": God = a.Length > 3 && a[3] == "on"; return "unverwundbar " + God;
                case "resetydan": ResetYdan(); return toast;
                case "resetgame": ResetGame(); return toast;
                case "newgame": ResetGame(); OotTitle.Show(true); return toast + " | Startbildschirm + Intro";
                case "tp":
                    {
                        if (a.Length < 4 || a[3].Length < 2) return "oot dev tp k0 | y3";
                        var list = a[3][0] == 'k' ? Kf : Ydan; int i;
                        if (!int.TryParse(a[3].Substring(1), out i) || i < 0 || i >= list.Length) return "nr 0.." + (list.Length - 1);
                        Teleport(list[i]); return "tp " + list[i].Name;
                    }
            }
            return "kf " + Kf.Length + " ydan " + Ydan.Length + " eigene " + (mine != null ? mine.Count : 0);
        }

        // ---------- freie Kamera ----------
        static Vector3 fcPos; static float fcYaw, fcPitch, fcSpeed = 5f;
        static readonly List<float[]> keys = new List<float[]>();
        public static float SegSec = 4f;
        static bool flying;

        static void SetFreeCam(bool on)
        {
            if (on && !FreeCam)
            {
                var c = UnityEngine.Camera.main;
                if (c != null) { fcPos = c.transform.position; var e = c.transform.eulerAngles; fcYaw = e.y; fcPitch = e.x > 180f ? e.x - 360f : e.x; }
                if (flying) StopFly();
                Toast("Freie Kamera an  ·  WASD fliegen, E/Q hoch/runter, Umschalt schnell  ·  K Punkt setzen, P Fahrt abspielen, L letzten loeschen");
            }
            else if (!on && FreeCam) Toast("Freie Kamera aus");
            FreeCam = on;
        }

        public static void Camera(UnityEngine.Camera c)
        {
            float dt = Time.unscaledDeltaTime;
            if (!Open)
            {
                float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
                fcYaw += mx * 2.2f; fcPitch = Mathf.Clamp(fcPitch - my * 1.8f, -89f, 89f);
                float sc = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(sc) > 0.01f) { fcSpeed = Mathf.Clamp(fcSpeed * (sc > 0 ? 1.25f : 0.8f), 0.3f, 80f); Toast("Kameratempo " + fcSpeed.ToString("F1") + " m/s"); }
                var rot = Quaternion.Euler(fcPitch, fcYaw, 0f);
                var mv = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) mv += Vector3.forward;
                if (Input.GetKey(KeyCode.S)) mv += Vector3.back;
                if (Input.GetKey(KeyCode.D)) mv += Vector3.right;
                if (Input.GetKey(KeyCode.A)) mv += Vector3.left;
                var up = Vector3.zero;
                if (Input.GetKey(KeyCode.E) || Input.GetKey(KeyCode.Space)) up += Vector3.up;
                if (Input.GetKey(KeyCode.Q) || Input.GetKey(KeyCode.LeftControl)) up += Vector3.down;
                float k = fcSpeed * (Input.GetKey(KeyCode.LeftShift) ? 4f : 1f) * (Input.GetKey(KeyCode.LeftAlt) ? 0.25f : 1f);
                fcPos += (rot * mv + up) * k * dt;
            }
            c.transform.position = fcPos; c.transform.rotation = Quaternion.Euler(fcPitch, fcYaw, 0f); c.fieldOfView = 55f;
        }

        static void AddKey()
        {
            var look = fcPos + Quaternion.Euler(fcPitch, fcYaw, 0f) * Vector3.forward * 10f;
            float t = keys.Count == 0 ? 0f : keys[keys.Count - 1][0] + SegSec;
            keys.Add(new[] { t, fcPos.x, fcPos.y, fcPos.z, look.x, look.y, look.z });
            Toast("Kamerapunkt " + keys.Count + " gesetzt (" + t.ToString("F0") + " s)");
        }

        static void PlayFly()
        {
            if (keys.Count < 2) { Toast("Erst mindestens 2 Kamerapunkte setzen (K)"); return; }
            var ci = CultureInfo.InvariantCulture; var sb = new StringBuilder("# t px py pz lx ly lz (Welt), aus der freien Kamera (F10, K)\n");
            foreach (var e in keys) { for (int i = 0; i < 7; i++) sb.Append(e[i].ToString("F3", ci)).Append(i < 6 ? " " : "\n"); }
            try { File.WriteAllText(FlyFile, sb.ToString()); } catch (Exception ex) { Toast("Speichern ging nicht: " + ex.Message); return; }
            FreeCam = false;
            OotPlay.Remote(new[] { "oot", "camfly", "fly_user.txt" });
            flying = true;
            Toast("Kamerafahrt " + keys[keys.Count - 1][0].ToString("F0") + " s  ·  P stoppt");
        }

        static void StopFly() { OotPlay.Remote(new[] { "oot", "camfly", "off" }); flying = false; Toast("Kamerafahrt gestoppt"); }

        // ---------- Aktionen ----------
        static void Teleport(Spot s)
        {
            if (OotPlay.Settling && !float.IsNaN(s.P.y)) { OotPlay.DevMove(s.P, s.Yaw); Toast("-> " + s.Name); return; }   // Laden haengt (Spielstand in der Luft): direkt hin
            if (OotPlay.Warping || (Player.m_localPlayer != null && Player.m_localPlayer.IsTeleporting())) { Toast("Teleport laeuft noch ..."); return; }
            var p = s.P;
            if (float.IsNaN(p.y)) p.y = Pack.Ground(p.x, p.z) + 0.3f;
            var pl = Player.m_localPlayer;
            int gmask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            RaycastHit hit;
            bool floor = Physics.Raycast(p + Vector3.up * 1.5f, Vector3.down, out hit, 4f, gmask, QueryTriggerInteraction.Ignore);
            if (floor) p.y = hit.point.y;                              // genau auf den Boden
            bool near = pl != null && Vector3.Distance(pl.transform.position, p) < 160f && floor;
            if (near) OotPlay.DevMove(p, s.Yaw);                      // im selben Gebiet: sofort
            else if (!OotPlay.WarpTo(p, s.Yaw)) { Toast("Teleport laeuft noch"); return; }   // weit weg: mit Ladeblende
            if (FreeCam) { fcPos = p + Vector3.up * 2.2f + Quaternion.Euler(0f, s.Yaw, 0f) * Vector3.back * 3.5f; fcYaw = s.Yaw; fcPitch = 15f; }
            Toast("-> " + s.Name);
        }

        static string R(params string[] a) { try { return OotPlay.Remote(a); } catch (Exception e) { return e.Message; } }

        static void GiveAll()
        {
            R("oot", "items", "sword"); R("oot", "shield"); R("oot", "sticks", "10"); R("oot", "seeds", "30"); R("oot", "rupees", "99");
            OotCombat.Hp = OotCombat.MaxHp;
            Toast("Schwert, Schild, Schleuder (30), 10 Deku-Staebe, 99 Rubine, Herzen voll");
        }

        // ---------- Neustart fuer Aufnahmen ----------
        static void Rebuild()
        {
            OotDungeon.Clear(); OotYdanObj.Clear(); OotEnemies.Clear(); OotNpcs.Clear(); OotItems.Rebuild(); OotDekuTree.Rebuild();
        }

        /// <summary>Deku-Baum wie beim ersten Betreten: Netze, Fackeln, Schalter, Truhen, Tueren, Gegner, Intro; Schleuder weg.</summary>
        static void ResetYdan()
        {
            var rm = new List<string>();
            foreach (var f in Story.Flags)
                if (f.StartsWith("oot_ydan_") || f == "used:ydan_intro" || f == "oot_slingshot" || f.StartsWith("oot_n_seeds=")) rm.Add(f);
            foreach (var f in rm) Story.Flags.Remove(f);
            OotItems.Seeds = 0;
            Story.Save(); Rebuild();
            Toast("Deku-Baum zurueckgesetzt (" + rm.Count + " Merker) – Netze, Truhen, Gegner und Intro wieder da");
        }

        /// <summary>Spielstand von vorn: alle Merker weg, keine Gegenstaende, Herzen voll, ab ins Bett (Aufwach-Szene).</summary>
        static void ResetGame()
        {
            Story.Flags.Clear();
            OotItems.Sticks = 0; OotItems.Nuts = 0; OotItems.Seeds = 0; OotHud.Rupees = 0;
            OotCombat.Hp = OotCombat.MaxHp;
            Story.Save(); Rebuild();
            Teleport(Kf[0]);
            Toast("Spiel von vorn – Aufwach-Szene in Links Haus");
        }

        static void LoadMine()
        {
            mine = new List<Spot>();
            try
            {
                if (!File.Exists(SpotFile)) return;
                var l = Json.Parse(File.ReadAllText(SpotFile)) as List<object>;
                if (l == null) return;
                foreach (var o in l)
                {
                    var p = Json.L(o, "p"); if (p == null || p.Count < 3) continue;
                    mine.Add(new Spot(Json.S(o, "name", "Punkt"), new Vector3(Convert.ToSingle(p[0]), Convert.ToSingle(p[1]), Convert.ToSingle(p[2])), Json.F(o, "yaw", 0f)));
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotDev.LoadMine"); }
        }

        static void SaveMine()
        {
            var ci = CultureInfo.InvariantCulture; var sb = new StringBuilder("[\n");
            for (int i = 0; i < mine.Count; i++)
            {
                var s = mine[i];
                sb.Append("  {\"name\": \"").Append(s.Name.Replace("\\", "/").Replace("\"", "'")).Append("\", \"p\": [")
                  .Append(s.P.x.ToString("F3", ci)).Append(", ").Append(s.P.y.ToString("F3", ci)).Append(", ").Append(s.P.z.ToString("F3", ci))
                  .Append("], \"yaw\": ").Append(s.Yaw.ToString("F1", ci)).Append(i < mine.Count - 1 ? "},\n" : "}\n");
            }
            sb.Append("]\n");
            try { File.WriteAllText(SpotFile, sb.ToString()); } catch (Exception e) { Toast("Speichern ging nicht: " + e.Message); }
        }

        static string PosText(out string tp)
        {
            var pl = Player.m_localPlayer; tp = "";
            if (pl == null) return "-";
            var p = pl.transform.position; var ci = CultureInfo.InvariantCulture;
            tp = "tp " + p.x.ToString("F2", ci) + " " + p.y.ToString("F2", ci) + " " + p.z.ToString("F2", ci);
            string s = "Welt " + p.x.ToString("F1", ci) + " / " + p.y.ToString("F1", ci) + " / " + p.z.ToString("F1", ci);
            if (p.x < -1700f && p.x > -1860f && p.z < -940f && p.z > -1120f && p.y > 180f && p.y < 320f)
                s += "   (OoT " + ((p.x + 1750f) / 0.033f).ToString("F0", ci) + ", " + ((p.y - 261.98f) / 0.033f).ToString("F0", ci) + ", " + (-(p.z + 1050f) / 0.033f).ToString("F0", ci) + ")";
            return s;
        }

        // ---------- Anzeige ----------
        static void Styles()
        {
            if (box != null) return;
            box = new GUIStyle(GUI.skin.box) { padding = new RectOffset(12, 12, 10, 10) };
            lab = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true }; lab.normal.textColor = Color.white;
            head = new GUIStyle(lab) { fontSize = 16, fontStyle = FontStyle.Bold }; head.normal.textColor = new Color(1f, 0.85f, 0.3f);
            small = new GUIStyle(lab) { fontSize = 12 }; small.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            btn = new GUIStyle(GUI.skin.button) { fontSize = 13, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(8, 6, 4, 4) };
            btnOn = new GUIStyle(btn) { fontStyle = FontStyle.Bold }; btnOn.normal.textColor = new Color(0.5f, 1f, 0.5f); btnOn.hover.textColor = btnOn.normal.textColor;
        }

        static bool Toggle(bool v, string label) { return GUILayout.Button((v ? "[x] " : "[ ] ") + label, v ? btnOn : btn) ? !v : v; }

        public static void Gui()
        {
            if (!OotPlay.On) return;
            Styles();
            if (toast != null && Time.unscaledTime - toastT < 3f && !OotHud.Hidden)
            {
                var r = new Rect(Screen.width * 0.5f - 420f, Screen.height - 70f, 840f, 34f);
                GUI.Box(r, ""); GUI.Label(new Rect(r.x + 10f, r.y + 7f, r.width - 20f, 24f), toast, small);
            }
            if (!Open) return;
            if (mine == null) LoadMine();
            float w = 460f, h = Mathf.Min(Screen.height - 40f, 860f);
            if (bg == null) { bg = new Texture2D(1, 1); bg.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.06f, 0.92f)); bg.Apply(); bg.hideFlags = HideFlags.HideAndDontSave; }
            GUI.DrawTexture(new Rect(20f, 20f, w, h), bg);            // dunkel hinterlegt (Herzen/HUD scheinen sonst durch)
            GUILayout.BeginArea(new Rect(20f, 20f, w, h), box);
            GUILayout.Label("Dev-Werkzeuge (F9)", head);
            GUILayout.Label("F10 freie Kamera  ·  F11 Anzeige an/aus  ·  F7 Stimmung  ·  F8 Cutscenes", small);
            string tp; GUILayout.Label(PosText(out tp), small);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Position kopieren", btn)) { GUIUtility.systemCopyBuffer = tp; Toast("Kopiert: " + tp); }
            newName = GUILayout.TextField(newName, GUILayout.Width(150f));
            if (GUILayout.Button("Hier merken", btn))
            {
                var pl = Player.m_localPlayer;
                if (pl != null)
                {
                    if (mine == null) LoadMine();
                    string n = newName.Trim().Length > 0 ? newName.Trim() : "Punkt " + DateTime.Now.ToString("HH:mm:ss");
                    mine.Add(new Spot(n, pl.transform.position, pl.transform.eulerAngles.y)); SaveMine(); newName = ""; Toast("Gemerkt: " + n);
                }
            }
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label("Schalter", head);
            GUILayout.BeginHorizontal();
            God = Toggle(God, "Unverwundbar");
            bool hud = Toggle(!OotHud.Hidden, "Anzeige"); OotHud.Hidden = !hud;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool fc = Toggle(FreeCam, "Freie Kamera"); if (fc != FreeCam) SetFreeCam(fc);
            NoFog = Toggle(NoFog, "Nebel aus");
            OotAtmos.Enabled = Toggle(OotAtmos.Enabled, "Teilchen");
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tempo", lab, GUILayout.Width(60f));
            foreach (var s in new[] { 0.25f, 0.5f, 1f, 2f })
                if (GUILayout.Button(s.ToString("0.##", CultureInfo.InvariantCulture) + "x", Mathf.Approximately(Slow, s) ? btnOn : btn)) { Slow = s; Time.timeScale = s; }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fahrt: " + keys.Count + " Punkte, " + SegSec.ToString("F0") + " s je Abschnitt", lab, GUILayout.Width(250f));
            if (GUILayout.Button("-", btn, GUILayout.Width(28f))) SegSec = Mathf.Max(1f, SegSec - 1f);
            if (GUILayout.Button("+", btn, GUILayout.Width(28f))) SegSec = Mathf.Min(20f, SegSec + 1f);
            if (GUILayout.Button("Loeschen", btn)) { keys.Clear(); Toast("Kamerapunkte geloescht"); }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(flying ? "Fahrt stoppen (P)" : "Fahrt abspielen (P)", btn)) { if (flying) StopFly(); else PlayFly(); }
            GUILayout.EndHorizontal();

            GUILayout.Label("Aktionen", head);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Alle Items", btn)) GiveAll();
            if (GUILayout.Button("Herzen voll", btn)) { OotCombat.Hp = OotCombat.MaxHp; Toast("Herzen voll"); }
            if (GUILayout.Button("Raetsel zuruecksetzen", btn)) Toast(R("oot", "obj", "reset"));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Deku-Baum neu", btn)) ResetYdan();
            if (GUILayout.Button("Ganzes Spiel von vorn", btn)) ResetGame();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Startbildschirm", btn)) { Open = false; OotTitle.Show(false); }
            if (GUILayout.Button("Neues Spiel: Start + Intro", btn)) { Open = false; ResetGame(); OotTitle.Show(true); }
            GUILayout.EndHorizontal();

            if (mine.Count > 0)
            {
                GUILayout.Label("Eigene Punkte", head);
                for (int i = 0; i < mine.Count; i++)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(mine[i].Name, btn)) Teleport(mine[i]);
                    if (GUILayout.Button("x", btn, GUILayout.Width(28f))) { mine.RemoveAt(i); SaveMine(); GUILayout.EndHorizontal(); break; }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.Label("Kokiri-Wald", head);
            foreach (var s in Kf) if (GUILayout.Button(s.Name, btn)) Teleport(s);
            GUILayout.Label("Deku-Baum", head);
            foreach (var s in Ydan) if (GUILayout.Button(s.Name, btn)) Teleport(s);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
