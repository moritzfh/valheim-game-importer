using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace GameImporter
{
    // Cutscene aus den Originaldaten (tools/witcher3/w3cutscene.py export): Kamerafahrt Bild fuer Bild, Wege der Darsteller,
    // Zeilen mit Stimme, Blenden, Gelaende ausblenden, Clipping. Darsteller sind Valheim-Figuren:
    //   "player" = ein Double des eigenen Charakters (der echte Spieler wartet unsichtbar), sonst NPC-Id aus npcs.json.
    // Datei (JSON): fps, dur, fov_kind ("v" = senkrechter Bildwinkel wie bei Witcher, "h" = waagerecht), time (Tageszeit 0..1, Standard
    // 0.5 = Mittag), cam {pos[], rot[], fov[]}, cuts[], actors {rolle: {step, pos[], yaw[], pose[] 0 stehen/1 sitzen/2 liegen,
    // bones = Knochen-Datei (Original-Animation, siehe CutsceneBones)}}, lines [{t, speaker, text, voice}], fades [{t, in, dur}],
    // hide_terrain [[t0,t1]], clip [[t, near, far]].
    // Zusaetzlich: "rate" (Tempo, z. B. 0.821 = PAL-OoT 16,4 Spielbilder/s bei einer Spur in 20 Bildern/s, damit der Original-
    // Mitschnitt synchron bleibt), "loop" (Startbildschirm), "follow" (Spieler unsichtbar unter der Kamera mitfuehren, damit
    // Valheim dort Gelaende laedt; danach zurueck), Tonspuren "audio" [{t (Spurzeit), file, kind}] laufen in Echtzeit.
    public class CutsceneTrack
    {
        public float Fps = 30f, Dur, Rate = 1f;
        public bool Loop, NoSkip, Follow, NoBars;
        float camLightTrack = 1f; bool grassOff, grassWas;          // "cam_light": Faktor Kameralicht, "grass": false = Valheim-Gras aus
        bool hfov = true;
        Vector3[] camPos; Quaternion[] camRot; float[] fov;
        readonly HashSet<int> cutAt = new HashSet<int>();
        class Actor { public string Role; public int Step; public Vector3[] Pos; public float[] Yaw; public int[] Pose; public GameObject Go; public int LastPose = -1; public bool Double; public BoneAnim Bones; public BoneRig Rig; public float Scale = 1f, HeadScale = 1f; public bool ExactHip; public List<KeyValuePair<float, object>> Outfits = new List<KeyValuePair<float, object>>(); public int OutfitIdx = -2; public Dictionary<string, object> BaseLook; public bool Hidden; public List<float[]> Shift = new List<float[]>(); }
        class Line { public float T; public string Speaker; public object Text, Voice; }
        class Fade { public float T, Dur; public bool In; public Color Col = Color.black; }
        // Tonspuren aus dem Original (Geraeusche der Cutscene, Musik), zeitgenau zum Bild
        class Aud { public float T; public string File, Kind; public AudioClip Clip; public AudioSource Src; public bool Done; }
        readonly List<Aud> audio = new List<Aud>();
        public static bool MusicActive;
        // Kleine Darsteller ohne Menschenskelett (z. B. Spielzeug-Arachas): Valheim-Kreatur nur als Bild, folgt Position/Ausrichtung
        class Prop { public string Name, Prefab, Model, Clip; public Animation Legacy; public float Scale, LightMul = 1f; public Vector3[] Pos, Fwd, Up; public bool[] Vis; public GameObject Go; public Animator Anim; public bool Shown = true, HasSpeed; public Color FxCol = new Color(0.78f, 0.88f, 1f); public float FxGlow = 0.8f; }
        readonly List<Prop> props = new List<Prop>();
        // Bewegte feste Modelle (z. B. Zugbruecke): Statics mit Tag bekommen pro Bild Position + Drehung ("movers")
        class Mover { public string Tag; public Vector3[] Pos; public Quaternion[] Rot; }
        // Stimmung wie im Original: "env": {"weather": "Rain", "ambient": [r,g,b], "fog_color": [r,g,b], "fog": 1.5, "sun": 0.2,
        //   "fire": [{"pos": [x,y,z], "size": 1.2}], "lights": [{"pos": [...], "color": [...], "range": 8, "intensity": 2}],
        //   "flashes": [[t, dauer, staerke], ...]}  (Blitze: Umgebungslicht kurz hell)
        object env; string envWeather; Color? envAmb, envFogCol; float envFog = 1f, envSun = 1f, envSunBase = -1f, envSunSet = -1f, envFogBase = -1f, envFogSet = -1f;
        readonly List<GameObject> envObjs = new List<GameObject>(); readonly List<Vector3> flashes = new List<Vector3>();
        readonly List<Mover> movers = new List<Mover>();
        readonly List<Actor> actors = new List<Actor>();
        readonly List<Line> lines = new List<Line>();
        readonly List<Fade> fades = new List<Fade>();
        readonly List<Vector2> hideTerrain = new List<Vector2>();
        readonly List<Vector3> clips = new List<Vector3>();
        readonly List<string> hideTags = new List<string>();
        Light camLight;
        float t0 = -1f; int lineIdx; bool terrainHidden; bool hidePlayer;
        public string Subtitle; float subUntil;
        Vector3 playerHome; bool havePlayer;
        float timeOfDay = 0.5f; bool hudWas, dbgWas, mapWas; float dbgTWas; GameObject mapRoot;

        public static CutsceneTrack Load(string path)
        {
            var o = Json.Parse(File.ReadAllText(path));
            var t = new CutsceneTrack { Fps = Json.F(o, "fps", 30f), Dur = Json.F(o, "dur", 0f), hfov = Json.S(o, "fov_kind", "h") == "h", timeOfDay = Json.F(o, "time", 0.5f), hidePlayer = Json.B(o, "hide_player") };
            t.Rate = Mathf.Clamp(Json.F(o, "rate", 1f), 0.1f, 4f); t.Loop = Json.B(o, "loop"); t.Follow = Json.B(o, "follow");
            t.NoBars = Json.Has(o, "bars") && !Json.B(o, "bars");
            t.camLightTrack = Json.F(o, "cam_light", 1f); t.grassOff = Json.Has(o, "grass") && !Json.B(o, "grass");
            var cam = Json.O(o, "cam");
            var P = Floats(Json.L(cam, "pos")); var R = Floats(Json.L(cam, "rot")); var F = Floats(Json.L(cam, "fov"));
            int n = P.Length / 3;
            t.camPos = new Vector3[n]; t.camRot = new Quaternion[n]; t.fov = new float[n];
            for (int i = 0; i < n; i++)
            {
                t.camPos[i] = new Vector3(P[i * 3], P[i * 3 + 1], P[i * 3 + 2]);
                t.camRot[i] = i * 4 + 3 < R.Length ? new Quaternion(R[i * 4], R[i * 4 + 1], R[i * 4 + 2], R[i * 4 + 3]) : Quaternion.identity;
                t.fov[i] = i < F.Length && F[i] > 1f ? F[i] : 50f;
            }
            if (t.Dur <= 0f) t.Dur = n / t.Fps;
            foreach (var c in Json.L(o, "cuts") ?? new List<object>()) t.cutAt.Add(Convert.ToInt32(c));
            var acts = Json.O(o, "actors");
            if (acts != null) foreach (var kv in acts)
                {
                    var a = new Actor { Role = kv.Key, Step = Math.Max(1, (int)Json.F(kv.Value, "step", 3f)), Scale = Json.F(kv.Value, "scale", 1f), HeadScale = Json.F(kv.Value, "head_scale", 1f), ExactHip = Json.B(kv.Value, "exact_hip") };
                    var ap = Floats(Json.L(kv.Value, "pos")); var ay = Floats(Json.L(kv.Value, "yaw")); var po = Floats(Json.L(kv.Value, "pose"));
                    int m = ap.Length / 3;
                    a.Pos = new Vector3[m]; a.Yaw = new float[m]; a.Pose = new int[m];
                    for (int i = 0; i < m; i++) { a.Pos[i] = new Vector3(ap[i * 3], ap[i * 3 + 1], ap[i * 3 + 2]); a.Yaw[i] = i < ay.Length ? ay[i] : 0f; a.Pose[i] = i < po.Length ? (int)po[i] : 0; }
                    foreach (var sh in Json.L(kv.Value, "shift") ?? new List<object>()) { var f5 = Floats(sh as List<object>); if (f5.Length >= 5) a.Shift.Add(f5); }
                    foreach (var of in Json.L(kv.Value, "outfits") ?? new List<object>())
                        a.Outfits.Add(new KeyValuePair<float, object>(Json.F(of, "t"), Json.O(of, "set")));
                    a.Outfits.Sort((x, y) => x.Key.CompareTo(y.Key));
                    var bf = Json.S(kv.Value, "bones");
                    if (!string.IsNullOrEmpty(bf))
                    {
                        try { a.Bones = BoneAnim.Load(Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(bf))); }
                        catch (Exception e) { Plugin.Warn("Cutscene-Knochen " + bf + ": " + e.Message); }
                    }
                    t.actors.Add(a);
                }
            foreach (var l in Json.L(o, "lines") ?? new List<object>())
                t.lines.Add(new Line { T = Json.F(l, "t"), Speaker = Json.S(l, "speaker"), Text = Json.V(l, "text"), Voice = Json.V(l, "voice") });
            t.lines.Sort((a, b) => a.T.CompareTo(b.T));
            foreach (var f in Json.L(o, "fades") ?? new List<object>()) t.fades.Add(new Fade { T = Json.F(f, "t"), Dur = Json.F(f, "dur"), In = Json.B(f, "in"), Col = FadeCol(Json.L(f, "col")) });
            foreach (var h in Json.L(o, "hide_terrain") ?? new List<object>()) { var l = h as List<object>; if (l != null && l.Count >= 2) t.hideTerrain.Add(new Vector2(F0(l[0]), F0(l[1]))); }
            foreach (var c in Json.L(o, "clip") ?? new List<object>()) { var l = c as List<object>; if (l != null && l.Count >= 3) t.clips.Add(new Vector3(F0(l[0]), F0(l[1]), F0(l[2]))); }
            foreach (var h in Json.L(o, "hide_tags") ?? new List<object>()) if (h is string) t.hideTags.Add((string)h);
            var pr = Json.O(o, "props");
            if (pr != null) foreach (var kv in pr)
                {
                    var P3 = Floats(Json.L(kv.Value, "pos")); var F3 = Floats(Json.L(kv.Value, "fwd")); var U3 = Floats(Json.L(kv.Value, "up")); var V = Floats(Json.L(kv.Value, "vis"));
                    int m = P3.Length / 3; var p = new Prop { Name = kv.Key, Prefab = Json.S(kv.Value, "prefab"), Model = string.IsNullOrEmpty(Json.S(kv.Value, "model", "")) ? null : Path.Combine(Path.GetDirectoryName(path), Json.S(kv.Value, "model").Replace('/', Path.DirectorySeparatorChar)), Clip = Json.S(kv.Value, "clip", "cs"), Scale = Json.F(kv.Value, "scale", 1f), Pos = new Vector3[m], Fwd = new Vector3[m], Up = new Vector3[m], Vis = new bool[m] };
                    var fxc = Col(Json.V(kv.Value, "color")); if (fxc != null) p.FxCol = fxc.Value;
                    p.FxGlow = Json.F(kv.Value, "glow", 0.8f); p.LightMul = Json.F(kv.Value, "light", 1f);
                    for (int i = 0; i < m; i++)
                    {
                        p.Pos[i] = new Vector3(P3[i * 3], P3[i * 3 + 1], P3[i * 3 + 2]);
                        p.Fwd[i] = i * 3 + 2 < F3.Length ? new Vector3(F3[i * 3], F3[i * 3 + 1], F3[i * 3 + 2]) : Vector3.forward;
                        p.Up[i] = i * 3 + 2 < U3.Length ? new Vector3(U3[i * 3], U3[i * 3 + 1], U3[i * 3 + 2]) : Vector3.up;
                        p.Vis[i] = i >= V.Length || V[i] > 0.5f;
                    }
                    if (m > 0) t.props.Add(p);
                }
            t.env = Json.V(o, "env");
            if (t.env != null)
            {
                t.envWeather = Json.S(t.env, "weather", null); t.envAmb = Col(Json.V(t.env, "ambient")); t.envFogCol = Col(Json.V(t.env, "fog_color"));
                t.envFog = Json.F(t.env, "fog", 1f); t.envSun = Json.F(t.env, "sun", 1f);
                foreach (var fl in Json.L(t.env, "flashes") ?? new List<object>()) { var f3 = Floats(fl as List<object>); if (f3.Length >= 3) t.flashes.Add(new Vector3(f3[0], f3[1], f3[2])); }
            }
            var mv = Json.O(o, "movers");
            if (mv != null) foreach (var kv in mv)
                {
                    var P3 = Floats(Json.L(kv.Value, "pos")); var R4 = Floats(Json.L(kv.Value, "rot"));
                    int m = Math.Min(P3.Length / 3, R4.Length / 4);
                    var mo = new Mover { Tag = kv.Key, Pos = new Vector3[m], Rot = new Quaternion[m] };
                    for (int i = 0; i < m; i++) { mo.Pos[i] = new Vector3(P3[i * 3], P3[i * 3 + 1], P3[i * 3 + 2]); mo.Rot[i] = new Quaternion(R4[i * 4], R4[i * 4 + 1], R4[i * 4 + 2], R4[i * 4 + 3]); }
                    if (m > 0) t.movers.Add(mo);
                }
            foreach (var au in Json.L(o, "audio") ?? new List<object>())
            {
                var f = Json.S(au, "file"); if (string.IsNullOrEmpty(f)) continue;
                t.audio.Add(new Aud { T = Json.F(au, "t"), Kind = Json.S(au, "kind", "sfx"), File = Path.Combine(Path.GetDirectoryName(path), Path.GetFileName(f)) });
            }
            return t;
        }

        static float F0(object o) { return o is double ? (float)(double)o : 0f; }
        static Color? Col(object o) { var l = o as List<object>; return l != null && l.Count >= 3 ? new Color(F0(l[0]), F0(l[1]), F0(l[2])) : (Color?)null; }
        static Vector3 V3l(object o) { var l = o as List<object>; return l != null && l.Count >= 3 ? new Vector3(F0(l[0]), F0(l[1]), F0(l[2])) : Vector3.zero; }

        void EnvStart()
        {
            if (env == null) return;
            try { if (!string.IsNullOrEmpty(envWeather) && EnvMan.instance != null) EnvMan.instance.SetForceEnvironment(envWeather); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Wetter"); }
            foreach (var fj in Json.L(env, "fire") ?? new List<object>())
            {
                try { var fg = FireFx.Spawn(V3l(Json.V(fj, "pos")), Json.F(fj, "size", 1.2f), Json.F(fj, "rate", 25f), !Json.Has(fj, "light") || Json.B(fj, "light")); if (fg != null) envObjs.Add(fg); }
                catch (Exception e) { Plugin.Quiet(e, "Cutscene.Feuer"); }
            }
            if (Json.F(env, "motes", 0f) > 0f)
            {
                try { envObjs.Add(AmbientFx.Spawn(Json.F(env, "motes", 1f))); } catch (Exception e) { Plugin.Warn("Cutscene-Stimmung: " + e.Message); }
            }
            foreach (var lj in Json.L(env, "lights") ?? new List<object>())
            {
                var go = new GameObject("gi_cs_light"); go.transform.position = V3l(Json.V(lj, "pos"));
                var li = go.AddComponent<Light>(); li.type = LightType.Point; li.color = Col(Json.V(lj, "color")) ?? new Color(1f, 0.6f, 0.3f);
                li.range = Json.F(lj, "range", 8f); li.intensity = Json.F(lj, "intensity", 2f); li.shadows = LightShadows.None; li.renderMode = LightRenderMode.ForcePixel;
                envObjs.Add(go);
            }
        }

        void EnvTick(float t)
        {
            if (env == null) return;
            float flash = 0f;
            foreach (var f in flashes) if (t >= f.x && t < f.x + f.y) flash = Mathf.Max(flash, f.z * (1f - (t - f.x) / Mathf.Max(f.y, 0.01f)));
            if (envAmb.HasValue)
            {
                var a = envAmb.Value + new Color(flash, flash, flash * 1.1f);
                RenderSettings.ambientLight = a; RenderSettings.ambientSkyColor = a; RenderSettings.ambientEquatorColor = a * 0.85f; RenderSettings.ambientGroundColor = a * 0.6f;
            }
            if (envFogCol.HasValue) RenderSettings.fogColor = envFogCol.Value + new Color(flash, flash, flash) * 0.5f;
            if (envFog != 1f)
            {
                if (!Mathf.Approximately(RenderSettings.fogDensity, envFogSet)) envFogBase = RenderSettings.fogDensity;
                RenderSettings.fogDensity = envFogSet = envFogBase * envFog;
            }
            if (EnvMan.instance != null && EnvMan.instance.m_dirLight != null && (envSun != 1f || flash > 0f))
            {
                var L = EnvMan.instance.m_dirLight;
                if (!Mathf.Approximately(L.intensity, envSunSet)) envSunBase = L.intensity;
                L.intensity = envSunSet = envSunBase * envSun + flash * 1.5f;
            }
        }

        void EnvEnd()
        {
            foreach (var g in envObjs) if (g != null) UnityEngine.Object.Destroy(g);
            envObjs.Clear();
            try { if (!string.IsNullOrEmpty(envWeather) && EnvMan.instance != null) EnvMan.instance.SetForceEnvironment(""); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Wetter"); }
        }
        static float[] Floats(List<object> l)
        {
            if (l == null) return new float[0];
            var r = new float[l.Count];
            for (int i = 0; i < l.Count; i++) r[i] = l[i] is double ? (float)(double)l[i] : 0f;
            return r;
        }

        // Punkte, um die vorab gebaut werden soll (Kamera an jedem Schnitt + Darsteller)
        // Tonspuren laden (waehrend "Laedt..." vor der Cutscene)
        public bool AudioReady { get { foreach (var a in audio) if (!a.Done) return false; return true; } }
        public System.Collections.IEnumerator LoadAudio()
        {
            foreach (var a in audio)
            {
                if (!File.Exists(a.File)) { Plugin.Warn("Cutscene-Ton fehlt: " + a.File); a.Done = true; continue; }
                var type = a.File.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : a.File.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? AudioType.MPEG : AudioType.WAV;
                using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + a.File.Replace('\\', '/'), type))
                {
                    yield return rq.SendWebRequest();
                    if (rq.result == UnityWebRequest.Result.Success) a.Clip = DownloadHandlerAudioClip.GetContent(rq);
                    else Plugin.Warn("Cutscene-Ton " + a.File + ": " + rq.error);
                }
                a.Done = true;
            }
        }

        static float Pref(string k) { try { return Mathf.Clamp01(PlayerPrefs.GetFloat(k, 1f)); } catch { return 1f; } }

        void TickAudio(float t)
        {
            foreach (var a in audio)
            {
                if (a.Clip == null) continue;
                if (a.Src == null)
                {
                    var go = new GameObject("gi_cs_audio_" + a.Kind); UnityEngine.Object.DontDestroyOnLoad(go);
                    a.Src = go.AddComponent<AudioSource>(); a.Src.spatialBlend = 0f; a.Src.loop = false; a.Src.playOnAwake = false; a.Src.clip = a.Clip;
                    a.Src.bypassReverbZones = true; a.Src.priority = 0;
                }
                a.Src.volume = (a.Kind == "music" ? 0.85f * Pref("MusicVolume") : Pref("SfxVolume")) * (AudioListener.volume > 0f ? 1f : 0f);
                float at = (t - a.T) / Rate;
                bool want = at >= 0f && at < a.Clip.length - 0.05f;
                if (!want || frozen >= 0f) { if (a.Src.isPlaying) a.Src.Pause(); continue; }
                if (!a.Src.isPlaying) { a.Src.time = Mathf.Clamp(at, 0f, a.Clip.length - 0.1f); a.Src.Play(); }
                else if (Mathf.Abs(a.Src.time - at) > 0.15f) a.Src.time = Mathf.Clamp(at, 0f, a.Clip.length - 0.1f);   // nach Springen/Ruckeln nachziehen
                if (MusicVol < 1f) a.Src.volume *= MusicVol;
            }
        }

        public List<Vector3> Preload()
        {
            var l = new List<Vector3>();
            if (camPos.Length > 0) l.Add(camPos[0]);
            foreach (var c in cutAt) if (c < camPos.Length) l.Add(camPos[c]);
            foreach (var a in actors) if (a.Pos.Length > 0) l.Add(a.Pos[0]);
            return l;
        }

        public Vector3 FirstCam { get { return camPos.Length > 0 ? camPos[0] : Vector3.zero; } }
        public Quaternion FirstRot { get { return camRot.Length > 0 ? camRot[0] : Quaternion.identity; } }
        // t0 darf negativ werden (Sprung weiter als die bisherige Spielzeit), daher eigener Merker fuer "gestartet"
        bool started;
        public float T { get { return !started ? 0f : ForceT >= 0f ? ForceT : frozen >= 0f ? frozen : (Time.unscaledTime - t0) * Rate; } }
        // Aufnahme (Shots): Zeit der Spur von aussen vorgeben, Bild fuer Bild
        public static float ForceT = -1f;
        // Staerke des Cutscene-Kameralichts (Einstellung "cs_light", z. B. 0.3 fuer dunkle Nachtszenen)
        public static float CamLightMul = 1f;

        // Fehlersuche/Zuordnung zum Original: wo ist jede Figur auf dem Bild (Kopf/Huefte als Bildkoordinaten 0..1 und
        // Abstand zur Kamera), kopfueber/liegend, Blick zur Kamera. Eine JSON-Zeile pro Bild.
        static readonly Dictionary<GameObject, Transform[]> boneCache = new Dictionary<GameObject, Transform[]>();
        static Transform FindBone(Transform r, string n)
        {
            if (string.Equals(r.name, n, StringComparison.OrdinalIgnoreCase)) return r;
            for (int i = 0; i < r.childCount; i++) { var f = FindBone(r.GetChild(i), n); if (f != null) return f; }
            return null;
        }
        static string V3(Vector3 v) { return "[" + v.x.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + v.y.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "," + v.z.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "]"; }
        public string ActorLog(Camera c)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"t\":").Append(T.ToString("0.###", ic)).Append(",\"cam\":").Append(V3(c.transform.position)).Append(",\"fwd\":").Append(V3(c.transform.forward))
              .Append(",\"fov\":").Append(c.fieldOfView.ToString("0.#", ic)).Append(",\"upv\":").Append(V3(c.transform.up)).Append(",\"a\":{");
            bool first = true;
            foreach (var a in actors)
            {
                if (a.Go == null || !a.Go.activeInHierarchy) continue;
                Transform[] b;
                if (!boneCache.TryGetValue(a.Go, out b)) { b = new[] { FindBone(a.Go.transform, "Head"), FindBone(a.Go.transform, "Hips"), FindBone(a.Go.transform, "LeftFoot") }; boneCache[a.Go] = b; }
                if (b[0] == null || b[1] == null) continue;
                var h = c.WorldToViewportPoint(b[0].position); var p = c.WorldToViewportPoint(b[1].position);
                float face = Vector3.Dot(a.Go.transform.forward, (c.transform.position - b[1].position).normalized);   // >0: Figur schaut zur Kamera
                float up = b[0].position.y - b[1].position.y;                                                         // <0: kopfueber
                float foot = b[2] != null ? b[1].position.y - b[2].position.y : 0f;                                   // ~0: liegt
                if (!first) sb.Append(","); first = false;
                sb.Append("\"").Append(a.Role).Append("\":{\"h\":").Append(V3(h)).Append(",\"p\":").Append(V3(p))
                  .Append(",\"hw\":").Append(V3(b[0].position)).Append(",\"pw\":").Append(V3(b[1].position))
                  .Append(",\"up\":").Append(up.ToString("0.##", ic)).Append(",\"foot\":").Append(foot.ToString("0.##", ic))
                  .Append(",\"face\":").Append(face.ToString("0.##", ic)).Append("}");
            }
            sb.Append("}}");
            return sb.ToString();
        }
        float frozen = -1f;
        // Fehlersuche/Vergleich: an eine Stelle springen und anhalten ("cutseek 41.5", "cutseek off")
        public void Seek(float t, bool freeze)
        {
            if (!started) return;
            lineIdx = 0; while (lineIdx < lines.Count && lines[lineIdx].T < t - 0.05f) lineIdx++;
            frozen = freeze ? t : -1f;
            t0 = Time.unscaledTime - t / Rate;
        }
        public bool Done { get { return started && T >= Dur; } }
        // Lautstaerke von aussen (Startbildschirm blendet beim Weiterdruecken aus)
        public static float MusicVol = 1f;

        /// <summary>Schleife: wieder von vorn (Startbildschirm), ohne neu zu laden.</summary>
        public void Restart()
        {
            t0 = Time.unscaledTime; lineIdx = 0; Subtitle = null;
            foreach (var a in audio) if (a.Src != null) a.Src.Stop();
        }

        /// <summary>"follow": Spieler (unsichtbar, festgehalten) unter die Kamera stellen, damit Valheim dort Gelaende laedt.</summary>
        /// <summary>Wo der Spieler vor der Cutscene stand (vor dem Mitfuehren beim Laden merken).</summary>
        public bool HomeIfFollowing(out Vector3 home) { home = playerHome; return Follow && havePlayer; }
        public void CaptureHome()
        {
            var pl = Player.m_localPlayer;
            if (pl != null && !havePlayer) { havePlayer = true; playerHome = pl.transform.position; if (hidePlayer) SetVisible(pl.gameObject, false); }
        }

        public static void PlaceUnder(Vector3 cp)
        {
            var pl = Player.m_localPlayer; if (pl == null) return;
            float h = cp.y - 3f;
            try { h = Mathf.Max(cp.y - 3f, MapImport.Height(cp.x, cp.z) + 0.5f); } catch { }
            var body = pl.GetComponent<Rigidbody>(); if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
            pl.transform.position = new Vector3(cp.x, h, cp.z);
            if (body != null) body.position = pl.transform.position;
            try { ZNet.instance.SetReferencePosition(pl.transform.position); } catch { }
        }

        public Vector3 CamAt(float t)
        {
            if (camPos.Length == 0) return Vector3.zero;
            return camPos[Mathf.Clamp((int)(t * Fps), 0, camPos.Length - 1)];
        }
        public bool Started { get { return started; } }

        public void Start()
        {
            t0 = Time.unscaledTime; started = true; lineIdx = 0;
            if (grassOff) { grassWas = Grass.ShotOff; Grass.ShotOff = true; }
            MusicActive = audio.Exists(x => x.Kind == "music" && x.Clip != null);
            if (hideTags.Count > 0) Statics.HideTags(hideTags);
            // Kameralicht wie in Witchers Cutscenes (Innenraeume waeren sonst fast schwarz)
            try
            {
                var lg = new GameObject("gi_cs_camlight"); camLight = lg.AddComponent<Light>();
                camLight.type = LightType.Point; camLight.range = 9f; camLight.intensity = 1.1f; camLight.color = new Color(1f, 0.93f, 0.82f);
                camLight.shadows = LightShadows.None;
            }
            catch (Exception e) { Plugin.Quiet(e, "Cutscene.Licht"); }
            var pl = Player.m_localPlayer;
            if (pl != null) { if (!havePlayer) { havePlayer = true; playerHome = pl.transform.position; } if (hidePlayer) SetVisible(pl.gameObject, false); }
            foreach (var a in actors)
            {
                try
                {
                    if (a.Role == "player")
                    {
                        a.Go = Npcs.SpawnTemp("cs_player_double", PlayerLook(), a.Pos[0], a.Yaw[0]); a.Double = true;
                        if (pl != null) SetVisible(pl.gameObject, false);
                    }
                    else
                    {
                        a.Go = Npcs.Get(a.Role);                                    // NPC aus dem Paket ist schon da: der spielt mit
                        if (a.Go == null) { a.Go = Npcs.SpawnTemp("cs_" + a.Role, NpcLook(a.Role), a.Pos[0], a.Yaw[0]); a.Double = true; }
                    }
                }
                catch (Exception e) { Plugin.Warn("Cutscene-Darsteller " + a.Role + ": " + e.Message); }
                // Groesse wie im Original (Kinder: Link, Zelda)
                if (a.Go != null && Math.Abs(a.Scale - 1f) > 0.001f) a.Go.transform.localScale = Vector3.one * a.Scale;
                // Kleidung waehrend der Cutscene (z. B. Geralt zieht sich an): Aenderungen gegenueber dem normalen Aussehen
                if (a.Go != null && a.Outfits.Count > 0)
                {
                    var bl = a.Role == "player" ? PlayerLook() : NpcLook(a.Role);
                    a.BaseLook = bl is Dictionary<string, object> ? new Dictionary<string, object>((Dictionary<string, object>)bl) : new Dictionary<string, object>();
                }
                // Original-Animation Knochen fuer Knochen (sonst nur Valheim-Posen)
                if (a.Go != null && a.Bones != null)
                {
                    try { a.Rig = BoneRig.Build(a.Go, a.Bones); if (a.Rig != null) { a.Rig.SetHeadScale(a.HeadScale); if (a.ExactHip) a.Rig.SetRider(); } Plugin.Log("Cutscene-Darsteller " + a.Role + ": " + (a.Rig != null ? a.Rig.Info : "kein Skelett")); }
                    catch (Exception e) { Plugin.Warn("Cutscene-Knochen " + a.Role + ": " + e); a.Rig = null; }
                }
            }
            foreach (var p in props)
            {
                try { p.Go = SpawnProp(p); }
                catch (Exception e) { Plugin.Warn("Cutscene-Requisite " + p.Name + ": " + e.Message); }
            }
            // Bild wie im Original: Tageszeit der Szene, keine Valheim-Anzeigen
            try
            {
                if (EnvMan.instance != null)
                {
                    var tv = Traverse.Create(EnvMan.instance);
                    dbgWas = tv.Field("m_debugTimeOfDay").GetValue<bool>(); dbgTWas = tv.Field("m_debugTime").GetValue<float>();
                    tv.Field("m_debugTimeOfDay").SetValue(true); tv.Field("m_debugTime").SetValue(timeOfDay);
                }
                if (Hud.instance != null) { hudWas = Hud.instance.m_userHidden; Hud.instance.m_userHidden = true; }
                if (Minimap.instance != null && Minimap.instance.m_smallRoot != null) { mapRoot = Minimap.instance.m_smallRoot; mapWas = mapRoot.activeSelf; }
            }
            catch (Exception e) { Plugin.Quiet(e, "Cutscene.Bild"); }
            EnvStart();
            Plugin.Log("Cutscene-Spur: " + Dur.ToString("F1") + " s, " + camPos.Length + " Kamerabilder, " + actors.Count + " Darsteller, " + lines.Count + " Zeilen");
        }

        // Nur das Aussehen einer Valheim-Kreatur (Kind "Visual" des Prefabs), ohne Verhalten/Netzwerk
        static GameObject SpawnProp(Prop p)
        {
            // Originalmodell (GLB mit Skelett) mit einem Clip ueber die ganze Cutscene, Bild fuer Bild zur Spur gesetzt
            if (p.Model != null)
            {
                var g = Glb.Instantiate(p.Model, false);
                g.name = "gi_cs_prop_" + p.Name; g.transform.localScale = Vector3.one * p.Scale;
                p.Legacy = g.GetComponentInChildren<Animation>();
                if (p.Legacy != null && p.Legacy[p.Clip] != null) { var st = p.Legacy[p.Clip]; p.Legacy.clip = st.clip; st.speed = 0f; st.weight = 1f; st.enabled = true; p.Legacy.Play(p.Clip); }
                return g;
            }
            if (p.Prefab == "@navi") { var ng = NaviFx.Spawn(p.FxCol, p.FxGlow); ng.name = "gi_cs_prop_" + p.Name; return ng; }
            var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(p.Prefab) : null;
            if (pf == null) throw new Exception("Prefab fehlt: " + p.Prefab);
            Transform vis = pf.transform.Find("Visual");
            if (vis == null) { var an0 = pf.GetComponentInChildren<Animator>(true); vis = an0 != null ? an0.transform : pf.transform; }
            var holder = new GameObject("gi_prop_holder"); holder.SetActive(false);
            var go = UnityEngine.Object.Instantiate(vis.gameObject, holder.transform);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
            go.name = "gi_cs_prop_" + p.Name;
            go.transform.SetParent(null, false); go.transform.localScale = vis.lossyScale * p.Scale;
            go.SetActive(true); UnityEngine.Object.Destroy(holder);
            if (Mathf.Abs(p.LightMul - 1f) > 0.01f) foreach (var l in go.GetComponentsInChildren<Light>(true)) l.intensity *= p.LightMul;
            p.Anim = go.GetComponentInChildren<Animator>();
            if (p.Anim != null) foreach (var par in p.Anim.parameters) if (par.name == "forward_speed") p.HasSpeed = true;
            return go;
        }

        void TickProps(float t)
        {
            foreach (var p in props)
            {
                if (p.Go == null) continue;
                float f = t * Fps; int i = Mathf.Clamp((int)f, 0, p.Pos.Length - 1), j = Mathf.Min(i + 1, p.Pos.Length - 1);
                float u = Mathf.Clamp01(f - i); if (cutAt.Contains(j)) u = 0f;
                bool vis = u < 0.5f ? p.Vis[i] : p.Vis[j];
                if (vis != p.Shown) { p.Go.SetActive(vis); p.Shown = vis; }
                if (!vis) continue;
                var pos = Vector3.Lerp(p.Pos[i], p.Pos[j], u);
                var fw = Vector3.Lerp(p.Fwd[i], p.Fwd[j], u); var up = Vector3.Lerp(p.Up[i], p.Up[j], u);
                p.Go.transform.position = pos;
                if (fw.sqrMagnitude > 1e-6f && Vector3.Cross(fw, up).sqrMagnitude > 1e-6f) p.Go.transform.rotation = Quaternion.LookRotation(fw, up);
                if (p.Legacy != null) { var st = p.Legacy[p.Clip]; if (st != null) { st.enabled = true; st.weight = 1f; st.speed = 0f; st.time = Mathf.Min(t, st.length); p.Legacy.Sample(); } }
                if (p.HasSpeed)
                {
                    float sp = cutAt.Contains(j) ? 0f : (p.Pos[j] - p.Pos[i]).magnitude * Fps / Mathf.Max(0.05f, p.Scale);
                    p.Anim.SetFloat("forward_speed", frozen >= 0f ? 0f : Mathf.Min(sp, 6f));
                }
            }
        }

        public void Tick(StoryUi ui)
        {
            float t = T;
            if (Follow) PlaceUnder(CamAt(t));
            // Zeilen
            while (lineIdx < lines.Count && lines[lineIdx].T <= t)
            {
                var ln = lines[lineIdx++];
                var txt = Pack.T(ln.Text);
                var who = Pack.Current != null ? Pack.Current.NpcName(ln.Speaker) : ln.Speaker;
                Subtitle = (string.IsNullOrEmpty(who) ? "" : "<b><color=#f2c766>" + who + ":</color></b>  ") + txt;
                float len = ui.CutVoice(ln.Voice);
                subUntil = t + Mathf.Max(2.5f, len > 0f ? len + 0.3f : 2.2f + txt.Length * 0.055f);
            }
            if (t > subUntil) Subtitle = null;
            // Darsteller
            foreach (var a in actors)
            {
                if (a.Go == null || a.Pos.Length == 0) continue;
                float fi = t * Fps / a.Step; int i = Mathf.Clamp((int)fi, 0, a.Pos.Length - 1), j = Mathf.Min(i + 1, a.Pos.Length - 1);
                float u = Mathf.Clamp01(fi - i);
                // Zwischen zwei Proben ein Kameraschnitt? Dann nicht gleiten (Figuren werden je Einstellung neu gestellt)
                bool cut = false;
                foreach (var c in cutAt) if (c > i * a.Step && c <= j * a.Step) { cut = true; u = t * Fps >= c ? 1f : 0f; break; }
                var p = Vector3.Lerp(a.Pos[i], a.Pos[j], u);
                float yaw = Mathf.LerpAngle(a.Yaw[i], a.Yaw[j], u);
                var tr = a.Go.transform;
                tr.position = p; tr.rotation = Quaternion.Euler(0f, yaw, 0f);
                if (a.BaseLook != null)
                {
                    int oi = -1; for (int k = 0; k < a.Outfits.Count; k++) if (a.Outfits[k].Key <= t) oi = k;
                    if (oi != a.OutfitIdx)
                    {
                        var lk = new Dictionary<string, object>(a.BaseLook);
                        for (int k = 0; k <= oi; k++)
                        {
                            var set = a.Outfits[k].Value as Dictionary<string, object>;
                            if (set != null) foreach (var kv in set)
                                {
                                    if ((kv.Value as string) == "$base") { object bv; if (a.BaseLook.TryGetValue(kv.Key, out bv)) lk[kv.Key] = bv; else lk.Remove(kv.Key); }
                                    else lk[kv.Key] = kv.Value;
                                }
                        }
                        try { Npcs.ApplyLook(a.Go, lk); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Kleidung"); }
                        a.OutfitIdx = oi;
                    }
                }
                if (a.Rig != null)
                {
                    var b = a.Bones;
                    float bf = t * b.Fps; int bi = Mathf.Clamp((int)bf, 0, b.Frames - 1), bj = Mathf.Min(bi + 1, b.Frames - 1);
                    float bu = Mathf.Clamp01(bf - bi);
                    foreach (var c in cutAt) { float ct = c / Fps; if (ct > bi / b.Fps && ct <= bj / b.Fps) { bu = t >= ct ? 1f : 0f; break; } }
                    var pel = Vector3.Lerp(b.Pelvis[bi], b.Pelvis[bj], bu);
                    float rootY = Mathf.Lerp(b.RootY[bi], b.RootY[bj], bu);
                    tr.position = new Vector3(pel.x, BoneRig.Floor(b, pel.y, rootY), pel.z);
                    // unter dem Boden versteckt (so blendet Witcher unbenutzte Darsteller aus)
                    bool hid = pel.y < rootY - 0.5f;
                    if (hid != a.Hidden) { SetVisible(a.Go, !hid); a.Hidden = hid; }
                    var off = Vector3.zero; foreach (var s5 in a.Shift) if (t >= s5[0] && t < s5[1]) off = new Vector3(s5[2], s5[3], s5[4]);
                    a.Rig.Offset = off;
                    a.Rig.Apply(b, bi, bj, bu);
                    continue;
                }
                // Laufgeschwindigkeit aus der Spur (Spruenge an Schnitten zaehlen nicht)
                float dist = new Vector2(a.Pos[j].x - a.Pos[i].x, a.Pos[j].z - a.Pos[i].z).magnitude;
                float sp = (!cut && dist < 0.6f) ? dist / (a.Step / Fps) : 0f;
                var anim = a.Go.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    int pose = a.Pose[i];
                    if (pose != a.LastPose)
                    {
                        // Valheim: emote_sit / attach_chair sind Schalter (bool), keine Ausloeser; Aufstehen = beide aus + emote_stop
                        try
                        {
                            anim.SetBool("emote_sit", pose == 2);
                            anim.SetBool("attach_chair", pose == 1);
                            if (pose == 0) anim.SetTrigger("emote_stop");
                        }
                        catch { }
                        a.LastPose = pose;
                    }
                    try { anim.SetFloat("forward_speed", pose == 0 && sp > 0.25f ? Mathf.Min(sp, 7f) : 0f); } catch { }
                }
            }
            TickAudio(t);
            TickProps(t);
            EnvTick(t);
            foreach (var mo in movers)
            {
                float f = t * Fps; int i = Mathf.Clamp((int)f, 0, mo.Pos.Length - 1), j = Mathf.Min(i + 1, mo.Pos.Length - 1);
                float u = Mathf.Clamp01(f - i);
                Statics.Pose(mo.Tag, Vector3.Lerp(mo.Pos[i], mo.Pos[j], u), Quaternion.Slerp(mo.Rot[i], mo.Rot[j], u));
            }
            // Valheim-Anzeigen bleiben aus (Karte, Fenster)
            try
            {
                if (Hud.instance != null) Hud.instance.m_userHidden = true;
                if (mapRoot != null && mapRoot.activeSelf) mapRoot.SetActive(false);
                if (InventoryGui.IsVisible() && InventoryGui.instance != null) InventoryGui.instance.Hide();
            }
            catch (Exception e) { Plugin.Quiet(e, "Cutscene.Hud"); }
            // Gelaende aus/an
            bool hide = false; foreach (var h in hideTerrain) if (t >= h.x && t < h.y) hide = true;
            if (hide != terrainHidden) { SetTerrain(!hide); terrainHidden = hide; }
        }

        public void ApplyCamera(Camera cam)
        {
            if (camPos.Length == 0) return;
            float f = T * Fps;
            int i = Mathf.Clamp((int)f, 0, camPos.Length - 1), j = Mathf.Min(i + 1, camPos.Length - 1);
            float u = Mathf.Clamp01(f - i);
            if (cutAt.Contains(j)) u = 0f;                                   // ueber einen Schnitt nicht weich blenden
            cam.transform.position = Vector3.Lerp(camPos[i], camPos[j], u);
            cam.transform.rotation = Quaternion.Slerp(camRot[i], camRot[j], u);
            if (camLight != null)
            {
                camLight.transform.position = cam.transform.position + cam.transform.rotation * new Vector3(0.6f, 0.5f, 0.3f);
                camLight.intensity = 1.1f * CamLightMul * camLightTrack;
            }
            var warm = new Color(0.60f, 0.46f, 0.32f);
            RenderSettings.ambientLight = Color.Lerp(RenderSettings.ambientLight, warm, 0.75f);
            RenderSettings.ambientSkyColor = Color.Lerp(RenderSettings.ambientSkyColor, warm, 0.75f);
            RenderSettings.ambientEquatorColor = Color.Lerp(RenderSettings.ambientEquatorColor, warm * 0.85f, 0.75f);
            RenderSettings.ambientGroundColor = Color.Lerp(RenderSettings.ambientGroundColor, warm * 0.6f, 0.75f);
            RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, new Color(0.30f, 0.24f, 0.18f), 0.8f);
            RenderSettings.fogDensity = Mathf.Min(RenderSettings.fogDensity, 0.004f);
            float fv = Mathf.Lerp(fov[i], fov[j], u);
            if (hfov) fv = 2f * Mathf.Atan(Mathf.Tan(fv * 0.5f * Mathf.Deg2Rad) / Mathf.Max(0.5f, cam.aspect)) * Mathf.Rad2Deg;
            cam.fieldOfView = Mathf.Clamp(fv, 5f, 120f);
            // Clipping aus der Cutscene (versteckt Waende zwischen Kamera und Figur)
            float near = -1f, far = 0f, t = T;
            foreach (var c in clips) if (c.x <= t) { near = c.y; far = c.z; }
            if (near > 0f) cam.nearClipPlane = Mathf.Clamp(near, 0.05f, 2f);
        }

        // Schwarzblende: 0 = klar, 1 = schwarz
        public Color FadeColor = Color.black;

        static Color FadeCol(List<object> l)
        {
            if (l == null || l.Count < 3) return Color.black;
            Func<object, float> n = v => v is double ? (float)(double)v : 0f;
            return new Color(n(l[0]), n(l[1]), n(l[2]));
        }

        public float Black()
        {
            float t = T, a = 0f;
            foreach (var f in fades)
            {
                if (t < f.T) break;
                float k = f.Dur > 0f ? Mathf.Clamp01((t - f.T) / f.Dur) : 1f;
                a = f.In ? 1f - k : k;
                FadeColor = f.Col;
            }
            if (t < 0.05f) a = Mathf.Max(a, 1f);
            if (Dur - t < 0.6f) a = Mathf.Max(a, Mathf.Clamp01(1f - (Dur - t) / 0.6f));
            return a;
        }

        public void End()
        {
            if (hideTags.Count > 0) Statics.HideTags(null);
            if (grassOff) Grass.ShotOff = grassWas;
            if (camLight != null) UnityEngine.Object.Destroy(camLight.gameObject);
            EnvEnd();
            foreach (var a in audio) if (a.Src != null) { a.Src.Stop(); UnityEngine.Object.Destroy(a.Src.gameObject); a.Src = null; }
            foreach (var p in props) if (p.Go != null) { UnityEngine.Object.Destroy(p.Go); p.Go = null; }
            MusicActive = false;
            try
            {
                if (EnvMan.instance != null)
                {
                    var tv = Traverse.Create(EnvMan.instance);
                    tv.Field("m_debugTimeOfDay").SetValue(dbgWas); tv.Field("m_debugTime").SetValue(dbgTWas);
                }
                if (Hud.instance != null) Hud.instance.m_userHidden = hudWas;
                if (mapRoot != null) mapRoot.SetActive(mapWas);
            }
            catch (Exception e) { Plugin.Quiet(e, "Cutscene.Bild"); }
            foreach (var a in actors)
            {
                if (a.Rig != null) { try { a.Rig.Restore(); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Knochen"); } a.Rig = null; }
                if (a.Go != null && a.Double) UnityEngine.Object.Destroy(a.Go);
                else if (a.Go != null)
                {
                    if (a.Hidden) SetVisible(a.Go, true);
                    if (a.BaseLook != null) { try { Npcs.ApplyLook(a.Go, a.BaseLook); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Kleidung"); } }
                    Npcs.ResetPlace(a.Role);
                }
            }
            if (terrainHidden) { SetTerrain(true); terrainHidden = false; }
            var pl = Player.m_localPlayer;
            if (pl != null && Follow && havePlayer)
            {
                var body = pl.GetComponent<Rigidbody>();
                pl.transform.position = playerHome;
                if (body != null) { body.position = playerHome; body.velocity = Vector3.zero; body.isKinematic = false; }
                try { ZNet.instance.SetReferencePosition(playerHome); } catch { }
            }
            if (pl != null)
            {
                SetVisible(pl.gameObject, true);
                // Spieler steht danach dort, wo sein Double aufgehoert hat
                var a = actors.Find(x => x.Role == "player");
                if (a != null && a.Pos.Length > 0)
                {
                    var p = a.Pos[a.Pos.Length - 1] + Vector3.up * 0.05f;
                    var body = pl.GetComponent<Rigidbody>();
                    if (body != null) { body.position = p; body.velocity = Vector3.zero; }
                    pl.transform.position = p; pl.transform.rotation = Quaternion.Euler(0f, a.Yaw[a.Yaw.Length - 1], 0f);
                }
            }
        }

        static void SetVisible(GameObject go, bool on) { foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = on; }

        static System.Reflection.FieldInfo fMr;
        static void SetTerrain(bool on)
        {
            try
            {
                if (fMr == null) fMr = AccessTools.Field(typeof(Heightmap), "m_meshRenderer");
                foreach (var h in Heightmap.GetAllHeightmaps()) { var mr = fMr != null ? fMr.GetValue(h) as MeshRenderer : null; if (mr != null) mr.enabled = on; }
            }
            catch (Exception e) { Plugin.Quiet(e, "Cutscene.Terrain"); }
        }

        // Aussehen des eigenen Charakters fuer das Double
        public static object PlayerLook()
        {
            var d = new Dictionary<string, object>();
            var pl = Player.m_localPlayer; var v = pl != null ? pl.GetComponent<VisEquipment>() : null;
            if (v == null) return d;
            Func<string, object> F = n => { var f = AccessTools.Field(typeof(VisEquipment), n); return f != null ? f.GetValue(v) : null; };
            d["model"] = Convert.ToInt32(F("m_modelIndex") ?? 0) == 1 ? "female" : "male";
            foreach (var kv in new[] { new[] { "hair", "m_hairItem" }, new[] { "beard", "m_beardItem" }, new[] { "chest", "m_chestItem" }, new[] { "legs", "m_legItem" },
                                       new[] { "helmet", "m_helmetItem" }, new[] { "shoulder", "m_shoulderItem" } })
                d[kv[0]] = F(kv[1]) as string ?? "";
            foreach (var kv in new[] { new[] { "skin", "m_skinColor" }, new[] { "hair_color", "m_hairColor" } })
            {
                var c = F(kv[1]); if (c is Vector3) { var c3 = (Vector3)c; d[kv[0]] = new List<object> { (double)c3.x, (double)c3.y, (double)c3.z }; }
            }
            return d;
        }

        public static object NpcLook(string id)
        {
            Pack.Npc n;
            if (Shots.Active && Shots.Recasts(id)) return Shots.CastLook(id);   // Einstellung aendert das Aussehen
            if (Pack.Current != null && Pack.Current.NpcById.TryGetValue(id, out n) && n.Look != null) return n.Look;
            var c = Shots.CastLook(id);                    // Besetzung fuer Videos (trailer/cast.json)
            return c ?? new Dictionary<string, object> { { "model", "female" } };
        }
    }
}
