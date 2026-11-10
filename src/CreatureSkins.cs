using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Valheim-Kreaturen im Aussehen des Originalspiels (z. B. Greydwarf -> Nekker, Neck -> Ertrunkener). pack.json:
    //   "creatures": {"Greydwarf": {"model": "models/nekker.glb", "scale": 1.0, "name": {"de": "Nekker"}, "anims": {"idle": "idle", "walk": "walk", "run": "run", "attack": "attack", "death": "death"}}}
    // Nur Optik: KI, Schaden und Beute bleiben Valheim. Jeder Client baut die Optik lokal (gleiches Paket -> gleiches Bild).
    public static class CreatureSkins
    {
        class Cfg { public string Model; public float Scale = 1f; public object Name; public readonly Dictionary<string, string> Anims = new Dictionary<string, string>(); }
        class S { public Character C; public Cfg K; public GameObject Go; public Animation Anim; public Vector3 Last; public float Speed, HideT, ForceUntil; public string Cur, Force; public bool WasAttack, Dead; public int N; }
        static readonly Dictionary<string, Cfg> cfg = new Dictionary<string, Cfg>();
        static readonly Dictionary<Character, S> all = new Dictionary<Character, S>();
        static Pack forPack; static float scanT;

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk; cfg.Clear();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var c = Json.O(meta, "creatures");
            if (c == null) return;
            foreach (var kv in c)
            {
                var m = Json.S(kv.Value, "model"); if (string.IsNullOrEmpty(m)) continue;
                var path = Path.Combine(pk.Dir, m.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path)) { Plugin.Warn("Kreatur-Modell fehlt: " + path); continue; }
                var k = new Cfg { Model = path, Scale = Json.F(kv.Value, "scale", 1f), Name = Json.V(kv.Value, "name") };
                var an = Json.O(kv.Value, "anims"); if (an != null) foreach (var a in an) k.Anims[a.Key] = a.Value as string;
                cfg[kv.Key] = k;
            }
            Plugin.Log("Kreaturen im Original-Aussehen: " + string.Join(", ", new List<string>(cfg.Keys).ToArray()));
        }

        public static void Clear()
        {
            foreach (var s in all.Values) if (s.Go != null) UnityEngine.Object.Destroy(s.Go);
            all.Clear();
        }

        public static void Tick()
        {
            if (Pack.Current != forPack) Setup(Pack.Current);
            if (cfg.Count == 0) return;
            if (Time.time > scanT)
            {
                scanT = Time.time + 0.5f;
                foreach (var c in Character.GetAllCharacters())
                {
                    if (c == null || c.IsPlayer() || all.ContainsKey(c)) continue;
                    Cfg k; if (!cfg.TryGetValue(Utils.GetPrefabName(c.gameObject), out k)) continue;
                    try { Attach(c, k); } catch (Exception e) { Plugin.Warn("Kreatur-Optik: " + e.Message); cfg.Remove(Utils.GetPrefabName(c.gameObject)); }
                }
                var gone = new List<Character>();
                foreach (var kv in all) if (kv.Key == null || (kv.Value.Go == null && kv.Value.Cur != "valheim")) gone.Add(kv.Key);
                foreach (var c in gone) all.Remove(c);
            }
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            foreach (var s in all.Values) if (s.C != null && s.Go != null) Animate(s, dt);
        }

        static void Attach(Character c, Cfg k)
        {
            if (k.Name != null) c.m_name = Pack.T(k.Name);
            if (Pack.Current != null && Pack.Current.ValheimStyle)
            {
                // Valheim-Optik: Valheims eigenes Monster, nur mit dem Namen aus dem Spiel
                all[c] = new S { C = c, K = k, Go = null, Last = c.transform.position, Cur = "valheim" };
                return;
            }
            var go = Glb.Instantiate(k.Model);
            go.name = "GI_Skin";
            go.transform.SetParent(c.transform, false);
            go.transform.localPosition = Vector3.zero; go.transform.localRotation = Quaternion.identity; go.transform.localScale = Vector3.one * k.Scale;
            var s = new S { C = c, K = k, Go = go, Anim = go.GetComponent<Animation>(), Last = c.transform.position };
            all[c] = s;
            Hide(s);
        }

        static void Hide(S s)
        {
            foreach (var r in s.C.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                var t = r.transform; bool mine = false;
                while (t != null) { if (t.name == "GI_Skin") { mine = true; break; } t = t.parent; }
                if (!mine) r.enabled = false;
            }
        }

        static bool Has(S s, string k) { string c; return s.K.Anims.TryGetValue(k, out c) && !string.IsNullOrEmpty(c) && s.Anim != null && s.Anim[c] != null; }

        static void Animate(S s, float dt)
        {
            var pos = s.C.transform.position; var d = pos - s.Last; s.Last = pos;
            float hs = new Vector2(d.x, d.z).magnitude / dt; if (hs > 30f) hs = 0f;
            s.Speed = Mathf.Lerp(s.Speed, hs, 1f - Mathf.Exp(-dt * 8f));
            if (Time.time > s.HideT) { s.HideT = Time.time + 1f; Hide(s); }
            if (s.Anim == null) return;
            string want;
            bool dead = s.C.IsDead();
            bool atk = false; try { atk = s.C.InAttack(); } catch { }
            if (atk && !s.WasAttack && Has(s, "attack"))
            {
                s.N++; s.Force = s.N % 2 == 0 && Has(s, "attack2") ? "attack2" : "attack";
                var st0 = s.Anim[s.K.Anims[s.Force]]; s.ForceUntil = Time.time + Mathf.Min(st0.length, 2f) * 0.9f; s.Cur = null;
            }
            s.WasAttack = atk;
            if (dead && Has(s, "death")) want = "death";
            else if (s.Force != null && Time.time < s.ForceUntil) want = s.Force;
            else if (s.Speed < 0.3f) want = "idle";
            else if (s.Speed < 3.5f || !Has(s, "run")) want = "walk";
            else want = "run";
            if (!Has(s, want)) want = Has(s, "idle") ? "idle" : null;
            if (want == null) return;
            var clip = s.K.Anims[want]; var st = s.Anim[clip];
            if (s.Cur != want)
            {
                bool once = want == "death" || want.StartsWith("attack");
                st.wrapMode = once ? WrapMode.ClampForever : WrapMode.Loop; if (once) st.time = 0f;
                s.Anim.CrossFade(clip, 0.2f); s.Cur = want;
            }
            st.speed = want == "walk" ? Mathf.Clamp(s.Speed / 1.5f, 0.6f, 1.8f) : want == "run" ? Mathf.Clamp(s.Speed / 5f, 0.7f, 1.6f) : 1f;
        }

        public static string Info()
        {
            var sb = new System.Text.StringBuilder("kreaturen " + all.Count + ": ");
            foreach (var s in all.Values) sb.Append(Utils.GetPrefabName(s.C.gameObject) + "=" + s.Cur + " ");
            return sb.ToString();
        }
    }
}
