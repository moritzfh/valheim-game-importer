using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Hexer-Mechaniken aus dem Spielpaket: Zeichen (Aard, Igni, Quen) und Traenke mit Toxizitaet. pack.json:
    //   "witcher": {"signs": [{"id": "aard", "name": {...}, "icon": "icons/aard.png", "anim": "sign_aard", "cost": 0.35}, ...],
    //               "potions": [{"id": "swallow", "name": {...}, "icon": "icons/swallow.png", "effect": "regen", "value": 3, "dur": 25, "tox": 25, "charges": 3}, ...]}
    // Tasten: Q Zeichen wirken, G Zeichen wechseln, Y Trank trinken, U Trank wechseln. Traenke fuellen sich beim Ausruhen (Valheim "Ausgeruht") wieder auf.
    public static class Witcher
    {
        class Sign { public string Id, Icon, Anim; public object Name; public float Cost; public Texture2D Tex; }
        class Potion { public string Id, Icon, Effect; public object Name; public float Value, Dur, Tox; public int Max, Left; public Texture2D Tex; }
        static readonly List<Sign> signs = new List<Sign>();
        static readonly List<Potion> potions = new List<Potion>();
        static Pack forPack;
        static int curSign, curPotion;
        static float tox, castT, quenHp, quenUntil, msgT; static string msg;
        static readonly Dictionary<string, float> active = new Dictionary<string, float>();   // Wirkung -> Ende
        static readonly Dictionary<string, float> activeVal = new Dictionary<string, float>();
        static Light catLight; static bool wasRested;
        public static bool On { get { return signs.Count > 0 || potions.Count > 0; } }
        public static float Toxicity { get { return tox; } }

        // Trank-Reihe fuer das Inventar (Symbol, Ladungen, aktiv)
        public static void DrawPotionRow(Rect r, float s)
        {
            var st = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * s), normal = { textColor = new Color(0.92f, 0.88f, 0.78f) } };
            float x = r.x;
            for (int i = 0; i < potions.Count; i++)
            {
                var pt = potions[i]; var ir = new Rect(x, r.y, 64 * s, 64 * s);
                if (i == curPotion) { GUI.color = new Color(0.86f, 0.74f, 0.47f, 0.35f); GUI.DrawTexture(new Rect(ir.x - 3, ir.y - 3, ir.width + 6, ir.height + 6), Texture2D.whiteTexture); GUI.color = Color.white; }
                if (pt.Tex != null) GUI.DrawTexture(ir, pt.Tex);
                GUI.Label(new Rect(x, r.y + 66 * s, 120 * s, 22 * s), pt.Left + "/" + pt.Max + (Active(pt.Effect) ? " *" : ""), st);
                if (ir.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown) { curPotion = i; Event.current.Use(); }
                x += 92 * s;
            }
        }

        static void Setup(Pack pk)
        {
            forPack = pk; signs.Clear(); potions.Clear(); active.Clear(); tox = 0f;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var w = Json.V(meta, "witcher");
            if (w == null) return;
            foreach (var s in Json.L(w, "signs") ?? new List<object>())
                signs.Add(new Sign { Id = Json.S(s, "id"), Name = Json.V(s, "name"), Icon = Json.S(s, "icon"), Anim = Json.S(s, "anim"), Cost = Json.F(s, "cost", 0.35f), Tex = Img(pk, Json.S(s, "icon")) });
            foreach (var s in Json.L(w, "potions") ?? new List<object>())
            {
                int mx = (int)Json.F(s, "charges", 3f);
                potions.Add(new Potion { Id = Json.S(s, "id"), Name = Json.V(s, "name"), Icon = Json.S(s, "icon"), Effect = Json.S(s, "effect"), Value = Json.F(s, "value", 1f),
                                         Dur = Json.F(s, "dur", 20f), Tox = Json.F(s, "tox", 20f), Max = mx, Left = mx, Tex = Img(pk, Json.S(s, "icon")) });
            }
            Plugin.Log("Hexer-Mechaniken: " + signs.Count + " Zeichen, " + potions.Count + " Traenke");
        }

        static Texture2D Img(Pack pk, string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            var p = Path.Combine(pk.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) return null;
            var t = new Texture2D(2, 2); t.LoadImage(File.ReadAllBytes(p)); return t;
        }

        public static void Tick()
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            var p = Player.m_localPlayer;
            if (!On || p == null || p.IsDead()) return;
            float dt = Time.deltaTime;
            tox = Mathf.Max(0f, tox - dt * 1.2f);
            if (Horse.FreeInput() && !p.InPlaceMode() && !W3Controls.On)
            {
                if (signs.Count > 0 && Input.GetKeyDown(KeyCode.G)) { curSign = (curSign + 1) % signs.Count; Say(Pack.T(signs[curSign].Name)); }
                if (potions.Count > 0 && Input.GetKeyDown(KeyCode.U)) { curPotion = (curPotion + 1) % potions.Count; Say(Pack.T(potions[curPotion].Name)); }
                if (signs.Count > 0 && Input.GetKeyDown(KeyCode.Q)) Cast(p, signs[curSign]);
                if (potions.Count > 0 && Input.GetKeyDown(KeyCode.Y)) Drink(p, potions[curPotion]);
            }
            // Wirkungen
            float now = Time.time;
            if (Active("regen")) p.Heal(activeVal["regen"] * dt, false);
            if (Active("stamina")) p.AddStamina(activeVal["stamina"] * dt);
            bool cat = Active("vision");
            if (cat && catLight == null && Camera.main != null)
            {
                var go = new GameObject("GI_CatVision"); go.transform.SetParent(Camera.main.transform, false);
                catLight = go.AddComponent<Light>(); catLight.type = LightType.Point; catLight.range = 40f; catLight.intensity = 1.6f; catLight.color = new Color(0.75f, 0.85f, 1f); catLight.shadows = LightShadows.None;
            }
            else if (!cat && catLight != null) { UnityEngine.Object.Destroy(catLight.gameObject); catLight = null; }
            if (quenFx != null && (Time.time > quenUntil || quenHp <= 0f))
            {
                Fx(new[] { "fx_StaffShield_Break" }, p.transform.position + Vector3.up, p.transform.forward);
                UnityEngine.Object.Destroy(quenFx); quenFx = null;
            }
            if (tox > 100f && Time.frameCount % 30 == 0) p.Damage(new HitData { m_damage = new HitData.DamageTypes { m_poison = 2f }, m_point = p.transform.position });
            // Ausruhen fuellt die Traenke auf (wie Meditieren)
            bool rested = false;
            try { rested = p.GetSEMan().HaveStatusEffect("Rested".GetStableHashCode()); } catch { }
            if (rested && !wasRested) { foreach (var pt in potions) pt.Left = pt.Max; Say(Story.L("Tränke aufgefüllt", "Potions refilled")); }
            wasRested = rested;
        }

        // fuer die Witcher-Steuerung (Q wirken, R trinken, Tab-Schnellmenue)
        public static int SignCount { get { return signs.Count; } }
        public static int PotionCount { get { return potions.Count; } }
        public static int CurSign { get { return curSign; } set { if (signs.Count > 0) curSign = (value % signs.Count + signs.Count) % signs.Count; } }
        public static int CurPotion { get { return curPotion; } set { if (potions.Count > 0) curPotion = (value % potions.Count + potions.Count) % potions.Count; } }
        public static Texture2D SignTex(int i) { return signs[i].Tex; }
        public static Texture2D PotionTex(int i) { return potions[i].Tex; }
        public static string SignName(int i) { return Pack.T(signs[i].Name); }
        public static string PotionName(int i) { return Pack.T(potions[i].Name) + " (" + potions[i].Left + "/" + potions[i].Max + ")"; }
        public static void CastCurrent() { var p = Player.m_localPlayer; if (p != null && signs.Count > 0) Cast(p, signs[curSign]); }
        public static void DrinkCurrent() { var p = Player.m_localPlayer; if (p != null && potions.Count > 0) Drink(p, potions[curPotion]); }

        static bool Active(string e) { float t; return active.TryGetValue(e, out t) && Time.time < t; }

        static void Drink(Player p, Potion pt)
        {
            if (pt.Left <= 0) { Say(Story.L("Leer – ausruhen zum Auffüllen", "Empty – rest to refill")); return; }
            if (tox + pt.Tox > 100f) { Say(Story.L("Toxizität zu hoch", "Toxicity too high")); return; }
            pt.Left--; tox += pt.Tox;
            if (pt.Effect == "heal") p.Heal(p.GetMaxHealth() * pt.Value, true);
            else { active[pt.Effect] = Time.time + pt.Dur; activeVal[pt.Effect] = pt.Value; }
            Avatar.PlayOnce(p, "drink");
            Say(Pack.T(pt.Name));
        }

        static void Cast(Player p, Sign s)
        {
            if (Time.time < castT) return;
            float cost = s.Cost * p.GetMaxStamina();
            if (p.GetStamina() < cost) { Say(Story.L("Zu wenig Ausdauer", "Not enough stamina")); return; }
            p.UseStamina(cost);
            castT = Time.time + 1.0f;
            Avatar.PlayOnce(p, s.Anim);
            var dir = p.transform.forward; dir.y = 0f; dir.Normalize();
            var hand = p.transform.position + Vector3.up * 1.4f + dir * 0.8f;
            switch (s.Id)
            {
                case "aard":
                    Fx(new[] { "fx_eikthyr_forwardshockwave", "vfx_lox_groundslam", "fx_eikthyr_stomp" }, p.transform.position + dir * 0.5f, dir);
                    Cone(p, dir, 9f, 70f, (c, d) =>
                    {
                        var h = new HitData(); h.m_damage.m_blunt = 8f; h.m_pushForce = 350f; h.m_staggerMultiplier = 6f;
                        h.m_point = c.transform.position + Vector3.up; h.m_dir = d; h.SetAttacker(p); c.Damage(h);
                    });
                    foreach (var rb in Physics.OverlapSphere(p.transform.position + dir * 4f, 5f))
                    {
                        var body = rb.attachedRigidbody;
                        if (body != null && !body.isKinematic && body.GetComponent<Character>() == null) body.AddForce((dir + Vector3.up * 0.3f) * 12f, ForceMode.VelocityChange);
                    }
                    break;
                case "igni":
                    for (int i = 1; i <= 5; i++) Fx(new[] { "fx_fenring_flames", "fx_DvergerMage_Fire_hit", "fx_fireball_staff_explosion" }, hand + dir * i * 1.2f - Vector3.up * 0.4f * i / 5f, dir);
                    Cone(p, dir, 7f, 50f, (c, d) =>
                    {
                        var h = new HitData(); h.m_damage.m_fire = 28f; h.m_pushForce = 40f;
                        h.m_point = c.transform.position + Vector3.up; h.m_dir = d; h.SetAttacker(p); c.Damage(h);
                    });
                    break;
                case "quen":
                    quenHp = 40f + p.GetMaxHealth() * 0.3f; quenUntil = Time.time + 25f;
                    Fx(new[] { "fx_shield_start" }, p.transform.position + Vector3.up, dir);
                    if (quenFx != null) UnityEngine.Object.Destroy(quenFx);
                    quenFx = Fx(new[] { "vfx_StaffShield" }, p.transform.position + Vector3.up * 0.9f, dir, 0f);
                    if (quenFx != null) quenFx.transform.SetParent(p.transform, true);
                    break;
            }
            Say(Pack.T(s.Name));
        }

        static void Cone(Player p, Vector3 dir, float range, float angle, Action<Character, Vector3> act)
        {
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == p || c.IsPlayer() && !c.IsPVPEnabled()) continue;
                var to = c.transform.position - p.transform.position; to.y = 0f;
                if (to.magnitude > range || Vector3.Angle(dir, to) > angle * 0.5f) continue;
                try { act(c, to.normalized); } catch (Exception e) { Plugin.Quiet(e, "Witcher.Cone"); }
            }
        }

        static GameObject quenFx;

        static GameObject Fx(string[] names, Vector3 pos, Vector3 dir, float life = 5f)
        {
            if (ZNetScene.instance == null) return null;
            foreach (var n in names)
            {
                var pf = ZNetScene.instance.GetPrefab(n);
                if (pf == null) continue;
                var go = UnityEngine.Object.Instantiate(pf, pos, Quaternion.LookRotation(dir));
                var nv = go.GetComponent<ZNetView>(); if (nv != null) UnityEngine.Object.Destroy(nv);
                if (life > 0f) UnityEngine.Object.Destroy(go, life);
                return go;
            }
            return null;
        }

        static void Say(string s) { msg = s; msgT = Time.time + 2f; }

        // Quen schluckt Treffer, Donner macht mehr Schaden
        public static void Patch(Harmony h)
        {
            try
            {
                var dm = AccessTools.Method(typeof(Character), "Damage", new[] { typeof(HitData) });
                if (dm != null) h.Patch(dm, prefix: new HarmonyMethod(typeof(Witcher), nameof(DamagePre)));
            }
            catch (Exception e) { Plugin.Warn("Hexer-Patch: " + e.Message); }
        }

        static void DamagePre(Character __instance, HitData hit)
        {
            try
            {
                var me = Player.m_localPlayer;
                if (me == null || hit == null) return;
                if (__instance == me && Time.time < quenUntil && quenHp > 0f)
                {
                    float d = hit.GetTotalDamage();
                    float take = Mathf.Min(d, quenHp); quenHp -= take;
                    if (d > 0f) hit.m_damage.Modify(Mathf.Clamp01((d - take) / d));
                    if (quenHp <= 0f) { quenUntil = 0f; Say("Quen"); }
                }
                else if (__instance != me && hit.GetAttacker() == me && Active("damage"))
                    hit.m_damage.Modify(1f + activeVal["damage"]);
            }
            catch (Exception e) { Plugin.Quiet(e, "Witcher.Damage"); }
        }

        // ---- Anzeige (unten links, Hexer-Stil) ----
        public static void Gui()
        {
            if (!On || Player.m_localPlayer == null || StoryUi.Busy || WitcherInv.Open) return;
            float s = Screen.height / 1080f, x = 24f * s, y = Screen.height - 230f * s;
            var box = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * s), normal = { textColor = new Color(0.92f, 0.86f, 0.7f) } };
            if (signs.Count > 0)
            {
                var sg = signs[curSign];
                GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(x - 6 * s, y - 6 * s, 84 * s, 84 * s), Texture2D.whiteTexture); GUI.color = Color.white;
                if (sg.Tex != null) GUI.DrawTexture(new Rect(x, y, 72 * s, 72 * s), sg.Tex);
                GUI.Label(new Rect(x, y + 76 * s, 160 * s, 24 * s), Pack.T(sg.Name) + (W3Controls.On ? "  [Q]  (Tab)" : "  [Q]  (G)"), box);
                if (Time.time < quenUntil) GUI.Label(new Rect(x + 90 * s, y, 200 * s, 24 * s), "Quen " + Mathf.CeilToInt(quenHp), box);
            }
            if (potions.Count > 0)
            {
                var pt = potions[curPotion]; float px = x + 180 * s;
                GUI.color = new Color(0f, 0f, 0f, 0.55f); GUI.DrawTexture(new Rect(px - 6 * s, y - 6 * s, 84 * s, 84 * s), Texture2D.whiteTexture); GUI.color = Color.white;
                if (pt.Tex != null) GUI.DrawTexture(new Rect(px, y, 72 * s, 72 * s), pt.Tex);
                GUI.Label(new Rect(px + 52 * s, y + 50 * s, 40 * s, 24 * s), pt.Left + "/" + pt.Max, box);
                GUI.Label(new Rect(px, y + 76 * s, 200 * s, 24 * s), Pack.T(pt.Name) + (W3Controls.On ? "  [R]  (Tab)" : "  [Y]  (U)"), box);
                // Toxizitaet
                float bw = 240 * s, by = y + 104 * s;
                GUI.color = new Color(0f, 0f, 0f, 0.6f); GUI.DrawTexture(new Rect(x, by, bw, 10 * s), Texture2D.whiteTexture);
                GUI.color = Color.Lerp(new Color(0.35f, 0.8f, 0.3f), new Color(0.9f, 0.2f, 0.15f), tox / 100f); GUI.DrawTexture(new Rect(x, by, bw * Mathf.Clamp01(tox / 100f), 10 * s), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(x + bw + 8 * s, by - 7 * s, 200 * s, 24 * s), Story.L("Toxizität", "Toxicity"), box);
                // aktive Wirkungen
                float ax = x; float ay = by + 18 * s;
                foreach (var p2 in potions)
                {
                    float t; if (p2.Effect == "heal" || !active.TryGetValue(p2.Effect, out t) || Time.time >= t) continue;
                    if (p2.Tex != null) GUI.DrawTexture(new Rect(ax, ay, 32 * s, 32 * s), p2.Tex);
                    GUI.Label(new Rect(ax + 34 * s, ay + 6 * s, 50 * s, 24 * s), Mathf.CeilToInt(t - Time.time) + "s", box);
                    ax += 90 * s;
                }
            }
            if (Time.time < msgT && !string.IsNullOrEmpty(msg))
            {
                var big = new GUIStyle(box) { fontSize = Mathf.RoundToInt(26 * s), alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(0, Screen.height * 0.62f, Screen.width, 40 * s), msg, big);
            }
        }

        // Fernsteuerung zum Testen: witcher sign <aard|igni|quen> | drink <id> | info
        public static string Test(string[] a)
        {
            var p = Player.m_localPlayer;
            if (p == null || !On) return "witcher: aus";
            if (a.Length > 2 && a[1] == "sign") { var s = signs.Find(x => x.Id == a[2]); if (s != null) { castT = 0f; Cast(p, s); } }
            if (a.Length > 2 && a[1] == "drink") { var pt = potions.Find(x => x.Id == a[2]); if (pt != null) Drink(p, pt); }
            var sb = new System.Text.StringBuilder("witcher tox " + tox.ToString("F0") + " quen " + quenHp.ToString("F0") + " | ");
            foreach (var pt in potions) sb.Append(pt.Id + " " + pt.Left + "/" + pt.Max + (Active(pt.Effect) ? "*" : "") + "  ");
            return sb.ToString();
        }
    }
}
