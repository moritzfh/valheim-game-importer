using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Bewohner aus Ocarina of Time (Original-Modelle + Animationen, tools/oot/oot_npc.py): Kokiri, Saria, Mido.
    /// pack.json "oot_npcs": { "npcs": [{id, model, pos, yaw, clip, text: [erstes Mal, danach], radius, blocks}],
    ///                          "texts": {"0x1004": {pages, next, choice}} }
    /// A (Shift) vor einer Figur: Original-Text Seite fuer Seite (wie die Gegenstands-Textbox), Figur dreht sich zu Link.
    /// </summary>
    public static class OotNpcs
    {
        class Rule { public string If, Set; public List<string> Ids = new List<string>(); }
        class Npc
        {
            public string Id, Model, Clip; public Vector3 Pos, Base; public float Yaw, Radius; public List<Rule> Talk = new List<Rule>();
            public GameObject Go; public Animation Anim; public float CurYaw;
            public float BlockR; public string BlockUnless; public Vector3 Aside; public bool Armed = true; public string PendingSet;
        }
        static readonly List<Npc> npcs = new List<Npc>();
        static readonly Dictionary<string, object> texts = new Dictionary<string, object>();
        static Pack forPack; static float scale = 0.00033f; static GameObject holder;
        static Npc talking; static List<string> pages; static int page; static string nextId;
        public static bool Talking { get { return talking != null; } }

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var o = Json.V(meta, "oot_npcs");
            var play = Json.V(meta, "oot_play");
            if (o == null) return;
            scale = Json.F(play, "scale", 0.00033f);
            var tx = Json.V(o, "texts") as Dictionary<string, object>;
            if (tx != null) foreach (var kv in tx) texts[kv.Key] = kv.Value;
            foreach (var e in Json.L(o, "npcs") ?? new List<object>())
            {
                var p = Json.L(e, "pos"); if (p == null || p.Count < 3) continue;
                var n = new Npc
                {
                    Id = Json.S(e, "id"), Model = Json.S(e, "model"), Clip = Json.S(e, "clip", "idle"),
                    Pos = new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]),
                    Yaw = Json.F(e, "yaw"), Radius = Json.F(e, "radius", 0.45f)
                };
                foreach (var r in Json.L(e, "talk") ?? new List<object>())
                {
                    var rule = new Rule { If = Json.S(r, "if"), Set = Json.S(r, "set") };
                    foreach (var t in Json.L(r, "ids") ?? new List<object>()) rule.Ids.Add(t is double ? "0x" + ((int)(double)t).ToString("X4") : t.ToString());
                    n.Talk.Add(rule);
                }
                var bl = Json.V(e, "block");
                if (bl != null)
                {
                    n.BlockR = Json.F(bl, "radius", 2f); n.BlockUnless = Json.S(bl, "unless");
                    var a = Json.L(bl, "aside");
                    if (a != null && a.Count >= 3) n.Aside = new Vector3((float)(double)a[0], (float)(double)a[1], (float)(double)a[2]);
                }
                n.CurYaw = n.Yaw; n.Base = n.Pos;
                npcs.Add(n);
            }
            Plugin.Log("OoT-Figuren: " + npcs.Count + ", Texte: " + texts.Count);
        }

        public static void Clear()
        {
            foreach (var n in npcs) if (n.Go != null) UnityEngine.Object.Destroy(n.Go);
            npcs.Clear(); texts.Clear(); talking = null; pages = null;
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; forPack = null;
        }

        /// <summary>Jeden Frame aus OotPlay: Figuren nahe Link erzeugen, Blick/Drehung, Animation.</summary>
        public static void Tick(Player p)
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if (npcs.Count == 0 || p == null) return;
                if (holder == null) holder = new GameObject("oot_npcs");
                var me = p.transform.position;
                foreach (var n in npcs)
                {
                    float d = (n.Pos - me).magnitude;
                    if (n.Go == null && d < 70f) Spawn(n);
                    if (n.Go == null) continue;
                    if (d > 95f) { UnityEngine.Object.Destroy(n.Go); n.Go = null; continue; }
                    if (n.BlockR > 0f) Blocker(n, p);
                    // Im Gespraech zu Link drehen, danach langsam zurueck in die Ausgangsrichtung
                    float want = n.Yaw;
                    if (talking == n) { var dv = me - n.Pos; dv.y = 0f; if (dv.sqrMagnitude > 0.01f) want = Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg; }
                    n.CurYaw = Mathf.MoveTowardsAngle(n.CurYaw, want, (talking == n ? 360f : 90f) * Time.deltaTime);
                    n.Go.transform.rotation = Quaternion.Euler(0f, n.CurYaw, 0f);
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotNpcs.Tick"); }
        }

        // Mido: stellt sich Link in den Weg (folgt ihm quer zum Pfad) und spricht ihn an; mit Schwert + Schild geht er zur Seite
        public static Action<string> AutoTalk;
        static void Blocker(Npc n, Player p)
        {
            var rot = Quaternion.Euler(0f, n.Yaw, 0f);
            var right = rot * Vector3.right;
            bool open = !string.IsNullOrEmpty(n.BlockUnless) && Story.Flags.Contains(n.BlockUnless);
            Vector3 target;
            if (open) target = n.Base + rot * n.Aside;
            else
            {
                float side = Mathf.Clamp(Vector3.Dot(p.transform.position - n.Base, right), -1.6f, 1.6f);
                target = n.Base + right * side;
            }
            var cur = n.Go.transform.position;
            var nxt = Vector3.MoveTowards(cur, new Vector3(target.x, n.Base.y, target.z), (open ? 1.6f : 3.5f) * Time.deltaTime);
            bool moving = (nxt - cur).sqrMagnitude > 1e-6f;
            n.Go.transform.position = nxt; n.Pos = nxt;
            if (n.Anim != null && talking != n)
            {
                string clip = open && moving ? "walk" : n.Clip;
                if (n.Anim.GetClip(clip) != null && !n.Anim.IsPlaying(clip)) n.Anim.CrossFade(clip, 0.2f);
            }
            if (open || talking != null) return;
            float d = Vector3.Distance(new Vector3(p.transform.position.x, 0f, p.transform.position.z), new Vector3(nxt.x, 0f, nxt.z));
            if (d > n.BlockR + 1.5f) n.Armed = true;
            if (n.Armed && d < n.BlockR && Mathf.Abs(p.transform.position.y - nxt.y) < 2f)
            {
                n.Armed = false;
                var first = StartTalkWith(n);
                if (first != null && AutoTalk != null) AutoTalk(first);
            }
        }

        static void Spawn(Npc n)
        {
            string path = Path.Combine(forPack.Dir, n.Model.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return;
            var root = new GameObject("oot_npc_" + n.Id);
            root.transform.SetParent(holder.transform, false);
            root.transform.position = n.Pos; root.transform.rotation = Quaternion.Euler(0f, n.CurYaw, 0f);
            var m = Glb.Instantiate(path);
            m.transform.SetParent(root.transform, false);
            m.transform.localPosition = Vector3.zero; m.transform.localRotation = Quaternion.identity; m.transform.localScale = Vector3.one * scale;
            foreach (var c in m.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            var col = root.AddComponent<CapsuleCollider>();
            col.radius = n.Radius; col.height = 1.2f; col.center = new Vector3(0f, 0.6f, 0f);
            root.layer = LayerMask.NameToLayer("piece") >= 0 ? LayerMask.NameToLayer("piece") : 0;
            n.Anim = m.GetComponentInChildren<Animation>();
            if (n.Anim != null)
            {
                foreach (AnimationState s in n.Anim) s.wrapMode = WrapMode.Loop;
                if (n.Anim.GetClip(n.Clip) != null) n.Anim.Play(n.Clip); else n.Anim.Play();
            }
            n.Go = root;
        }

        /// <summary>Figur vor Link, mit der man reden kann (A).</summary>
        public static bool CanTalk(Player p)
        {
            return Nearest(p) != null;
        }

        static Npc Nearest(Player p)
        {
            Npc best = null; float bd = 2.3f;
            var me = p.transform.position; var fw = p.transform.forward; fw.y = 0f; fw.Normalize();
            foreach (var n in npcs)
            {
                if (n.Go == null || n.Talk.Count == 0) continue;
                var dv = n.Pos - me; float dy = dv.y; dv.y = 0f;
                float d = dv.magnitude;
                if (d > bd || Mathf.Abs(dy) > 1.6f) continue;
                if (d > 0.3f && Vector3.Angle(fw, dv) > 100f) continue;
                best = n; bd = d;
            }
            return best;
        }

        /// <summary>Gespraech beginnen; liefert die erste Textseite.</summary>
        public static string StartTalk(Player p)
        {
            var n = Nearest(p);
            if (n == null) return null;
            return StartTalkWith(n);
        }

        static bool FlagsOk(string cond)
        {
            if (string.IsNullOrEmpty(cond)) return true;
            foreach (var f in cond.Split('+')) if (!Story.Flags.Contains(f.Trim())) return false;
            return true;
        }

        static string StartTalkWith(Npc n)
        {
            for (int ri = 0; ri < n.Talk.Count; ri++)
            {
                var r = n.Talk[ri];
                if (!FlagsOk(r.If) || r.Ids.Count == 0) continue;
                // Fortschritt je Regel: erstes Gespraech, zweites, ... (letzter Text bleibt)
                string pre = "oot_tk_" + n.Id + "_" + ri + "_";
                int k = 0;
                while (Story.Flags.Contains(pre + k)) k++;
                string id = r.Ids[Mathf.Min(k, r.Ids.Count - 1)];
                Story.Flags.Add(pre + k);
                n.PendingSet = r.Set;
                Story.Save();
                talking = n;
                return Load(id) ? pages[0] : End();
            }
            return null;
        }

        static bool Load(string id)
        {
            object t;
            pages = null; page = 0; nextId = null;
            if (id == null || !texts.TryGetValue(id, out t)) return false;
            pages = new List<string>();
            foreach (var s in Json.L(t, "pages") ?? new List<object>()) pages.Add(s.ToString());
            nextId = Json.S(t, "next");
            return pages.Count > 0;
        }

        /// <summary>Naechste Seite (oder Folgetext); null = Gespraech zu Ende.</summary>
        public static string Advance()
        {
            if (talking == null || pages == null) return End();
            page++;
            if (page < pages.Count) return pages[page];
            if (!string.IsNullOrEmpty(nextId) && Load(nextId)) return pages[0];
            return End();
        }

        static string End()
        {
            if (talking != null && !string.IsNullOrEmpty(talking.PendingSet)) { Story.Flags.Add(talking.PendingSet); Story.Save(); talking.PendingSet = null; }
            talking = null; pages = null; return null;
        }

        /// <summary>Wohin Link im Gespraech schaut.</summary>
        public static bool TalkPos(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (talking == null) return false;
            pos = talking.Pos; return true;
        }
    }
}
