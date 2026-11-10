using System;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Einstieg wie im Original:
    //  - pack.json "start": {"pos": [x, z], "yaw": 90}: neue Charaktere (ohne Abmeldepunkt/Bett) landen hier statt an den Startsteinen.
    //    Valheims Walkuere fliegt sie also direkt dorthin; nach dem Tod ohne Bett geht's auch wieder hierher.
    //  - npcs.json "auto": {"radius": 18, "flag": "intro_done"}: Gespraech startet von selbst, sobald man nah genug ist (einmal).
    //  - cutscenes.json "trigger": {"start": true}: Intro, laeuft einmal pro Charakter und Welt kurz nach der Ankunft (nach der Walkuere).
    public static class Intro
    {
        public static void Patch(Harmony h)
        {
            var m = AccessTools.Method(typeof(Game), "FindSpawnPoint");
            if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(Intro), "FindSpawnPre"));
            else Plugin.Warn("Intro: Game.FindSpawnPoint fehlt");
        }

        static bool StartPos(out Vector3 v, out float yaw)
        {
            v = Vector3.zero; yaw = 0f;
            var pk = Pack.Current;
            if (pk == null || pk.Start == null) return false;
            yaw = Json.F(pk.Start, "yaw");
            return pk.Pos(Json.V(pk.Start, "pos"), out v);
        }

        static bool FindSpawnPre(ref Vector3 point, ref bool usedLogoutPoint, ref bool __result)
        {
            try
            {
                Vector3 st; float yaw;
                if (!StartPos(out st, out yaw) || Game.instance == null) return true;
                var prof = Game.instance.GetPlayerProfile();
                if (prof == null || prof.HaveCustomSpawnPoint()) return true;                             // Bett: normaler Ablauf
                // Abmeldepunkt zaehlt erst, wenn der Charakter hier schon richtig angefangen hat (Intro gesehen).
                // Sonst (Welt vorher ohne Paket betreten, Abbruch beim Laden) startet er am Startpunkt.
                // Nach dem Tod (ohne Bett) nicht an Valheims Startstein-Ring, sondern wieder am Startpunkt
                bool afterDeath = false;
                try { var fd = AccessTools.Field(typeof(Game), "m_respawnAfterDeath"); if (fd != null) afterDeath = (bool)fd.GetValue(Game.instance); } catch { }
                if (!afterDeath && prof.HaveLogoutPoint() && Story.StartSeen(prof.GetName())) return true;
                usedLogoutPoint = false;
                // Hoehe angegeben (z. B. Zimmer in einer Burg): von knapp darueber nach unten suchen, sonst landet man auf dem Dach
                bool fixedY = !float.IsNaN(st.y);
                if (!fixedY) st.y = 40f;
                ZNet.instance.SetReferencePosition(st);
                if (!ZNetScene.instance.IsAreaReady(st)) { __result = false; return false; }               // weiter warten
                float h; RaycastHit hit;
                int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
                if (Physics.Raycast(new Vector3(st.x, st.y + (fixedY ? 1.2f : 80f), st.z), Vector3.down, out hit, fixedY ? 6f : 200f, mask)) st.y = hit.point.y + 0.3f;
                else if (fixedY) st.y += 0.3f;
                else if (!fixedY && ZoneSystem.instance.GetGroundHeight(st, out h)) st.y = h + 0.3f;
                point = st; __result = true;
                if (fixedY) { holdPos = st; holdT = Time.time; }                                           // festhalten, bis der Boden (Modelle) steht
                Plugin.Log("Start: " + (afterDeath ? "nach dem Tod zurueck" : "neuer Charakter landet") + " bei " + st);
                return false;
            }
            catch (Exception e) { Plugin.Quiet(e, "Intro.FindSpawn"); return true; }
        }

        // Valheims eigener Ladebildschirm noch zu sehen? (Hud.m_loadingScreen, CanvasGroup)
        static System.Reflection.FieldInfo fLoad;
        public static bool Loading()
        {
            try
            {
                if (Hud.instance == null) return true;
                if (fLoad == null) fLoad = AccessTools.Field(typeof(Hud), "m_loadingScreen");
                var cg = fLoad != null ? fLoad.GetValue(Hud.instance) as CanvasGroup : null;
                return cg != null && cg.gameObject.activeInHierarchy && cg.alpha > 0.01f;
            }
            catch { return false; }
        }

        // Nach dem Start auf festgelegter Hoehe (Zimmer) den Spieler halten, bis die Modelle um ihn gebaut sind (max. 20 s)
        static Vector3 holdPos; static float holdT = -1f;
        static void Hold()
        {
            var p = Player.m_localPlayer;
            if (holdT < 0f || p == null) return;
            bool ready = Time.time - holdT > 1.5f && Statics.Pending == 0 && Time.time - Statics.QueueBuiltT < 0.5f && Glb.PrefetchCount == 0;
            var body = p.GetComponent<Rigidbody>();
            if (ready || Time.time - holdT > 20f) { holdT = -1f; if (body != null) body.isKinematic = false; Plugin.Log("Start: Boden steht, Spieler frei"); return; }
            if (body != null) { body.isKinematic = true; body.position = holdPos; body.velocity = Vector3.zero; }
            p.transform.position = holdPos;
        }

        // Gespraeche, die von selbst starten
        static float scanT, landedT = -1f;
        public static void Tick()
        {
            try { Hold(); } catch (Exception e) { Plugin.Quiet(e, "Intro.Hold"); }
            var pk = Pack.Current; var p = Player.m_localPlayer;
            if (pk == null || p == null || StoryUi.Busy || Time.time < scanT) return;
            scanT = Time.time + 0.5f;
            try { if (Valkyrie.m_instance != null) { landedT = -1f; return; } } catch { }          // erst nach der Landung
            if (p.IsTeleporting() || p.IsDead() || Loading()) { landedT = -1f; return; }
            // Intro-Cutscene (trigger "start"): einmal pro Charakter und Welt, kurz nach der Ankunft
            if (landedT < 0f) landedT = Time.time;
            if (Time.time - landedT > 2.5f)
                foreach (var kv in pk.Cutscenes)
                {
                    if (!Json.B(Json.V(kv.Value, "trigger"), "start") || Story.SeenCutscenes.Contains(kv.Key)) continue;
                    StoryUi.PlayCutscene(kv.Key);
                    return;
                }
            foreach (var n in pk.Npcs)
            {
                if (n.Auto == null) continue;
                var flag = Json.S(n.Auto, "flag", "auto_" + n.Id);
                if (Story.Check(new System.Collections.Generic.List<object> { flag })) continue;
                var go = Npcs.Get(n.Id);
                if (go == null) continue;
                if (Story.Flat(p.transform.position, go.transform.position) > Json.F(n.Auto, "radius", 15f)) continue;
                Story.Do(new System.Collections.Generic.List<object> { new System.Collections.Generic.Dictionary<string, object> { { "set", flag } } });
                StoryUi.StartDialogue(n.Id);
                break;
            }
        }
    }
}
