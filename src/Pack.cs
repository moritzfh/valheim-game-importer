using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;

namespace GameImporter
{
    // Spielpaket (docs/PACK_FORMAT.md): Orte, NPCs, Dialoge, Quests, Cutscenes aus BepInEx/config/GameImporter/packs/<id>/
    public class Pack
    {
        public static Pack Current;
        public static string Root { get { return Path.Combine(Paths.ConfigPath, "GameImporter", "packs"); } }

        public string Id, Title, Dir, DefaultLang = "en", MapFile, SitesFile;
        public object PlayerModel;          // pack.json "player_model": {"model": "...glb", "scale": 1, "anims": {...}}
        public object Start;                // pack.json "start": {"pos": [x, z], "yaw": 90} - hier landen neue Charaktere (Walkuere)
        public object Meta;                 // pack.json komplett (fuer seltene Felder: "locks", "doors_open" ...)
        public Color? InteriorLight;        // pack.json "interior_light": [r, g, b] = Grundlicht unter Dach (warm statt Himmelsblau)
        public bool Snow;                   // pack.json "snow": true = Valheims Hoehen-Schnee auf Gebaeuden/Baeumen erlaubt (Standard: aus)
        public string Style = "original";   // pack.json "style": "original" (Modelle aus den Spieldateien) | "valheim" (alles in Valheim-Optik)
        public bool ValheimStyle { get { return Style == "valheim"; } }
        public readonly Dictionary<string, Place> Places = new Dictionary<string, Place>();
        public readonly List<Npc> Npcs = new List<Npc>();
        public readonly Dictionary<string, Npc> NpcById = new Dictionary<string, Npc>();
        public readonly Dictionary<string, object> Dialogues = new Dictionary<string, object>();
        public readonly Dictionary<string, Quest> Quests = new Dictionary<string, Quest>();
        public readonly List<Quest> QuestOrder = new List<Quest>();
        public readonly Dictionary<string, object> Cutscenes = new Dictionary<string, object>();
        public readonly Dictionary<string, object> Speakers = new Dictionary<string, object>();

        public class Place { public string Id; public object Name; public float X, Z, Radius, Safe = -1f; }
        public class Npc { public string Id, Dialogue, Model, Anim, Pose; public object Name, Pos, Bark, Look, Auto; public float Yaw, Scale = 1f; public List<object> If; }
        public class Stage { public string Id, Next; public object Log, Pin; public Dictionary<string, object> Goal; public bool TrackOnly; public List<object> Do; }
        public class Quest { public string Id, Giver; public object Title; public List<Stage> Stages = new List<Stage>(); public List<object> Rewards; }

        // Fuer welche Karte/Welt? Gleiche Namensregel wie der Karten-Import (map-<id>)
        public static void OnWorld(string map)
        {
            Current = null;
            if (string.IsNullOrEmpty(map)) return;
            var dir = Path.Combine(Root, map);
            if (!File.Exists(Path.Combine(dir, "pack.json"))) return;
            try
            {
                Current = Load(dir);
                Plugin.Log("Spielpaket '" + Current.Id + "' geladen: " + Current.Places.Count + " Orte, " + Current.Npcs.Count + " NPCs, " + Current.Dialogues.Count + " Dialoge, " + Current.Quests.Count + " Quests, " + Current.Cutscenes.Count + " Cutscenes");
            }
            catch (Exception e) { Plugin.Warn("Spielpaket " + dir + " nicht lesbar: " + e.Message); }
        }

        static object Read(string dir, string file)
        {
            var p = Path.Combine(dir, file);
            if (!File.Exists(p)) return null;
            try { return Json.Parse(File.ReadAllText(p)); }
            catch (Exception e) { throw new Exception(file + ": " + e.Message); }
        }

        public static Pack Load(string dir)
        {
            var meta = Read(dir, "pack.json");
            if (Json.S(meta, "format") != "gameimporter-pack") throw new Exception("pack.json: kein gameimporter-pack");
            var pk = new Pack { Dir = dir, Id = Json.S(meta, "id", Path.GetFileName(dir)), Title = Json.S(meta, "title", ""), DefaultLang = Json.S(meta, "default_language", "en"),
                                MapFile = Json.S(meta, "map"), SitesFile = Json.S(meta, "sites"), PlayerModel = Json.V(meta, "player_model"), Style = Json.S(meta, "style", "original"), Start = Json.V(meta, "start"), Snow = Json.V(meta, "snow") is bool && (bool)Json.V(meta, "snow") };
            pk.Meta = meta;
            var il = Json.L(meta, "interior_light");
            if (il != null && il.Count >= 3) pk.InteriorLight = new Color((float)(double)il[0], (float)(double)il[1], (float)(double)il[2]);
            var sp = Json.O(meta, "speakers");
            if (sp != null) foreach (var kv in sp) pk.Speakers[kv.Key] = kv.Value;
            var places = Read(dir, "places.json") as List<object>;
            if (places != null) foreach (var o in places)
                {
                    var p = Json.L(o, "pos");
                    if (p == null || p.Count < 2) continue;
                    var pl = new Place { Id = Json.S(o, "id"), Name = Json.V(o, "name"), X = (float)(double)p[0], Z = (float)(double)p[p.Count - 1], Radius = Json.F(o, "radius", 50f), Safe = Json.F(o, "safe", -1f) };
                    pk.Places[pl.Id] = pl;
                }
            var npcs = Read(dir, "npcs.json") as List<object>;
            if (npcs != null) foreach (var o in npcs)
                {
                    var n = new Npc { Id = Json.S(o, "id"), Name = Json.V(o, "name"), Pos = Json.V(o, "pos"), Yaw = Json.F(o, "yaw"), Look = Json.V(o, "look"), Pose = Json.S(o, "pose"),
                                      Dialogue = Json.S(o, "dialogue"), Bark = Json.V(o, "bark"), If = Json.L(o, "if"), Auto = Json.V(o, "auto"),
                                      Model = Json.S(o, "model"), Anim = Json.S(o, "anim"), Scale = Json.F(o, "scale", 1f) };
                    if (n.Id == null) continue;
                    pk.Npcs.Add(n); pk.NpcById[n.Id] = n;
                }
            var dl = Read(dir, "dialogues.json") as Dictionary<string, object>;
            if (dl != null) foreach (var kv in dl) pk.Dialogues[kv.Key] = kv.Value;
            var qs = Read(dir, "quests.json") as List<object>;
            if (qs != null) foreach (var o in qs)
                {
                    var q = new Quest { Id = Json.S(o, "id"), Title = Json.V(o, "title"), Giver = Json.S(o, "giver"), Rewards = Json.L(o, "rewards") };
                    var st = Json.L(o, "stages");
                    if (st != null) foreach (var s in st)
                            q.Stages.Add(new Stage { Id = Json.S(s, "id"), Next = Json.S(s, "next"), Log = Json.V(s, "log"), Goal = Json.O(s, "goal"), TrackOnly = Json.B(s, "track_only"), Do = Json.L(s, "do"), Pin = Json.V(s, "pin") });
                    if (q.Id == null || q.Stages.Count == 0) continue;
                    pk.Quests[q.Id] = q; pk.QuestOrder.Add(q);
                }
            var cs = Read(dir, "cutscenes.json") as List<object>;
            if (cs != null) foreach (var o in cs) { var id = Json.S(o, "id"); if (id != null) pk.Cutscenes[id] = o; }
            return pk;
        }

        // ---------- Texte ----------
        static string lang;
        public static string Lang
        {
            get
            {
                if (lang != null) return lang;
                string l = "English";
                try { l = Localization.instance.GetSelectedLanguage(); } catch (Exception e) { Plugin.Quiet(e, "Pack.Lang"); }
                switch (l)
                {
                    case "German": lang = "de"; break; case "English": lang = "en"; break; case "French": lang = "fr"; break;
                    case "Spanish": lang = "es"; break; case "Italian": lang = "it"; break; case "Polish": lang = "pl"; break;
                    case "Russian": lang = "ru"; break; case "Portuguese_Brazilian": lang = "pt"; break; case "Dutch": lang = "nl"; break;
                    case "Japanese": lang = "ja"; break; case "Chinese": lang = "zh"; break; case "Korean": lang = "ko"; break;
                    default: lang = l.Length >= 2 ? l.Substring(0, 2).ToLowerInvariant() : "en"; break;
                }
                return lang;
            }
        }

        public static string T(object text)
        {
            if (text == null) return "";
            var s = text as string; if (s != null) return s;
            var d = text as Dictionary<string, object>;
            if (d == null || d.Count == 0) return "";
            object v;
            if (d.TryGetValue(Lang, out v) && v != null) return v.ToString();
            if (Current != null && d.TryGetValue(Current.DefaultLang, out v) && v != null) return v.ToString();
            if (d.TryGetValue("en", out v) && v != null) return v.ToString();
            foreach (var kv in d) return kv.Value != null ? kv.Value.ToString() : "";
            return "";
        }

        public string NpcName(string id) { Npc n; object sn; if (id == "player" && ValheimStyle && Player.m_localPlayer != null) return Player.m_localPlayer.GetPlayerName(); if (id != null && Speakers.TryGetValue(id, out sn)) return T(sn); return id == "player" ? (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "") : NpcById.TryGetValue(id ?? "", out n) ? T(n.Name) : (id ?? ""); }

        // ---------- Positionen ----------
        // [x,z] oder [x,y,z] oder {"place": id, "offset": [dx,dz]}; Hoehe NaN = Boden. Hoehe als "+2" im Text = 2 m ueber Boden.
        public bool Pos(object p, out Vector3 v)
        {
            v = Vector3.zero;
            var l = p as List<object>;
            if (l != null && l.Count >= 2)
            {
                float x = Num(l[0]), z = Num(l[l.Count - 1]);
                float y = l.Count >= 3 ? Height(x, z, l[1]) : float.NaN;
                v = new Vector3(x, y, z); return true;
            }
            var d = p as Dictionary<string, object>;
            if (d != null)
            {
                Place pl;
                if (!Places.TryGetValue(Json.S(d, "place", ""), out pl)) return false;
                var off = Json.L(d, "offset");
                float x = pl.X + (off != null && off.Count >= 2 ? Num(off[0]) : 0f), z = pl.Z + (off != null && off.Count >= 2 ? Num(off[off.Count - 1]) : 0f);
                v = new Vector3(x, off != null && off.Count >= 3 ? Height(x, z, off[1]) : float.NaN, z); return true;
            }
            return false;
        }

        static float Num(object o) { if (o is double) return (float)(double)o; float f; return float.TryParse(o as string, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) ? f : 0f; }

        static float Height(float x, float z, object y)
        {
            var s = y as string;
            if (s != null && s.StartsWith("+")) return Ground(x, z) + Num(s.Substring(1));
            return Num(y);
        }

        public static float Ground(float x, float z)
        {
            float h;
            try { if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z), out h)) return h; } catch (Exception e) { Plugin.Quiet(e, "Pack.Ground"); }
            if (MapImport.Active) return MapImport.Height(x, z);
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(x, z) : 0f;
        }

        public string Info()
        {
            return "pack " + Id + " (" + Title + ") lang " + Lang + ": " + Places.Count + " places, " + Npcs.Count + " npcs, " + Dialogues.Count + " dialogues, " + Quests.Count + " quests, " + Cutscenes.Count + " cutscenes";
        }
    }
}
