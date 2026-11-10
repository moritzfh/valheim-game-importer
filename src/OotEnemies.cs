using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    /// <summary>
    /// Gegner aus Ocarina of Time, Verhalten 1:1 aus der Decomp nachgebaut (20 Spielbilder pro Sekunde, OoT-Einheiten und
    /// Binaerwinkel intern, gezeichnet in Valheim-Koordinaten).
    ///   Deku-Ranha (En_Dekubaba): wartet im Laub, schnellt hervor, schnappt in der Luft, beisst zu; nach einem Fehlbiss
    ///   liegt sie am Boden -> Treffer = sie steht steif -> zweiter Treffer = sie faellt um und laesst einen Deku-Stab da.
    ///   Zwei Treffer waehrend sie angreift = sie schrumpft weg und laesst eine Deku-Nuss da.
    /// pack.json "oot_enemies": {"dekubaba": {"head", "stems": [3], "leaves", "stick"}, "list": [{"type", "pos", "yaw", "big"}]}
    /// </summary>
    public static class OotEnemies
    {
        const float S = 0.033f;                        // Meter je OoT-Einheit
        static Pack forPack; static GameObject holder;
        static float modelScale = 0.00033f, acc;
        static string babaHead, babaLeaves, babaStick; static string[] babaStems = new string[3];
        static readonly List<Dekubaba> babas = new List<Dekubaba>();
        static readonly List<Karebaba> kares = new List<Karebaba>();
        static readonly List<Skulltula> sts = new List<Skulltula>();
        static string stModel; static string gomaModel, gomaEggModel;
        static readonly List<Goma> gomas = new List<Goma>();
        static readonly List<Scrub> scrubs = new List<Scrub>(); static readonly List<Nut> nuts = new List<Nut>();
        static readonly List<WallSt> walls = new List<WallSt>(); static string tokenModel; public static int Tokens;
        static readonly List<Boulder> boulders = new List<Boulder>();
        static string scrubModel, flowerModel, nutModel; static readonly Dictionary<string, object> enemyTexts = new Dictionary<string, object>();
        public static Action<string> GotStick;         // OotPlay: Deku-Stab aufgehoben (erstes Mal mit Textbox)
        public static bool Tint = true;

        /// <summary>Fernsteuerung: oot enemy info | tint on/off</summary>
        public static string Remote(string[] a)
        {
            if (a.Length > 3 && a[2] == "tint") { Tint = a[3] != "off"; return "tint " + Tint; }
            var sb = new System.Text.StringBuilder();
            var p = Player.m_localPlayer;
            if (p != null) sb.Append("link " + p.transform.position.ToString("F1") + " | ");
            foreach (var k in kares) sb.Append(k.Info() + " | ");
            foreach (var k in sts) sb.Append(k.Info() + " | ");
            foreach (var k in boulders) sb.Append(k.Info() + " | ");
            foreach (var k in scrubs) if (k.Root != null) sb.Append(k.Info() + " | ");
            foreach (var k in gomas) sb.Append(k.Info() + " | ");
            sb.Append("nuesse " + nuts.Count);
            foreach (var b in babas) sb.Append((b.Root != null ? b.Root.transform.position.ToString("F1") : "-") + " | ");
            return sb.ToString();
        }

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var o = Json.V(meta, "oot_enemies");
            if (o == null) return;
            modelScale = Json.F(Json.V(meta, "oot_play"), "scale", 0.00033f);
            stModel = Json.S(Json.V(o, "st"), "model");
            var sc = Json.V(o, "scrub");
            tokenModel = Json.S(Json.V(o, "st"), "token");
            scrubModel = Json.S(sc, "model"); flowerModel = Json.S(sc, "flower"); nutModel = Json.S(sc, "nut");
            var gm = Json.V(o, "goma"); gomaModel = Json.S(gm, "model"); gomaEggModel = Json.S(gm, "egg");
            var tx = Json.V(o, "texts") as Dictionary<string, object>;
            enemyTexts.Clear(); if (tx != null) foreach (var kv in tx) enemyTexts[kv.Key] = kv.Value;
            var db = Json.V(o, "dekubaba");
            if (db != null)
            {
                babaHead = Json.S(db, "head"); babaLeaves = Json.S(db, "leaves"); babaStick = Json.S(db, "stick");
                var st = Json.L(db, "stems");
                for (int i = 0; i < 3; i++) babaStems[i] = st != null && i < st.Count ? st[i].ToString() : null;
            }
            foreach (var e in Json.L(o, "list") ?? new List<object>())
            {
                var p = Json.L(e, "pos"); if (p == null || p.Count < 3) continue;
                var pos = new Vector3((float)(double)p[0], (float)(double)p[1], (float)(double)p[2]);
                if (Json.S(e, "type") == "dekubaba")
                    babas.Add(new Dekubaba(pos, (short)(int)Json.F(e, "yaw"), Json.F(e, "big", 0f) > 0.5f));
                else if (Json.S(e, "type") == "karebaba")
                    kares.Add(new Karebaba(pos, (short)(int)Json.F(e, "yaw")));
                else if (Json.S(e, "type") == "scrub")
                    scrubs.Add(new Scrub(pos, (short)(int)Json.F(e, "yaw"), (int)Json.F(e, "num"), Json.S(e, "text"), Json.S(e, "flag")));
                else if (Json.S(e, "type") == "wallst")
                    walls.Add(new WallSt(pos, (short)(int)Json.F(e, "yaw"), Json.F(e, "gold", 0f) > 0.5f, Json.S(e, "flag")));
                else if (Json.S(e, "type") == "boulder")
                {
                    var pts = new List<Vector3>();
                    foreach (var q in Json.L(e, "path") ?? new List<object>()) { var l = q as List<object>; if (l != null && l.Count >= 3) pts.Add(new Vector3(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2]))); }
                    if (pts.Count >= 2) boulders.Add(new Boulder(pts, Json.S(e, "model"), (int)Json.F(e, "loop"), Json.F(e, "fall", 1f) > 0.5f, (int)Json.F(e, "rotz", 1f), Json.F(e, "speed", 9.2f)));
                }
                else if (Json.S(e, "type") == "goma")
                    gomas.Add(new Goma(pos));
                else if (Json.S(e, "type") == "st")
                    sts.Add(new Skulltula(pos, (short)(int)Json.F(e, "yaw"), Json.F(e, "big", 0f) > 0.5f));
            }
            Plugin.Log("OoT-Gegner: " + babas.Count + " Deku-Ranhas, " + kares.Count + " welke Deku-Ranhas");
        }

        public static void Clear()
        {
            foreach (var b in babas) b.Despawn();
            foreach (var b in kares) b.Despawn();
            foreach (var b in sts) b.Despawn();
            sts.Clear();
            foreach (var b in walls) b.Despawn();
            walls.Clear();
            foreach (var b in boulders) b.Despawn();
            boulders.Clear();
            foreach (var b in gomas) b.Despawn();
            gomas.Clear();
            foreach (var b in scrubs) b.Despawn();
            foreach (var n in nuts) n.Kill();
            scrubs.Clear(); nuts.Clear();
            babas.Clear(); kares.Clear();
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null; forPack = null;
        }

        /// <summary>Jeden Frame aus OotPlay.</summary>
        public static void Tick(Player p)
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if ((babas.Count == 0 && kares.Count == 0 && sts.Count == 0 && scrubs.Count == 0 && walls.Count == 0 && boulders.Count == 0 && gomas.Count == 0) || p == null) return;
                if (holder == null) holder = new GameObject("oot_enemies");
                var me = p.transform.position;
                acc += Mathf.Min(Time.deltaTime, 0.1f) * 20f;
                int steps = 0;
                while (acc >= 1f && steps < 4) { acc -= 1f; steps++; }
                foreach (var b in babas)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f && !b.Gone) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); b.Reset(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw(acc);
                }
                foreach (var b in walls)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f && !b.Gone) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw();
                }
                foreach (var b in scrubs)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f && !b.Gone) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw();
                }
                for (int i = nuts.Count - 1; i >= 0; i--)
                {
                    var n = nuts[i];
                    for (int k = 0; k < steps && n.Alive; k++) n.Update(p);
                    if (!n.Alive) { n.Kill(); nuts.RemoveAt(i); }
                }
                foreach (var b in sts)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f && !b.Gone) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw();
                }
                foreach (var b in boulders)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw();
                }
                foreach (var b in gomas)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f && !b.Gone) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) { b.Update(p); if (b.Root == null) break; }
                    b.Draw();
                }
                foreach (var b in kares)
                {
                    float d = (b.Home - me).magnitude;
                    if (b.Root == null && d < 60f) b.Spawn(holder.transform);
                    if (b.Root == null) continue;
                    if (d > 80f) { b.Despawn(); continue; }
                    for (int i = 0; i < steps; i++) b.Update(p);
                    b.Draw();
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotEnemies.Tick"); }
        }

        // ---------- OoT-Mathematik (z_lib.c), Winkel als s16 ----------
        const float UPD = 1.5f;                       // R_UPDATE_RATE * 0.5
        static bool ScaledStepToS(ref short v, int target, int step)
        {
            if (step != 0)
            {
                if ((short)(v - target) > 0) step = -step;
                v = (short)(v + (short)(step * UPD));
                if ((short)(v - target) * step >= 0) { v = (short)target; return true; }
            }
            else if (target == v) return true;
            return false;
        }
        static void ApproachS(ref short v, int target, int scale, int step)
        {
            short diff = (short)(target - v);
            diff = (short)(diff / scale);
            if (diff > step) v = (short)(v + step); else if (diff < -step) v = (short)(v - step); else v = (short)(v + diff);
        }
        static short SmoothStepToS(ref short v, int target, int scale, int step, int minStep)
        {
            short diff = (short)(target - v);
            if (v != (short)target)
            {
                short st = (short)(diff / scale);
                if (st > minStep || st < -minStep)
                {
                    if (st > step) st = (short)step;
                    if (st < -step) st = (short)(-step);
                    v = (short)(v + st);
                }
                else if (diff >= 0) { v = (short)(v + minStep); if ((short)(target - v) <= 0) v = (short)target; }
                else { v = (short)(v - minStep); if ((short)(target - v) >= 0) v = (short)target; }
            }
            return diff;
        }
        static bool StepToF(ref float v, float target, float step)
        {
            if (step != 0f)
            {
                if (target < v) step = -step;
                v += step;
                if ((v - target) * step >= 0f) { v = target; return true; }
            }
            else if (target == v) return true;
            return false;
        }
        static float SinS(int a) { return Mathf.Sin((short)a * (Mathf.PI / 32768f)); }
        static float CosS(int a) { return Mathf.Cos((short)a * (Mathf.PI / 32768f)); }
        static short Atan2S(float x, float z) { return (short)Mathf.RoundToInt(Mathf.Atan2(x, z) * (32768f / Mathf.PI)); }

        /// <summary>OoT-Drehung (Y, dann X, dann Z, wie Matrix_SetTranslateRotateYXZ) -> Valheim-Rotation fuer ein GLB (lokal +Z = OoT vorn).</summary>
        public static Quaternion Rot(int rx, int ry, int rz)
        {
            float sx = SinS(rx), cx = CosS(rx), sy = SinS(ry), cy = CosS(ry), sz = SinS(rz), cz = CosS(rz);
            var f = new Vector3(cx * sy, -sx, cx * cy);
            var u = new Vector3(-sz * cy + cz * sx * sy, cz * cx, sz * sy + cz * sx * cy);
            return Quaternion.LookRotation(new Vector3(f.x, f.y, -f.z), new Vector3(u.x, u.y, -u.z));
        }
        static Vector3 V(float x, float y, float z) { return new Vector3(x * S, y * S, -z * S); }

        // Spielerlage in OoT-Einheiten relativ zum Ursprung
        static Vector3 Local(Vector3 world, Vector3 home) { var d = world - home; return new Vector3(d.x / S, d.y / S, -d.z / S); }

        static GameObject Model(string rel, Transform parent)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            string path = Path.Combine(forPack.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return null;
            var m = Glb.Instantiate(path);
            m.transform.SetParent(parent, false);
            foreach (var c in m.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.Destroy(c);
            foreach (var r in m.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
            return m;
        }

        // ================= Deku-Ranha =================
        class Dekubaba
        {
            public Vector3 Home; public bool Gone; public GameObject Root;
            readonly short homeYaw; readonly float sf; readonly bool big;
            short rx, ry, rz; readonly short[] stem = new short[3]; short wobble;
            int state, health; float scale;                 // scale = actor.scale.x
            Vector3 pos;                                    // actor.world.pos (OoT-Einheiten relativ zu Home)
            float speed, velY; short moveYaw;               // fuer das Wegfliegen beim Sterben
            enum A { Wait, Exit, Enter, ChompAir, Prepare, Attack, Recover, Soothe, Attacked, Vulnerable, Wobble, DieStick, Die, Stick }
            A act; bool acOn = true; float colorT; Color colorC;
            GameObject head, leaves, stick; readonly GameObject[] stems = new GameObject[3];
            Animation anim; string clip = "chomp"; float frame, speedA, lastFrame, prevFrame; bool loop;
            readonly List<Renderer> rends = new List<Renderer>(); readonly List<Material> mats = new List<Material>(); readonly List<Color> emBase = new List<Color>();
            float lastFlash = -1f;
            OotCombat.Target target; Transform focus; Func<Vector3, float, bool> hitFn;

            public Dekubaba(Vector3 home, short yaw, bool big)
            {
                Home = home; homeYaw = yaw; this.big = big; sf = big ? 2.5f : 1f; Reset();
            }

            public void Reset()
            {
                health = big ? 4 : 2; ry = homeYaw; rz = 0; Gone = false;
                SetupWait(); state = 0;
            }

            public void Spawn(Transform parent)
            {
                Root = new GameObject("oot_dekubaba");
                Root.transform.SetParent(parent, false); Root.transform.position = Home;
                head = Model(babaHead, Root.transform);
                leaves = Model(babaLeaves, Root.transform);
                for (int i = 0; i < 3; i++) stems[i] = Model(babaStems[i], Root.transform);
                stick = Model(babaStick, Root.transform);
                if (stick != null) stick.SetActive(false);
                anim = head != null ? head.GetComponentInChildren<Animation>() : null;
                if (anim != null) foreach (AnimationState s in anim) { s.enabled = false; s.weight = 1f; s.speed = 0f; }
                rends.Clear(); rends.AddRange(Root.GetComponentsInChildren<Renderer>(true));
                mats.Clear(); emBase.Clear();
                foreach (var r in rends) foreach (var m in r.materials) { mats.Add(m); emBase.Add(m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black); }
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                // Z-Anvisieren: ATTENTION_RANGE_1 (klein) bzw. _2 (gross)
                float leash, range = OotCombat.RangeM(big ? 2 : 1, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 0.45f * sf, Enemy = true, Range = range, Leash = Mathf.Min(leash, 20f), Alive = () => act != A.Die && act != A.DieStick && act != A.Stick && Root != null };
                OotCombat.Add(target);
                hitFn = SwordHit; OotCombat.Hittables.Add(hitFn);
                SetAnim("chomp", 0f, 0f, true);
            }

            public void Despawn()
            {
                if (target != null) OotCombat.Remove(target);
                if (hitFn != null) OotCombat.Hittables.Remove(hitFn);
                target = null; hitFn = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; head = null; anim = null;
            }

            // ---------- Animation (SkelAnime: Bild += Tempo * 1,5 je Spielbild) ----------
            void SetAnim(string c, float start, float spd, bool lp)
            {
                clip = c; frame = start; speedA = spd; loop = lp; prevFrame = start; lastFrame = LastFrame(c);
            }
            float LastFrame(string c)
            {
                if (anim == null || anim[c] == null) return c == "chomp" ? 23f : 30f;
                return Mathf.Max(1f, Mathf.Round(anim[c].clip.length * 30f));
            }
            void AnimUpdate()
            {
                prevFrame = frame;
                frame += speedA * UPD;
                if (loop) { float n = lastFrame; if (n > 0f) { while (frame >= n) frame -= n; while (frame < 0f) frame += n; } }
                else frame = Mathf.Clamp(frame, 0f, lastFrame);
            }
            bool OnFrame(float f)
            {
                if (speedA == 0f) return false;
                float a = prevFrame, b = frame;
                if (speedA > 0f) return b >= a ? (f > a && f <= b) || (a == f && f == 0f && b == a) : (f > a || f <= b);
                return b <= a ? (f < a && f >= b) : (f < a || f >= b);
            }

            void Sfx(string n, float vol = 0.9f) { OotSfx.Play(n, vol, Root != null ? Root.transform.position + V(pos.x, pos.y, pos.z) : (Vector3?)null, 0.03f); }
            void Flash(Color c, float t) { colorC = c; colorT = t; }

            // ---------- Zustaende (z_en_dekubaba.c) ----------
            void SetupWait()
            {
                stem[0] = stem[1] = stem[2] = rx = -0x4000;
                pos = new Vector3(0f, 14f * sf, 0f);
                scale = sf * 0.01f * 0.5f;
                state = 45; act = A.Wait; acOn = true;
            }
            void SetupExit() { SetAnim("chomp", 0f, LastFrame("chomp") / 15f, false); state = 15; act = A.Exit; Sfx("NA_SE_EN_DUMMY482"); }
            void SetupEnter() { SetAnim("chomp", LastFrame("chomp"), -1.5f, false); state = 15; act = A.Enter; }
            void SetupChompAir() { state = (int)(LastFrame("chomp") * 2); SetAnim("chomp", 0f, 1f, true); act = A.ChompAir; }
            void SetupPrepare() { state = 8; act = A.Prepare; speedA = 0f; }
            void SetupAttack() { SetAnim("idle", 0f, 1f, false); state = 0; act = A.Attack; }
            void SetupRecover() { SetAnim("idle", 15f, 1f, false); state = 0; act = A.Recover; }
            void SetupSoothe() { state = 9; acOn = true; act = A.Soothe; speedA = -1f; }
            void SetupAttacked(int type)
            {
                SetAnim("idle", 0f, 1f, false); state = type; acOn = false; scale = sf * 0.01f;
                if (type == 2) Flash(new Color(0.45f, 0.55f, 1f), 62f / 20f); else Flash(new Color(1f, 0.3f, 0.3f), 42f / 20f);
                act = A.Attacked;
            }
            void SetupDieStick()
            {
                state = 0; speedA = 0f; velY = 4f; moveYaw = (short)(ry + 0x8000); speed = sf * 3f; acOn = false; act = A.DieStick;
            }
            void SetupDie() { SetAnim("chomp", LastFrame("chomp"), -1.5f, false); acOn = false; act = A.Die; }
            void SetupVulnerable()
            {
                if (state == 1) { SetAnim("chomp", 0f, 4f, true); state = 40; } else { SetAnim("chomp", 0f, 0f, true); state = 60; }
                pos = new Vector3(0f, 60f * sf, 0f);
                act = A.Vulnerable;
            }
            void SetupWobble() { wobble = -0x6000; stem[2] = -0x5000; stem[1] = -0x4800; Flash(new Color(1f, 0.3f, 0.3f), 35f / 20f); acOn = false; act = A.Wobble; }
            void SetupStick()
            {
                scale = 0.03f; rx = 0x0800; pos.y = GroundY(pos) + 2f; state = 200; act = A.Stick;
            }

            void HeadPos()
            {
                float dxz = (CosS(stem[0]) + CosS(stem[1]) + CosS(stem[2])) * 20f;
                pos.x = dxz * sf * SinS(ry);
                pos.y = -(SinS(stem[0]) + SinS(stem[1]) + SinS(stem[2])) * 20f * sf;
                pos.z = dxz * sf * CosS(ry);
            }

            float ExitDxz(float dy, bool exit)
            {
                if (rx < -0x38E3) return 0f;
                if (rx < -0x238E)
                {
                    if (exit) ScaledStepToS(ref stem[0], -0x5555, 0x38E); else ScaledStepToS(ref stem[0], -0x4000, 0x555);
                    return 20f * CosS(stem[0]);
                }
                if (rx < -0xE38)
                {
                    if (exit) { ScaledStepToS(ref stem[0], -0xAAA, 0x38E); ScaledStepToS(ref stem[1], -0x5555, 0x38E); ScaledStepToS(ref stem[2], -0x5555, 0x222); }
                    else { ScaledStepToS(ref stem[0], -0x5555, 0x555); ScaledStepToS(ref stem[1], -0x4000, 0x555); ScaledStepToS(ref stem[2], -0x4000, 0x333); }
                }
                else
                {
                    if (exit) { ScaledStepToS(ref stem[0], -0xAAA, 0x38E); ScaledStepToS(ref stem[1], -0x31C7, 0x222); ScaledStepToS(ref stem[2], -0x5555, 0x222); }
                    else { ScaledStepToS(ref stem[0], -0x5555, 0x555); ScaledStepToS(ref stem[1], -0x5555, 0x333); ScaledStepToS(ref stem[2], -0x4000, 0x333); }
                }
                return 20f * (CosS(stem[0]) + CosS(stem[1])) + ((dy - 20f * (-SinS(stem[0]) - SinS(stem[1]))) * CosS(stem[2])) / -SinS(stem[2]);
            }

            public void Update(Player p)
            {
                var pl = Local(p.transform.position, Home);
                float distXZHome = new Vector2(pl.x, pl.z).magnitude;
                float xzDist = new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude;
                float yDist = pl.y - pos.y;
                short yawHome = Atan2S(pl.x, pl.z), yawActor = Atan2S(pl.x - pos.x, pl.z - pos.z);
                if (colorT > 0f) colorT -= 0.05f;

                switch (act)
                {
                    case A.Wait:
                        if (state != 0) state--;
                        pos = new Vector3(0f, 14f * sf, 0f);
                        if (state == 0 && xzDist < 200f * sf && Mathf.Abs(yDist) < 30f * sf) SetupExit();
                        break;
                    case A.Exit:
                    {
                        if (state != 0) state--;
                        AnimUpdate();
                        scale = sf * 0.01f * (0.5f + ((15 - state) * 0.5f) / 15f);
                        ScaledStepToS(ref rx, 0x1800, 0x800);
                        float dy = Mathf.Sin(Mathf.Min((15 - state) * (1f / 15f), 0.7f) * Mathf.PI) * 32f + 14f;
                        float dxz = ExitDxz(dy, true);
                        if (state < 10) ApproachS(ref ry, yawHome, 2, 0xE38);
                        pos = new Vector3(dxz * sf * SinS(ry), dy * sf, dxz * sf * CosS(ry));
                        if (state == 0) { if (distXZHome < 240f * sf) SetupPrepare(); else SetupEnter(); }
                        break;
                    }
                    case A.Enter:
                    {
                        if (state != 0) state--;
                        AnimUpdate();
                        scale = sf * 0.01f * (0.5f + state * (1f / 30f));
                        ScaledStepToS(ref rx, -0x4000, 0x300);
                        float dy = Mathf.Sin(Mathf.Min(state * 0.033f, 0.7f) * Mathf.PI) * 32f + 14f;
                        float dxz = ExitDxz(dy, false);
                        pos = new Vector3(dxz * sf * SinS(ry), dy * sf, dxz * sf * CosS(ry));
                        if (state == 0) SetupWait();
                        break;
                    }
                    case A.ChompAir:
                        AnimUpdate();
                        if (OnFrame(0f) || OnFrame(12f)) Sfx(big ? "NA_SE_EN_DEKU_MOUTH" : "NA_SE_EN_DEKU_JR_MOUTH", 0.7f);
                        if (state != 0) state--;
                        ApproachS(ref ry, yawHome, 2, (state % 5) * 0x222);
                        if (state < 10) { stem[0] += 0x16C; stem[1] += 0x16C; stem[2] += 0xB6; rx += 0x222; }
                        else if (state < 20) { stem[0] -= 0x16C; stem[1] += 0x111; rx += 0x16C; }
                        else if (state < 30) { stem[1] -= 0x111; rx -= 0xB6; }
                        else { stem[1] -= 0xB6; stem[2] += 0xB6; rx -= 0x16C; }
                        HeadPos();
                        if (distXZHome > 240f * sf) SetupEnter();
                        else if (state == 0 || xzDist < 80f * sf) SetupPrepare();
                        break;
                    case A.Attack:
                        AnimUpdate();
                        if (state == 0)
                        {
                            if (OnFrame(1f)) Sfx(big ? "NA_SE_EN_DEKU_ATTACK" : "NA_SE_EN_DEKU_JR_ATTACK");
                            ScaledStepToS(ref rx, 0, 0x222);
                            int stepBase = (int)(frame * 10f);
                            bool lying = true;
                            lying &= ScaledStepToS(ref stem[0], -0xE38, stepBase + 0x38E);
                            lying &= ScaledStepToS(ref stem[1], -0xE38, stepBase + 0x71C);
                            lying &= ScaledStepToS(ref stem[2], -0xE38, stepBase + 0xE38);
                            if (lying) { SetAnim("chomp", 0f, 4f, true); state = 1; acOn = true; }
                        }
                        else if (state > 10) SetupRecover();
                        else
                        {
                            state++;
                            if (state >= 4 && Mathf.Abs((short)(yawActor - ry)) >= 0x16C) ApproachS(ref ry, yawActor, 15, 0x71C);
                            if (OnFrame(0f) || OnFrame(12f)) Sfx(big ? "NA_SE_EN_DEKU_MOUTH" : "NA_SE_EN_DEKU_JR_MOUTH", 0.7f);
                        }
                        HeadPos();
                        if (act == A.Attack || act == A.Recover) BiteCheck(p);
                        break;
                    case A.Prepare:
                        if (state != 0) state--;
                        SmoothStepToS(ref rx, 0x1800, 2, 0xE38, 0x71C);
                        ApproachS(ref ry, yawHome, 2, 0xE38);
                        ScaledStepToS(ref stem[0], 0xAAA, 0x444);
                        ScaledStepToS(ref stem[1], -0x4718, 0x888);
                        ScaledStepToS(ref stem[2], -0x6AA4, 0x888);
                        if (state == 0) SetupAttack();
                        HeadPos();
                        break;
                    case A.Recover:
                        AnimUpdate();
                        if (state == 0)
                        {
                            ScaledStepToS(ref rx, -0x93E, 0x38E);
                            ScaledStepToS(ref stem[0], -0x888, 0x16C);
                            ScaledStepToS(ref stem[1], -0x888, 0x16C);
                            if (ScaledStepToS(ref stem[2], -0x888, 0x16C)) state = 1;
                        }
                        else if (state == 11)
                        {
                            ScaledStepToS(ref rx, -0x93E, 0x200); ScaledStepToS(ref stem[0], -0xAAA, 0x200); ScaledStepToS(ref stem[2], -0x5C71, 0x200);
                            if (ScaledStepToS(ref stem[1], 0x238C, 0x200)) state = 12;
                        }
                        else if (state == 18)
                        {
                            ScaledStepToS(ref rx, 0x2AA8, 0xAAA);
                            if (ScaledStepToS(ref stem[0], 0x1554, 0x5B0)) state = 25;
                            ScaledStepToS(ref stem[1], -0x38E3, 0xAAA); ScaledStepToS(ref stem[2], -0x5C71, 0x2D8);
                        }
                        else if (state == 25)
                        {
                            ScaledStepToS(ref rx, -0x5550, 0xAAA);
                            if (ScaledStepToS(ref stem[0], -0x6388, 0x93E)) state = 26;
                            ScaledStepToS(ref stem[1], -0x3FFC, 0x4FA); ScaledStepToS(ref stem[2], -0x238C, 0x444);
                        }
                        else if (state == 26)
                        {
                            ScaledStepToS(ref rx, 0x1800, 0x93E);
                            if (ScaledStepToS(ref stem[0], -0x1555, 0x71C)) state = 27;
                            ScaledStepToS(ref stem[1], -0x38E3, 0x2D8); ScaledStepToS(ref stem[2], -0x5C71, 0x5B0);
                        }
                        else if (state >= 27)
                        {
                            state++;
                            if (state > 30) { if (xzDist < 80f * sf) SetupPrepare(); else SetupChompAir(); }
                        }
                        else
                        {
                            state++;
                            if (state == 10) Sfx("NA_SE_EN_DEKU_SCRAPE");
                            if (state >= 12) ScaledStepToS(ref stem[2], -0x5C71, 0x88);
                        }
                        HeadPos();
                        break;
                    case A.Soothe:
                        AnimUpdate();
                        if (state >= 9)
                        {
                            int rem = 0;
                            rem |= SmoothStepToS(ref rx, 0x1800, 1, 0x11C6, 0x71C);
                            rem |= SmoothStepToS(ref stem[0], -0x1555, 1, 0xAAA, 0x71C);
                            rem |= SmoothStepToS(ref stem[1], -0x38E3, 1, 0xE38, 0x71C);
                            rem |= SmoothStepToS(ref stem[2], -0x5C71, 1, 0x11C6, 0x71C);
                            if (rem == 0) state = 8;
                        }
                        else { if (state != 0) state--; if (state == 0) SetupChompAir(); }
                        HeadPos();
                        break;
                    case A.Attacked:
                    {
                        AnimUpdate();
                        bool up = true;
                        up &= ScaledStepToS(ref rx, -0x4000, 0xE38);
                        up &= ScaledStepToS(ref stem[0], -0x4000, 0xE38);
                        up &= ScaledStepToS(ref stem[1], -0x4000, 0xE38);
                        up &= ScaledStepToS(ref stem[2], -0x4000, 0xE38);
                        if (up)
                        {
                            if (health == 0) SetupDie();
                            else
                            {
                                acOn = true;
                                if (state == 0) { if (xzDist < 80f * sf) SetupPrepare(); else SetupSoothe(); }
                                else SetupVulnerable();
                            }
                        }
                        if (act != A.Vulnerable) HeadPos();
                        break;
                    }
                    case A.Vulnerable:
                        AnimUpdate();
                        if (state != 0) state--;
                        if (state == 0) { if (xzDist < 80f * sf) SetupPrepare(); else SetupSoothe(); }
                        break;
                    case A.Wobble:
                    {
                        AnimUpdate();
                        ScaledStepToS(ref rx, stem[0], 0x71C);
                        ScaledStepToS(ref stem[0], stem[1], 0x71C);
                        ScaledStepToS(ref stem[1], stem[2], 0x71C);
                        if (ScaledStepToS(ref stem[2], wobble, 0x71C)) wobble = (short)(int)(-16384f - (wobble + 0x4000) * 0.8f);
                        short t = (short)(wobble + 0x4000);
                        if (Mathf.Abs(t) < 0x100) { acOn = true; if (xzDist < 80f * sf) SetupPrepare(); else SetupSoothe(); }
                        HeadPos();
                        break;
                    }
                    case A.DieStick:
                    {
                        StepToF(ref speed, 0f, sf * 0.1f);
                        // Actor_MoveXZGravity: Tempo und Schwerkraft je Bild * 1,5
                        velY = Mathf.Max(velY - 0.8f, -20f);
                        pos.x += SinS(moveYaw) * speed * UPD; pos.z += CosS(moveYaw) * speed * UPD; pos.y += velY * UPD;
                        float gy = GroundY(pos);
                        bool touch = pos.y <= gy;
                        if (touch) pos.y = gy;
                        if (state == 0)
                        {
                            ScaledStepToS(ref rx, 0x4800, 0x71C); ScaledStepToS(ref stem[0], 0x4800, 0x71C); ScaledStepToS(ref stem[1], 0x4800, 0x71C);
                            if (scale > 0.005f && touch) { scale = 0f; speed = 0f; }
                            if (touch) { Sfx("NA_SE_EN_DODO_M_GND"); state = 1; }
                        }
                        else if (state == 1) SetupStick();
                        break;
                    }
                    case A.Die:
                        AnimUpdate();
                        StepToF(ref pos.y, 0f, sf * 5f);
                        if (StepToF(ref scale, sf * 0.1f * 0.01f, sf * 0.1f * 0.01f))
                        {
                            OotItems.DropAt(Root.transform.position + V(pos.x, pos.y, pos.z), "nuts");
                            Gone = true; Despawn();
                            return;
                        }
                        rz += 0x1C70;
                        break;
                    case A.Stick:
                        if (state != 0) state--;
                        var sp = Root.transform.position + V(pos.x, pos.y, pos.z);
                        if ((p.transform.position - sp).magnitude < 0.75f) { if (GotStick != null) GotStick("stick"); Gone = true; Despawn(); return; }
                        if (state == 0) { Gone = true; Despawn(); return; }
                        break;
                }
            }

            float GroundY(Vector3 local)
            {
                var w = Root.transform.position + V(local.x, local.y + 30f, local.z);
                RaycastHit h;
                if (Physics.Raycast(w, Vector3.down, out h, 6f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore))
                    return (h.point.y - Home.y) / S;
                return 0f;
            }

            // Biss: Kopf-Kugel (15 Einheiten, 10 vor dem Kopf) gegen Links Zylinder (Radius 12, Hoehe ~50)
            void BiteCheck(Player p)
            {
                if (act != A.Attack) return;
                var hp = Root.transform.position + V(pos.x, pos.y, pos.z) + Rot(rx, ry, rz) * new Vector3(0f, 1f * S * sf, 10f * S * sf);
                var lp = p.transform.position;
                float dxz = new Vector2(hp.x - lp.x, hp.z - lp.z).magnitude;
                if (dxz < (15f * sf + 12f) * S && hp.y > lp.y - 0.3f && hp.y < lp.y + 1.9f)
                {
                    OotCombat.Damage(big ? 0x10 : 0x08, hp);
                    SetupSoothe();
                }
            }

            // Schwert trifft? (Kopf; im steifen Zustand auch der Stiel)
            bool SwordHit(Vector3 lp, float lyaw)
            {
                if (Root == null || !acOn || act == A.Die || act == A.DieStick || act == A.Stick) return false;
                var fw = Quaternion.Euler(0f, lyaw, 0f) * Vector3.forward;
                var chest = lp + Vector3.up * 0.75f;
                bool hit = false;
                var pts = new List<Vector3> { Root.transform.position + V(pos.x, pos.y, pos.z) };
                if (act == A.Vulnerable || act == A.Wait) pts.Add(Root.transform.position + V(0f, 30f * sf, 0f));
                foreach (var w in pts)
                {
                    var d = w - chest; float dy = d.y; d.y = 0f;
                    if (d.magnitude < 1.75f + 0.25f * sf && Mathf.Abs(dy) < 1.3f * sf && (d.magnitude < 0.4f || Vector3.Angle(fw, d) < 80f)) { hit = true; break; }
                }
                if (!hit) return false;
                if (act == A.Wait) { Sfx("NA_SE_IT_SHIELD_REFLECT_SW", 0.6f); return true; }       // im Laub: hart (COL_MATERIAL_HARD)
                int newHealth = health - 1;                                                         // Kokiri-Schwert: 1 Schaden
                if (act != A.Vulnerable)
                {
                    if (act == A.Recover) { if (newHealth <= 0) newHealth = 1; SetupAttacked(1); }
                    else SetupAttacked(0);
                }
                else { if (newHealth > 0) SetupWobble(); else SetupDieStick(); }
                health = Mathf.Max(0, newHealth);
                if (health != 0) Sfx("NA_SE_EN_DEKU_DAMAGE");
                else Sfx(big ? "NA_SE_EN_DEKU_DEAD" : "NA_SE_EN_DEKU_JR_DEAD");
                OotSfx.Play("NA_SE_IT_SWORD_STRIKE", 0.7f);
                return true;
            }

            // ---------- Zeichnen (EnDekubaba_Draw) ----------
            public void Draw(float alpha)
            {
                if (Root == null) return;
                var home = Root.transform.position;
                float stemScale = modelScale * sf;
                var hpos = home + V(pos.x, pos.y, pos.z);
                bool stickMode = act == A.Stick;
                if (head != null)
                {
                    head.SetActive(!stickMode && scale > 0.0001f);
                    head.transform.position = hpos;
                    head.transform.rotation = Rot(rx, ry, rz);
                    head.transform.localScale = Vector3.one * (modelScale * scale / 0.01f);
                }
                if (anim != null && anim[clip] != null)
                {
                    foreach (AnimationState s in anim) s.enabled = s.name == clip;
                    var st = anim[clip]; st.weight = 1f; st.speed = 0f; st.time = Mathf.Max(0f, frame) / 30f;
                }
                if (leaves != null) { leaves.transform.position = home; leaves.transform.rotation = Rot(0, homeYaw, 0); leaves.transform.localScale = Vector3.one * stemScale; leaves.SetActive(!stickMode); }
                int n = act == A.DieStick ? 2 : 3;
                float x = pos.x, y = pos.y, z = pos.z;
                for (int i = 0; i < 3; i++)
                {
                    var g = stems[i]; if (g == null) continue;
                    if (stickMode) { g.SetActive(false); continue; }
                    if (act == A.Wait)
                    {
                        g.SetActive(i == 0);
                        if (i == 0) { g.transform.position = home + V(0f, -6f * sf, 0f); g.transform.rotation = Rot(stem[0], ry, 0); g.transform.localScale = Vector3.one * stemScale; }
                        continue;
                    }
                    if (i >= n)
                    {
                        // Stiel-Rest im Boden (EnDekubaba_DrawStem2)
                        g.SetActive(true); g.transform.position = home; g.transform.rotation = Rot(0, homeYaw, 0) * Rot(stem[2], ry, 0); g.transform.localScale = Vector3.one * stemScale;
                        continue;
                    }
                    y += 20f * SinS(stem[i]) * sf;
                    float dxz = CosS(stem[i]) * 20f * sf;
                    x -= dxz * SinS(ry); z -= dxz * CosS(ry);
                    g.SetActive(true);
                    g.transform.position = home + V(x, y, z);
                    g.transform.rotation = Rot(stem[i], ry, 0);
                    g.transform.localScale = Vector3.one * stemScale;
                    if (i == 0 && focus != null) focus.position = act == A.Wobble ? home + V(0f, 40f * sf, 0f) : home + V(x, y, z);
                }
                if (act == A.Wait && focus != null) focus.position = hpos;
                if (stick != null)
                {
                    bool vis = stickMode && (state > 40 || (state & 1) == 1);
                    stick.SetActive(vis);
                    if (vis)
                    {
                        var r = Rot(rx, ry, rz);
                        stick.transform.rotation = r;
                        stick.transform.position = hpos + r * new Vector3(0f, 0f, 200f * 0.03f * S);
                        stick.transform.localScale = Vector3.one * (modelScale * 0.03f / 0.01f);
                    }
                }
                // Treffer-Farbe (Actor_SetColorFilter): rot/blau aufleuchten
                float k = colorT > 0f ? Mathf.Clamp01(colorT * 2f) : 0f;
                if (k != lastFlash)
                {
                    lastFlash = k;
                    for (int i = 0; i < mats.Count; i++)
                    {
                        var m = mats[i]; if (m == null || !m.HasProperty("_EmissionColor")) continue;
                        m.EnableKeyword("_EMISSION");
                        m.SetColor("_EmissionColor", emBase[i] + colorC * (0.8f * k));
                    }
                }
            }
        }

        // ================= Welke Deku-Ranha (En_Karebaba): im Kokiri-Wald auf dem Weg zum Deku-Baum =================
        // Wacht auf, wenn Link nahe ist, steht steif und kreiselt zwischendurch; ein Schwerthieb -> faellt um, laesst einen Deku-Stab da,
        // waechst nach 10 s wieder nach.
        class Karebaba
        {
            public Vector3 Home; public GameObject Root;
            readonly short homeYaw; short rx, ry; int prm; float scale = 0.005f; Vector3 pos; float speed, velY; short moveYaw;
            enum A { Grow, Idle, Awaken, Upright, Spin, Dying, Drop, Retract, Dead, Regrow }
            A act; int headR = 4;
            GameObject head, leaves, stick; readonly GameObject[] stems = new GameObject[3];
            Animation anim; float frame, speedA, prevFrame, lastFrame = 23f;
            readonly List<Renderer> rends = new List<Renderer>();
            OotCombat.Target target; Transform focus; Func<Vector3, float, bool> hitFn;

            public Karebaba(Vector3 home, short yaw) { Home = home; homeYaw = yaw; SetupIdle(); }
            public string Info()
            {
                if (Root == null) return "kare " + Home.ToString("F1") + " -";
                string h = head != null ? head.transform.position.ToString("F1") + " s" + head.transform.lossyScale.x.ToString("F5") : "kein Kopf";
                var r0 = head != null ? head.GetComponentInChildren<Renderer>() : null;
                string mat = r0 != null && r0.sharedMaterial != null ? r0.sharedMaterial.shader.name + " b" + r0.bounds.size.ToString("F2") : "";
                return "kare " + Home.ToString("F1") + " " + act + " kopf " + h + " " + mat;
            }

            public void Spawn(Transform parent)
            {
                Root = new GameObject("oot_karebaba");
                Root.transform.SetParent(parent, false); Root.transform.position = Home;
                head = Model(babaHead, Root.transform);
                leaves = Model(babaLeaves, Root.transform);
                for (int i = 0; i < 3; i++) stems[i] = Model(babaStems[i], Root.transform);
                stick = Model(babaStick, Root.transform); if (stick != null) stick.SetActive(false);
                anim = head != null ? head.GetComponentInChildren<Animation>() : null;
                if (anim != null) { foreach (AnimationState s in anim) { s.enabled = false; s.weight = 1f; s.speed = 0f; } if (anim["chomp"] != null) lastFrame = Mathf.Max(1f, Mathf.Round(anim["chomp"].clip.length * 30f)); }
                rends.Clear(); rends.AddRange(Root.GetComponentsInChildren<Renderer>(true));
                // welk: dunkel und braeunlich (func_80026230: schwarzer Nebel ganz nah)
                if (Tint) foreach (var r in rends) foreach (var m in r.materials) if (m.HasProperty("_Color")) m.color = new Color(0.74f, 0.63f, 0.5f, 1f);
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                float leash, range = OotCombat.RangeM(1, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 25f * S * 0.5f, Enemy = true, Range = range, Leash = leash,
                                                Alive = () => Root != null && (act == A.Idle || act == A.Awaken || act == A.Upright || act == A.Spin || act == A.Retract) };
                OotCombat.Add(target);
                hitFn = SwordHit; OotCombat.Hittables.Add(hitFn);
            }

            public void Despawn()
            {
                if (target != null) OotCombat.Remove(target);
                if (hitFn != null) OotCombat.Hittables.Remove(hitFn);
                target = null; hitFn = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; head = null; anim = null;
                if (act == A.Dying || act == A.Drop || act == A.Dead || act == A.Regrow) SetupIdle();
            }

            void Sfx(string n, float vol = 0.9f) { OotSfx.Play(n, vol, Root != null ? Root.transform.position + V(pos.x, pos.y, pos.z) : (Vector3?)null, 0.03f); }
            void AnimUpd() { prevFrame = frame; frame += speedA * UPD; if (frame >= lastFrame) frame -= lastFrame; if (frame < 0f) frame += lastFrame; }
            bool OnFrame(float f) { float a = prevFrame, b = frame; return speedA > 0f && (b >= a ? (f > a && f <= b) : (f > a || f <= b)); }

            void SetupIdle() { scale = 0.005f; rx = -0x4000; act = A.Idle; pos = new Vector3(0f, 14f, 0f); headR = 4; }
            void SetupUpright() { if (act != A.Spin) scale = 0.01f; prm = 40; act = A.Upright; }

            public void Update(Player p)
            {
                var pl = Local(p.transform.position, Home);
                float distHome = new Vector2(pl.x, pl.z).magnitude;
                float xzDist = new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude, yDist = pl.y - pos.y;
                switch (act)
                {
                    case A.Idle:
                        if (xzDist < 200f && Mathf.Abs(yDist) < 30f) { speedA = 4f; act = A.Awaken; Sfx("NA_SE_EN_DUMMY482"); }
                        break;
                    case A.Awaken:
                        AnimUpd();
                        StepToF(ref scale, 0.01f, 0.0005f);
                        if (StepToF(ref pos.y, 60f, 5f)) SetupUpright();
                        ry += 0x1999;
                        break;
                    case A.Upright:
                        AnimUpd();
                        if (prm != 0) prm--;
                        if (OnFrame(0f) || OnFrame(12f)) Sfx("NA_SE_EN_DEKU_JR_MOUTH", 0.7f);
                        if (distHome > 240f) { speedA = -3f; act = A.Retract; headR = 4; }
                        else if (prm == 0) { prm = 40; act = A.Spin; }
                        break;
                    case A.Spin:
                    {
                        if (prm != 0) prm--;
                        AnimUpd();
                        if (OnFrame(0f) || OnFrame(12f)) Sfx("NA_SE_EN_DEKU_JR_MOUTH", 0.7f);
                        int v = 20 - prm; v = 20 - Mathf.Abs(v); if (v > 10) v = 10;
                        headR = 4 + v * 2;
                        rx = (short)(0xC000 - v * 0x100);
                        ry = (short)(ry + v * 0x2C0);
                        pos.y = SinS(rx) * -60f;
                        float c60 = CosS(rx) * 60f;
                        pos.x = SinS(ry) * c60; pos.z = CosS(ry) * c60;
                        if (prm == 0) SetupUpright();
                        break;
                    }
                    case A.Dying:
                    {
                        StepToF(ref speed, 0f, 0.1f);
                        velY = Mathf.Max(velY - 0.8f, -20f);
                        pos.x += SinS(moveYaw) * speed * UPD; pos.z += CosS(moveYaw) * speed * UPD; pos.y += velY * UPD;
                        float gy = GroundY(pos); bool touch = pos.y <= gy; if (touch) pos.y = gy;
                        if (prm == 0)
                        {
                            ScaledStepToS(ref rx, 0x4800, 0x71C);
                            if (scale > 0.005f && touch) { scale = 0f; speed = 0f; }
                            if (touch) { Sfx("NA_SE_EN_DODO_M_GND"); prm = 1; }
                        }
                        else if (prm == 1) { scale = 0.03f; rx = 0x0800; pos.y = GroundY(pos) + 2f; prm = 200; act = A.Drop; }      // Stab liegt flach (Endlage 0x4800 - 0x4000)
                        break;
                    }
                    case A.Drop:
                    {
                        if (prm != 0) prm--;
                        var sp = Root.transform.position + V(pos.x, pos.y, pos.z);
                        bool took = (p.transform.position - sp).magnitude < 0.75f && GotStick != null && OotItems.Take("stick");
                        if (took || prm == 0) { prm = 200; rx = -0x4000; pos = Vector3.zero; act = A.Dead; }
                        break;
                    }
                    case A.Retract:
                        AnimUpd();
                        StepToF(ref scale, 0.005f, 0.0005f);
                        if (StepToF(ref pos.y, 14f, 5f)) SetupIdle();
                        ry += 0x1999;
                        break;
                    case A.Dead:
                        if (prm != 0) prm--;
                        if (prm == 0) { scale = 0f; act = A.Regrow; prm = 0; }
                        break;
                    case A.Regrow:
                        prm++;
                        scale = 0.005f * prm * 0.05f; pos = new Vector3(0f, 14f * prm * 0.05f, 0f);
                        if (prm == 20) SetupIdle();
                        break;
                }
                // Kopf beisst (Kopf-Zylinder Radius 4..24, Hoehe 25/80, Schaden 0x08), ausser beim Wachsen/Sterben
                if (act == A.Idle || act == A.Awaken || act == A.Upright || act == A.Spin || act == A.Retract)
                {
                    var hp = Root.transform.position + V(pos.x, pos.y, pos.z);
                    var lp = p.transform.position;
                    float dxz = new Vector2(hp.x - lp.x, hp.z - lp.z).magnitude;
                    float h = (act == A.Idle || act == A.Retract) ? 25f : 80f;
                    if (dxz < (headR + 12f) * S && lp.y < hp.y + h * S * 0.5f && lp.y + 1.5f > hp.y - 0.2f) OotCombat.Damage(0x08, hp);
                }
            }

            float GroundY(Vector3 local)
            {
                var w = Root.transform.position + V(local.x, local.y + 30f, local.z);
                RaycastHit h;
                if (Physics.Raycast(w, Vector3.down, out h, 6f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore))
                    return (h.point.y - Home.y) / S;
                return 0f;
            }

            // Stiel-Zylinder an der Wurzel: im Boden hart, aufrecht (Radius 15, Hoehe 80) verwundbar fuer das Schwert
            bool SwordHit(Vector3 lp, float lyaw)
            {
                if (Root == null) return false;
                bool soft = act == A.Upright || act == A.Spin;
                bool hard = act == A.Idle || act == A.Awaken || act == A.Retract;
                if (!soft && !hard) return false;
                var fw = Quaternion.Euler(0f, lyaw, 0f) * Vector3.forward;
                var d = Root.transform.position - lp; d.y = 0f;
                float reach = 1.45f + (soft ? 15f : 7f) * S;
                if (d.magnitude > reach || (d.magnitude > 0.4f && Vector3.Angle(fw, d) > 80f)) return false;
                if (hard) { OotSfx.Play("NA_SE_IT_SHIELD_REFLECT_SW", 0.6f, Root.transform.position); return true; }
                prm = 0; velY = 4f; moveYaw = (short)(ry + 0x8000); speed = 3f; act = A.Dying;
                Sfx("NA_SE_EN_DEKU_JR_DEAD");
                OotSfx.Play("NA_SE_IT_SWORD_STRIKE", 0.7f);
                return true;
            }

            public void Draw()
            {
                if (Root == null) return;
                var home = Root.transform.position;
                var hpos = home + V(pos.x, pos.y, pos.z);
                bool drop = act == A.Drop, dead = act == A.Dead;
                float sc = (act == A.Regrow || act == A.Grow) ? prm * 0.0005f : 0.01f;
                if (head != null)
                {
                    head.SetActive(!drop && !dead && scale > 0.0001f);
                    head.transform.position = hpos; head.transform.rotation = Rot(rx, ry, 0);
                    head.transform.localScale = Vector3.one * (modelScale * scale / 0.01f);
                }
                if (anim != null && anim["chomp"] != null) { var st = anim["chomp"]; st.enabled = true; st.weight = 1f; st.speed = 0f; st.time = Mathf.Max(0f, frame) / 30f; }
                // Stiel: drei Stuecke gerade hinter dem Kopf (je 2000 Modell-Einheiten zurueck)
                var r = Rot(rx, ry, 0);
                int n = act == A.Dying ? 2 : 3;
                for (int i = 0; i < 3; i++)
                {
                    var g = stems[i]; if (g == null) continue;
                    bool vis = !drop && !dead && i < n;
                    g.SetActive(vis); if (!vis) continue;
                    g.transform.rotation = r;
                    g.transform.position = hpos + r * new Vector3(0f, 0f, -2000f * (i + 1) * sc * S);
                    g.transform.localScale = Vector3.one * (modelScale * sc / 0.01f);
                }
                if (leaves != null) { leaves.SetActive(true); leaves.transform.position = home; leaves.transform.rotation = Rot(0, homeYaw, 0); leaves.transform.localScale = Vector3.one * (modelScale * ((act == A.Grow) ? sc : 0.01f) / 0.01f); }
                if (act == A.Dying && stems[2] != null) { var g = stems[2]; g.SetActive(true); g.transform.position = home; g.transform.rotation = Rot(-0x4000, ry, 0); g.transform.localScale = Vector3.one * modelScale; }
                if (stick != null)
                {
                    bool vis = drop && (prm > 40 || (prm & 1) == 1);
                    stick.SetActive(vis);
                    if (vis) { stick.transform.rotation = r; stick.transform.position = hpos + r * new Vector3(0f, 0f, 200f * 0.03f * S); stick.transform.localScale = Vector3.one * (modelScale * 0.03f / 0.01f); }
                }
                if (focus != null) focus.position = home + V(0f, Mathf.Min(pos.y + scale * 10f / 0.01f, 40f), 0f);

            }
        }

        // ================= Skulltula (En_St): haengt an der Decke, laesst sich fallen, wenn Link nahe ist, dreht abwechselnd
        // Gesicht (Panzer) und Bauch zu Link; nur Treffer auf den Bauch (von hinten) zaehlen, 2 Treffer. Beruehrung = halbes Herz.
        class Skulltula
        {
            public Vector3 Home; public bool Gone; public GameObject Root;
            readonly short initYaw; readonly float sc; readonly bool big;
            Vector3 pos; float velY, floorY, gravity; short ry, rx, rz; int health = 2;
            int rotAway, rotTowards, sway, swayAngle, takeSpin, gaveSpin, sfxT, deathT, finishT, bounces;
            enum A { Ceiling, Drop, Land, Ground, Return, Bounce, Finish, Die }
            A act;
            GameObject model; Animation anim; string clip = "idle"; float frame, spd = 1f; bool loop = true;
            OotCombat.Target target; Transform focus; Func<Vector3, float, bool> hitFn; float hitCool;
            readonly List<Material> mats = new List<Material>(); readonly List<Color> emBase = new List<Color>(); float flashT; Color flashC;

            public Skulltula(Vector3 home, short yaw, bool big) { Home = home; initYaw = yaw; this.big = big; sc = big ? 1.4f : 1f; }
            public string Info() { return Root == null ? "st -" : "st " + act + " hp" + health + " y" + pos.y.ToString("F0") + " boden" + floorY.ToString("F0") + " @" + (Root.transform.position + V(pos.x, pos.y, pos.z)).ToString("F1"); }

            float Off { get { return 32f * sc; } }
            public void Spawn(Transform parent)
            {
                Root = new GameObject("oot_skulltula"); Root.transform.SetParent(parent, false); Root.transform.position = Home;
                model = Model(stModel, Root.transform);
                anim = model != null ? model.GetComponentInChildren<Animation>() : null;
                if (anim != null) foreach (AnimationState st in anim) { st.enabled = false; st.weight = 1f; st.speed = 0f; }
                mats.Clear(); emBase.Clear();
                if (model != null) foreach (var r in model.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.materials) { mats.Add(m); emBase.Add(m.HasProperty("_EmissionColor") ? m.GetColor("_EmissionColor") : Color.black); }
                pos = Vector3.zero; velY = 0f; ry = initYaw; rx = 0; rz = 0; health = 2; act = A.Ceiling; rotAway = 60; gravity = 0f;
                floorY = FloorBelow();
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                float leash, range = OotCombat.RangeM(2, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 0.6f * sc, Enemy = true, Range = range, Leash = Mathf.Min(leash, 20f),
                                                Alive = () => Root != null && health > 0 };
                OotCombat.Add(target);
                hitFn = SwordHit; OotCombat.Hittables.Add(hitFn); RegisterShot();
                SetAnim("idle", 1f, true);
                if (-floorY <= Off + 1f) { pos.y = floorY + Off; act = A.Ground; }         // steht schon am Boden
            }
            public void Despawn()
            {
                if (shot != null) OotCombat.Shootables.Remove(shot); shot = null;
                if (target != null) OotCombat.Remove(target);
                if (hitFn != null) OotCombat.Hittables.Remove(hitFn);
                target = null; hitFn = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; model = null; anim = null;
            }
            float FloorBelow()
            {
                RaycastHit h;
                if (Physics.Raycast(Home + Vector3.up * 0.2f, Vector3.down, out h, 40f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore))
                    return (h.point.y - Home.y) / S;
                return -400f;
            }
            void SetAnim(string c, float s, bool lp) { clip = c; frame = 0f; spd = s; loop = lp; }
            float AnimLen(string c) { return anim != null && anim[c] != null ? Mathf.Max(1f, Mathf.Round(anim[c].clip.length * 30f)) : 20f; }
            void Sfx(string n, float vol = 0.9f) { OotSfx.Play(n, vol, Root != null ? Root.transform.position + V(pos.x, pos.y, pos.z) : (Vector3?)null, 0.03f); }

            bool CloseToPlayer(Vector3 pl)
            {
                if (takeSpin != 0) return false;
                if (new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude > 160f) return false;
                float yd = pos.y - pl.y;
                if (yd < 0f || yd > 400f) return false;
                return pl.y >= floorY - 10f;
            }

            public void Update(Player p)
            {
                if (Root == null) return;
                if (floorY <= -399f) { floorY = FloorBelow(); if (floorY > -399f && -floorY <= Off + 1f && act == A.Ceiling) { pos.y = floorY + Off; act = A.Ground; } }
                var pl = Local(p.transform.position, Home);
                short yawToPlayer = Atan2S(pl.x - pos.x, pl.z - pos.z);
                if (hitCool > 0f) hitCool -= 0.05f;
                if (flashT > 0f) flashT -= 0.05f;
                frame += spd * UPD; float len = AnimLen(clip); if (loop) { while (frame >= len) frame -= len; } else frame = Mathf.Min(frame, len);

                if (sway == 0 && act != A.Bounce && act != A.Finish && act != A.Die) pos.y += velY * UPD;
                switch (act)
                {
                    case A.Ceiling:
                        if (CloseToPlayer(pl)) { if (takeSpin == 0) SetAnim("land", 1f, false); velY = -10f; sfxT = 0; act = A.Drop; }
                        else Bob();
                        break;
                    case A.Drop:
                        if (!CloseToPlayer(pl)) { Sfx("NA_SE_EN_STALTU_UP"); SetAnim("idle", 4f, false); act = A.Return; }
                        else if (pos.y + velY * 2f - floorY <= Off) { pos.y = floorY + Off; SetAnim("land", 1f, false); sfxT = 0; act = A.Land; }
                        else if (sfxT-- <= 0) { Sfx("NA_SE_EN_STALTU_DOWN", 0.7f); sfxT = 3; }
                        break;
                    case A.Land:
                        if (++sfxT == 14) Sfx("NA_SE_EN_STALTU_DOWN_SET");
                        if (floorY + Off < pos.y) { sfxT = 0; act = A.Ground; SetAnim("idle", 1f, true); }
                        else velY = Mathf.Lerp(velY, 2f, 0.3f);
                        break;
                    case A.Ground:
                        if (takeSpin > 0 && --takeSpin == 0) SetAnim("idle", 1f, true);
                        if (!CloseToPlayer(pl)) { Sfx("NA_SE_EN_STALTU_UP"); SetAnim("idle", 4f, false); act = A.Return; break; }
                        if (sfxT-- <= 0) { Sfx("NA_SE_EN_STALTU_LAUGH", 0.8f); sfxT = 64; }
                        Bob();
                        break;
                    case A.Return:
                        if (CloseToPlayer(pl)) { velY = -10f; act = A.Drop; }
                        else if (pos.y + velY * 2f >= 0f) { pos.y = Mathf.Min(pos.y, 0f); velY = 0f; SetAnim("idle", 1f, true); act = A.Ceiling; }
                        else velY = 4f * Mathf.Clamp01(frame / Mathf.Max(1f, AnimLen("idle") - 1f));
                        break;
                    case A.Bounce:
                        velY = Mathf.Max(velY + gravity, -20f); pos.y += velY * UPD;
                        rx += 0x800; rz -= 0x800;
                        if (Bounced()) { gravity = -2f; deathT = 20; act = A.Finish; }
                        break;
                    case A.Finish:
                        velY = Mathf.Max(velY + gravity, -20f); pos.y += velY * UPD;
                        ScaledStep(ref rx, 0x3FFC); ScaledStep(ref rz, 0);
                        bounces = Mathf.Max(bounces, 1); Bounced();
                        if (--deathT <= 0) { finishT = 8; act = A.Die; }
                        break;
                    case A.Die:
                        if (--finishT <= 0)
                        {
                            var w = Root.transform.position + V(pos.x, pos.y, pos.z);
                            try { var fx = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("vfx_skeleton_death") : null; if (fx != null) UnityEngine.Object.Instantiate(fx, w, Quaternion.identity); } catch { }
                            float r = UnityEngine.Random.value;
                            if (r < 0.35f) OotItems.DropAt(w, "heart"); else if (r < 0.8f) OotItems.DropAt(w, "rupee");
                            Gone = true; Despawn(); return;
                        }
                        break;
                }
                // Drehen: am Boden abwechselnd 30 Bilder zu Link / weg (Bauch zeigt dann zu Link), vorher zittern
                if (sway > 0) Sway();
                else if (act != A.Bounce && act != A.Finish && act != A.Die) UpdateYaw(yawToPlayer);
                // Beruehrung: Beine (Zylinder r 20, h 60) gegen Link
                if (health > 0 && takeSpin == 0 && (act == A.Ground || act == A.Land || act == A.Drop))
                {
                    float dxz = new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude;
                    if (dxz < (20f * sc + 12f) && pl.y < pos.y + 10f * sc && pl.y + 50f > pos.y - 60f * sc)
                        if (OotCombat.Damage(0x08, Root.transform.position + V(pos.x, pos.y, pos.z))) { if (sway == 0) Sfx("NA_SE_EN_STALTU_ROLL"); gaveSpin = 30; OotSfx.Play("NA_SE_PL_BODY_HIT", 0.8f); }
                }
                if (gaveSpin > 0) gaveSpin--;
                if (takeSpin > 0 && health > 0) takeSpin--;          // EnSt_UpdateCylinders: zaehlt jedes Bild mit runter
            }

            int bobN;
            void Bob() { bobN++; float t = ((bobN / 8) & 1) == 0 ? 0.5f : -0.5f; velY = Mathf.Lerp(velY, t, 0.4f); }
            static void ScaledStep(ref short v, int target) { short d = (short)(target - v); v = (short)(v + Mathf.Clamp(d / 4, -0x2710, 0x2710)); }
            bool Bounced()
            {
                if (velY > 0f || bounces == 0 || pos.y > floorY + 2f) return false;
                pos.y = floorY + 2f;
                Sfx("NA_SE_EN_DODO_M_GND");
                velY = 6f / (4 - bounces); bounces--;
                if (bounces != 0) return false;
                velY = 0f; return true;
            }
            void UpdateYaw(short yawToPlayer)
            {
                if (takeSpin != 0 || gaveSpin != 0) { ry += 0x2000; return; }
                int yawDir = 0;
                if (act != A.Ground) { rotAway = 30; rotTowards = 0; }
                if (rotAway != 0) { if (--rotAway == 0) { Sfx("NA_SE_EN_STALTU_ROLL", 0.7f); rotTowards = 30; } }
                else if (rotTowards != 0) { if (--rotTowards == 0) { Sfx("NA_SE_EN_STALTU_ROLL", 0.7f); rotAway = 30; } yawDir = 0x8000; }
                short yt = (short)((act == A.Ground ? yawToPlayer : initYaw) ^ yawDir);
                short diff = (short)(ry - yt);
                if (Mathf.Abs(diff) <= 0x4000) SmoothStepToS(ref ry, yt, 4, 0x2000, 1); else ry += 0x2000;
                int timer = (yawDir == 0 && rotAway < 10) ? rotAway : (yawDir == 0x8000 && rotTowards < 10) ? rotTowards : -1;
                if (timer >= 0) ry += (short)((timer % 2) != 0 ? 0x800 : -0x800);
            }
            void Sway()
            {
                swayAngle += 0xA28; sway--;
                if (sway == 0) swayAngle = 0;
                float amt = sway * (7f / 15f);
                short ang = (short)(SinS(swayAngle) * (amt * (65536f / 360f)));
                rz = (short)(-ang * 2);
                // am Faden um den Aufhaengepunkt (200 Einheiten darueber) pendeln
                pos.x = SinS(ry + 0x4000) * SinS(ang) * -200f * 0.2f;
                pos.z = CosS(ry + 0x4000) * SinS(ang) * -200f * 0.2f;
                if (sway == 0) { pos.x = 0f; pos.z = 0f; }
            }

            OotCombat.Shootable shot;
            public void RegisterShot()
            {
                shot = new OotCombat.Shootable
                {
                    Test = (r, max) => Root == null || health <= 0 ? -1f : RaySphere(r, Root.transform.position + V(pos.x, pos.y, pos.z), 32f * sc * S * 0.8f, max),
                    Hit = (w, dir) =>
                    {
                        var face = Rot(0, ry, 0) * Vector3.forward; face.y = 0f; dir.y = 0f;
                        if (Vector3.Angle(face, -dir) < 90f) { sway = 60; OotSfx.Play("NA_SE_IT_SHIELD_REFLECT_SW", 0.8f, w); return; }
                        hitCool = 0f; var lp = Root.transform.position + V(pos.x, pos.y, pos.z) - dir.normalized * 1.0f - Vector3.up * 0.75f;
                        SwordHit(lp, Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg);
                    }
                };
                OotCombat.Shootables.Add(shot);
            }
            bool SwordHit(Vector3 lp, float lyaw)
            {
                if (Root == null || health <= 0 || hitCool > 0f) return false;
                var w = Root.transform.position + V(pos.x, pos.y, pos.z);
                var d = w - (lp + Vector3.up * 0.75f); float dy = d.y; d.y = 0f;
                var fw = Quaternion.Euler(0f, lyaw, 0f) * Vector3.forward;
                if (d.magnitude > 1.15f + 32f * sc * S || dy < -1.2f || dy > 1.6f * sc || (d.magnitude > 0.4f && Vector3.Angle(fw, d) > 80f)) return false;   // Koerper-Zylinder r 32 (x1,4) + Schwert
                hitCool = 0.4f;
                // Gesicht (vorn) zeigt zu Link -> Panzer: pendeln, kein Schaden
                var face = Rot(0, ry, 0) * Vector3.forward; face.y = 0f;
                if (Vector3.Angle(face, -d) < 90f)
                {
                    sway = 60; OotSfx.Play("NA_SE_IT_SHIELD_REFLECT_SW", 0.8f, w); return true;
                }
                sway = 0; gaveSpin = 1; takeSpin = (int)AnimLen("idle"); SetAnim("idle", 1f, true);
                flashT = 1f; flashC = new Color(1f, 0.25f, 0.2f); Flash();
                OotSfx.Play("NA_SE_IT_SWORD_STRIKE", 0.7f, w);
                if (--health > 0) { Sfx("NA_SE_EN_STALTU_DAMAGE"); return true; }
                Sfx("NA_SE_EN_STALWALL_DEAD");
                bounces = 3; deathT = 20; gravity = -1f; velY = 0f; act = A.Bounce;
                return true;
            }
            void Flash()
            {
                for (int i = 0; i < mats.Count; i++) { var m = mats[i]; if (m == null || !m.HasProperty("_EmissionColor")) continue; m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", emBase[i] + flashC * (0.8f * Mathf.Clamp01(flashT * 2f))); }
            }

            public void Draw()
            {
                if (Root == null || model == null) return;
                var w = Root.transform.position + V(pos.x, pos.y, pos.z);
                model.transform.position = w;
                model.transform.rotation = Rot(rx, ry, rz);
                model.transform.localScale = Vector3.one * (S * 0.04f * sc);
                if (anim != null && anim[clip] != null) { foreach (AnimationState st in anim) st.enabled = st.name == clip; var a = anim[clip]; a.weight = 1f; a.speed = 0f; a.time = frame / 30f; }
                if (flashT > 0f) Flash();
                else if (flashT > -0.1f) { flashT = -1f; for (int i = 0; i < mats.Count; i++) if (mats[i] != null && mats[i].HasProperty("_EmissionColor")) mats[i].SetColor("_EmissionColor", emBase[i]); }
                if (focus != null) focus.position = w + Vector3.up * (0.2f * sc);
            }
        }

        static List<string> TextPages(string id)
        {
            var l = new List<string>(); int guard = 0;
            while (!string.IsNullOrEmpty(id) && guard++ < 8) { object t; if (!enemyTexts.TryGetValue(id, out t)) break; foreach (var x in Json.L(t, "pages") ?? new List<object>()) l.Add(x.ToString()); id = Json.S(t, "next"); }
            return l;
        }
        static readonly int GroundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
        static float RaySphere(Ray r, Vector3 c, float rad, float max)
        {
            var oc = r.origin - c; float b = Vector3.Dot(oc, r.direction), cc = oc.sqrMagnitude - rad * rad, h = b * b - cc;
            if (h < 0f) return -1f; float t = -b - Mathf.Sqrt(h); return t >= 0f && t <= max ? t : -1f;
        }

        // ================= Deku-Nuss (En_Nutsball): fliegt geradeaus (10 Einheiten/Bild), Deku-Schild wirft sie zurueck =================
        class Nut
        {
            public bool Alive = true; GameObject g; Vector3 w; short yaw; int timer = 30; float vy; bool reflected; Scrub from;
            public Nut(Vector3 world, short yaw, Scrub from)
            {
                w = world; this.yaw = yaw; this.from = from;
                g = !string.IsNullOrEmpty(nutModel) ? Model(nutModel, holder.transform) : null;
                if (g == null) { g = GameObject.CreatePrimitive(PrimitiveType.Sphere); UnityEngine.Object.Destroy(g.GetComponent<Collider>()); g.transform.SetParent(holder.transform, true); g.GetComponent<Renderer>().material.color = new Color(0.45f, 0.3f, 0.15f); g.transform.localScale = Vector3.one * 0.25f; }
                else g.transform.localScale = Vector3.one * (S * 0.01f * 1f);
            }
            public void Kill() { if (g != null) UnityEngine.Object.Destroy(g); g = null; Alive = false; }
            void Burst() { OotSfx.Play("NA_SE_EN_OCTAROCK_ROCK", 0.8f, w); Alive = false; }
            public void Update(Player p)
            {
                if (--timer == 0) vy = 0f;
                if (timer <= 0) vy -= 1f;
                if (timer < -300) { Alive = false; return; }
                var step = new Vector3(SinS(yaw) * 10f * S, vy * S, -CosS(yaw) * 10f * S) * UPD;
                RaycastHit h;
                if (Physics.Raycast(w, step.normalized, out h, step.magnitude + 0.1f, GroundMask, QueryTriggerInteraction.Ignore)) { w = h.point; Burst(); return; }
                w += step;
                if (g != null) { g.transform.position = w; g.transform.rotation = Quaternion.Euler(timer * 60f, 0f, 0f); }
                // Link (Zylinder r 12 + Nuss r 13)
                var lp = p.transform.position;
                if (!reflected && new Vector2(w.x - lp.x, w.z - lp.z).magnitude < 25f * S && w.y > lp.y - 0.1f && w.y < lp.y + 1.6f)
                {
                    if (OotPlay.Shielding && OotCombat.ShieldFaces != null && OotCombat.ShieldFaces(w))
                    {
                        // zurueck in Links Blickrichtung (shieldRot + 0x8000 = Links Vorne)
                        yaw = (short)Mathf.RoundToInt(0x8000 - p.transform.eulerAngles.y * 65536f / 360f);
                        reflected = true; timer = 30; vy = 0f;
                        OotSfx.Play("NA_SE_IT_SHIELD_REFLECT_SW", 0.8f, w);
                        return;
                    }
                    OotCombat.Damage(0x08, w); Burst(); return;
                }
                if (reflected && from != null && from.HitBy(w)) { Burst(); return; }
            }
        }

        // ================= Deku-Schrubbe (En_Hintnuts): in der Blume, taucht auf und spuckt Nuesse; zurueckgeworfene Nuss
        // trifft -> rennt weg; einholen = Hinweis (Original-Text), dann verschwindet er. num 1-3 = Raetsel 2-3-1 vor dem Boss.
        static int puzzle;
        class Scrub
        {
            public Vector3 Home; public bool Gone; public GameObject Root;
            readonly short homeYaw; readonly int num; readonly string text, flag;
            Vector3 pos; short ry, runYaw; float speed; int timer, frozenT; bool exposed;
            enum A { Wait, Look, Stand, Throw, Burrow, BeginRun, Run, Talk, Leave, Freeze }
            A act; GameObject model, flower; Animation anim; string clip = "up"; float frame, spd; bool loop;
            OotCombat.Target target; Transform focus;

            public Scrub(Vector3 home, short yaw, int num, string text, string flag) { Home = home; homeYaw = yaw; this.num = num; this.text = text; this.flag = flag; }
            public string Info() { return Root == null ? "schrubbe -" : "schrubbe " + act + " t" + timer + " pos " + pos.ToString("F0") + (exposed ? " draussen" : ""); }
            public void Spawn(Transform parent)
            {
                if (!string.IsNullOrEmpty(flag) && Story.Flags.Contains(flag)) { Gone = true; return; }
                Root = new GameObject("oot_scrub"); Root.transform.SetParent(parent, false); Root.transform.position = Home;
                model = Model(scrubModel, Root.transform); flower = Model(flowerModel, Root.transform);
                anim = model != null ? model.GetComponentInChildren<Animation>() : null;
                if (anim != null) foreach (AnimationState st in anim) { st.enabled = false; st.weight = 1f; st.speed = 0f; }
                pos = Vector3.zero; ry = homeYaw; SetupWait();
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                float leash, range = OotCombat.RangeM(1, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 0.5f, Enemy = true, Range = range, Leash = leash, Alive = () => Root != null && exposed };
                OotCombat.Add(target);
                shot = new OotCombat.Shootable
                {
                    Test = (r, max) => Root == null || !exposed ? -1f : RaySphere(r, Root.transform.position + V(pos.x, pos.y + 20f, pos.z), 0.6f, max),
                    Hit = (w, dir) => HitBy(Root.transform.position + V(pos.x, pos.y + 20f, pos.z))
                };
                OotCombat.Shootables.Add(shot);
            }
            public void Despawn()
            {
                if (shot != null) OotCombat.Shootables.Remove(shot); shot = null;
                if (target != null) OotCombat.Remove(target); target = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; model = null; anim = null;
            }
            void Play(string c, float s, bool lp, float start = 0f) { clip = c; spd = s; loop = lp; frame = start; }
            float Len(string c) { return anim != null && anim[c] != null ? Mathf.Max(1f, Mathf.Round(anim[c].clip.length * 30f)) : 12f; }
            bool OnFrame(float f, float prev) { return spd > 0f && prev < f && frame >= f; }
            bool Done() { return !loop && frame >= Len(clip) - 0.01f; }
            void Sfx(string n) { OotSfx.Play(n, 0.9f, Root != null ? Root.transform.position + V(pos.x, pos.y, pos.z) : (Vector3?)null, 0.03f); }

            void SetupWait() { Play("up", 0f, false); timer = UnityEngine.Random.Range(100, 150); act = A.Wait; exposed = false; pos = Vector3.zero; }
            void SetupStand() { Play("stand", 1f, true); timer = act == A.Throw ? (2 | 0x1000) : 1; act = A.Stand; }
            void SetupBurrow() { Play("burrow", 1f, false); Sfx("NA_SE_EN_NUTS_DOWN"); act = A.Burrow; }

            OotCombat.Shootable shot;
            public bool HitBy(Vector3 w)
            {
                if (Root == null || !exposed || act == A.BeginRun || act == A.Run || act == A.Talk || act == A.Leave || act == A.Freeze) return false;
                var c = Root.transform.position + V(pos.x, pos.y, pos.z);
                if (new Vector2(w.x - c.x, w.z - c.z).magnitude > (18f + 13f) * S || w.y < c.y - 0.3f || w.y > c.y + 1.4f) return false;
                Play("unburrow", 1f, false); Sfx("NA_SE_EN_NUTS_DAMAGE");
                if (num >= 1 && num <= 3)
                {
                    if (puzzle == -4) puzzle = 0;
                    if (num == puzzle + 1) puzzle++; else { if (puzzle > 0) puzzle = -puzzle; puzzle--; }
                    act = A.Freeze; frozenT = 0;
                }
                else act = A.BeginRun;
                return true;
            }

            public void Update(Player p)
            {
                var pl = Local(p.transform.position, Home);
                float dxz = new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude, dy = pl.y - pos.y;
                short toPlayer = Atan2S(pl.x - pos.x, pl.z - pos.z);
                float prev = frame;
                frame += spd * UPD; float len = Len(clip);
                if (loop) { while (frame >= len) frame -= len; } else frame = Mathf.Min(frame, len);
                switch (act)
                {
                    case A.Wait:
                        if (spd < 0.5f && timer != 0) timer--;
                        if (OnFrame(8f, prev)) Sfx("NA_SE_EN_NUTS_UP");
                        exposed = frame >= 9f;
                        if (spd >= 0.5f && dxz < 120f) SetupBurrow();
                        else if (Done() && spd > 0f) { if (dxz < 120f) SetupBurrow(); else SetupStand(); }
                        if (spd < 0.5f && dxz > 160f && Mathf.Abs(dy) < 120f && (timer == 0 || dxz < 480f)) spd = 1f;
                        break;
                    case A.Stand:
                        if (frame < prev && timer != 0 && timer != 0x1000) timer--;
                        if ((timer & 0x1000) == 0) ApproachS(ref ry, toPlayer, 2, 0xE38);
                        if (dxz < 120f || timer == 0x1000) SetupBurrow();
                        else if (timer == 0) { Play("spit", 1f, false); act = A.Throw; }
                        break;
                    case A.Throw:
                        ApproachS(ref ry, toPlayer, 2, 0xE38);
                        if (dxz < 120f) SetupBurrow();
                        else if (Done()) SetupStand();
                        else if (OnFrame(6f, prev))
                        {
                            var np = Root.transform.position + V(pos.x + SinS(ry) * 23f, pos.y + 12f, pos.z + CosS(ry) * 23f);
                            nuts.Add(new Nut(np, ry, this)); Sfx("NA_SE_EN_NUTS_THROW");
                        }
                        break;
                    case A.Burrow:
                        if (OnFrame(4f, prev)) exposed = false;
                        pos.x = Mathf.MoveTowards(pos.x, 0f, 3f); pos.z = Mathf.MoveTowards(pos.z, 0f, 3f);
                        if (Done()) SetupWait();
                        break;
                    case A.BeginRun:
                        ApproachS(ref ry, toPlayer, 2, 0xE38);
                        if (Done()) { runYaw = (short)(toPlayer + 0x8000); Play("run", 1f, true); timer = 5; act = A.Run; exposed = true; }
                        break;
                    case A.Run:
                    {
                        if (frame < prev && timer != 0) timer--;
                        if (frame < prev || OnFrame(6f, prev)) Sfx("NA_SE_EN_NUTS_WALK");
                        speed = Mathf.MoveTowards(speed, 7.5f, 1f);
                        short homeYawTo = Atan2S(-pos.x, -pos.z);
                        if (SmoothStepToS(ref runYaw, runYaw, 1, 0xE38, 0xB6) == 0)
                        {
                            if (timer == 0)
                            {
                                short diff = (short)(homeYawTo - toPlayer);
                                runYaw = Mathf.Abs(diff) >= 0x2001 ? homeYawTo : (short)((diff >= 0 ? -8192f : 8192f) + toPlayer);
                            }
                            else runYaw = (short)(toPlayer + 0x8000);
                        }
                        var w0 = Root.transform.position + V(pos.x, pos.y, pos.z);
                        var dir = new Vector3(SinS(runYaw), 0f, -CosS(runYaw));
                        if (Physics.Raycast(w0 + Vector3.up * 0.3f, dir, 0.6f, GroundMask, QueryTriggerInteraction.Ignore)) runYaw = (short)(runYaw + 0x6000);
                        pos.x += SinS(runYaw) * speed * UPD; pos.z += CosS(runYaw) * speed * UPD;
                        RaycastHit h;
                        if (Physics.Raycast(Root.transform.position + V(pos.x, pos.y + 40f, pos.z), Vector3.down, out h, 4f, GroundMask, QueryTriggerInteraction.Ignore)) pos.y = (h.point.y - Home.y) / S;
                        ry = (short)(runYaw + 0x8000);
                        // eingeholt: Link steht dran oder hat ihn anvisiert und ist nahe -> sprechen
                        bool locked = OotCombat.Lock != null && OotCombat.Lock.T == focus;
                        if (dxz < 130f && (dxz < 40f || locked) && OotPlay.Calm)
                        {
                            speed = 0f; Play("talk", 1f, true); act = A.Talk;
                            var pages = TextPages(text);
                            OotPlay.StartDialog(pages, 0, k => { Play("run", 1f, true); timer = 30; act = A.Leave; Sfx("NA_SE_EN_NUTS_DAMAGE"); });
                        }
                        else if (timer == 0 && new Vector2(pos.x, pos.z).magnitude < 20f) { speed = 0f; SetupBurrow(); }
                        break;
                    }
                    case A.Talk:
                        SmoothStepToS(ref ry, toPlayer, 3, 0x400, 0x100);
                        break;
                    case A.Leave:
                        if (frame < prev || OnFrame(6f, prev)) Sfx("NA_SE_EN_NUTS_WALK");
                        pos.x += SinS(ry) * 7.5f * UPD; pos.z += CosS(ry) * 7.5f * UPD;
                        if (--timer <= 0)
                        {
                            if (!string.IsNullOrEmpty(flag)) { Story.Flags.Add(flag); Story.Save(); }
                            try { var fx = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("vfx_Place_wood_pole") : null; if (fx != null) UnityEngine.Object.Instantiate(fx, Root.transform.position + V(pos.x, pos.y, pos.z), Quaternion.identity); } catch { }
                            Gone = true; Despawn(); return;
                        }
                        break;
                    case A.Freeze:
                        if (frame < prev) Sfx("NA_SE_EN_NUTS_FAINT");
                        if (Done() && clip == "unburrow") Play("freeze", 1f, true);
                        if (frozenT == 0) { if (puzzle == 3) frozenT = 1; else if (puzzle == -4) frozenT = 2; }
                        else
                        {
                            pos.y = Mathf.MoveTowards(pos.y, -35f, 7f);
                            if (pos.y <= -35f)
                            {
                                if (frozenT == 1) { if (num == 3) { if (!string.IsNullOrEmpty(flag)) Story.Flags.Add(flag); Story.Flags.Add("oot_ydan_scrubs_231"); Story.Save(); OotPlay.StartDialog(TextPages(text), 0, null); } Gone = true; Despawn(); return; }
                                pos.y = 0f; SetupWait();
                            }
                        }
                        break;
                }
            }
            public void Draw()
            {
                if (Root == null) return;
                if (flower != null) { flower.transform.position = Home; flower.transform.rotation = Rot(0, homeYaw, 0); flower.transform.localScale = Vector3.one * (S * 0.01f); }
                if (model == null) return;
                model.transform.position = Root.transform.position + V(pos.x, pos.y, pos.z);
                model.transform.rotation = Rot(0, ry, 0);
                model.transform.localScale = Vector3.one * (S * 0.01f);
                if (anim != null && anim[clip] != null) { foreach (AnimationState st in anim) st.enabled = st.name == clip; var a = anim[clip]; a.weight = 1f; a.speed = 0f; a.time = frame / 30f; }
                if (focus != null) focus.position = model.transform.position + Vector3.up * 0.5f;
            }
        }



        // ================= Gohma-Larve (En_Goma, params 8 = Ei an der Decke): Ei faellt, wenn Link 10 Bilder lang darunter steht
        // (|dx|,|dz| < 100), schluepft beim Aufprall, steht 10-40 Bilder, rennt auf Link zu (10/3 Einheiten/Bild), ab 150 Einheiten
        // Anlauf (30 Bilder) und Kopfsprung (vy 8, Tempo 10). Beruehrung = halbes Herz, Schild stoesst sie weg, 2 Treffer.
        class Goma
        {
            public Vector3 Home; public bool Gone; public GameObject Root;
            Vector3 pos; float velY, speed, scale = 0.01f, eggScale = 1f, eggYOff = -1500f, eggSquishAngle, eggSquishAmount, eggPitch, floorY;
            short worldYaw, shapeYaw; int actionTimer, invinc, hurtTimer, health = 2, detect, eggTimer, hatchState;
            Vector3 knock;
            enum A { Egg, EggFall, Hatch, Stand, Chase, Prep, Jump, Land, Hurt, Flee, Die, Dead }
            A act; bool isEgg = true;
            GameObject model, egg; Animation anim; string clip = "idle"; float frame, spd = 1f; bool loop = true;
            OotCombat.Target target; Transform focus; Func<Vector3, float, bool> hitFn; OotCombat.Shootable shot;

            public Goma(Vector3 home) { Home = home; }
            public string Info() { return Root == null ? "goma -" : "goma " + act + " hp" + health + " pos " + pos.ToString("F0") + " t" + actionTimer; }
            public void Spawn(Transform parent)
            {
                Root = new GameObject("oot_goma"); Root.transform.SetParent(parent, false); Root.transform.position = Home;
                model = Model(gomaModel, Root.transform); egg = Model(gomaEggModel, Root.transform);
                anim = model != null ? model.GetComponentInChildren<Animation>() : null;
                if (anim != null) foreach (AnimationState st in anim) { st.enabled = false; st.weight = 1f; st.speed = 0f; }
                pos = Vector3.zero; velY = 0f; speed = 0f; isEgg = true; act = A.Egg; health = 2; scale = 0.01f; eggScale = 1f; eggYOff = -1500f;
                eggTimer = UnityEngine.Random.Range(0, 200); eggSquishAngle = UnityEngine.Random.value * 1000f; hatchState = 0; detect = 0;
                if (model != null) model.SetActive(false);
                RaycastHit h;
                floorY = Physics.Raycast(Home + Vector3.down * 0.5f, Vector3.down, out h, 40f, GroundMask, QueryTriggerInteraction.Ignore) ? (h.point.y - Home.y) / S : -440f;
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                float leash, range = OotCombat.RangeM(3, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 0.35f, Enemy = true, Range = range, Leash = leash, Alive = () => Root != null && health > 0 && act != A.Egg };
                OotCombat.Add(target);
                hitFn = SwordHit; OotCombat.Hittables.Add(hitFn);
                shot = new OotCombat.Shootable { Test = (r, max) => health <= 0 ? -1f : RaySphere(r, Center(), isEgg ? 0.5f : 0.45f, max), Hit = (w, d) => TakeHit(1) };
                OotCombat.Shootables.Add(shot);
            }
            public void Despawn()
            {
                if (shot != null) OotCombat.Shootables.Remove(shot); shot = null;
                if (hitFn != null) OotCombat.Hittables.Remove(hitFn); hitFn = null;
                if (target != null) OotCombat.Remove(target); target = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; model = null; egg = null; anim = null;
            }
            Vector3 World() { return Root.transform.position + V(pos.x, pos.y, pos.z); }
            Vector3 Center() { return World() + Vector3.up * (isEgg ? (eggYOff * 0.01f + 10f) * S : 20f * S); }
            void Play(string c, float s, bool lp) { clip = c; spd = s; loop = lp; frame = 0f; }
            float Len(string c) { return anim != null && anim[c] != null ? Mathf.Max(1f, Mathf.Round(anim[c].clip.length * 30f)) : 10f; }
            bool OnFrame(float f, float prev) { return prev < f && frame >= f; }
            void Sfx(string n) { OotSfx.Play(n, 0.9f, Root != null ? World() : (Vector3?)null, 0.03f); }

            bool SwordHit(Vector3 lp, float lyaw)
            {
                if (Root == null || health <= 0 || hurtTimer > 0 || act == A.Egg) return false;
                var d = Center() - (lp + Vector3.up * 0.6f); float dy = d.y; d.y = 0f;
                var fw = Quaternion.Euler(0f, lyaw, 0f) * Vector3.forward;
                // Schwertreichweite: Zylinder 35 x 35 Einheiten, waehrend Link zuschlaegt (EnGoma_Update)
                if (d.magnitude > 1.75f || dy < -1.0f || dy > 1.3f || (d.magnitude > 0.4f && Vector3.Angle(fw, d) > 80f)) return false;
                TakeHit(1); return true;
            }
            void TakeHit(int dmg)
            {
                if (Root == null || health <= 0 || hurtTimer > 0) return;
                if (isEgg)
                {
                    // noch ein Ei: zerplatzt sofort (EnGoma_UpdateHit, gomaType != NORMAL)
                    if (act == A.Egg) return;
                    Sfx("NA_SE_EN_GOMA_EGG1"); health = 0; Gone = true; Despawn(); return;
                }
                OotSfx.Play("NA_SE_IT_SWORD_STRIKE", 0.5f, Center());
                health -= dmg; hurtTimer = 13;
                Play("hurt", 1f, false); act = A.Hurt; actionTimer = health <= 0 ? 5 : 10;
                speed = 20f; var lp = Local(Player.m_localPlayer.transform.position, Home);
                worldYaw = (short)(Atan2S(lp.x - pos.x, lp.z - pos.z) + 0x8000);
                Sfx("NA_SE_EN_GOMA_JR_DAM1");
            }
            void SetupStand() { Play("idle", 1f, true); actionTimer = 10 + UnityEngine.Random.Range(0, 30); act = A.Stand; }

            public void Update(Player p)
            {
                if (Root == null) return;
                if (actionTimer > 0) actionTimer--;
                if (invinc > 0) invinc--;
                if (hurtTimer > 0) hurtTimer--;
                eggTimer++;
                var lpw = p.transform.position;
                var pl = Local(lpw, Home);
                short toPlayer = Atan2S(pl.x - pos.x, pl.z - pos.z);
                float dxz = new Vector2(pl.x - pos.x, pl.z - pos.z).magnitude;
                float prev = frame;
                frame += spd * UPD; float len = Len(clip);
                if (loop) { while (frame >= len) frame -= len; } else frame = Mathf.Min(frame, len);
                bool ground = pos.y <= floorY + 0.5f;
                switch (act)
                {
                    case A.Egg:
                        eggSquishAngle += 1f; eggSquishAmount = Mathf.MoveTowards(eggSquishAmount, 0.1f, 0.005f);
                        if (Mathf.Abs(pl.x - pos.x) < 100f && Mathf.Abs(pl.z - pos.z) < 100f) { if (++detect > 9) act = A.EggFall; }
                        else detect = 0;
                        break;
                    case A.EggFall:
                        eggSquishAngle += 1f; eggSquishAmount = Mathf.MoveTowards(eggSquishAmount, 0f, 0.005f);
                        eggYOff = Mathf.MoveTowards(eggYOff, 1500f, 150f);
                        velY = Mathf.Max(velY - 1.3f * UPD, -20f);
                        if (ground && hatchState == 0)
                        {
                            Sfx("NA_SE_EN_GOMA_EGG1");
                            // schluepfen (EnGoma_SetupHatch): Larve halb so gross, Kopfsprung-Animation, schaut zu Link
                            isEgg = false; scale = 0.005f; Play("jump", 1f, false); act = A.Hatch; actionTimer = 5;
                            shapeYaw = worldYaw = toPlayer; speed = 0f; hatchState = 1;
                            if (egg != null) egg.SetActive(false); if (model != null) model.SetActive(true);
                            OotSfx.Play("NA_SE_EV_PLANT_BROKEN", 0.6f, World());
                        }
                        break;
                    case A.Hatch:
                        if (actionTimer == 0) SetupStand();
                        break;
                    case A.Stand:
                        speed = Mathf.MoveTowards(speed, 0f, 2f);
                        ApproachS(ref shapeYaw, toPlayer, 2, 3000);
                        if (actionTimer == 0) { Play("run", 1f, true); act = A.Chase; actionTimer = 70 + UnityEngine.Random.Range(0, 40); }
                        break;
                    case A.Chase:
                        if (OnFrame(1f, prev) || OnFrame(5f, prev)) Sfx("NA_SE_EN_GOMA_JR_WALK");
                        speed = Mathf.MoveTowards(speed, 10f / 3f, 2f);
                        ApproachS(ref worldYaw, toPlayer, 3, 2000); ApproachS(ref shapeYaw, worldYaw, 2, 3000);
                        if (dxz <= 150f) { Play("prep", 1f, false); act = A.Prep; actionTimer = 30; }
                        break;
                    case A.Prep:
                        speed = Mathf.MoveTowards(speed, 0f, 2f);
                        ApproachS(ref worldYaw, toPlayer, 2, 4000); ApproachS(ref shapeYaw, toPlayer, 2, 3000);
                        if (actionTimer == 0) { Play("jump", 1f, false); act = A.Jump; velY = 8f; Sfx("NA_SE_EN_GOMA_JR_CRY"); }
                        break;
                    case A.Jump:
                        speed = Mathf.MoveTowards(speed, 10f, 5f);
                        if (velY <= 0f && ground) { Play("land", 1f, false); act = A.Land; actionTimer = 10; Sfx("NA_SE_EN_GOMA_JR_LAND2"); }
                        break;
                    case A.Land:
                        if (ground) speed = Mathf.MoveTowards(speed, 0f, 2f);
                        if (actionTimer == 0) SetupStand();
                        break;
                    case A.Hurt:
                        if (ground) speed = Mathf.MoveTowards(speed, 0f, 2f);
                        if (actionTimer == 0)
                        {
                            if (health <= 0) { Play("die", 1f, false); act = A.Die; actionTimer = 30; Sfx("NA_SE_EN_GOMA_JR_DEAD"); }
                            else { Play("run", 2f, true); act = A.Flee; actionTimer = 20; Sfx("NA_SE_EN_GOMA_JR_DAM2"); }
                        }
                        break;
                    case A.Flee:
                        speed = Mathf.MoveTowards(speed, 20f / 3f, 2f);
                        ApproachS(ref worldYaw, (short)(toPlayer + 0x8000), 3, 2000); ApproachS(ref shapeYaw, worldYaw, 2, 3000);
                        if (actionTimer == 0) SetupStand();
                        break;
                    case A.Die:
                        if (ground) speed = Mathf.MoveTowards(speed, 0f, 2f);
                        if (actionTimer == 17) Sfx("NA_SE_EN_GOMA_JR_LAND");
                        if (actionTimer == 0) { Play("dead", 1f, true); act = A.Dead; actionTimer = 3; sy0 = 1f; }
                        break;
                    case A.Dead:
                        speed = Mathf.MoveTowards(speed, 0f, 2f);
                        if (actionTimer == 0)
                        {
                            sy0 = Mathf.MoveTowards(sy0, 0f, 0.2f);        // Math_SmoothStepToF(scale.y, 0, 0.5, 0.00225): in ~5 Bildern platt
                            if (sy0 <= 0.001f)
                            {
                                var w = World(); Sfx("NA_SE_EN_EXTINCT");
                                try { var fx = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("vfx_skeleton_death") : null; if (fx != null) UnityEngine.Object.Instantiate(fx, w, Quaternion.identity); } catch { }
                                float r = UnityEngine.Random.value;
                                if (r < 0.35f) OotItems.DropAt(w, "heart"); else if (r < 0.7f) OotItems.DropAt(w, "rupee");
                                Gone = true; Despawn(); return;
                            }
                        }
                        break;
                }
                if (!isEgg) scale = Mathf.MoveTowards(scale, 0.01f, 0.00075f);
                // Bewegung (Actor_MoveXZGravity): Tempo entlang worldYaw, Schild-Rueckstoss, Schwerkraft auf den Boden
                if (act != A.Egg)
                {
                    var mv = new Vector3(SinS(worldYaw) * speed + knock.x, 0f, CosS(worldYaw) * speed + knock.z) * UPD;
                    knock.x = Mathf.MoveTowards(knock.x, 0f, 3f); knock.z = Mathf.MoveTowards(knock.z, 0f, 3f);
                    if (mv.sqrMagnitude > 1e-4f)
                    {
                        var w0 = World() + Vector3.up * 0.4f; var dirW = new Vector3(mv.x, 0f, -mv.z);
                        if (!Physics.Raycast(w0, dirW.normalized, dirW.magnitude * S + 0.35f, GroundMask, QueryTriggerInteraction.Ignore)) { pos.x += mv.x; pos.z += mv.z; }
                    }
                    if (act != A.EggFall) velY = Mathf.Max(velY - 1.3f * UPD, -20f);        // Schwerkraft bleibt nach dem Ei bei -1,3
                    pos.y += velY * UPD;
                    RaycastHit h;
                    if (Physics.Raycast(World() + Vector3.up * 0.6f, Vector3.down, out h, 8f, GroundMask, QueryTriggerInteraction.Ignore)) floorY = (h.point.y - Home.y) / S;
                    if (pos.y <= floorY) { pos.y = floorY; if (velY < 0f) velY = 0f; }
                }
                // Beruehrung (colliderCylinder1: r 15, h 30, yShift 10; Schaden 0x08): Link verletzt oder Schild stoesst zurueck
                if (!isEgg && health > 0 && invinc == 0 && act != A.Die && act != A.Dead && act != A.Hurt)
                {
                    var c = World(); var l = lpw;
                    float dd = new Vector2(c.x - l.x, c.z - l.z).magnitude;
                    if (dd < (15f + 12f) * S && l.y < c.y + 40f * S && l.y + 1.4f > c.y + 10f * S)
                    {
                        bool blocked = OotPlay.Shielding && OotCombat.ShieldFaces != null && OotCombat.ShieldFaces(c);
                        if (blocked)
                        {
                            OotSfx.Play("NA_SE_IT_SHIELD_BOUND", 0.8f, c);
                            if (act == A.Jump) { Play("land", 1f, false); act = A.Land; actionTimer = 10; velY = 0f; speed = -5f; }
                            else { float ly = p.transform.eulerAngles.y * Mathf.Deg2Rad; knock = new Vector3(Mathf.Sin(ly) * 20f, 0f, -Mathf.Cos(ly) * 20f); invinc = 5; }
                        }
                        else if (OotCombat.Damage(0x08, c))
                        {
                            if (act == A.Jump) { Play("land", 1f, false); act = A.Land; actionTimer = 10; speed = 0f; velY = 0f; }
                        }
                    }
                }
            }
            float sy0;

            public void Draw()
            {
                if (Root == null) return;
                var w = World();
                if (isEgg && egg != null)
                {
                    // EnGoma_Draw (Ei): pulsierendes Quetschen, haengt 15 Einheiten unter der Decke, faellt
                    egg.transform.position = w + Vector3.up * (eggYOff * 0.01f * S);
                    float sq = eggSquishAngle;
                    egg.transform.rotation = Quaternion.Euler(eggPitch * Mathf.Rad2Deg, sq * 0.15f * Mathf.Rad2Deg, sq * 0.1f * Mathf.Rad2Deg);
                    float k = S * 0.01f;
                    egg.transform.localScale = new Vector3(k * eggScale * (0.95f - eggSquishAmount), k / eggScale * (1.05f + eggSquishAmount), k * eggScale * (0.95f - eggSquishAmount));
                }
                if (!isEgg && model != null)
                {
                    model.transform.position = w;
                    model.transform.rotation = Rot(0, shapeYaw, 0);
                    float sy = act == A.Dead ? sy0 : 1f;
                    model.transform.localScale = new Vector3(S * scale, S * scale * sy, S * scale);
                    if (anim != null && anim[clip] != null) { foreach (AnimationState st in anim) st.enabled = st.name == clip; var a = anim[clip]; a.weight = 1f; a.speed = 0f; a.time = frame / 30f; }
                }
                if (focus != null) focus.position = Center();
            }
        }

        // ================= Rollender Felsbrocken (En_Goroiwa): rollt den Pfad ab (Kokiri-Wald: Schwert-Labyrinth, 9,2 Einheiten/Bild),
        // trifft er Link, wird Link umgeworfen (Viertelherz), der Fels huepft kurz zurueck (MoveAndFallToGround) und rollt weiter.
        // params: Pfad, Schleifenart (Bits 8-9), Boden-Modus (Bit 10), rot.z & 1 (kurze Pause, 50 Bilder ohne Stoss nach Treffer).
        class Boulder
        {
            public Vector3 Home; public GameObject Root;
            readonly List<Vector3> pts = new List<Vector3>();       // OoT-Einheiten relativ zu Home
            readonly string model; readonly int loopMode, rotZ; readonly bool fall; readonly float maxSpeed;
            Vector3 pos, prevPos, vel; float speed, rollRot = 1f, prevRollDiff, floorY; short rotY;
            int cur, next, dir, endW, wait, noCol, bounce; int state; bool inWay, ground;     // state 0 rollen, 1 zurueckhuepfen, 2 warten
            Quaternion roll = Quaternion.identity; Vector3 lastAxis = Vector3.right;
            AudioSource rollSnd; float sndVol;
            GameObject mdl;

            public Boulder(List<Vector3> world, string model, int loopMode, bool fall, int rotZ, float speed)
            {
                Home = world[0]; this.model = model; this.loopMode = loopMode; this.fall = fall; this.rotZ = rotZ; maxSpeed = speed;
                foreach (var w in world) pts.Add(Local(w, Home));
                Reset();
            }

            void Reset()
            {
                endW = pts.Count - 1; cur = 0; next = 1; dir = 1;
                pos = pts[0]; prevPos = pos; vel = Vector3.zero; speed = 0f; state = 0; rollRot = 1f; wait = 0; noCol = 0;
                floorY = pos.y; Face();
            }

            void Face() { var n = pts[next]; rotY = Atan2S(n.x - pos.x, n.z - pos.z); }

            public void Spawn(Transform parent)
            {
                Root = new GameObject("oot_goroiwa"); Root.transform.SetParent(parent, false);
                mdl = Model(model, Root.transform);
                if (mdl != null) { mdl.transform.localPosition = Vector3.up * 59.5f * S; }
                var clip = OotSfx.Clip("NA_SE_EV_BIGBALL_ROLL");
                if (clip != null)
                {
                    rollSnd = Root.AddComponent<AudioSource>(); rollSnd.clip = clip; rollSnd.loop = true; rollSnd.spatialBlend = 1f;
                    rollSnd.rolloffMode = AudioRolloffMode.Linear; rollSnd.minDistance = 3f; rollSnd.maxDistance = 28f; rollSnd.volume = 0f; rollSnd.dopplerLevel = 0f;
                    rollSnd.Play();
                }
                Reset(); Draw();
                try { ClearPath(); } catch (Exception e) { Plugin.Quiet(e, "Fels.ClearPath"); }
            }

            public void Despawn()
            {
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; mdl = null; rollSnd = null;
            }

            void NextWaypoint()
            {
                cur = next; next += dir;
                if (next < 0)
                {
                    if (loopMode == 0 || loopMode == 1) { cur = endW; next = endW - 1; dir = -1; }
                    else { cur = 0; next = 1; dir = 1; }
                }
                else if (next > endW)
                {
                    if (loopMode == 0 || loopMode == 1) { cur = 0; next = 1; dir = 1; }
                    else { cur = endW; next = endW - 1; dir = -1; }
                }
                if ((loopMode == 0 || loopMode == 1) && (cur == 0 || cur == endW)) pos = pts[cur];
                Face();
            }

            void Reverse() { dir = -dir; cur = next; next += dir; if (next < 0 || next > endW) { next = Mathf.Clamp(next, 0, endW); } Face(); }

            // Boden nahe der Pfadhoehe (Valheim-Deko darueber/darunter zaehlt nicht)
            float FloorAt(Vector3 lp)
            {
                float py = Mathf.Lerp(pts[cur].y, pts[next].y, 0.5f);
                var w = Home + V(lp.x, py + 20f, lp.z);
                float best = py; bool any = false;
                foreach (var h in Physics.RaycastAll(w, Vector3.down, 60f * S, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    float y = (h.point.y - Home.y) / S;
                    if (y > py - 30f && y < py + 20f && (!any || y > best)) { best = y; any = true; }
                }
                return best;
            }
            // Valheim-Deko (Buesche, Baeume, Steine) auf dem Pfad weg: der Fels rollt im Original durch freie Gaenge
            void ClearPath()
            {
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var a = Home + V(pts[i].x, pts[i].y + 60f, pts[i].z); var b = Home + V(pts[i + 1].x, pts[i + 1].y + 60f, pts[i + 1].z);
                    int n = Mathf.Max(2, (int)((b - a).magnitude / 0.8f));
                    for (int k = 0; k <= n; k++)
                        foreach (var c in Physics.OverlapSphere(Vector3.Lerp(a, b, k / (float)n), 66f * S))
                        {
                            var t = c.transform;
                            while (t.parent != null && t.parent.name != "gi_foliage") t = t.parent;
                            if (t.parent != null) t.gameObject.SetActive(false);
                        }
                }
            }

            // EnGoroiwa_MoveAndFall (Bit 10: mit Schwerkraft und Bodenpruefung)
            bool MoveAndFall()
            {
                StepToF(ref speed, maxSpeed, 0.3f);
                vel.x = SinS(rotY) * speed; vel.z = CosS(rotY) * speed;
                vel.y = Mathf.Max(vel.y - 0.86f, -15f);
                var n = pts[next];
                bool r = true;
                r &= StepToF(ref pos.x, n.x, Mathf.Abs(vel.x));
                r &= StepToF(ref pos.z, n.z, Mathf.Abs(vel.z));
                pos.y += vel.y;
                return r;
            }

            // EnGoroiwa_Move (ohne Schwerkraft, genau den Pfad entlang)
            bool Move()
            {
                StepToF(ref speed, maxSpeed, 0.3f);
                var n = pts[next]; var c = pts[cur];
                var d = (n - pos).sqrMagnitude < 25f ? n - pos : n - c;
                vel = d.sqrMagnitude > 1e-6f ? d.normalized * speed : Vector3.zero;
                bool r = true;
                r &= StepToF(ref pos.x, n.x, Mathf.Abs(vel.x));
                r &= StepToF(ref pos.y, n.y, Mathf.Abs(vel.y));
                r &= StepToF(ref pos.z, n.z, Mathf.Abs(vel.z));
                return r;
            }

            public void Update(Player p)
            {
                if (noCol > 0) noCol--;
                prevPos = pos;
                var lp = Local(p.transform.position, Home);
                var c = pos + Vector3.up * 59.5f;
                float xz = new Vector2(lp.x - pos.x, lp.z - pos.z).magnitude;
                // Treffer (AT): Kugel r=58 um die Felsmitte gegen Links Koerper (Zylinder r=12, 0..50)
                bool hit = false;
                if (state == 0 && noCol <= 0 && xz < 300f)
                {
                    float cy = Mathf.Clamp(c.y, lp.y, lp.y + 50f);
                    var q = new Vector3(lp.x, cy, lp.z) - c; q.y *= 1f;
                    float hd = new Vector2(q.x, q.z).magnitude - 12f; float dy = q.y;
                    hit = Mathf.Sqrt(Mathf.Max(0f, hd) * Mathf.Max(0f, hd) + dy * dy) < 58f;
                }
                if (hit)
                {
                    short toPl = Atan2S(lp.x - pos.x, lp.z - pos.z);
                    short diff = (short)(toPl - rotY);
                    inWay = diff > -0x4000 && diff < 0x4000;
                    if (inWay && (fall || (rotZ & 1) != 1)) Reverse();
                    var wc = Home + V(c.x, c.y, c.z);
                    OotCombat.Damage(0x04, wc, true);
                    OotSfx.Play("NA_SE_PL_BODY_HIT", 0.9f);
                    if (fall) { state = 1; speed *= 0.15f; vel.y = 5f; rollRot = 1f; }
                    else { state = 2; speed = 0f; wait = (rotZ & 1) == 1 ? 6 : 20; rollRot = 0f; }
                    if ((rotZ & 1) == 1) noCol = 50;
                }
                else if (state == 0)
                {
                    if (fall ? MoveAndFall() : Move())
                    {
                        NextWaypoint();
                        if (loopMode == 3 && (cur == 0 || cur == endW)) { state = 2; speed = 0f; wait = (rotZ & 1) == 1 ? 6 : 20; rollRot = 0f; }
                    }
                }
                else if (state == 1)
                {
                    MoveAndFall();
                    if (ground && vel.y < 0f)
                    {
                        if (inWay && (rotZ & 1) == 1) Reverse();
                        state = 2; speed = 0f; wait = (rotZ & 1) == 1 ? 6 : 20; rollRot = 0f;
                    }
                }
                else if (state == 2)
                {
                    if (wait > 0) wait--; else { state = 0; rollRot = 1f; }
                }
                // Boden (Actor_UpdateBgCheckInfo)
                ground = false;
                if (fall)
                {
                    floorY = FloorAt(pos);
                    if (pos.y <= floorY) { pos.y = floorY; ground = true; }
                }
                // Rollen (EnGoroiwa_UpdateRotation): Winkel = Weg / 59,5, Achse = oben x Bewegung
                prevRollDiff = (pos - prevPos).magnitude / 59.5f;
                float ang = prevRollDiff * rollRot;
                var wv = new Vector3(vel.x, 0f, -vel.z);
                var ax = Vector3.Cross(Vector3.up, wv);
                if (ax.sqrMagnitude > 1e-6f) lastAxis = ax.normalized;
                if (ang > 0f) roll = Quaternion.AngleAxis(ang * Mathf.Rad2Deg, lastAxis) * roll;
                // Wegschieben (OC): Link steckt nicht im Fels
                if (noCol <= 0 && xz < 70f && lp.y < c.y + 58f && lp.y + 50f > c.y - 58f && xz > 0.01f)
                {
                    var push = new Vector2(lp.x - pos.x, lp.z - pos.z).normalized * (70f - xz);
                    var body = p.GetComponent<Rigidbody>();
                    var np = p.transform.position + new Vector3(push.x * S, 0f, -push.y * S);
                    p.transform.position = np; if (body != null) body.position = np;
                }
                sndVol = Mathf.MoveTowards(sndVol, state == 0 ? 1f : 0f, 0.25f);
            }

            public void Draw()
            {
                if (Root == null) return;
                Root.transform.position = Home + V(pos.x, pos.y, pos.z);
                if (mdl != null) mdl.transform.rotation = roll;
                if (rollSnd != null)
                {
                    float sv = 1f; try { sv = Mathf.Clamp01(PlayerPrefs.GetFloat("SfxVolume", 1f)); } catch { }
                    rollSnd.volume = sndVol * 0.8f * sv;
                }
            }

            public string Info() { return "fels " + state + " wp " + cur + "->" + next + " pos " + pos.ToString("F0") + " v " + speed.ToString("F1") + " boden " + ground; }
        }

        // ================= Wand-Skulltula (En_Sw): klebt an Ranken/Waenden; braun = krabbelt auf Link zu und beisst,
        // gold = bleibt sitzen und laesst ein Skulltula-Symbol fallen (Get-Item-Text mit Zaehler). Schwert oder Samen toeten.
        class WallSt
        {
            public Vector3 Home; public bool Gone; public GameObject Root;
            readonly short yaw0; readonly bool gold; readonly string flag;
            Vector3 n = Vector3.forward, w, w0, n0; bool wallOk; int wallTries; GameObject model, token; Animation anim; float frame, bob, hitCool, dieT = -1f, tokenT, wallRetry;
            OotCombat.Target target; Transform focus; Func<Vector3, float, bool> hitFn; OotCombat.Shootable shot; int health = 1;
            public WallSt(Vector3 home, short yaw, bool gold, string flag) { Home = home; yaw0 = yaw; this.gold = gold; this.flag = flag; }
            public void Spawn(Transform parent)
            {
                if (gold && !string.IsNullOrEmpty(flag) && Story.Flags.Contains(flag)) { Gone = true; return; }
                Root = new GameObject(gold ? "oot_goldskulltula" : "oot_skullwalltula"); Root.transform.SetParent(parent, false); Root.transform.position = Home;
                w = Home; FindWall();
                model = Model(stModel, Root.transform);
                anim = model != null ? model.GetComponentInChildren<Animation>() : null;
                if (anim != null) foreach (AnimationState st in anim) { st.enabled = false; st.weight = 1f; st.speed = 0f; }
                if (model != null && gold)
                    foreach (var r in model.GetComponentsInChildren<Renderer>(true)) foreach (var m in r.materials)
                    {
                        if (m.HasProperty("_Color")) m.color = new Color(1f, 0.78f, 0.25f);
                        if (m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(0.35f, 0.25f, 0.03f)); }
                    }
                focus = new GameObject("focus").transform; focus.SetParent(Root.transform, false);
                float leash, range = OotCombat.RangeM(2, out leash);
                target = new OotCombat.Target { T = focus, Focus = 0f, Arrow = 0.4f, Enemy = true, Range = range, Leash = leash, Alive = () => Root != null && health > 0 };
                OotCombat.Add(target);
                hitFn = SwordHit; OotCombat.Hittables.Add(hitFn);
                shot = new OotCombat.Shootable { Test = (r, max) => health <= 0 ? -1f : RaySphere(r, w, 0.45f, max), Hit = (hp, dir) => Die() };
                OotCombat.Shootables.Add(shot);
            }
            public void Despawn()
            {
                if (shot != null) OotCombat.Shootables.Remove(shot); shot = null;
                if (hitFn != null) OotCombat.Hittables.Remove(hitFn); hitFn = null;
                if (target != null) OotCombat.Remove(target); target = null;
                if (Root != null) UnityEngine.Object.Destroy(Root);
                Root = null; model = null; anim = null; token = null;
            }
            // Nur echte Waende (Normale fast waagerecht): vorher traf der Strahl manchmal Boden/Deko -> Spinne stand wie auf dem
            // Boden halb in der Wand (Moritz 09.11.). Ohne Treffer (Modelle noch nicht geladen) spaeter nochmal versuchen.
            static bool WallHit(Vector3 from, Vector3 dir, float len, out RaycastHit best)
            {
                best = default(RaycastHit); bool any = false;
                foreach (var h in Physics.RaycastAll(from, dir, len, GroundMask, QueryTriggerInteraction.Ignore))
                {
                    if (Mathf.Abs(h.normal.y) > 0.55f || h.collider.GetComponentInParent<Pickable>() != null) continue;
                    if (!any || h.distance < best.distance) { best = h; any = true; }
                }
                return any;
            }
            void FindWall()
            {
                // Wand hinter dem Akteur suchen (er schaut von der Wand weg), sonst rundherum
                var fw = Rot(0, yaw0, 0) * Vector3.forward; RaycastHit h;
                wallTries++;
                if (WallHit(Home + fw * 0.4f, -fw, 1.5f, out h)) { n = h.normal; w = h.point + n * 0.12f; wallOk = true; }
                else
                {
                    for (int i = 0; i < 8 && !wallOk; i++)
                    {
                        var d = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                        if (WallHit(Home - d * 0.3f, d, 1.2f, out h)) { n = h.normal; w = h.point + n * 0.12f; wallOk = true; }
                    }
                }
                if (!wallOk) { n = fw; n.y = 0f; n = n.sqrMagnitude > 0.01f ? n.normalized : Vector3.forward; w = Home; }
                w0 = w; n0 = n;
            }
            bool SwordHit(Vector3 lp, float lyaw)
            {
                if (Root == null || health <= 0 || hitCool > 0f) return false;
                var d = w - (lp + Vector3.up * 0.75f); float dy = d.y; d.y = 0f;
                var fw = Quaternion.Euler(0f, lyaw, 0f) * Vector3.forward;
                if (d.magnitude > 1.7f || dy < -1.1f || dy > 1.6f || (d.magnitude > 0.4f && Vector3.Angle(fw, d) > 80f)) return false;
                Die(); return true;
            }
            void Die()
            {
                if (health <= 0) return;
                health = 0; dieT = 0f; OotSfx.Play("NA_SE_EN_STALWALL_DEAD", 0.9f, w); OotSfx.Play("NA_SE_IT_SWORD_STRIKE", 0.6f, w);
            }
            public void Update(Player p)
            {
                if (Root == null) return;
                if (!wallOk && wallTries < 12 && health > 0 && Time.time > wallRetry) { wallRetry = Time.time + 0.5f; FindWall(); }
                if (hitCool > 0f) hitCool -= 0.05f;
                frame += 1.5f; bob += 0.05f;
                var lp = p.transform.position + Vector3.up * 0.8f;
                if (health > 0)
                {
                    if (!gold && (lp - w).sqrMagnitude < 13.2f * 13.2f)
                    {
                        // auf der Wandebene zu Link krabbeln (4 Einheiten/Bild), an der Wand bleiben
                        var to = lp - w; to -= n * Vector3.Dot(to, n);
                        if (to.magnitude > 0.2f)
                        {
                            var nw = w + to.normalized * Mathf.Min(to.magnitude, 4f * S * UPD);
                            // auf der eigenen Wand bleiben (Abstand zur Heimat, gleiche Wandrichtung), sonst sammeln sich alle bei Link
                            RaycastHit h;
                            if ((nw - w0).magnitude < 2.4f && WallHit(nw + n * 0.4f, -n, 0.9f, out h) && Vector3.Dot(h.normal, n0) > 0.75f) { w = h.point + h.normal * 0.12f; n = h.normal; }
                        }
                        if (((int)frame) % 30 == 0) OotSfx.Play("NA_SE_EN_STALWALL_ROLL", 0.6f, w);
                        if ((lp - w).magnitude < 0.55f) OotCombat.Damage(0x08, w);
                    }
                    else if (gold && ((int)frame) % 120 == 0) OotSfx.Play("NA_SE_EN_STALGOLD_ROLL", 0.5f, w);
                }
                else if (dieT >= 0f)
                {
                    dieT += 0.05f;
                    w += (n * 0.03f + Vector3.down * dieT * 0.12f);
                    if (dieT > 0.7f)
                    {
                        if (model != null) { model.SetActive(false); }
                        try { var fx = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("vfx_skeleton_death") : null; if (fx != null) UnityEngine.Object.Instantiate(fx, w, Quaternion.identity); } catch { }
                        dieT = -1f;
                        if (gold)
                        {
                            token = !string.IsNullOrEmpty(tokenModel) ? Model(tokenModel, Root.transform) : null;
                            if (token != null) token.transform.localScale = Vector3.one * (S * 0.01f * 2f);
                            tokenT = 0f; OotSfx.Play("NA_SE_EN_STALGOLD_UP_CRY", 0.8f, w);
                        }
                        else { if (UnityEngine.Random.value < 0.5f) OotItems.DropAt(w, "rupee"); Gone = true; Despawn(); return; }
                    }
                }
                else if (token != null)
                {
                    // Symbol schwebt von der Wand weg zu Link hin erreichbar und dreht sich (En_Si)
                    tokenT += 0.05f;
                    var tp = w + n * Mathf.Min(0.6f, tokenT * 0.6f);
                    token.transform.position = tp; token.transform.rotation = Quaternion.Euler(0f, tokenT * 180f, 0f);
                    if ((p.transform.position + Vector3.up * 0.8f - tp).magnitude < 0.9f && OotPlay.Calm)
                    {
                        Tokens++; if (!string.IsNullOrEmpty(flag)) Story.Flags.Add(flag); Story.Flags.Add("oot_tokens_" + Tokens); Story.Save();
                        OotPlay.GiveItem("token");
                        Gone = true; Despawn(); return;
                    }
                }
            }
            public void Draw()
            {
                if (Root == null || model == null || !model.activeSelf) { if (focus != null) focus.position = w; return; }
                model.transform.position = w;
                // Bauch an der Wand: Oberseite zeigt von der Wand weg, Kopf nach oben
                var up = n; var fwd = Vector3.ProjectOnPlane(Vector3.up, n); if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
                model.transform.rotation = Quaternion.LookRotation(fwd.normalized, up) * Quaternion.Euler(0f, 0f, health <= 0 ? dieT * 900f : 0f);
                model.transform.localScale = Vector3.one * (S * 0.02f);
                if (anim != null && anim["idle"] != null) { var a = anim["idle"]; a.enabled = true; a.weight = 1f; a.speed = 0f; a.time = (frame % 30f) / 30f; }
                if (focus != null) focus.position = w;
            }
        }
    }
}
