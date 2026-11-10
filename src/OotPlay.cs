using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Spielen wie in Ocarina of Time (pack.json "oot_play": {"model": "player/link_child.glb", "scale": 0.00033, "hud": "player/hud"}).
    //   Figur: Original-Kind-Link (GLB mit Original-Spieleranimationen als Clips), Valheims Koerper unsichtbar.
    //   Bewegung wie z_player.c: Tempo nach Stick (Tastatur = voll), max. 6 OoT-Einheiten/Bild (drinnen 5), Link dreht sich mit
    //   begrenzter Geschwindigkeit in die Laufrichtung (nicht sofort), Bremsen/Beschleunigen, Rolle auf A (Shift), keine Ausdauer.
    //   Extra (Remaster): Springen mit Leertaste. Z (rechte Maustaste halten): Kamera hinter Link, seitwaerts gehen,
    //   Leertaste + zurueck = Rueckwaertssalto, + links/rechts = Seitsprung. An Kanten springt Link beim Rennen von selbst.
    //   Kamera: folgt wie die OoT-Normalkamera (schwenkt beim Laufen langsam hinter Link), Maus dreht frei.
    // Valheims Physik (Boden, Waende, Schwerkraft) bleibt; nur waagrechte Geschwindigkeit, Drehung, Kamera und Optik kommen von hier.
    public static class OotPlay
    {
        // OoT -> Valheim: 1 Einheit = 0,033 m, Spielbild 1/20 s, Bewegung je Bild = Tempo * 1,5 (R_UPDATE_RATE 3)
        const float U = 0.033f * 1.5f * 20f;            // Tempo (Einheiten/Bild) -> m/s
        const float RUN = 6f * U, RUN_IN = 5f * U, WALK = 2.2f * U;
        const float ACCEL = 40f, DECEL = 55f, TURN = 900f, ROLL_SPEED = 6.6f * U;
        // Remaster-Extra: nach der Rolle A (Shift) gedrueckt halten = Sprinten (schneller als Dauerrollen)
        const float SPRINT = 7.3f * U, ROLL_TIME = 32f / 30f / 1.25f;

        static Pack forPack; static bool cfg;
        static readonly List<GameObject> lights = new List<GameObject>();       // feste Lichter (Innenraeume), pack.json oot_play.lights
        static string modelPath, hudDir; static float modelScale = 0.00033f;
        public static bool On { get { if (Pack.Current != forPack) Setup(Pack.Current); return cfg && Player.m_localPlayer != null; } }
        public static bool Driving { get { return On && !StoryUi.Busy && !Shots.Active; } }

        // Zustand
        static Player owner; static GameObject link; static Animation anim;
        static float yaw, speed; static Vector3 airVel; static bool airborne, wasGround = true;
        static string state = "idle", clip; static float stateT, airT, hideT;
        static bool zHeld, aDown, aHeld, jumpDown, sprintArmed;      // sprintArmed: seit der letzten Rolle A gehalten
        static bool bDown, hitDone; static int slashN, textPage; static float swordOutUntil;
        public static bool CUpDown; static bool zPrev, zDown, rHeld, c1Down, c3Down; public static bool Aiming { get { return state == "aim"; } } public static bool StickOut; static float stickFire, climbAnimT; static Vector3 climbN, endFrom, endTo; static float wantY; static bool wantYOn;
        public static float Speed { get { return speed; } }
        static bool zPrev2; static Vector3 knock; static Vector3 respawnPos; static float respawnYaw; static bool haveRespawn;
        public const string ShieldFlag = "oot_shield";
        public static bool HasShield { get { return Story.Flags.Contains(ShieldFlag); } }
        public static bool Shielding { get { return state == "shield"; } }
        public static string Text;                       // Textbox (OotHud), z. B. Get-Item-Text aus der ROM
        // Teile an Link (Hand, Schwert in der Faust, Scheide mit/ohne Schwert): Renderer + Materialplatz
        class PartSlot { public Renderer R; public int I; public Material M; public string Part; }
        static readonly List<PartSlot> partSlots = new List<PartSlot>(); static string partState;
        static Vector2 stick; static float stickYaw; static bool walkKey;
        static float camYaw, camPitch = 14f, camDist = 5.2f, mouseT; static bool camInit;
        static Vector3 camPos;
        public static string ALabel;                    // Aktion auf dem A-Knopf ("Open", "Speak", "Check") fuer OotHud
        static readonly AccessTools.FieldRef<Character, Rigidbody> bodyRef = AccessTools.FieldRefAccess<Character, Rigidbody>("m_body");
        static AccessTools.FieldRef<Character, float> airAltRef;
        static float airTopY; static Vector3 lastTickPos;
        const float CHEST_SP = 1.35f, CHEST_T = 133f / 30f / CHEST_SP, CHEST_LID = CHEST_T * 0.42f;   // Truhe: Original-Animation (133 Bilder) etwas schneller
        static readonly List<Material> liftMats = new List<Material>(); static readonly List<Color> liftBase = new List<Color>();
        public static float LiftK = 1f, SunLum, LiftS0 = 0.42f, LiftS1 = 1.1f;   // gemessen: Mond ~0,39, Mittagssonne ~1,38
        static void UpdateLift()
        {
            // Mass fuer "wie hell ist es": Valheims Sonne/Mond (Richtungslicht); Umgebungslicht bleibt nachts zu hoch
            SunLum = 0f;
            try
            {
                var dl = EnvMan.instance != null ? EnvMan.instance.m_dirLight : null;
                if (dl != null && dl.enabled && dl.gameObject.activeInHierarchy) { var c = dl.color; SunLum = dl.intensity * (c.r * 0.3f + c.g * 0.59f + c.b * 0.11f); }
            }
            catch { }
            if (liftMats.Count == 0) return;
            float k = Mathf.Clamp01((SunLum - LiftS0) / Mathf.Max(0.05f, LiftS1 - LiftS0));
            LiftK = Mathf.Lerp(LiftK, k, Mathf.Clamp01(Time.deltaTime * 2f));
            for (int i = 0; i < liftMats.Count; i++) if (liftMats[i] != null) liftMats[i].SetColor("_EmissionColor", liftBase[i] * LiftK);
            // Navis Licht (wie in OoT: die Fee leuchtet und hellt Link im Dunkeln auf); spaeter haengt es an Navis Figur
            if (naviLight == null && owner != null)
            {
                var go = new GameObject("gi_navi_light"); naviLight = go.AddComponent<Light>();
                naviLight.type = LightType.Point; naviLight.color = new Color(0.72f, 0.86f, 1f); naviLight.range = 5.5f; naviLight.shadows = LightShadows.None;
            }
            if (naviLight != null && owner != null)
            {
                float dark = 1f - LiftK;
                naviLight.intensity = NaviLight * dark;
                naviLight.enabled = naviLight.intensity > 0.02f;
                OotNavi.Tick(owner, dark);
                if (OotNavi.T != null) naviLight.transform.position = OotNavi.T.position;
            }
        }
        static Light naviLight; public static float NaviLight = 1.3f;

        // ---------- Stimmung (Uhrzeit/Wetter je Gebiet) ----------
        // Gebiet: 2D-Kasten (min/max x,z) oder 3D ("box": [x0,y0,z0,x1,y1,z1]); bei Ueberlappung gewinnt das kleinste.
        //   "time": feste Uhrzeit nur hier, "reverb": Hall (Unity-Vorgabe, z. B. "Cave"), "motes"/"flies": Schwebeteilchen/Gluehwuermchen,
        //   "mote_color": [r,g,b,a], "env"/"envmod"/"gust"/"fog"/"ambient" wie bisher
        class Area { public string Id = ""; public float X0, Z0, X1, Z1, Y0 = -1e6f, Y1 = 1e6f; public string Env, Gust; public float GustEvery0 = 150f, GustEvery1 = 300f, GustLen0 = 35f, GustLen1 = 60f;
            public bool HasFog, HasAmb; public Color FogC, Amb; public float FogD; public float Time = -1f, Motes, Flies; public string Reverb, Track, Title, TitleGroup; public Color? MoteCol;
            public bool Has3D; public float Vol { get { return (X1 - X0) * (Z1 - Z0) * (Has3D ? (Y1 - Y0) : 1e6f); } }
            public bool Inside(Vector3 p) { return p.x >= X0 && p.x <= X1 && p.z >= Z0 && p.z <= Z1 && p.y >= Y0 && p.y <= Y1; } }
        static Area curArea; static string lastTitleGroup = "#start";      // beim Laden kein Ortsname
        static readonly Dictionary<string, object> envMods = new Dictionary<string, object>();
        static readonly HashSet<string> envMade = new HashSet<string>();
        static EnvMan envFor;
        static void MakeEnvs()
        {
            if (EnvMan.instance == null) return;
            if (envFor != EnvMan.instance) { envFor = EnvMan.instance; envMade.Clear(); }
            foreach (var kv in envMods)
            {
                if (envMade.Contains(kv.Key) || string.IsNullOrEmpty(kv.Key)) continue;
                try
                {
                    EnvSetup src = null;
                    foreach (var e in EnvMan.instance.m_environments) if (e != null && e.m_name == Json.S(kv.Value, "base", "Crypt")) { src = e; break; }
                    if (src == null) continue;
                    var env = (EnvSetup)AccessTools.Method(typeof(object), "MemberwiseClone").Invoke(src, null);
                    env.m_name = kv.Key;
                    Action<string, object> set = (f, v) => { var fi = AccessTools.Field(typeof(EnvSetup), f); if (fi != null) fi.SetValue(env, v); };
                    var fc = Json.L(kv.Value, "fog");
                    if (fc != null && fc.Count >= 3)
                    {
                        var c = new Color(Convert.ToSingle(fc[0]), Convert.ToSingle(fc[1]), Convert.ToSingle(fc[2]));
                        foreach (var f in new[] { "m_fogColorNight", "m_fogColorMorning", "m_fogColorDay", "m_fogColorEvening", "m_fogColorSunNight", "m_fogColorSunMorning", "m_fogColorSunDay", "m_fogColorSunEvening" }) set(f, c);
                    }
                    if (Json.Has(kv.Value, "fog_density"))
                        foreach (var f in new[] { "m_fogDensityNight", "m_fogDensityMorning", "m_fogDensityDay", "m_fogDensityEvening" }) set(f, Json.F(kv.Value, "fog_density"));
                    var ac = Json.L(kv.Value, "amb");
                    if (ac != null && ac.Count >= 3)
                    {
                        var c = new Color(Convert.ToSingle(ac[0]), Convert.ToSingle(ac[1]), Convert.ToSingle(ac[2]));
                        set("m_ambColorDay", c); set("m_ambColorNight", c);
                    }
                    // Krypta-Nebelschwaden (gruene Partikel) weg
                    if (Json.B(kv.Value, "no_particles"))
                        foreach (var f in new[] { "m_psystems", "m_envObject" })
                        {
                            var fi = AccessTools.Field(typeof(EnvSetup), f); if (fi == null) continue;
                            fi.SetValue(env, fi.FieldType.IsArray ? (object)Array.CreateInstance(fi.FieldType.GetElementType(), 0) : null);
                        }
                    EnvMan.instance.m_environments.Add(env);
                    envMade.Add(kv.Key);
                    Plugin.Log("OoT-Umgebung " + kv.Key + " aus " + src.m_name);
                }
                catch (Exception e) { Plugin.Warn("OoT-Umgebung " + kv.Key + ": " + e.Message); envMade.Add(kv.Key); }
            }
        }
        static readonly List<Area> areas = new List<Area>();
        static float fixTime = -1f, moodT, gustAt = -1f, gustUntil = -1f;
        public static bool MoodOn = true, MoodGustOn = true;
        static Pack moodPack; static object moodCfg;
        // fuer das F7-Pult (OotMoodUi): Werte des ersten Gebiets (Kokiri-Wald)
        public static float MoodTime { get { return fixTime; } set { fixTime = value; moodT = 0f; if (value < 0f && fDbgOn != null && EnvMan.instance != null) fDbgOn.SetValue(EnvMan.instance, false); } }
        public static string MoodEnv { get { return areas.Count > 0 ? areas[0].Env : ""; } set { if (areas.Count > 0) { areas[0].Env = value ?? ""; moodT = 0f; } } }
        public static string MoodGust { get { return areas.Count > 0 ? areas[0].Gust : ""; } set { if (areas.Count > 0) { areas[0].Gust = value ?? ""; moodT = 0f; } } }
        public static void GustNow() { gustAt = Time.time; gustUntil = -1f; moodT = 0f; }
        public static void GustStop() { gustUntil = Time.time; gustAt = Time.time + 9999f; moodT = 0f; }
        public static void ReloadMood() { LoadMood(moodPack, moodCfg); moodT = 0f; }

        static void LoadMood(Pack pk, object o)
        {
            moodPack = pk; moodCfg = o;
            fixTime = Json.F(o, "time", -1f);
            areas.Clear(); envMods.Clear();
            foreach (var aj in Json.L(o, "areas") ?? new List<object>())
            {
                var mn = Json.L(aj, "min"); var mx = Json.L(aj, "max"); var bx = Json.L(aj, "box");
                Area ar;
                if (bx != null && bx.Count >= 6)
                    ar = new Area { X0 = Convert.ToSingle(bx[0]), Y0 = Convert.ToSingle(bx[1]), Z0 = Convert.ToSingle(bx[2]), X1 = Convert.ToSingle(bx[3]), Y1 = Convert.ToSingle(bx[4]), Z1 = Convert.ToSingle(bx[5]), Has3D = true };
                else if (mn != null && mx != null && mn.Count >= 2 && mx.Count >= 2)
                    ar = new Area { X0 = Convert.ToSingle(mn[0]), Z0 = Convert.ToSingle(mn[1]), X1 = Convert.ToSingle(mx[0]), Z1 = Convert.ToSingle(mx[1]) };
                else continue;
                ar.Id = Json.S(aj, "id", ""); ar.Env = Json.S(aj, "env", ""); ar.Gust = Json.S(aj, "gust", "");
                var mu = Json.S(aj, "music", null); if (!string.IsNullOrEmpty(mu) && pk != null) ar.Track = Path.Combine(pk.Dir, mu.Replace('/', Path.DirectorySeparatorChar));   // eigene Musik je Gebiet
                ar.Title = Json.S(aj, "title", null); ar.TitleGroup = Json.S(aj, "title_group", ar.Title);      // Ortsname (OotHud) beim Betreten der Gruppe
                ar.Time = Json.F(aj, "time", -1f); ar.Reverb = Json.S(aj, "reverb", null); ar.Motes = Json.F(aj, "motes", 0f); ar.Flies = Json.F(aj, "flies", 0f);
                var mc = Json.L(aj, "mote_color"); if (mc != null && mc.Count >= 4) ar.MoteCol = new Color(Convert.ToSingle(mc[0]), Convert.ToSingle(mc[1]), Convert.ToSingle(mc[2]), Convert.ToSingle(mc[3]));
                var ge = Json.L(aj, "gust_every"); if (ge != null && ge.Count >= 2) { ar.GustEvery0 = Convert.ToSingle(ge[0]); ar.GustEvery1 = Convert.ToSingle(ge[1]); }
                var gl = Json.L(aj, "gust_len"); if (gl != null && gl.Count >= 2) { ar.GustLen0 = Convert.ToSingle(gl[0]); ar.GustLen1 = Convert.ToSingle(gl[1]); }
                // Innenraum-Licht: "fog": [r, g, b, dichte], "ambient": [r, g, b] (ueberschreibt Valheims Umgebung jedes Bild)
                var fg = Json.L(aj, "fog"); if (fg != null && fg.Count >= 4) { ar.HasFog = true; ar.FogC = new Color(Convert.ToSingle(fg[0]), Convert.ToSingle(fg[1]), Convert.ToSingle(fg[2])); ar.FogD = Convert.ToSingle(fg[3]); }
                var am = Json.L(aj, "ambient"); if (am != null && am.Count >= 3) { ar.HasAmb = true; ar.Amb = new Color(Convert.ToSingle(am[0]), Convert.ToSingle(am[1]), Convert.ToSingle(am[2])); }
                // eigene Umgebung aus einer Valheim-Vorlage: "envmod": {"base": "Crypt", "fog": [r,g,b], "fog_density": d, "amb": [r,g,b]}
                var em = Json.V(aj, "envmod");
                if (em != null) envMods[ar.Env] = em;
                areas.Add(ar);
            }
            MoodGustOn = true;
            // eigene Einstellung aus dem F7-Pult (config/GameImporter/oot_mood.json) geht vor
            try
            {
                var f = OotMoodUi.FilePath;
                if (File.Exists(f))
                {
                    var j = Json.Parse(File.ReadAllText(f));
                    if (Json.Has(j, "time")) fixTime = Json.F(j, "time", fixTime);
                    if (areas.Count > 0 && Json.Has(j, "env")) areas[0].Env = Json.S(j, "env", areas[0].Env);
                    if (areas.Count > 0 && Json.Has(j, "gust")) areas[0].Gust = Json.S(j, "gust", areas[0].Gust);
                    if (Json.Has(j, "gust_on")) MoodGustOn = Json.B(j, "gust_on");
                    Plugin.Log("Stimmung aus " + f);
                }
            }
            catch (Exception e) { Plugin.Warn("Stimmung (oot_mood.json): " + e.Message); }
        }
        static System.Reflection.FieldInfo fDbgOn, fDbgT, fForce; static float todLastT; static bool envTrSet;
        static void Mood(Player p)
        {
            if (!MoodOn || EnvMan.instance == null || Time.time < moodT) return;
            MakeEnvs();
            moodT = Time.time + (OotMoodUi.Open ? 0f : 1f);
            Area ar = null;
            {
                // Ortsname schon beim Betreten (auch wenn gleich eine Cutscene startet, wie gDekuTreeIntroCs mit SHOW_TITLE_CARD)
                var pos0 = ghostOn && camFix ? camFixPos : p.transform.position;     // Rundflug: Stimmung dort, wo die Kamera ist
                foreach (var a in areas) if (a.Inside(pos0) && (ar == null || a.Vol < ar.Vol)) ar = a;
                var tg = ar != null ? ar.TitleGroup : null;
                if (tg != lastTitleGroup) { if (tg != null && lastTitleGroup != "#start" && ar.Title != null) OotHud.ShowTitle(ar.Title); lastTitleGroup = tg; }
            }
            if (StoryUi.Busy) return;                 // Cutscenes haben ihr eigenes Licht/Wetter
            try
            {
                if (fDbgOn == null) { fDbgOn = AccessTools.Field(typeof(EnvMan), "m_debugTimeOfDay"); fDbgT = AccessTools.Field(typeof(EnvMan), "m_debugTime"); fForce = AccessTools.Field(typeof(EnvMan), "m_forceEnv"); }
                curArea = ar;
                Music.AreaTrack = ar != null ? ar.Track : null;
                float tod = ar != null && ar.Time >= 0f ? ar.Time : fixTime;
                if (tod >= 0f && fDbgOn != null && fDbgT != null)
                {
                    // Moritz 09.11.: Wechsel Dorf <-> Deku-Wiese weich (Uhrzeit gleitet ~0,05 Tag/s, Valheims Wetter blendet selbst ueber)
                    bool was = (bool)fDbgOn.GetValue(EnvMan.instance);
                    if (!was) fDbgOn.SetValue(EnvMan.instance, true);
                    float curT = (float)fDbgT.GetValue(EnvMan.instance), now = Time.time;
                    float step = Mathf.Clamp(now - todLastT, 0.01f, 1f) * 0.05f; todLastT = now;
                    float nt = was && Mathf.Abs(curT - tod) < 0.2f ? Mathf.MoveTowards(curT, tod, step) : tod;
                    if (Mathf.Abs(curT - nt) > 0.00001f) fDbgT.SetValue(EnvMan.instance, nt);
                    if (Mathf.Abs(nt - tod) > 0.0001f) moodT = Time.time + 0.05f;
                }
                if (!envTrSet) { envTrSet = true; try { var ft = AccessTools.Field(typeof(EnvMan), "m_transitionDuration"); if (ft != null && ft.FieldType == typeof(float)) ft.SetValue(EnvMan.instance, 3f); } catch { } }
                AreaFx(ar);
                string want = "";
                if (ar != null)
                {
                    want = ar.Env;
                    if (!string.IsNullOrEmpty(ar.Gust) && MoodGustOn)
                    {
                        if (gustAt < 0f) gustAt = Time.time + UnityEngine.Random.Range(ar.GustEvery0, ar.GustEvery1);
                        if (Time.time >= gustAt && gustUntil < gustAt) gustUntil = Time.time + UnityEngine.Random.Range(ar.GustLen0, ar.GustLen1);
                        if (Time.time < gustUntil) want = ar.Gust;
                        else if (gustUntil > 0f && Time.time >= gustUntil && gustAt < gustUntil) gustAt = Time.time + UnityEngine.Random.Range(ar.GustEvery0, ar.GustEvery1);
                    }
                }
                var cur = fForce != null ? fForce.GetValue(EnvMan.instance) as string : null;
                if ((cur ?? "") != want) EnvMan.instance.SetForceEnvironment(want);
            }
            catch (Exception e) { Plugin.Quiet(e, "OotPlay.Mood"); }
        }
        static System.Reflection.MethodInfo interactM;

        // Hall (wie OoT: Echo je Boden) und Schwebeteilchen je Gebiet
        static GameObject reverbGo, ambGo; static Area fxFor; static bool fxInit;
        static void AreaFx(Area ar)
        {
            if (fxInit && ar == fxFor) return;
            fxInit = true; fxFor = ar;
            try
            {
                if (reverbGo == null) { reverbGo = new GameObject("gi_oot_reverb"); UnityEngine.Object.DontDestroyOnLoad(reverbGo); var z = reverbGo.AddComponent<AudioReverbZone>(); z.minDistance = 500f; z.maxDistance = 600f; }
                var rz = reverbGo.GetComponent<AudioReverbZone>();
                AudioReverbPreset pr = AudioReverbPreset.Off;
                if (ar != null && !string.IsNullOrEmpty(ar.Reverb)) { try { pr = (AudioReverbPreset)Enum.Parse(typeof(AudioReverbPreset), ar.Reverb, true); } catch { } }
                rz.reverbPreset = pr; rz.enabled = pr != AudioReverbPreset.Off;
                // Musik ohne Hall
                try { var ms = AccessTools.Field(typeof(MusicMan), "m_musicSource"); var src = ms != null && MusicMan.instance != null ? ms.GetValue(MusicMan.instance) as AudioSource : null; if (src != null) src.bypassReverbZones = true; } catch { }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotPlay.Hall"); }
            if (ambGo != null) { UnityEngine.Object.Destroy(ambGo); ambGo = null; }
            if (ar != null && (ar.Motes > 0f || ar.Flies > 0f))
            {
                try { ambGo = AmbientFx.Spawn(ar.Flies, ar.Motes, ar.MoteCol); UnityEngine.Object.DontDestroyOnLoad(ambGo); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Teilchen"); }
            }
        }

        static void Setup(Pack pk)
        {
            forPack = pk; cfg = false; Detach();
            if (pk == null || pk.Meta == null) return;
            var o = Json.V(pk.Meta, "oot_play");
            if (o == null) return;
            modelPath = Path.Combine(pk.Dir, Json.S(o, "model", "player/link_child.glb").Replace('/', Path.DirectorySeparatorChar));
            hudDir = Path.Combine(pk.Dir, Json.S(o, "hud", "player/hud").Replace('/', Path.DirectorySeparatorChar));
            modelScale = Json.F(o, "scale", modelScale);
            var stl = Json.L(o, "stick_tip");
            if (stl != null && stl.Count >= 3) { stickTipLocal = new Vector3(-Convert.ToSingle(stl[0]), Convert.ToSingle(stl[1]), Convert.ToSingle(stl[2])); stickTipKnown = true; }
            foreach (var g in lights) if (g != null) UnityEngine.Object.Destroy(g);
            lights.Clear();
            foreach (var lj in Json.L(o, "lights") ?? new List<object>())
            {
                Vector3 lp; if (!pk.Pos(Json.V(lj, "pos"), out lp)) continue;
                var go = new GameObject("gi_oot_light"); go.transform.position = lp; UnityEngine.Object.DontDestroyOnLoad(go);
                var li = go.AddComponent<Light>(); li.type = LightType.Point; li.range = Json.F(lj, "range", 8f); li.intensity = Json.F(lj, "intensity", 1f);
                var cl = Json.L(lj, "color"); li.color = cl != null && cl.Count >= 3 ? new Color(Convert.ToSingle(cl[0]), Convert.ToSingle(cl[1]), Convert.ToSingle(cl[2])) : new Color(1f, 0.85f, 0.65f);
                // "spot": [x, y, z] = Richtung (Lichtschacht von oben), "angle": Kegel in Grad; "shadows": false = ohne Schatten (billiger)
                var sp = Json.L(lj, "spot");
                if (sp != null && sp.Count >= 3) { li.type = LightType.Spot; li.spotAngle = Json.F(lj, "angle", 40f); go.transform.rotation = Quaternion.LookRotation(new Vector3(Convert.ToSingle(sp[0]), Convert.ToSingle(sp[1]), Convert.ToSingle(sp[2]))); }
                li.shadows = Json.Has(lj, "shadows") && !Json.B(lj, "shadows") ? LightShadows.None : LightShadows.Soft;
                if (Json.B(lj, "pixel")) li.renderMode = LightRenderMode.ForcePixel;
                lights.Add(go);
            }
            // Stimmung je Gebiet: feste Uhrzeit, Valheim-Wetter, ab und zu eine Nebel-Boee
            //   "time": 0.30, "areas": [{"min": [x, z], "max": [x, z], "env": "Twilight_Clear", "gust": "Misty", "gust_every": [150, 300], "gust_len": [35, 60]}]
            LoadMood(pk, o);
            cfg = File.Exists(modelPath);
            if (!cfg) Plugin.Warn("OoT-Spielfigur fehlt: " + modelPath);
            else { Plugin.Log("OoT-Steuerung an: " + modelPath); OotHud.Load(hudDir); }
        }

        public static void Patch(Harmony h)
        {
            var uw = AccessTools.Method(typeof(Character), "UpdateWalking");
            if (uw != null) h.Patch(uw, prefix: new HarmonyMethod(typeof(OotPlay), nameof(WalkPre)));
            else Plugin.Warn("OotPlay: Character.UpdateWalking fehlt");
            var cf = AccessTools.Method(typeof(Character), "CustomFixedUpdate");
            if (cf != null) h.Patch(cf, postfix: new HarmonyMethod(typeof(OotPlay), nameof(FixedPost)) { priority = Priority.Last });
            var ur = AccessTools.Method(typeof(Character), "UpdateRotation");
            if (ur != null) h.Patch(ur, prefix: new HarmonyMethod(typeof(OotPlay), nameof(RotPre)));
            var cl = AccessTools.Method(typeof(GameCamera), "LateUpdate");
            if (cl != null) h.Patch(cl, postfix: new HarmonyMethod(typeof(OotPlay), nameof(CameraPost)) { priority = Priority.High });
            var zi = AccessTools.TypeByName("ZInput");
            foreach (var n in new[] { "GetButtonDown", "GetButton", "GetButtonUp" })
            {
                var m = AccessTools.Method(zi, n, new[] { typeof(string) });
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(OotPlay), nameof(ButtonPre)));
            }
            interactM = AccessTools.Method(typeof(Player), "Interact", new[] { typeof(GameObject), typeof(bool), typeof(bool) });
            try { airAltRef = AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude"); } catch (Exception e) { Plugin.Warn("OotPlay: m_maxAirAltitude " + e.Message); }
            var sm = AccessTools.Method(typeof(MessageHud), "ShowMessage");
            if (sm != null) h.Patch(sm, prefix: new HarmonyMethod(typeof(OotPlay), nameof(MsgPre)));
            // Valheims Wetter-Zustaende (kalt, nass, Unterschlupf ...) gar nicht erst vergeben: sonst blitzt jede Sekunde der Kaelte-Atem auf
            var ue = AccessTools.Method(typeof(Player), "UpdateEnvStatusEffects");
            if (ue != null) h.Patch(ue, prefix: new HarmonyMethod(typeof(OotPlay), nameof(EnvSePre)));
            else Plugin.Warn("OotPlay: Player.UpdateEnvStatusEffects fehlt");
            var us = AccessTools.Method(typeof(SpawnSystem), "UpdateSpawning");
            if (us != null) h.Patch(us, prefix: new HarmonyMethod(typeof(OotPlay), nameof(SpawnPre)));
        }

        // keine Valheim-Tiere/Monster im Zelda-Spiel (Wildschweine im Kokiri-Dorf ...)
        static bool SpawnPre() { return !On; }
        static bool EnvSePre() { return !On; }
        static void SweepCreatures(Player me)
        {
            if (ZNetScene.instance == null) return;
            // Valheim-Grabsteine (nach einem Tod) gibt es in OoT nicht
            try
            {
                foreach (var tb in UnityEngine.Object.FindObjectsOfType<TombStone>())
                {
                    var nv = tb != null ? tb.GetComponent<ZNetView>() : null;
                    if (nv != null && nv.IsValid() && nv.IsOwner()) ZNetScene.instance.Destroy(tb.gameObject);
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotPlay.Tomb"); }
            foreach (var c in new List<Character>(Character.GetAllCharacters()))
            {
                try
                {
                    if (c == null || c.IsPlayer() || c.IsTamed()) continue;
                    if (c.GetComponent<MonsterAI>() == null && c.GetComponent<AnimalAI>() == null) continue;
                    var nv = c.GetComponent<ZNetView>();
                    if (nv == null || !nv.IsValid() || !nv.IsOwner() || nv.GetZDO().GetBool("gi_quest")) continue;
                    ZNetScene.instance.Destroy(c.gameObject);
                }
                catch (Exception e) { Plugin.Quiet(e, "OotPlay.Sweep"); }
            }
        }

        // Valheim-Meldungen oben links ("Dir ist kalt", Fertigkeiten ...) gibt es in OoT nicht
        static bool MsgPre(MessageHud.MessageType type)
        {
            return !On;      // auch "Dir ist kalt" in der Mitte: OoT hat keine Valheim-Meldungen
        }

        // ---------- Valheim-Eingaben, die es in OoT nicht gibt ----------
        static bool ButtonPre(string name, ref bool __result)
        {
            if (!Driving) return true;
            switch (name)
            {
                case "Run": case "Attack": case "SecondaryAttack": case "Block": case "Hide": case "AutoRun": case "Sit": case "Crouch":
                case "Hotbar1": case "Hotbar2": case "Hotbar3": case "Hotbar4": case "Hotbar5": case "Hotbar6": case "Hotbar7": case "Hotbar8":
                case "Forsaken": case "Use": case "AltPlace":
                    __result = false; return false;
                case "Jump":
                    if (zHeld || state == "roll") { __result = false; return false; }       // Z: Spruenge macht OotPlay selbst
                    return true;
            }
            return true;
        }

        static bool RotPre(Character __instance, ref float __result)
        {
            if (__instance != Player.m_localPlayer || !Driving) return true;
            __result = 0f; return false;
        }

        // ---------- Figur ----------
        static void Attach(Player p)
        {
            Detach();
            owner = p;
            link = Glb.Instantiate(modelPath);
            link.name = "GI_Avatar";            // gleicher Name wie Avatar.cs: diese Renderer bleiben sichtbar
            link.transform.SetParent(p.transform, false);
            link.transform.localPosition = Vector3.zero; link.transform.localRotation = Quaternion.identity; link.transform.localScale = Vector3.one * modelScale;
            anim = link.GetComponentInChildren<Animation>();
            if (anim != null)
            {
                foreach (AnimationState st in anim)
                {
                    bool loop = st.name == "idle" || st.name == "walk" || st.name == "run" || st.name == "back_walk" || st.name.StartsWith("side_")
                                || st.name == "fall_wait" || st.name == "talk_wait" || st.name == "push" || st.name == "push_wait" || st.name == "hang" || st.name == "shield" || st.name == "bow_wait"
                                || st.name == "climb_u" || st.name == "climb_d" || st.name == "climb_sl" || st.name == "climb_sr" || st.name == "climb_w" || st.name == "swim" || st.name == "swim_wait" || st.name.StartsWith("op3_wait");
                    st.wrapMode = loop ? WrapMode.Loop : WrapMode.ClampForever;
                }
                anim.Play("idle");
            }
            foreach (var r in link.GetComponentsInChildren<Renderer>(true)) { r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.receiveShadows = true; }
            partSlots.Clear(); partState = null;
            foreach (var r in link.GetComponentsInChildren<Renderer>(true))
            {
                var ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++) { string pn; if (ms[i] != null && Glb.PartOf.TryGetValue(ms[i], out pn)) partSlots.Add(new PartSlot { R = r, I = i, M = ms[i], Part = pn }); }
            }
            // Schatten-Aufhellung (GLB "lift" = Eigenleuchten) folgt dem Umgebungslicht: tags voll, nachts/in Hoehlen kaum
            liftMats.Clear(); liftBase.Clear();
            foreach (var r in link.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.HasProperty("_EmissionColor") && m.GetColor("_EmissionColor").maxColorComponent > 0.01f && !liftMats.Contains(m)) { liftMats.Add(m); liftBase.Add(m.GetColor("_EmissionColor")); }
            yaw = p.transform.eulerAngles.y; speed = 0f; state = "idle"; clip = null; camInit = false; noFallDmgUntil = Time.time + 8f;
            if (!haveRespawn) { respawnPos = p.transform.position; respawnYaw = yaw; haveRespawn = true; }
            // Spielstand im Innenraum (Haus, Laden, Deku-Baum liegen hoch in der Luft): festhalten, bis die Modelle darunter stehen
            if (!warping && StoryUi.I != null) StoryUi.I.StartCoroutine(SettleCo(p));
            // keine Ausdauer (OoT), Sprung kleiner (Kind)
            try { p.m_jumpStaminaUsage = 0f; p.m_jumpForce = 6.4f; p.m_jumpForceForward = 0f; } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Jump"); }
            Hide(p);
            try { p.SetHealth(p.GetMaxHealth()); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Health"); }
            var b = new Bounds(); bool first = true;
            foreach (var r in link.GetComponentsInChildren<Renderer>()) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); }
            Plugin.Log("OoT-Link angelegt, Groesse " + b.size.y.ToString("F2") + " m, Clips " + (anim != null ? anim.GetClipCount() : 0));
        }

        static void Detach()
        {
            if (naviLight != null) UnityEngine.Object.Destroy(naviLight.gameObject);
            naviLight = null; OotNavi.Destroy();
            if (link != null) UnityEngine.Object.Destroy(link);
            link = null; anim = null;
            if (owner != null) Show(owner);
            owner = null;
            SetValheimHud(true);
        }

        static void Hide(Player p)
        {
            foreach (var r in p.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || r is ParticleSystemRenderer) continue;
                var t = r.transform; bool mine = false;
                while (t != null) { if (t.name == "GI_Avatar") { mine = true; break; } t = t.parent; }
                if (!mine) r.enabled = false;
            }
        }

        static void Show(Player p)
        {
            if (p == null) return;
            foreach (var r in p.GetComponentsInChildren<Renderer>(true)) if (r != null && !(r is ParticleSystemRenderer)) r.enabled = true;
        }

        static bool hudOff;
        static void SetValheimHud(bool on)
        {
            if (hudOff == !on) return;
            hudOff = !on;
            try { if (Hud.instance != null && Hud.instance.m_rootObject != null) Hud.instance.m_rootObject.SetActive(on); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Hud"); }
            try { if (Minimap.instance != null && Minimap.instance.m_smallRoot != null) Minimap.instance.m_smallRoot.SetActive(on); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Minimap"); }
        }

        static bool Typing()
        {
            return (Chat.instance != null && Chat.instance.HasFocus()) || global::Console.IsVisible() || TextInput.IsVisible() || Menu.IsVisible()
                   || InventoryGui.IsVisible() || Minimap.IsOpen() || OotMoodUi.Open || OotDev.Open || OotDev.FreeCam;
        }

        // ---------- jedes Bild (StoryUi.Update) ----------
        public static void Tick()
        {
            if (!On) { if (link != null || owner != null) Detach(); return; }
            var p = Player.m_localPlayer;
            if (p != owner || link == null) { try { Attach(p); } catch (Exception e) { Plugin.Warn("OoT-Link: " + e.Message); cfg = false; return; } }
            if (Time.time > hideT)
            {
                hideT = Time.time + 1f; Hide(p);
                // keine Valheim-Zustaende (nass, kalt, ausgeruht ...): gibt es in OoT nicht
                try { p.GetSEMan().RemoveAllStatusEffects(true); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.SE"); }
                SweepCreatures(p);
            }
            // Ortswechsel (Tuer, tp): Valheim zaehlt sonst die Hoehe vor dem Sprung als Sturzhoehe -> Link stirbt beim Ankommen
            {
                var pp = p.transform.position;
                bool jumped = (pp - lastTickPos).sqrMagnitude > 9f;
                bool tele = false; try { tele = p.IsTeleporting(); } catch { }
                if ((jumped || tele) && airAltRef != null) { airAltRef(p) = pp.y; airTopY = pp.y; noFallDmgUntil = Time.time + 3f; }
                lastTickPos = pp;
            }
            UpdateLift();
            VoidOut(p);
            HideLoading();
            OotMoodUi.Tick();
            try { OotDev.Tick(); } catch (Exception e) { Plugin.Quiet(e, "OotDev.Tick"); }
            Mood(p);
            try { OotItems.Tick(p); OotItems.RespawnTick(); } catch (Exception e) { Plugin.Quiet(e, "OotItems"); }
            UpdateParts();
            try { OotItems.GetItemTick(p, state == "get_item", stateT); } catch (Exception e) { Plugin.Quiet(e, "OotItems.GI"); }
            if (OotNpcs.AutoTalk == null) OotNpcs.AutoTalk = t => { Text = t; SetState("talk"); speed = 0f; OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.6f); };
            OotNpcs.Tick(p);
            OotEnemies.Tick(p);
            OotDungeon.Tick(p);
            OotDekuTree.Tick(p);
            OotYdanObj.Tick(p);
            try { if (Driving) OotWake.Tick(p); } catch (Exception e) { Plugin.Quiet(e, "OotWake"); }
            if (OotWake.Active && state != "wake") { SetState("wake"); clip = null; }
            SlingTick(p);
            StickTick(p);
            bool drive = Driving;
            SetValheimHud(false);
            link.SetActive(true);
            // Eingabe
            zHeld = false; aDown = false; aHeld = false; jumpDown = false; bDown = false; stick = Vector2.zero; walkKey = false; rHeld = false; c1Down = false; CUpDown = false; c3Down = false;
            if (drive && !Typing())
            {
                float x = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
                float y = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
                try { float jx = ZInput.GetJoyLeftStickX(), jy = -ZInput.GetJoyLeftStickY(); if (Mathf.Abs(jx) + Mathf.Abs(jy) > 0.2f) { x = jx; y = jy; } } catch { }
                stick = Vector2.ClampMagnitude(new Vector2(x, y), 1f);
                zHeld = Input.GetMouseButton(1);
                aDown = Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
                aHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                jumpDown = Input.GetKeyDown(KeyCode.Space);
                bDown = Input.GetMouseButtonDown(0);
                walkKey = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.LeftAlt);
                rHeld = Input.GetKey(KeyCode.R) || Input.GetMouseButton(2);           // R = Schild (OoT: R-Taste)
                c1Down = Input.GetKeyDown(KeyCode.Alpha1);                             // C-links: Deku-Stab
                CUpDown = Input.GetKeyDown(KeyCode.Q);                                 // C-oben: Navi
                c3Down = Input.GetKeyDown(KeyCode.Alpha3);                             // C-rechts: Feen-Schleuder
            }
            stickYaw = camYaw + Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
            if (drive) RemoteInput();
            zDown = zHeld && !zPrev; zPrev = zHeld;
            CombatTick(p);
            ALabel = null;
            if (drive) Actions(p);
            stickWasOut = stick.magnitude > 0.5f;
            OotShop.Tick();
            Animate(p);
            if (state == "wake" && link != null) { link.transform.position = OotWake.ModelPos; link.transform.rotation = Quaternion.Euler(0f, OotWake.ModelYaw, 0f); }
        }

        // Aufwach-Szene (OotWake): Spielfigur steht schon am Endpunkt, das Modell liegt im Bett
        public static void BeginWake(Vector3 end, float y)
        {
            var p = Player.m_localPlayer; Text = null; StickOut = false;
            if (p != null) { var b = bodyRef(p); p.transform.position = end; if (b != null) { b.position = end; b.velocity = Vector3.zero; } }
            yaw = y; speed = 0f; SetState("wake"); clip = null;
        }
        public static void EndWake(Vector3 end, float y)
        {
            if (link != null) { link.transform.localPosition = Vector3.zero; link.transform.localRotation = Quaternion.identity; }
            var p = Player.m_localPlayer; Text = null;
            if (p != null) { var b = bodyRef(p); p.transform.position = end; if (b != null) { b.position = end; b.velocity = Vector3.zero; b.useGravity = true; } }
            yaw = y; camYaw = y; camPitch = 14f; camInit = true; speed = 0f; SetState("idle"); noFallDmgUntil = Time.time + 3f;
            respawnPos = end; respawnYaw = y; haveRespawn = true;
        }
        public static bool Warping { get { return warping; } }
        // ---------- Kanten hochklettern (Player_ActionHandler_Climb, vereinfacht) ----------
        static Vector3 ledgeTop, ledgeN, ledgeFrom, ledgeTo; static float ledgeDh, ledgeDur; static bool ledgeHi;
        static bool LedgeAhead(Player p, out Vector3 top, out Vector3 n, out float dh)
        {
            top = Vector3.zero; n = Vector3.zero; dh = 0f;
            var pos = p.transform.position; var f = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            int m = GroundMask(); RaycastHit h, t;
            if (!Physics.Raycast(pos + Vector3.up * 0.45f, f, out h, 0.7f, m, QueryTriggerInteraction.Ignore)) return false;
            n = h.normal; n.y = 0f; if (n.sqrMagnitude < 0.25f) return false; n.Normalize();
            if (Vector3.Angle(-n, f) > 40f) return false;
            Vector3 cp, cn; int ct;
            if (OotDungeon.ClimbAt(pos + Vector3.up * 0.9f, f, 0.8f, out cp, out cn, out ct)) return false;      // Ranken/Leiter: klettern statt Kante
            float maxH = 71f * 0.033f;
            var probe = h.point - n * 0.25f; probe.y = pos.y + maxH + 0.35f;
            if (!Physics.Raycast(probe, Vector3.down, out t, maxH + 0.35f, m, QueryTriggerInteraction.Ignore)) return false;
            if (t.normal.y < 0.7f) return false;
            dh = t.point.y - pos.y;
            if (dh < 0.5f || dh > maxH) return false;
            if (Physics.Raycast(t.point + Vector3.up * 0.05f, Vector3.up, 1.3f, m, QueryTriggerInteraction.Ignore)) return false;
            if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.up, dh + 0.4f, m, QueryTriggerInteraction.Ignore)) return false;
            top = t.point;
            return true;
        }
        // Kante in der Luft (Player_UpdateWallCheck + Player_Action_8084411C): Wand vor Link, Oberkante bis 150 Einheiten hoeher,
        // Oberkante > 70 * 11/17 Einheiten ueber dem Boden unter Link (sonst landet er einfach)
        static bool AirLedge(Player p, out Vector3 top, out Vector3 n)
        {
            top = Vector3.zero; n = Vector3.zero;
            var pos = p.transform.position; var f = new Vector3(airVel.x, 0f, airVel.z); if (f.sqrMagnitude < 0.01f) return false; f.Normalize();
            int m = GroundMask(); RaycastHit h, t, fl;
            if (!Physics.Raycast(pos + Vector3.up * 0.6f, f, out h, 0.55f, m, QueryTriggerInteraction.Ignore)) return false;
            n = h.normal; n.y = 0f; if (n.sqrMagnitude < 0.25f) return false; n.Normalize();
            if (Vector3.Angle(-n, f) > 50f) return false;
            Vector3 cp, cn; int ct;
            if (OotDungeon.ClimbAt(pos + Vector3.up * 0.9f, f, 0.8f, out cp, out cn, out ct)) return false;      // Ranken: dort klettern
            float maxH = 150f * 0.033f;
            var probe = h.point - n * 0.25f; probe.y = pos.y + maxH + 0.3f;
            if (!Physics.Raycast(probe, Vector3.down, out t, maxH + 0.3f, m, QueryTriggerInteraction.Ignore)) return false;
            if (t.normal.y < 0.7f || t.point.y < pos.y + 0.3f) return false;
            float below = Physics.Raycast(pos + Vector3.up * 0.2f, Vector3.down, out fl, 40f, m, QueryTriggerInteraction.Ignore) ? fl.point.y : pos.y - 40f;
            if (t.point.y - below < 70f * 11f / 17f * 0.033f) return false;
            if (Physics.Raycast(t.point + Vector3.up * 0.05f, Vector3.up, 1.3f, m, QueryTriggerInteraction.Ignore)) return false;
            // echte Kante: ueber der Oberkante ist zu Link hin frei (sonst geht die Wand weiter und die "Kante" liegt in einem
            // Hohlraum dahinter, z. B. zwischen den Stockwerken am Loch im 1. Stock)
            if (Physics.Raycast(t.point + Vector3.up * 0.4f, n, 0.6f, m, QueryTriggerInteraction.Ignore)) return false;
            top = t.point;
            return true;
        }
        // Schiebeblock (OotYdanObj): Link drueckt -> Schiebe-Animation; Stick in Weltrichtung
        public static bool Pushing;
        // Objekte mit A-Aktion (Tueren im Deku-Baum): Beschriftung + Aktion, jedes Bild neu gesetzt
        public static string ObjALabel; public static Action ObjAPress;
        // Durch eine Tuer gehen (wie Player_Action_OpenDoor: Link laeuft von selbst hindurch)
        static Vector3 walkTo, walkFrom; static float walkUntil, walkStart;
        public static bool WalkingThrough { get { return state == "doorwalk"; } }
        public static void WalkThrough(Vector3 to, float dur)
        {
            var p = Player.m_localPlayer; walkFrom = p != null ? p.transform.position : to; walkTo = new Vector3(to.x, walkFrom.y, to.z);
            walkStart = Time.time + 0.45f; walkUntil = walkStart + Mathf.Max(0.5f, (walkTo - walkFrom).magnitude / 1.9f); SetState("doorwalk");
        }
        public static Vector3 StickWorld { get { return stick.magnitude > 0.2f ? Quaternion.Euler(0f, stickYaw, 0f) * Vector3.forward * stick.magnitude : Vector3.zero; } }

        static void Actions(Player p)
        {
            bool ground = p.IsOnGround();
            // A-Knopf: erst Aktion in der Naehe (Tuer, Stelle, Figur), sonst Rolle
            var hov = p.GetHoverObject();
            int ik = Interact.Kind;
            if (ik == 2) ALabel = "Open"; else if (ik == 1) ALabel = "Check";
            else if (hov != null && hov.GetComponentInParent<Interactable>() != null) ALabel = hov.GetComponentInParent<global::Door>() != null ? "Open" : "Speak";
            // Aufwach-Szene: nur Texte weiterblaettern
            if (state == "wake")
            {
                ALabel = OotWake.TextOpen ? "Next" : null;
                if (aDown || bDown) OotWake.Press();
                Text = OotWake.Text;
                if (!OotWake.Active) { Text = null; if (state == "wake") SetState("idle"); }
                return;
            }
            // Textbox offen (Gegenstand erhalten): A/B blaettert weiter
            if (state == "get_item")
            {
                ALabel = "Next";
                if ((aDown || bDown) && stateT > 0.8f)
                {
                    textPage++; stateT = 0.4f;
                    var tx = OotItems.GiText;
                    if (tx == null || textPage >= tx.Length) { Text = null; if (!string.IsNullOrEmpty(OotItems.GiFlag)) Story.Flags.Add(OotItems.GiFlag); OotItems.ApplyGet(OotItems.GiKey); Story.Save(); swordOutUntil = 0f; OotSfx.Play("NA_SE_SY_MESSAGE_END", 0.8f);
                        if (OotShop.Active) { OotShop.FanfareDone(); SetState("shop"); Text = ChoiceText(OotShop.Text, OotShop.Choice, OotShop.ChoiceSel); } else SetState("idle"); }
                    else { Text = tx[textPage]; OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.8f); }
                }
                return;
            }
            // Gespraech mit einer Figur: A/B blaettert weiter
            if (state == "talk")
            {
                ALabel = "Next";
                if ((aDown || bDown) && stateT > 0.25f)
                {
                    var nx = OotNpcs.Advance();
                    if (nx == null) { Text = null; SetState("idle"); OotSfx.Play("NA_SE_SY_MESSAGE_END", 0.8f); }
                    else { Text = nx; stateT = 0f; OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.8f); }
                }
                return;
            }
            // Gespraech aus einem Ablauf (Deku-Baum, Navi): Seiten blaettern, am Ende ggf. Auswahl
            if (state == "dialog")
            {
                ALabel = dlgChoice > 0 && dlgPage >= dlgPages.Count - 1 ? "Decide" : "Next";
                bool last = dlgPage >= dlgPages.Count - 1;
                if (last && dlgChoice > 0 && stick.magnitude > 0.5f && !stickWasOut && Mathf.Abs(stick.y) > 0.5f) { dlgSel = (dlgSel + (stick.y < 0 ? 1 : dlgChoice - 1)) % dlgChoice; OotSfx.Play("NA_SE_SY_CURSOR", 0.6f); }
                if ((aDown || (bDown && dlgChoice == 0)) && stateT > 0.25f)
                {
                    if (!last) { dlgPage++; stateT = 0f; OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.8f); }
                    else
                    {
                        var done = dlgDone; int sel = dlgSel; dlgDone = null; Text = null; dlgCam = false; SetState("idle");
                        OotSfx.Play(dlgChoice > 0 ? "NA_SE_SY_DECIDE" : "NA_SE_SY_MESSAGE_END", 0.8f);
                        if (done != null) done(sel);
                        return;
                    }
                }
                Text = ChoiceText(dlgPages[dlgPage], last ? dlgChoice : 0, dlgSel);
                return;
            }
            // Laden: Regale ansehen, kaufen
            if (state == "shop")
            {
                ALabel = OotShop.Text != null && OotShop.Choice == 0 ? "Next" : "Decide";
                bool edge = stick.magnitude > 0.5f && !stickWasOut;
                if (!OotShop.Update(stick, aDown, bDown, edge)) { Text = null; SetState("idle"); OotSfx.Play("NA_SE_SY_MESSAGE_END", 0.8f); }
                else if (state == "shop") Text = ChoiceText(OotShop.Text, OotShop.Choice, OotShop.ChoiceSel);      // (Kauf mit Fanfare: GiveItem hat schon umgeschaltet)
                return;
            }
            if (state == "chest" || state == "slash" || state == "hurt" || state == "die" || state == "shield" || state == "ledge" || state == "hang" || state == "doorwalk" || state == "aim") return;      // Zielen: B schiesst (SlingTick), kein Schwert
            if (ground && state != "roll" && OotShop.NearKeeper(p))
            {
                ALabel = "Speak";
                if (aDown && speed < 3.5f) { OotShop.Begin(); Text = OotShop.Text; SetState("shop"); speed = 0f; return; }
            }
            // Figur vor Link: A = Ansprechen (Original-Texte)
            if (ground && state != "roll" && OotNpcs.CanTalk(p))
            {
                ALabel = "Speak";
                if (aDown && speed < 3.5f)
                {
                    var t = OotNpcs.StartTalk(p);
                    if (t != null) { Text = t; SetState("talk"); OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.6f); }
                    return;
                }
            }
            // Truhe vor Link: A oeffnet (Original-Animation, Deckel klappt auf, Schwert hochhalten)
            if (OotItems.NearChest(p) && ground)
            {
                ALabel = "Open";
                if (aDown) { SetState("chest"); speed = 0f; return; }
            }
            // R: Schild hochnehmen
            if (rHeld && HasShield && ground && (state == "idle" || state == "move" || state == "land_short")) { SetState("shield"); return; }
            // B: Schwertschlag (abwechselnd waagrecht/schraeg wie in OoT)
            if (bDown && (OotItems.HasSword || StickOut) && ground && state != "roll")
            {
                slashN++; hitDone = false; swordOutUntil = Time.time + 3.5f; SetState("slash");
                return;
            }
            if (ObjALabel != null && ObjAPress != null && ground && (state == "idle" || state == "move"))
            {
                ALabel = ObjALabel;
                if (aDown) { var a = ObjAPress; ObjAPress = null; a(); return; }
            }
            // Kante vor Link (bis 71 Einheiten hoch, ageProperties unk_0C Kind): A = Klettern
            if (ground && ik == 0 && !zHeld && (state == "idle" || state == "move") && LedgeAhead(p, out ledgeTop, out ledgeN, out ledgeDh))
            {
                ALabel = "Climb";
                if (aDown)
                {
                    ledgeHi = ledgeDh > 47f * 0.033f;
                    ledgeDur = ledgeHi ? 37f / 30f : 27f / 30f;
                    ledgeFrom = new Vector3(p.transform.position.x, ledgeTop.y + 0.02f, p.transform.position.z);
                    ledgeTo = ledgeTop - ledgeN * 0.32f; ledgeTo.y = ledgeTop.y + 0.02f;
                    yaw = Mathf.Atan2(-ledgeN.x, -ledgeN.z) * Mathf.Rad2Deg; speed = 0f;
                    noFallDmgUntil = Time.time + 2f; SetState("ledge");
                    OotSfx.Play(ledgeHi ? "NA_SE_VO_LI_CLIMB_END_KID" : "NA_SE_PL_CLIMB_CLIFF", 0.8f);
                    return;
                }
            }
            if (aDown && state != "roll" && state != "sprint")
            {
                if (ik != 0 && speed < 2.5f) Interact.Press();
                else if (ALabel != null && hov != null && speed < 2.5f && interactM != null) { try { interactM.Invoke(p, new object[] { hov, false, false }); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Interact"); } }
                else if (ground && (stick.magnitude > 0.2f || speed > 1f)) { SetState("roll"); sprintArmed = true; }
            }
            // Sprung nur auf Tastendruck (Leertaste, kein Auto-Sprung): Valheim springt, hier die passende Original-Pose
            // (aus dem Lauf der Weitsprung, sonst Hocksprung)
            if (!zHeld && jumpDown && ground && state != "roll") { SetState(speed > 3f ? "run_jump" : "jump"); airT = 0f; }
            // Z-Spruenge (Valheims Sprung ist dann gesperrt)
            if (zHeld && jumpDown && ground && state != "roll" && !warping)
            {
                var body = bodyRef(p);
                Quaternion q = Quaternion.Euler(0f, yaw, 0f);
                if (stick.y < -0.5f) { airVel = q * Vector3.back * 6.5f; SetState("backflip"); if (body != null) body.velocity = new Vector3(airVel.x, 7f, airVel.z); }
                else if (Mathf.Abs(stick.x) > 0.5f) { airVel = q * (stick.x < 0 ? Vector3.left : Vector3.right) * 7f; SetState(stick.x < 0 ? "hop_l" : "hop_r"); if (body != null) body.velocity = new Vector3(airVel.x, 4.5f, airVel.z); }
                else { airVel = q * Vector3.forward * Mathf.Max(speed, 3f); SetState("jump"); if (body != null) body.velocity = new Vector3(airVel.x, 6.4f, airVel.z); }
                airborne = true; airT = 0f;
            }
        }

        static float lockMix; static Vector3 lockAt; static bool hurtBack, giChest, stickWasOut; static float giYaw, shopCamK; static Vector3 shopLook;

        // Auswahl in der Textbox (TWO_CHOICE/THREE_CHOICE): die letzten Zeilen sind die Optionen, gruener Pfeil vor der gewaehlten
        static string ChoiceText(string t, int n, int sel)
        {
            if (t == null || n <= 0) return t;
            var lines = t.Split('\n');
            int first = Mathf.Max(0, lines.Length - n);
            for (int i = first; i < lines.Length; i++)
                lines[i] = (i - first == sel ? "<color=#46ff50>\u25B6</color> " : "    ") + lines[i];
            return string.Join("\n", lines);
        }

        /// <summary>Gegenstand erhalten ohne Truhe (erstes Aufsammeln, Shop, Geschenk): Link dreht sich zur Kamera, haelt ihn hoch, Original-Text.</summary>
        public static void GiveItem(string key, string flag = null)
        {
            OotItems.GiKey = key; OotItems.GiText = OotItems.Pages(key); OotItems.GiFlag = flag;
            giChest = false; giYaw = camYaw + 180f; speed = 0f; textPage = 0;
            Text = OotItems.GiText != null && OotItems.GiText.Length > 0 ? OotItems.GiText[0] : null;
            SetState("get_item");
        }
        static float YawTo(Player p, Vector3 w) { var d = w - p.transform.position; d.y = 0f; return d.sqrMagnitude > 1e-4f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : yaw; }

        // Kampf: Ziele, Leben, Treffer (OotCombat); Valheims eigenes Leben bleibt voll
        static void CombatTick(Player p)
        {
            if (OotItems.FirstGet == null) OotItems.FirstGet = k => GiveItem(k);
            if (OotShop.GiveFanfare == null) OotShop.GiveFanfare = k => { GiveItem(k, k == "shield" ? ShieldFlag : null); return true; };
            if (OotEnemies.GotStick == null) OotEnemies.GotStick = k => OotItems.Take(k);
            if (OotCombat.OnHurt == null)
            {
                OotCombat.OnHurt = (from, n) =>
                {
                    var pl = Player.m_localPlayer; if (pl == null) return;
                    var d = pl.transform.position - from; d.y = 0f; if (d.sqrMagnitude < 1e-4f) d = -pl.transform.forward;
                    hurtBack = Vector3.Dot(d.normalized, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward) > 0f;
                    knock = d.normalized * 6f; Text = null; SetState("hurt");
                };
                OotCombat.OnDie = () => { Text = null; SetState("die"); };
                OotCombat.OnKnock = (from, dir) =>
                {
                    var d = dir; d.y = 0f; if (d.sqrMagnitude < 1e-4f) d = -(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward);
                    hurtBack = Vector3.Dot(d.normalized, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward) > 0f;
                    knock = d.normalized * 6f; Text = null; SetState("hurt");
                };
                OotCombat.OnBlock = () => { knock = Vector3.zero; };
                OotCombat.ShieldFaces = from =>
                {
                    var pl = Player.m_localPlayer; if (pl == null || state != "shield") return false;
                    var d = from - pl.transform.position; d.y = 0f;
                    return Vector3.Angle(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward, d) < 75f;
                };
            }
            try { OotCombat.Tick(p, yaw, zHeld, zDown); } catch (Exception e) { Plugin.Quiet(e, "OotCombat"); }
            try { if (p.GetHealth() < p.GetMaxHealth()) p.SetHealth(p.GetMaxHealth()); } catch { }
        }

        // Game Over vereinfacht: schwarz, am letzten Eingang mit vollen Herzen weiter (OoT: "Continue" mit 3 Herzen)
        static void Respawn()
        {
            OotCombat.Reset();
            if (!WarpTo(respawnPos, respawnYaw)) SetState("idle");
        }

        static float climbDropT; public static float noFallDmgUntil;
        static List<string> dlgPages = new List<string>(); static int dlgPage, dlgChoice, dlgSel; static Action<int> dlgDone;
        static bool dlgCam; static Vector3 dlgCamPos, dlgCamLook;
        public static bool InDialog { get { return state == "dialog"; } }
        public static bool Calm { get { return state == "idle" || state == "move" || state == "land_short"; } }    // fuer Navi-Hinweise: am Boden, nicht schwimmend/kletternd
        /// <summary>Gespraech starten: Seiten (letzte ggf. mit Auswahl 2/3), Kamera optional; done(Auswahl) am Ende.</summary>
        public static void StartDialog(List<string> pages, int choice, Action<int> done, Vector3? camPos = null, Vector3? camLook = null)
        {
            if (pages == null || pages.Count == 0) { if (done != null) done(0); return; }
            dlgPages = pages; dlgPage = 0; dlgChoice = choice; dlgSel = 0; dlgDone = done;
            dlgCam = camPos.HasValue && camLook.HasValue; if (dlgCam) { dlgCamPos = camPos.Value; dlgCamLook = camLook.Value; }
            speed = 0f; Text = ChoiceText(pages[0], pages.Count == 1 ? choice : 0, 0); SetState("dialog");
            OotSfx.Play("NA_SE_SY_MESSAGE_PASS", 0.6f);
        }
        static int dive; static float diveT;       // 0 = an der Oberflaeche, 1 runter, 2 unten, 3 hoch
        public static bool Diving { get { return dive != 0; } }
        static float camLift;
        // freie Kamera-Entfernung entlang rot (wie oben: weiche Pflanzen zaehlen nicht)
        static float CamFree(Vector3 target, Quaternion rot)
        {
            float d = camDist;
            foreach (var ch in Physics.SphereCastAll(target, 0.25f, rot * Vector3.back, camDist, GroundMask(), QueryTriggerInteraction.Ignore))
            {
                if (ch.distance <= 0f || ch.distance - 0.1f >= d || SoftForCamera(ch.collider)) continue;
                d = Mathf.Max(0.6f, ch.distance - 0.1f);
            }
            return d;
        }
        static void ClimbSwim(Player p, Rigidbody body, float dt)
        {
            if (state != "swim") dive = 0;
            var pos = p.transform.position;
            if (state == "climb_end")
            {
                // an der Kante hochziehen (Klettern oben / aus dem Wasser)
                body.useGravity = false;
                float k = Mathf.Clamp01(stateT / 0.75f); k = k * k * (3f - 2f * k);
                var t = Vector3.Lerp(endFrom, endTo, k);
                body.position = t; p.transform.position = t; body.velocity = Vector3.zero; wantH = Vector2.zero; haveWant = true; wantY = 0f; wantYOn = true;
                if (stateT > 0.8f) { body.useGravity = true; wantYOn = false; SetState("idle"); }
                SetRot(p, body); return;
            }
            if (state == "climb")
            {
                body.useGravity = false;
                yaw = Mathf.Atan2(-climbN.x, -climbN.z) * Mathf.Rad2Deg;
                var right = Vector3.Cross(climbN, Vector3.up).normalized;
                float up = Mathf.Abs(stick.y) > 0.25f ? Mathf.Sign(stick.y) : 0f, side = Mathf.Abs(stick.x) > 0.35f ? Mathf.Sign(stick.x) : 0f;
                if (up != 0f) side = 0f;
                var v = Vector3.up * up * 1.15f + right * side * 0.85f;
                if (aDown) { body.useGravity = true; climbDropT = Time.time; airborne = true; airT = 0f; airVel = climbN * 0.8f; airTopY = pos.y; SetState("fall"); return; }
                Vector3 cp, cn; int ct;
                var chest = pos + Vector3.up * 0.9f;
                bool wall = OotDungeon.ClimbAt(chest, -climbN, 1.0f, out cp, out cn, out ct);
                if (wall) { climbN = cn; float dist = Vector3.Dot(chest - cp, cn); v += cn * (0.32f - dist) * 5f; }
                Vector3 hp, hn; int ht;
                bool headWall = OotDungeon.ClimbAt(pos + Vector3.up * 1.5f, -climbN, 1.0f, out hp, out hn, out ht);
                if (up > 0f && !headWall)
                {
                    RaycastHit h;
                    var probe = pos + Vector3.up * 2.3f - climbN * 0.75f;
                    endFrom = pos; endTo = Physics.Raycast(probe, Vector3.down, out h, 2.6f, GroundMask(), QueryTriggerInteraction.Ignore) ? h.point + Vector3.up * 0.05f : pos + Vector3.up * 1.5f - climbN * 0.7f;
                    SetState("climb_end"); return;
                }
                if (!wall && up <= 0f) { body.useGravity = true; climbDropT = Time.time; airborne = true; airT = 0f; airVel = Vector3.zero; airTopY = pos.y; SetState("fall"); return; }
                if (up < 0f && Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, 0.25f, GroundMask(), QueryTriggerInteraction.Ignore)) { body.useGravity = true; climbDropT = Time.time; SetState("idle"); return; }
                climbAnimT += dt * (up != 0f || side != 0f ? 1f : 0f);
                body.velocity = v; wantH = new Vector2(v.x, v.z); haveWant = true; wantY = v.y; wantYOn = true; speed = 0f;
                if (airAltRef != null) airAltRef(p) = pos.y;
                airTopY = pos.y;
                SetRot(p, body); return;
            }
            // Schwimmen: Kopf ueber Wasser, langsamer, Stick lenkt
            float wy;
            if (!OotDungeon.WaterAt(pos + Vector3.up * 0.3f, out wy) && dive == 0) { body.useGravity = true; SetState("idle"); return; }
            if (dive != 0 && !OotDungeon.WaterAt(pos + Vector3.up * 0.3f, out wy)) { dive = 0; body.useGravity = true; SetState("idle"); return; }
            body.useGravity = false;
            float vy = Mathf.Clamp((wy - 1.08f - pos.y) * 3f, -5f, 2.2f);
            // Tauchen (A): ohne Schuppe hoechstens 120 Einheiten tief (z_player.c D_80854784[0]), unten kurz halten, dann auftauchen
            if (aDown && dive == 0 && pos.y > wy - 1.4f) { dive = 1; diveT = 0f; OotSfx.Play("NA_SE_PL_DIVE_BUBBLE", 0.8f, pos); OotSfx.Play("NA_SE_PL_FACE_UP", 0.6f, pos); }
            if (dive != 0)
            {
                diveT += dt;
                bool bottom = Physics.Raycast(pos + Vector3.up * 0.1f, Vector3.down, 0.2f, GroundMask(), QueryTriggerInteraction.Ignore);
                if (dive == 1) { vy = diveT < 0.2f ? -1.2f : -2.8f; if (wy - pos.y >= 120f * 0.033f || bottom || diveT > 2.2f) { dive = 2; diveT = 0f; } }
                if (dive == 2) { vy = bottom ? 0f : -0.4f; if (diveT > 0.45f) { dive = 3; diveT = 0f; } }
                if (dive == 3) { vy = 2.4f; if (pos.y >= wy - 1.15f) { dive = 0; OotSfx.Play("NA_SE_PL_FACE_UP", 0.7f, pos); } }
            }
            if (stick.magnitude > 0.15f) { yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, 300f * dt); speed = Mathf.MoveTowards(speed, (dive != 0 ? 1.6f : 2.4f) * stick.magnitude, 4f * dt); }
            else speed = Mathf.MoveTowards(speed, 0f, 4f * dt);
            var fw = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            // an Land: vorn Boden knapp ueber dem Wasser -> hochziehen
            if (stick.magnitude > 0.5f && pos.y > wy - 1.4f && dive == 0)
            {
                RaycastHit h;
                if (Physics.Raycast(new Vector3(pos.x, wy + 1.5f, pos.z) + fw * 0.75f, Vector3.down, out h, 1.9f, GroundMask(), QueryTriggerInteraction.Ignore) && h.point.y > wy - 0.3f && h.point.y < wy + 1.3f)
                { endFrom = pos; endTo = h.point + Vector3.up * 0.05f; SetState("climb_end"); return; }
            }
            if (p.IsOnGround() && pos.y > wy - 0.7f && dive == 0) { body.useGravity = true; SetState("idle"); return; }
            var hv = fw * speed;
            body.velocity = new Vector3(hv.x, vy, hv.z); wantH = new Vector2(hv.x, hv.z); haveWant = true; wantY = vy; wantYOn = true;
            if (airAltRef != null) airAltRef(p) = pos.y;
            airTopY = pos.y; airborne = false;
            if (StickOut && stickFire > 0f) stickFire = 0f;         // Wasser loescht den Stab
            SetRot(p, body);
        }

        // ---------- Deku-Stab (C-links, Taste 1) ----------
        static Transform handBone; static Vector3 stickTipLocal = new Vector3(0f, 0f, 0f); static bool stickTipKnown;
        static GameObject stickFx;
        public static Vector3 StickTip()
        {
            var p = Player.m_localPlayer;
            if (handBone == null && link != null) foreach (var t in link.GetComponentsInChildren<Transform>(true)) if (t.name == "limb15") { handBone = t; break; }
            if (handBone != null && stickTipKnown) return handBone.TransformPoint(stickTipLocal);
            if (p == null) return Vector3.zero;
            return p.transform.position + Vector3.up * 1.0f + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * 0.9f;
        }
        // Feen-Schleuder (C-rechts = Taste 3): zielen ueber die Schulter, Linksklick/B schiesst einen Samen, 3 oder Z beendet
        public static string LastShot = "-";
        static float aimYaw, aimPitch;
        // Samen wie EnArrow (ARROW_SEED): 80 Einheiten/Bild, 15 Bilder, in den letzten 7 Bildern Schwerkraft -0,4; Bild =
        // funkelnder Stern (gEffSparklesDL: weiss mit tuerkisem Rand, pulsiert, dreht sich), Wand: Steinchen + NA_SE_IT_SLING_REFLECT
        class SeedShot { public Vector3 P, P0, V; public int F; public float Acc; public Transform Q; public bool Puff; public Vector3[] Pv; public Transform[] Pq; public float T; }
        static readonly List<SeedShot> seedShots = new List<SeedShot>();
        static Material seedMat, puffMat; static Mesh seedQuad; static Texture2D sparkTex, ringTex;
        public static Texture2D RingTex()
        {
            if (ringTex != null) return ringTex;
            int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r = Mathf.Sqrt(dx * dx + dy * dy);
                float ring = Mathf.Clamp01(1f - Mathf.Abs(r - 0.78f) / 0.09f), dot = Mathf.Clamp01(1f - r / 0.14f);
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Max(ring * 0.85f, dot)));
            }
            t.Apply(); return ringTex = t;
        }
        static Texture2D SparkTex()
        {
            if (sparkTex != null) return sparkTex;
            int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, false); t.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f, r2 = dx * dx + dy * dy;
                float glow = Mathf.Exp(-r2 * 7f), star = Mathf.Max(Mathf.Exp(-Mathf.Abs(dx) * 22f) * Mathf.Exp(-Mathf.Abs(dy) * 2.6f), Mathf.Exp(-Mathf.Abs(dy) * 22f) * Mathf.Exp(-Mathf.Abs(dx) * 2.6f));
                t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(glow * 0.85f + star)));
            }
            t.Apply(); return sparkTex = t;
        }
        static Transform SeedQuad(Material m, float size)
        {
            if (seedQuad == null)
            {
                seedQuad = new Mesh();
                seedQuad.vertices = new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) };
                seedQuad.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
                seedQuad.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
                seedQuad.RecalculateBounds();
            }
            var g = new GameObject("oot_seed"); g.AddComponent<MeshFilter>().sharedMesh = seedQuad;
            var r = g.AddComponent<MeshRenderer>(); r.material = m; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
            g.transform.localScale = Vector3.one * size;
            return g.transform;
        }
        static void SeedFly(float dt)
        {
            var cam = Camera.main;
            for (int i = seedShots.Count - 1; i >= 0; i--)
            {
                var s = seedShots[i];
                if (s.Puff)
                {
                    // Steinchen (EffectSsStone1): kurz auseinander, fallen, verblassen
                    s.T += dt; bool alive = s.T < 0.45f;
                    for (int k = 0; k < s.Pq.Length; k++)
                    {
                        if (s.Pq[k] == null) continue;
                        s.Pv[k] += Vector3.down * 9f * dt; s.Pq[k].position += s.Pv[k] * dt;
                        if (cam != null) s.Pq[k].rotation = Quaternion.LookRotation(cam.transform.forward);
                        var mr = s.Pq[k].GetComponent<Renderer>(); if (mr != null && mr.material.HasProperty("_TintColor")) mr.material.SetColor("_TintColor", new Color(0.42f, 0.36f, 0.27f, 0.6f * (1f - s.T / 0.45f)));
                        if (!alive) UnityEngine.Object.Destroy(s.Pq[k].gameObject);
                    }
                    if (!alive) seedShots.RemoveAt(i);
                    continue;
                }
                s.Acc += dt; bool gone = false;
                while (s.Acc >= 0.05f && !gone)
                {
                    s.Acc -= 0.05f;
                    s.P0 = s.P; s.P += s.V * 0.033f; s.F++;                 // 1 Einheit = 0,033 m, 20 Bilder/s
                    if (s.F > 8) s.V.y -= 0.4f;
                    var d = s.P - s.P0; float len = d.magnitude; if (len < 1e-4f) continue;
                    var ray = new Ray(s.P0, d / len);
                    float best = len; OotCombat.Shootable hit = null; RaycastHit rh; bool wall = false;
                    if (Physics.Raycast(ray, out rh, len, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore)) { best = rh.distance; wall = true; }
                    foreach (var sh in OotCombat.Shootables.ToArray())
                    {
                        float t = -1f; try { t = sh.Test(ray, best); } catch { }
                        if (t >= 0f && t < best) { best = t; hit = sh; wall = false; }
                    }
                    var w = ray.GetPoint(best);
                    if (hit != null)
                    {
                        LastShot = "treffer bei " + w.ToString("F2") + " nach " + s.F + " Bildern";
                        try { hit.Hit(w, ray.direction); } catch (Exception e) { Plugin.Quiet(e, "Schleuder"); }
                        gone = true;
                    }
                    else if (wall)
                    {
                        LastShot = "wand " + (rh.collider != null ? rh.collider.name : "?") + " bei " + w.ToString("F2") + " nach " + s.F + " Bildern";
                        OotSfx.Play("NA_SE_IT_SLING_REFLECT", 0.7f, w);
                        SeedPuff(w - ray.direction * 0.05f, rh.normal);
                        gone = true;
                    }
                    else if (s.F >= 15) { LastShot = "verfehlt"; gone = true; }
                    if (gone) s.P = w;
                }
                if (gone) { if (s.Q != null) UnityEngine.Object.Destroy(s.Q.gameObject); seedShots.RemoveAt(i); continue; }
                if (s.Q != null)
                {
                    s.Q.position = Vector3.Lerp(s.P0, s.P, Mathf.Clamp01(s.Acc / 0.05f));
                    float spin = (Time.time * 20f * 4000f / 65536f * 360f) % 360f;
                    if (cam != null) s.Q.rotation = Quaternion.LookRotation(cam.transform.forward) * Quaternion.Euler(0f, 0f, spin);
                    float a = Mathf.Cos(Time.time * 20f * 5000f / 65536f * Mathf.PI * 2f) * 0.5f + 0.5f;
                    var mr = s.Q.GetComponent<Renderer>(); if (mr != null && mr.material.HasProperty("_TintColor")) mr.material.SetColor("_TintColor", new Color(0.55f, 0.95f, 1f, 0.55f + 0.45f * a));
                }
            }
        }
        static void SeedPuff(Vector3 at, Vector3 n)
        {
            if (puffMat == null) puffMat = FireFx.Make(SparkTex());
            var s = new SeedShot { Puff = true, Pq = new Transform[5], Pv = new Vector3[5] };
            for (int k = 0; k < 5; k++)
            {
                s.Pq[k] = SeedQuad(puffMat, 0.11f); s.Pq[k].position = at;
                s.Pv[k] = (n * 1.4f + UnityEngine.Random.insideUnitSphere * 1.6f + Vector3.up * 1.2f);
            }
            seedShots.Add(s);
        }
        static void SlingTick(Player p)
        {
            SeedFly(Time.deltaTime);
            if (!Driving) return;
            if (state != "aim")
            {
                if (c3Down && (state == "idle" || state == "move" || state == "land_short"))
                {
                    if (!Story.Flags.Contains("oot_slingshot") || OotItems.Seeds <= 0) { OotSfx.Play("NA_SE_SY_ERROR", 0.6f); return; }
                    StickOut = false; aimYaw = camYaw; aimPitch = 0f; speed = 0f; SetState("aim");
                    OotSfx.Play("NA_SE_IT_SLING_DRAW", 0.7f);
                }
                return;
            }
            float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
            aimYaw += mx * 2.2f + remoteStick.x * 60f * Time.deltaTime; aimPitch = Mathf.Clamp(aimPitch - my * 1.6f - remoteStick.y * 40f * Time.deltaTime, -60f, 60f);
            yaw = aimYaw; camYaw = aimYaw;
            if (c3Down || zDown) { SetState("idle"); return; }
            if (bDown && stateT > 0.25f)
            {
                if (OotItems.Seeds <= 0) { OotSfx.Play("NA_SE_SY_ERROR", 0.6f); return; }
                OotItems.Seeds--; stateT = 0.25f;
                OotSfx.Play("NA_SE_IT_SLING_SHOT", 0.9f);
                var cam = Camera.main; if (cam == null) return;
                // Ziel = was in der Bildmitte liegt; der Samen fliegt von der Schleuder dorthin
                var ray = new Ray(cam.transform.position, cam.transform.forward);
                RaycastHit rh; float dist = 40f;
                if (Physics.Raycast(ray, out rh, 60f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore)) dist = rh.distance;
                foreach (var sh in OotCombat.Shootables.ToArray()) { float t = -1f; try { t = sh.Test(ray, dist); } catch { } if (t >= 0f && t < dist) dist = t; }
                var aimAt = ray.GetPoint(dist);
                var from = p.transform.position + Vector3.up * 1.0f + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0.15f, 0f, 0.35f);
                var dir = (aimAt - from).normalized;
                if (Vector3.Dot(dir, cam.transform.forward) < 0.3f) dir = cam.transform.forward;
                if (seedMat == null) seedMat = FireFx.Make(SparkTex());
                var shot = new SeedShot { P = from, P0 = from, V = dir * 80f, Q = SeedQuad(seedMat, 0.42f) };
                shot.Q.position = from;
                seedShots.Add(shot);
            }
        }

        static void StickTick(Player p)
        {
            if (c1Down && Driving && (state == "idle" || state == "move" || state == "land_short"))
            {
                if (StickOut) StickOut = false;
                else if (Story.Flags.Contains("oot_has_sticks") && OotItems.Sticks > 0) { StickOut = true; OotSfx.Play("NA_SE_IT_SWORD_PICKOUT", 0.6f); }
                else OotSfx.Play("NA_SE_SY_ERROR", 0.6f);
            }
            if (!StickOut) { stickFire = 0f; if (stickFx != null) { UnityEngine.Object.Destroy(stickFx); stickFx = null; } return; }
            var tip = StickTip();
            if (stickFire <= 0f && OotDungeon.LitTorchAt(tip)) { stickFire = 10f; OotSfx.Play("NA_SE_EV_FLAME_IGNITION", 0.9f, tip); }
            if (stickFire > 0f)
            {
                stickFire -= Time.deltaTime;
                OotDungeon.Burn(tip);
                if (stickFx == null) stickFx = FireFx.Spawn(tip, 0.12f, 22f, true);
                stickFx.transform.position = tip;
                if (stickFire <= 0f)
                {
                    // abgebrannt: Stab ist weg
                    StickOut = false; OotItems.Sticks = Mathf.Max(0, OotItems.Sticks - 1);
                    if (stickFx != null) { UnityEngine.Object.Destroy(stickFx); stickFx = null; }
                }
            }
            else if (stickFx != null) { UnityEngine.Object.Destroy(stickFx); stickFx = null; }
        }

        static void SetState(string s) { if (state != s) { state = s; stateT = 0f; StateSound(s); } }

        // Original-Sounds (OotSfx) beim Wechsel der Aktion
        static float stepT;
        static void StateSound(string s)
        {
            try
            {
                switch (s)
                {
                    case "slash": OotSfx.Play("NA_SE_IT_SWORD_SWING", 0.85f); OotSfx.Play("NA_SE_VO_LI_SWORD_N_KID", 1f); break;
                    case "roll": OotSfx.Play("NA_SE_PL_ROLL", 0.8f); break;
                    case "jump": case "run_jump": case "backflip": case "hop_l": case "hop_r":
                        OotSfx.Play("NA_SE_VO_LI_AUTO_JUMP_KID", 1f); OotSfx.Play("NA_SE_PL_JUMP", 0.5f); break;
                    case "land": case "land_short": OotSfx.Play("NA_SE_PL_LAND", 0.6f); break;
                    case "chest":
                        OotSfx.Play("NA_SE_EV_TBOX_UNLOCK", 1f, OotItems.ChestStand());
                        OotSfx.PlayLater("NA_SE_EV_TBOX_OPEN", CHEST_LID, 1f, OotItems.ChestStand()); break;
                    case "get_item":
                        if (OotSfx.Play("BGM_ITEM_GET", 1f) != null) Music.Duck(3.4f);
                        break;
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotSfx.State"); }
        }

        static Player stepsOff;
        static void Footstep(Player p, float dt)
        {
            if (stepsOff != p && OotSfx.Has("NA_SE_PL_WALK_GRASS"))
            {
                stepsOff = p;
                foreach (var f in p.GetComponentsInChildren<FootStep>(true)) f.enabled = false;     // Valheim-Schritte aus, OoT-Schritte an
            }
            if (!((state == "move" || state == "sprint") && speed > 1.2f)) { stepT = 0.05f; return; }
            stepT -= dt;
            if (stepT > 0f) return;
            stepT = Mathf.Clamp(1.5f / speed, 0.17f, 0.45f);
            RaycastHit h;
            bool grass = Physics.Raycast(p.transform.position + Vector3.up * 0.3f, Vector3.down, out h, 1.2f, GroundMask(), QueryTriggerInteraction.Ignore)
                         && h.collider != null && h.collider.GetComponentInParent<Heightmap>() != null;
            OotSfx.Play(grass ? "NA_SE_PL_WALK_GRASS" : "NA_SE_PL_WALK_GROUND", 0.5f, null, 0.04f);
        }

        // Schwert in der Faust nur beim Schlagen/Hochhalten, sonst in der Scheide auf dem Ruecken (erst nach der Truhe)
        static void UpdateParts()
        {
            if (partSlots.Count == 0) return;
            // beim Hochhalten schwebt das GI-Modell ueber dem Kopf (OotItems.GetItemTick), die Haende sind leer
            bool has = OotItems.HasSword && state != "get_item";
            bool stk = StickOut && state != "get_item" && state != "climb" && state != "swim";
            bool outS = has && !stk && (state == "slash" || Time.time < swordOutUntil || (OotCombat.Lock != null && OotCombat.Lock.Enemy));
            bool sh = HasShield && state != "get_item";
            bool shHand = sh && (outS || state == "shield");          // Schwert gezogen oder R: Schild in der rechten Hand
            if (state == "aim") { shHand = false; outS = false; }
            string st = (has ? "1" : "0") + (outS ? "1" : "0") + (sh ? "1" : "0") + (shHand ? "1" : "0") + (stk ? "1" : "0") + (state == "aim" ? "1" : "0");
            if (st == partState) return;
            partState = st;
            var hid = Glb.HiddenMat();
            var byR = new Dictionary<Renderer, Material[]>();
            foreach (var s in partSlots)
            {
                if (s.R == null) continue;
                Material[] ms; if (!byR.TryGetValue(s.R, out ms)) byR[s.R] = ms = s.R.sharedMaterials;
                bool back = sh && !shHand;
                bool vis;
                switch (s.Part)
                {
                    case "hand_l": vis = !outS && !stk; break;
                    case "stick_hand": vis = stk; break;
                    case "sword_hand": vis = outS; break;
                    case "hand_r": vis = !shHand && state != "aim"; break;
                    case "sling_hand": vis = state == "aim"; break;
                    case "shield_hand": vis = shHand; break;
                    case "sheath_sword": vis = has && !outS && !back; break;
                    case "sheath_empty": vis = has && outS && !back; break;
                    case "sheath_shield_sword": vis = back && has && !outS; break;
                    case "sheath_shield_empty": vis = back && has && outS; break;
                    case "back_shield": vis = back && !has; break;
                    default: vis = true; break;
                }
                ms[s.I] = vis ? s.M : hid;
            }
            foreach (var kv in byR) kv.Key.sharedMaterials = kv.Value;
        }

        // ---------- Physik (Character.UpdateWalking, FixedUpdate) ----------
        // Valheim veraendert die Geschwindigkeit nach UpdateWalking noch (Reibung/Boden) -> am Ende des Physik-Schritts nochmal setzen
        static Vector2 wantH; static bool haveWant;
        static Vector3 dbgLast; static float dbgSpeed, dbgBodyIn, dbgDt, dbgDrag;
        static void FixedPost(Character __instance)
        {
            if (!haveWant || __instance != Player.m_localPlayer || !Driving) return;
            var body = bodyRef(__instance); if (body == null) return;
            var pp = body.position; dbgSpeed = new Vector2(pp.x - dbgLast.x, pp.z - dbgLast.z).magnitude / Mathf.Max(1e-4f, Time.fixedDeltaTime); dbgLast = pp;
            dbgBodyIn = new Vector2(body.velocity.x, body.velocity.z).magnitude; dbgDt = Time.fixedDeltaTime; dbgDrag = body.linearDamping;
            if (wantH.sqrMagnitude > 0.01f) body.linearDamping = 0f;
            body.velocity = new Vector3(wantH.x, wantYOn ? wantY : body.velocity.y, wantH.y);
            // Valheim nimmt Haft-Reibung, wenn es selbst keine Laufrichtung kennt -> die Geschwindigkeit verpufft am Boden (Link lief halb so schnell)
            var col = __instance.GetComponent<CapsuleCollider>();
            if (col != null)
            {
                if (noFriction == null) noFriction = new PhysicsMaterial("gi_oot_nofriction") { dynamicFriction = 0f, staticFriction = 0f, frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0f };
                bool moving = wantH.sqrMagnitude > 0.01f;
                if (moving && col.sharedMaterial != noFriction) { if (col.sharedMaterial != noFriction) frictionWas = col.sharedMaterial; col.sharedMaterial = noFriction; }
                else if (!moving && col.sharedMaterial == noFriction && frictionWas != null) col.sharedMaterial = frictionWas;
            }
        }
        static PhysicsMaterial noFriction, frictionWas;

        // Valheims Laufberechnung fuer Link ganz auslassen: sie gibt per AddForce eine Gegenkraft zu ihrem eigenen Ziel (Stillstand),
        // die erst im Physik-Schritt wirkt -> Link lief nur halb so schnell. Wir setzen die Geschwindigkeit selbst.
        static bool WalkPre(Character __instance, float dt)
        {
            if (__instance != Player.m_localPlayer || !Driving || link == null) return true;
            WalkPost(__instance, dt);
            return false;
        }

        static void WalkPost(Character __instance, float dt)
        {
            haveWant = false;
            WalkCore(__instance, dt);
            if (__instance == Player.m_localPlayer && Driving && link != null)
            {
                var b = bodyRef(__instance);
                if (b != null) { wantH = new Vector2(b.velocity.x, b.velocity.z); haveWant = true; }
            }
        }

        static void WalkCore(Character __instance, float dt)
        {
            if (__instance != Player.m_localPlayer || !Driving || link == null) return;
            var p = (Player)__instance;
            var body = bodyRef(p);
            if (body == null) return;
            bool ground = p.IsOnGround();
            stateT += dt;
            bool inside = Interior(p);
            float max = (walkKey ? WALK : inside ? RUN_IN : RUN) * stick.magnitude;
            Vector3 v = body.velocity;
            wantYOn = false;
            if (state == "climb" || state == "climb_end" || state == "swim") { ClimbSwim(p, body, dt); return; }
            if (state == "doorwalk")
            {
                // wie Player_Action_OpenDoor: Link geht ohne Kollision durch den Tuerrahmen (die Szene hat dort eine Wand)
                var dd = walkTo - walkFrom; dd.y = 0f;
                yaw = Mathf.Atan2(dd.x, dd.z) * Mathf.Rad2Deg;
                body.useGravity = false; body.velocity = Vector3.zero; wantH = Vector2.zero; haveWant = true; wantY = 0f; wantYOn = true;
                if (Time.time < walkStart) { speed = 0f; SetRot(p, body); return; }     // Tuer geht erst hoch
                float k = Mathf.Clamp01((Time.time - walkStart) / Mathf.Max(0.1f, walkUntil - walkStart));
                var q = Vector3.Lerp(walkFrom, walkTo, k);
                body.position = q; p.transform.position = q; speed = 1.9f; airborne = false;
                if (airAltRef != null) airAltRef(p) = q.y;
                if (k >= 1f) { body.useGravity = true; SetState("idle"); speed = 0f; }
                SetRot(p, body); return;
            }
            if (state == "hang")
            {
                // an der Kante haengen (jump_climb_hold): Stick zur Wand oder A = hochziehen, Stick weg = loslassen
                body.useGravity = false; body.position = ledgeFrom; p.transform.position = ledgeFrom; body.velocity = Vector3.zero;
                wantH = Vector2.zero; haveWant = true; wantY = 0f; wantYOn = true; speed = 0f; airborne = false;
                if (airAltRef != null) airAltRef(p) = ledgeFrom.y;
                float toWall = stick.magnitude > 0.4f ? Mathf.Abs(Mathf.DeltaAngle(stickYaw, yaw)) : 999f;
                if (stateT > 0.3f && (toWall < 70f || aDown)) { OotSfx.Play("NA_SE_VO_LI_CLIMB_END_KID", 0.8f); SetState("ledge"); }
                else if (stateT > 0.3f && toWall > 120f && toWall < 999f)
                {
                    body.useGravity = true; climbDropT = Time.time; airborne = true; airT = 0f; airVel = ledgeN * 0.8f; airTopY = ledgeFrom.y;
                    var dp = ledgeFrom + ledgeN * 0.25f + Vector3.down * 0.9f; body.position = dp; p.transform.position = dp; SetState("fall");
                }
                SetRot(p, body); return;
            }
            if (state == "ledge")
            {
                body.useGravity = false;
                float k = Mathf.Clamp01((stateT / ledgeDur - 0.45f) / 0.55f); k = k * k * (3f - 2f * k);
                var q = Vector3.Lerp(ledgeFrom, ledgeTo, k);
                body.position = q; p.transform.position = q; body.velocity = Vector3.zero;
                wantH = Vector2.zero; haveWant = true; wantY = 0f; wantYOn = true; speed = 0f; airborne = false;
                if (airAltRef != null) airAltRef(p) = q.y;
                if (stateT >= ledgeDur) { body.useGravity = true; SetState("idle"); }
                SetRot(p, body); return;
            }
            if (state == "wake")
            {
                if (!OotWake.Active) { SetState("idle"); return; }
                var ep = OotWake.EndPos; body.useGravity = false; body.position = ep; p.transform.position = ep; body.velocity = Vector3.zero;
                wantH = Vector2.zero; haveWant = true; wantY = 0f; wantYOn = true; speed = 0f; airborne = false;
                if (airAltRef != null) airAltRef(p) = ep.y;
                SetRot(p, body); return;
            }
            if (body.useGravity == false) body.useGravity = true;
            {
                // ins Wasser (Wasserkasten): schwimmen
                float wy;
                if (OotDungeon.WaterAt(p.transform.position, out wy) && p.transform.position.y < wy - 0.8f) { SetState("swim"); airborne = false; speed = Mathf.Min(speed, 2f); ClimbSwim(p, body, dt); return; }
                // Ranken/Leiter vor Link: Stick nach vorn -> klettern
                Vector3 cp, cn; int ct;
                if (stick.magnitude > 0.5f && state != "roll" && Time.time - climbDropT > 0.6f &&
                    Mathf.Abs(Mathf.DeltaAngle(stickYaw, yaw)) < 60f &&
                    OotDungeon.ClimbAt(p.transform.position + Vector3.up * 0.9f, Quaternion.Euler(0f, yaw, 0f) * Vector3.forward, 0.6f, out cp, out cn, out ct))
                { climbN = cn; SetState("climb"); airborne = false; speed = 0f; ClimbSwim(p, body, dt); return; }
            }
            if (state == "aim")
            {
                speed = 0f; body.velocity = new Vector3(0f, v.y, 0f);
                Landed(p); SetRot(p, body);
                return;
            }
            if (state == "hurt" || state == "die" || state == "shield")
            {
                if (state == "hurt")
                {
                    // Rueckstoss (z_player.c func_8083...: Link fliegt kurz zurueck), danach wieder frei
                    knock = Vector3.MoveTowards(knock, Vector3.zero, 14f * dt);
                    body.velocity = new Vector3(knock.x, v.y, knock.z); speed = 0f;
                    if (stateT > 0.45f) SetState("idle");
                }
                else if (state == "die")
                {
                    body.velocity = new Vector3(0f, v.y, 0f); speed = 0f;
                    if (stateT > 2.6f && !warping) Respawn();
                }
                else
                {
                    // Schild: Link duckt sich hinter den Deku-Schild und bleibt stehen; mit Z dreht er zum Ziel
                    speed = 0f; body.velocity = new Vector3(0f, v.y, 0f);
                    if (OotCombat.Lock != null) yaw = Mathf.MoveTowardsAngle(yaw, YawTo(p, OotCombat.FocusPos(OotCombat.Lock)), TURN * dt);
                    else if (stick.magnitude > 0.2f) yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, 360f * dt);
                    if (!rHeld || !HasShield) SetState("idle");
                }
                Landed(p); SetRot(p, body);
                return;
            }
            if (state == "slash" || state == "chest" || state == "get_item" || state == "talk" || state == "shop" || state == "dialog")
            {
                speed = Mathf.MoveTowards(speed, 0f, DECEL * 1.5f * dt);
                if (state == "shop") { var dv = OotShop.KeeperPos - p.transform.position; dv.y = 0f; if (dv.sqrMagnitude > 0.01f) yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg, 360f * dt); }
                if (state == "slash")
                {
                    if (OotCombat.Lock != null) yaw = Mathf.MoveTowardsAngle(yaw, YawTo(p, OotCombat.FocusPos(OotCombat.Lock)), 720f * dt);
                    else if (stick.magnitude > 0.2f) yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, 240f * dt);
                    if (!hitDone && stateT > 0.07f) { hitDone = true; OotItems.Hit(p.transform.position, yaw); OotCombat.SwordHit(p.transform.position, yaw); }
                    if (stateT > 0.42f) SetState(stick.magnitude > 0.2f ? "move" : "idle");
                }
                else if (state == "chest")
                {
                    yaw = Mathf.MoveTowardsAngle(yaw, OotItems.ChestFacingYaw(), 360f * dt);
                    if (stateT > CHEST_LID) OotItems.OpenLid();
                    if (stateT > CHEST_T) { OotItems.GiKey = OotItems.ChestKey; OotItems.GiText = OotItems.ChestText; OotItems.GiFlag = OotItems.ChestFlag; giChest = true; SetState("get_item"); textPage = 0; Text = OotItems.GiText != null && OotItems.GiText.Length > 0 ? OotItems.GiText[0] : null; }
                }
                else if (state == "talk")
                {
                    Vector3 tp;
                    if (OotNpcs.TalkPos(out tp)) { var dv = tp - p.transform.position; dv.y = 0f; if (dv.sqrMagnitude > 0.01f) yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg, 360f * dt); }
                }
                else if (state == "get_item")
                {
                    // wie im Original: Link dreht sich von der Truhe weg zur Kamera und haelt das Schwert hoch
                    yaw = Mathf.MoveTowardsAngle(yaw, giChest ? OotItems.ChestFacingYaw() + 180f : giYaw, 900f * dt);
                }
                var fw0 = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * speed;
                body.velocity = new Vector3(fw0.x, v.y, fw0.z);
                Landed(p); SetRot(p, body);
                return;
            }
            if (state == "roll")
            {
                // Rolle: volles Tempo nach vorn, leicht lenkbar (Original ~ 32 Bilder Animation x 1,25)
                if (stick.magnitude > 0.2f) yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, 180f * dt);
                speed = Mathf.Max(speed, ROLL_SPEED);
                // wie Player_Action_Roll: Ende bei Bild 20 (von 32), ab Bild 15 mit Stick direkt weiterlaufen (vorher lief die Rolle bis
                // zum Schluss = Aufrichten auf der Stelle, Moritz 09.11.: "steht kurz still")
                if (stateT > ROLL_TIME * 20f / 32f || (stateT > ROLL_TIME * 15f / 32f && stick.magnitude > 0.2f))
                    SetState(stick.magnitude > 0.2f ? (aHeld ? "sprint" : "move") : "idle");
            }
            else if (state == "sprint" && ground)
            {
                // Sprint: solange A gehalten und gelaufen wird; lenkt etwas traeger
                if (!aHeld || stick.magnitude < 0.15f) { sprintArmed = false; SetState(stick.magnitude > 0.15f ? "move" : "idle"); }
                else
                {
                    yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, TURN * 0.6f * dt);
                    speed = Mathf.MoveTowards(speed, SPRINT, ACCEL * 0.5f * dt);
                }
            }
            else if (!ground)
            {
                // Luft: Schwung behalten, wenig Lenkung
                if (!airborne) { airborne = true; airT = 0f; airVel = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * speed; }
                // Fallschaden wie OoT erst bei richtig tiefen Stuerzen (Valheim: schon ab 4 m)
                airTopY = airT < 0.05f ? p.transform.position.y : Mathf.Max(airTopY, p.transform.position.y);
                if (airAltRef != null) airAltRef(p) = p.transform.position.y;          // kein Valheim-Fallschaden (OoT-Fallschaden: Landed)
                airT += dt;
                // im Fallen an einer Kante festhalten (Player_Action_8084411C: Wand beruehrt, vorwaerts unterwegs, Kante < 150 Einheiten
                // ueber Link und > 70 * 11/17 ueber dem Boden darunter) -> haengen, Stick vor/A = hochziehen
                bool webFall = Time.time - OotDungeon.BrokeUnder < 3f;                  // mit dem gerissenen Netz senkrecht nach B1 (wie OoT)
                if (webFall) airVel = Vector3.MoveTowards(airVel, Vector3.zero, 12f * dt);
                if (!webFall && v.y < -0.3f && airT > 0.12f && !zHeld && state != "backflip" && state != "hop_l" && state != "hop_r" && Time.time - climbDropT > 0.8f &&
                    new Vector2(airVel.x, airVel.z).magnitude > 0.5f && AirLedge(p, out ledgeTop, out ledgeN))
                {
                    yaw = Mathf.Atan2(-ledgeN.x, -ledgeN.z) * Mathf.Rad2Deg; speed = 0f; airborne = false;
                    ledgeHi = true; ledgeDur = 37f / 30f;
                    ledgeFrom = new Vector3(p.transform.position.x, ledgeTop.y + 0.02f, p.transform.position.z) + ledgeN * 0.05f;
                    ledgeTo = ledgeTop - ledgeN * 0.32f; ledgeTo.y = ledgeTop.y + 0.02f;
                    body.velocity = Vector3.zero; body.useGravity = false; body.position = ledgeFrom; p.transform.position = ledgeFrom;
                    noFallDmgUntil = Time.time + 2f; OotSfx.Play("NA_SE_VO_LI_HANG_KID", 0.9f); SetState("hang");
                    SetRot(p, body); return;
                }
                if (stick.magnitude > 0.2f && !zHeld && !webFall) airVel = Vector3.MoveTowards(airVel, Quaternion.Euler(0f, stickYaw, 0f) * Vector3.forward * Mathf.Max(airVel.magnitude, 1.5f), 4f * dt);
                OotDungeon.WebPull(p.transform.position, v.y, ref airVel, dt);
                body.velocity = new Vector3(airVel.x, v.y, airVel.z);
                wasGround = false;
                SetRot(p, body);
                return;
            }
            else if (zHeld)
            {
                // Z: Blick bleibt (oder zum Ziel), seitwaerts/rueckwaerts gehen; mit Ziel kreist Link darum
                float face = OotCombat.Lock != null ? YawTo(p, OotCombat.FocusPos(OotCombat.Lock)) : camYaw;
                yaw = Mathf.MoveTowardsAngle(yaw, face, TURN * dt);
                var dir = Quaternion.Euler(0f, OotCombat.Lock != null ? yaw : camYaw, 0f) * new Vector3(stick.x, 0f, stick.y);
                speed = Mathf.MoveTowards(speed, max * 0.8f, ACCEL * dt);
                var hv = dir.sqrMagnitude > 0.01f ? dir.normalized * speed : Vector3.zero;
                body.velocity = new Vector3(hv.x, v.y, hv.z);
                Landed(p); SetRot(p, body);
                return;
            }
            else if (stick.magnitude > 0.15f)
            {
                // Laufen: Link dreht sich mit begrenzter Rate in Stickrichtung; bei Kehrtwende erst abbremsen
                float diff = Mathf.Abs(Mathf.DeltaAngle(yaw, stickYaw));
                if (diff > 150f && speed > 3.5f) speed = Mathf.MoveTowards(speed, 0f, DECEL * dt);
                else
                {
                    float rate = speed < 1.5f ? 1440f : TURN;
                    yaw = Mathf.MoveTowardsAngle(yaw, stickYaw, rate * dt);
                    speed = Mathf.MoveTowards(speed, max * Mathf.Clamp01(1.2f - diff / 180f), ACCEL * dt);
                }
                if (!aHeld) sprintArmed = false;
                bool takeoff = (state == "jump" || state == "run_jump") && stateT < 0.25f;      // gerade abgesprungen, noch Bodenkontakt
                if (takeoff) { }
                else if (sprintArmed && aHeld && state != "roll") SetState("sprint");
                else if (state != "land") SetState("move");
            }
            else
            {
                speed = Mathf.MoveTowards(speed, 0f, DECEL * dt);
                if (state == "move" && speed < 0.1f) SetState("idle");
            }
            var fw = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward * speed;
            body.velocity = new Vector3(fw.x, v.y, fw.z);
            // kein Auto-Sprung an Kanten mehr (OoT-Konzept aus Zeiten ohne Sprungtaste): gesprungen wird nur mit Leertaste,
            // an Kanten laeuft Link einfach herunter und faellt.
            Landed(p); SetRot(p, body);
            Footstep(p, dt);
        }

        static void Landed(Player p)
        {
            if (airborne)
            {
                airborne = false;
                float fall = airTopY - p.transform.position.y;
                bool web = fall > 1.0f && OotDungeon.Land(p.transform.position, fall);
                if (!web && fall > 400f * 0.033f && state != "die" && Time.time > noFallDmgUntil && airT < 4f)
                {
                    // tiefer Sturz (> 400 Einheiten): ein Herz Schaden
                    OotCombat.Damage(0x10, p.transform.position + Quaternion.Euler(0f, yaw, 0f) * Vector3.forward);
                }
                speed = new Vector2(airVel.x, airVel.z).magnitude;
                if (state == "backflip" || state == "hop_l" || state == "hop_r") speed = 0f;
                // Netz reisst unter Link (Sprung vom 3. Stock): wie im Original abfangen, dann faellt er mit dem Netz durch
                if (web && Time.time - OotDungeon.BrokeUnder < 0.2f) { speed = 0f; airVel = Vector3.zero; SetState("land"); }
                else SetState(airT > 0.55f && stick.magnitude < 0.2f ? "land" : (airT > 0.15f && stick.magnitude < 0.2f ? "land_short" : (stick.magnitude > 0.2f ? "move" : "idle")));
            }
            if ((state == "land" && stateT > 0.5f && Time.time - OotDungeon.BrokeUnder > 1f) || (state == "land_short" && stateT > 0.3f)) SetState(stick.magnitude > 0.2f ? "move" : "idle");
            if ((state == "jump" || state == "run_jump" || state == "fall") && stateT > 0.25f) SetState(speed > 0.3f ? "move" : "idle");
            wasGround = true;
        }

        static void SetRot(Player p, Rigidbody body)
        {
            var q = Quaternion.Euler(0f, yaw, 0f);
            body.rotation = q; p.transform.rotation = q;
            try { p.SetLookDir(q * Vector3.forward); } catch { }
        }

        static int gmask;
        static bool SoftForCamera(Collider col)
        {
            if (col == null) return true;
            var t = col.transform;
            while (t.parent != null && t.parent.name != "gi_foliage") t = t.parent;
            if (t.parent == null) return false;                  // kein Bewuchs
            var n = t.name;
            return !(n.Contains("Tree") || n.StartsWith("Beech") || n.StartsWith("Oak") || n.StartsWith("Pine") || n.StartsWith("Birch") || n.StartsWith("rock"));
        }
        static int GroundMask() { if (gmask == 0) gmask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle"); return gmask; }

        static bool Interior(Player p)
        {
            return Physics.Raycast(p.transform.position + Vector3.up * 1.5f, Vector3.up, 6f, GroundMask(), QueryTriggerInteraction.Ignore);
        }

        // ---------- Animation (Original-Clips) ----------
        static void Animate(Player p)
        {
            if (anim == null) return;
            bool ground = p.IsOnGround();
            string want; float sp = 1f, fade = 0.12f;
            var body = bodyRef(p);
            float vy = body != null ? body.velocity.y : 0f;
            switch (state)
            {
                case "roll": want = "roll"; sp = 1.25f; fade = 0.05f; break;
                case "sprint": want = "run"; sp = Mathf.Clamp(speed / RUN * 1.0f, 1f, 1.7f); fade = 0.15f; break;
                case "backflip": want = "backflip"; fade = 0.05f; break;
                case "hop_l": case "hop_r": want = state; fade = 0.05f; break;
                case "land": want = "land"; break;
                case "slash": want = (slashN % 2 == 1 ? "slash" : "slash2") + (stateT > 0.17f ? "_end" : ""); fade = 0.03f; break;
                case "chest": want = "chest_open"; sp = CHEST_SP; fade = 0.1f; break;
                case "get_item": want = "get_item"; fade = 0.1f; break;
                case "talk": case "shop": case "dialog": want = "idle"; fade = 0.2f; break;
                case "climb":
                {
                    float up = Mathf.Abs(stick.y) > 0.25f ? Mathf.Sign(stick.y) : 0f, side = Mathf.Abs(stick.x) > 0.35f ? Mathf.Sign(stick.x) : 0f;
                    want = up > 0f ? "climb_u" : up < 0f ? "climb_d" : side < 0f ? "climb_sl" : side > 0f ? "climb_sr" : "climb_w"; fade = 0.15f;
                    if (want == "climb_w") sp = 0f;          // Halten: erstes Bild der Kletterbewegung
                    break;
                }
                case "climb_end": want = "climb_e"; fade = 0.08f; break;
                case "ledge": want = ledgeHi ? "climb_up" : "step_up"; fade = 0.05f; break;
                case "hang": want = "hang"; fade = 0.06f; break;
                case "swim": want = speed > 0.3f ? "swim" : "swim_wait"; fade = 0.2f; break;
                case "hurt": want = hurtBack ? "damage_back" : "damage_front"; fade = 0.04f; break;
                case "die": want = "die"; fade = 0.1f; break;
                case "shield": want = stateT < 0.12f && anim["shield_in"] != null ? "shield_in" : "shield"; fade = 0.06f; break;
                case "aim": want = stateT < 0.25f && anim["bow_ready"] != null ? "bow_ready" : "bow_wait"; fade = 0.08f; break;
                case "land_short": want = "land_short"; break;
                case "wake": want = OotWake.Clip ?? "op3_wait1"; fade = 0.08f; break;
                default:
                    if (!ground && airborne)
                    {
                        // Sprung-Pose bis zur Landung halten; die Fall-Animation (Arme hoch) nur bei echten Stuerzen
                        if ((state == "run_jump" || state == "jump") && airT < 1.1f) want = state;
                        else if (airT < 0.35f && (clip == "run" || clip == "walk" || clip == "idle")) want = clip;
                        else want = "fall";
                    }
                    else if (!ground && vy > 1f) want = clip == "run_jump" ? "run_jump" : "jump";
                    else if (zHeld && stick.magnitude > 0.15f)
                    {
                        if (Mathf.Abs(stick.x) > Mathf.Abs(stick.y)) want = stick.x < 0 ? "side_l" : "side_r";
                        else want = stick.y < 0 ? "back_walk" : "walk";
                        sp = Mathf.Max(0.6f, speed / 2.2f);
                    }
                    else if (Pushing && ground) { want = speed > 0.2f ? "push" : "push_wait"; fade = 0.15f; }
                    else if (speed < 0.12f) want = "idle";
                    else if (speed < 2.6f) { want = "walk"; sp = Mathf.Clamp(speed / 1.7f, 0.5f, 1.6f); }
                    else { want = "run"; sp = Mathf.Clamp(speed / RUN * 1.05f, 0.6f, 1.4f); }
                    break;
            }
            if (anim[want] == null) want = state == "die" ? "fall" : "idle";
            if (want != clip)
            {
                anim.CrossFade(want, fade);
                if (anim[want].wrapMode != WrapMode.Loop) anim[want].time = 0f;
                clip = want;
            }
            anim[want].speed = sp;
        }

        // ---------- Kamera (nach Valheims Kamera, vor Dialog-/Cutscene-Kameras) ----------
        static bool camFix; static Vector3 camFixPos, camFixLook;
        // Kamerafahrt fuer Rundgang-Videos ("oot camfly <datei>"): Zeilen "t px py pz lx ly lz" (Welt), weich (Catmull-Rom),
        // laeuft in Spielzeit (bei Aufnahmen mit festen 50 Bildern/s gleichmaessig)
        static List<float[]> camKeys; static float camKeyT0;
        static Vector3 CR(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
        static void CamFly()
        {
            float t = Time.time - camKeyT0; var k = camKeys; int n = k.Count;
            Func<int, Vector3> P = i => { var e = k[Mathf.Clamp(i, 0, n - 1)]; return new Vector3(e[1], e[2], e[3]); };
            Func<int, Vector3> L = i => { var e = k[Mathf.Clamp(i, 0, n - 1)]; return new Vector3(e[4], e[5], e[6]); };
            int s = 0; while (s < n - 2 && t > k[s + 1][0]) s++;
            float u = n < 2 ? 0f : Mathf.Clamp01((t - k[s][0]) / Mathf.Max(0.001f, k[s + 1][0] - k[s][0]));
            u = u * u * (3f - 2f * u) * 0.35f + u * 0.65f;      // sanft an den Haltepunkten
            camFixPos = CR(P(s - 1), P(s), P(s + 1), P(s + 2), u); camFixLook = CR(L(s - 1), L(s), L(s + 1), L(s + 2), u); camFix = true;
        }
        // Gebiet mit eigenem Licht: Innenraum-Grundlicht des Pakets (StoryUi.InteriorLight) haelt sich raus
        public static bool AreaOwnsLight { get { return On && curArea != null && (curArea.HasAmb || curArea.HasFog); } }
        // weich ueberblenden (Raumwechsel im Deku-Baum: Nebel/Licht gleiten statt springen)
        static Color sFogC, sAmb; static float sFogD; static bool sInit;
        static void AreaLight()
        {
            var a = curArea; if (a == null || StoryUi.Busy || (!a.HasFog && !a.HasAmb)) { sInit = false; return; }
            if (!sInit) { sFogC = RenderSettings.fogColor; sFogD = RenderSettings.fogDensity; sAmb = RenderSettings.ambientLight; }
            float k = sInit ? Mathf.Clamp01(Time.deltaTime * 1.1f) : 1f; sInit = true;
            if (a.HasFog)
            {
                sFogC = Color.Lerp(sFogC, a.FogC, k); sFogD = Mathf.Lerp(sFogD, a.FogD, k);
                RenderSettings.fog = true; RenderSettings.fogColor = sFogC; RenderSettings.fogDensity = sFogD;
            }
            if (a.HasAmb) { sAmb = Color.Lerp(sAmb, a.Amb, k); RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = sAmb; }
        }
        static void CameraPost(GameCamera __instance)
        {
            HideLoading();
            AreaLight();
            try { OotAtmos.Tick(); } catch (Exception e) { Plugin.Quiet(e, "OotAtmos.Tick"); }
            if (reverbGo != null && Camera.main != null) reverbGo.transform.position = Camera.main.transform.position;
            if (!Driving || link == null) return;
            var c = __instance.GetComponent<Camera>(); var p = Player.m_localPlayer;
            if (c == null || p == null) return;
            if (state == "wake" && OotWake.Active) { OotWake.Camera(c); OotNavi.Face(c); return; }
            if (OotDev.FreeCam) { OotDev.Camera(c); OotNavi.Face(c); return; }
            if (camKeys != null) CamFly();
            if (camFix) { c.transform.position = camFixPos; c.transform.rotation = Quaternion.LookRotation(camFixLook - camFixPos); c.fieldOfView = 55f; OotNavi.Face(c); return; }
            float dt = Time.deltaTime;
            if (!camInit) { camYaw = yaw; camPitch = 14f; camInit = true; }
            if (!Typing())
            {
                float mx = Input.GetAxis("Mouse X"), my = Input.GetAxis("Mouse Y");
                if (Mathf.Abs(mx) + Mathf.Abs(my) > 0.01f) { camYaw += mx * 3f; camPitch = Mathf.Clamp(camPitch - my * 2f, -25f, 60f); mouseT = Time.time; }
                float sc = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(sc) > 0.01f) camDist = Mathf.Clamp(camDist - sc * 6f, 1.2f, 9f);
            }
            if (zHeld && OotCombat.Lock != null)
            {
                // Anvisier-Kamera: hinter Link, Blick zwischen Link und Ziel
                float ty = YawTo(p, OotCombat.FocusPos(OotCombat.Lock));
                camYaw = Mathf.LerpAngle(camYaw, ty, 1f - Mathf.Exp(-dt * 6f)); camPitch = Mathf.Lerp(camPitch, 12f, 1f - Mathf.Exp(-dt * 6f));
            }
            else if (zHeld) { camYaw = Mathf.LerpAngle(camYaw, yaw, 1f - Mathf.Exp(-dt * 14f)); camPitch = Mathf.Lerp(camPitch, 10f, 1f - Mathf.Exp(-dt * 8f)); }
            else if (Time.time - mouseT > 0.8f && speed > 0.5f && state != "roll")
            {
                // OoT-Normalkamera: zieht beim Laufen langsam hinter Link (schneller, je schneller er laeuft)
                float d = Mathf.DeltaAngle(camYaw, yaw);
                if (Mathf.Abs(d) < 165f) camYaw += d * (1f - Mathf.Exp(-dt * speed * 0.35f));
            }
            var target = p.transform.position + Vector3.up * 1.15f;
            lockMix = Mathf.MoveTowards(lockMix, zHeld && OotCombat.Lock != null ? 1f : 0f, dt * 3f);
            if (lockMix > 0f && OotCombat.Lock != null) lockAt = OotCombat.FocusPos(OotCombat.Lock);
            if (lockMix > 0f) target = Vector3.Lerp(target, Vector3.Lerp(target, lockAt, 0.4f), lockMix);
            var rot = Quaternion.Euler(camPitch, camYaw, 0f);
            float dist = camDist;
            // Kamera wie OoT durch Buesche/Strauecher hindurch (sonst steckt sie in Links Kopf); Gelaende, Baeume, Bauten halten sie auf
            var hits = Physics.SphereCastAll(target, 0.25f, rot * Vector3.back, camDist, GroundMask(), QueryTriggerInteraction.Ignore);
            foreach (var ch in hits)
            {
                if (ch.distance <= 0f || ch.distance - 0.1f >= dist || SoftForCamera(ch.collider)) continue;
                dist = Mathf.Max(0.6f, ch.distance - 0.1f);
            }
            // Wand dicht hinter Link (enge Gaenge): wie OoT ueber Link hinweg von oben schauen statt in seinem Kopf zu stecken
            {
                float wantLift = 0f;
                if (state != "aim" && dist < 1.4f)
                {
                    float d2 = CamFree(target, Quaternion.Euler(Mathf.Min(camPitch + 42f, 76f), camYaw, 0f));
                    if (d2 > dist + 0.4f) wantLift = 1f;
                }
                camLift = Mathf.MoveTowards(camLift, wantLift, dt * (wantLift > camLift ? 3f : 1.2f));
                if (camLift > 0.001f) { rot = Quaternion.Euler(Mathf.Min(camPitch + 42f * camLift, 76f), camYaw, 0f); dist = CamFree(target, rot); }
            }
            if (state == "aim")
            {
                rot = Quaternion.Euler(aimPitch, aimYaw, 0f);
                target = p.transform.position + Vector3.up * 1.25f + Quaternion.Euler(0f, aimYaw, 0f) * new Vector3(0.45f, 0f, 0f);
                dist = 1.1f;
            }
            var want = target + rot * Vector3.back * dist;
            Vector3 sp = Vector3.zero, sl = Vector3.zero;
            if (state == "dialog" && dlgCam) { sp = dlgCamPos; sl = dlgCamLook; }
            if ((state == "dialog" && dlgCam) || (state == "shop" && OotShop.Cam(out sp, out sl)))
            {
                shopCamK = Mathf.MoveTowards(shopCamK, 1f, dt * 2.5f);
                shopLook = shopCamK < 0.02f ? sl : Vector3.Lerp(shopLook, sl, 1f - Mathf.Exp(-dt * 6f));
                want = Vector3.Lerp(want, sp, Mathf.SmoothStep(0f, 1f, shopCamK));
                rot = Quaternion.Slerp(rot, Quaternion.LookRotation(shopLook - want), Mathf.SmoothStep(0f, 1f, shopCamK));
            }
            else shopCamK = 0f;
            camPos = want;
            c.transform.position = camPos; c.transform.rotation = rot;
            c.fieldOfView = 55f;
            OotNavi.Face(c);
            // Kamera steckt trotzdem fast in Link (Wand + Decke dicht): Link ausblenden wie in OoT, statt seine Muetze bildfuellend zu zeigen
            LinkNearCam(link != null && (camPos - target).magnitude < 0.85f && state != "dialog" && state != "shop");
        }
        static bool linkCamHidden; static readonly List<Renderer> linkCamOff = new List<Renderer>();
        static readonly List<Renderer> ghostOff = new List<Renderer>(); static bool ghostOn;
        static void LinkNearCam(bool hide)
        {
            if (hide == linkCamHidden) return;
            linkCamHidden = hide;
            if (hide && link != null)
            {
                linkCamOff.Clear();
                foreach (var r in link.GetComponentsInChildren<Renderer>()) if (r.enabled) { r.enabled = false; linkCamOff.Add(r); }
            }
            else { foreach (var r in linkCamOff) if (r != null) r.enabled = true; linkCamOff.Clear(); }
        }

        // Dev-Teleport in der Naehe (Modelle schon geladen): sofort umsetzen, ohne Valheims Ladebild, ohne Fallschaden
        public static void DevMove(Vector3 pos, float y)
        {
            var p = Player.m_localPlayer; if (p == null) return;
            if (warping) { settleCancel = true; warping = false; Fade = 0f; }
            pos += Vector3.up * 0.15f;
            p.transform.position = pos; p.transform.rotation = Quaternion.Euler(0f, y, 0f);
            var rb = bodyRef(p); if (rb != null) { rb.position = pos; rb.velocity = Vector3.zero; }
            if (airAltRef != null) airAltRef(p) = pos.y;
            airTopY = pos.y; airborne = false; airVel = Vector3.zero;
            respawnPos = pos; respawnYaw = y; haveRespawn = true;
            Warped(y);
        }

        // nach Tuer/Ausgang: Blickrichtung und Kamera wie beim Betreten einer Szene (Kamera hinter Link)
        public static void Warped(float y) { yaw = y; camYaw = y; camPitch = 14f; speed = 0f; state = "idle"; noFallDmgUntil = Time.time + 3f; }

        // Ortswechsel wie in OoT: schwarz abblenden, umsetzen (Valheim laedt), wieder aufblenden; Valheims Teleport-Wirbel bleibt unter dem Schwarz
        public static float Fade;                       // 0..1 Schwarz (OotHud zeichnet es)
        static bool warping;
        public static bool WarpTo(Vector3 pos, float y)
        {
            if (!On || StoryUi.I == null) return false;
            if (!warping) StoryUi.I.StartCoroutine(WarpCo(pos, y));
            return true;
        }
        static bool settleCancel;       // Dev-Teleport waehrend des Festhaltens: sofort loslassen
        static bool settling;
        public static bool Settling { get { return settling; } }
        static System.Collections.IEnumerator SettleCo(Player p)
        {
            var pos = p.transform.position;
            var rb = p.GetComponent<Rigidbody>();
            int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            Func<bool> Ground = () => Physics.Raycast(pos + Vector3.up * 1f, Vector3.down, 8f, mask, QueryTriggerInteraction.Ignore);
            if (Ground()) yield break;                // Boden da (Wald: Gelaende sofort) -> nicht festhalten
            warping = true; hideLoadUntil = Time.time + 9999f; Fade = 1f; settleCancel = false; settling = true;
            float t0 = Time.unscaledTime, ok = -1f;
            while (p != null && Time.unscaledTime - t0 < 35f && !settleCancel)
            {
                p.transform.position = pos;
                if (rb != null) { rb.position = pos; rb.velocity = Vector3.zero; }
                if (Ground()) { if (ok < 0f) ok = Time.unscaledTime; if (Time.unscaledTime - ok > 0.6f) break; }
                else ok = -1f;
                Fade = 1f;
                yield return null;
            }
            noFallDmgUntil = Time.time + 4f; settling = false;
            if (settleCancel) { settleCancel = false; yield break; }          // Dev-Teleport hat uebernommen
            try { if (p != null) AccessTools.Field(typeof(Character), "m_maxAirAltitude").SetValue(p, p.transform.position.y); } catch { }
            for (float t = 0f; t < 0.35f; t += Time.unscaledDeltaTime) { Fade = 1f - t / 0.35f; yield return null; }
            Fade = 0f; warping = false; hideLoadUntil = Time.time + 4f;
        }
        // Ins Leere gefallen (wie OoT "void out"): Innenraeume (Haus, Laden, Deku-Baum) liegen hoch ueber Valheims Meer; faellt Link
        // durch eine Luecke/ein Loch darunter (oder steht nach dem Laden im Meer), zurueck an den letzten sicheren Punkt, Viertelherz Schaden.
        static float voidT, lastVoidT = -99f; static int voidStreak; static Vector3 lastVoidResp;
        static void VoidOut(Player p)
        {
            if (warping || !haveRespawn || p == null || p.IsTeleporting() || OotWake.Active || StoryUi.Busy || OotTitle.Active) { voidT = 0f; return; }
            var pos = p.transform.position;
            bool interior = respawnPos.y > 150f;                    // letzter sicherer Punkt in einem Innenraum
            if (!interior && pos.y < 5f)
            {
                // im Meer unter der Welt (z. B. so gespeichert): zum Spielstart (Links Haus)
                var st = Pack.Current != null ? Json.L(Json.O(Pack.Current.Meta, "start"), "pos") : null;
                if (st != null && st.Count >= 3)
                {
                    voidT += Time.deltaTime; if (voidT < 0.4f) return; voidT = 0f;
                    var sp = new Vector3(Convert.ToSingle(st[0]), Convert.ToSingle(st[1]), Convert.ToSingle(st[2]));
                    Plugin.Log("OoT: im Meer bei " + pos + " -> Spielstart " + sp);
                    WarpTo(sp, Json.F(Json.O(Pack.Current.Meta, "start"), "yaw", 0f));
                }
                return;
            }
            if (!interior || pos.y > 150f) { voidT = 0f; return; }
            voidT += Time.deltaTime;
            if (voidT < 0.4f) return;
            voidT = 0f;
            // derselbe Halt fuehrt sofort wieder ins Leere (z. B. Spielstand im noch nicht gebauten B2-Wasser): Eingang/Spielstart statt Endlosschleife
            if (Time.time - lastVoidT < 10f && (respawnPos - lastVoidResp).sqrMagnitude < 4f) voidStreak++; else voidStreak = 0;
            lastVoidT = Time.time; lastVoidResp = respawnPos;
            if (voidStreak >= 1)
            {
                var ye = OotDev.YdanEntrance; var d = respawnPos - ye; d.y = 0f;
                Vector3 fb = ye; float fy = 0f;
                if (d.magnitude > 150f)
                {
                    var st = Pack.Current != null ? Json.L(Json.O(Pack.Current.Meta, "start"), "pos") : null;
                    if (st != null && st.Count >= 3) { fb = new Vector3(Convert.ToSingle(st[0]), Convert.ToSingle(st[1]), Convert.ToSingle(st[2])); fy = Json.F(Json.O(Pack.Current.Meta, "start"), "yaw", 0f); }
                }
                Plugin.Log("OoT: Halt " + respawnPos + " fuehrt wieder ins Leere -> " + fb);
                voidStreak = 0; respawnPos = fb; respawnYaw = fy;
            }
            Plugin.Log("OoT: ins Leere gefallen bei " + pos + " -> zurueck nach " + respawnPos);
            if (OotCombat.Hp > 4) OotCombat.Damage(4, pos, true);
            WarpTo(respawnPos, respawnYaw);
        }
        static float hideLoadUntil;
        static System.Reflection.FieldInfo fLoadScreen;
        static void HideLoading()
        {
            if (Time.time > hideLoadUntil || Hud.instance == null) return;
            try
            {
                if (fLoadScreen == null) fLoadScreen = AccessTools.Field(typeof(Hud), "m_loadingScreen");
                var cg = fLoadScreen != null ? fLoadScreen.GetValue(Hud.instance) as CanvasGroup : null;
                if (cg != null) cg.alpha = 0f;
            }
            catch { }
        }
        static System.Collections.IEnumerator WarpCo(Vector3 pos, float y)
        {
            warping = true; hideLoadUntil = Time.time + 9999f;
            for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime) { Fade = t / 0.3f; yield return null; }
            Fade = 1f;
            var p = Player.m_localPlayer;
            if (p != null) { p.TeleportTo(pos + Vector3.up * 0.2f, Quaternion.Euler(0f, y, 0f), true); Warped(y); respawnPos = pos; respawnYaw = y; haveRespawn = true; }
            float t0 = Time.unscaledTime;
            yield return null;
            while (p != null && p.IsTeleporting() && Time.unscaledTime - t0 < 30f) yield return null;
            // weit weg (Innenraum): schwarz bleiben, bis Valheims Ladebild (Portal-Wirbel) weg ist und die Modelle stehen
            float t1 = Time.unscaledTime;
            int gmask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            while (Time.unscaledTime - t1 < 20f && Statics.Pending > 0 && !(p != null && Physics.Raycast(p.transform.position + Vector3.up, Vector3.down, 6f, gmask, QueryTriggerInteraction.Ignore)))
            { Fade = 1f; yield return null; }
            for (float t = 0f; t < 0.4f; t += Time.unscaledDeltaTime) { yield return null; }
            Warped(y);
            for (float t = 0f; t < 0.35f; t += Time.unscaledDeltaTime) { Fade = 1f - t / 0.35f; yield return null; }
            Fade = 0f; warping = false; hideLoadUntil = Time.time + 4f;
        }

        public static string Info()
        {
            return "oot: " + (On ? "an" : "aus") + " fahren " + Driving + " state " + state + " clip " + clip + " speed " + speed.ToString("F2") + " yaw " + yaw.ToString("F0")
                   + " cam " + camYaw.ToString("F0") + "/" + camPitch.ToString("F0") + " boden " + (Player.m_localPlayer != null && Player.m_localPlayer.IsOnGround())
                   + " ist " + dbgSpeed.ToString("F2") + " body " + dbgBodyIn.ToString("F2") + " want " + wantH.magnitude.ToString("F2") + " fdt " + dbgDt.ToString("F3") + " drag " + dbgDrag.ToString("F2") + " kin " + (Player.m_localPlayer != null && bodyRef(Player.m_localPlayer) != null && bodyRef(Player.m_localPlayer).isKinematic)
                   + (Player.m_localPlayer != null ? " leben " + Player.m_localPlayer.GetHealth().ToString("F0") + "/" + Player.m_localPlayer.GetMaxHealth().ToString("F0") : "")
                   + " amb " + RenderSettings.ambientSkyColor.ToString("F2") + " sonne " + SunLum.ToString("F2") + " lift " + LiftK.ToString("F2") + " (" + liftMats.Count + " Mat.)";
        }

        // Fernsteuerung (Tests ohne Tastatur): stick x y | a | jump | z on/off
        public static string Remote(string[] a)
        {
            if (a.Length < 2) return Info();
            switch (a[1])
            {
                case "stick": remoteStick = new Vector2(a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 0f, a.Length > 3 ? float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture) : 0f); remoteUntil = Time.time + (a.Length > 4 ? float.Parse(a[4], System.Globalization.CultureInfo.InvariantCulture) : 3f); return "stick " + remoteStick;
                case "a": remoteA = true; return "A";
                case "ledge":     // oot ledge: Kante vor Link pruefen (Test) - Wand, Hoehe der Kante
                    {
                        var pl = Player.m_localPlayer; if (pl == null) return "-";
                        Vector3 top, n; float dh; bool ok = LedgeAhead(pl, out top, out n, out dh);
                        var pos = pl.transform.position; var f = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward; RaycastHit h;
                        string wall = Physics.Raycast(pos + Vector3.up * 0.45f, f, out h, 3f, GroundMask(), QueryTriggerInteraction.Ignore) ? "wand " + h.distance.ToString("F2") + " m n " + h.normal.ToString("F2") + " " + h.collider.name : "keine wand (3 m)";
                        RaycastHit t; string hi = "";
                        if (Physics.Raycast(pos + f * 1.0f + Vector3.up * 4f, Vector3.down, out t, 6f, GroundMask(), QueryTriggerInteraction.Ignore)) hi = " boden 1 m vorn " + ((t.point.y - pos.y) / 0.033f).ToString("F0") + " Einh.";
                        return "kante " + ok + " dh " + (dh / 0.033f).ToString("F0") + " Einh. | " + wall + hi;
                    }
                case "yaw":       // oot yaw <grad> | oot yaw x z: Link (und Kamera) in eine Richtung / zu einem Punkt drehen (Test)
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture; var pl = Player.m_localPlayer; if (pl == null || a.Length < 3) return "yaw " + yaw;
                        if (a.Length >= 4) { var d = new Vector3(float.Parse(a[2], ci) - pl.transform.position.x, 0f, float.Parse(a[3], ci) - pl.transform.position.z); yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; }
                        else yaw = float.Parse(a[2], ci);
                        yaw = (yaw % 360f + 360f) % 360f; camYaw = yaw; pl.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
                        return "yaw " + yaw.ToString("F0");
                    }
                case "jump": remoteJump = true; return "jump";
                case "title": return OotTitle.Remote(a);
                case "z": remoteZ = a.Length > 2 && a[2] == "on"; return "z " + remoteZ;
                case "play": return StartScript(a);
                case "mood":       // oot mood on|off: feste Uhrzeit/Gebiets-Wetter an/aus | oot mood gust: Nebel-Boee jetzt
                    if (a.Length > 3 && a[2] == "gust") { MoodGustOn = a[3] != "off"; gustUntil = -1f; moodT = 0f; return "mood: Boeen " + (MoodGustOn ? "an" : "aus"); }   // oot mood gust on|off
                    if (a.Length > 2 && a[2] == "gust") { gustAt = Time.time; gustUntil = -1f; moodT = 0f; return "mood: Boee kommt"; }
                    MoodOn = !(a.Length > 2 && a[2] == "off");
                    if (!MoodOn && EnvMan.instance != null) { try { EnvMan.instance.SetForceEnvironment(""); if (fDbgOn != null) fDbgOn.SetValue(EnvMan.instance, false); } catch { } }
                    return "mood " + (MoodOn ? "an" : "aus");
                case "b": remoteB = true; return "b";
                case "enemy": return OotEnemies.Remote(a);
                case "camat":      // oot camat px py pz lx ly lz | oot camat off: feste Kamera fuer Testbilder (relativ zu Link mit "rel")
                    if (a.Length < 8) { camFix = false; camKeys = null; return "camat aus"; }
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        camFixPos = new Vector3(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci));
                        camFixLook = new Vector3(float.Parse(a[5], ci), float.Parse(a[6], ci), float.Parse(a[7], ci));
                        camFix = true; return "camat " + camFixPos + " -> " + camFixLook;
                    }
                case "hp": if (a.Length > 2) OotCombat.Hp = int.Parse(a[2]); return "hp " + OotCombat.Hp + "/" + OotCombat.MaxHp + " zuletzt " + OotCombat.LastHit;
                case "shield": Story.Flags.Add(ShieldFlag); Story.Save(); return "schild";
                case "sticks": OotItems.Sticks = a.Length > 2 ? int.Parse(a[2]) : 10; Story.Flags.Add("oot_has_sticks"); Story.Save(); return "staebe " + OotItems.Sticks;
                case "c1": remoteC1 = true; return "C-links";
                case "cup": remoteCUp = true; return "C-oben";
                case "wake": return OotWake.Remote(a.Length > 2 ? a[2] : "");
                case "amb":       // oot amb r g b [fog_r fog_g fog_b dichte]: Licht des aktuellen Gebiets ausprobieren
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture; if (curArea == null) return "kein Gebiet";
                        if (a.Length >= 5) { curArea.HasAmb = true; curArea.Amb = new Color(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci)); }
                        if (a.Length >= 9) { curArea.HasFog = true; curArea.FogC = new Color(float.Parse(a[5], ci), float.Parse(a[6], ci), float.Parse(a[7], ci)); curArea.FogD = float.Parse(a[8], ci); }
                        return "gebiet " + curArea.Id + " amb " + curArea.Amb + " nebel " + curArea.FogC + " " + curArea.FogD;
                    }
                case "dev": return OotDev.Remote(a);
                case "hud": OotHud.Hidden = a.Length > 2 && a[2] == "off"; return "hud " + (OotHud.Hidden ? "aus" : "an");
                case "ghost":     // oot ghost on|off: Link + Navi unsichtbar (Rundflug-Aufnahmen)
                    {
                        bool on = a.Length > 2 && a[2] == "on";
                        ghostOn = on;
                        if (on && link != null && ghostOff.Count == 0) foreach (var r in link.GetComponentsInChildren<Renderer>()) if (r.enabled) { r.enabled = false; ghostOff.Add(r); }
                        if (!on) { foreach (var r in ghostOff) if (r != null) r.enabled = true; ghostOff.Clear(); }
                        if (OotNavi.T != null) OotNavi.T.gameObject.SetActive(!on);
                        return "ghost " + (on ? "an" : "aus");
                    }
                case "camfly":    // oot camfly <datei> | off: Kamerafahrt (Datei im GameImporter-Ordner)
                    {
                        if (a.Length < 3 || a[2] == "off") { camKeys = null; camFix = false; return "camfly aus"; }
                        var f = Path.Combine(GameImporter.Remote.Dir, a[2]); if (!File.Exists(f)) return "fehlt: " + f;
                        var ci = System.Globalization.CultureInfo.InvariantCulture; var ks = new List<float[]>();
                        foreach (var ln in File.ReadAllLines(f))
                        {
                            var w = ln.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (w.Length < 7 || w[0].StartsWith("#")) continue;
                            var e = new float[7]; for (int i = 0; i < 7; i++) e[i] = float.Parse(w[i], ci); ks.Add(e);
                        }
                        if (ks.Count == 0) return "camfly: keine Punkte";
                        camKeys = ks; camKeyT0 = Time.time; CamFly();
                        return "camfly " + ks.Count + " Punkte, " + ks[ks.Count - 1][0].ToString("F1") + " s";
                    }
                case "atmos":     // oot atmos [on|off]: Teilchen-Stimmung (Deku-Baum) an/aus, Zustand
                    if (a.Length > 2) OotAtmos.Enabled = a[2] != "off";
                    return OotAtmos.Info();
                case "area": return "gebiet " + (curArea != null ? curArea.Id + " env " + curArea.Env + " zeit " + curArea.Time + " hall " + curArea.Reverb + " musik " + (curArea.Track != null ? Path.GetFileName(curArea.Track) : "-") : "-");
                case "aimat":     // oot aimat x y z: Schleuder auf einen Weltpunkt richten (Test)
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture; var pl = Player.m_localPlayer; if (pl == null || a.Length < 5) return "aimat x y z";
                        var tgt = new Vector3(float.Parse(a[2], ci), float.Parse(a[3], ci), float.Parse(a[4], ci));
                        var from = pl.transform.position + Vector3.up * 1.25f; var dv = tgt - from;
                        aimYaw = Mathf.Atan2(dv.x, dv.z) * Mathf.Rad2Deg; aimPitch = -Mathf.Atan2(dv.y, new Vector2(dv.x, dv.z).magnitude) * Mathf.Rad2Deg;
                        return "aim " + aimYaw.ToString("F0") + "/" + aimPitch.ToString("F0");
                    }
                case "obj": return OotYdanObj.Remote(a);
                case "torch": return OotDungeon.TorchRemote(a);
                case "web": return OotDungeon.WebRemote();
                case "c3": remoteC3 = true; return "C-rechts";
                case "shot": return "schuss " + LastShot;
                case "seeds": if (a.Length > 2) OotItems.Seeds = int.Parse(a[2]); Story.Flags.Add("oot_slingshot"); return "samen " + OotItems.Seeds;
                case "r": remoteR = a.Length > 2 && a[2] == "on"; return "R " + remoteR;
                case "fire": return "stab " + StickOut + " feuer " + stickFire.ToString("F1") + " spitze " + StickTip().ToString("F2");
                case "rupees": OotHud.Rupees = a.Length > 2 ? int.Parse(a[2]) : 99; return "rubine " + OotHud.Rupees;
                case "flag": if (a.Length > 3 && a[3] == "off") Story.Flags.Remove(a[2]); else if (a.Length > 2) Story.Flags.Add(a[2]); Story.Save(); return "flags " + string.Join(",", Story.Flags);
                case "text": return "text " + (Text ?? "-").Replace("\n", " / ") + " | state " + state;
                case "items":      // oot items reset: Schwert weg, Truhe zu, Buesche wieder da | oot items sword: Schwert geben
                    if (a.Length > 2 && a[2] == "reset") { OotItems.ResetAll(); return "items reset"; }
                    if (a.Length > 2 && a[2] == "sword") { Story.Flags.Add(OotItems.SwordFlag); Story.Save(); return "schwert"; }
                    if (a.Length > 2 && a[2] == "chest") { var pl = Player.m_localPlayer; if (pl != null) { var sp = OotItems.ChestStand() + Vector3.up * 0.2f; var bd = bodyRef(pl); if (bd != null) { bd.position = sp; bd.velocity = Vector3.zero; } pl.transform.position = sp; yaw = OotItems.ChestFacingYaw(); } return "an der truhe"; }
                    if (a.Length > 4 && a[2] == "bushes")
                    {
                        var ci = System.Globalization.CultureInfo.InvariantCulture;
                        float bx = float.Parse(a[3], ci), bz = float.Parse(a[4], ci); int bn = a.Length > 5 ? int.Parse(a[5]) : 6; float byaw = a.Length > 6 ? float.Parse(a[6], ci) : 90f;
                        return "buesche " + OotItems.SpawnRow(new Vector3(bx, 0f, bz), Quaternion.Euler(0f, byaw, 0f) * Vector3.forward, bn, 1.7f);
                    }
                    return "items: reset|sword|chest|bushes x z n yaw";
                case "navi":       // oot navi <staerke>: Navis Licht im Dunkeln
                    NaviLight = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); return "navi " + NaviLight;
                case "lift":       // oot lift <s0> <s1>: Sonnenhelligkeit, ab der die Schattenaufhellung beginnt / voll ist
                    LiftS0 = float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture); LiftS1 = float.Parse(a[3], System.Globalization.CultureInfo.InvariantCulture);
                    return "lift " + LiftS0 + ".." + LiftS1;
                case "pitch": camPitch = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 14f; mouseT = Time.time + 9999f; return "pitch " + camPitch;
                case "dist": camDist = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : 5.2f; return "dist " + camDist;
                case "mat": return MatDump(link) + " ||| " + MatDump(ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(a.Length > 2 ? a[2] : "Greydwarf") : null);
                case "stop": script = null; if (recOn) { recOn = false; Time.captureFramerate = 0; } return "stop";
                case "cam": camYaw = a.Length > 2 ? float.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture) : yaw; return "cam " + camYaw;
            }
            return Info();
        }
        static string MatDump(GameObject go)
        {
            if (go == null) return "kein Objekt";
            var sb = new System.Text.StringBuilder(go.name + ":");
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var mt = r.sharedMaterial; if (mt == null || mt.shader == null) continue;
                sb.Append(" [" + r.name + " " + mt.name + " sh=" + mt.shader.name);
                var sh = mt.shader;
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    var n = sh.GetPropertyName(i); var t = sh.GetPropertyType(i);
                    if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range) sb.Append(" " + n + "=" + mt.GetFloat(n).ToString("F2"));
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Color) sb.Append(" " + n + "=" + mt.GetColor(n).ToString("F2"));
                    else if (t == UnityEngine.Rendering.ShaderPropertyType.Texture) { var tx = mt.GetTexture(n); sb.Append(" " + n + "=" + (tx != null ? tx.name : "-")); }
                }
                sb.Append(" kw=" + string.Join(",", mt.shaderKeywords) + "]");
                break;
            }
            return sb.ToString();
        }

        static Vector2 remoteStick; static float remoteUntil; static bool remoteA, remoteJump, remoteZ, remoteB, remoteC1, remoteCUp, remoteR, remoteC3;

        // Ablauf-Skript (Testvideos ohne Tastatur): Zeilen "t x y [a] [j] [z] [cam=grad]" in Spielzeit, gilt bis zur naechsten Zeile.
        // oot play <datei in config/GameImporter> [aufnahme-name] [sekunden]
        class Step { public float T, X, Y; public bool A, H, J, Z, B; public float Cam = float.NaN; }
        static float scriptLogT;
        static List<Step> script; static float scriptT0; static int scriptI = -1; static float scriptDur;
        static bool recOn; static string recDir; static int recFrame;

        static string StartScript(string[] a)
        {
            var f = Path.Combine(GameImporter.Remote.Dir, a.Length > 2 ? a[2] : "oot_script.txt");
            if (!File.Exists(f)) return "fehlt: " + f;
            script = new List<Step>();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var ln in File.ReadAllLines(f))
            {
                var w = ln.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (w.Length < 3 || w[0].StartsWith("#")) continue;
                var st = new Step { T = float.Parse(w[0], ci), X = float.Parse(w[1], ci), Y = float.Parse(w[2], ci) };
                for (int i = 3; i < w.Length; i++)
                {
                    if (w[i] == "a") st.A = true; else if (w[i] == "h") st.H = true; else if (w[i] == "j") st.J = true; else if (w[i] == "z") st.Z = true; else if (w[i] == "b") st.B = true;
                    else if (w[i].StartsWith("cam=")) st.Cam = float.Parse(w[i].Substring(4), ci);
                }
                script.Add(st);
            }
            scriptT0 = Time.time; scriptI = -1; scriptLogT = 0f;
            scriptDur = a.Length > 4 ? float.Parse(a[4], ci) : (script.Count > 0 ? script[script.Count - 1].T + 1f : 1f);
            if (a.Length > 3)
            {
                recDir = Path.Combine(GameImporter.Remote.Dir, "rec", a[3]);
                if (Directory.Exists(recDir)) { var alt = recDir + "_alt_" + DateTime.Now.ToString("HHmmss"); Directory.Move(recDir, alt); }
                Directory.CreateDirectory(recDir); recFrame = 0; recOn = true;
                Time.captureFramerate = 50;
                if (camKeys != null) camKeyT0 = Time.time;          // Kamerafahrt beginnt mit der Aufnahme
                if (StoryUi.I != null) StoryUi.I.StartCoroutine(RecLoop());
            }
            return "skript " + script.Count + " Zeilen, " + scriptDur.ToString("F1") + " s" + (recOn ? ", Aufnahme " + recDir : "");
        }

        static System.Collections.IEnumerator RecLoop()
        {
            while (recOn)
            {
                yield return new WaitForEndOfFrame();
                if (!recOn) break;
                var tx = ScreenCapture.CaptureScreenshotAsTexture();
                try { File.WriteAllBytes(Path.Combine(recDir, "f" + (++recFrame).ToString("D5") + ".jpg"), tx.EncodeToJPG(92)); }
                catch (Exception e) { Plugin.Quiet(e, "OotPlay.Rec"); }
                UnityEngine.Object.Destroy(tx);
            }
        }

        static void ScriptInput()
        {
            if (script == null) return;
            float t = Time.time - scriptT0;
            if (t > scriptDur)
            {
                script = null; remoteUntil = 0f;
                if (recOn) { recOn = false; Time.captureFramerate = 0; Plugin.Log("OoT-Aufnahme fertig: " + recFrame + " Bilder in " + recDir); GameImporter.Remote.Out("oot rec fertig " + recFrame); }
                return;
            }
            if (t > scriptLogT) { scriptLogT = t + 0.25f; var pp = Player.m_localPlayer; if (pp != null) GameImporter.Remote.Out("skript t " + t.ToString("F2") + " pos " + pp.transform.position.ToString("F2") + " " + state + " v " + speed.ToString("F1") + " yaw " + yaw.ToString("F0")); }
            int i = scriptI; while (i + 1 < script.Count && script[i + 1].T <= t) i++;
            if (i < 0) return;
            mouseT = Time.time;                       // Kamera waehrend Aufnahmen nicht nachziehen (Stick bleibt eindeutig)
            var st = script[i];
            stick = Vector2.ClampMagnitude(new Vector2(st.X, st.Y), 1f);
            zHeld = st.Z; aHeld = st.H || st.A;
            if (i != scriptI)
            {
                if (st.A) aDown = true;
                if (st.B) bDown = true;
                if (st.J) { jumpDown = true; var p = Player.m_localPlayer; if (p != null && !zHeld && p.IsOnGround()) { var b = bodyRef(p); if (b != null) b.velocity = new Vector3(b.velocity.x, p.m_jumpForce, b.velocity.z); } }
                if (!float.IsNaN(st.Cam)) { camYaw = st.Cam; mouseT = Time.time; }
                scriptI = i;
            }
        }

        // Fernsteuerungs-Eingaben in die normale Eingabe mischen (in Tick nach dem Lesen der Tasten)
        public static void RemoteInput()
        {
            if (Time.time < remoteUntil && remoteStick.sqrMagnitude > 0.01f) stick = Vector2.ClampMagnitude(remoteStick, 1f);
            if (remoteA) { aDown = true; remoteA = false; }
            if (remoteB) { bDown = true; remoteB = false; }
            if (remoteC1) { c1Down = true; remoteC1 = false; }
            if (remoteCUp) { CUpDown = true; remoteCUp = false; }
            if (remoteR) rHeld = true;
            if (remoteC3) { c3Down = true; remoteC3 = false; }
            if (remoteJump) { jumpDown = true; remoteJump = false; var p = Player.m_localPlayer; if (p != null && !zHeld && p.IsOnGround()) { var b = bodyRef(p); if (b != null) b.velocity = new Vector3(b.velocity.x, p.m_jumpForce, b.velocity.z); } }
            if (remoteZ) zHeld = true;
            ScriptInput();
            stickYaw = camYaw + Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg;
        }
    }
}
