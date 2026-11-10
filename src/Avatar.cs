using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Spielfigur aus dem Spielpaket (z. B. Geralt): pack.json
    //   "player_model": {"model": "models/geralt.glb", "scale": 1.0,
    //                    "anims": {"idle": "idle", "walk": "walk", "run": "run", "sprint": "sprint", "jump": "jump", "fall": "fall", "attack": "attack"},
    //                    "speeds": {"walk": 1.6, "run": 4.2, "sprint": 7.0}}
    // Jeder Client ersetzt bei allen Spielern den sichtbaren Valheim-Koerper durch das Modell (nur Optik, Spiellogik bleibt Valheim).
    // Animation nach Bewegung: Tempo aus der Positionsaenderung (klappt auch fuer andere Spieler im Multiplayer).
    public static class Avatar
    {
        class A
        {
            public Player P; public GameObject Go; public Animation Anim; public Vector3 Last; public float Speed, VSpeed;
            public string Force; public float ForceUntil; public bool Armed; public float AttackSpeed = 1f;
            public string Cur; public float AttackT; public float HideT; public bool WasAttack; public int AttackN;
            public readonly List<KeyValuePair<GameObject, string>> Extras = new List<KeyValuePair<GameObject, string>>();
        }
        static readonly Dictionary<Player, A> all = new Dictionary<Player, A>();
        static Pack forPack;
        static string model; static float scale = 1f;
        static readonly Dictionary<string, string> anims = new Dictionary<string, string>();
        static float spWalk = 1.6f, spRun = 4.2f, spSprint = 7f;
        static readonly List<object> attachCfg = new List<object>();
        static System.Reflection.MethodInfo rightItem;
        public static bool Enabled = true;

        static void Setup(Pack pk)
        {
            Clear();
            forPack = pk; model = null; anims.Clear();
            var pm = pk != null && !pk.ValheimStyle ? pk.PlayerModel : null;     // Valheim-Optik: eigener Valheim-Charakter
            if (pm == null) return;
            var m = Json.S(pm, "model");
            if (string.IsNullOrEmpty(m)) return;
            model = Path.Combine(pk.Dir, m.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(model)) { Plugin.Warn("Spielfigur fehlt: " + model); model = null; return; }
            scale = Json.F(pm, "scale", 1f);
            attachCfg.Clear();
            var at = Json.L(pm, "attach"); if (at != null) attachCfg.AddRange(at);
            var an = Json.O(pm, "anims");
            if (an != null) foreach (var kv in an) anims[kv.Key] = kv.Value as string;
            var sp = Json.O(pm, "speeds");
            if (sp != null) { spWalk = Json.F(sp, "walk", spWalk); spRun = Json.F(sp, "run", spRun); spSprint = Json.F(sp, "sprint", spSprint); }
            Plugin.Log("Spielfigur: " + model);
        }

        public static void Clear()
        {
            foreach (var a in all.Values) { if (a.Go != null) UnityEngine.Object.Destroy(a.Go); Show(a.P, true); }
            all.Clear();
        }

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) Setup(pk);
            if (model == null || !Enabled) { if (all.Count > 0) Clear(); return; }
            // neue Spieler bekommen die Figur, weg sind weg
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == null || all.ContainsKey(p)) continue;
                try { Attach(p); } catch (Exception e) { Plugin.Warn("Spielfigur: " + e.Message); model = null; return; }
            }
            var gone = new List<Player>();
            foreach (var kv in all) if (kv.Key == null || kv.Value.Go == null) gone.Add(kv.Key);
            foreach (var p in gone) all.Remove(p);
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            foreach (var a in all.Values) Animate(a, dt);
        }

        static void Attach(Player p)
        {
            var go = Glb.Instantiate(model);
            go.name = "GI_Avatar";
            go.transform.SetParent(p.transform, false);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one * scale;
            var a = new A { P = p, Go = go, Anim = go.GetComponent<Animation>(), Last = p.transform.position };
            // Anbauteile (Schwert in der Hand ...) an Knochen haengen
            foreach (var c in attachCfg)
            {
                try
                {
                    var bone = Find(go.transform, Json.S(c, "bone", ""));
                    var path = Path.Combine(forPack.Dir, Json.S(c, "model", "").Replace('/', Path.DirectorySeparatorChar));
                    if (bone == null || !File.Exists(path)) { Plugin.Warn("Anbauteil fehlt: " + path + " / " + Json.S(c, "bone")); continue; }
                    var x = Glb.Instantiate(path);
                    x.name = "GI_Avatar_part";
                    x.transform.SetParent(bone, false);
                    a.Extras.Add(new KeyValuePair<GameObject, string>(x, Json.S(c, "when", "always")));
                }
                catch (Exception e) { Plugin.Warn("Anbauteil: " + e.Message); }
            }
            all[p] = a;
            Show(p, false);
            Plugin.Log("Spielfigur angelegt fuer " + p.GetPlayerName());
        }

        static Transform Find(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++) { var r = Find(t.GetChild(i), name); if (r != null) return r; }
            return null;
        }

        // Valheims eigene Koerper-/Ausruestungs-Renderer aus- bzw. einblenden (Ausruestung kommt bei jedem Wechsel neu -> regelmaessig)
        static void Show(Player p, bool on)
        {
            if (p == null) return;
            foreach (var r in p.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                var t = r.transform; bool mine = false;
                while (t != null) { if (t.name == "GI_Avatar") { mine = true; break; } t = t.parent; }
                if (!mine) r.enabled = on;
            }
        }

        static void Animate(A a, float dt)
        {
            var pos = a.P.transform.position;
            var d = pos - a.Last; a.Last = pos;
            float hs = new Vector2(d.x, d.z).magnitude / dt, vs = d.y / dt;
            if (hs > 30f) hs = 0f;                                   // Teleport
            a.Speed = Mathf.Lerp(a.Speed, hs, 1f - Mathf.Exp(-dt * 8f));
            a.VSpeed = Mathf.Lerp(a.VSpeed, vs, 1f - Mathf.Exp(-dt * 8f));
            if (Time.time > a.HideT) { a.HideT = Time.time + 0.5f; Show(a.P, false); }
            // Blick- und Laufrichtung: Valheim dreht den Spieler selbst; die Figur folgt dem Transform
            // Schwert sichtbar, wenn Valheim eine Waffe in der rechten Hand hat
            bool armed = false;
            try { if (rightItem == null) rightItem = HarmonyLib.AccessTools.Method(typeof(Humanoid), "GetRightItem"); armed = rightItem != null && rightItem.Invoke(a.P, null) != null; } catch { }
            a.Armed = armed;
            foreach (var x in a.Extras) if (x.Key != null) x.Key.SetActive(x.Value == "always" || (x.Value == "weapon" && armed));
            if (a.Anim == null) return;
            int ride = Horse.RideGear(a.P);
            a.Go.transform.localPosition = ride >= 0 ? -Vector3.up * Horse.Saddle : Vector3.zero;
            bool ground = a.P.IsOnGround() || Mathf.Abs(a.VSpeed) < 0.5f && a.P != Player.m_localPlayer;
            string want; float spd = 1f;
            bool attacking = a.P.InAttack();
            if (attacking && !a.WasAttack)
            {
                // Angriff: Witcher-Hieb ganz abspielen (etwas schneller, damit er zu Valheims Angriffstempo passt)
                a.AttackN++;
                string k = (a.AttackN % 2 == 0 && Has("attack2")) ? "attack2" : "attack";
                if (Has(k) && ride < 0)
                {
                    var stA = a.Anim[anims[k]];
                    if (stA != null) { a.Force = k; a.AttackSpeed = Mathf.Clamp(stA.length / 1.15f, 1f, 2.2f); a.ForceUntil = Time.time + stA.length / a.AttackSpeed * 0.92f; a.Cur = null; }
                }
            }
            a.WasAttack = attacking;
            if (a.Force != null && Time.time < a.ForceUntil && ride < 0) want = a.Force;
            else if (ride >= 0)
            {
                want = "ride_" + Horse.Gears[Mathf.Clamp(ride, 0, 4)];
                if (!Has(want)) want = Has("ride_idle") ? "ride_idle" : null;
                if (want == null) return;
            }
            else if (!ground && a.VSpeed > 1f && Has("jump")) want = "jump";
            else if (!ground && a.VSpeed < -2f && Has("fall")) want = "fall";
            else if (a.Speed < 0.35f) want = "idle";
            else if (a.Speed < (spWalk + spRun) * 0.5f) { want = "walk"; spd = a.Speed / spWalk; }
            else if (a.Speed < (spRun + spSprint) * 0.5f || !Has("sprint")) { want = "run"; spd = a.Speed / spRun; }
            else { want = "sprint"; spd = a.Speed / spSprint; }
            if (!Has(want)) want = Has("idle") ? "idle" : null;
            if (want == null) return;
            // mit gezogener Waffe: Kampf-Haltung (Schwert vor dem Koerper)
            if (a.Armed && (want == "idle" || want == "walk" || want == "run" || want == "sprint") && Has(want + "_armed")) want += "_armed";
            var clip = anims[want];
            var st = a.Anim[clip];
            if (st == null) return;
            if (a.Cur != want)
            {
                bool once = want == "jump" || want.StartsWith("attack") || want == a.Force;
                st.wrapMode = once ? WrapMode.ClampForever : WrapMode.Loop;
                if (once) st.time = 0f;
                a.Anim.CrossFade(clip, once ? 0.1f : 0.2f);
                a.Cur = want;
            }
            st.speed = want.StartsWith("walk") || want.StartsWith("run") || want.StartsWith("sprint") ? Mathf.Clamp(spd, 0.6f, 1.6f) : want.StartsWith("attack") ? a.AttackSpeed : 1f;
        }

        // einmalige Animation (Zeichen, Trinken ...); Laenge = Clip-Laenge
        public static void PlayOnce(Player p, string key)
        {
            A a; if (p == null || string.IsNullOrEmpty(key) || !all.TryGetValue(p, out a) || !Has(key) || a.Anim == null) return;
            var st = a.Anim[anims[key]]; if (st == null) return;
            a.Force = key; a.ForceUntil = Time.time + Mathf.Min(st.length, 2.5f) * 0.9f; a.Cur = null;
        }

        static bool Has(string k) { string c; return anims.TryGetValue(k, out c) && !string.IsNullOrEmpty(c); }

        public static string Info()
        {
            var sb = new System.Text.StringBuilder("avatar " + (model ?? "aus") + " | ");
            foreach (var a in all.Values)
            {
                sb.Append(a.P.GetPlayerName() + ": " + a.Cur + " v=" + a.Speed.ToString("F1") + " armed=" + a.Armed + " teile=");
                foreach (var x in a.Extras) sb.Append(x.Key != null ? x.Key.name + (x.Key.activeSelf ? "(an)" : "(aus)") + "@" + (x.Key.transform.parent != null ? x.Key.transform.parent.name : "-") + " " : "null ");
                sb.Append("; ");
            }
            return sb.ToString();
        }
    }
}
