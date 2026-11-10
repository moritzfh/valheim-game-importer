using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Figuren aus dem Spielpaket. Jeder Spieler baut sie lokal nach (kein Netzwerk-Objekt): gleiches Paket -> gleiche Figuren.
    // Koerper = Valheims Spielermodell (ohne Spieler-Logik) mit Haaren, Bart, Hautfarbe und Kleidung aus "look".
    public static class Npcs
    {
        class Inst { public Pack.Npc N; public GameObject Go; public bool Barked; public Vector3 Pos; public bool HasPos; }
        static readonly Dictionary<string, Inst> all = new Dictionary<string, Inst>();
        static Pack forPack;
        static float tickT;
        const float SpawnDist = 70f, DespawnDist = 95f, BarkDist = 7f;

        public static void Tick()
        {
            var pk = Pack.Current; var pl = Player.m_localPlayer;
            if (pk != forPack) { Clear(); forPack = pk; if (pk != null) foreach (var n in pk.Npcs) all[n.Id] = new Inst { N = n }; }
            if (pk == null || pl == null || ZNetScene.instance == null || Time.time < tickT) return;
            tickT = Time.time + 0.5f;
            var me = pl.transform.position;
            foreach (var it in all.Values)
            {
                if (!it.HasPos) { Vector3 p; if (!pk.Pos(it.N.Pos, out p)) continue; it.Pos = p; it.HasPos = true; }
                float d = Story.Flat(me, it.Pos);
                bool want = d < SpawnDist && Story.Check(it.N.If);
                if (it.Go == null && want && ZoneSystem.instance.IsZoneLoaded(it.Pos))
                {
                    try { it.Go = Spawn(it.N, it.Pos); Plugin.Log("NPC " + it.N.Id + " erscheint bei " + it.Go.transform.position); }
                    catch (Exception e) { Plugin.Warn("NPC " + it.N.Id + ": " + e.Message); it.HasPos = false; }
                }
                else if (it.Go != null && (d > DespawnDist || !Story.Check(it.N.If))) { UnityEngine.Object.Destroy(it.Go); it.Go = null; }
                if (it.Go != null && !string.IsNullOrEmpty(it.N.Pose) && !StoryUi.Busy) ApplyPose(it.Go, it.N.Pose);
                if (it.Go != null)
                {
                    float dn = Vector3.Distance(me, it.Go.transform.position);
                    if (!it.Barked && dn < BarkDist && it.N.Bark != null && !StoryUi.Busy) { it.Barked = true; Say(it.N.Id, Pack.T(it.N.Bark)); }
                    else if (it.Barked && dn > BarkDist * 3f) it.Barked = false;
                }
            }
        }

        // Haltung aus npcs.json ("pose"): "chair" sitzt auf einem Stuhl (auch schlafend), "sit" auf dem Boden
        static void ApplyPose(GameObject go, string pose)
        {
            var an = go.GetComponentInChildren<Animator>();
            if (an == null || go.GetComponent<Animation>() != null) return;
            try
            {
                an.SetBool("attach_chair", pose == "chair" || pose == "sleep");
                an.SetBool("emote_sit", pose == "sit");
            }
            catch { }
        }

        public static void Clear()
        {
            foreach (var it in all.Values) if (it.Go != null) UnityEngine.Object.Destroy(it.Go);
            all.Clear();
        }

        public static GameObject Get(string id) { Inst it; return all.TryGetValue(id ?? "", out it) ? it.Go : null; }

        // nach einer Cutscene: Figur zurueck an ihren Platz aus npcs.json, stehend
        public static void ResetPlace(string id)
        {
            Inst it;
            if (!all.TryGetValue(id ?? "", out it) || it.Go == null || !it.HasPos) return;
            var p = it.Pos; if (float.IsNaN(p.y)) p.y = Pack.Ground(p.x, p.z);
            it.Go.transform.position = p; it.Go.transform.rotation = Quaternion.Euler(0f, it.N.Yaw, 0f);
            var an = it.Go.GetComponentInChildren<Animator>();
            if (an != null) { try { an.SetBool("emote_sit", false); an.SetBool("attach_chair", false); an.SetFloat("forward_speed", 0f); an.SetTrigger("emote_stop"); } catch { } }
        }

        public static void Say(string id, string text)
        {
            var go = Get(id);
            if (go == null || string.IsNullOrEmpty(text) || Chat.instance == null) return;
            Chat.instance.SetNpcText(go, Vector3.up * 2.25f, 20f, 5f, "", text, false);
        }

        public static string Info()
        {
            var me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            var sb = new System.Text.StringBuilder("npcs " + all.Count + ":");
            foreach (var it in all.Values) sb.Append(" " + it.N.Id + (it.Go != null ? "[da]" : "") + "@" + (it.HasPos ? it.Pos.x.ToString("F0") + "," + it.Pos.z.ToString("F0") + " (" + Story.Flat(me, it.Pos).ToString("F0") + " m)" : "?"));
            return sb.ToString();
        }

        // ---------- Koerper bauen ----------
        static int hitLayer = -1;

        static GameObject Spawn(Pack.Npc n, Vector3 pos)
        {
            if (float.IsNaN(pos.y)) pos.y = Pack.Ground(pos.x, pos.z);
            // Valheim-Optik: Figuren mit "look" als Valheim-Charakter, auch wenn ein Originalmodell da waere
            bool vstyle = Pack.Current != null && Pack.Current.ValheimStyle && n.Look != null;
            if (!string.IsNullOrEmpty(n.Model) && !vstyle) return SpawnModel(n, pos);
            var prefab = ZNetScene.instance.GetPrefab("Player");
            if (prefab == null) throw new Exception("Spieler-Prefab fehlt");

            // inaktiv erzeugen: so laeuft keine Spieler-Logik an (kein Awake), bevor wir sie entfernt haben
            var holder = new GameObject("gi_npc_holder"); holder.SetActive(false);
            var go = UnityEngine.Object.Instantiate(prefab, holder.transform);
            go.name = "gi_npc_" + n.Id;
            Strip(go);

            var talk = go.AddComponent<NpcTalk>(); talk.Id = n.Id;
            var hit = new GameObject("gi_hit"); hit.transform.SetParent(go.transform, false);
            hit.layer = HitLayer();
            var cap = hit.AddComponent<CapsuleCollider>(); cap.center = new Vector3(0f, 0.95f, 0f); cap.height = 1.9f; cap.radius = 0.35f;

            go.transform.localPosition = Vector3.zero;
            holder.transform.position = pos;
            holder.transform.rotation = Quaternion.Euler(0f, n.Yaw, 0f);
            // Beim Herausloesen aus dem inaktiven Halter wacht das Objekt auf: ZNetView darf dabei kein Netzwerk-Objekt anlegen
            ZNetView.m_forceDisableInit = true;
            try { go.transform.SetParent(null, true); go.SetActive(true); } finally { ZNetView.m_forceDisableInit = false; }
            UnityEngine.Object.Destroy(holder);

            foreach (var an in go.GetComponentsInChildren<Animator>(true))
            {
                an.fireEvents = false;               // Fussschritt-Ereignisse usw. haben keinen Empfaenger mehr
                an.updateMode = AnimatorUpdateMode.Normal;
                foreach (var p in an.parameters) if (p.name == "onGround" && p.type == AnimatorControllerParameterType.Bool) an.SetBool(p.nameHash, true);
            }
            ApplyLook(go, n.Look);
            return go;
        }

        // Figur nur fuer eine Cutscene (Double des Spielers, Darsteller ohne Eintrag): nicht ansprechbar, Aufrufer raeumt weg
        public static GameObject SpawnTemp(string id, object look, Vector3 pos, float yaw)
        {
            var go = Spawn(new Pack.Npc { Id = id, Look = look ?? new Dictionary<string, object>(), Yaw = yaw }, pos);
            foreach (var t in go.GetComponentsInChildren<NpcTalk>(true)) UnityEngine.Object.Destroy(t);
            var hit = go.transform.Find("gi_hit"); if (hit != null) UnityEngine.Object.Destroy(hit.gameObject);
            return go;
        }

        // Originalfigur aus dem Paket (.glb mit eigenem Skelett und eigenen Animationen)
        static GameObject SpawnModel(Pack.Npc n, Vector3 pos)
        {
            var go = Glb.Instantiate(System.IO.Path.Combine(Pack.Current.Dir, n.Model));
            go.name = "gi_npc_" + n.Id;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, n.Yaw, 0f);
            go.transform.localScale = Vector3.one * n.Scale;
            go.AddComponent<NpcTalk>().Id = n.Id;
            var hit = new GameObject("gi_hit"); hit.transform.SetParent(go.transform, false);
            hit.layer = HitLayer();
            var cap = hit.AddComponent<CapsuleCollider>(); cap.center = new Vector3(0f, 0.95f / Mathf.Max(0.01f, n.Scale), 0f); cap.height = 1.9f / Mathf.Max(0.01f, n.Scale); cap.radius = 0.35f / Mathf.Max(0.01f, n.Scale);
            Play(go, n.Anim, true);
            return go;
        }

        // Animation einer Figur abspielen (nur Originalfiguren aus .glb haben eigene Clips)
        public static bool Play(GameObject go, string clip, bool loop, float fade = 0.25f)
        {
            var an = go != null ? go.GetComponent<Animation>() : null;
            if (an == null) return false;
            AnimationState st = null;
            if (!string.IsNullOrEmpty(clip)) st = an[clip];
            if (st == null && an.clip != null) st = an[an.clip.name];
            if (st == null) return false;
            st.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
            an.CrossFade(st.name, fade);
            return true;
        }

        public static string Clips(string id)
        {
            var an = Get(id) != null ? Get(id).GetComponent<Animation>() : null;
            if (an == null) return id + ": keine Clips";
            var l = new List<string>(); foreach (AnimationState st in an) l.Add(st.name + " (" + st.length.ToString("F1") + " s)");
            return id + ": " + string.Join(", ", l.ToArray());
        }

        // alles entfernen, was das Modell zum echten Spieler macht; uebrig bleiben Aussehen (VisEquipment), Animator, Meshes
        static void Strip(GameObject go)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform || c is Renderer || c is MeshFilter || c is Animator || c is LODGroup || c is VisEquipment || c is ZNetView) continue;
                    if (c is Rigidbody && pass == 0) continue;            // erst wenn nichts mehr davon abhaengt
                    if (c is Collider || c is Rigidbody || c is Behaviour || c is Joint || c is AudioSource || c is ParticleSystem || c is MonoBehaviour)
                        UnityEngine.Object.DestroyImmediate(c);
                }
            }
        }

        static int HitLayer()
        {
            if (hitLayer >= 0) return hitLayer;
            int mask = 0;
            try { var f = AccessTools.Field(typeof(Player), "m_interactMask"); if (f != null && Player.m_localPlayer != null) { var o = f.GetValue(Player.m_localPlayer); mask = o is LayerMask ? ((LayerMask)o).value : Convert.ToInt32(o); } } catch (Exception e) { Plugin.Quiet(e, "Npcs.mask"); }
            foreach (var name in new[] { "character", "Default", "piece", "item" })
            {
                int l = LayerMask.NameToLayer(name);
                if (l >= 0 && (mask == 0 || (mask & (1 << l)) != 0)) { hitLayer = l; break; }
            }
            if (hitLayer < 0) hitLayer = 0;
            Plugin.Log("NPCs: Treffer-Ebene " + LayerMask.LayerToName(hitLayer) + " (Maske " + mask + ")");
            return hitLayer;
        }

        static readonly Dictionary<string, MethodInfo> visM = new Dictionary<string, MethodInfo>();
        static void Vis(VisEquipment v, string method, params object[] args)
        {
            MethodInfo m;
            var key = method + "/" + string.Join(",", Array.ConvertAll(args, a => a != null ? a.GetType().Name : "null"));
            if (!visM.TryGetValue(key, out m))
            {
                foreach (var x in typeof(VisEquipment).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (x.Name != method) continue;
                    var ps = x.GetParameters();
                    if (ps.Length != args.Length) continue;
                    bool ok = true;
                    for (int i = 0; i < ps.Length && ok; i++) ok = args[i] == null ? !ps[i].ParameterType.IsValueType : ps[i].ParameterType.IsAssignableFrom(args[i].GetType());
                    if (ok) { m = x; break; }
                }
                visM[key] = m;
                if (m == null) Plugin.Log("NPCs: VisEquipment." + key + " gibt's nicht");
            }
            if (m == null) { Plugin.Quiet(new Exception("fehlt"), "VisEquipment." + method); return; }
            try { m.Invoke(v, args); } catch (Exception e) { Plugin.Quiet(e, "VisEquipment." + method); }
        }

        // Ausruestung/Haare: Valheim nimmt je nach Version den Namen oder dessen Hash, manchmal mit Variante
        static void SetItem(VisEquipment v, string method, string item)
        {
            if (item == null) return;                    // "" = Platz leeren (z. B. Kleidung ausziehen in Cutscenes)
            MethodInfo best = null;
            foreach (var x in typeof(VisEquipment).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (x.Name != method) continue;
                var p0 = x.GetParameters();
                if (p0.Length == 0 || (p0[0].ParameterType != typeof(string) && p0[0].ParameterType != typeof(int))) continue;
                if (best == null || p0[0].ParameterType == typeof(string)) best = x;
            }
            if (best == null) { Plugin.Log("NPCs: VisEquipment." + method + " gibt's nicht"); return; }
            {
                var x = best; var ps = x.GetParameters();
                var a = new object[ps.Length]; a[0] = ps[0].ParameterType == typeof(int) ? (object)(item.Length > 0 ? item.GetStableHashCode() : 0) : item;
                for (int i = 1; i < ps.Length; i++) a[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
                try { x.Invoke(v, a); } catch (Exception e) { Plugin.Log("NPCs: VisEquipment." + method + ": " + e.Message); }
            }
        }

        static Vector3 Col(object o, Vector3 def)
        {
            var l = o as List<object>;
            return l != null && l.Count >= 3 ? new Vector3((float)(double)l[0], (float)(double)l[1], (float)(double)l[2]) : def;
        }

        public static void ApplyLook(GameObject go, object look)
        {
            var v = go.GetComponent<VisEquipment>();
            if (v == null) return;
            try { var f = AccessTools.Field(typeof(VisEquipment), "m_isPlayer"); if (f != null) f.SetValue(v, true); } catch (Exception e) { Plugin.Quiet(e, "Npcs.isPlayer"); }
            bool female = Json.S(look, "model", "male") == "female";
            Vis(v, "SetModel", female ? 1 : 0);
            Vis(v, "SetSkinColor", Col(Json.V(look, "skin"), new Vector3(0.85f, 0.7f, 0.58f)));
            Vis(v, "SetHairColor", Col(Json.V(look, "hair_color"), new Vector3(0.3f, 0.22f, 0.12f)));
            SetItem(v, "SetHairItem", Json.S(look, "hair", "Hair1"));
            SetItem(v, "SetBeardItem", female ? "BeardNone" : Json.S(look, "beard", "BeardNone"));
            SetItem(v, "SetChestItem", Json.S(look, "chest", "ArmorRagsChest"));
            SetItem(v, "SetLegItem", Json.S(look, "legs", "ArmorRagsLegs"));
            SetItem(v, "SetHelmetItem", Json.S(look, "helmet", ""));
            SetItem(v, "SetShoulderItem", Json.S(look, "shoulder", ""));
            SetItem(v, "SetRightItem", Json.S(look, "right", ""));
            SetItem(v, "SetLeftItem", Json.S(look, "left", ""));
            // auf dem Ruecken (wie weggesteckte Waffen): z. B. Links Schild und Schwert
            SetItem(v, "SetLeftBackItem", Json.S(look, "left_back", ""));
            SetItem(v, "SetRightBackItem", Json.S(look, "right_back", ""));
            var bs = go.GetComponent<BackItemScale>();
            if (Json.Has(look, "back_scale")) { if (bs == null) bs = go.AddComponent<BackItemScale>(); bs.K = Json.F(look, "back_scale", 1f); }
            else if (bs != null) UnityEngine.Object.Destroy(bs);
            // Farbe der Brustkleidung umfaerben (z. B. Links Tunika gruen): Farbton der bunten Pixel ersetzen
            var ct = go.GetComponent<ChestTint>();
            if (Json.Has(look, "chest_hue"))
            {
                if (ct == null) ct = go.AddComponent<ChestTint>();
                ct.Hue = Json.F(look, "chest_hue", 110f) / 360f; ct.Sat = Json.F(look, "chest_sat", 1f); ct.Val = Json.F(look, "chest_val", 1f);
                ct.Done = null;
            }
            else if (ct != null) UnityEngine.Object.Destroy(ct);
        }
    }

    // Rueckengegenstaende verkleinern (Valheim-Schild ist fuer Kind-Link viel zu gross)
    public class BackItemScale : MonoBehaviour
    {
        public float K = 1f;
        VisEquipment v; System.Reflection.FieldInfo fl, fr;
        GameObject lastL, lastR;
        void LateUpdate()
        {
            if (v == null) { v = GetComponent<VisEquipment>(); fl = HarmonyLib.AccessTools.Field(typeof(VisEquipment), "m_leftBackItemInstance"); fr = HarmonyLib.AccessTools.Field(typeof(VisEquipment), "m_rightBackItemInstance"); }
            if (v == null) return;
            var l = fl != null ? fl.GetValue(v) as GameObject : null; var r = fr != null ? fr.GetValue(v) as GameObject : null;
            if (l != null && l != lastL) { l.transform.localScale *= K; lastL = l; }
            if (r != null && r != lastR) { r.transform.localScale *= K; lastR = r; }
        }
    }

    public class ChestTint : MonoBehaviour
    {
        public float Hue = 0.3f, Sat = 1f, Val = 1f;
        public Texture Done;
        static readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();
        static readonly int chestTex = Shader.PropertyToID("_ChestTex");
        VisEquipment v; System.Reflection.FieldInfo fBody, fChest;
        readonly HashSet<Material> doneMats = new HashSet<Material>();

        Texture2D Tint(Texture src)
        {
            if (src == null) return null;
            string key = src.GetInstanceID() + "_" + Hue + "_" + Sat + "_" + Val;
            Texture2D t;
            if (cache.TryGetValue(key, out t) && t != null) return t;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, true);
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = t.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                float h, s, vv; Color.RGBToHSV(px[i], out h, out s, out vv);
                if (s < 0.18f) continue;                               // Grau/Leder-Schatten bleiben
                var c = Color.HSVToRGB(Hue, Mathf.Clamp01(s * Sat), Mathf.Clamp01(vv * Val)); c.a = px[i].a; px[i] = c;
            }
            t.SetPixels(px); t.Apply(true); t.name = src.name + "_tint";
            cache[key] = t;
            return t;
        }

        void LateUpdate()
        {
            try
            {
                if (v == null) { v = GetComponent<VisEquipment>(); fBody = HarmonyLib.AccessTools.Field(typeof(VisEquipment), "m_bodyModel"); fChest = HarmonyLib.AccessTools.Field(typeof(VisEquipment), "m_chestItemInstances"); }
                if (v == null) return;
                var body = fBody != null ? fBody.GetValue(v) as SkinnedMeshRenderer : null;
                if (body != null)
                {
                    var m = body.material;
                    if (m != null && m.HasProperty(chestTex))
                    {
                        var cur = m.GetTexture(chestTex);
                        if (cur != null && cur != Done) { Done = Tint(cur); if (Done != null) m.SetTexture(chestTex, Done); }
                    }
                }
                var inst = fChest != null ? fChest.GetValue(v) as List<GameObject> : null;
                if (inst != null) foreach (var g in inst)
                    {
                        if (g == null) continue;
                        foreach (var r in g.GetComponentsInChildren<Renderer>(true))
                            foreach (var m in r.materials)
                            {
                                if (m == null || doneMats.Contains(m) || m.mainTexture == null) continue;
                                var tt = Tint(m.mainTexture); if (tt != null) m.mainTexture = tt;
                                doneMats.Add(m);
                            }
                    }
            }
            catch (Exception e) { Plugin.Quiet(e, "ChestTint"); enabled = false; }
        }
    }

    // Ansprechen: "[E] Sprechen"
    public class NpcTalk : MonoBehaviour, Hoverable, Interactable
    {
        public string Id;

        public string GetHoverName() { return Pack.Current != null ? Pack.Current.NpcName(Id) : Id; }
        public float GetHoverOffset() { return 0f; }

        public string GetHoverText()
        {
            var n = Pack.Current != null && Pack.Current.NpcById.ContainsKey(Id) ? Pack.Current.NpcById[Id] : null;
            var s = GetHoverName();
            if (n != null && !string.IsNullOrEmpty(n.Dialogue)) s += "\n[<color=yellow><b>$KEY_Use</b></color>] " + Story.L("Sprechen", "Talk");
            return Localization.instance.Localize(s);
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold || StoryUi.Busy) return false;
            return StoryUi.StartDialogue(Id);
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }
    }
}
