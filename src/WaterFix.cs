using System;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Wasser auf importierten Karten: Valheim faerbt das Meer pro 64-m-Gebiet nur nach der Tiefe an den 4 Ecken
    // (Heightmap.m_oceanDepth). Auf Valheim-Karten passt das, an Witchers Steilkuesten liegen die Ecken aber oft an Land
    // -> Tiefe 0 -> das ganze Stueck Wasser wird hell und durchsichtig, und an den Gebietsgrenzen sieht man Kanten.
    // Hier: Tiefe je Ecke aus der tiefsten Stelle im Umkreis der Ecke (gleiche Ecke = gleicher Wert -> keine Kanten).
    public static class WaterFix
    {
        static System.Reflection.FieldInfo fDepth, fWidth, fScale;
        public static int Fixed, Match, Mismatch;
        public const float Reach = 20f;

        public static void Patch(Harmony h)
        {
            try
            {
                var t = typeof(Heightmap);
                fDepth = AccessTools.Field(t, "m_oceanDepth"); fWidth = AccessTools.Field(t, "m_width"); fScale = AccessTools.Field(t, "m_scale");
                var m = AccessTools.Method(t, "UpdateCornerDepths");
                if (m == null || fDepth == null) { Plugin.Warn("Wasser: Heightmap.UpdateCornerDepths fehlt"); return; }
                h.Patch(m, postfix: new HarmonyMethod(typeof(WaterFix), nameof(CornerPost)));
            }
            catch (Exception e) { Plugin.Warn("Wasser-Patch: " + e.Message); }
        }

        // Ecken wie in Valheim: [0] (0, w)  [1] (w, w)  [2] (w, 0)  [3] (0, 0)  in Gitter-Koordinaten (x, y=z)
        public static Vector3[] Corners(Heightmap hm)
        {
            int w = (int)fWidth.GetValue(hm); float s = (float)fScale.GetValue(hm);
            var p = hm.transform.position; float half = w * s * 0.5f;
            float x0 = p.x - half, z0 = p.z - half, x1 = p.x + half, z1 = p.z + half;
            return new[] { new Vector3(x0, 0, z1), new Vector3(x1, 0, z1), new Vector3(x1, 0, z0), new Vector3(x0, 0, z0) };
        }

        public static float Depth(Vector3 c)
        {
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            float lo = float.MaxValue;
            for (float dz = -Reach; dz <= Reach; dz += 4f)
                for (float dx = -Reach; dx <= Reach; dx += 4f)
                {
                    if (dx * dx + dz * dz > Reach * Reach) continue;
                    lo = Mathf.Min(lo, MapImport.Height(c.x + dx, c.z + dz));
                }
            float depth = Mathf.Max(0f, water - lo);
            // Valheim blendet die Wasserflaeche eines Gebiets aus, wenn alle Ecken trocken sind -> Wasser endet mit
            // gerader Kante. Darum: liegt irgendwo in den 4 Gebieten an dieser Ecke Wasser, bekommt die Ecke Mindesttiefe.
            if (depth < MinDepth)
                for (float dz = -64f; dz <= 64f && depth < MinDepth; dz += 4f)
                    for (float dx = -64f; dx <= 64f; dx += 4f)
                        if (MapImport.Height(c.x + dx, c.z + dz) < water - 0.05f) { depth = MinDepth; break; }
            return depth;
        }
        public const float MinDepth = 1.5f;

        static void CornerPost(Heightmap __instance)
        {
            try
            {
                if (!MapImport.Active || __instance == null) return;
                var d = fDepth.GetValue(__instance) as float[];
                if (d == null || d.Length < 4) return;
                var cs = Corners(__instance);
                float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                // Pruefen, ob unsere Ecken-Reihenfolge zu Valheims passt (einmalig melden, falls nicht)
                for (int i = 0; i < 4; i++)
                {
                    float direct = Mathf.Max(0f, water - MapImport.Height(cs[i].x, cs[i].z));
                    if (Mathf.Abs(direct - d[i]) > 1.5f) { Mismatch++; if (Mismatch <= 3) Plugin.Log("Wasser: Ecke " + i + " passt nicht (" + d[i].ToString("F1") + " vs " + direct.ToString("F1") + ") bei " + cs[i]); }
                    else Match++;
                }
                for (int i = 0; i < 4; i++) d[i] = Mathf.Max(d[i], Depth(cs[i]));
                Fixed++;
            }
            catch (Exception e) { Plugin.Quiet(e, "WaterFix"); }
        }

        // Fehlersuche: Valheims Ecken-Tiefen vs. unsere am naechsten Gebiet
        public static string Info(Vector3 p)
        {
            var hm = Heightmap.FindHeightmap(p);
            if (hm == null) return "wasser: kein Gelaende hier";
            var d = fDepth.GetValue(hm) as float[];
            var cs = Corners(hm);
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            var s = "wasser: gebiet " + hm.transform.position.ToString("F0") + ", angepasst " + Fixed + ", ecken passen " + Match + "/" + (Match + Mismatch);
            for (int i = 0; i < 4; i++)
                s += "\n  ecke " + i + " " + cs[i].ToString("F0") + " valheim/jetzt " + (d != null ? d[i].ToString("F1") : "?") + " | karte direkt " + Mathf.Max(0f, water - MapImport.Height(cs[i].x, cs[i].z)).ToString("F1") + " umkreis " + Depth(cs[i]).ToString("F1");
            // Wasserflaechen in der Naehe: an/aus
            try
            {
                int on = 0, off = 0; var offs = "";
                var fs = AccessTools.Field(typeof(WaterVolume), "m_waterSurface");
                foreach (var wv in UnityEngine.Object.FindObjectsOfType<WaterVolume>())
                {
                    var d2 = wv.transform.position - p; d2.y = 0f;
                    if (d2.magnitude > 200f) continue;
                    var mr = fs != null ? fs.GetValue(wv) as MeshRenderer : null;
                    bool vis = mr != null && mr.enabled && mr.gameObject.activeInHierarchy;
                    if (vis) on++; else { off++; if (off <= 6) offs += " " + wv.transform.position.ToString("F0"); }
                }
                s += "\n  wasserflaechen im Umkreis 200 m: an " + on + ", aus " + off + offs;
            }
            catch (Exception e) { s += "\n  " + e.Message; }
            return s;
        }
    }
}
