using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Einstellungen fuer Videos (z. B. einen Trailer Bild fuer Bild nachstellen) inszenieren und bildgenau aufnehmen.
    // Datei im Paket: shots/<id>.json
    //  {"dur": 3.73, "fps": 30, "time": 0.55, "weather": "Clear", "fog": 0.002,
    //   "cam": [{"t": 0, "pos": [x, y, z], "look": [x, y, z], "fov": 40}, {"t": 3.7, "pos": [...], "look": [...], "fov": 38}],
    //   "actors": [{"id": "geralt", "look": {...} | "npc": "<npc-id>" | "player": true,
    //               "path": [{"t": 0, "pos": [x, y, z], "yaw": 90}, ...], "anim": [{"t": 1.2, "trigger": "emote_point"}]}],
    //   "creatures": [{"prefab": "Troll", "path": [...], "scale": 1, "anim": [{"t": 0.5, "trigger": "attack"}]}],
    //   "hide": ["<teil eines Modellnamens>", ...], "hide_box": [[x0, y0, z0, x1, y1, z1, maxRadius], ...],
    //   "grass": false (Valheim-Gras aus, z. B. in Innenraeumen),
    //   "cutscene": "<id>", "from": 12.5 (Spiel-Cutscene des Pakets mit Originalkamera abspielen und ab "from" "dur" s aufnehmen;
    //               mit "cam" dazu: Animationen aus der Cutscene, eigene Kamera - fuer Trailer-Einstellungen, die es im Spiel nicht gibt),
    //   "clear_cam": 3 (keine Baeume im Schlauch Kamera -> Blickrichtung, 25 m lang; Darsteller-Plaetze frei), "clear": [[x, y, z, r], ...],
    //   "light": {"ambient": [r, g, b], "sun": 0.6, "fog": 2.0, "fog_color": [r, g, b]},
    //   "fire": [{"pos": [x, y, z], "size": 2.5, "rate": 25} | {"burn": "<modellteil>", "near": [x, y, z], "radius": 25, "size": 2, "rate": 80}]
    //               (Witcher-Feuer, siehe FireFx), "lights": [{"pos": [x, y, z], "color": [r, g, b], "range": 6, "intensity": 2, "spot": [x, y, z], "angle": 40}]}  (Fuehrungslicht usw.)
    // Kamera: Positionen weich (Catmull-Rom) durch die Schluessel, "fov" = senkrechter Bildwinkel wie im Original.
    // Aufnahme ("shot <id> rec"): feste Bildrate (Time.captureFramerate) -> jedes Bild landet als JPG in
    // config/GameImporter/rec/<id>/, unabhaengig davon, wie schnell der PC gerade rendert (laeuft auch im Hintergrund).
    public static class Shots
    {
        class CKey { public float T, Fov; public Vector3 P, L; }
        class PKey { public float T, Yaw; public Vector3 P; }
        class Act { public string Id; public GameObject Go; public List<PKey> Path = new List<PKey>(); public List<KeyValuePair<float, string>> Anims = new List<KeyValuePair<float, string>>(); public int AnimIdx; public Animator An; public bool Creature; public string Ride; public Vector3 RideOff; public Transform Seat; }

        public static bool Active { get { return shot != null; } }
        static object shot; static string shotId;
        static readonly List<CKey> cam = new List<CKey>();
        static readonly List<Act> acts = new List<Act>();
        static float t, dur; static int fps = 30, frame; static bool rec, hudWas, started; static string outDir;
        static int recW = 1920, recH = 1080;
        static RenderTexture rt; static Texture2D rb;
        static Camera recCam;
        static int zoneWas = -1, followWait; static bool holding; static int holdN;
        static readonly List<string> hidden = new List<string>();
        static readonly List<GameObject> lamps = new List<GameObject>();
        static float preload = 2f;
        static string csId; static float csFrom;
        // "recast": {"geralt": {"chest": ""}} = Aussehen nur fuer diese Einstellung aendern (ueber die Besetzung gelegt)
        static Dictionary<string, object> recast;
        public static bool Recasts(string id) { return recast != null && id != null && recast.ContainsKey(id); }
        public static object CastLook(string id)
        {
            object b; try { b = Cast(id); } catch { b = null; }
            object r;
            if (recast == null || id == null || !recast.TryGetValue(id, out r) || !(r is Dictionary<string, object>)) return b;
            var m = b is Dictionary<string, object> ? new Dictionary<string, object>((Dictionary<string, object>)b) : new Dictionary<string, object>();
            foreach (var kv in (Dictionary<string, object>)r) m[kv.Key] = kv.Value;
            return m;
        }
        // Licht fuer die Einstellung (nach Valheims EnvMan, jedes Bild)
        static Color? lAmb, lFogCol; static float lSun = 1f, lFog = 1f, sunSet = -1f, sunBase, fogSet = -1f, fogBase;

        // [x, y, z]; y als "+1.6" = ueber dem Boden (wie in den anderen Paketdateien)
        static Vector3 V(object o, Vector3 def)
        {
            var l = o as List<object>;
            if (l == null || l.Count < 3) return def;
            Vector3 v;
            if (Pack.Current != null && Pack.Current.Pos(l, out v) && !float.IsNaN(v.y)) return v;
            // ohne Paket (normale Valheim-Welt): "+1.6" = ueber dem Boden
            float x = N(l[0]), z = N(l[2]);
            var ys = l[1] as string;
            if (ys != null)
            {
                float off; float.TryParse(ys.TrimStart('+'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out off);
                float gy = 0f;
                if (ZoneSystem.instance != null) gy = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 500f, z));
                return new Vector3(x, gy + off, z);
            }
            return new Vector3(x, N(l[1]), z);
        }
        static float N(object o) { return o is double ? (float)(double)o : 0f; }
        static Color? C(object o) { var l = o as List<object>; return l != null && l.Count >= 3 ? new Color(N(l[0]), N(l[1]), N(l[2])) : (Color?)null; }

        static void ApplyLight()
        {
            if (lAmb.HasValue)
            {
                var a = lAmb.Value;
                RenderSettings.ambientLight = a; RenderSettings.ambientSkyColor = a; RenderSettings.ambientEquatorColor = a * 0.85f; RenderSettings.ambientGroundColor = a * 0.6f;
            }
            if (lFogCol.HasValue) RenderSettings.fogColor = lFogCol.Value;
            if (lFog != 1f)
            {
                if (!Mathf.Approximately(RenderSettings.fogDensity, fogSet)) fogBase = RenderSettings.fogDensity;   // EnvMan hat neu gesetzt
                RenderSettings.fogDensity = fogSet = fogBase * lFog;
            }
            if (lSun != 1f && EnvMan.instance != null && EnvMan.instance.m_dirLight != null)
            {
                var L = EnvMan.instance.m_dirLight;
                if (!Mathf.Approximately(L.intensity, sunSet)) sunBase = L.intensity;
                L.intensity = sunSet = sunBase * lSun;
            }
        }

        public static string Play(string id, bool record, int w = 1920, int h = 1080)
        {
            Stop();
            // Paket-Einstellung, sonst allgemeine (config/GameImporter/trailer/shots/<id>.json, z. B. in einer normalen Valheim-Welt)
            var pk = Pack.Current;
            var f = pk != null ? Path.Combine(pk.Dir, "shots", id + ".json") : null;
            if (f == null || !File.Exists(f)) f = Path.Combine(Path.Combine(Remote.Dir, "trailer"), Path.Combine("shots", id + ".json"));
            if (!File.Exists(f)) return "fehlt: " + f;
            var o = Json.Parse(File.ReadAllText(f));
            shot = o; shotId = id; t = 0f; frame = 0; started = false;
            dur = Json.F(o, "dur", 3f); fps = (int)Json.F(o, "fps", 30f);
            csId = Json.S(o, "cutscene"); csFrom = Json.F(o, "from", 0f);
            recast = Json.V(o, "recast") as Dictionary<string, object>;
            CutsceneTrack.CamLightMul = Json.F(o, "cs_light", 1f);
            recW = w; recH = h;
            cam.Clear();
            foreach (var k in Json.L(o, "cam") ?? new List<object>())
                cam.Add(new CKey { T = Json.F(k, "t"), P = V(Json.V(k, "pos"), Vector3.zero), L = V(Json.V(k, "look"), Vector3.forward), Fov = Json.F(k, "fov", 40f) });
            cam.Sort((a, b) => a.T.CompareTo(b.T));
            // Zeit, Wetter
            try
            {
                if (EnvMan.instance != null)
                {
                    var tv = HarmonyLib.Traverse.Create(EnvMan.instance);
                    if (Json.Has(o, "time")) { tv.Field("m_debugTimeOfDay").SetValue(true); tv.Field("m_debugTime").SetValue(Json.F(o, "time", 0.5f)); }
                    if (Json.Has(o, "weather")) EnvMan.instance.SetForceEnvironment(Json.S(o, "weather"));
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "Shots.Env"); }
            // Darsteller
            foreach (var a in Json.L(o, "actors") ?? new List<object>()) acts.Add(MakeActor(a, false));
            foreach (var a in Json.L(o, "creatures") ?? new List<object>()) acts.Add(MakeActor(a, true));
            acts.RemoveAll(x => x == null || x.Go == null);
            hidden.Clear(); foreach (var x in Json.L(o, "hide") ?? new List<object>()) hidden.Add(x.ToString());
            foreach (var hn in hidden) Statics.Hide(hn, true);
            var boxes = new List<float[]>();
            foreach (var b in Json.L(o, "hide_box") ?? new List<object>())
            {
                var l = b as List<object>; if (l == null || l.Count < 6) continue;
                boxes.Add(new[] { N(l[0]), N(l[1]), N(l[2]), N(l[3]), N(l[4]), N(l[5]), l.Count > 6 ? N(l[6]) : 4f });
            }
            Statics.HideBoxes(boxes);
            Grass.ShotOff = Json.Has(o, "grass") && !Json.B(o, "grass");

            foreach (var lj in Json.L(o, "lights") ?? new List<object>())
            {
                var go = new GameObject("gi_shot_light");
                go.transform.position = V(Json.V(lj, "pos"), Vector3.zero);
                var li = go.AddComponent<Light>();
                bool spot = Json.Has(lj, "spot");
                li.type = spot ? LightType.Spot : LightType.Point;
                if (spot) { go.transform.rotation = Quaternion.LookRotation(V(Json.V(lj, "spot"), Vector3.zero) - go.transform.position); li.spotAngle = Json.F(lj, "angle", 40f); }
                li.color = C(Json.V(lj, "color")) ?? new Color(1f, 0.8f, 0.6f);
                li.range = Json.F(lj, "range", 6f); li.intensity = Json.F(lj, "intensity", 1.5f);
                li.shadows = Json.B(lj, "shadows") ? LightShadows.Soft : LightShadows.None;
                li.renderMode = LightRenderMode.ForcePixel;
                lamps.Add(go);
            }
            var lo = Json.V(o, "light");
            lAmb = C(Json.V(lo, "ambient")); lFogCol = C(Json.V(lo, "fog_color"));
            lSun = Json.F(lo, "sun", 1f); lFog = Json.F(lo, "fog", 1f); sunSet = -1f; fogSet = -1f;
            Statics.Extra.Clear(); foreach (var k in cam) Statics.Extra.Add(k.P);
            var segs = new List<Vector3[]>();
            float cr = Json.F(o, "clear_cam", 0f), cl = Json.F(o, "clear_len", 25f);
            if (cr > 0f)
            {
                foreach (var k in cam) { var dv = k.L - k.P; segs.Add(new[] { k.P, k.P + dv.normalized * Mathf.Min(dv.magnitude, cl), new Vector3(cr, 0f, 0f) }); }
                for (int i = 0; i + 1 < cam.Count; i++) segs.Add(new[] { cam[i].P, cam[i + 1].P, new Vector3(cr, 0f, 0f) });
                foreach (var a in acts) for (int i = 0; i < a.Path.Count; i++) segs.Add(new[] { a.Path[i].P, a.Path[Mathf.Min(i + 1, a.Path.Count - 1)].P, new Vector3(a.Creature ? 3f : 1.5f, 0f, 0f) });
            }
            foreach (var c in Json.L(o, "clear") ?? new List<object>())
            {
                var l = c as List<object>; if (l == null || l.Count < 4) continue;
                var p = V(l.GetRange(0, 3), Vector3.zero); segs.Add(new[] { p, p, new Vector3(N(l[3]), 0f, 0f) });
            }
            Foliage.SetClear(segs);
            if (Hud.instance != null) { hudWas = Hud.instance.m_userHidden; Hud.instance.m_userHidden = true; }
            // Spieler unsichtbar; Valheim laedt Gelaende um den Spieler -> bei fernen Orten dorthin ("player_at")
            var pl = Player.m_localPlayer;
            if (pl != null)
            {
                if (csId == null) foreach (var r in pl.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                if (Json.Has(o, "player_at")) { var pa = V(Json.V(o, "player_at"), pl.transform.position); pl.TeleportTo(pa, pl.transform.rotation, true); }
            }
            preload = Json.F(o, "preload", Json.Has(o, "player_at") ? 6f : 2f);
            // "zone_area": n = Valheim laedt Gelaende in n Zonen (64 m) um den Spieler statt 2 - fuer Kamerafluege ueber weite Strecken
            if (Json.Has(o, "zone_area") && ZoneSystem.instance != null)
            {
                try
                {
                    var tz = HarmonyLib.Traverse.Create(ZoneSystem.instance);
                    if (zoneWas < 0) zoneWas = tz.Field("m_activeArea").GetValue<int>();
                    tz.Field("m_activeArea").SetValue((int)Json.F(o, "zone_area", 2f));
                    Plugin.Log("Einstellung: Gelaende-Zonen " + zoneWas + " -> " + (int)Json.F(o, "zone_area", 2f));
                }
                catch (Exception e) { Plugin.Warn("zone_area: " + e.Message); }
            }
            rec = record;
            if (rec)
            {
                outDir = Path.Combine(Remote.Dir, "rec", id);
                if (Directory.Exists(outDir)) { var old = outDir + "_alt_" + DateTime.Now.ToString("HHmmss"); Directory.Move(outDir, old); }   // nichts loeschen
                Directory.CreateDirectory(outDir);
            }
            Plugin.Log("Einstellung " + id + ": " + dur.ToString("F2") + " s, " + cam.Count + " Kamerapunkte, " + acts.Count + " Darsteller" + (rec ? ", Aufnahme " + recW + "x" + recH + " @" + fps : ""));
            // Spur gleich an der Startstelle festhalten (sonst laeuft eine kurze Cutscene waehrend des Vorladens schon zu Ende)
            if (csId != null) { CutsceneTrack.ForceT = csFrom; StoryUi.PlayCutscene(csId); }
            if (StoryUi.I != null) StoryUi.I.StartCoroutine(Run());
            return "shot " + id + (rec ? " (Aufnahme -> " + outDir + ")" : "");
        }

        // Besetzung: feste Looks fuer wiederkehrende Figuren (Geralt, Ciri ...) in config/GameImporter/trailer/cast.json
        // oder cast.json im Paket: {"geralt": {"model": "male", "hair": ...}, ...}
        static Dictionary<string, object> cast; static DateTime castT;
        static object Cast(string id)
        {
            var f1 = Path.Combine(Remote.Dir, "trailer", "cast.json");
            var f2 = Pack.Current != null ? Path.Combine(Pack.Current.Dir, "cast.json") : null;
            var t = File.Exists(f1) ? File.GetLastWriteTimeUtc(f1) : DateTime.MinValue;
            if (cast == null || t != castT)
            {
                cast = new Dictionary<string, object>(); castT = t;
                foreach (var f in new[] { f1, f2 })
                    if (f != null && File.Exists(f)) { var o = Json.Parse(File.ReadAllText(f)) as Dictionary<string, object>; if (o != null) foreach (var kv in o) cast[kv.Key] = kv.Value; }
            }
            object l; return id != null && cast.TryGetValue(id, out l) ? l : null;
        }

        static Act MakeActor(object a, bool creature)
        {
            var act = new Act { Id = Json.S(a, "id", Json.S(a, "prefab", "?")), Creature = creature, Ride = Json.S(a, "ride") };
            act.RideOff = V(Json.V(a, "ride_offset"), new Vector3(0f, 0f, 0f));
            foreach (var k in Json.L(a, "path") ?? new List<object>())
                act.Path.Add(new PKey { T = Json.F(k, "t"), P = V(Json.V(k, "pos"), Vector3.zero), Yaw = Json.F(k, "yaw") });
            act.Path.Sort((x, y) => x.T.CompareTo(y.T));
            foreach (var k in Json.L(a, "anim") ?? new List<object>())
                act.Anims.Add(new KeyValuePair<float, string>(Json.F(k, "t"), Json.S(k, "trigger") ?? Json.S(k, "bool")));
            var p0 = act.Path.Count > 0 ? act.Path[0] : new PKey();
            try
            {
                if (creature)
                {
                    var pf = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Json.S(a, "prefab")) : null;
                    if (pf == null) { Plugin.Warn("Einstellung: Prefab fehlt " + Json.S(a, "prefab")); return null; }
                    var go = UnityEngine.Object.Instantiate(pf, p0.P, Quaternion.Euler(0f, p0.Yaw, 0f));
                    foreach (var ai in go.GetComponentsInChildren<BaseAI>(true)) ai.enabled = false;
                    foreach (var rb2 in go.GetComponentsInChildren<Rigidbody>(true)) rb2.isKinematic = true;
                    // Bauteile als Requisite (Feuerstellen fuer brennende Haeuser usw.): nicht einstuerzen, Feuer an
                    foreach (var w in go.GetComponentsInChildren<WearNTear>(true)) UnityEngine.Object.Destroy(w);
                    foreach (var fp in go.GetComponentsInChildren<Fireplace>(true))
                    {
                        try
                        {
                            var tv = HarmonyLib.Traverse.Create(fp);
                            if (tv.Field("m_infiniteFuel").FieldExists()) tv.Field("m_infiniteFuel").SetValue(true);
                            var nv = go.GetComponent<ZNetView>();
                            if (nv != null && nv.GetZDO() != null) nv.GetZDO().Set("fuel", Mathf.Max(1f, fp.m_maxFuel));
                        }
                        catch (Exception e) { Plugin.Quiet(e, "Shots.Feuer"); }
                    }
                    go.transform.localScale *= Json.F(a, "scale", 1f);
                    act.Go = go;
                }
                else
                {
                    object look = Json.V(a, "look");
                    if (look == null && Json.Has(a, "cast")) look = Cast(Json.S(a, "cast"));
                    if (look == null && Json.B(a, "player")) look = CutsceneTrack.PlayerLook();
                    if (look == null && Json.Has(a, "npc")) look = CutsceneTrack.NpcLook(Json.S(a, "npc"));
                    act.Go = Npcs.SpawnTemp("shot_" + act.Id, look, p0.P, p0.Yaw);
                    if (Json.Has(a, "scale")) act.Go.transform.localScale = Vector3.one * Json.F(a, "scale", 1f);
                }
                act.An = act.Go.GetComponentInChildren<Animator>();
            }
            catch (Exception e) { Plugin.Warn("Einstellung: Darsteller " + act.Id + ": " + e.Message); return null; }
            return act;
        }

        public static void Stop()
        {
            if (shot == null) return;
            foreach (var a in acts) if (a.Go != null) { if (a.Creature) { var nv = a.Go.GetComponent<ZNetView>(); if (nv != null && nv.GetZDO() != null) nv.Destroy(); else UnityEngine.Object.Destroy(a.Go); } else UnityEngine.Object.Destroy(a.Go); }
            acts.Clear(); cam.Clear();
            if (hidden.Count > 0) Statics.Hide(null, false);
            Statics.HideBoxes(null);
            Foliage.SetClear(null);
            Grass.ShotOff = false;
            foreach (var lp in lamps) if (lp != null) UnityEngine.Object.Destroy(lp);
            lamps.Clear(); camLight = null; RestoreSky(); ShowWater();
            if (lSun != 1f && EnvMan.instance != null && EnvMan.instance.m_dirLight != null && sunSet >= 0f) EnvMan.instance.m_dirLight.intensity = sunBase;
            lAmb = null; lFogCol = null; lSun = 1f; lFog = 1f;
            recast = null; CutsceneTrack.CamLightMul = 1f;
            if (Time.timeScale == 0f) Time.timeScale = 1f;
            { var plr = Player.m_localPlayer; var bd = plr != null ? plr.GetComponent<Rigidbody>() : null; if (bd != null) bd.isKinematic = false; }
            if (followWait > 0) { Plugin.Log("Einstellung: " + followWait + " Bilder auf Gelaende gewartet"); followWait = 0; }
            if (zoneWas >= 0 && ZoneSystem.instance != null) { try { HarmonyLib.Traverse.Create(ZoneSystem.instance).Field("m_activeArea").SetValue(zoneWas); } catch { } zoneWas = -1; }
            Statics.Extra.Clear();
            if (recCam != null) recCam.targetTexture = null;
            if (rec) Time.captureFramerate = 0;
            if (csId != null) { CutsceneTrack.ForceT = -1f; try { StoryUi.StopCut(); } catch (Exception e) { Plugin.Quiet(e, "Shots.StopCut"); } csId = null; }
            if (Hud.instance != null) Hud.instance.m_userHidden = hudWas;
            if (Player.m_localPlayer != null) foreach (var r in Player.m_localPlayer.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            try { if (EnvMan.instance != null) { HarmonyLib.Traverse.Create(EnvMan.instance).Field("m_debugTimeOfDay").SetValue(false); } } catch { }
            Plugin.Log("Einstellung " + shotId + " Ende" + (rec ? ": " + frame + " Bilder in " + outDir : ""));
            shot = null; rec = false;
        }

        // erst laden lassen (feste Modelle um die Kamerapunkte), dann abspielen; bei Aufnahme jedes Bild speichern
        static IEnumerator Run()
        {
            var o = shot;
            float wait = Time.realtimeSinceStartup;
            while (shot == o && Time.realtimeSinceStartup - wait < preload + 12f && (Statics.Pending > 0 || Intro.Loading() || Time.realtimeSinceStartup - wait < preload)) yield return null;
            if (csId != null)
            {
                float w2 = Time.realtimeSinceStartup;
                while (shot == o && Time.realtimeSinceStartup - w2 < 40f && (StoryUi.I == null || StoryUi.I.track == null || !StoryUi.I.track.Started)) yield return null;
                CutsceneTrack.ForceT = csFrom;
                for (int k = 0; k < 20 && shot == o; k++) yield return null;      // Darsteller an der Startstelle einschwingen lassen
            }
            if (shot != o) yield break;
            MakeFires(o);
            if (rec) Time.captureFramerate = fps;
            started = true; t = 0f; holding = false;
            bool follow = Json.B(o, "follow");
            while (shot == o)
            {
                yield return new WaitForEndOfFrame();
                if (shot != o) yield break;
                if (follow)
                {
                    // "follow": Spieler (unsichtbar) unter der Kamera mitfuehren, damit Valheim dort Gelaende laedt;
                    // solange die Zone unter der Kamera noch nicht geladen ist, steht die Zeit (kein Bild)
                    var plf = Player.m_localPlayer; var cmf = recCam != null ? recCam : Camera.main;
                    if (plf != null && cmf != null)
                    {
                        var cp = cmf.transform.position;
                        plf.transform.position = new Vector3(cp.x, Mathf.Max(cp.y - 3f, MapImport.Height(cp.x, cp.z) + 0.5f), cp.z);
                        var body = plf.GetComponent<Rigidbody>(); if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
                        bool ready = ZoneSystem.instance == null || ZoneSystem.instance.IsZoneLoaded(cp);
                        if (!ready && holdN < 240) { followWait++; holdN++; holding = true; continue; }   // Valheim-Zeit laeuft weiter (sonst laedt nichts), nur die Cutscene steht
                        holding = false; holdN = 0;
                    }
                }
                if (rec && rt != null)
                {
                    var prev = RenderTexture.active; RenderTexture.active = rt;
                    rb.ReadPixels(new Rect(0, 0, recW, recH), 0, 0, false);
                    RenderTexture.active = prev;
                    // Blenden der Cutscene (sonst nur als UI-Ueberlagerung) in die Aufnahme einrechnen
                    var trk = StoryUi.I != null ? StoryUi.I.track : null;
                    float bl = trk != null && csId != null ? trk.Black() : 0f;
                    if (bl > 0.003f)
                    {
                        var px = rb.GetPixels32(); var fc = (Color32)trk.FadeColor; int ib = Mathf.RoundToInt(Mathf.Clamp01(bl) * 256f);
                        for (int q = 0; q < px.Length; q++)
                        {
                            var c0 = px[q];
                            px[q] = new Color32((byte)(c0.r + (((fc.r - c0.r) * ib) >> 8)), (byte)(c0.g + (((fc.g - c0.g) * ib) >> 8)), (byte)(c0.b + (((fc.b - c0.b) * ib) >> 8)), 255);
                        }
                        rb.SetPixels32(px);
                    }
                    rb.Apply(false);
                    File.WriteAllBytes(Path.Combine(outDir, "f" + frame.ToString("D5") + ".jpg"), rb.EncodeToJPG(95));
                    if (frame == 3 && Json.Has(o, "probe")) Probe(recCam, Json.L(o, "probe"));
                    if (Json.B(o, "actor_log") && StoryUi.I != null && StoryUi.I.track != null)
                        try { File.AppendAllText(Path.Combine(outDir, "actors.jsonl"), StoryUi.I.track.ActorLog(recCam) + "\n"); } catch (Exception e) { Plugin.Quiet(e, "Shots.ActorLog"); }
                    frame++;
                }
                if (t >= dur) { Stop(); yield break; }
            }
        }

        // "probe": [[0.5, 0.85], ...] = Fehlersuche: was liegt an diesen Bildstellen (Kollision + alle Modelle auf dem Strahl)
        static void Probe(Camera c, List<object> pts)
        {
            if (c == null || pts == null) return;
            var rs = UnityEngine.Object.FindObjectsOfType<Renderer>();
            foreach (var pj in pts)
            {
                var l = pj as List<object>; if (l == null || l.Count < 2) continue;
                var ray = c.ViewportPointToRay(new Vector3(N(l[0]), N(l[1]), 0f));
                var sb = new System.Text.StringBuilder("Probe " + N(l[0]) + "," + N(l[1]) + " von " + ray.origin + ": ");
                foreach (var h in Physics.RaycastAll(ray, 500f, ~0, QueryTriggerInteraction.Collide)) sb.Append("[hit " + h.collider.name + " " + h.distance.ToString("0.0") + "] ");
                var near = new List<KeyValuePair<float, string>>();
                foreach (var r in rs)
                {
                    if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                    float d; if (!r.bounds.IntersectRay(ray, out d)) continue;
                    var m = r.sharedMaterial; var sh = m != null && m.shader != null ? m.shader.name : "-";
                    near.Add(new KeyValuePair<float, string>(d, r.name + "/" + (m != null ? m.name : "-") + "/" + sh + " " + d.ToString("0.0") + " b" + r.bounds.size.ToString("0")));
                }
                near.Sort((a, b) => a.Key.CompareTo(b.Key));
                for (int i = 0; i < near.Count && i < 12; i++) sb.Append("{" + near[i].Value + "} ");
                Plugin.Log(sb.ToString());
            }
            Plugin.Log("Probe Kamera " + c.name + " clear " + c.clearFlags + " far " + c.farClipPlane + " fog " + RenderSettings.fog + " " + RenderSettings.fogColor + " " + RenderSettings.fogDensity + " " + RenderSettings.fogMode);
            var comps = new List<string>();
            foreach (var mb in c.GetComponents<Behaviour>()) comps.Add(mb.GetType().Name + (mb.enabled ? "" : "(aus)"));
            Plugin.Log("Probe Kamera-Komponenten: " + string.Join(", ", comps.ToArray()));
            var cams = new List<string>();
            foreach (var oc in Camera.allCameras) cams.Add(oc.name + " d" + oc.depth + " " + oc.clearFlags + (oc.targetTexture != null ? " rt" : ""));
            Plugin.Log("Probe Kameras: " + string.Join(", ", cams.ToArray()));
            var around = new List<string>();
            foreach (var r in rs)
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !r.bounds.Contains(c.transform.position)) continue;
                var m = r.sharedMaterial;
                around.Add(r.name + "/" + (m != null && m.shader != null ? m.shader.name : "-") + " b" + r.bounds.size.ToString("0"));
            }
            Plugin.Log("Probe um die Kamera: " + string.Join(", ", around.ToArray()));
        }

        // Reiter: Darsteller sitzt auf einem Tier der Einstellung ("ride": "<id>"), am Sattelpunkt (sonst Ruecken)
        static void PlaceRider(Act a)
        {
            Act m = null; foreach (var x in acts) if (x.Id == a.Ride) { m = x; break; }
            if (m == null || m.Go == null || a.Go == null) return;
            if (a.Seat == null)
            {
                foreach (var tr in m.Go.GetComponentsInChildren<Transform>(true))
                {
                    var n = tr.name.ToLowerInvariant();
                    if (n.Contains("attach") && (a.Seat == null || n.Contains("saddle") || (tr.parent != null && tr.parent.name.ToLowerInvariant().Contains("saddle")))) a.Seat = tr;
                }
                if (a.Seat == null) a.Seat = m.Go.transform;
                foreach (var tr in m.Go.GetComponentsInChildren<Transform>(true)) if (tr.name.ToLowerInvariant().Contains("saddle")) tr.gameObject.SetActive(true);
            }
            a.Go.transform.position = a.Seat.position + m.Go.transform.rotation * a.RideOff;
            a.Go.transform.rotation = m.Go.transform.rotation;
            if (a.An != null) { try { a.An.SetFloat("forward_speed", 0f); } catch { } }
        }

        public static void Tick()
        {
            if (shot == null || !started) return;
            if (holding) return;          // Gelaende laedt noch: Cutscene-Zeit steht (sonst fehlen Bilder und der Ton verrutscht)
            t += rec ? 1f / fps : Time.deltaTime;
            float tt = Mathf.Min(t, dur);
            if (csId != null) CutsceneTrack.ForceT = csFrom + tt;
            foreach (var a in acts)
            {
                if (a.Go == null) continue;
                if (a.Ride == null && a.Path.Count > 0)
                {
                    int i = 0; while (i + 1 < a.Path.Count && a.Path[i + 1].T <= tt) i++;
                    var p0 = a.Path[i]; var p1 = a.Path[Mathf.Min(i + 1, a.Path.Count - 1)];
                    float u = p1.T > p0.T ? Mathf.Clamp01((tt - p0.T) / (p1.T - p0.T)) : 0f;
                    var pos = Vector3.Lerp(p0.P, p1.P, u);
                    a.Go.transform.position = pos;
                    a.Go.transform.rotation = Quaternion.Euler(0f, Mathf.LerpAngle(p0.Yaw, p1.Yaw, u), 0f);
                    float speed = p1.T > p0.T ? Vector3.Distance(new Vector3(p0.P.x, 0f, p0.P.z), new Vector3(p1.P.x, 0f, p1.P.z)) / (p1.T - p0.T) : 0f;
                    if (a.An != null) { try { a.An.SetFloat("forward_speed", speed); } catch { } }
                }
                while (a.AnimIdx < a.Anims.Count && a.Anims[a.AnimIdx].Key <= tt)
                {
                    var n = a.Anims[a.AnimIdx++].Value;
                    if (a.An != null && !string.IsNullOrEmpty(n)) { try { if (n.StartsWith("!")) a.An.SetBool(n.Substring(1), false); else if (n.StartsWith("+")) a.An.SetBool(n.Substring(1), true); else a.An.SetTrigger(n); } catch { } }
                }
            }
        }

        public static void LateTick()
        {
            if (shot == null) return;
            foreach (var a in acts) if (a.Ride != null) PlaceRider(a);
        }

        // aus GameCamera.LateUpdate (nach Valheims Kamera)
        public static bool ApplyCamera(Camera c)
        {
            if (shot == null || c == null) return false;
            if (csId != null && cam.Count == 0)
            {
                if (rec) { EnsureRt(); c.targetTexture = rt; recCam = c; }
                ApplyLight();
                return false;                                   // Kamera kommt aus der Cutscene
            }
            // Cutscene mit eigener Kamera ("cam" gesetzt): Animationen aus dem Spiel, Kamera von uns
            if (cam.Count == 0) return false;
            try { LateTick(); } catch (Exception e) { Plugin.Quiet(e, "Shots.Ride"); }
            float tt = started ? Mathf.Min(t, dur) : 0f;
            int i = 0; while (i + 1 < cam.Count && cam[i + 1].T <= tt) i++;
            var k1 = cam[i]; var k2 = cam[Mathf.Min(i + 1, cam.Count - 1)];
            var k0 = cam[Mathf.Max(i - 1, 0)]; var k3 = cam[Mathf.Min(i + 2, cam.Count - 1)];
            float u = k2.T > k1.T ? Mathf.Clamp01((tt - k1.T) / (k2.T - k1.T)) : 0f;
            var p = CR(k0.P, k1.P, k2.P, k3.P, u); var l = CR(k0.L, k1.L, k2.L, k3.L, u);
            c.transform.position = p;
            if ((l - p).sqrMagnitude > 1e-6f) c.transform.rotation = Quaternion.LookRotation(l - p);
            c.fieldOfView = Mathf.Lerp(k1.Fov, k2.Fov, u);
            if (c.farClipPlane < 6000f) c.farClipPlane = 6000f;
            ApplyLight();
            PlaceCamLight(c); ApplySky(c);
            if (rec) { EnsureRt(); c.targetTexture = rt; recCam = c; }
            return true;
        }

        // "cam_light": {"color": [1, 0.7, 0.45], "intensity": 1.5, "range": 8, "offset": [0.6, 0.4, 0.2]} = Licht, das mit der
        // Kamera mitfaehrt (offset in Kamera-Richtungen rechts/oben/vor): Nachtaufnahmen, Gesichter wie von Fackeln beleuchtet
        static Light camLight;
        // "sky": false = kein Himmel (schwarz bzw. Nebelfarbe), z. B. unterirdische Raeume: Valheims Gelaende ist von
        // unten durchsichtig, man saehe sonst den Himmel
        static Camera skyCam; static CameraClearFlags skyFlags; static Color skyBg;
        // "water": false = Valheims Wasserflaeche ausblenden (Raeume unter dem Meeresspiegel: von unten waere sie eine graue Decke)
        static readonly List<Renderer> waterOff = new List<Renderer>(); static float waterNext;
        static void HideWater()
        {
            if (shot == null || !Json.Has(shot, "water") || Json.B(shot, "water") || Time.unscaledTime < waterNext) return;
            bool first = waterNext == 0f;
            waterNext = Time.unscaledTime + 0.1f;      // oft pruefen: beim Mitfuehren (follow) kommen staendig neue Zonen mit Wasser dazu
            int nv = 0, n0 = waterOff.Count;
            foreach (var wv in UnityEngine.Object.FindObjectsOfType<WaterVolume>())
            {
                nv++;
                var o = HarmonyLib.Traverse.Create(wv).Field("m_waterSurface").GetValue();
                var r = o as Renderer ?? (o is GameObject ? ((GameObject)o).GetComponent<Renderer>() : null);
                if (r != null && r.enabled) { r.enabled = false; waterOff.Add(r); }
            }
            // alles mit Wasser-Shader (Seen/Fluesse des Pakets, Meer)
            var names = new List<string>();
            foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
            {
                if (!r.enabled || r.sharedMaterial == null || r.sharedMaterial.shader == null) continue;
                var sn = r.sharedMaterial.shader.name.ToLowerInvariant();
                if (!sn.Contains("water") && !sn.Contains("ocean")) continue;
                r.enabled = false; waterOff.Add(r);
                if (names.Count < 6) names.Add(r.name + "/" + r.sharedMaterial.shader.name);
            }
            if (first || waterOff.Count != n0) Plugin.Log("Wasser aus: " + nv + " Wasserflaechen, " + (waterOff.Count - n0) + " neu aus (" + string.Join(", ", names.ToArray()) + ")");
        }
        static void ShowWater()
        {
            foreach (var r in waterOff) if (r != null) r.enabled = true;
            waterOff.Clear(); waterNext = 0f;
        }
        static void ApplySky(Camera c)
        {
            try { HideWater(); } catch (Exception e) { Plugin.Quiet(e, "Shots.Wasser"); }
            if (shot == null || c == null || !Json.Has(shot, "sky") || Json.B(shot, "sky")) return;
            if (skyCam != c)
            {
                RestoreSky(); skyCam = c; skyFlags = c.clearFlags; skyBg = c.backgroundColor;
                // Wolkendecke/Himmelsobjekte (folgen der Kamera; von unten durchs Gelaende sonst eine graue Decke)
                foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (!r.enabled) continue;
                    var m = r.sharedMaterial; var sn = m != null && m.shader != null ? m.shader.name : "";
                    var n = r.name.ToLowerInvariant();
                    float sz = r.bounds.size.magnitude;
                    bool sky = sn == "Custom/Clouds" || sn == "Custom/SkyObject" || sn == "Custom/Yggdrasil"
                        || ((n.Contains("cloud") || n.StartsWith("mist") || n.Contains("fog") || n.StartsWith("rain") || n.StartsWith("snow")) && sz > 60f)
                        || (r is ParticleSystemRenderer && sz > 200f)                                 // Wetter-Partikel um die Kamera
                        || (sn == "Custom/Heightmap" && n == "lodmesh")                              // ferne Gelaende-Huelle (von unten sichtbar)
                        || (sn == "Sprites/Default" && sz > 500f);                                   // riesige Partikel-/Sprite-Huelle um die Kamera
                    if (sky) { r.enabled = false; skyOff.Add(r); }
                }
                // Sonnenstrahlen-Effekt malt den "Himmel" (fernste Tiefe) hell an -> unter der Erde graue Decke
                foreach (var b in c.GetComponents<Behaviour>())
                {
                    var tn = b.GetType().Name;
                    if ((tn == "SunShafts" || tn == "GlobalFog") && b.enabled) { b.enabled = false; fxOff.Add(b); }
                }
                var hn = new List<string>(); foreach (var r in skyOff) if (hn.Count < 12 && !hn.Contains(r.name)) hn.Add(r.name);
                foreach (var b in fxOff) hn.Add(b.GetType().Name);
                Plugin.Log("Himmel aus: " + skyOff.Count + " Himmelsobjekte (" + string.Join(", ", hn.ToArray()) + ")");
            }
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = lFogCol ?? Color.black;
        }
        static readonly List<Renderer> skyOff = new List<Renderer>();
        static readonly List<Behaviour> fxOff = new List<Behaviour>();
        static void RestoreSky()
        {
            foreach (var b in fxOff) if (b != null) b.enabled = true;
            fxOff.Clear();
            if (skyCam != null) { skyCam.clearFlags = skyFlags; skyCam.backgroundColor = skyBg; }
            foreach (var r in skyOff) if (r != null) r.enabled = true;
            skyOff.Clear();
            skyCam = null;
        }
        static void PlaceCamLight(Camera c)
        {
            object cl = shot != null ? Json.V(shot, "cam_light") : null;
            if (cl == null || c == null) return;
            if (camLight == null)
            {
                var go = new GameObject("gi_shot_camlight");
                camLight = go.AddComponent<Light>();
                camLight.type = LightType.Point;
                var col = Json.V(cl, "color") as List<object>;
                camLight.color = col != null && col.Count >= 3 ? new Color(N(col[0]), N(col[1]), N(col[2])) : new Color(1f, 0.72f, 0.48f);
                camLight.intensity = Json.F(cl, "intensity", 1.5f);
                camLight.range = Json.F(cl, "range", 8f);
                camLight.shadows = LightShadows.None;
                lamps.Add(go);
            }
            var off = Json.V(cl, "offset") as List<object>;
            var o3 = off != null && off.Count >= 3 ? new Vector3(N(off[0]), N(off[1]), N(off[2])) : new Vector3(0.6f, 0.4f, 0.2f);
            var tr = c.transform;
            camLight.transform.position = tr.position + tr.right * o3.x + tr.up * o3.y + tr.forward * o3.z;
        }

        // nach der Cutscene-Kamera (die setzt warmes Umgebungslicht): eigenes Licht der Einstellung drueber
        // "cam_back": Originalkamera um so viele Meter nach hinten ziehen (Valheim-Figuren sind kraeftiger/groessere Koepfe -> Nahaufnahmen sonst zu eng)
        public static void AfterCutCamera(Camera c = null)
        {
            if (shot == null || csId == null) return;
            try
            {
                ApplyLight();
                float back = Json.F(shot, "cam_back", 0f);
                if (c != null && back != 0f) c.transform.position -= c.transform.forward * back;
                // "cam_shift": [rechts, hoch, vor] in Kamera-Richtungen (Figuren stehen in Valheim manchmal hoeher/tiefer als im Original)
                var sh = shot != null ? Json.V(shot, "cam_shift") as List<object> : null;
                if (c != null && sh != null && sh.Count >= 3) c.transform.position += c.transform.right * N(sh[0]) + c.transform.up * N(sh[1]) + c.transform.forward * N(sh[2]);
                // "cam_tilt": Grad nach oben (+) / unten (-) drehen
                float tilt = Json.F(shot, "cam_tilt", 0f);
                if (c != null && tilt != 0f) c.transform.rotation = Quaternion.AngleAxis(-tilt, c.transform.right) * c.transform.rotation;
                PlaceCamLight(c); ApplySky(c);
            }
            catch (Exception e) { Plugin.Quiet(e, "Shots.Licht"); }
        }

        // Feuer erst nach dem Vorladen anlegen (brennende Gebaeude brauchen die geladenen Modelle)
        static void MakeFires(object o)
        {
            foreach (var fj in Json.L(o, "fire") ?? new List<object>())
            {
                try
                {
                    GameObject fg;
                    if (Json.Has(fj, "burn")) fg = FireFx.Burn(Json.S(fj, "burn"), V(Json.V(fj, "near"), Vector3.zero), Json.F(fj, "radius", 25f), Json.F(fj, "size", 2f), Json.F(fj, "rate", 80f));
                    else fg = FireFx.Spawn(V(Json.V(fj, "pos"), Vector3.zero), Json.F(fj, "size", 2.5f), Json.F(fj, "rate", 25f), !Json.Has(fj, "light") || Json.B(fj, "light"));
                    if (fg != null) lamps.Add(fg);
                }
                catch (Exception e) { Plugin.Warn("Feuer: " + e.Message); }
            }
        }

        static void EnsureRt()
        {
            if (rt == null || rt.width != recW || rt.height != recH)
            {
                rt = new RenderTexture(recW, recH, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1 };
                rb = new Texture2D(recW, recH, TextureFormat.RGB24, false);
            }
        }

        static Vector3 CR(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
        {
            float u2 = u * u, u3 = u2 * u;
            return 0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
        }

        public static string Info() { return shot == null ? "shot: aus" : "shot " + shotId + " t=" + t.ToString("F2") + "/" + dur.ToString("F2") + (rec ? " Bild " + frame : "") + (started ? "" : " (laedt)"); }
    }
}
