using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Tastenbelegung wie in The Witcher 3 (PC). pack.json "witcher": {"controls": "witcher"} (Standard) oder "valheim".
    //   LMB schneller Angriff, Shift+LMB starker Angriff, RMB Blocken/Parieren (mit Waffe) bzw. Hexersinne (ohne Waffe),
    //   Alt Ausweichen, Leertaste im Kampf Rolle, Q Zeichen wirken, Tab Schnellmenue (Zeichen/Traenke), R Trank trinken,
    //   1/2 Stahl-/Silberschwert ziehen, X Ploetze rufen, E am Wegweiser Schnellreise, I Inventar, M Karte, J Tagebuch.
    // Valheim-Eingaben, die dabei im Weg sind (Tab-Inventar, R Waffen weg, Q Autolauf, X Sitzen, 1/2 Schnellleiste), werden abgefangen.
    public static class W3Controls
    {
        static Pack forPack; static bool on;
        public static bool On { get { if (Pack.Current != forPack) Setup(Pack.Current); return on && Witcher.On; } }
        public static bool QuickOpen;               // Tab-Schnellmenue
        public static bool SensesHeld;              // RMB ohne Waffe
        static int hover = -1;
        static bool slowed;

        static void Setup(Pack pk)
        {
            forPack = pk; on = false;
            if (pk == null) return;
            try
            {
                var meta = Json.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(pk.Dir, "pack.json")));
                on = Json.S(Json.V(meta, "witcher"), "controls", "witcher") == "witcher" && Json.V(meta, "witcher") != null;
            }
            catch (Exception e) { Plugin.Quiet(e, "W3Controls.Setup"); }
        }

        public static void Patch(Harmony h)
        {
            var zi = AccessTools.TypeByName("ZInput");
            foreach (var n in new[] { "GetButtonDown", "GetButton", "GetButtonUp" })
            {
                var m = AccessTools.Method(zi, n, new[] { typeof(string) });
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(W3Controls), n == "GetButton" ? "HoldPre" : n == "GetButtonDown" ? "DownPre" : "UpPre"));
                else Plugin.Warn("W3Controls: ZInput." + n + " fehlt");
            }
        }

        // ---------- Valheim-Eingaben umbiegen ----------
        static bool Active() { return On && Player.m_localPlayer != null && Horse.FreeInput(); }
        static bool Shift() { return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift); }

        static bool Blocked(string name)
        {
            switch (name)
            {
                case "Inventory": case "Hide": case "AutoRun": case "Sit": case "Hotbar1": case "Hotbar2": return true;
                case "Jump": return WeaponDrawn() && InCombat();     // im Kampf: Rolle statt Sprung
            }
            return false;
        }

        static bool DownPre(string name, ref bool __result)
        {
            if (name == "Use" && Interact.Claim) { __result = false; return false; }
            if (!Active()) return true;
            if (QuickOpen && (name == "Attack" || name == "SecondaryAttack" || name == "Block")) { __result = false; return false; }
            if (Blocked(name)) { __result = false; return false; }
            if (name == "Attack" && Shift()) { __result = false; return false; }
            if (name == "SecondaryAttack" && Shift() && Input.GetMouseButtonDown(0)) { __result = true; return false; }
            if (name == "Block" && !WeaponDrawn()) { __result = false; return false; }
            return true;
        }

        static bool HoldPre(string name, ref bool __result)
        {
            if (name == "Use" && Interact.Claim) { __result = false; return false; }
            if (!Active()) return true;
            if (QuickOpen && (name == "Attack" || name == "SecondaryAttack" || name == "Block")) { __result = false; return false; }
            if (Blocked(name)) { __result = false; return false; }
            if (name == "Attack" && Shift()) { __result = false; return false; }
            if (name == "SecondaryAttack" && Shift() && Input.GetMouseButton(0)) { __result = true; return false; }
            if (name == "Block" && !WeaponDrawn()) { __result = false; return false; }
            return true;
        }

        static bool UpPre(string name, ref bool __result)
        {
            if (name == "Use" && Interact.Claim) { __result = false; return false; }
            if (!Active()) return true;
            if (Blocked(name)) { __result = false; return false; }
            return true;
        }

        // ---------- eigene Tasten ----------
        public static void Tick()
        {
            var p = Player.m_localPlayer;
            if (!On || p == null || p.IsDead()) { CloseQuick(); SensesHeld = false; return; }
            bool free = Horse.FreeInput() && !p.InPlaceMode() && !WitcherInv.Open && !FastTravel.Open;
            SensesHeld = free && Input.GetMouseButton(1) && !WeaponDrawn() && !QuickOpen;
            if (!free) { CloseQuick(); return; }

            // Tab gehalten: Schnellmenue, Zeit verlangsamt (nur allein)
            if (Input.GetKeyDown(KeyCode.Tab)) { QuickOpen = true; hover = -1; Slow(true); }
            if (QuickOpen && Input.GetKeyUp(KeyCode.Tab)) { Pick(hover); CloseQuick(); }
            if (QuickOpen && !Input.GetKey(KeyCode.Tab) && !slowed && Time.timeScale == 1f) { }      // per Fernsteuerung geoeffnet: offen lassen
            if (QuickOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; return; }

            if (Input.GetKeyDown(KeyCode.Q)) Witcher.CastCurrent();
            if (Input.GetKeyDown(KeyCode.R)) Witcher.DrinkCurrent();
            if (Input.GetKeyDown(KeyCode.X)) Horse.Call();
            if (Input.GetKeyDown(KeyCode.Alpha1)) DrawSword(0);
            if (Input.GetKeyDown(KeyCode.Alpha2)) DrawSword(1);
            if (Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt)) Dodge(p, true);
            if (Input.GetKeyDown(KeyCode.Space) && WeaponDrawn() && InCombat()) Dodge(p, false);
        }

        static void CloseQuick() { if (QuickOpen) { QuickOpen = false; Slow(false); } }

        static void Slow(bool on1)
        {
            bool alone = false;
            try { alone = ZNet.instance != null && ZNet.instance.IsServer() && ZNet.instance.GetPeers().Count == 0; } catch { }
            if (on1 && alone) { Time.timeScale = 0.2f; slowed = true; }
            else if (!on1 && slowed) { Time.timeScale = 1f; slowed = false; }
        }

        static void Pick(int i)
        {
            if (i < 0) return;
            if (i < Witcher.SignCount) { Witcher.CurSign = i; Story.Msg(Witcher.SignName(i)); }
            else if (i - Witcher.SignCount < Witcher.PotionCount) { Witcher.CurPotion = i - Witcher.SignCount; Story.Msg(Witcher.PotionName(Witcher.CurPotion)); }
        }

        // ---------- Hilfen ----------
        static MethodInfo rightItem, leftItem, toggleEquipped, dodge;
        public static bool WeaponDrawn()
        {
            var p = Player.m_localPlayer; if (p == null) return false;
            try
            {
                if (rightItem == null) { rightItem = AccessTools.Method(typeof(Humanoid), "GetRightItem"); leftItem = AccessTools.Method(typeof(Humanoid), "GetLeftItem"); }
                return (rightItem != null && rightItem.Invoke(p, null) != null) || (leftItem != null && leftItem.Invoke(p, null) != null);
            }
            catch { return false; }
        }

        static float combatT; static bool combat;
        static bool InCombat()
        {
            if (Time.time < combatT) return combat;
            combatT = Time.time + 0.3f; combat = false;
            var p = Player.m_localPlayer; if (p == null) return false;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed()) continue;
                if ((c.transform.position - p.transform.position).sqrMagnitude < 15f * 15f) { combat = true; break; }
            }
            return combat;
        }

        static void Dodge(Player p, bool shortStep)
        {
            try
            {
                if (dodge == null) dodge = AccessTools.Method(typeof(Player), "Dodge", new[] { typeof(Vector3) });
                var cam = Camera.main != null ? Camera.main.transform : p.transform;
                var f = cam.forward; f.y = 0f; f.Normalize(); var r = cam.right; r.y = 0f; r.Normalize();
                var d = f * ((Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0)) + r * ((Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0));
                if (d.sqrMagnitude < 0.01f) d = shortStep ? -p.transform.forward : p.transform.forward;    // Ausweichen ohne Richtung: nach hinten
                if (dodge != null) dodge.Invoke(p, new object[] { d.normalized });
            }
            catch (Exception e) { Plugin.Quiet(e, "W3Controls.Dodge"); }
        }

        public static void TestDraw(int which) { DrawSword(which); }

        // 1 = erstes Schwert (Stahl), 2 = zweites (Silber); schon gezogen -> wegstecken
        static void DrawSword(int which)
        {
            var p = Player.m_localPlayer; if (p == null) return;
            try
            {
                var swords = new List<ItemDrop.ItemData>();
                foreach (var it in p.GetInventory().GetAllItems())
                {
                    var sh = it.m_shared;
                    if ((sh.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon || sh.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon) && sh.m_skillType == Skills.SkillType.Swords) swords.Add(it);
                }
                swords.Sort((a, b) => (b.m_customData != null && b.m_customData.ContainsKey("w3_silver") ? 0 : 1).CompareTo(a.m_customData != null && a.m_customData.ContainsKey("w3_silver") ? 0 : 1));
                if (which >= swords.Count) { Story.Msg(which == 0 ? Story.L("Kein Stahlschwert", "No steel sword") : Story.L("Kein Silberschwert", "No silver sword")); return; }
                if (toggleEquipped == null) toggleEquipped = AccessTools.Method(typeof(Humanoid), "ToggleEquipped", new[] { typeof(ItemDrop.ItemData) });
                toggleEquipped.Invoke(p, new object[] { swords[which] });
            }
            catch (Exception e) { Plugin.Quiet(e, "W3Controls.DrawSword"); }
        }

        // ---------- Schnellmenue (Tab) ----------
        public static void Gui()
        {
            if (!QuickOpen) return;
            int ns = Witcher.SignCount, np = Witcher.PotionCount, n = ns + np;
            if (n == 0) return;
            float s = Screen.height / 1080f, cx = Screen.width / 2f, cy = Screen.height / 2f, R = 250f * s, box = 92f * s;
            GUI.color = new Color(0f, 0f, 0f, 0.45f); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture); GUI.color = Color.white;
            var m = Event.current.mousePosition; var dv = new Vector2(m.x - cx, m.y - cy);
            hover = -1;
            if (dv.magnitude > 60f * s)
            {
                float ang = Mathf.Atan2(dv.x, -dv.y) * Mathf.Rad2Deg; if (ang < 0) ang += 360f;
                hover = Mathf.RoundToInt(ang / (360f / n)) % n;
            }
            var lab = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(20 * s), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.86f, 0.74f, 0.47f) } };
            for (int i = 0; i < n; i++)
            {
                float a = i * 360f / n * Mathf.Deg2Rad;
                var c = new Vector2(cx + Mathf.Sin(a) * R, cy - Mathf.Cos(a) * R);
                bool sel = i == hover || (hover < 0 && (i < ns ? i == Witcher.CurSign : i - ns == Witcher.CurPotion));
                GUI.color = sel ? new Color(0.86f, 0.74f, 0.47f, 0.9f) : new Color(0.1f, 0.09f, 0.08f, 0.85f);
                GUI.DrawTexture(new Rect(c.x - box / 2 - 4 * s, c.y - box / 2 - 4 * s, box + 8 * s, box + 8 * s), Texture2D.whiteTexture);
                GUI.color = new Color(0.08f, 0.07f, 0.06f, 0.95f); GUI.DrawTexture(new Rect(c.x - box / 2, c.y - box / 2, box, box), Texture2D.whiteTexture);
                GUI.color = Color.white;
                var tex = i < ns ? Witcher.SignTex(i) : Witcher.PotionTex(i - ns);
                if (tex != null) GUI.DrawTexture(new Rect(c.x - box / 2 + 6 * s, c.y - box / 2 + 6 * s, box - 12 * s, box - 12 * s), tex);
                if (i == hover && Input.GetMouseButtonDown(0)) { Pick(i); CloseQuick(); return; }
            }
            int show = hover >= 0 ? hover : Witcher.CurSign;
            GUI.Label(new Rect(cx - 300 * s, cy - 20 * s, 600 * s, 40 * s), show < ns ? Witcher.SignName(show) : Witcher.PotionName(show - ns), lab);
        }
    }
}
