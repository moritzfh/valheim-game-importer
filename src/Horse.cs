using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Reitpferd aus dem Spielpaket (Witcher: Ploetze). pack.json:
    //   "horse": {"name": {"de": "Plötze"}, "model": "models/roach.glb", "key": "H", "saddle": 1.35,
    //             "anims": {"idle": "idle", "walk": "walk", "trot": "trot", "canter": "canter", "gallop": "gallop"},
    //             "speeds": {"walk": 1.8, "trot": 4.5, "canter": 8.0, "gallop": 13.0}}
    // H ruft das Pferd (es galoppiert heran), E steigt auf, Springen/E steigt ab. Gelenkt wird wie Valheims Lox:
    // W = Trab, W + Rennen (Shift) = Galopp, Richtung = Blickrichtung der Kamera.
    // Andere Spieler sehen den Reiter auf einem eigenen, lokal nachgebauten Pferd (Gangart ueber das ZDO "gi_ride").
    public static class Horse
    {
        public static readonly string[] Gears = { "idle", "walk", "trot", "canter", "gallop" };
        internal static string Model; internal static object Name; internal static KeyCode Key = KeyCode.H; internal static float Saddle = 1.35f;
        internal static readonly Dictionary<string, string> Anims = new Dictionary<string, string>();
        internal static readonly float[] Speeds = { 0f, 1.8f, 4.5f, 8f, 13f };
        static Pack forPack;
        static HorseRide mine;                                           // eigenes Pferd (dieser Client)
        static readonly Dictionary<Player, HorseRide> remote = new Dictionary<Player, HorseRide>();
        static float syncT;
        static readonly int RideHash = "gi_ride".GetStableHashCode();

        static void Setup(Pack pk)
        {
            Clear(); forPack = pk; Model = null; Anims.Clear();
            var cfg = pk != null ? Json.V(PackMeta(pk), "horse") : null;
            if (cfg == null) return;
            Name = Json.V(cfg, "name") ?? "Pferd";
            if (pk.ValheimStyle) { ValheimMount.Prefab = Json.S(cfg, "valheim", "Lox"); Plugin.Log("Reittier (Valheim): " + ValheimMount.Prefab); return; }
            var m = Json.S(cfg, "model");
            if (string.IsNullOrEmpty(m)) return;
            Model = Path.Combine(pk.Dir, m.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(Model)) { Plugin.Warn("Pferd fehlt: " + Model); Model = null; return; }
            Name = Json.V(cfg, "name") ?? "Pferd";
            Saddle = Json.F(cfg, "saddle", 1.35f);
            try { Key = (KeyCode)Enum.Parse(typeof(KeyCode), Json.S(cfg, "key", "H"), true); } catch { Key = KeyCode.H; }
            var an = Json.O(cfg, "anims");
            if (an != null) foreach (var kv in an) Anims[kv.Key] = kv.Value as string;
            var sp = Json.O(cfg, "speeds");
            if (sp != null) for (int i = 1; i < Gears.Length; i++) Speeds[i] = Json.F(sp, Gears[i], Speeds[i]);
            Plugin.Log("Pferd: " + Model);
        }

        static object PackMeta(Pack pk)
        {
            try { return Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { return null; }
        }

        public static void Clear()
        {
            if (mine != null) { mine.Dismount(); UnityEngine.Object.Destroy(mine.gameObject); }
            mine = null;
            foreach (var r in remote.Values) if (r != null) UnityEngine.Object.Destroy(r.gameObject);
            remote.Clear();
        }

        // Gangart, wenn der Spieler reitet, sonst -1 (fuer die Reiter-Animation der Spielfigur)
        public static int RideGear(Player p)
        {
            if (p == null) return -1;
            if (p == Player.m_localPlayer) return mine != null && mine.Rider == p ? mine.Gear : -1;
            HorseRide r;
            return remote.TryGetValue(p, out r) && r != null ? r.Gear : -1;
        }

        public static void Tick()
        {
            var pk = Pack.Current;
            if (pk != forPack) Setup(pk);
            var me = Player.m_localPlayer;
            if (pk != null && pk.ValheimStyle && ValheimMount.Prefab != null) { if (me != null && Input.GetKeyDown(Key) && FreeInput()) Call(); return; }
            if (Model == null || me == null) return;
            // Rufen
            if (Input.GetKeyDown(Key) && FreeInput()) Call();
            // Gangart an die anderen melden
            if (Time.time > syncT)
            {
                syncT = Time.time + 0.2f;
                var zdo = me.GetComponent<ZNetView>() != null ? me.GetComponent<ZNetView>().GetZDO() : null;
                if (zdo != null) zdo.Set(RideHash, mine != null && mine.Rider == me ? mine.Gear + 1 : 0);
                // andere Reiter sichtbar machen
                foreach (var p in Player.GetAllPlayers())
                {
                    if (p == null || p == me) continue;
                    var nv = p.GetComponent<ZNetView>(); var z = nv != null ? nv.GetZDO() : null;
                    int g = z != null ? z.GetInt(RideHash, 0) : 0;
                    HorseRide r; remote.TryGetValue(p, out r);
                    if (g > 0 && r == null) { r = Spawn(true); if (r != null) { r.Follow = p; remote[p] = r; } }
                    else if (g == 0 && r != null) { UnityEngine.Object.Destroy(r.gameObject); remote.Remove(p); }
                    if (r != null) r.Gear = g - 1;
                }
            }
        }

        static HorseRide Spawn(bool remoteOnly)
        {
            try
            {
                var go = new GameObject(remoteOnly ? "GI_HorseRemote" : "GI_Horse");
                var vis = Glb.Instantiate(Model);
                vis.transform.SetParent(go.transform, false);
                var hr = go.AddComponent<HorseRide>();
                hr.Anim = vis.GetComponent<Animation>();
                hr.RemoteOnly = remoteOnly;
                if (!remoteOnly)
                {
                    var hit = new GameObject("hit"); hit.transform.SetParent(go.transform, false);
                    var box = hit.AddComponent<BoxCollider>(); box.center = new Vector3(0f, 1.3f, 0f); box.size = new Vector3(0.7f, 1.4f, 2.2f);
                    hit.AddComponent<HorseUse>().Ride = hr;
                    int l = LayerMask.NameToLayer("character"); if (l >= 0) hit.layer = l;
                    var ap = new GameObject("attach"); ap.transform.SetParent(go.transform, false); ap.transform.localPosition = new Vector3(0f, Saddle, 0f);
                    hr.AttachPoint = ap.transform;
                }
                return hr;
            }
            catch (Exception e) { Plugin.Warn("Pferd: " + e.Message); Model = null; return null; }
        }

        // Ploetze rufen (H, bei Witcher-Steuerung X)
        public static void Call()
        {
            var me = Player.m_localPlayer;
            if (Pack.Current != null && Pack.Current.ValheimStyle && ValheimMount.Prefab != null) { ValheimMount.Call(me); return; }
            if (Model == null || me == null) return;
            if (mine == null) mine = Spawn(false);
            if (mine != null && mine.Rider == null) mine.CallTo(me);
        }

        internal static bool FreeInput()
        {
            try
            {
                if (Chat.instance != null && Chat.instance.HasFocus()) return false;
                if (Console.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || StoryUi.Busy) return false;
            }
            catch { }
            return true;
        }

        internal static string T(object o) { return Pack.T(o); }

        // Fernsteuerung zum Testen ohne Tastatur: horse call | mount | off | go <0..4> | info
        public static string Test(string[] a)
        {
            var me = Player.m_localPlayer;
            if (Model == null || me == null) return "horse: kein Pferd im Paket / kein Spieler";
            string c = a.Length > 1 ? a[1] : "info";
            if (c == "call") { if (mine == null) mine = Spawn(false); if (mine != null) mine.CallTo(me); }
            else if (c == "mount" && mine != null) mine.Mount(me);
            else if (c == "off" && mine != null) mine.Dismount();
            else if (c == "go" && mine != null) mine.TestGear = a.Length > 2 ? int.Parse(a[2]) : 0;
            return mine == null ? "horse: keins" : "horse " + mine.transform.position + " gear " + mine.Gear + " rider " + (mine.Rider != null);
        }
    }

    // Die Pferde-Logik: Bewegung, Boden folgen, Animation, Steuerung durch den Reiter (Valheims Doodad-Steuerung wie beim Lox)
    public class HorseRide : MonoBehaviour, IDoodadController
    {
        public Animation Anim; public Transform AttachPoint; public bool RemoteOnly;
        public Player Rider, Follow; public int Gear; public int TestGear = -1;
        float heading, speed; Vector3 lookDir; bool inFwd, inBack, inRun;
        Player callTo; string curAnim; static int groundMask = -1;

        void Awake() { heading = transform.eulerAngles.y; }

        public void CallTo(Player p)
        {
            callTo = p;
            if (Vector3.Distance(transform.position, p.transform.position) > 60f || transform.position == Vector3.zero)
            {
                // irgendwo in 15-20 m auftauchen, wo freie Bahn zum Spieler ist (zuerst hinter ihm)
                var back = -p.transform.forward; back.y = 0f; back.Normalize();
                Vector3 best = p.transform.position + back * 4f; 
                foreach (float ang in new[] { 0f, 40f, -40f, 80f, -80f, 120f, -120f, 180f })
                    foreach (float dist in new[] { 18f, 12f, 7f })
                    {
                        var dir = Quaternion.Euler(0f, ang, 0f) * back;
                        var cand = Ground(p.transform.position + dir * dist);
                        if (cand.y < 30f - 1f) continue;
                        var from = cand + Vector3.up * 1.2f; var to = p.transform.position + Vector3.up * 1.2f;
                        if (Physics.Linecast(from, to, ObstacleMask(), QueryTriggerInteraction.Ignore)) continue;
                        best = cand; goto found;
                    }
                found:
                transform.position = Ground(best);
                var look = p.transform.position - transform.position; look.y = 0f;
                if (look.sqrMagnitude > 0.01f) heading = Quaternion.LookRotation(look).eulerAngles.y;
            }
            if (MessageHud.instance != null) MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, Horse.T(Horse.Name) + "!");
        }

        public void Mount(Player p)
        {
            if (Rider != null || p == null) return;
            Rider = p; callTo = null;
            try
            {
                var sd = AccessTools.Method(typeof(Player), "StartDoodadControl");
                if (sd != null) sd.Invoke(p, new object[] { this });
                var at = AccessTools.Method(typeof(Player), "AttachStart");
                if (at != null)
                {
                    var ps = at.GetParameters(); var args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        var n = ps[i].Name.ToLowerInvariant(); var t = ps[i].ParameterType;
                        if (t == typeof(Transform)) args[i] = n.Contains("camera") ? null : AttachPoint;
                        else if (t == typeof(GameObject)) args[i] = gameObject;
                        else if (t == typeof(bool)) args[i] = false;
                        else if (t == typeof(string)) args[i] = "attach_lox";
                        else if (t == typeof(Vector3)) args[i] = new Vector3(0f, 0.3f, 1.2f);
                        else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : (t.IsValueType ? Activator.CreateInstance(t) : null);
                    }
                    at.Invoke(p, args);
                }
            }
            catch (Exception e) { Plugin.Warn("Aufsteigen: " + e.Message); }
        }

        public void Dismount()
        {
            var p = Rider; Rider = null; speed = 0f; Gear = 0;
            if (p == null) return;
            try
            {
                var st = AccessTools.Method(typeof(Player), "StopDoodadControl"); if (st != null) st.Invoke(p, null);
                var at = AccessTools.Method(typeof(Character), "AttachStop") ?? AccessTools.Method(typeof(Player), "AttachStop"); if (at != null) at.Invoke(p, null);
            }
            catch (Exception e) { Plugin.Quiet(e, "Absteigen"); }
        }

        // ---- IDoodadController ----
        public void OnUseStop(Player player) { if (player == Rider) Dismount(); }
        public void ApplyControlls(Vector3 moveDir, Vector3 lookDir, bool run, bool autoRun, bool block)
        {
            inFwd = moveDir.z > 0.3f || autoRun; inBack = moveDir.z < -0.3f; inRun = run; this.lookDir = lookDir;
        }
        public Component GetControlledComponent() { return this; }
        public Vector3 GetPosition() { return transform.position; }
        public bool IsValid() { return this != null && Rider != null; }

        void Update()
        {
            float dt = Time.deltaTime;
            if (RemoteOnly) { UpdateRemote(); return; }
            float target = 0f; int gear = 0;
            if (Rider != null)
            {
                if (Rider.IsDead()) { Dismount(); return; }
                if (TestGear >= 0) { inFwd = TestGear > 0; inRun = TestGear >= 4; lookDir = Rider.transform.forward; if (TestGear == 0) inBack = false; }
                if (inFwd) { gear = inRun ? 4 : 2; }
                else if (inBack) gear = 1;
                if (inFwd || inBack)
                {
                    var ld = lookDir; ld.y = 0f;
                    if (ld.sqrMagnitude > 0.01f)
                    {
                        float want = Quaternion.LookRotation(ld).eulerAngles.y;
                        float rate = Mathf.Lerp(150f, 70f, speed / Horse.Speeds[4]);
                        heading = Mathf.MoveTowardsAngle(heading, want, rate * dt);
                    }
                }
                target = inBack ? -Horse.Speeds[1] : Horse.Speeds[gear];
                if ((ZInput.GetButtonDown("Jump") || ZInput.GetButtonDown("Use")) && Horse.FreeInput() && Mathf.Abs(speed) < 6f) { Dismount(); return; }
            }
            else if (callTo != null)
            {
                var to = callTo.transform.position - transform.position; to.y = 0f;
                float d = to.magnitude;
                if (d > 3f) { heading = Mathf.MoveTowardsAngle(heading, Quaternion.LookRotation(to).eulerAngles.y, 240f * dt); target = d > 12f ? Horse.Speeds[4] : Horse.Speeds[2]; }
                else callTo = null;
            }
            float acc = target > speed ? 6f : 10f;
            speed = Mathf.MoveTowards(speed, target, acc * dt);
            var fwd = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
            var next = transform.position + fwd * speed * dt;
            // Hindernis vorne? Erst seitlich ausweichen, sonst anhalten
            if (Mathf.Abs(speed) > 0.1f && Blocked(fwd * Mathf.Sign(speed), Mathf.Abs(speed) * dt + 1.3f))
            {
                bool ok = false;
                foreach (float ang in new[] { 35f, -35f, 70f, -70f })
                {
                    var alt = Quaternion.Euler(0f, heading + ang, 0f) * Vector3.forward;
                    if (!Blocked(alt * Mathf.Sign(speed), Mathf.Abs(speed) * dt + 1.3f))
                    {
                        heading = Mathf.MoveTowardsAngle(heading, heading + ang, 200f * dt);
                        fwd = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
                        next = transform.position + fwd * speed * dt; ok = true; break;
                    }
                }
                if (!ok) { speed = 0f; next = transform.position; }
            }
            var g = Ground(next);
            if (g.y < 30f - 1.2f) { speed = 0f; g = Ground(transform.position); }      // nicht ins tiefe Wasser
            transform.position = g;
            transform.rotation = Quaternion.Euler(0f, heading, 0f);
            float a = Mathf.Abs(speed);
            Gear = a < 0.3f ? 0 : a < (Horse.Speeds[1] + Horse.Speeds[2]) * 0.5f ? 1 : a < (Horse.Speeds[2] + Horse.Speeds[3]) * 0.5f ? 2 : a < (Horse.Speeds[3] + Horse.Speeds[4]) * 0.5f ? 3 : 4;
            Play(Gear, a);
            if (Rider != null) { Rider.transform.position = AttachPoint.position; Rider.transform.rotation = transform.rotation; }
        }

        void UpdateRemote()
        {
            if (Follow == null) return;
            transform.position = Follow.transform.position - Vector3.up * Horse.Saddle;
            transform.rotation = Quaternion.Euler(0f, Follow.transform.eulerAngles.y, 0f);
            Play(Mathf.Clamp(Gear, 0, 4), Horse.Speeds[Mathf.Clamp(Gear, 0, 4)]);
        }

        void Play(int gear, float spd)
        {
            if (Anim == null) return;
            string k = Horse.Gears[gear], clip;
            if (!Horse.Anims.TryGetValue(k, out clip) || string.IsNullOrEmpty(clip)) return;
            var st = Anim[clip]; if (st == null) return;
            if (curAnim != clip) { st.wrapMode = WrapMode.Loop; Anim.CrossFade(clip, 0.25f); curAnim = clip; }
            st.speed = gear == 0 ? 1f : Mathf.Clamp(spd / Mathf.Max(0.1f, Horse.Speeds[gear]), 0.7f, 1.4f);
        }

        static Vector3 Ground(Vector3 p)
        {
            if (groundMask < 0) groundMask = LayerMask.GetMask("terrain", "static_solid", "Default", "piece", "Default_small");
            RaycastHit hit;
            if (Physics.Raycast(p + Vector3.up * 4f, Vector3.down, out hit, 40f, groundMask, QueryTriggerInteraction.Ignore)) return hit.point;
            float h; if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out h)) return new Vector3(p.x, h, p.z);
            return p;
        }

        static int obstacleMask = -1;
        static int ObstacleMask() { if (obstacleMask < 0) obstacleMask = LayerMask.GetMask("static_solid", "Default", "piece", "Default_small", "terrain"); return obstacleMask; }

        bool Blocked(Vector3 dir, float dist)
        {
            RaycastHit hit;
            if (Physics.SphereCast(transform.position + Vector3.up * 1.1f, 0.35f, dir, out hit, dist, ObstacleMask(), QueryTriggerInteraction.Ignore))
                return hit.normal.y < 0.55f && !hit.collider.transform.IsChildOf(transform);
            return false;
        }
    }

    // E am Pferd: aufsteigen
    public class HorseUse : MonoBehaviour, Hoverable, Interactable
    {
        public HorseRide Ride;
        public string GetHoverText() { return Localization.instance.Localize(Horse.T(Horse.Name) + "\n[<color=yellow><b>$KEY_Use</b></color>] " + Story.L("Aufsteigen", "Mount")); }
        public float GetHoverOffset() { return 0f; }
        public string GetHoverName() { return Horse.T(Horse.Name); }
        public bool Interact(Humanoid user, bool hold, bool alt) { if (hold) return false; var p = user as Player; if (p == null || Ride == null) return false; Ride.Mount(p); return true; }
        public bool UseItem(Humanoid user, ItemDrop.ItemData item) { return false; }
    }
}


namespace GameImporter
{
    // Valheim-Optik: Ploetze ist ein echtes Valheim-Reittier (gezaehmt, mit Sattel; Reiten macht Valheim selbst, auch im Mehrspieler).
    // Rufen (X/H): gibt's noch keins -> erscheint hinter dem Spieler; ist es weit weg -> kommt es herangelaufen.
    public static class ValheimMount
    {
        public static string Prefab;
        static readonly int OwnerHash = "gi_mount_owner".GetStableHashCode();

        static Character Find(Player me)
        {
            long id = me.GetPlayerID();
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead()) continue;
                var nv = c.GetComponent<ZNetView>(); var z = nv != null ? nv.GetZDO() : null;      // Name des Objekts aendert sich mit dem Tiernamen -> ueber die Kennung gehen
                if (z != null && z.GetLong(OwnerHash, 0L) == id) return c;
            }
            return null;
        }

        static Vector3 Behind(Player me, float dist)
        {
            var p = me.transform.position - me.transform.forward * dist;
            float h; if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out h)) p.y = h + 0.3f; else p.y = me.transform.position.y + 0.5f;
            return p;
        }

        public static void Call(Player me)
        {
            if (me == null || ZNetScene.instance == null) return;
            try
            {
                var c = Find(me);
                if (c == null)
                {
                    var pf = ZNetScene.instance.GetPrefab(Prefab);
                    if (pf == null) { Plugin.Warn("Reittier gibt's nicht: " + Prefab); return; }
                    var go = UnityEngine.Object.Instantiate(pf, Behind(me, 12f), Quaternion.LookRotation(me.transform.forward));
                    c = go.GetComponent<Character>();
                    var nv = go.GetComponent<ZNetView>();
                    if (nv != null && nv.GetZDO() != null) nv.GetZDO().Set(OwnerHash, me.GetPlayerID());
                    if (c != null) c.SetTamed(true);
                    var tm = go.GetComponent<Tameable>();
                    if (tm != null)
                    {
                        var ss = AccessTools.Method(typeof(Tameable), "SetSaddle", new[] { typeof(bool) }); if (ss != null) ss.Invoke(tm, new object[] { true });
                        var sn = AccessTools.Method(typeof(Tameable), "SetName", new[] { typeof(string) }); if (sn != null) sn.Invoke(tm, new object[] { Pack.T(Horse.Name) });
                    }
                    Story.Msg(Pack.T(Horse.Name));
                }
                else if (Vector3.Distance(c.transform.position, me.transform.position) > 60f)
                    c.transform.position = Behind(me, 25f);          // ausser Sicht hinter dem Spieler, dann kommt es angelaufen
                var ai = c.GetComponent<MonsterAI>();
                if (ai != null) ai.SetFollowTarget(me.gameObject);
            }
            catch (Exception e) { Plugin.Warn("Reittier: " + e.Message); }
        }

        public static string Info(Player me)
        {
            var c = me != null ? Find(me) : null;
            return c == null ? "kein Reittier" : "reittier " + Prefab + " @" + Vector3.Distance(c.transform.position, me.transform.position).ToString("F0") + " m";
        }
    }
}
