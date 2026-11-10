using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Kokiri-Laden wie En_Ossan/En_GirlA: Verkaeufer ansprechen (Text 0x10BA), dann Regale ansehen
    /// (Stick links/rechts = Regal, auf/ab = Fach), Gegenstand dreht sich, Beschreibung; A = Kaufen?-Frage mit Auswahl,
    /// B = zurueck. Preise, Texte und Kauf-Regeln aus sShopItemEntries / EnGirlA_CanBuy_*.
    /// pack.json "oot_shop": {"keeper": {"model","pos","yaw","clip"}, "cam": {"pos","look_l","look_r","look_k"},
    ///   "items": [{"key","price","count","desc","prompt","model","pos","yaw","shelf","slot"}], "texts": {"0x10BA": {...}}}
    /// </summary>
    public static class OotShop
    {
        class Item { public string Key, Desc, Prompt, Model; public int Price, Count, Shelf, Slot; public Vector3 Pos; public float Yaw; public GameObject Go; public float Spin; }
        static readonly List<Item> items = new List<Item>();
        static readonly Dictionary<string, object> texts = new Dictionary<string, object>();
        static Pack forPack; static GameObject holder, keeper; static Animation keeperAnim;
        static Vector3 keeperPos, camPos, lookL, lookR, lookK; static float keeperYaw; static float scale = 0.00033f; static string keeperClip;
        static bool haveCam;

        enum M { None, Talk, Shelf, Desc, Prompt, Msg, Fanfare }
        static M mode; static int shelf = -1, sel; static List<string> pages; static int page; static string nextId;
        static Action afterMsg;
        public static bool Active { get { return mode != M.None; } }
        public static string Text { get; private set; }
        public static int Choice { get; private set; }
        public static int ChoiceSel;
        public static Func<string, bool> GiveFanfare;   // OotPlay.GiveItem fuer Gegenstaende mit Fanfare

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var o = Json.V(meta, "oot_shop"); if (o == null) return;
            scale = Json.F(Json.V(meta, "oot_play"), "scale", 0.00033f);
            var tx = Json.V(o, "texts") as Dictionary<string, object>;
            if (tx != null) foreach (var kv in tx) texts[kv.Key] = kv.Value;
            var k = Json.V(o, "keeper");
            if (k != null) { keeperPos = V3(Json.L(k, "pos")); keeperYaw = Json.F(k, "yaw"); keeperClip = Json.S(k, "clip", "idle"); }
            var c = Json.V(o, "cam");
            if (c != null) { camPos = V3(Json.L(c, "pos")); lookL = V3(Json.L(c, "look_l")); lookR = V3(Json.L(c, "look_r")); lookK = V3(Json.L(c, "look_k")); haveCam = true; }
            foreach (var e in Json.L(o, "items") ?? new List<object>())
                items.Add(new Item
                {
                    Key = Json.S(e, "key"), Price = (int)Json.F(e, "price"), Count = (int)Json.F(e, "count", 1f), Desc = Json.S(e, "desc"), Prompt = Json.S(e, "prompt"),
                    Model = Json.S(e, "model"), Pos = V3(Json.L(e, "pos")), Yaw = Json.F(e, "yaw"), Shelf = (int)Json.F(e, "shelf"), Slot = (int)Json.F(e, "slot")
                });
            holder = new GameObject("oot_shop");
            var km = Json.S(k, "model");
            if (!string.IsNullOrEmpty(km))
            {
                keeper = Model(km, keeperPos, Quaternion.Euler(0f, keeperYaw, 0f), scale);
                if (keeper != null)
                {
                    keeperAnim = keeper.GetComponentInChildren<Animation>();
                    if (keeperAnim != null) { foreach (AnimationState s in keeperAnim) s.wrapMode = WrapMode.Loop; if (keeperAnim.GetClip(keeperClip) != null) keeperAnim.Play(keeperClip); else keeperAnim.Play(); }
                    var col = keeper.AddComponent<CapsuleCollider>(); col.radius = 0.4f / scale; col.height = 1.2f / scale; col.center = new Vector3(0f, 0.6f / scale, 0f);
                }
            }
            foreach (var it in items) it.Go = Model(it.Model, it.Pos, Quaternion.Euler(0f, it.Yaw, 0f), 1f);
            Plugin.Log("OoT-Laden: " + items.Count + " Gegenstaende");
        }

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }

        static GameObject Model(string rel, Vector3 pos, Quaternion rot, float sc)
        {
            if (string.IsNullOrEmpty(rel) || forPack == null) return null;
            var path = Path.Combine(forPack.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;
            var g = Glb.Instantiate(path);
            g.transform.SetParent(holder.transform, false);
            g.transform.position = pos; g.transform.rotation = rot;
            if (sc != 1f) g.transform.localScale = Vector3.one * sc;
            foreach (var cl in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(cl);
            return g;
        }

        public static void Clear()
        {
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; keeper = null; items.Clear(); texts.Clear(); forPack = null; mode = M.None; haveCam = false;
        }

        public static void Tick()
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                // ausgewaehlter Gegenstand dreht sich (shopItemSelectedTween), die anderen kehren zurueck
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i]; if (it.Go == null) continue;
                    bool on = (mode == M.Shelf || mode == M.Desc || mode == M.Prompt) && Cur() == it;
                    it.Spin = on ? it.Spin + Time.deltaTime * 180f : Mathf.MoveTowardsAngle(it.Spin, 0f, Time.deltaTime * 360f);
                    it.Go.transform.rotation = Quaternion.Euler(0f, it.Yaw + it.Spin, 0f);
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotShop.Tick"); }
        }

        public static bool NearKeeper(Player p)
        {
            if (keeper == null || p == null) return false;
            var d = keeperPos - p.transform.position; float dy = d.y; d.y = 0f;
            return d.magnitude < 3.9f && Mathf.Abs(dy) < 1.8f;          // ueber den Tresen (Link steht ~100 Einheiten vor dem Verkaeufer)
        }
        public static Vector3 KeeperPos { get { return keeperPos; } }

        // ---------- Texte ----------
        static bool Load(string id)
        {
            object t; pages = null; page = 0; nextId = null; Choice = 0; ChoiceSel = 0;
            if (id == null || !texts.TryGetValue(id, out t)) return false;
            pages = new List<string>();
            foreach (var s in Json.L(t, "pages") ?? new List<object>()) pages.Add(s.ToString());
            nextId = Json.S(t, "next");
            Choice = (int)Json.F(t, "choice");
            return pages.Count > 0;
        }
        static void Show(string id, M m, Action after = null)
        {
            mode = m; afterMsg = after;
            Text = Load(id) ? pages[0] : null;
            if (Text == null && after != null) { afterMsg = null; after(); }
        }
        static bool LastPage { get { return pages != null && page >= pages.Count - 1; } }

        static Item Cur()
        {
            foreach (var it in items) if (it.Shelf == shelf && it.Slot == sel) return it;
            return null;
        }

        // ---------- Ablauf ----------
        public static void Begin()
        {
            shelf = -1; sel = 0;
            Show("0x10BA", M.Talk, () => { mode = M.Shelf; Text = null; });
            OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.6f);
        }

        /// <summary>Eingabe im Laden; false = Laden verlassen.</summary>
        public static bool Update(Vector2 stick, bool a, bool b, bool stickEdge)
        {
            switch (mode)
            {
                case M.Talk: case M.Msg:
                    if (a || b)
                    {
                        if (!LastPage) { page++; Text = pages[page]; OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.8f); }
                        else if (!string.IsNullOrEmpty(nextId) && Load(nextId)) Text = pages[0];
                        else { var f = afterMsg; afterMsg = null; Text = null; if (f != null) f(); else mode = M.Shelf; }
                    }
                    return true;
                case M.Shelf:
                    // Blick auf den Verkaeufer (shelf -1) oder ein Regal (0 links, 1 rechts); Stick waehlt
                    if (stickEdge && Mathf.Abs(stick.x) > 0.5f)
                    {
                        int want = stick.x < 0 ? (shelf == 1 ? -1 : 0) : (shelf == 0 ? -1 : 1);
                        if (shelf != -1 && want != -1 && want == shelf) { sel = Slot(sel, stick); }
                        else { shelf = want; sel = 0; }
                        OotSfx.Play("NA_SE_SY_CURSOR", 0.6f);
                        DescNow();
                    }
                    else if (stickEdge && Mathf.Abs(stick.y) > 0.5f && shelf >= 0) { sel = Slot(sel, stick); OotSfx.Play("NA_SE_SY_CURSOR", 0.6f); DescNow(); }
                    if (shelf >= 0 && Text == null) DescNow();
                    if (a && shelf >= 0 && Cur() != null) { var it = Cur(); Show(it.Prompt, M.Prompt); OotSfx.Play("NA_SE_SY_DECIDE", 0.7f); }
                    if (b) { if (shelf >= 0) { shelf = -1; Text = null; OotSfx.Play("NA_SE_SY_CURSOR", 0.5f); } else { End(); return false; } }
                    return true;
                case M.Desc:
                    return true;
                case M.Prompt:
                    if (Choice > 0 && stickEdge && Mathf.Abs(stick.y) > 0.5f) { ChoiceSel = (ChoiceSel + (stick.y < 0 ? 1 : Choice - 1)) % Choice; OotSfx.Play("NA_SE_SY_CURSOR", 0.6f); }
                    if (b) { mode = M.Shelf; DescNow(); return true; }
                    if (a)
                    {
                        if (ChoiceSel == 0) Buy(Cur());
                        else { mode = M.Shelf; DescNow(); }
                    }
                    return true;
                case M.Fanfare:
                    return true;
            }
            return false;
        }

        static int Slot(int s, Vector2 st)
        {
            // Fach 0..3: 0/1 unten (aussen/innen), 2/3 oben
            if (Mathf.Abs(st.y) > Mathf.Abs(st.x)) return st.y > 0 ? (s < 2 ? s + 2 : s) : (s >= 2 ? s - 2 : s);
            bool outer = (shelf == 0) == (st.x < 0);
            int col = s % 2; col = outer ? 0 : 1; return (s / 2) * 2 + col;
        }

        static void DescNow()
        {
            var it = Cur();
            if (it == null) { Text = null; return; }
            Load(it.Desc); Text = pages != null && pages.Count > 0 ? pages[0] : null; Choice = 0;
        }

        // EnGirlA_CanBuy_* -> EnOssan_HandleCanBuyItem
        static void Buy(Item it)
        {
            if (it == null) return;
            int r = CanBuy(it);
            if (r == 4) { OotSfx.Play("NA_SE_SY_ERROR", 0.8f); Show("0x0085", M.Msg, () => { mode = M.Shelf; DescNow(); }); return; }
            if (r == 2) { OotSfx.Play("NA_SE_SY_ERROR", 0.8f); Show("0x0086", M.Msg, () => { mode = M.Shelf; DescNow(); }); return; }
            OotHud.Rupees -= it.Price;
            if (r == 0 && GiveFanfare != null)
            {
                Give(it, true);
                mode = M.Fanfare; Text = null;
                GiveFanfare(it.Key);
                return;
            }
            Give(it, false);
            OotSfx.Play("NA_SE_SY_GET_ITEM", 0.8f);
            Show("0x0084", M.Msg, () => { mode = M.Shelf; DescNow(); });
        }

        /// <summary>OotPlay: Fanfare/Textbox fertig -> zurueck ans Regal.</summary>
        public static void FanfareDone() { if (mode == M.Fanfare) { mode = M.Shelf; DescNow(); } }

        // 0 = mit Fanfare, 1 = still, 2 = geht gerade nicht, 4 = zu wenig Rubine
        static int CanBuy(Item it)
        {
            switch (it.Key)
            {
                case "shield": if (OotPlay.HasShield) return 2; if (OotHud.Rupees < it.Price) return 4; return 0;
                case "nuts5": case "nuts10":
                    if (Story.Flags.Contains("oot_has_nuts") && OotItems.Nuts >= OotItems.MAX_NUTS) return 2;
                    if (OotHud.Rupees < it.Price) return 4; return Story.Flags.Contains("oot_has_nuts") ? 1 : 0;
                case "stick":
                    if (Story.Flags.Contains("oot_has_sticks") && OotItems.Sticks >= OotItems.MAX_STICKS) return 2;
                    if (OotHud.Rupees < it.Price) return 4; return Story.Flags.Contains("oot_has_sticks") ? 1 : 0;
                case "heart": if (OotCombat.Hp >= OotCombat.MaxHp) return 2; if (OotHud.Rupees < it.Price) return 4; return 1;
                case "seeds": case "arrows10": case "arrows30": return 2;      // ohne Schleuder/Bogen
            }
            return 2;
        }

        static void Give(Item it, bool fanfare)
        {
            switch (it.Key)
            {
                case "shield": if (!fanfare) Story.Flags.Add(OotPlay.ShieldFlag); break;
                case "nuts5": case "nuts10": OotItems.Nuts = Mathf.Min(OotItems.MAX_NUTS, OotItems.Nuts + it.Count); Story.Flags.Add("oot_has_nuts"); break;
                case "stick": OotItems.Sticks = Mathf.Min(OotItems.MAX_STICKS, OotItems.Sticks + it.Count); Story.Flags.Add("oot_has_sticks"); break;
                case "heart": OotCombat.Heal(0x10); break;
            }
            Story.Save();
        }

        static void End() { mode = M.None; Text = null; shelf = -1; Choice = 0; }

        /// <summary>Kamera im Laden: Blick auf Verkaeufer oder Regal.</summary>
        public static bool Cam(out Vector3 pos, out Vector3 look)
        {
            pos = camPos; look = shelf == 0 ? lookL : shelf == 1 ? lookR : lookK;
            return haveCam && mode != M.None && mode != M.Fanfare;
        }
    }
}
