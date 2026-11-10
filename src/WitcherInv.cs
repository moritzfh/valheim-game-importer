using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Inventar im Hexer-Stil (Taste I): links Ausruestung (Stahl-/Silberschwert, Ruestung ...), rechts das echte
    // Valheim-Inventar als Raster, unten Traenke, Vitalitaet/Ausdauer/Toxizitaet/Gewicht. Klick = benutzen/ausruesten.
    // Bilder: pack.json "witcher": {"inventory": {"bg": "icons/inv_bg.png", "slots": {"steel": "icons/steel.png", ...}}}
    public static class WitcherInv
    {
        public static bool Open;
        static Texture2D bg; static readonly Dictionary<string, Texture2D> slotTex = new Dictionary<string, Texture2D>();
        static Pack forPack;
        static string hover; static Vector2 scroll;
        static GUIStyle title, small, label, tip;
        static readonly Color gold = new Color(0.86f, 0.74f, 0.47f), dim = new Color(0.62f, 0.58f, 0.5f);

        static void Setup(Pack pk)
        {
            forPack = pk; bg = null; slotTex.Clear();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var inv = Json.O(Json.V(meta, "witcher"), "inventory");
            if (inv == null) return;
            bg = Img(pk, Json.S(inv, "bg"));
            var sl = Json.O(inv, "slots");
            if (sl != null) foreach (var kv in sl) slotTex[kv.Key] = Img(pk, kv.Value as string);
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
            if (p == null || !Witcher.On) { Open = false; return; }
            if (Input.GetKeyDown(KeyCode.I) && (Open || Horse.FreeInput())) Open = !Open;
            if (Open && Input.GetKeyDown(KeyCode.Escape)) Open = false;
            if (Open) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
        }

        static void Styles(float s)
        {
            title = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(30 * s), fontStyle = FontStyle.Bold, normal = { textColor = gold } };
            small = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(15 * s), normal = { textColor = dim } };
            label = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(18 * s), normal = { textColor = new Color(0.92f, 0.88f, 0.78f) } };
            tip = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(16 * s), wordWrap = true, alignment = TextAnchor.UpperLeft, normal = { textColor = Color.white } };
        }

        static void Panel(Rect r, float a = 0.72f)
        {
            GUI.color = new Color(0.05f, 0.045f, 0.04f, a); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = new Color(gold.r, gold.g, gold.b, 0.55f);
            GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), Texture2D.whiteTexture); GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        static void Icon(Rect r, ItemDrop.ItemData it)
        {
            Sprite sp = null;
            try { sp = it.GetIcon(); } catch { }
            if (sp == null) return;
            var tr = sp.textureRect; var tx = sp.texture;
            GUI.DrawTextureWithTexCoords(r, tx, new Rect(tr.x / tx.width, tr.y / tx.height, tr.width / tx.width, tr.height / tx.height));
        }

        static string Name(ItemDrop.ItemData it) { try { return Localization.instance.Localize(it.m_shared.m_name); } catch { return "?"; } }

        public static void Gui()
        {
            if (!Open) return;
            var p = Player.m_localPlayer; if (p == null) return;
            float s = Screen.height / 1080f;
            if (title == null || title.fontSize != Mathf.RoundToInt(30 * s)) Styles(s);
            // Hintergrund
            GUI.color = new Color(0f, 0f, 0f, 0.82f); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture); GUI.color = Color.white;
            if (bg != null) { GUI.color = new Color(1, 1, 1, 0.35f); GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), bg, ScaleMode.ScaleAndCrop); GUI.color = Color.white; }
            GUI.Label(new Rect(60 * s, 30 * s, 800 * s, 44 * s), Story.L("INVENTAR", "INVENTORY"), title);
            GUI.Label(new Rect(60 * s, 72 * s, 800 * s, 24 * s), Story.L("[I] schließen · Klick = benutzen / ausrüsten", "[I] close · click = use / equip"), small);
            hover = null;
            var inv = p.GetInventory();
            // ---- Ausruestung (links) ----
            var eq = new Rect(60 * s, 120 * s, 420 * s, 640 * s); Panel(eq);
            GUI.Label(new Rect(eq.x + 20 * s, eq.y + 12 * s, 380 * s, 30 * s), Story.L("Ausrüstung", "Equipment"), label);
            var equipped = new List<ItemDrop.ItemData>();
            foreach (var it in inv.GetAllItems()) if (it.m_equipped) equipped.Add(it);
            Func<Func<ItemDrop.ItemData, bool>, ItemDrop.ItemData> find = f => equipped.Find(x => { try { return f(x); } catch { return false; } });
            var slots = new[]
            {
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Stahlschwert", "Steel sword"), find(x => x.IsWeapon() && x.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Bow && x.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Torch)),
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Silberschwert", "Silver sword"), find(x => x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow || x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)),
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Rüstung", "Armor"), find(x => x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest)),
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Hose", "Trousers"), find(x => x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs)),
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Kopf", "Head"), find(x => x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet)),
                new KeyValuePair<string, ItemDrop.ItemData>(Story.L("Umhang", "Cloak"), find(x => x.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder)),
            };
            string[] keys = { "steel", "silver", "armor", "trousers", "head", "cloak" };
            for (int i = 0; i < slots.Length; i++)
            {
                float y = eq.y + 56 * s + i * 96 * s;
                var r = new Rect(eq.x + 20 * s, y, 84 * s, 84 * s); Panel(r, 0.5f);
                Texture2D ph; slotTex.TryGetValue(keys[i], out ph);
                if (slots[i].Value != null) Icon(new Rect(r.x + 6 * s, r.y + 6 * s, 72 * s, 72 * s), slots[i].Value);
                else if (ph != null) { GUI.color = new Color(1, 1, 1, 0.35f); GUI.DrawTexture(new Rect(r.x + 6 * s, r.y + 6 * s, 72 * s, 72 * s), ph, ScaleMode.ScaleToFit); GUI.color = Color.white; }
                GUI.Label(new Rect(r.xMax + 14 * s, y + 10 * s, 290 * s, 26 * s), slots[i].Key, small);
                GUI.Label(new Rect(r.xMax + 14 * s, y + 36 * s, 290 * s, 30 * s), slots[i].Value != null ? Name(slots[i].Value) : "—", label);
                if (slots[i].Value != null && r.Contains(Event.current.mousePosition)) hover = Name(slots[i].Value);
            }
            // ---- Inventar-Raster (rechts) ----
            var grid = new Rect(520 * s, 120 * s, Screen.width - 580 * s, 640 * s); Panel(grid);
            GUI.Label(new Rect(grid.x + 20 * s, grid.y + 12 * s, 400 * s, 30 * s), Story.L("Gegenstände", "Items"), label);
            float cell = 80 * s; int cols = Mathf.Max(4, Mathf.FloorToInt((grid.width - 40 * s) / (cell + 8 * s)));
            int n = 0;
            foreach (var it in inv.GetAllItems())
            {
                float x = grid.x + 20 * s + (n % cols) * (cell + 8 * s), y = grid.y + 56 * s + (n / cols) * (cell + 8 * s);
                if (y + cell > grid.yMax - 10 * s) break;
                var r = new Rect(x, y, cell, cell);
                Panel(r, it.m_equipped ? 0.85f : 0.45f);
                if (it.m_equipped) { GUI.color = new Color(gold.r, gold.g, gold.b, 0.25f); GUI.DrawTexture(r, Texture2D.whiteTexture); GUI.color = Color.white; }
                Icon(new Rect(x + 6 * s, y + 6 * s, cell - 12 * s, cell - 12 * s), it);
                if (it.m_stack > 1) GUI.Label(new Rect(x + 4 * s, y + cell - 24 * s, cell - 8 * s, 22 * s), it.m_stack.ToString(), small);
                if (r.Contains(Event.current.mousePosition))
                {
                    hover = Name(it) + (it.m_shared.m_description != null ? "\n" + Localization.instance.Localize(it.m_shared.m_description) : "");
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 0) { try { p.UseItem(inv, it, true); } catch (Exception e) { Plugin.Quiet(e, "Inv.Use"); } Event.current.Use(); }
                }
                n++;
            }
            // ---- Werte (unten) ----
            var st = new Rect(60 * s, 780 * s, Screen.width - 120 * s, 120 * s); Panel(st);
            Bar(new Rect(st.x + 20 * s, st.y + 20 * s, 380 * s, 22 * s), p.GetHealth() / Mathf.Max(1f, p.GetMaxHealth()), new Color(0.75f, 0.12f, 0.1f), Story.L("Vitalität ", "Vitality ") + Mathf.CeilToInt(p.GetHealth()) + "/" + Mathf.CeilToInt(p.GetMaxHealth()));
            Bar(new Rect(st.x + 20 * s, st.y + 62 * s, 380 * s, 22 * s), p.GetStamina() / Mathf.Max(1f, p.GetMaxStamina()), new Color(0.85f, 0.7f, 0.2f), Story.L("Ausdauer ", "Stamina ") + Mathf.CeilToInt(p.GetStamina()));
            Bar(new Rect(st.x + 440 * s, st.y + 20 * s, 380 * s, 22 * s), Witcher.Toxicity / 100f, new Color(0.35f, 0.75f, 0.3f), Story.L("Toxizität ", "Toxicity ") + Mathf.RoundToInt(Witcher.Toxicity) + "%");
            float w = inv.GetTotalWeight(), mw = p.GetMaxCarryWeight();
            Bar(new Rect(st.x + 440 * s, st.y + 62 * s, 380 * s, 22 * s), w / Mathf.Max(1f, mw), new Color(0.55f, 0.5f, 0.42f), Story.L("Gewicht ", "Weight ") + Mathf.RoundToInt(w) + "/" + Mathf.RoundToInt(mw));
            Witcher.DrawPotionRow(new Rect(st.x + 870 * s, st.y + 14 * s, st.width - 890 * s, 92 * s), s);
            if (hover != null)
            {
                var mp = Event.current.mousePosition;
                GUI.Box(new Rect(mp.x + 18, mp.y + 18, 360 * s, 110 * s), hover, tip);
            }
        }

        static void Bar(Rect r, float f, Color c, string text)
        {
            GUI.color = new Color(0, 0, 0, 0.7f); GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = c; GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(f), r.height), Texture2D.whiteTexture);
            GUI.color = Color.white; GUI.Label(new Rect(r.x + 8, r.y - 1, r.width, r.height + 4), text, small);
        }
    }
}
