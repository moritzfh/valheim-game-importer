using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GameImporter
{
    /// <summary>
    /// Startbildschirm wie in OoT (En_Mag + gHyruleFieldTitleScreenCs): beim Spielstart (einmal je Sitzung, sobald die Welt steht)
    /// laeuft die Titel-Cutscene (Link auf Epona in der Steppe) in Schleife, Titelmusik, das Logo blendet ein, PRESS START blinkt.
    /// Taste wie im Original: vor dem Logo -> Logo sofort da; danach -> Ton (NA_SE_SY_PIECE_OF_HEART), Abblenden, dann
    ///   neues Spiel (Merker "new_flag" fehlt): Intro (Schlaf -> Albtraum -> Kokiri-Wald), danach Aufwachen (OotWake)
    ///   sonst: weiter, wo man war.
    /// pack.json "oot_title": {"cutscene": "title", "logo": "player/hud/title_logo.png", "press": "player/hud/title_press.png",
    ///   "music": ["music/title_intro.ogg", "music/title_loop.ogg"], "logo_at": 15.1, "press_at": 27.2,
    ///   "intro": ["intro_sleep", "nightmare", "intro_kf"], "new_flag": "oot_awake"}
    /// Fernsteuerung: "oot title" (nochmal), "oot title new" (wie neues Spiel, mit Intro), "oot title press", "oot title off".
    /// </summary>
    public class OotTitle : MonoBehaviour
    {
        public static OotTitle I;
        Pack forPack; object cfg;
        Texture2D logo, press, black;
        AudioClip musIntro, musLoop; AudioSource srcA, srcB;
        // Ablauf
        enum S { Off, Wait, Title, FadeOut, Intro, FadeIn }
        S st = S.Off;
        float t0, stT, logoForced = -1f, waitT;
        bool shownThisSession, forceNew, introOn = true, hudWas;
        readonly Queue<string> chain = new Queue<string>();
        public static bool Active { get { return I != null && I.st != S.Off; } }
        /// <summary>Startbildschirm kommt noch (Welt laedt, Paket noch nicht gelesen): Aufwach-Szene wartet darauf.</summary>
        public static bool Pending { get { return I != null && !I.shownThisSession && (I.cfg != null || Pack.Current != I.forPack); } }

        public static void Ensure(GameObject host) { if (I == null) I = host.AddComponent<OotTitle>(); }

        void Setup(Pack pk)
        {
            forPack = pk; cfg = null; logo = press = null; musIntro = musLoop = null;
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            cfg = Json.V(meta, "oot_title");
            if (cfg == null) return;
            logo = LoadTex(pk, Json.S(cfg, "logo", "player/hud/title_logo.png"));
            press = LoadTex(pk, Json.S(cfg, "press", "player/hud/title_press.png"));
            StartCoroutine(LoadMusic(pk));
        }

        static Texture2D LoadTex(Pack pk, string rel)
        {
            try
            {
                var f = Path.Combine(pk.Dir, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) { Plugin.Warn("Startbildschirm: fehlt " + f); return null; }
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, true); t.LoadImage(File.ReadAllBytes(f)); t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Trilinear; t.anisoLevel = 4;
                return t;
            }
            catch (Exception e) { Plugin.Quiet(e, "OotTitle.Tex"); return null; }
        }

        IEnumerator LoadMusic(Pack pk)
        {
            var l = Json.L(cfg, "music") ?? new List<object>();
            for (int i = 0; i < l.Count && i < 2; i++)
            {
                var f = Path.Combine(pk.Dir, ((string)l[i]).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(f)) { Plugin.Warn("Startbildschirm-Musik fehlt: " + f); continue; }
                using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + f.Replace('\\', '/'), AudioType.OGGVORBIS))
                {
                    ((DownloadHandlerAudioClip)rq.downloadHandler).streamAudio = false;
                    yield return rq.SendWebRequest();
                    if (rq.result != UnityWebRequest.Result.Success) { Plugin.Warn("Startbildschirm-Musik: " + rq.error); continue; }
                    var c = DownloadHandlerAudioClip.GetContent(rq);
                    if (i == 0 && l.Count > 1) musIntro = c; else musLoop = c;
                }
            }
        }

        static float Pref(string k) { try { return Mathf.Clamp01(PlayerPrefs.GetFloat(k, 1f)); } catch { return 1f; } }

        void Update()
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                if (cfg == null) { if (st != S.Off) Stop(); return; }
                Step();
            }
            catch (Exception e) { Plugin.Quiet(e, "OotTitle"); }
        }

        // Waehrend Startbildschirm/Intro: Spieler festhalten und unverwundbar (sonst z. B. Valheims Schwimm-Ausdauer im Wasser)
        bool godWas, held;
        void Hold(Player p, bool on)
        {
            if (p == null) { held = false; return; }
            var body = p.GetComponent<Rigidbody>();
            if (on)
            {
                if (!held) { held = true; try { var ig = HarmonyLib.AccessTools.Method(typeof(Player), "InGodMode"); godWas = ig != null && (bool)ig.Invoke(p, null); } catch { godWas = false; } }
                God(p, true);
                if (body != null) { body.velocity = Vector3.zero; body.isKinematic = true; }
            }
            else if (held)
            {
                held = false;
                God(p, godWas);
                if (body != null) { body.isKinematic = false; body.velocity = Vector3.zero; }
            }
        }

        // Startbildschirm: die Kamera springt zwischen weit auseinander liegenden Einstellungen -> Valheim-Gelaende im groesseren
        // Umkreis halten (ZoneSystem.m_activeArea 2 -> 4), sonst baut es sich bei jedem Schnitt erst auf (Moritz 09.11.)
        static int areaWas = -1; static System.Reflection.FieldInfo areaF; static bool areaLooked;
        static void WideZones(bool on)
        {
            try
            {
                if (ZoneSystem.instance == null) return;
                // aktuelle Valheim-Fassung hat kein m_activeArea mehr -> einmal suchen, sonst nichts tun (keine Warnungen je Bild)
                if (!areaLooked) { areaLooked = true; areaF = typeof(ZoneSystem).GetField("m_activeArea", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); }
                var f = areaF;
                if (f == null || f.FieldType != typeof(int)) return;
                if (on && areaWas < 0) { areaWas = (int)f.GetValue(ZoneSystem.instance); f.SetValue(ZoneSystem.instance, Math.Max(areaWas, 4)); }
                else if (!on && areaWas >= 0) { f.SetValue(ZoneSystem.instance, areaWas); areaWas = -1; }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotTitle.Zonen"); }
        }

        // Moritz' Spielstand stand nach Beenden im Startbildschirm auf der Steppe (Spieler wird dort unter die Kamera gestellt):
        // beim Speichern des Abmeldepunkts waehrend einer mitfuehrenden Cutscene den Platz vor der Cutscene nehmen
        public static void Patch(HarmonyLib.Harmony h)
        {
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(PlayerProfile), "SaveLogoutPoint");
                if (m != null) h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(OotTitle), nameof(SaveLogoutPre)));
                else Plugin.Warn("OotTitle: PlayerProfile.SaveLogoutPoint fehlt");
            }
            catch (Exception e) { Plugin.Quiet(e, "OotTitle.Patch"); }
        }
        static System.Reflection.MethodInfo setLogout;
        static bool SaveLogoutPre(PlayerProfile __instance)
        {
            try
            {
                var tr = StoryUi.I != null ? StoryUi.I.track : null;
                Vector3 home;
                if (tr != null && tr.HomeIfFollowing(out home))
                {
                    if (setLogout == null) setLogout = HarmonyLib.AccessTools.Method(typeof(PlayerProfile), "SetLogoutPoint");
                    if (setLogout != null) { setLogout.Invoke(__instance, new object[] { home }); return false; }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "OotTitle.Abmeldepunkt"); }
            return true;
        }

        static void God(Player p, bool on)
        {
            try { var m = HarmonyLib.AccessTools.Method(typeof(Player), "SetGodMode"); if (m != null) m.Invoke(p, new object[] { on }); } catch (Exception e) { Plugin.Quiet(e, "OotTitle.God"); }
        }

        void Step()
        {
            var p = Player.m_localPlayer;
            if (st != S.Off)
            {
                if (p == null) { Plugin.Warn("Startbildschirm: kein Spieler mehr - abgebrochen"); Finish(false); return; }
                Hold(p, true);
            }
            switch (st)
            {
                case S.Off:
                    if (shownThisSession || p == null || StoryUi.I == null || StoryUi.Busy) return;
                    if (Intro.Loading() || OotPlay.Settling || OotPlay.Warping) { waitT = Time.unscaledTime; return; }
                    if (Time.unscaledTime - waitT < 1.0f) return;                                 // Welt kurz stehen lassen
                    Begin();
                    return;
                case S.Wait:
                    WideZones(true);
                    // Titel-Cutscene laedt (schwarz) -> laeuft
                    if (StoryUi.CutId != CutTitle) { if (Time.unscaledTime - stT > 2f) { Plugin.Warn("Startbildschirm: Cutscene fehlt"); Finish(false); } return; }
                    var tr = StoryUi.I.track;
                    if (tr != null) { tr.Loop = true; tr.NoSkip = true; tr.Follow = true; tr.NoBars = true; }
                    if (tr != null && tr.Started) { st = S.Title; t0 = Time.unscaledTime; StartMusic(); }
                    CutsceneTrack.MusicActive = true;
                    return;
                case S.Title:
                    CutsceneTrack.MusicActive = true;
                    if (StoryUi.CutId != CutTitle) { Finish(false); return; }                      // von aussen beendet
                    if (Time.unscaledTime - t0 > 0.8f && Pressed())
                    {
                        if (!LogoUp) { logoForced = Time.unscaledTime; OotSfx.Play("NA_SE_SY_PIECE_OF_HEART", 0.9f); }
                        else if (logoForced < 0f || Time.unscaledTime - logoForced > 0.35f) { OotSfx.Play("NA_SE_SY_PIECE_OF_HEART", 0.9f); st = S.FadeOut; stT = Time.unscaledTime; }
                    }
                    return;
                case S.FadeOut:
                    {
                        CutsceneTrack.MusicActive = true;
                        float k = Mathf.Clamp01((Time.unscaledTime - stT) / 1.1f);
                        SetMusicVol(1f - k);
                        if (k < 1f) return;
                        WideZones(false);
                        StopMusic();
                        if (StoryUi.CutId == CutTitle) StoryUi.StopCut();
                        bool isNew = forceNew || !Story.Flags.Contains(Json.S(cfg, "new_flag", "oot_awake"));
                        chain.Clear();
                        if (isNew && introOn) foreach (var c in Json.L(cfg, "intro") ?? new List<object>()) if (c is string && Pack.Current.Cutscenes.ContainsKey((string)c)) chain.Enqueue((string)c);
                        st = chain.Count > 0 ? S.Intro : S.FadeIn; stT = Time.unscaledTime;
                        // neues Spiel: vorher ins Haus (dort endet das Intro, dann weckt Navi Link)
                        Vector3 hp; float hy;
                        if (isNew && OotWake.TryEnd(out hp, out hy) && p != null)
                        {
                            p.transform.position = hp + Vector3.up * 0.05f; p.transform.rotation = Quaternion.Euler(0f, hy, 0f);
                            var body = p.GetComponent<Rigidbody>(); if (body != null) { body.position = p.transform.position; body.velocity = Vector3.zero; }
                            try { ZNet.instance.SetReferencePosition(hp); } catch { }
                        }
                        return;
                    }
                case S.Intro:
                    if (StoryUi.Busy) { stT = Time.unscaledTime; return; }
                    if (chain.Count > 0) { var id = chain.Dequeue(); StoryUi.PlayCutscene(id); Plugin.Log("Startbildschirm: Intro " + id); return; }
                    if (Time.unscaledTime - stT > 0.3f) { st = S.FadeIn; stT = Time.unscaledTime; }
                    return;
                case S.FadeIn:
                    if (Time.unscaledTime - stT > 1.0f) Finish(true);
                    return;
            }
        }

        string CutTitle { get { return Json.S(cfg, "cutscene", "title"); } }
        bool LogoUp { get { return logoForced >= 0f || Time.unscaledTime - t0 >= Json.F(cfg, "logo_at", 15.1f); } }

        void Begin()
        {
            if (Pack.Current == null || !Pack.Current.Cutscenes.ContainsKey(CutTitle)) { shownThisSession = true; return; }
            shownThisSession = true; logoForced = -1f; chain.Clear();
            hudWas = OotHud.Hidden; OotHud.Hidden = true;
            StoryUi.PlayCutscene(CutTitle);
            st = S.Wait; stT = Time.unscaledTime;
            Plugin.Log("Startbildschirm");
        }

        void Finish(bool ok)
        {
            Hold(Player.m_localPlayer, false);
            WideZones(false);
            st = S.Off; forceNew = false; introOn = true;
            StopMusic(); OotHud.Hidden = hudWas; CutsceneTrack.MusicActive = false; CutsceneTrack.MusicVol = 1f;
            if (StoryUi.CutId == CutTitle) StoryUi.StopCut();
        }

        void Stop() { Finish(false); }

        static bool Pressed()
        {
            if (OotDev.Open) return false;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.LeftShift)
                || Input.GetKeyDown(KeyCode.RightShift) || Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(0)) return true;
            if (Input.GetKeyDown(KeyCode.JoystickButton7) || Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(KeyCode.JoystickButton1)) return true;   // Start, A, B
            if (remotePress) { remotePress = false; return true; }
            return false;
        }
        static bool remotePress;

        // ---------- Musik: Vorspiel, dann nahtlose Schleife ----------
        void StartMusic()
        {
            if (musLoop == null && musIntro == null) return;
            if (srcA == null)
            {
                srcA = gameObject.AddComponent<AudioSource>(); srcB = gameObject.AddComponent<AudioSource>();
                foreach (var s in new[] { srcA, srcB }) { s.spatialBlend = 0f; s.playOnAwake = false; s.bypassReverbZones = true; s.ignoreListenerPause = true; s.priority = 0; }
            }
            SetMusicVol(1f);
            double at = AudioSettings.dspTime + 0.1;
            if (musIntro != null)
            {
                srcA.clip = musIntro; srcA.loop = false; srcA.PlayScheduled(at);
                at += (double)musIntro.samples / musIntro.frequency;
            }
            if (musLoop != null) { srcB.clip = musLoop; srcB.loop = true; srcB.PlayScheduled(at); }
        }

        void SetMusicVol(float k)
        {
            float v = 0.85f * Pref("MusicVolume") * k;
            if (srcA != null) srcA.volume = v;
            if (srcB != null) srcB.volume = v;
        }

        void StopMusic()
        {
            if (srcA != null) srcA.Stop();
            if (srcB != null) srcB.Stop();
        }

        // ---------- Bild: Logo, PRESS START, Blenden ----------
        void OnGUI()
        {
            if (st == S.Off || cfg == null) return;
            GUI.depth = -1000;
            if (black == null) { black = new Texture2D(1, 1); black.SetPixel(0, 0, Color.black); black.Apply(); }
            float W = Screen.width, H = Screen.height;
            var old = GUI.color;
            if (st == S.Title || st == S.FadeOut)
            {
                float tt = Time.unscaledTime - t0;
                float la = logoForced >= 0f ? 1f : Mathf.Clamp01((tt - Json.F(cfg, "logo_at", 15.1f)) / 1.6f);
                if (logo != null && la > 0f)
                {
                    float w = W * 0.66f, h = w * logo.height / logo.width;
                    GUI.color = new Color(1f, 1f, 1f, la);
                    GUI.DrawTexture(new Rect((W - w) / 2f, H * 0.065f, w, h), logo, ScaleMode.ScaleToFit, true);
                }
                float pt = logoForced >= 0f ? Time.unscaledTime - logoForced : tt - Json.F(cfg, "press_at", 27.2f);
                if (press != null && pt >= 0f && (st == S.FadeOut || Mathf.Repeat(pt, 1.4f) < 0.95f))
                {
                    float w = W * 0.27f, h = w * press.height / press.width;
                    GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(pt / 0.8f));
                    GUI.DrawTexture(new Rect((W - w) / 2f, H - H * 0.213f, w, h), press, ScaleMode.ScaleToFit, true);
                }
            }
            float b = 0f;
            if (st == S.Wait) b = 1f;
            else if (st == S.FadeOut) b = Mathf.Clamp01((Time.unscaledTime - stT) / 1.1f);
            else if (st == S.Intro) b = StoryUi.Busy && StoryUi.I.track != null && StoryUi.I.track.Started ? 0f : 1f;
            else if (st == S.FadeIn) b = 1f - Mathf.Clamp01((Time.unscaledTime - stT) / 1.0f);
            if (b > 0.001f) { GUI.color = new Color(0f, 0f, 0f, b); GUI.DrawTexture(new Rect(0, 0, W, H), black); }
            GUI.color = old;
        }

        public static string Remote(string[] a)
        {
            if (I == null) return "title: nicht bereit";
            if (I.cfg == null) return "title: kein oot_title im Paket";
            string arg = a.Length > 2 ? a[2] : "";
            if (arg == "press") { remotePress = true; return "title: Taste"; }
            if (arg == "off") { I.Finish(false); return "title: aus"; }
            if (arg == "info") return "title " + I.st + " t " + (Time.unscaledTime - I.t0).ToString("F1") + " logo " + I.LogoUp + " kette " + I.chain.Count + " cut " + StoryUi.CutId;
            if (Active) I.Finish(false);
            I.forceNew = arg == "new"; I.introOn = arg != "nointro";
            I.shownThisSession = false; I.waitT = -10f;
            return "title: startet" + (I.forceNew ? " (neues Spiel, mit Intro)" : "");
        }

        /// <summary>Dev-Pult: Startbildschirm zeigen, optional wie ein neues Spiel (mit Intro).</summary>
        public static void Show(bool asNew) { Remote(new[] { "oot", "title", asNew ? "new" : "" }); }
    }
}
