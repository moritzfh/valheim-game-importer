using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace GameImporter
{
    // Musik aus dem Spielpaket statt Valheims Musik. pack.json:
    //   "music": {"explore": ["music/a.mp3", ...], "combat": ["music/b.mp3", ...], "volume": 0.6}
    // Erkunden: Titel nacheinander (mit Pause), Kampf: sobald ein Gegner den Spieler angreift; weiche Ueberblendung.
    public class Music : MonoBehaviour
    {
        static Music I;
        Pack forPack;
        readonly List<string> explore = new List<string>(), combat = new List<string>();
        float volume = 0.6f; bool loopOne;
        AudioSource a, b; bool useA = true;
        string mode, cur; float combatUntil, nextT, fadeT;
        readonly Dictionary<string, AudioClip> cache = new Dictionary<string, AudioClip>();
        int ei, ci;

        public static void Ensure(GameObject host) { if (I == null) I = host.AddComponent<Music>(); }

        // Gebietsmusik (OotPlay-Gebiete mit "music"): voller Pfad oder null = normale Erkunden-Liste. Laeuft als nahtlose
        // Schleife; beim Zurueckkehren geht ein Titel dort weiter, wo er aufgehoert hat (wie OoT SEQ_FLAG_RESUME).
        public static string AreaTrack;
        // Geschichte (z. B. Gespraech mit dem Deku-Baum): hat Vorrang vor der Gebietsmusik, null = aus
        public static string StoryTrack;
        readonly Dictionary<string, float> resumeAt = new Dictionary<string, float>();

        // Fanfare (z.B. Gegenstand erhalten): Musik kurz ausblenden und danach wieder hochfahren
        static float duckUntil;
        public static void Duck(float seconds) { duckUntil = Mathf.Max(duckUntil, Time.time + seconds); }
        static float DuckFactor() { float r = duckUntil - Time.time; return r > 0f ? 0f : Mathf.Clamp01(-r / 1.5f); }

        void Setup(Pack pk)
        {
            forPack = pk; explore.Clear(); combat.Clear(); cur = null; mode = null;
            if (a != null) a.Stop(); if (b != null) b.Stop();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var m = Json.V(meta, "music");
            if (m == null) return;
            foreach (var s in Json.L(m, "explore") ?? new List<object>()) explore.Add(Path.Combine(pk.Dir, ((string)s).Replace('/', Path.DirectorySeparatorChar)));
            foreach (var s in Json.L(m, "combat") ?? new List<object>()) combat.Add(Path.Combine(pk.Dir, ((string)s).Replace('/', Path.DirectorySeparatorChar)));
            volume = Json.F(m, "volume", 0.6f);
            loopOne = Json.B(m, "loop");
            if (a == null) { a = gameObject.AddComponent<AudioSource>(); b = gameObject.AddComponent<AudioSource>(); foreach (var s in new[] { a, b }) { s.spatialBlend = 0f; s.loop = false; s.volume = 0f; s.ignoreListenerPause = true; s.bypassReverbZones = true; } }
            Plugin.Log("Musik: " + explore.Count + " Erkunden, " + combat.Count + " Kampf");
        }

        void Update()
        {
            try
            {
                if (Pack.Current != forPack) Setup(Pack.Current);
                var p = Player.m_localPlayer;
                string area = StoryTrack ?? AreaTrack;
                bool on = forPack != null && (explore.Count + combat.Count > 0 || area != null) && p != null;
                MuteValheim(on || CutsceneTrack.MusicActive);
                if (!on || CutsceneTrack.MusicActive) { Fade(null); return; }        // Cutscene bringt ihre eigene Musik mit
                if (InCombat(p)) combatUntil = Time.time + 8f;
                string want = Time.time < combatUntil && combat.Count > 0 ? "combat" : area != null ? "area:" + area : "explore";
                var src = useA ? a : b;
                bool ended = cur != null && !src.isPlaying && src.time == 0f;
                if (want != mode && cur != null && cur == area && want.StartsWith("area:")) mode = want;   // laeuft schon (z. B. Wald-Musik)
                if (want != mode || ended && Time.time > nextT)
                {
                    if (want != mode || ended)
                    {
                        mode = want;
                        var list = want == "combat" ? combat : explore;
                        string file;
                        if (want.StartsWith("area:")) file = area;
                        else if (list.Count == 0) return;
                        else file = want == "combat" ? list[ci++ % list.Count] : list[ei++ % list.Count];
                        StartCoroutine(PlayFile(file));
                        nextT = Time.time + 2f;
                    }
                }
                // Lautstaerke: aktive Quelle hoch, andere runter
                float k = Mathf.Clamp01((Time.time - fadeT) / 2.5f);
                var on1 = useA ? a : b; var off1 = useA ? b : a;
                float master = AudioListener.volume > 0f ? 1f : 0f;
                float duck = DuckFactor();
                on1.volume = volume * k * MusicVolume() * master * duck; off1.volume = volume * (1f - k) * MusicVolume() * master * duck;
                if (k >= 1f && off1.isPlaying) off1.Stop();
            }
            catch (Exception e) { Plugin.Quiet(e, "Music"); }
        }

        IEnumerator PlayFile(string file)
        {
            AudioClip clip;
            if (!cache.TryGetValue(file, out clip))
            {
                if (!File.Exists(file)) { Plugin.Warn("Musik fehlt: " + file); yield break; }
                var type = file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? AudioType.MPEG : file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.OGGVORBIS;
                using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + file.Replace('\\', '/'), type))
                {
                    ((DownloadHandlerAudioClip)rq.downloadHandler).streamAudio = true;
                    yield return rq.SendWebRequest();
                    if (rq.result != UnityWebRequest.Result.Success) { Plugin.Warn("Musik: " + rq.error); yield break; }
                    clip = DownloadHandlerAudioClip.GetContent(rq);
                    cache[file] = clip;
                }
            }
            var old = useA ? a : b;
            if (cur != null && old.clip != null && old.loop && old.isPlaying) resumeAt[cur] = old.time;
            useA = !useA;
            var src = useA ? a : b;
            bool loop = mode != null && mode.StartsWith("area:") || loopOne && mode == "explore" && explore.Count == 1;   // "loop": Titel nahtlos wiederholen (OoT-Musik)
            float at; if (!loop || !resumeAt.TryGetValue(file, out at) || at >= clip.length) at = 0f;
            src.clip = clip; src.loop = loop; src.Play(); src.time = at;
            cur = file; fadeT = Time.time;
            Plugin.Log("Musik: " + Path.GetFileName(file));
        }

        void Fade(string x) { if (a != null) { a.volume = Mathf.MoveTowards(a.volume, 0f, Time.deltaTime); b.volume = Mathf.MoveTowards(b.volume, 0f, Time.deltaTime); } cur = null; mode = null; }

        static float MusicVolume()
        {
            try { return Mathf.Clamp01(PlayerPrefs.GetFloat("MusicVolume", 1f)); } catch { return 1f; }
        }

        static System.Reflection.FieldInfo musicSrc;
        static void MuteValheim(bool mute)
        {
            if (MusicMan.instance == null) return;
            if (musicSrc == null) musicSrc = AccessTools.Field(typeof(MusicMan), "m_musicSource");
            var s = musicSrc != null ? musicSrc.GetValue(MusicMan.instance) as AudioSource : null;
            if (s != null && mute) s.volume = 0f;
        }

        static float checkT; static bool lastCombat;
        static bool InCombat(Player p)
        {
            if (Time.time < checkT) return lastCombat;
            checkT = Time.time + 0.5f; lastCombat = false;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed()) continue;
                if ((c.transform.position - p.transform.position).sqrMagnitude > 30f * 30f) continue;
                var ai = c.GetComponent<MonsterAI>();
                if (ai != null && ai.GetTargetCreature() == p) { lastCombat = true; break; }
            }
            return lastCombat;
        }
    }
}
