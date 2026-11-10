using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GameImporter
{
    /// <summary>
    /// Original-Soundeffekte aus Ocarina of Time (aus der SFX-Sequenz des ROMs gerendert, tools/oot/oot_sfx.py).
    /// pack.json "oot_sfx": { "clips": { "NA_SE_IT_SWORD_SWING": ["sfx/....ogg", ...] }, "volume": 0.8 }
    /// Play("NA_SE_...") spielt eine (zufaellige) Variante; mit Position als 3D-Klang in der Welt.
    /// Mischung (Moritz: Link hallt zu sehr, Schritte zu leise): Link/Menue (ohne Position) fast ohne Raumhall, Welt-Geraeusche
    /// mit etwas Hall; Lautstaerke je Effekt-Praefix ("mix") und Verstaerkung zu leiser Dateien beim Laden ("gain").
    /// pack.json "oot_sfx": {"mix": {"NA_SE_PL_WALK_": 2.0}, "gain": {"NA_SE_PL_WALK_GROUND": 3.0}, "reverb_link": 0.08, "reverb_world": 0.55}
    /// </summary>
    public class OotSfx : MonoBehaviour
    {
        public static OotSfx I;
        Pack forPack;
        readonly Dictionary<string, List<AudioClip>> clips = new Dictionary<string, List<AudioClip>>();
        readonly Dictionary<string, string> last = new Dictionary<string, string>();
        readonly List<AudioSource> pool = new List<AudioSource>();
        float volume = 0.8f, revLink = 0.08f, revWorld = 0.55f;
        // Lautstaerke je Effekt (Faktor auf den Aufruf; laengster passender Praefix gewinnt)
        readonly Dictionary<string, float> mix = new Dictionary<string, float>
        {
            { "NA_SE_PL_WALK_", 2.0f },          // Schritte: OotPlay ruft mit 0,5 -> 1,0
            { "NA_SE_VO_LI_", 0.7f },            // Links Stimme (Schlag, Sprung, Klettern, Schaden)
            { "NA_SE_PL_ROLL", 0.7f },
            { "NA_SE_PL_LAND", 1.4f },
            { "NA_SE_PL_JUMP", 1.3f },
            { "NA_SE_PL_CLIMB_CLIFF", 1.2f },
        };
        // Verstaerkung beim Laden (Datei zu leise, AudioSource.volume geht nicht ueber 1)
        readonly Dictionary<string, float> gain = new Dictionary<string, float>
        {
            { "NA_SE_PL_WALK_GROUND", 3.0f },    // ~1/3 so laut wie die Gras-Schritte
            { "NA_SE_PL_CLIMB_CLIFF", 2.5f },
        };
        static readonly System.Random rnd = new System.Random();

        public static void Ensure(GameObject host) { if (I == null) I = host.AddComponent<OotSfx>(); }

        void Update()
        {
            try { if (Pack.Current != forPack) Setup(Pack.Current); }
            catch (Exception e) { Plugin.Quiet(e, "OotSfx"); }
        }

        void Setup(Pack pk)
        {
            forPack = pk; clips.Clear();
            if (pk == null) return;
            object meta = null;
            try { meta = Json.Parse(File.ReadAllText(Path.Combine(pk.Dir, "pack.json"))); } catch { }
            var m = Json.V(meta, "oot_sfx");
            if (m == null) return;
            volume = Json.F(m, "volume", 0.8f);
            revLink = Json.F(m, "reverb_link", 0.08f); revWorld = Json.F(m, "reverb_world", 0.55f);
            var mj = Json.V(m, "mix") as Dictionary<string, object>;
            if (mj != null) foreach (var kv in mj) mix[kv.Key] = Convert.ToSingle(kv.Value);
            var gj = Json.V(m, "gain") as Dictionary<string, object>;
            if (gj != null) foreach (var kv in gj) gain[kv.Key] = Convert.ToSingle(kv.Value);
            var c = Json.V(m, "clips") as Dictionary<string, object>;
            if (c == null) return;
            StartCoroutine(LoadAll(pk, c));
        }

        IEnumerator LoadAll(Pack pk, Dictionary<string, object> c)
        {
            int n = 0;
            foreach (var kv in c)
            {
                var list = new List<AudioClip>();
                foreach (var o in (kv.Value as List<object>) ?? new List<object>())
                {
                    string file = Path.Combine(pk.Dir, ((string)o).Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(file)) continue;
                    using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + file.Replace('\\', '/'), AudioType.OGGVORBIS))
                    {
                        ((DownloadHandlerAudioClip)rq.downloadHandler).streamAudio = false;
                        yield return rq.SendWebRequest();
                        if (rq.result != UnityWebRequest.Result.Success) { Plugin.Warn("OoT-Sound: " + rq.error); continue; }
                        var clip = DownloadHandlerAudioClip.GetContent(rq);
                        float g;
                        if (gain.TryGetValue(kv.Key, out g) && Mathf.Abs(g - 1f) > 0.01f) clip = Amplify(clip, g);
                        clip.name = kv.Key;
                        list.Add(clip); n++;
                    }
                }
                if (list.Count > 0) clips[kv.Key] = list;
            }
            Plugin.Log("OoT-Sounds geladen: " + n + " Dateien, " + clips.Count + " Effekte");
        }

        static AudioClip Amplify(AudioClip c, float g)
        {
            try
            {
                var d = new float[c.samples * c.channels];
                if (!c.GetData(d, 0)) return c;
                for (int i = 0; i < d.Length; i++) d[i] = Mathf.Clamp(d[i] * g, -1f, 1f);
                var n = AudioClip.Create(c.name, c.samples, c.channels, c.frequency, false);
                n.SetData(d, 0);
                return n;
            }
            catch (Exception e) { Plugin.Quiet(e, "OotSfx.Gain"); return c; }
        }

        float Mix(string name)
        {
            float f = 1f; int best = -1;
            foreach (var kv in mix) if (kv.Key.Length > best && name.StartsWith(kv.Key, StringComparison.Ordinal)) { best = kv.Key.Length; f = kv.Value; }
            return f;
        }

        AudioSource Source()
        {
            foreach (var s in pool) if (s != null && !s.isPlaying) return s;
            if (pool.Count >= 24) return pool[rnd.Next(pool.Count)];
            var go = new GameObject("OotSfx");
            go.transform.SetParent(transform, false);
            var a = go.AddComponent<AudioSource>();
            a.playOnAwake = false; a.rolloffMode = AudioRolloffMode.Linear; a.minDistance = 4f; a.maxDistance = 40f; a.dopplerLevel = 0f;
            pool.Add(a);
            return a;
        }

        static float SfxVolume()
        {
            try { return Mathf.Clamp01(PlayerPrefs.GetFloat("SfxVolume", 1f)); } catch { return 1f; }
        }

        /// <summary>Spielt einen OoT-Effekt. pos == null: direkt (2D, z.B. Link, Menue); sonst 3D in der Welt.</summary>
        public static AudioSource Play(string name, float vol = 1f, Vector3? pos = null, float pitchVar = 0f)
        {
            try
            {
                if (I == null) return null;
                List<AudioClip> list;
                if (!I.clips.TryGetValue(name, out list) || list.Count == 0) return null;
                // Varianten wie im Spiel: nicht zweimal hintereinander dieselbe
                AudioClip clip = list[rnd.Next(list.Count)];
                string prev;
                if (list.Count > 1 && I.last.TryGetValue(name, out prev) && prev == clip.GetInstanceID().ToString()) clip = list[(list.IndexOf(clip) + 1) % list.Count];
                I.last[name] = clip.GetInstanceID().ToString();
                var a = I.Source();
                a.clip = clip; a.loop = false;
                a.volume = Mathf.Clamp01(vol * I.Mix(name) * I.volume * SfxVolume());
                a.reverbZoneMix = pos.HasValue ? I.revWorld : I.revLink;
                a.pitch = 1f + (pitchVar > 0f ? (float)(rnd.NextDouble() * 2 - 1) * pitchVar : 0f);
                if (pos.HasValue) { a.spatialBlend = 1f; a.transform.position = pos.Value; }
                else { a.spatialBlend = 0f; a.transform.localPosition = Vector3.zero; }
                a.Play();
                return a;
            }
            catch (Exception e) { Plugin.Quiet(e, "OotSfx.Play"); return null; }
        }

        public static void PlayLater(string name, float delay, float vol = 1f, Vector3? pos = null)
        {
            if (I != null) I.StartCoroutine(I.Later(name, delay, vol, pos));
        }

        IEnumerator Later(string name, float delay, float vol, Vector3? pos)
        {
            yield return new WaitForSeconds(delay);
            Play(name, vol, pos);
        }

        public static bool Has(string name) { return I != null && I.clips.ContainsKey(name); }
        public static AudioClip Clip(string name) { List<AudioClip> l; return I != null && I.clips.TryGetValue(name, out l) && l.Count > 0 ? l[0] : null; }
    }
}
