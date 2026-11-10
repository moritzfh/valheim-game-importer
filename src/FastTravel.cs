using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Schnellreise wie im Original: an einem Wegweiser [N] druecken -> Liste aller Wegweiser (nach Inseln), Klick = hinreisen.
    // places.json: {"id": "ft_kaer_trolde_harbor", "name": {...}, "pos": [x, z], "travel": {"tp": [x, y, z], "yaw": 90, "group": {"de": "Ard Skellig"}}}
    // Alle Wegweiser stehen als Markierung auf der Karte. pack.json "witcher": {"fast_travel_anywhere": true} erlaubt die Reise ueberall.
    public static class FastTravel
    {
        public class Point { public string Id; public object Name, Group; public Vector3 Sign, Tp; public float Yaw; }
        static readonly List<Point> pts = new List<Point>();
        static Pack forPack; static bool anywhere;
        public static bool Open;
        static Point near; static Vector2 scroll;
        static readonly List<object> pins = new List<object>();
        static float pinT;

        static void Setup(Pack pk)
        {
            forPack = pk; pts.Clear(); Open = false; ClearPins();
            if (pk == null) return;
            try
            {
                var pl = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "places.json"))) as List<object>;
                if (pl != null) foreach (var o in pl)
                    {
                        var tr = Json.O(o, "travel"); var p = Json.L(o, "pos"); var tp = Json.L(tr, "tp");
                        if (tr == null || p == null || tp == null || tp.Count < 3) continue;
                        pts.Add(new Point { Id = Json.S(o, "id"), Name = Json.V(o, "name"), Group = Json.V(tr, "group"),
                                            Sign = new Vector3(N(p[0]), 0f, N(p[p.Count - 1])), Tp = new Vector3(N(tp[0]), N(tp[1]), N(tp[2])), Yaw = Json.F(tr, "yaw") });
                    }
                var meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json")));
                anywhere = Json.B(Json.V(meta, "witcher"), "fast_travel_anywhere");
            }
            catch (Exception e) { Plugin.Quiet(e, "FastTravel.Setup"); }
            if (pts.Count > 0) Plugin.Log("Schnellreise: " + pts.Count + " Wegweiser");
        }

        static float N(object o) { return o is double ? (float)(double)o : 0f; }

        public static void Tick()
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            var p = Player.m_localPlayer;
            if (p == null || pts.Count == 0) { Open = false; return; }
            near = null; float best = 7f;
            foreach (var t in pts) { float d = Story.Flat(p.transform.position, t.Sign); if (d < best) { best = d; near = t; } }
            // wie im Original: am Wegweiser E (wenn sonst nichts zum Benutzen da ist), ausserdem N
            bool eSign = W3Controls.On && near != null && !Open && Input.GetKeyDown(KeyCode.E) && Horse.FreeInput() && p.GetHoverObject() == null;
            if (eSign || Input.GetKeyDown(KeyCode.N) && (Open || Horse.FreeInput()))
            {
                if (Open) Open = false;
                else if (near != null || anywhere) Open = true;
                else Story.Msg(Story.L("Schnellreise nur an Wegweisern (Karte: Portal-Zeichen)", "Fast travel only at signposts (map: portal icon)"));
            }
            if (Open && Input.GetKeyDown(KeyCode.Escape)) Open = false;
            if (Open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Time.time > pinT) { pinT = Time.time + 5f; if (pins.Count == 0) AddPins(); }
        }

        public static string Travel(string id)
        {
            var t = pts.Find(x => x.Id == id || x.Id == "ft_" + id);
            if (t == null) return "unbekannt: " + id;
            Go(t); return "reise nach " + Pack.T(t.Name);
        }

        public static string List() { var s = "wegweiser " + pts.Count + ":"; foreach (var t in pts) s += " " + t.Id; return s; }

        static void Go(Point t)
        {
            var p = Player.m_localPlayer; if (p == null) return;
            Open = false;
            var pos = t.Tp; float h;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out h) && h > pos.y - 3f) pos.y = h + 0.2f;
            try { p.TeleportTo(pos + Vector3.up * 0.5f, Quaternion.Euler(0f, t.Yaw, 0f), true); } catch (Exception e) { Plugin.Warn("Schnellreise: " + e.Message); }
            Story.Msg(Pack.T(t.Name));
        }

        // ---------- Karte ----------
        static System.Reflection.MethodInfo addPin, removePin;
        static void AddPins()
        {
            if (Minimap.instance == null) return;
            try
            {
                if (addPin == null) addPin = AccessTools.Method(typeof(Minimap), "AddPin");
                var ps = addPin.GetParameters();
                foreach (var t in pts)
                {
                    var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                    args[0] = new Vector3(t.Sign.x, 0f, t.Sign.z); args[1] = Minimap.PinType.Icon4; args[2] = Pack.T(t.Name);
                    for (int i = 3; i < ps.Length; i++) if (ps[i].ParameterType == typeof(bool)) args[i] = false;
                    pins.Add(addPin.Invoke(Minimap.instance, args));
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "FastTravel.Pins"); }
        }

        static void ClearPins()
        {
            try
            {
                if (Minimap.instance != null && pins.Count > 0)
                {
                    if (removePin == null) removePin = AccessTools.Method(typeof(Minimap), "RemovePin", new[] { typeof(Minimap.PinData) });
                    foreach (var p in pins) if (p != null) removePin.Invoke(Minimap.instance, new[] { p });
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "FastTravel.ClearPins"); }
            pins.Clear();
        }

        // ---------- Anzeige ----------
        static GUIStyle title, head, btn, hint;
        static readonly Color gold = new Color(0.86f, 0.74f, 0.47f);

        public static void Gui()
        {
            if (Player.m_localPlayer == null || pts.Count == 0) return;
            float s = Mathf.Clamp(Screen.height / 1080f, 0.7f, 2f);
            if (title == null || title.fontSize != Mathf.RoundToInt(30 * s))
            {
                title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(30 * s), fontStyle = FontStyle.Bold, normal = { textColor = gold } };
                head = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(19 * s), fontStyle = FontStyle.Bold, normal = { textColor = gold } };
                btn = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(17 * s), alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.92f, 0.88f, 0.78f) } };
                hint = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(18 * s), normal = { textColor = new Color(0.92f, 0.88f, 0.78f) } };
            }
            if (!Open)
            {
                if (near != null && !StoryUi.Busy)
                    GUI.Label(new Rect(Screen.width / 2f - 200 * s, Screen.height * 0.62f, 400 * s, 34 * s), "<color=#dcbc78>" + (W3Controls.On ? "[E]" : "[N]") + "</color> " + Story.L("Schnellreise", "Fast travel") + " – " + Pack.T(near.Name), hint);
                return;
            }
            float w = 560 * s, h = Screen.height * 0.8f, x = (Screen.width - w) / 2f, y = Screen.height * 0.1f;
            GUI.color = new Color(0.05f, 0.045f, 0.04f, 0.88f); GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture); GUI.color = Color.white;
            GUILayout.BeginArea(new Rect(x + 20 * s, y + 14 * s, w - 40 * s, h - 28 * s));
            GUILayout.Label(Story.L("Schnellreise", "Fast Travel"), title);
            scroll = GUILayout.BeginScrollView(scroll);
            string last = null;
            foreach (var t in Sorted())
            {
                var g = Pack.T(t.Group);
                if (g != last) { GUILayout.Space(8 * s); GUILayout.Label(g, head); last = g; }
                if (GUILayout.Button((t == near ? "● " : "   ") + Pack.T(t.Name), btn, GUILayout.Height(30 * s)) && t != near) Go(t);
            }
            GUILayout.EndScrollView();
            GUILayout.Label("<color=#8a7f6a>[N] / [Esc] " + Story.L("schließen", "close") + "</color>", head);
            GUILayout.EndArea();
        }

        static List<Point> Sorted()
        {
            var l = new List<Point>(pts);
            l.Sort((a, b) => { int c = string.Compare(Pack.T(a.Group), Pack.T(b.Group), StringComparison.Ordinal); return c != 0 ? c : string.Compare(Pack.T(a.Name), Pack.T(b.Name), StringComparison.Ordinal); });
            return l;
        }
    }
}
