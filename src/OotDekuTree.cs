using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Der Deku-Baum (Bg_Treemouth + Cutscenes gDekuTreeMeetingCs/ChoiceCs/MouthOpeningCs/AskAgainCs): Link kommt auf die
    /// Lichtung -> Navi "Great Deku Tree... I'm back!" (0x107D), die Rede des Baums (0x1015 ...), Frage mit Auswahl (0x1016).
    /// Ja -> 0x1017, der Mund faehrt auf (unk_168 0..1: (4029,136,-1255) -> (3869,-263,-1163)), Eingang in den Baum frei.
    /// Nein -> 0x1018, beim naechsten Mal fragt er wieder. pack.json "oot_deku".
    /// </summary>
    public static class OotDekuTree
    {
        public const string OpenFlag = "oot_deku_open";
        static Pack forPack; static object cfg; static GameObject mouth;
        static Vector3 closedP, openP, trig, enter, warp, camPos, camLook; static float trigR = 20f, warpYaw, open; static bool opening, asked, armed = true;
        static readonly Dictionary<string, object> texts = new Dictionary<string, object>();

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }

        /// <summary>Neu einlesen (Mund zu/auf nach Merker), z. B. nach "Ganzes Spiel neu" im Dev-Pult.</summary>
        public static void Rebuild() { forPack = null; opening = false; asked = false; armed = true; }

        static void Setup(Pack pk)
        {
            if (mouth != null) UnityEngine.Object.Destroy(mouth);
            mouth = null; forPack = pk; cfg = null; texts.Clear();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            cfg = Json.V(meta, "oot_deku"); if (cfg == null) return;
            closedP = V3(Json.L(cfg, "closed")); openP = V3(Json.L(cfg, "open")); trig = V3(Json.L(cfg, "trigger")); trigR = Json.F(cfg, "radius", 20f);
            enter = V3(Json.L(cfg, "enter")); warp = V3(Json.L(cfg, "warp")); warpYaw = Json.F(cfg, "warp_yaw");
            camPos = V3(Json.L(cfg, "cam")); camLook = V3(Json.L(cfg, "look"));
            var tx = Json.V(cfg, "texts") as Dictionary<string, object>;
            if (tx != null) foreach (var kv in tx) texts[kv.Key] = kv.Value;
            open = Story.Flags.Contains(OpenFlag) ? 1f : 0f;
        }

        /// <summary>Seiten eines Textes inkl. Folgetexte (TEXTID), Auswahl des letzten.</summary>
        static List<string> Pages(string id, out int choice)
        {
            var l = new List<string>(); choice = 0; int guard = 0;
            while (!string.IsNullOrEmpty(id) && guard++ < 10)
            {
                object t; if (!texts.TryGetValue(id, out t)) break;
                foreach (var s in Json.L(t, "pages") ?? new List<object>()) l.Add(s.ToString());
                choice = (int)Json.F(t, "choice"); id = Json.S(t, "next");
            }
            return l;
        }

        public static void Tick(Player p)
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if (cfg == null || p == null) return;
                var me = p.transform.position;
                float d = (me - trig).magnitude;
                if (mouth == null && d < 150f)
                {
                    var rel = Json.S(cfg, "mouth");
                    var path = Path.Combine(forPack.Dir, (rel ?? "").Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(path))
                    {
                        mouth = Glb.Instantiate(path); mouth.name = "oot_deku_mouth";
                        mouth.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                        foreach (var c in mouth.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
                        foreach (var mf in mouth.GetComponentsInChildren<MeshFilter>(true)) { var mc = mf.gameObject.AddComponent<MeshCollider>(); mc.sharedMesh = mf.sharedMesh; }
                    }
                }
                if (mouth != null)
                {
                    if (d > 200f) { UnityEngine.Object.Destroy(mouth); mouth = null; return; }
                    if (opening) { open = Mathf.Min(1f, open + Time.deltaTime * 0.3f); if (open >= 1f) opening = false; }   // +0.01 je Bild
                    mouth.transform.position = Vector3.Lerp(closedP, openP, open);
                }
                bool isOpen = Story.Flags.Contains(OpenFlag);
                // hinein: Mund offen, Link im Rachen
                // Moritz 09.11.: Hineinspringen soll reichen (vorher Kugel 1,6 m um den Rachen-Boden -> man musste ganz hinunter)
                var de = me - enter;
                if (isOpen && open >= 1f && new Vector2(de.x, de.z).magnitude < 1.8f && de.y > -2f && de.y < 5f && OotPlay.Driving) { OotPlay.WarpTo(warp, warpYaw); return; }
                if (isOpen || !OotPlay.Driving || OotPlay.InDialog) return;
                if (d > trigR + 4f) armed = true;
                if (!armed || d > trigR) return;
                // Link schaut zum Baum (Actor_IsFacingAndNearPlayer)
                var to = trig - me; to.y = 0f;
                if (Vector3.Angle(p.transform.forward, to) > 80f) return;
                armed = false;
                int ch;
                var pages = new List<string>();
                if (!asked) { pages.AddRange(Pages("0x107D", out ch)); pages.AddRange(Pages("0x1015", out ch)); }
                else pages.AddRange(Pages("0x1016", out ch));
                asked = true;
                // gDekuTreeMeetingCs: Wald-Musik aus, NA_BGM_DEKU_TREE_CS; Mund oeffnen / nochmal fragen: wieder Kokiri-Wald
                var csm = Path.Combine(forPack.Dir, "music", "deku_tree_cs.ogg");
                if (File.Exists(csm)) Music.StoryTrack = csm;
                OotPlay.StartDialog(pages, ch, sel =>
                {
                    int c2;
                    if (sel == 0)
                        OotPlay.StartDialog(Pages("0x1017", out c2), 0, s2 =>
                        {
                            Music.StoryTrack = null;
                            Story.Flags.Add(OpenFlag); Story.Save(); opening = true;
                            OotSfx.Play("NA_SE_EV_WOODDOOR_OPEN", 1f, closedP);
                        }, camPos, camLook);
                    else OotPlay.StartDialog(Pages("0x1018", out c2), 0, s3 => { Music.StoryTrack = null; }, camPos, camLook);
                }, camPos, camLook);
            }
            catch (Exception e) { Plugin.Quiet(e, "OotDekuTree"); }
        }
    }
}
