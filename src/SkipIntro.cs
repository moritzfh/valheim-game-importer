using System;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Valheims Vorspann weg:
    //  - Intro-Film beim Spielstart (FejdStartup.TryPlayIntroCinematic fragt PlatformPrefs "SkipIntroCinematic" ab) -> immer "ja",
    //    nur solange die Mod laeuft (die Einstellung selbst wird nicht gespeichert, das normale Valheim bleibt unberuehrt)
    //  - lange Logo-/Erst-Start-Animation des Hauptmenues (Animator-Schalter "FirstStartup")
    //  - Walkuerenflug und Odin-Text bei neuen Charakteren in importierten Spielwelten (dort gibt es den eigenen Spielanfang,
    //    z. B. Witcher-Prolog)
    public static class SkipIntro
    {
        public static void Patch(Harmony h)
        {
            try
            {
                var pp = AccessTools.TypeByName("PlatformPrefs");
                if (pp != null)
                    foreach (var m in pp.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                    {
                        var ps = m.GetParameters();
                        if (m.Name == "GetBool" && m.ReturnType == typeof(bool) && ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                            h.Patch(m, prefix: new HarmonyMethod(typeof(SkipIntro), nameof(GetBoolPre)));
                    }
                var st = AccessTools.Method(typeof(FejdStartup), "Start");
                if (st != null) h.Patch(st, postfix: new HarmonyMethod(typeof(SkipIntro), nameof(MenuStartPost)));
                var si = AccessTools.Method(typeof(Game), "ShowIntro");
                if (si != null) h.Patch(si, prefix: new HarmonyMethod(typeof(SkipIntro), nameof(ShowIntroPre)));
                foreach (var m in typeof(Player).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "OnSpawned" && ps.Length >= 1 && ps[0].ParameterType == typeof(bool))
                        h.Patch(m, prefix: new HarmonyMethod(typeof(SkipIntro), nameof(OnSpawnedPre)));
                }
            }
            catch (Exception e) { Plugin.Warn("SkipIntro: " + e.Message); }
        }

        static bool GetBoolPre(string __0, ref bool __result)
        {
            if (__0 == "SkipIntroCinematic" && Plugin.SkipIntroOn.Value) { __result = true; return false; }
            return true;
        }

        static void MenuStartPost(FejdStartup __instance)
        {
            if (!Plugin.SkipIntroOn.Value) return;
            try
            {
                var an = Traverse.Create(__instance).Field("m_menuAnimator").GetValue<Animator>();
                if (an != null) an.SetBool("FirstStartup", false);
            }
            catch (Exception e) { Plugin.Quiet(e, "SkipIntro.Menu"); }
        }

        // Odin-Text ("When Oden heard ...") beim ersten Auftritt: in importierten Welten nicht
        static bool ShowIntroPre(Game __instance)
        {
            if (!Plugin.SkipValkyrie.Value || Pack.Current == null) return true;
            try { __instance.SkipIntro(); } catch (Exception e) { Plugin.Quiet(e, "SkipIntro.Text"); }
            return false;
        }

        // erster Auftritt eines neuen Charakters: Walkuere nur in normalen Valheim-Welten
        static void OnSpawnedPre(ref bool __0)
        {
            if (__0 && Plugin.SkipValkyrie.Value && Pack.Current != null) { __0 = false; Plugin.Log("Spielanfang ohne Walkuere (importierte Welt)"); }
        }
    }
}
