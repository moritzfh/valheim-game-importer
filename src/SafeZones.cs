using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Orte wie im Original ohne zufaellige Monster: um Startpunkt und Orte des Pakets (Doerfer, Haefen, Wegweiser)
    // legt die Mod unsichtbare Valheim-"Basis"-Bereiche (EffectArea PlayerBase). Valheim spawnt dort keine Gegner.
    // Quest-Gegner (goal.spawn) sind davon nicht betroffen, die werden direkt gesetzt.
    // Und: waehrend Cutscenes und Gespraechen ist der Spieler unverwundbar und fuer Gegner unsichtbar
    // (sonst stirbt ein neuer Charakter im Intro, waehrend er nichts tun kann).
    public static class SafeZones
    {
        static readonly List<GameObject> zones = new List<GameObject>();
        static Pack forPack;

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) { Clear(); forPack = pk; if (pk != null && Player.m_localPlayer != null) Build(pk); else forPack = null; }
            Protect();
            if (zones.Count > 0 && Time.time > sweepT) { sweepT = Time.time + 2f; Sweep(); }
        }

        // Gegner, die schon vorher da waren (beim Erzeugen der Gegend gespawnt), aus den Schutzzonen nehmen.
        // Nur wilde Monster (MonsterAI, nicht gezaehmt, kein Boss, keine Quest-Gegner); Tiere und NPCs bleiben.
        static float sweepT;
        public static int Removed;
        static void Sweep()
        {
            var me = Player.m_localPlayer;
            if (me == null || ZNetScene.instance == null) return;
            foreach (var c in new List<Character>(Character.GetAllCharacters()))
            {
                try
                {
                    if (c == null || c.IsPlayer() || c.IsTamed() || c.IsBoss() || c.GetComponent<MonsterAI>() == null) continue;
                    if (Vector3.Distance(c.transform.position, me.transform.position) > 200f) continue;
                    var nv = c.GetComponent<ZNetView>();
                    if (nv == null || !nv.IsValid() || !nv.IsOwner() || nv.GetZDO().GetBool("gi_quest")) continue;
                    if (!InZone(c.transform.position)) continue;
                    ZNetScene.instance.Destroy(c.gameObject);
                    Removed++;
                }
                catch (Exception e) { Plugin.Quiet(e, "SafeZones.Sweep"); }
            }
        }

        static bool InZone(Vector3 p)
        {
            foreach (var z in zones)
            {
                if (z == null) continue;
                var sc = z.GetComponent<SphereCollider>();
                var d = z.transform.position - p; d.y = 0f;
                if (sc != null && d.magnitude < sc.radius) return true;
            }
            return false;
        }

        static void Clear()
        {
            foreach (var z in zones) if (z != null) UnityEngine.Object.Destroy(z);
            zones.Clear();
        }

        static void Build(Pack pk)
        {
            try
            {
                var ft = AccessTools.Field(typeof(EffectArea), "m_type");
                if (ft == null) { Plugin.Warn("Schutzzonen: EffectArea.m_type fehlt"); return; }
                int layer = LayerMask.NameToLayer("character_trigger");
                Vector3 v; float yaw;
                var pts = new List<KeyValuePair<Vector3, float>>();
                if (pk.Start != null && pk.Pos(Json.V(pk.Start, "pos"), out v)) pts.Add(new KeyValuePair<Vector3, float>(v, Json.F(pk.Start, "safe", 110f)));
                foreach (var pl in pk.Places.Values)
                {
                    float r = pl.Safe >= 0f ? pl.Safe : 45f;
                    if (r > 0f) pts.Add(new KeyValuePair<Vector3, float>(new Vector3(pl.X, 0f, pl.Z), r));
                }
                foreach (var kv in pts)
                {
                    var p = kv.Key; if (float.IsNaN(p.y)) p.y = Pack.Ground(p.x, p.z);
                    var go = new GameObject("GI_SafeZone");
                    go.SetActive(false);
                    if (layer >= 0) go.layer = layer;
                    go.transform.position = p;
                    var sc = go.AddComponent<SphereCollider>(); sc.radius = kv.Value; sc.isTrigger = true;
                    var ea = go.AddComponent<EffectArea>();
                    ft.SetValue(ea, Enum.ToObject(ft.FieldType, 4));          // PlayerBase: keine zufaelligen Gegner
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.SetActive(true);
                    zones.Add(go);
                }
                Plugin.Log("Schutzzonen: " + zones.Count + " Orte ohne zufaellige Gegner");
            }
            catch (Exception e) { Plugin.Warn("Schutzzonen: " + e.Message); }
        }

        // ---------- Schutz waehrend Cutscenes/Gespraechen ----------
        static bool protecting, oldGod, oldGhost;
        static Player protectedPl;

        static void Protect()
        {
            var p = Player.m_localPlayer;
            bool want = p != null && StoryUi.Busy;
            if (want == protecting && p == protectedPl) return;
            try
            {
                if (protecting && protectedPl != null)
                {
                    Call(protectedPl, "SetGodMode", oldGod); Call(protectedPl, "SetGhostMode", oldGhost);
                }
                protecting = false; protectedPl = null;
                if (want)
                {
                    oldGod = Get(p, "InGodMode"); oldGhost = Get(p, "InGhostMode");
                    Call(p, "SetGodMode", true); Call(p, "SetGhostMode", true);
                    protecting = true; protectedPl = p;
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "SafeZones.Protect"); }
        }

        static void Call(Player p, string m, bool v) { var mi = AccessTools.Method(typeof(Player), m); if (mi != null) mi.Invoke(p, new object[] { v }); }
        static bool Get(Player p, string m) { var mi = AccessTools.Method(typeof(Player), m); return mi != null && (bool)mi.Invoke(p, null); }

        public static string Info() { return "schutzzonen " + zones.Count + ", schutz aktiv " + protecting + ", entfernt " + Removed; }
    }
}
