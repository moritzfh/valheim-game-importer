using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Bewegliche Objekte im Deku-Baum, Logik 1:1 aus der Decomp (20 Bilder/s, OoT-Einheiten):
    ///  - Obj_Lift: Plattform wackelt, sobald Link drauf steht (20 Bilder), faellt, zerbricht am Boden, Schalter-Merker -> bleibt weg
    ///  - Bg_Ydan_Maruta: Leiter (mit der Schleuder abschiessen -> faellt herunter, beklettterbar) / rollender Stachelstamm (stoesst Link weg)
    ///  - Bg_Ydan_Hasi: schwimmender Block (pendelt), Wasserspiegel (Schalter senkt ihn 600 Bilder lang), drei Bloecke (Schalter hebt sie 260 Bilder)
    ///  - Obj_Switch (Boden): drauftreten -> Merker, bleibt unten bis der Merker geloescht wird
    ///  - Obj_Oshihiki / Obj_Makeoshihiki: Schiebeblock (20 Einheiten je Stoss, faellt Kanten hinunter, setzt Merker am Zielplatz)
    /// Schalter-Merker: 0x00-0x37 dauerhaft ("oot_ydan_sw_N", wie Elf_Msg), 0x38-0x3F nur bis zum Verlassen.
    /// pack.json "oot_ydan_obj" (oot_ydan.objects).
    /// </summary>
    public static class OotYdanObj
    {
        static Pack forPack; static object cfg; static GameObject holder;
        static Vector3 org; static float S = 0.033f;
        static readonly Dictionary<string, string> models = new Dictionary<string, string>();
        static readonly List<Obj> objs = new List<Obj>();
        static readonly HashSet<int> temp = new HashSet<int>();
        static float acc; public static int Frames; static string lastErr;
        static readonly Dictionary<int, List<string>> clear = new Dictionary<int, List<string>>();
        static bool Clear(int room) { List<string> l; if (!clear.TryGetValue(room, out l)) return true; foreach (var f in l) if (!Story.Flags.Contains(f)) return false; return true; }
        static int GroundMask { get { return LayerMask.GetMask("Default", "static_solid", "terrain", "piece"); } }

        public static bool Flag(int n)
        {
            if (n < 0 || n > 0x3F) return false;
            return n >= 0x38 ? temp.Contains(n) : Story.Flags.Contains("oot_ydan_sw_" + n);
        }
        public static void SetFlag(int n, bool on)
        {
            if (n < 0 || n > 0x3F) return;
            if (n >= 0x38) { if (on) temp.Add(n); else temp.Remove(n); return; }
            var k = "oot_ydan_sw_" + n;
            if (on) { if (Story.Flags.Add(k)) Story.Save(); }
            else if (Story.Flags.Remove(k)) Story.Save();
        }

        static Vector3 V3(List<object> l) { return l == null || l.Count < 3 ? Vector3.zero : new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2])); }
        static Vector3 W(Vector3 o) { return org + new Vector3(o.x * S, o.y * S, -o.z * S); }
        static Vector3 L(Vector3 w) { var d = w - org; return new Vector3(d.x / S, d.y / S, -d.z / S); }
        static float SinS(int a) { return Mathf.Sin((short)a * (Mathf.PI / 32768f)); }
        static float CosS(int a) { return Mathf.Cos((short)a * (Mathf.PI / 32768f)); }
        static bool StepToF(ref float v, float target, float step)
        {
            if (step != 0f) { if (target < v) step = -step; v += step; if ((v - target) * step >= 0f) { v = target; return true; } }
            else if (target == v) return true;
            return false;
        }

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            cfg = Json.V(meta, "oot_ydan_obj"); if (cfg == null) return;
            org = V3(Json.L(cfg, "origin")); S = Json.F(cfg, "s", 0.033f);
            var cl = Json.V(cfg, "clear") as Dictionary<string, object>;
            if (cl != null) foreach (var kv in cl) { var l = new List<string>(); foreach (var f in (kv.Value as List<object>) ?? new List<object>()) l.Add(f.ToString()); clear[int.Parse(kv.Key)] = l; }
            var md = Json.V(cfg, "models") as Dictionary<string, object>;
            if (md != null) foreach (var kv in md) models[kv.Key] = kv.Value.ToString();
            foreach (var e in Json.L(cfg, "list") ?? new List<object>())
            {
                Obj o = null;
                switch (Json.S(e, "type"))
                {
                    case "lift": o = new Lift(e); break;
                    case "ladder": o = new Ladder(e); break;
                    case "spikelog": o = new SpikeLog(e); break;
                    case "hasi_block": o = new HasiBlock(e); break;
                    case "hasi_water": o = new HasiWater(e); break;
                    case "hasi_3": o = new Hasi3(e); break;
                    case "switch": o = new FloorSwitch(e); break;
                    case "block": o = new Block(e, null); break;
                    case "makeblock": o = MakeBlock(e); break;
                    case "door": o = new Door(e); break;
                    case "eye": o = new EyeSwitch(e); break;
                }
                if (o != null) objs.Add(o);
            }
            Plugin.Log("OoT-Deku-Baum: " + objs.Count + " bewegliche Objekte");
        }

        public static void Clear()
        {
            foreach (var o in objs) o.Despawn();
            objs.Clear(); models.Clear(); temp.Clear(); clear.Clear();
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; forPack = null; cfg = null;
        }

        public static string Remote(string[] a)
        {
            if (a.Length > 2 && a[2] == "reset") { foreach (var o in objs) { o.Despawn(); o.Gone = false; } return "objekte neu (Merker bleiben)"; }
            if (a.Length > 3 && a[2] == "flag") { int n = int.Parse(a[3]); SetFlag(n, !(a.Length > 4 && a[4] == "off")); return "merker " + n + " " + Flag(n); }
            var sb = new System.Text.StringBuilder();
            foreach (var o in objs) sb.Append(o.Info()).Append(" | ");
            if (lastErr != null) sb.Append(" FEHLER ").Append(lastErr);
            return sb.ToString();
        }

        /// <summary>Jeden Frame aus OotPlay.Tick.</summary>
        public static void Tick(Player p)
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if (cfg == null || p == null || objs.Count == 0) return;
                if (holder == null) holder = new GameObject("oot_ydan_obj");
                var me = p.transform.position;
                acc += Mathf.Min(Time.deltaTime, 0.1f) * 20f;
                int steps = 0;
                while (acc >= 1f && steps < 4) { acc -= 1f; steps++; }
                if (steps > 0) { OotPlay.Pushing = false; OotPlay.ObjALabel = null; OotPlay.ObjAPress = null; }      // nur in OoT-Bildern neu bestimmen
                foreach (var o in objs)
                {
                    float d = (o.Home - me).magnitude;
                    if (o.Go == null && d < 60f && !o.Gone) o.Spawn();
                    if (o.Go == null) continue;
                    if (d > 80f) { o.Despawn(); continue; }
                    try { for (int i = 0; i < steps; i++) { o.Step(p, L(me)); } o.Draw(p); }
                    catch (Exception ex) { lastErr = o.GetType().Name + ": " + ex.Message; }
                }
                Frames += steps;
            }
            catch (Exception e) { Plugin.Quiet(e, "OotYdanObj.Tick"); }
        }

        static GameObject Model(string key, bool collide, bool moving)
        {
            string rel; if (!models.TryGetValue(key, out rel) || forPack == null) return null;
            var path = Path.Combine(forPack.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;
            var root = new GameObject("oot_" + key); root.transform.SetParent(holder.transform, false);
            var g = Glb.Instantiate(path);
            g.transform.SetParent(root.transform, false);
            foreach (var c in g.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var r in g.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
            if (collide)
                foreach (var mf in g.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mc = mf.gameObject.AddComponent<MeshCollider>(); mc.sharedMesh = mf.sharedMesh;
                }
            if (moving) { var rb = root.AddComponent<Rigidbody>(); rb.isKinematic = true; rb.useGravity = false; rb.interpolation = RigidbodyInterpolation.None; }
            return root;
        }

        // Steht Link auf diesem Objekt? (DynaPolyActor_IsPlayerOnTop)
        static bool OnTop(GameObject go, Player p)
        {
            if (go == null || p == null) return false;
            RaycastHit h;
            if (!Physics.Raycast(p.transform.position + Vector3.up * 0.4f, Vector3.down, out h, 0.75f, GroundMask, QueryTriggerInteraction.Ignore)) return false;
            return h.collider != null && h.collider.transform.IsChildOf(go.transform);
        }

        // Boden unter einem Punkt (ohne das eigene Objekt), in OoT-Einheiten
        static float FloorBelow(Vector3 o, GameObject self, float up = 2f)
        {
            var hits = Physics.RaycastAll(W(o) + Vector3.up * up * S, Vector3.down, 40f, GroundMask, QueryTriggerInteraction.Ignore);
            float best = -32000f;
            foreach (var h in hits)
            {
                if (self != null && h.collider.transform.IsChildOf(self.transform)) continue;
                float y = L(h.point).y; if (y > best) best = y;
            }
            return best;
        }

        // Link mitnehmen, wenn er auf einem bewegten Objekt steht
        static void Carry(Player p, Vector3 deltaWorld)
        {
            if (deltaWorld.sqrMagnitude < 1e-8f) return;
            var body = p.GetComponent<Rigidbody>();
            var np = p.transform.position + deltaWorld;
            p.transform.position = np; if (body != null) body.position = np;
        }

        abstract class Obj
        {
            public Vector3 Home, O; public short Rx, Ry, Rz; public GameObject Go; public bool Gone;
            protected Obj(object e)
            {
                Home = V3(Json.L(e, "pos")); O = V3(Json.L(e, "oot"));
                var r = Json.L(e, "rot"); if (r != null && r.Count >= 3) { Rx = (short)Convert.ToInt32(r[0]); Ry = (short)Convert.ToInt32(r[1]); Rz = (short)Convert.ToInt32(r[2]); }
            }
            public abstract void Spawn();
            public abstract void Step(Player p, Vector3 lp);
            public virtual void Draw(Player p) { }
            public virtual void Despawn() { if (Go != null) UnityEngine.Object.Destroy(Go); Go = null; }
            public virtual string Info() { return GetType().Name + " " + O.ToString("F0"); }
        }

        // ================= Obj_Lift =================
        class Lift : Obj
        {
            static readonly short[] dur = { 0, 10, 20, 30, 40, 50, 60 };
            readonly int flag, timerIdx; readonly bool small; int state, timer; Vector3 pos, prev; float vy; short sx, sy, sz; short rotX, rotZ;
            public Lift(object e) : base(e) { flag = (int)Json.F(e, "flag"); timerIdx = (int)Json.F(e, "timer"); small = Json.F(e, "small") > 0.5f; Gone = Flag(flag); }
            public override void Spawn()
            {
                if (Flag(flag)) { Gone = true; return; }
                Go = Model("lift", true, true); if (Go == null) return;
                if (small) Go.transform.GetChild(0).localScale = Vector3.one * 0.5f;
                pos = O; prev = O; vy = 0f; state = 0; timer = Dur(); rotX = Rx; rotZ = Rz;
                sx = (short)UnityEngine.Random.Range(0, 65535); sy = (short)UnityEngine.Random.Range(0, 65535); sz = (short)UnityEngine.Random.Range(0, 65535);
                Place();
            }
            int Dur() { return timerIdx < dur.Length ? dur[timerIdx] : 0; }
            void Place() { if (Go != null) { Go.transform.position = W(pos); Go.transform.rotation = OotEnemies.Rot(rotX, Ry, rotZ); } }
            public override void Step(Player p, Vector3 lp)
            {
                if (timer > 0) timer--;
                var before = W(pos); bool on = OnTop(Go, p);
                prev = pos;
                if (state == 0)
                {
                    if (on) { if (timer <= 0) { if (timerIdx == 7) state = 2; else { state = 1; timer = 20; } } }
                    else timer = Dur();
                }
                else if (state == 1)
                {
                    if (timer <= 0) { state = 2; pos = O; rotX = Rx; rotZ = Rz; }
                    else
                    {
                        sx = (short)(sx + 10000); rotX = (short)(SinS(sx) * 300f + Rx); rotZ = (short)(CosS(sx) * 300f + Rz);
                        sy = (short)(sy + 18000); pos.y = SinS(sy) + O.y;
                        sz = (short)(sz + 18000); pos.x = SinS(sz) * 3f + O.x; pos.z = CosS(sz) * 3f + O.z;
                    }
                    if ((timer & 3) == 3) OotSfx.Play("NA_SE_EV_BLOCK_SHAKE", 0.8f, W(pos));
                }
                else if (state == 2)
                {
                    vy = Mathf.Max(vy - 0.6f, -15f); pos.y += vy * 1.5f;
                    float fall = small ? -9f : -18f;
                    float fy = FloorBelow(new Vector3(pos.x, prev.y + fall, pos.z), Go, 0.5f);
                    if (fy - pos.y >= fall - 0.001f)
                    {
                        Fragments(); OotSfx.Play("NA_SE_EV_BOX_BREAK", 1f, W(pos));
                        SetFlag(flag, true); Gone = true; Despawn(); return;
                    }
                }
                Place();
                if (on && state != 2) Carry(p, W(pos) - before);
            }
            void Fragments()
            {
                var go = Go != null ? Go.transform.GetChild(0).gameObject : null; if (go == null) return;
                int[,] fs = { { 120, -120 }, { 120, 0 }, { 120, 120 }, { 0, -120 }, { 0, 0 }, { 0, 120 }, { -120, -120 }, { -120, 0 }, { -120, 120 } };
                float sc = small ? 0.05f : 0.1f;
                for (int i = 0; i < 9; i++)
                {
                    var f = UnityEngine.Object.Instantiate(go, holder.transform);
                    foreach (var c in f.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
                    f.transform.position = W(new Vector3(pos.x + fs[i, 0] * sc, pos.y, pos.z + fs[i, 1] * sc));
                    f.transform.localScale = Vector3.one * (UnityEngine.Random.value * 0.5f + 0.5f) * 0.3f * (small ? 0.5f : 1f);
                    var fr = f.AddComponent<OotFragment>();
                    fr.Vel = new Vector3(fs[i, 0] * sc * 0.8f, UnityEngine.Random.value * 10f + 6f, -fs[i, 1] * sc * 0.8f) * S * 30f;
                }
            }
            public override string Info() { return "lift " + state + " t" + timer + " y" + pos.y.ToString("F0"); }
        }

        // ================= Obj_Switch Typ 2: Augen-Schalter (z_obj_switch.c ObjSwitch_Eye*) =================
        // Mit der Schleuder von vorn treffen (ObjSwitch_EyeIsHit: Flugrichtung > 0x5000 gegen die Blickrichtung) -> Auge schliesst
        // sich in 3 Bildern, Merker an. Untertyp 1 (Umschalter, Deku-Baum Raum 4): nochmal treffen -> Auge auf, Merker aus.
        class EyeSwitch : Obj
        {
            readonly int flag, sub; int tex, state; bool hit; OotCombat.Shootable shot; readonly GameObject[] eyes = new GameObject[4];
            public EyeSwitch(object e) : base(e) { flag = (int)Json.F(e, "flag"); sub = (int)Json.F(e, "sub"); }
            float lastTest = -2f;
            public override string Info() { return "auge " + flag + " " + (state == 0 ? "offen" : state == 2 ? "zu" : "wechselt") + " " + Flag(flag) + (Go != null ? " da" : " weg") + " test " + lastTest.ToString("F1") + " mitte " + P(0f, 0f, 8.5f).ToString("F2"); }
            public override void Spawn()
            {
                Go = new GameObject("oot_eye_switch"); Go.transform.SetParent(holder.transform, false);
                for (int i = 0; i < 4; i++)
                {
                    eyes[i] = Model("eye" + Mathf.Clamp(sub, 0, 1) + "_" + i, false, false);
                    if (eyes[i] == null) continue;
                    eyes[i].transform.SetParent(Go.transform, false);
                }
                Go.transform.position = W(O); Go.transform.rotation = OotEnemies.Rot(Rx, Ry, Rz);
                if (Flag(flag)) { state = 2; tex = 3; } else { state = 0; tex = 0; }
                Show();
                shot = new OotCombat.Shootable { Test = TestRay, Hit = (w, d) => { if (Front(d)) hit = true; else OotSfx.Play("NA_SE_IT_SLING_REFLECT", 0.6f, w); } };
                OotCombat.Shootables.Add(shot);
            }
            public override void Despawn() { if (shot != null) OotCombat.Shootables.Remove(shot); shot = null; base.Despawn(); }
            void Show() { for (int i = 0; i < 4; i++) if (eyes[i] != null) eyes[i].SetActive(i == tex); }
            // ObjSwitch_RotateY + Weltpunkt
            Vector3 P(float x, float y, float z) { float s = SinS(Ry), c = CosS(Ry); return W(new Vector3(O.x + x * c + z * s, O.y + y, O.z - x * s + z * c)); }
            bool Front(Vector3 dir)
            {
                var f = new Vector3(SinS(Ry), 0f, -CosS(Ry)); dir.y = 0f;
                return dir.sqrMagnitude > 1e-6f && Vector3.Dot(dir.normalized, f) < -0.383f;
            }
            float TestRay(Ray r, float max)
            {
                float best = -1f;
                var v = new[] { P(0f, 23f, 8.5f), P(-23f, 0f, 8.5f), P(0f, -23f, 8.5f), P(23f, 0f, 8.5f) };
                foreach (var t in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
                {
                    float d = Tri(r, v[t[0]], v[t[1]], v[t[2]]);
                    if (d >= 0f && d <= max + 0.2f && (best < 0f || d < best)) best = d;
                }
                lastTest = best;
                return best < 0f ? -1f : Mathf.Max(0f, best - 0.05f);
            }
            static float Tri(Ray r, Vector3 a, Vector3 b, Vector3 c)
            {
                var e1 = b - a; var e2 = c - a; var pv = Vector3.Cross(r.direction, e2); float det = Vector3.Dot(e1, pv);
                if (Mathf.Abs(det) < 1e-7f) return -1f;
                float inv = 1f / det; var tv = r.origin - a; float u = Vector3.Dot(tv, pv) * inv; if (u < -0.05f || u > 1.05f) return -1f;
                var qv = Vector3.Cross(tv, e1); float w = Vector3.Dot(r.direction, qv) * inv; if (w < -0.05f || u + w > 1.1f) return -1f;
                float t = Vector3.Dot(e2, qv) * inv; return t >= 0f ? t : -1f;
            }
            public override void Step(Player p, Vector3 lp)
            {
                bool h = hit; hit = false;
                if (state == 0)          // offen
                {
                    if (h) { state = 1; if (!Flag(flag)) { SetFlag(flag, true); OotSfx.Play(sub == 1 ? "NA_SE_SY_TRE_BOX_APPEAR" : "NA_SE_SY_CORRECT_CHIME", 1f); } }
                }
                else if (state == 1)     // schliesst sich
                {
                    if (++tex >= 3) { tex = 3; state = 2; OotSfx.Play("NA_SE_EV_FOOT_SWITCH", 0.9f, W(O)); }
                }
                else if (state == 2)     // zu
                {
                    if (sub == 0 && !Flag(flag)) state = 3;
                    else if (sub == 1 && h) { state = 3; if (Flag(flag)) { SetFlag(flag, false); OotSfx.Play("NA_SE_SY_TRE_BOX_APPEAR", 1f); } }
                }
                else if (state == 3)     // geht auf
                {
                    if (--tex <= 0) { tex = 0; state = 0; OotSfx.Play("NA_SE_EV_FOOT_SWITCH", 0.9f, W(O)); }
                }
                Show();
            }
        }

        // ================= Bg_Ydan_Maruta: Leiter =================
        class Ladder : Obj
        {
            readonly int flag; int state, timer; Vector3 pos; float vy; OotCombat.Shootable shot; OotDungeon.Climb[] climb;
            readonly Vector3 bottom;
            public Ladder(object e) : base(e) { flag = (int)Json.F(e, "flag"); bottom = new Vector3(O.x, O.y - 280f, O.z); }
            public override void Spawn()
            {
                Go = Model("ladder", true, true); if (Go == null) return;
                if (Flag(flag)) { pos = bottom; state = 3; } else { pos = O; state = 0; }
                Place();
                if (state == 3) SetClimb();
                shot = new OotCombat.Shootable { Test = TestRay, Hit = (w, d) => { if (state == 0) Shot(); } };
                OotCombat.Shootables.Add(shot);
            }
            public override void Despawn()
            {
                if (shot != null) OotCombat.Shootables.Remove(shot); shot = null;
                if (climb != null) foreach (var c in climb) OotDungeon.RemoveClimb(c); climb = null;
                base.Despawn();
            }
            void Place() { if (Go != null) { Go.transform.position = W(pos); Go.transform.rotation = OotEnemies.Rot(0, Ry, 0); } }
            // Ecken der Leiter (Kollider-Dreieck 1: x +-16, y 0..135)
            Vector3 Corner(float x, float y) { return W(new Vector3(pos.x + x * CosS(Ry), pos.y + y, pos.z - x * SinS(Ry))); }
            float TestRay(Ray r, float max)
            {
                var a = Corner(-16, 0); var b = Corner(16, 0); var c = Corner(-16, 135);
                var n = Vector3.Cross(b - a, c - a).normalized;
                float den = Vector3.Dot(n, r.direction); if (Mathf.Abs(den) < 1e-5f) return -1f;
                float t = Vector3.Dot(a - r.origin, n) / den; if (t < 0f || t > max + 0.2f) return -1f;
                var q = r.GetPoint(t) - a; float u = Vector3.Dot(q, (b - a).normalized), v = Vector3.Dot(q, (c - a).normalized);
                if (u < -0.1f || u > (b - a).magnitude + 0.1f || v < -0.1f || v > (c - a).magnitude + 0.1f) return -1f;
                return Mathf.Max(0f, t - 0.3f);
            }
            void Shot()
            {
                timer = 20; state = 1; SetFlag(flag, true);
                OotSfx.Play("NA_SE_SY_CORRECT_CHIME", 1f);
            }
            void SetClimb()
            {
                if (climb != null) foreach (var c in climb) OotDungeon.RemoveClimb(c);
                var a = Corner(-16, 0); var b = Corner(16, 0); var c2 = Corner(16, 135); var d = Corner(-16, 135);
                climb = new[] { OotDungeon.AddClimb(a, b, c2, 2), OotDungeon.AddClimb(a, c2, d, 2) };
            }
            public override void Step(Player p, Vector3 lp)
            {
                if (state == 1)
                {
                    if (timer > 0) timer--;
                    if (timer == 0) state = 2;
                    int t = (timer % 4) - 2; t = t == -2 ? 0 : t * 2;
                    pos.x = CosS(Ry) * t + O.x; pos.z = SinS(Ry) * t + O.z;
                    if ((timer & 7) == 0) OotSfx.Play("NA_SE_EV_TRAP_OBJ_SLIDE", 0.7f, W(pos));
                }
                else if (state == 2)
                {
                    vy += 1f;
                    if (StepToF(ref pos.y, bottom.y, vy)) { OotSfx.Play("NA_SE_EV_LADDER_DOUND", 1f, W(pos)); state = 3; Place(); SetClimb(); }
                }
                Place();
            }
            public override string Info() { return "leiter " + state + " y" + pos.y.ToString("F0"); }
        }

        // ================= Bg_Ydan_Maruta: rollender Stachelstamm =================
        class SpikeLog : Obj
        {
            short rot; AudioSource snd;
            public SpikeLog(object e) : base(e) { }
            public override void Spawn()
            {
                Go = Model("spikelog", false, false); if (Go == null) return;
                Go.transform.position = W(O); rot = Rx;
                var clip = OotSfx.Clip("NA_SE_EV_TOGE_STICK_ROLLING");
                if (clip != null)
                {
                    snd = Go.AddComponent<AudioSource>(); snd.clip = clip; snd.loop = true; snd.spatialBlend = 1f; snd.rolloffMode = AudioRolloffMode.Linear;
                    snd.minDistance = 2f; snd.maxDistance = 20f; snd.dopplerLevel = 0f; snd.volume = 0.7f; snd.Play();
                }
                Draw(null);
            }
            public override void Step(Player p, Vector3 lp)
            {
                rot = (short)(rot + 0x360);
                // AT: Flaeche x +-220, y +-10 um die Stammachse; Link-Zylinder r=12, h=50
                var d = lp - O;
                float lx = d.x * CosS(Ry) - d.z * SinS(Ry), lz = d.x * SinS(Ry) + d.z * CosS(Ry);
                if (Mathf.Abs(lx) < 232f && Mathf.Abs(lz) < 12f + 21f && d.y < 21f && d.y + 50f > -21f)
                    OotCombat.Knockback(W(O), Quaternion.Euler(0f, 180f - Ry / 65536f * 360f, 0f) * Vector3.forward, 7f, 6f);
            }
            public override void Draw(Player p)
            {
                if (Go == null) return;
                Go.transform.rotation = OotEnemies.Rot(rot, Ry, 0);
                if (snd != null) { float sv = 1f; try { sv = Mathf.Clamp01(PlayerPrefs.GetFloat("SfxVolume", 1f)); } catch { } snd.volume = 0.7f * sv; }
            }
        }

        // ================= Bg_Ydan_Hasi =================
        class HasiBlock : Obj
        {
            Vector3 pos; int timer;
            public HasiBlock(object e) : base(e) { }
            public override void Spawn()
            {
                Go = Model("hasi_block", true, true); if (Go == null) return;
                Go.transform.GetChild(0).localScale = new Vector3(1.5f, 1f, 1.5f);
                pos = O; timer = 0; Step(null, Vector3.zero);
            }
            public override void Step(Player p, Vector3 lp)
            {
                var before = W(pos); bool on = p != null && OnTop(Go, p);
                float f = Mathf.Sin((Frames & 0xFF) * (Mathf.PI / 128f)) * 165f;
                pos.x = SinS(Ry) * f + O.x; pos.z = CosS(Ry) * f + O.z;
                float wy; pos.y = (OotDungeon.WaterY(1, out wy) ? L(new Vector3(0f, wy, 0f)).y : O.y) + 20f;
                if (timer != 0) timer--;
                if (timer == 0) timer = 50;
                pos.y += 2f * Mathf.Sin(timer * (Mathf.PI / 25f));
                Go.transform.position = W(pos); Go.transform.rotation = OotEnemies.Rot(0, Ry, 0);
                if (on) Carry(p, W(pos) - before);
            }
        }

        class HasiWater : Obj
        {
            readonly int flag, box; float y; int state, timer;
            public HasiWater(object e) : base(e) { flag = (int)Json.F(e, "flag"); box = (int)Json.F(e, "water", 1f); }
            public override void Spawn()
            {
                Go = Model("hasi_water", false, false); if (Go == null) return;
                y = O.y - 5f; state = 0; Apply();
            }
            void Apply()
            {
                OotDungeon.SetWaterY(box, W(new Vector3(O.x, y, O.z)).y);
                if (Go != null) Go.transform.position = W(new Vector3(O.x, y, O.z));
            }
            public override void Step(Player p, Vector3 lp)
            {
                if (state == 0) { if (Flag(flag)) { timer = 600; state = 1; } }
                else if (state == 1)
                {
                    if (StepToF(ref y, O.y - 5f - 47f, 0.5f)) state = 2;
                    if ((Frames & 7) == 0) OotSfx.Play("NA_SE_EV_WATER_LEVEL_DOWN", 0.6f, W(new Vector3(O.x, y, O.z)));
                }
                else if (state == 2) { if (timer > 0) timer--; if (timer == 0) state = 3; }
                else if (state == 3)
                {
                    if (StepToF(ref y, O.y - 5f, 1f)) { SetFlag(flag, false); state = 0; }
                    if ((Frames & 7) == 0) OotSfx.Play("NA_SE_EV_WATER_LEVEL_DOWN", 0.6f, W(new Vector3(O.x, y, O.z)));
                }
                Apply();
            }
            public override string Info() { return "wasser " + state + " y" + y.ToString("F0"); }
        }

        class Hasi3 : Obj
        {
            readonly int flag; float y; int state, timer; Renderer[] rs;
            public Hasi3(object e) : base(e) { flag = (int)Json.F(e, "flag"); }
            public override void Spawn()
            {
                Go = Model("hasi_3", true, true); if (Go == null) return;
                rs = Go.GetComponentsInChildren<Renderer>(true);
                y = O.y; state = 0; Show(false); Place();
            }
            void Show(bool on) { if (rs != null) foreach (var r in rs) r.enabled = on; }
            void Place() { if (Go != null) { Go.transform.position = W(new Vector3(O.x, y, O.z)); Go.transform.rotation = OotEnemies.Rot(Rx, Ry, Rz); } }
            public override void Step(Player p, Vector3 lp)
            {
                var before = W(new Vector3(O.x, y, O.z)); bool on = OnTop(Go, p);
                if (state == 0) { if (Flag(flag)) { timer = 260; state = 1; Show(true); } }
                else
                {
                    if (timer > 0) timer--;
                    if (timer == 0)
                    {
                        if (StepToF(ref y, O.y, 3f)) { SetFlag(flag, false); Show(false); state = 0; }
                        else if ((Frames & 7) == 0) OotSfx.Play("NA_SE_EV_ELEVATOR_MOVE", 0.6f, W(new Vector3(O.x, y, O.z)));
                    }
                    else if (!StepToF(ref y, O.y + 120f, 3f)) { if ((Frames & 7) == 0) OotSfx.Play("NA_SE_EV_ELEVATOR_MOVE", 0.6f, W(new Vector3(O.x, y, O.z))); }
                    else if (timer < 60 && (timer % (timer < 20 ? 5 : 10)) == 0) OotSfx.Play("NA_SE_SY_TIMER", 0.5f);
                }
                Place();
                if (on) Carry(p, W(new Vector3(O.x, y, O.z)) - before);
            }
            public override string Info() { return "3bloecke " + state + " t" + timer + " y" + y.ToString("F0"); }
        }

        // ================= Obj_Switch (Boden) =================
        class FloorSwitch : Obj
        {
            readonly int flag, sub; float sy; int state; Transform mdl;      // state 0 oben, 1 wird gedrueckt, 2 unten, 3 kommt hoch
            const float UP = 33f / 200f, DOWN = 33f / 2000f;
            public FloorSwitch(object e) : base(e) { flag = (int)Json.F(e, "flag"); sub = (int)Json.F(e, "sub"); }
            public override void Spawn()
            {
                Go = Model("switch", true, false); if (Go == null) return;
                mdl = Go.transform.GetChild(0);
                Go.transform.position = W(new Vector3(O.x, O.y + 1f, O.z)); Go.transform.rotation = OotEnemies.Rot(0, Ry, 0);
                if (Flag(flag) && sub != 2) { sy = DOWN; state = 2; } else { sy = UP; state = 0; }
                Scale();
            }
            void Scale() { if (mdl != null) mdl.localScale = new Vector3(1f, sy / 0.1f, 1f); }
            bool Pressed(Player p)
            {
                if (OnTop(Go, p)) return true;
                var lp = L(p.transform.position);
                return Mathf.Abs(lp.x - O.x) < 22f && Mathf.Abs(lp.z - O.z) < 22f && lp.y > O.y - 4f && lp.y < O.y + 22f;
            }
            public override void Step(Player p, Vector3 lp)
            {
                bool on = Pressed(p);
                if (state == 0)
                {
                    if (on && (sub == 0 || sub == 1 || sub == 2))
                    {
                        state = 1;
                        if (!Flag(flag)) { SetFlag(flag, true); OotSfx.Play(sub == 0 ? "NA_SE_SY_CORRECT_CHIME" : "NA_SE_SY_TRE_BOX_APPEAR", 1f); }
                    }
                }
                else if (state == 1)
                {
                    sy -= 99f / 2000f;
                    if (sy <= DOWN) { sy = DOWN; state = 2; OotSfx.Play("NA_SE_EV_FOOT_SWITCH", 0.9f, W(O)); }
                }
                else if (state == 2)
                {
                    if ((sub == 0 && !Flag(flag)) || (sub == 2 && !on)) { state = 3; if (sub == 2) SetFlag(flag, false); }
                }
                else if (state == 3)
                {
                    sy += 99f / 2000f;
                    if (sy >= UP) { sy = UP; state = 0; OotSfx.Play("NA_SE_EV_FOOT_SWITCH", 0.6f, W(O)); }
                }
                Scale();
            }
            public override string Info() { return "schalter " + flag + " " + state + " " + Flag(flag); }
        }


        // ================= Door_Shutter (Schiebetuer, Gitter) =================
        // Gitter nur im hinteren Raum: Typ 1 bis der Raum frei ist (Schrubbe), Typ 2 bis der Schalter-Merker gesetzt ist.
        class Door : Obj
        {
            readonly int front, back, kind, flag; float y, vy, bars; int state; GameObject bar; bool wasBarred;
            public Door(object e) : base(e)
            {
                front = (int)Json.F(e, "front"); back = (int)Json.F(e, "back"); kind = (int)Json.F(e, "kind"); flag = (int)Json.F(e, "flag", 63f);
            }
            public override void Spawn()
            {
                Go = Model(kind == 0 ? "door1" : "door2", true, true); if (Go == null) return;
                bar = Model("bars", false, false);
                y = 0f; vy = 0f; state = 0; bars = 0f; wasBarred = false; Place(0f);
            }
            public override void Despawn() { if (bar != null) UnityEngine.Object.Destroy(bar); bar = null; base.Despawn(); }
            void Solid(bool on) { if (Go != null) foreach (var c in Go.GetComponentsInChildren<Collider>(true)) c.enabled = on; }
            void Place(float side)
            {
                if (Go != null) { Go.transform.position = W(new Vector3(O.x, O.y + y, O.z)); Go.transform.rotation = OotEnemies.Rot(0, Ry, 0); }
                if (bar != null)
                {
                    bar.SetActive(bars > 0.001f);
                    float s = side < 0f ? -1f : 1f;
                    var off = new Vector3(SinS(Ry) * 12f * s, 130f * (1f - bars), CosS(Ry) * 12f * s);
                    bar.transform.position = W(O + off);
                    bar.transform.rotation = OotEnemies.Rot(0, s < 0f ? (short)(Ry + 0x8000) : Ry, 0);
                }
            }
            public override void Step(Player p, Vector3 lp)
            {
                var d = lp - O;
                float lz = d.x * SinS(Ry) + d.z * CosS(Ry), lx = d.x * CosS(Ry) - d.z * SinS(Ry);
                // die Tuer schaut in den hinteren Raum (sides[1]); nur auf demselben Stockwerk und in der Naehe zaehlt die Seite
                // (sonst hatte z. B. die Tuer zu Raum 10 im 3. Stock Gitter, waehrend Link im 2. Stock in Raum 1 stand)
                int room = lz >= 0f && Mathf.Abs(d.y) < 200f && new Vector2(d.x, d.z).magnitude < 1200f ? back : front;
                bool barred = room == back && ((kind == 1 && !Clear(back)) || (kind == 2 && !Flag(flag)));
                if (barred != wasBarred) { OotSfx.Play(barred ? "NA_SE_EV_METALDOOR_CLOSE" : "NA_SE_EV_METALDOOR_OPEN", 0.9f, W(O)); wasBarred = barred; }
                StepToF(ref bars, barred ? 1f : 0f, 0.2f);
                if (state == 0)
                {
                    // vor der Tuer (DoorShutter_GetPlayerSide: seitlich < 20, hoehe < 15, Abstand < 50, Blick zur Tuer)
                    float pyaw = p.transform.eulerAngles.y, dyaw = Mathf.DeltaAngle(pyaw, 180f - Ry / 65536f * 360f + (lz >= 0f ? 180f : 0f));
                    if (!barred && bars <= 0f && Mathf.Abs(lx) < 30f && Mathf.Abs(d.y) < 25f && Mathf.Abs(lz) < 50f && Mathf.Abs(dyaw) < 70f)
                    {
                        OotPlay.ObjALabel = "Open";
                        var through = W(O + new Vector3(SinS(Ry), 0f, CosS(Ry)) * (lz >= 0f ? -60f : 60f));
                        OotPlay.ObjAPress = () => { state = 1; vy = 0f; OotSfx.Play("NA_SE_EV_SLIDE_DOOR_OPEN", 0.9f, W(O)); OotPlay.WalkThrough(through, 1.6f); };
                    }
                }
                else if (state == 1)
                {
                    vy = Mathf.Min(vy + 3f, 15f);
                    if (y > 60f) Solid(false);                     // offen genug: Link geht hindurch
                    if (StepToF(ref y, 200f, vy)) state = 2;
                }
                else if (state == 2)
                {
                    if (new Vector2(d.x, d.z).magnitude > 50f && !OotPlay.WalkingThrough) { state = 3; vy = 0f; OotSfx.Play("NA_SE_EV_SLIDE_DOOR_CLOSE", 0.9f, W(O)); }
                }
                else if (state == 3)
                {
                    vy = Mathf.Min(vy + 3f, 30f);
                    if (y < 80f) Solid(true);
                    if (StepToF(ref y, 0f, vy)) { state = 0; OotSfx.Play("NA_SE_EV_STONE_BOUND", 0.8f, W(O)); }
                }
                Place(lz);
            }
            public override string Info() { return "tuer " + front + "/" + back + " " + state + (wasBarred ? " gitter" : ""); }
        }

        // ================= Obj_Oshihiki (Schiebeblock) =================
        class Block : Obj
        {
            public Vector3 Pos; Vector3 home; float vy, pushDist, pushSpeed, dir; int state, timer; short angle; public bool CantMove; float holdT;
            public Action<Block> Moved;           // Makeoshihiki: Zielplaetze pruefen
            readonly int kind, flag;
            public Block(object e, Vector3? start) : base(e)
            {
                kind = (int)Json.F(e, "kind"); flag = (int)Json.F(e, "flag", 255f);
                Pos = start ?? O; home = Pos;
                if (flag >= 0 && flag <= 0x3F)
                {
                    bool set = Flag(flag), startOn = kind < 4;
                    if ((set && startOn) || (!set && !startOn)) Gone = true;
                }
            }
            float Half { get { return 30f; } }
            public override void Spawn()
            {
                Go = Model("block", true, true); if (Go == null) return;
                home = Pos; prevY = Pos.y; state = 0; vy = 0f; Place();
            }
            void Place() { if (Go != null) { Go.transform.position = W(Pos); Go.transform.rotation = Quaternion.identity; } }
            // Kollision ausblenden, waehrend der Block selbst prueft
            float prevY;
            // ObjOshihiki_SetFloors: Pruefpunkte auf Hoehe der vorigen Lage (sonst faellt der Block durch den Boden)
            float Floor()
            {
                var cs = Go.GetComponentsInChildren<Collider>(); foreach (var c in cs) c.enabled = false;
                float best = -32000f; float by = Mathf.Max(prevY, Pos.y);
                float[,] pts = { { 29.99f, -29.99f }, { -29.99f, -29.99f }, { -29.99f, 29.99f }, { 29.99f, 29.99f }, { 0f, 0f } };
                for (int i = 0; i < 5; i++) best = Mathf.Max(best, FloorBelow(new Vector3(Pos.x + pts[i, 0], by + 1.01f, Pos.z + pts[i, 1]), null, 0f));
                foreach (var c in cs) c.enabled = true;
                return best;
            }
            bool Wall(short ang, float d)
            {
                var cs = Go.GetComponentsInChildren<Collider>(); foreach (var c in cs) c.enabled = false;
                float max = (d >= 0f ? 1f : -1f) * (300f * 0.1f + 20f - 0.5f);
                float sn = SinS(ang), cn = CosS(ang); bool hit = false;
                float[,] fv = { { -30f, 0f, 1f, 1f }, { 30f, 0f, -1f, 1f }, { -30f, 60f, 1f, -1f }, { 30f, 60f, -1f, -1f } };
                for (int i = 0; i < 4 && !hit; i++)
                {
                    float ox = fv[i, 0] + fv[i, 2], oy = fv[i, 1] + fv[i, 3];
                    var a = new Vector3(Pos.x + ox * cn, Pos.y + oy, Pos.z - ox * sn);
                    var b = a + new Vector3(max * sn, 0f, max * cn);
                    var wa = W(a); var wb = W(b);
                    if (Physics.Linecast(wa, wb, GroundMask, QueryTriggerInteraction.Ignore)) hit = true;
                }
                foreach (var c in cs) c.enabled = true;
                return hit;
            }
            public override void Step(Player p, Vector3 lp)
            {
                if (timer > 0) timer--;
                Step2(p, lp); prevY = Pos.y;
            }
            void Step2(Player p, Vector3 lp)
            {
                if (state == 0)           // steht
                {
                    float fy = Floor();
                    if (fy < -30000f) return;                 // Raum-Kollision noch nicht geladen
                    if (fy - Pos.y < -0.5f) { state = 2; vy = 0f; return; }
                    Pos.y = fy;
                    // Link drueckt gegen eine Seite (Stick Richtung Block, nah an der Flaeche)
                    var mv = OotPlay.StickWorld; float side; short ang;
                    if (timer <= 0 && !CantMove && Face(lp, mv, out ang, out side))
                    {
                        OotPlay.Pushing = true;
                        holdT += 0.05f;
                        if (holdT > 0.35f && !Wall(ang, side)) { angle = ang; dir = side; state = 1; pushDist = 0f; pushSpeed = 0f; home = Pos; }
                    }
                    else holdT = 0f;
                }
                else if (state == 1)      // wird geschoben (ObjOshihiki_Push)
                {
                    OotPlay.Pushing = true;
                    var before = W(Pos);
                    pushSpeed = Mathf.Min(pushSpeed + 0.5f, 2f);
                    bool stop = StepToF(ref pushDist, 20f, pushSpeed);
                    float sd = (dir >= 0f ? 1f : -1f) * pushDist;
                    Pos.x = home.x + sd * SinS(angle); Pos.z = home.z + sd * CosS(angle);
                    float fy = Floor();
                    if (fy - Pos.y < -0.5f) { home = Pos; pushDist = 0f; pushSpeed = 0f; holdT = 0f; state = 2; vy = 0f; }
                    else if (stop)
                    {
                        if (Wall(angle, dir)) OotSfx.Play("NA_SE_EV_BLOCK_BOUND", 0.8f, W(Pos));
                        home = Pos; pushDist = 0f; pushSpeed = 0f; timer = 10; holdT = 0f; state = 0;
                        if (Moved != null) Moved(this);
                    }
                    if ((OotYdanObj.Frames & 7) == 0) OotSfx.Play("NA_SE_EV_ROCK_SLIDE", 0.6f, W(Pos));
                    Carry(p, (W(Pos) - before) * 0.98f);
                }
                else if (state == 2)      // faellt
                {
                    vy = Mathf.Max(vy - 1f, -20f); Pos.y += vy * 1.5f;
                    float fy = Floor();
                    if (fy < -30000f && Pos.y < home.y - 400f) { Pos = home; state = 0; vy = 0f; Place(); return; }   // ins Leere: zurueck
                    if (fy - Pos.y >= -0.001f)
                    {
                        Pos.y = fy; state = 0; vy = 0f;
                        OotSfx.Play("NA_SE_EV_BLOCK_BOUND", 1f, W(Pos)); OotSfx.Play("NA_SE_PL_WALK_GROUND", 0.8f, W(Pos));
                        if (Moved != null) Moved(this);
                    }
                    if (Pos.y < O.y - 3000f) { Gone = true; Despawn(); return; }
                }
                Place();
            }
            // Link steht vor einer Seite und drueckt (dyna.unk_150 / unk_158): Seite -> Schieberichtung (OoT-Winkel), dir = +1
            bool Face(Vector3 lp, Vector3 mvWorld, out short ang, out float side)
            {
                ang = 0; side = 1f;
                if (mvWorld.magnitude < 0.5f || lp.y > Pos.y + 40f || lp.y < Pos.y - 20f) return false;
                var d = lp - Pos;
                if (Mathf.Abs(d.x) > Mathf.Abs(d.z)) { if (Mathf.Abs(d.z) > Half - 6f || Mathf.Abs(d.x) > Half + 22f) return false; ang = (short)(d.x > 0f ? -0x4000 : 0x4000); }
                else { if (Mathf.Abs(d.x) > Half - 6f || Mathf.Abs(d.z) > Half + 22f) return false; ang = (short)(d.z > 0f ? -0x8000 : 0); }
                // Stick muss zum Block zeigen (OoT-Richtung -> Valheim: z gespiegelt)
                var want = new Vector3(SinS(ang), 0f, -CosS(ang));
                return Vector3.Angle(new Vector3(mvWorld.x, 0f, mvWorld.z), want) < 35f;
            }
            public override string Info() { return "block " + state + " " + Pos.ToString("F0") + (CantMove ? " fest" : ""); }
        }

        // Obj_Makeoshihiki: Block an Platz 0/1/2 je nach Merkern, am Zielplatz Merker setzen (+ fest)
        static Obj MakeBlock(object e)
        {
            var spots = new List<Vector3>(); foreach (var s in Json.L(e, "spots_oot") ?? new List<object>()) spots.Add(V3(s as List<object>));
            var bits = Json.L(e, "bits"); var fl = Json.L(e, "flags");
            int f1 = (int)Json.F(e, "flag1"), f2 = (int)Json.F(e, "flag2"); bool u1 = Json.F(e, "use1") > 0.5f, u2 = Json.F(e, "use2") > 0.5f;
            if (spots.Count < 3) return null;
            int idx = (u1 && Flag(f1)) ? 1 : (u2 && Flag(f2)) ? 2 : 0;
            var b = new Block(e, spots[idx]);
            b.Home = W(spots[idx]);
            if (bits != null && (Convert.ToInt32(bits[idx]) & 2) != 0) b.CantMove = true;
            b.Moved = blk =>
            {
                for (int i = 0; i < 3; i++)
                {
                    if ((blk.Pos - spots[i]).sqrMagnitude > 4f) continue;
                    var ff = fl != null && i < fl.Count ? fl[i] as List<object> : null;
                    bool want1 = ff != null && Convert.ToInt32(ff[0]) == 1, want2 = ff != null && Convert.ToInt32(ff[1]) == 1;
                    int bt = bits != null ? Convert.ToInt32(bits[i]) : 0;
                    if ((bt & 1) != 0 && ((u1 && want1 != Flag(f1)) || (u2 && want2 != Flag(f2)))) OotSfx.Play("NA_SE_SY_TRE_BOX_APPEAR", 1f);
                    if (u1) SetFlag(f1, want1);
                    if (u2) SetFlag(f2, want2);
                    if ((bt & 2) != 0) blk.CantMove = true;
                    break;
                }
            };
            return b;
        }
    }

    // Bruchstuecke (EffectSsKakera vereinfacht): fliegen, fallen, verschwinden
    public class OotFragment : MonoBehaviour
    {
        public Vector3 Vel; float t; Vector3 spin = new Vector3(UnityEngine.Random.Range(-400f, 400f), UnityEngine.Random.Range(-400f, 400f), 0f);
        void Update()
        {
            float dt = Time.deltaTime; t += dt;
            Vel += Vector3.down * 9.8f * dt * 1.6f;
            transform.position += Vel * dt; transform.Rotate(spin * dt);
            if (t > 1.6f) Destroy(gameObject);
        }
    }
}
