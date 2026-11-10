using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GameImporter
{
    // Valheim-Gras abschalten: fuers ganze Paket ("grass": false in pack.json, z. B. Palast nur aus Innenraeumen,
    // sonst waechst Gras durch die Boeden) oder nur waehrend einer Einstellung ("grass": false im Shot)
    public static class Grass
    {
        public static bool ShotOff;
        static bool applied; static List<bool> was; static ClutterSystem forCs;

        public static void Tick()
        {
            var cs = ClutterSystem.instance;
            if (cs != forCs) { forCs = cs; applied = false; was = null; }
            if (cs == null) return;
            var pk = Pack.Current;
            bool off = ShotOff || (pk != null && pk.Meta != null && Json.Has(pk.Meta, "grass") && !Json.B(pk.Meta, "grass"));
            if (off == applied) return;
            var list = HarmonyLib.Traverse.Create(cs).Field("m_clutter").GetValue() as IList;
            if (list == null) { applied = off; return; }
            if (off)
            {
                was = new List<bool>();
                foreach (var c in list) { var f = HarmonyLib.Traverse.Create(c).Field("m_enabled"); was.Add(f.GetValue<bool>()); f.SetValue(false); }
            }
            else
                for (int i = 0; i < list.Count; i++) HarmonyLib.Traverse.Create(list[i]).Field("m_enabled").SetValue(was == null || i >= was.Count || was[i]);
            applied = off;
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(ClutterSystem), "ClearAll");
                if (m != null && m.GetParameters().Length == 0) { m.Invoke(cs, null); return; }
                var r = HarmonyLib.AccessTools.Method(typeof(ClutterSystem), "ResetGrass");
                if (r != null && r.GetParameters().Length == 2)
                {
                    var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                    r.Invoke(cs, new object[] { p, 5000f });
                }
            }
            catch (System.Exception e) { Plugin.Quiet(e, "Grass.Reset"); }
        }
    }
}
