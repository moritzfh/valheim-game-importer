using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Spielstand der Geschichte: Flags, Quest-Stufen, Abschuesse. Pro Paket + Welt + Charakter in
    // BepInEx/config/GameImporter/state/. Alles laeuft lokal bei jedem Spieler (gleiches Paket -> gleiche Welt).
    public static class Story
    {
        public static readonly HashSet<string> Flags = new HashSet<string>();
        public static readonly Dictionary<string, string> Quests = new Dictionary<string, string>();   // id -> Stufe oder "done"
        public static readonly Dictionary<string, int> Kills = new Dictionary<string, int>();
        public static readonly HashSet<string> SeenCutscenes = new HashSet<string>();
        public static string Tracked;          // zuletzt geaenderte Quest (fuer die Anzeige oben rechts)
        static string statePath, loadedFor;
        static float tickT;

        static Pack P { get { return Pack.Current; } }

        // ---------- Laden / Speichern ----------
        public static void EnsureLoaded()
        {
            if (P == null || Player.m_localPlayer == null || ZNet.instance == null || Game.instance == null) return;
            string ch = "player";
            try { ch = Game.instance.GetPlayerProfile().GetName(); } catch (Exception e) { Plugin.Quiet(e, "Story.char"); }
            var key = P.Id + "__" + ZNet.instance.GetWorldName() + "__" + ch;
            if (key == loadedFor) return;
            loadedFor = key;
            Flags.Clear(); Quests.Clear(); Kills.Clear(); SeenCutscenes.Clear(); Tracked = null;
            var dir = Path.Combine(Paths.ConfigPath, "GameImporter", "state");
            statePath = Path.Combine(dir, Safe(key) + ".json");
            try
            {
                if (File.Exists(statePath))
                {
                    var o = Json.Parse(File.ReadAllText(statePath));
                    var f = Json.L(o, "flags"); if (f != null) foreach (var x in f) Flags.Add(x.ToString());
                    var q = Json.O(o, "quests"); if (q != null) foreach (var kv in q) Quests[kv.Key] = kv.Value.ToString();
                    var k = Json.O(o, "kills"); if (k != null) foreach (var kv in k) Kills[kv.Key] = (int)(double)kv.Value;
                    var c = Json.L(o, "cutscenes"); if (c != null) foreach (var x in c) SeenCutscenes.Add(x.ToString());
                    Tracked = Json.S(o, "tracked");
                }
                Plugin.Log("Geschichte: Stand " + statePath + " (" + Quests.Count + " Quests, " + Flags.Count + " Flags)");
            }
            catch (Exception e) { Plugin.Warn("Geschichte: Stand nicht lesbar: " + e.Message); }
        }

        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        // Hat dieser Charakter in dieser Welt das Start-Intro schon gesehen? (geht schon vor dem Spawnen, liest die Datei direkt)
        // Paket ohne Start-Intro: true (dann gilt Valheims normaler Abmeldepunkt)
        public static bool StartSeen(string ch)
        {
            try
            {
                if (P == null || ZNet.instance == null) return true;
                var starts = new List<string>();
                foreach (var kv in P.Cutscenes) if (Json.B(Json.V(kv.Value, "trigger"), "start")) starts.Add(kv.Key);
                if (starts.Count == 0) return true;
                var path = Path.Combine(Paths.ConfigPath, "GameImporter", "state", Safe(P.Id + "__" + ZNet.instance.GetWorldName() + "__" + ch) + ".json");
                if (!File.Exists(path)) return false;
                var c = Json.L(Json.Parse(File.ReadAllText(path)), "cutscenes");
                if (c != null) foreach (var x in c) if (starts.Contains(x.ToString())) return true;
                return false;
            }
            catch (Exception e) { Plugin.Quiet(e, "Story.StartSeen"); return true; }
        }

        public static void Save()
        {
            if (statePath == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                var o = new Dictionary<string, object>
                {
                    { "flags", new List<object>(Flags) },
                    { "quests", ToObj(Quests) },
                    { "kills", ToObj(Kills) },
                    { "cutscenes", new List<object>(SeenCutscenes) },
                    { "tracked", Tracked }
                };
                File.WriteAllText(statePath, Json.Write(o));
            }
            catch (Exception e) { Plugin.Warn("Geschichte: Speichern fehlgeschlagen: " + e.Message); }
        }

        static Dictionary<string, object> ToObj<T>(Dictionary<string, T> d) { var r = new Dictionary<string, object>(); foreach (var kv in d) r[kv.Key] = kv.Value; return r; }

        public static void Reset() { Flags.Clear(); Quests.Clear(); Kills.Clear(); SeenCutscenes.Clear(); Tracked = null; Save(); }

        // ---------- Bedingungen ----------
        public static bool Check(List<object> conds)
        {
            if (conds == null) return true;
            foreach (var c in conds) if (c != null && !Cond(c.ToString())) return false;
            return true;
        }

        public static bool Cond(string c)
        {
            bool neg = c.StartsWith("!");
            if (neg) c = c.Substring(1);
            bool r;
            if (c.StartsWith("has:"))
            {
                var a = c.Split(':');
                int n = a.Length > 2 ? int.Parse(a[2]) : 1;
                r = CountItem(a[1]) >= n;
            }
            else if (c.Contains("@"))
            {
                int i = c.IndexOf('@');
                string q = c.Substring(0, i), st = c.Substring(i + 1), cur;
                bool has = Quests.TryGetValue(q, out cur);
                if (st == "active") r = has && cur != "done";
                else if (st == "done") r = has && cur == "done";
                else r = has && cur == st;
            }
            else r = Flags.Contains(c);
            return neg ? !r : r;
        }

        // ---------- Aktionen ----------
        public static void Do(List<object> acts)
        {
            if (acts == null) return;
            foreach (var a in acts)
            {
                try { Act(a); } catch (Exception e) { Plugin.Warn("Geschichte: Aktion " + Json.Write(a) + ": " + e.Message); }
            }
        }

        static void Act(object a)
        {
            if (Json.Has(a, "set")) { Flags.Add(Json.S(a, "set")); Save(); }
            else if (Json.Has(a, "clear")) { Flags.Remove(Json.S(a, "clear")); Save(); }
            else if (Json.Has(a, "quest"))
            {
                var q = Json.S(a, "quest"); var st = Json.S(a, "stage");
                if (st == null) StartQuest(q); else SetStage(q, st);
            }
            else if (Json.Has(a, "give")) Give(Json.S(a, "give"), (int)Json.F(a, "count", 1f));
            else if (Json.Has(a, "take")) Take(Json.S(a, "take"), (int)Json.F(a, "count", 1f));
            else if (Json.Has(a, "cutscene")) StoryUi.PlayCutscene(Json.S(a, "cutscene"));
            else if (Json.Has(a, "say")) Npcs.Say(Json.S(a, "say"), Pack.T(Json.V(a, "text")));
            else if (Json.Has(a, "time")) SetDayTime(Json.S(a, "time"));
            else if (Json.Has(a, "talk")) StoryUi.StartDialogue(Json.S(a, "talk"));
            else if (Json.Has(a, "comment")) StoryUi.PlayComment(Json.S(a, "comment"));
            else if (Json.Has(a, "msg")) Interact.Say(Pack.T(Json.V(a, "msg")), Json.F(a, "secs", 3f));
            else if (Json.Has(a, "door"))
            {
                // {"door": "<tag>", "open": true|false}
                bool open = !Json.Has(a, "open") || Json.B(a, "open");
                foreach (var d in Door.Live) if (d != null && d.HasTag(Json.S(a, "door")) && d.IsOpen != open && Player.m_localPlayer != null) d.Toggle(Player.m_localPlayer.transform.position);
            }
            else if (Json.Has(a, "weather"))
            {
                // Wetter festhalten (Valheim-Umgebung, z. B. "Clear"); "" = wieder normales Wetter. Wird mit dem Stand gespeichert.
                Flags.RemoveWhere(x => x.StartsWith("weather:"));
                var w = Json.S(a, "weather", "");
                if (w != "") Flags.Add("weather:" + w);
                Save(); ApplyWeather();
            }
            else if (Json.Has(a, "warp"))
            {
                // {"warp": [x, y, z], "yaw": 180}: Ortswechsel wie eine Tuer/ein Ausgang in OoT (Szenenwechsel)
                Vector3 c; var pl = Player.m_localPlayer;
                if (pl != null && P.Pos(Json.V(a, "warp"), out c))
                {
                    float yw = Json.F(a, "yaw", pl.transform.eulerAngles.y);
                    if (!OotPlay.WarpTo(c, yw)) pl.TeleportTo(c + Vector3.up * 0.2f, Quaternion.Euler(0f, yw, 0f), true);
                }
            }
            else if (Json.Has(a, "spawn")) { Vector3 c; if (!P.Pos(Json.V(a, "pos"), out c)) c = Player.m_localPlayer.transform.position + Player.m_localPlayer.transform.forward * 10f; Spawn(Json.S(a, "spawn"), (int)Json.F(a, "count", 1f), c, Json.F(a, "radius", 4f), (int)Json.F(a, "level", 1f)); }
        }

        // Uhrzeit vorstellen ("13:00" oder Tagesanteil 0..1); nur der Server darf die Zeit setzen. Geht immer vorwaerts.
        public static void SetDayTime(string s)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || EnvMan.instance == null || string.IsNullOrEmpty(s)) return;
            double frac;
            var hm = s.Split(':');
            if (hm.Length == 2) frac = (int.Parse(hm[0]) + int.Parse(hm[1]) / 60.0) / 24.0;
            else frac = double.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
            double len = Convert.ToDouble(Traverse.Create(EnvMan.instance).Field("m_dayLengthSec").GetValue());
            double t = ZNet.instance.GetTimeSeconds();
            double target = Math.Floor(t / len) * len + frac * len;
            if (target < t) target += len;
            ZNet.instance.SetNetTime(target);
            Plugin.Log("Geschichte: Uhrzeit " + s + " (Tag " + Math.Floor(target / len) + ")");
        }

        static string weatherOn; static EnvMan weatherEnv;
        public static void ApplyWeather()
        {
            if (EnvMan.instance == null) return;
            if (EnvMan.instance != weatherEnv) { weatherEnv = EnvMan.instance; weatherOn = null; }
            string w = "";
            foreach (var f in Flags) if (f.StartsWith("weather:")) w = f.Substring(8);
            if (w == weatherOn) return;
            weatherOn = w;
            try { EnvMan.instance.SetForceEnvironment(w); Plugin.Log("Geschichte: Wetter " + (w == "" ? "normal" : w)); }
            catch (Exception e) { Plugin.Quiet(e, "Story.Weather"); }
        }

        // ---------- Quests ----------
        public static void StartQuest(string id)
        {
            Pack.Quest q;
            if (P == null || !P.Quests.TryGetValue(id, out q)) { Plugin.Warn("Quest " + id + " gibt's nicht"); return; }
            if (Quests.ContainsKey(id)) return;
            Quests[id] = q.Stages[0].Id; Tracked = id; Save();
            Msg(L("Neue Quest", "New quest") + ": " + Pack.T(q.Title));
            EnterStage(q, q.Stages[0]);
            OnStage(q, q.Stages[0].Id);
        }

        public static void SetStage(string id, string stage)
        {
            Pack.Quest q;
            if (P == null || !P.Quests.TryGetValue(id, out q)) return;
            if (stage == "done") { Complete(q); return; }
            Quests[id] = stage; Tracked = id; Save();
            var st = q.Stages.Find(s => s.Id == stage);
            if (st != null) Msg(Pack.T(q.Title) + ": " + Pack.T(st.Log));
            EnterStage(q, st);
            OnStage(q, stage);
        }

        static void Complete(Pack.Quest q)
        {
            string cur;
            if (Quests.TryGetValue(q.Id, out cur) && cur == "done") return;
            Quests[q.Id] = "done"; if (Tracked == q.Id) Tracked = null; Save();
            Msg(L("Quest abgeschlossen", "Quest complete") + ": " + Pack.T(q.Title));
            Do(q.Rewards);
            OnStage(q, "done");
        }

        // naechste Stufe (oder fertig)
        public static void Advance(Pack.Quest q, Pack.Stage st)
        {
            if (!string.IsNullOrEmpty(st.Next)) { SetStage(q.Id, st.Next); return; }
            int i = q.Stages.IndexOf(st);
            if (i >= 0 && i + 1 < q.Stages.Count) SetStage(q.Id, q.Stages[i + 1].Id); else Complete(q);
        }

        public static Pack.Stage Current(Pack.Quest q) { string cur; return Quests.TryGetValue(q.Id, out cur) ? q.Stages.Find(s => s.Id == cur) : null; }

        // Beim Betreten einer Stufe: Toetungen ab jetzt zaehlen, Aktionen der Stufe ausfuehren
        static void EnterStage(Pack.Quest q, Pack.Stage st)
        {
            if (st == null) return;
            if (st.Goal != null && Json.S(st.Goal, "type") == "kill")
            {
                int k; Kills.TryGetValue(Json.S(st.Goal, "creature", ""), out k);
                Kills["@" + q.Id + "/" + st.Id] = k; Save();
            }
            Do(st.Do);
        }

        public static int KillsFor(Pack.Quest q, Pack.Stage st)
        {
            int k, b; Kills.TryGetValue(Json.S(st.Goal, "creature", ""), out k);
            if (!Kills.TryGetValue("@" + q.Id + "/" + st.Id, out b)) b = 0;
            return Mathf.Max(0, k - b);
        }

        // Cutscenes, die an einer Quest-Stufe haengen
        static void OnStage(Pack.Quest q, string stage)
        {
            foreach (var kv in P.Cutscenes)
            {
                var tr = Json.O(kv.Value, "trigger");
                if (tr == null || Json.S(tr, "quest") != q.Id || Json.S(tr, "stage", q.Stages[0].Id) != stage) continue;
                StoryUi.PlayCutscene(kv.Key);
            }
        }

        // Dialog mit einer Figur ist zu Ende: Ziel "talk" erfuellt?
        public static void OnTalked(string npc, Dictionary<string, string> before)
        {
            if (P == null) return;
            foreach (var q in P.QuestOrder)
            {
                var st = Current(q);
                if (st == null || st.Goal == null || Json.S(st.Goal, "type") != "talk" || Json.S(st.Goal, "npc") != npc) continue;
                string b; before.TryGetValue(q.Id, out b);
                if (b != st.Id) continue;          // die Stufe hat sich im Gespraech gerade erst geaendert
                Advance(q, st);
            }
        }

        public static void Tick()
        {
            if (P == null || Player.m_localPlayer == null || Time.time < tickT) return;
            tickT = Time.time + 1f;
            EnsureLoaded();
            ApplyWeather();
            var me = Player.m_localPlayer.transform.position;
            foreach (var q in P.QuestOrder)
            {
                var st = Current(q);
                if (st == null || st.Goal == null || st.TrackOnly) continue;
                switch (Json.S(st.Goal, "type"))
                {
                    case "goto":
                        Vector3 p; float r = Json.F(st.Goal, "radius", 25f);
                        Pack.Place pl;
                        if (Json.Has(st.Goal, "place") && P.Places.TryGetValue(Json.S(st.Goal, "place"), out pl)) { p = new Vector3(pl.X, 0f, pl.Z); if (!Json.Has(st.Goal, "radius")) r = pl.Radius; }
                        else if (!P.Pos(Json.V(st.Goal, "pos"), out p)) break;
                        // mit Hoehe ([x, y, z]): auch das Stockwerk muss stimmen ("Begib dich auf die untere Etage")
                        if (Flat(me, p) < r && (float.IsNaN(p.y) || Mathf.Abs(me.y - p.y) < Json.F(st.Goal, "height", 3f))) Advance(q, st);
                        break;
                    case "use":
                        if (Flags.Contains("used:" + Json.S(st.Goal, "spot"))) Advance(q, st);
                        break;
                    case "collect":
                        if (CountItem(Json.S(st.Goal, "item")) >= (int)Json.F(st.Goal, "count", 1f)) Advance(q, st);
                        break;
                    case "kill":
                        SpawnForGoal(q, st, me);
                        if (KillsFor(q, st) >= (int)Json.F(st.Goal, "count", 1f)) Advance(q, st);
                        break;
                }
            }
            UpdatePin();
            // Cutscenes, die an einem Ort ausgeloest werden
            foreach (var kv in P.Cutscenes)
            {
                var tr = Json.O(kv.Value, "trigger"); Pack.Place pl;
                if (tr == null || !Json.Has(tr, "place") || SeenCutscenes.Contains(kv.Key) || !P.Places.TryGetValue(Json.S(tr, "place"), out pl)) continue;
                if (!Check(Json.L(tr, "if"))) continue;
                if (Flat(me, new Vector3(pl.X, 0f, pl.Z)) < Json.F(tr, "radius", pl.Radius)) StoryUi.PlayCutscene(kv.Key);
            }
        }

        // ---------- Gegner fuer Ziele vom Typ "kill" ----------
        // goal: {"type": "kill", "creature": "Greydwarf_Elite", "count": 1, "pos": [x, z], "spawn": [{"prefab": "Greydwarf_Elite", "count": 1, "level": 2, "radius": 3}]}
        // Erscheinen, sobald der Spieler naeher als 70 m ist (vorher ist die Gegend nicht geladen); einmal pro Stufe (Flag).
        static void SpawnForGoal(Pack.Quest q, Pack.Stage st, Vector3 me)
        {
            var sp = Json.L(st.Goal, "spawn");
            if (sp == null) return;
            var flag = "@spawned:" + q.Id + "/" + st.Id;
            if (Flags.Contains(flag)) return;
            Vector3 c;
            if (!P.Pos(Json.V(st.Goal, "pos"), out c) || Flat(me, c) > 70f) return;
            Flags.Add(flag); Save();
            foreach (var e in sp) Spawn(Json.S(e, "prefab", Json.S(st.Goal, "creature")), (int)Json.F(e, "count", 1f), c, Json.F(e, "radius", 5f), (int)Json.F(e, "level", 1f));
        }

        public static void Spawn(string prefab, int count, Vector3 c, float radius, int level)
        {
            if (ZNetScene.instance == null || string.IsNullOrEmpty(prefab)) return;
            var pf = ZNetScene.instance.GetPrefab(prefab);
            if (pf == null) { Plugin.Warn("Spawn: " + prefab + " gibt's nicht"); return; }
            for (int i = 0; i < count; i++)
            {
                var off = UnityEngine.Random.insideUnitCircle * radius;
                var p = new Vector3(c.x + off.x, c.y, c.z + off.y);
                float h;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out h)) p.y = h + 0.3f;
                else if (float.IsNaN(p.y)) p.y = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position.y + 1f : 40f;
                var go = UnityEngine.Object.Instantiate(pf, p, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                var ch = go.GetComponent<Character>();
                if (ch != null && level > 1) ch.SetLevel(level);
                try { var nv = go.GetComponent<ZNetView>(); if (nv != null && nv.GetZDO() != null) nv.GetZDO().Set("gi_quest", true); } catch (Exception e) { Plugin.Quiet(e, "Story.Spawn.tag"); }
            }
            Plugin.Log("Spawn: " + count + "x " + prefab + " bei " + c);
        }

        // ---------- Karten-Markierung fuer das aktuelle Ziel ----------
        static object pin; static string pinKey;
        static System.Reflection.MethodInfo addPin, removePin;

        static bool Target(Pack.Quest q, Pack.Stage st, out Vector3 v)
        {
            v = Vector3.zero;
            if (st.Pin != null && P.Pos(st.Pin, out v)) return true;
            if (st.Goal == null) return false;
            if (Json.Has(st.Goal, "spot")) return Interact.SpotPos(Json.S(st.Goal, "spot"), out v);
            if (Json.Has(st.Goal, "npc"))
            {
                var go = Npcs.Get(Json.S(st.Goal, "npc"));
                if (go != null) { v = go.transform.position; return true; }
                Pack.Npc n; return P.NpcById.TryGetValue(Json.S(st.Goal, "npc", ""), out n) && P.Pos(n.Pos, out v);
            }
            Pack.Place pl;
            if (Json.Has(st.Goal, "place") && P.Places.TryGetValue(Json.S(st.Goal, "place"), out pl)) { v = new Vector3(pl.X, 0f, pl.Z); return true; }
            return P.Pos(Json.V(st.Goal, "pos"), out v);
        }

        // aktuelles Ziel der verfolgten Quest (fuer Hexersinne)
        public static bool CurrentTarget(out Vector3 v)
        {
            v = Vector3.zero;
            if (P == null) return false;
            Pack.Quest q = null;
            if (Tracked != null && P.Quests.ContainsKey(Tracked) && Cond(Tracked + "@active")) q = P.Quests[Tracked];
            if (q == null) foreach (var x in P.QuestOrder) if (Cond(x.Id + "@active")) { q = x; break; }
            var st = q != null ? Current(q) : null;
            if (st == null || !Target(q, st, out v)) return false;
            float h;
            if (Json.Has(st.Goal, "spot")) return true;           // genau an der Stelle (Versteck, Hinweis)
            if (float.IsNaN(v.y) || v.y == 0f) v.y = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(v, out h) ? h : 30f;
            v.y += 1.5f;
            return true;
        }

        static void UpdatePin()
        {
            try
            {
                if (Minimap.instance == null) return;
                Pack.Quest q = null;
                if (Tracked != null && P.Quests.ContainsKey(Tracked) && Cond(Tracked + "@active")) q = P.Quests[Tracked];
                if (q == null) foreach (var x in P.QuestOrder) if (Cond(x.Id + "@active")) { q = x; break; }
                var st = q != null ? Current(q) : null;
                Vector3 v = Vector3.zero;
                bool has = st != null && Target(q, st, out v);
                var key = has ? q.Id + "/" + st.Id + "/" + Mathf.RoundToInt(v.x / 4f) + "/" + Mathf.RoundToInt(v.z / 4f) : null;
                if (key == pinKey) return;
                if (removePin == null) removePin = AccessTools.Method(typeof(Minimap), "RemovePin", new[] { typeof(Minimap.PinData) });
                if (pin != null && removePin != null) removePin.Invoke(Minimap.instance, new[] { pin });
                pin = null; pinKey = key;
                if (!has) return;
                if (float.IsNaN(v.y)) v.y = 0f;
                if (addPin == null) addPin = AccessTools.Method(typeof(Minimap), "AddPin");
                var ps = addPin.GetParameters(); var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                args[0] = v; args[1] = Minimap.PinType.Icon3; args[2] = Pack.T(q.Title);
                for (int i = 3; i < ps.Length; i++) if (ps[i].ParameterType == typeof(bool)) args[i] = false;
                pin = addPin.Invoke(Minimap.instance, args);
            }
            catch (Exception e) { Plugin.Quiet(e, "Story.Pin"); }
        }

        public static float Flat(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        // Fortschritt fuer die Anzeige (z. B. "3/5")
        public static string Progress(Pack.Stage st) { return Progress(null, st); }
        public static string Progress(Pack.Quest q, Pack.Stage st)
        {
            if (st == null || st.Goal == null) return "";
            var t = Json.S(st.Goal, "type");
            int n = (int)Json.F(st.Goal, "count", 1f);
            if (t == "collect") return " (" + Mathf.Min(CountItem(Json.S(st.Goal, "item")), n) + "/" + n + ")";
            if (t == "kill") { int k; if (q != null) k = KillsFor(q, st); else Kills.TryGetValue(Json.S(st.Goal, "creature", ""), out k); return " (" + Mathf.Min(k, n) + "/" + n + ")"; }
            return "";
        }

        // ---------- Gegenstaende ----------
        static string SharedName(string prefab)
        {
            var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            var id = go != null ? go.GetComponent<ItemDrop>() : null;
            return id != null ? id.m_itemData.m_shared.m_name : null;
        }

        public static int CountItem(string prefab)
        {
            var pl = Player.m_localPlayer; var n = SharedName(prefab);
            if (pl == null || n == null) return 0;
            return pl.GetInventory().CountItems(n, -1, true);
        }

        static void Give(string prefab, int count)
        {
            var pl = Player.m_localPlayer; var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefab) : null;
            if (pl == null || go == null) { Plugin.Warn("give: " + prefab + " unbekannt"); return; }
            if (!pl.GetInventory().AddItem(go, count))
            {
                // Inventar voll: vor die Fuesse legen
                var d = UnityEngine.Object.Instantiate(go, pl.transform.position + pl.transform.forward + Vector3.up, Quaternion.identity).GetComponent<ItemDrop>();
                if (d != null) d.m_itemData.m_stack = count;
            }
            Msg("+" + count + " " + Localization.instance.Localize(SharedName(prefab) ?? prefab));
        }

        static void Take(string prefab, int count)
        {
            var pl = Player.m_localPlayer; var n = SharedName(prefab);
            if (pl == null || n == null) return;
            pl.GetInventory().RemoveItem(n, count, -1, true);
        }

        // ---------- Abschuesse ----------
        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(Character), "OnDeath");
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(Story), nameof(DeathPost)));
            }
            catch (Exception e) { Plugin.Warn("Geschichte-Patch: " + e.Message); }
        }

        static void DeathPost(Character __instance)
        {
            try
            {
                if (P == null || __instance == null || __instance.IsPlayer() || Player.m_localPlayer == null) return;
                if (Vector3.Distance(__instance.transform.position, Player.m_localPlayer.transform.position) > 50f) return;
                var name = Utils.GetPrefabName(__instance.gameObject);
                int k; Kills.TryGetValue(name, out k); Kills[name] = k + 1; Save();
            }
            catch (Exception e) { Plugin.Quiet(e, "Story.Death"); }
        }

        // ---------- Kleinkram ----------
        public static string L(string de, string en) { return Pack.Lang == "de" ? de : en; }
        public static void Msg(string s) { try { if (Player.m_localPlayer != null) Player.m_localPlayer.Message(MessageHud.MessageType.Center, s, 0, null); } catch (Exception e) { Plugin.Quiet(e, "Story.Msg"); } }

        public static string Info()
        {
            var sb = new System.Text.StringBuilder("story " + (statePath ?? "-") + " | flags: " + string.Join(",", new List<string>(Flags).ToArray()) + " | quests:");
            foreach (var kv in Quests) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.Append(" | kills:"); foreach (var kv in Kills) sb.Append(" " + kv.Key + "=" + kv.Value);
            return sb.ToString();
        }
    }
}
