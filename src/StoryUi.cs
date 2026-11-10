using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace GameImporter
{
    // Dialoge, Quest-Anzeige, Tagebuch (J) und Cutscenes. Bedienung im Dialog: W/S oder Pfeile waehlen,
    // E / Leertaste / Enter bestaetigen, 1-9 direkt waehlen. Waehrend Dialog und Cutscene laeuft der Spieler nicht.
    public class StoryUi : MonoBehaviour
    {
        public static StoryUi I;
        public static bool Busy { get { return I != null && (I.dlg != null || I.cut != null); } }
        public static string CutId { get { return I != null && I.cut != null ? I.cutId : null; } }

        // ---------- Dialog ----------
        object dlg; string dlgId, npc, node; object nodeObj;
        readonly List<object> choices = new List<object>();
        int sel; float openT;
        Dictionary<string, string> questsBefore;
        bool dlgCam; Vector3 dlgCamPos, dlgCamDir; float dlgCamFov = 40f;
        object shownObj;      // letzte Zeile mit Text (bleibt bei reinen Auswahl-Knoten stehen)
        bool journal;

        void Awake() { I = this; }

        public static bool StartDialogue(string npcId)
        {
            var pk = Pack.Current; Pack.Npc n;
            if (I == null || pk == null || !pk.NpcById.TryGetValue(npcId, out n) || string.IsNullOrEmpty(n.Dialogue)) return false;
            object d;
            if (!pk.Dialogues.TryGetValue(n.Dialogue, out d)) { Plugin.Warn("Dialog " + n.Dialogue + " fehlt"); return false; }
            Story.EnsureLoaded();
            string start = null;
            var st = Json.V(d, "start");
            if (st is string) start = (string)st;
            else if (st is List<object>) foreach (var e in (List<object>)st) if (Story.Check(Json.L(e, "if"))) { start = Json.S(e, "node"); break; }
            if (start == null) return false;
            I.dlg = d; I.dlgId = n.Dialogue; I.npc = npcId; I.openT = Time.unscaledTime; I.dlgCam = false; I.shownObj = null;
            PlaceForDialogue(npcId, Json.L(d, "player_slot"));
            I.questsBefore = new Dictionary<string, string>(Story.Quests);
            I.Enter(start);
            return true;
        }

        void Enter(string id)
        {
            var nodes = Json.O(dlg, "nodes");
            object o = null;
            // Verzweigungen ({"branch": [{"if": [...], "node": "x"}, ...]}) und reine Aktionsknoten ({"pass": true, "do": [...], "next": "y"}) gleich durchlaufen
            for (int hop = 0; ; hop++)
            {
                if (nodes == null || id == null || !nodes.TryGetValue(id, out o) || hop > 40) { if (id != null) Plugin.Warn("Dialog " + dlgId + ": Knoten " + id + " fehlt"); Close(); return; }
                var br = Json.L(o, "branch");
                if (br != null)
                {
                    string to = null; bool hit = false;
                    foreach (var e in br) if (Story.Check(Json.L(e, "if"))) { to = Json.S(e, "node"); hit = true; break; }
                    if (!hit || to == null) { Close(); return; }
                    id = to; continue;
                }
                if (Json.B(o, "pass"))
                {
                    Story.Do(Json.L(o, "do"));
                    if (dlg == null) return;
                    id = Json.S(o, "next");
                    if (id == null) { Close(); return; }
                    continue;
                }
                break;
            }
            if (!string.IsNullOrEmpty(Pack.T(Json.V(o, "text")))) shownObj = o;
            node = id; nodeObj = o; sel = 0; choices.Clear();
            var cam = Json.O(o, "cam");
            if (cam != null)
            {
                var cp = Json.L(cam, "pos"); var cd = Json.L(cam, "dir");
                if (cp != null && cd != null && cp.Count == 3 && cd.Count == 3)
                {
                    dlgCamPos = new Vector3((float)(double)cp[0], (float)(double)cp[1], (float)(double)cp[2]);
                    dlgCamDir = new Vector3((float)(double)cd[0], (float)(double)cd[1], (float)(double)cd[2]);
                    dlgCamFov = Json.F(cam, "fov", 40f); dlgCam = true;
                }
            }
            var cl = Json.L(o, "choices");
            if (cl != null) foreach (var c in cl) if (Story.Check(Json.L(c, "if"))) choices.Add(c);
            Story.Do(Json.L(o, "do"));
            if (dlg == null) return;                  // eine Aktion hat den Dialog beendet (z. B. Cutscene)
            Voice(Json.V(o, "voice"), Json.S(o, "speaker"));
            var sp = Json.S(o, "speaker");
            if (sp != null && sp != "player") Npcs.Say(sp, Pack.T(Json.V(o, "text")));
        }

        void Confirm()
        {
            if (dlg == null) return;
            if (choices.Count > 0)
            {
                var c = choices[Mathf.Clamp(sel, 0, choices.Count - 1)];
                Story.Do(Json.L(c, "do"));
                if (dlg == null) return;
                var nx = Json.S(c, "next");
                if (nx != null) Enter(nx); else Close();
                return;
            }
            var next = Json.S(nodeObj, "next");
            if (next != null && !Json.B(nodeObj, "end")) Enter(next); else Close();
        }

        public void Close()
        {
            if (dlg == null) return;
            var n = npc; var before = questsBefore;
            dlg = null; nodeObj = null; choices.Clear(); dlgCam = false;
            if (voice != null) voice.Stop();
            Story.OnTalked(n, before ?? new Dictionary<string, string>());
            if (pendingCut != null) { var c = pendingCut; pendingCut = null; PlayCutscene(c); }
        }

        public static string Debug()
        {
            if (I == null || I.dlg == null) return "dialog: zu" + (I != null && I.cut != null ? " | cutscene " + I.cutId + " shot " + I.shot + (I.loading ? " laedt" : "") + (I.track != null ? " t=" + I.track.T.ToString("F1") + (I.track.Started ? "" : " (nicht gestartet)") : "") : "");
            var s = "dialog " + I.dlgId + "." + I.node + " [" + I.npc + "]: " + Pack.T(Json.V(I.nodeObj, "text"));
            for (int i = 0; i < I.choices.Count; i++) s += " | " + (i + 1) + (i == I.sel ? "*" : "") + ") " + Pack.T(Json.V(I.choices[i], "text"));
            return s;
        }

        public static void Pick(int i) { if (I == null || I.dlg == null) return; if (I.choices.Count > 0) I.sel = Mathf.Clamp(i, 0, I.choices.Count - 1); I.Confirm(); }

        // ---------- Sprachausgabe ----------
        AudioSource voice;
        // Cutscene-Zeilen duerfen sich ueberlappen wie im Original (z. B. Geralt brummt in Yennefers Satz): eigene Quelle je Zeile
        public float CutVoice(object fileObj) { Voice(fileObj, null, true); return -1f; }
        void Voice(object fileObj, string speaker, bool overlap = false)
        {
            var file = Pack.T(fileObj);
            if (string.IsNullOrEmpty(file) || Pack.Current == null) return;
            var path = Path.Combine(Pack.Current.Dir, file);
            if (!File.Exists(path)) return;
            StartCoroutine(PlayVoice(path, speaker, overlap));
        }

        AudioSource cutVoice;
        System.Collections.IEnumerator PlayVoice(string path, string speaker, bool overlap = false)
        {
            var type = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.OGGVORBIS;
            using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), type))
            {
                yield return rq.SendWebRequest();
                if (rq.result != UnityWebRequest.Result.Success) { Plugin.Quiet(new Exception(rq.error), "Voice " + path); yield break; }
                var clip = DownloadHandlerAudioClip.GetContent(rq);
                if (overlap)
                {
                    if (cutVoice == null) { var hc = new GameObject("gi_cutvoice"); DontDestroyOnLoad(hc); cutVoice = hc.AddComponent<AudioSource>(); cutVoice.spatialBlend = 0f; cutVoice.priority = 0; cutVoice.bypassReverbZones = true; }
                    cutVoice.PlayOneShot(clip);
                    yield break;
                }
                var go = Npcs.Get(speaker);
                if (voice == null) { var h = new GameObject("gi_voice"); DontDestroyOnLoad(h); voice = h.AddComponent<AudioSource>(); }
                voice.transform.position = go != null ? go.transform.position + Vector3.up * 1.7f : (Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero);
                voice.spatialBlend = go != null ? 0.8f : 0f; voice.minDistance = 4f; voice.maxDistance = 40f;
                voice.Stop(); voice.clip = clip; voice.Play();
                if (go != null) StartCoroutine(TalkAnim(go, speaker, clip.length));
            }
        }

        // Originalfiguren gestikulieren beim Sprechen (Clips "talk1".."talk3" aus der .glb), danach wieder Stehen
        System.Collections.IEnumerator TalkAnim(GameObject go, string id, float len)
        {
            var an = go.GetComponent<Animation>();
            if (an == null)
            {
                // Valheim-Charakter: gelegentlich eine Valheim-Geste waehrend er spricht
                var anim = go.GetComponentInChildren<Animator>();
                if (anim == null) yield break;
                var gestures = new[] { "point", "shrug", "comehere", "nonono", "thumbsup" };
                float end2 = Time.time + len;
                yield return new WaitForSeconds(0.6f);
                while (Time.time < end2 - 2f && go != null && anim != null)
                {
                    if (UnityEngine.Random.value < 0.55f) { try { anim.SetTrigger("emote_" + gestures[UnityEngine.Random.Range(0, gestures.Length)]); } catch { } }
                    yield return new WaitForSeconds(UnityEngine.Random.Range(2.8f, 4.5f));
                }
                if (anim != null) { try { anim.SetTrigger("emote_stop"); } catch { } }
                yield break;
            }
            var talks = new List<string>();
            for (int i = 1; i <= 6; i++) if (an["talk" + i] != null) talks.Add("talk" + i);
            if (talks.Count == 0) yield break;
            float end = Time.time + len;
            while (Time.time < end - 0.3f && go != null)
            {
                var c = talks[UnityEngine.Random.Range(0, talks.Count)];
                Npcs.Play(go, c, false, 0.3f);
                yield return new WaitForSeconds(Mathf.Min(an[c].length * 0.9f, Mathf.Max(0.5f, end - Time.time)));
            }
            if (go != null)
            {
                Pack.Npc n; string idle = Pack.Current != null && Pack.Current.NpcById.TryGetValue(id ?? "", out n) ? n.Anim : null;
                Npcs.Play(go, idle, true, 0.4f);
            }
        }

        // ---------- Kommentare ----------
        // Zeilen eines Dialogs nacheinander als Untertitel mit Stimme, waehrend man weiterspielt (Geralt denkt laut, Zurufe)
        Coroutine commentCo; string subSpeaker, subText; AudioSource commentVoice;
        public static void PlayComment(string id)
        {
            object d;
            if (I == null || Pack.Current == null || id == null || !Pack.Current.Dialogues.TryGetValue(id, out d)) { Plugin.Warn("Kommentar " + id + " fehlt"); return; }
            if (I.commentCo != null) I.StopCoroutine(I.commentCo);
            if (I.commentVoice != null) I.commentVoice.Stop();
            I.commentCo = I.StartCoroutine(I.CommentRun(d));
        }

        System.Collections.IEnumerator CommentRun(object d)
        {
            string id = null;
            var st = Json.V(d, "start");
            if (st is string) id = (string)st;
            else if (st is List<object>) foreach (var e in (List<object>)st) if (Story.Check(Json.L(e, "if"))) { id = Json.S(e, "node"); break; }
            var nodes = Json.O(d, "nodes");
            for (int hop = 0; id != null && nodes != null && hop < 80; hop++)
            {
                object o; if (!nodes.TryGetValue(id, out o)) break;
                var br = Json.L(o, "branch");
                if (br != null) { string to = null; foreach (var e in br) if (Story.Check(Json.L(e, "if"))) { to = Json.S(e, "node"); break; } id = to; continue; }
                Story.Do(Json.L(o, "do"));
                var txt = Pack.T(Json.V(o, "text"));
                if (!string.IsNullOrEmpty(txt))
                {
                    subSpeaker = Json.S(o, "speaker"); subText = txt;
                    float dur = Mathf.Clamp(txt.Length * 0.065f, 1.8f, 9f);
                    var vf = Pack.T(Json.V(o, "voice"));
                    if (!string.IsNullOrEmpty(vf) && Pack.Current != null)
                    {
                        var path = Path.Combine(Pack.Current.Dir, vf);
                        if (File.Exists(path))
                            using (var rq = UnityWebRequestMultimedia.GetAudioClip("file:///" + path.Replace('\\', '/'), path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? AudioType.WAV : AudioType.OGGVORBIS))
                            {
                                yield return rq.SendWebRequest();
                                if (rq.result == UnityWebRequest.Result.Success)
                                {
                                    var clip = DownloadHandlerAudioClip.GetContent(rq);
                                    if (commentVoice == null) { var h = new GameObject("gi_comment"); DontDestroyOnLoad(h); commentVoice = h.AddComponent<AudioSource>(); commentVoice.spatialBlend = 0f; commentVoice.priority = 0; }
                                    commentVoice.Stop(); commentVoice.clip = clip; commentVoice.Play();
                                    dur = clip.length + 0.25f;
                                }
                            }
                    }
                    float end = Time.time + dur;
                    while (Time.time < end && !Busy) yield return null;
                    if (Busy) break;
                }
                if (Json.B(o, "end")) break;
                string nx = Json.S(o, "next");
                var cl = Json.L(o, "choices");
                if (nx == null && cl != null) foreach (var c in cl) if (Story.Check(Json.L(c, "if"))) { Story.Do(Json.L(c, "do")); nx = Json.S(c, "next"); break; }
                id = nx;
            }
            subText = null; commentCo = null;
        }

        GUIStyle subSt, subShadow;
        void DrawSubtitle(float k, float sw, float sh)
        {
            if (subSt == null || subSt.fontSize != (int)(24 * k))
            {
                subSt = new GUIStyle { fontSize = (int)(24 * k), alignment = TextAnchor.LowerCenter, wordWrap = true, richText = true, normal = { textColor = new Color(0.97f, 0.95f, 0.9f) } };
                subShadow = new GUIStyle(subSt) { richText = false, normal = { textColor = new Color(0f, 0f, 0f, 0.9f) } };
            }
            string who = Pack.Current.NpcName(subSpeaker);
            var r = new Rect(sw * 0.18f, sh * 0.80f, sw * 0.64f, sh * 0.12f);
            GUI.Label(new Rect(r.x + 2, r.y + 2, r.width, r.height), (string.IsNullOrEmpty(who) ? "" : who + ": ") + subText, subShadow);
            GUI.Label(r, (string.IsNullOrEmpty(who) ? "" : "<color=#e8c06a>" + who + ":</color> ") + subText, subSt);
        }

        // ---------- Cutscenes ----------
        object cut; string cutId, pendingCut; int shot; float shotT;
        Vector3 camFrom, camTo, lookAt; float fov;

        public static void PlayCutscene(string id)
        {
            object c;
            if (I == null || Pack.Current == null || !Pack.Current.Cutscenes.TryGetValue(id, out c)) { Plugin.Warn("Cutscene " + id + " fehlt"); return; }
            if (I.dlg != null) { I.pendingCut = id; return; }      // erst nach dem Gespraech
            Story.SeenCutscenes.Add(id); Story.Save();
            I.cut = c; I.cutId = id;
            // erst alles um die Kamerapunkte bauen lassen (schwarzes Bild), dann los
            Statics.Extra.Clear();
            var shots = Json.L(c, "shots"); var pk = Pack.Current;
            if (shots != null) foreach (var s in shots)
                {
                    Vector3 v;
                    if (pk.Pos(Json.V(Json.O(s, "cam"), "pos"), out v)) Statics.Extra.Add(v);
                    if (Json.O(s, "cam_to") != null && pk.Pos(Json.V(Json.O(s, "cam_to"), "pos"), out v)) Statics.Extra.Add(v);
                }
            I.loading = true; I.loadT = Time.unscaledTime; I.shot = 0;
            I.track = null;
            var tp = Json.S(c, "track");
            if (!string.IsNullOrEmpty(tp))
            {
                try
                {
                    I.track = CutsceneTrack.Load(Path.Combine(pk.Dir, tp.Replace('/', Path.DirectorySeparatorChar)));
                    I.track.CaptureHome();
                    Statics.Extra.AddRange(I.track.Preload());
                    I.StartCoroutine(I.track.LoadAudio());
                    I.camFrom = I.camTo = I.track.FirstCam; I.lookAt = I.camFrom + I.track.FirstRot * Vector3.forward * 5f; I.fov = 40f;
                }
                catch (Exception e) { Plugin.Warn("Cutscene-Spur " + tp + ": " + e.Message); I.cut = null; I.loading = false; Statics.Extra.Clear(); return; }
            }
            else I.PrepShot(0);
        }

        public CutsceneTrack track; float camNear0 = -1f;

        bool loading; float loadT;
        // Laden fertig? Liste muss nach dem Setzen der Punkte neu gebaut sein und nichts mehr warten (max. 15 s)
        void TickLoading()
        {
            if (!loading) return;
            if (track != null && track.Follow) CutsceneTrack.PlaceUnder(track.FirstCam);
            float t = Time.unscaledTime - loadT;
            if (Intro.Loading()) { loadT = Time.unscaledTime; return; }                    // Valheim laedt noch selbst
            bool ready = t > 1.0f && Statics.QueueBuiltT > 0f && Time.time - Statics.QueueBuiltT < 0.5f && Statics.Pending == 0 && Glb.PrefetchCount == 0
                         && (track == null || track.AudioReady);
            if ((ready && t > 1.5f) || t > 15f) { loading = false; if (track != null) { if (Camera.main != null) camNear0 = Camera.main.nearClipPlane; track.Start(); } else StartShot(0); Plugin.Log("Cutscene " + cutId + ": geladen nach " + t.ToString("F1") + " s" + (ready ? "" : " (Zeitlimit)")); }
        }

        void StartShot(int i)
        {
            var shots = Json.L(cut, "shots");
            if (shots == null || i >= shots.Count) { EndCut(); return; }
            PrepShot(i);
            var s = shots[i];
            var ln = Json.O(s, "line");
            if (ln != null) { Voice(Json.V(ln, "voice"), Json.S(ln, "speaker")); }
            var anims = Json.L(s, "anims");
            if (anims != null) foreach (var a in anims) Npcs.Play(Npcs.Get(Json.S(a, "npc")), Json.S(a, "clip"), Json.B(a, "loop"));
        }

        // Kamera eines Shots berechnen (auch schon waehrend des Ladens, damit dort gebaut wird)
        void PrepShot(int i)
        {
            var shots = Json.L(cut, "shots");
            if (shots == null || i >= shots.Count) return;
            shot = i; shotT = Time.unscaledTime;
            var s = shots[i]; var pk = Pack.Current;
            Vector3 p;
            pk.Pos(Json.V(Json.O(s, "cam"), "pos"), out camFrom);
            pk.Pos(Json.V(Json.O(s, "cam"), "look"), out lookAt);
            camTo = camFrom;
            if (Json.O(s, "cam_to") != null && pk.Pos(Json.V(Json.O(s, "cam_to"), "pos"), out p)) camTo = p;
            if (float.IsNaN(camFrom.y)) camFrom.y = Pack.Ground(camFrom.x, camFrom.z) + 3f;
            if (float.IsNaN(camTo.y)) camTo.y = Pack.Ground(camTo.x, camTo.z) + 3f;
            if (float.IsNaN(lookAt.y)) lookAt.y = Pack.Ground(lookAt.x, lookAt.z) + 1.5f;
            fov = Json.F(Json.O(s, "cam"), "fov", 55f);
        }

        public static void StopCut() { if (I != null && I.cut != null) I.EndCut(); }

        void EndCut()
        {
            var c = cut; cut = null; loading = false;
            if (cutVoice != null) cutVoice.Stop();
            Statics.Extra.Clear();
            if (track != null) { try { track.End(); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.End"); } track = null; if (camNear0 > 0f && Camera.main != null) Camera.main.nearClipPlane = camNear0; }
            if (c != null) Story.Do(Json.L(c, "then"));
        }

        float CutDur() { var s = Json.L(cut, "shots")[shot]; return Json.F(s, "dur", 3f); }

        // Spieler an den Platz aus dem Dialogset stellen (relativ zur Figur) und zur Figur drehen
        static void PlaceForDialogue(string npcId, List<object> slot)
        {
            var pl = Player.m_localPlayer; var go = Npcs.Get(npcId);
            if (pl == null || go == null || slot == null || slot.Count < 2) return;
            var t = go.transform;
            var off = new Vector3((float)(double)slot[0], 0f, (float)(double)slot[1]);
            if (off.magnitude > 4f) return;              // Dialogset-Platz passt nicht (Figur sitzt z. B. woanders): Spieler bleibt stehen
            var p = t.position + t.rotation * off;
            float g = Pack.Ground(p.x, p.z);
            p.y = Mathf.Abs(g - t.position.y) < 1.2f ? g + 0.05f : t.position.y + 0.05f;     // in Gebaeuden mit Stockwerken: Hoehe der Figur
            var look = t.position - p; look.y = 0f;
            try
            {
                var body = pl.GetComponent<Rigidbody>();
                if (body != null) { body.position = p; body.velocity = Vector3.zero; }
                pl.transform.position = p;
                if (look.sqrMagnitude > 0.01f)
                {
                    pl.transform.rotation = Quaternion.LookRotation(look);
                    var m = AccessTools.Method(typeof(Character), "SetLookDir");
                    if (m != null) { var a = new object[m.GetParameters().Length]; a[0] = look.normalized; for (int i = 1; i < a.Length; i++) a[i] = 0f; m.Invoke(pl, a); }
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "StoryUi.Place"); }
        }

        static float defNear = -1f;
        string camChk; float camChkT; bool camAlt; Vector3 camAltPos;
        static System.Reflection.MethodInfo inShelter;
        static bool Sheltered()
        {
            try
            {
                var pl = Player.m_localPlayer; if (pl == null) return false;
                if (inShelter == null) inShelter = AccessTools.Method(typeof(Player), "InShelter") ?? AccessTools.Method(typeof(Character), "InShelter");
                return inShelter != null && (bool)inShelter.Invoke(pl, null);
            }
            catch { return false; }
        }
        static int blockMask = -1;
        static bool Blocked(Vector3 a, Vector3 b)
        {
            if (blockMask == -1) blockMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small", "vehicle");
            RaycastHit hit;
            var dir = b - a; float len = dir.magnitude;
            if (len < 0.3f) return false;
            // erst 0.35 m vor der Figur aufhoeren (ihr eigener Koerper zaehlt nicht)
            return Physics.Raycast(a, dir / len, out hit, len - 0.35f, blockMask, QueryTriggerInteraction.Ignore);
        }
        static void CameraPost(GameCamera __instance)
        {
            if (Shots.Active) { try { if (Shots.ApplyCamera(__instance.GetComponent<Camera>())) return; } catch (Exception e) { Plugin.Quiet(e, "Shots.Camera"); } }
            if (I != null && I.cut == null && I.dlg != null && I.dlgCam)
            {
                var c = __instance.GetComponent<Camera>(); var go = Npcs.Get(I.npc);
                if (c != null && go != null)
                {
                    var tr = go.transform;
                    var head = tr.position + Vector3.up * 1.55f;
                    var want = tr.position + tr.rotation * I.dlgCamPos;
                    // Originalkamera verdeckt (Saeule, Wand, Dach)? Dann ueber Geralts Schulter auf die Figur
                    if (I.camChk != I.node + "|" + I.dlgCamPos || Time.unscaledTime > I.camChkT)
                    {
                        I.camChk = I.node + "|" + I.dlgCamPos; I.camChkT = Time.unscaledTime + 0.5f;
                        I.camAlt = false;
                        // Unter Dach (Innenraeume fehlen, enge Ecken) oder verdeckt -> Schulterkamera
                        if (Sheltered() || Blocked(want, head))
                        {
                            var pl = Player.m_localPlayer;
                            if (pl != null)
                            {
                                var ph = pl.transform.position + Vector3.up * 1.6f;
                                var d = head - ph; d.y = 0f; if (d.sqrMagnitude < 0.01f) d = -tr.forward; d.Normalize();
                                var right = Vector3.Cross(Vector3.up, d);
                                foreach (var side in new[] { 0.55f, -0.55f })
                                {
                                    var alt = ph - d * 1.2f + right * side + Vector3.up * 0.1f;
                                    if (!Blocked(alt, head)) { I.camAlt = true; I.camAltPos = alt; break; }
                                }
                            }
                        }
                    }
                    if (I.camAlt) { c.transform.position = I.camAltPos; c.transform.rotation = Quaternion.LookRotation(head - I.camAltPos); c.fieldOfView = 32f; }
                    else
                    {
                        c.transform.position = want;
                        c.transform.rotation = Quaternion.LookRotation(tr.rotation * I.dlgCamDir);
                        c.fieldOfView = I.dlgCamFov;
                    }
                    // Was naeher an der Kamera ist als die Figur (Geralts Schulter, Balken, Waende), wird weggeschnitten
                    if (defNear < 0f) defNear = c.nearClipPlane;
                    float dn = Vector3.Distance(c.transform.position, tr.position + Vector3.up * 1.5f);
                    c.nearClipPlane = Mathf.Clamp(dn - 0.9f, defNear, 1.4f);
                }
                return;
            }
            if (defNear > 0f)
            {
                var c0 = __instance.GetComponent<Camera>();
                if (c0 != null && c0.nearClipPlane != defNear) c0.nearClipPlane = defNear;
            }
            if (I == null || I.cut == null) { InteriorLight(); return; }
            var cam = __instance.GetComponent<Camera>(); if (cam == null) return;
            if (I.track != null && !I.loading) { I.track.ApplyCamera(cam); Shots.AfterCutCamera(cam); return; }
            if (I.loading) { cam.transform.position = I.camFrom; cam.transform.rotation = Quaternion.LookRotation(I.lookAt - I.camFrom); cam.fieldOfView = I.fov; return; }
            float t = Mathf.Clamp01((Time.unscaledTime - I.shotT) / Mathf.Max(0.1f, I.CutDur()));
            t = t * t * (3f - 2f * t);
            var pos = Vector3.Lerp(I.camFrom, I.camTo, t);
            cam.transform.position = pos;
            cam.transform.rotation = Quaternion.LookRotation(I.lookAt - pos);
            cam.fieldOfView = I.fov;
        }

        // Innenraeume: Valheim beleuchtet sie mit dem Himmelslicht (blau, hell). Unter Dach wie im Original warm und gedaempft
        // (pack.json "interior_light"), weich ein- und ausgeblendet
        static float inK, shelterChkT; static bool shelterNow;
        static void InteriorLight()
        {
            var pk = Pack.Current;
            if (pk == null || !pk.InteriorLight.HasValue || OotPlay.AreaOwnsLight) return;
            if (Time.time > shelterChkT) { shelterChkT = Time.time + 0.25f; shelterNow = Sheltered(); }
            inK = Mathf.MoveTowards(inK, shelterNow ? 1f : 0f, Time.deltaTime * 1.2f);
            if (inK <= 0f) return;
            var warm = pk.InteriorLight.Value; float k = inK * inK * (3f - 2f * inK) * 0.75f;
            RenderSettings.ambientLight = Color.Lerp(RenderSettings.ambientLight, warm, k);
            RenderSettings.ambientSkyColor = Color.Lerp(RenderSettings.ambientSkyColor, warm, k);
            RenderSettings.ambientEquatorColor = Color.Lerp(RenderSettings.ambientEquatorColor, warm * 0.85f, k);
            RenderSettings.ambientGroundColor = Color.Lerp(RenderSettings.ambientGroundColor, warm * 0.6f, k);
            RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, warm * 0.5f, k);
        }

        // ---------- Eingabe ----------
        void Update()
        {
            try
            {
                if (Pack.Current != null && Player.m_localPlayer != null) { Story.Tick(); Npcs.Tick(); }
                else if (Pack.Current == null) { if (dlg != null) Close(); cut = null; Npcs.Tick(); }
                try { Statics.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Statics.Tick"); }
                try { Foliage.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Foliage.Tick"); }
                try { Horse.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Horse.Tick"); }
                try { Witcher.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Witcher.Tick"); }
                try { WitcherInv.Tick(); } catch (Exception e) { Plugin.Quiet(e, "WitcherInv.Tick"); }
                try { FastTravel.Tick(); } catch (Exception e) { Plugin.Quiet(e, "FastTravel.Tick"); }
                try { W3Controls.Tick(); } catch (Exception e) { Plugin.Quiet(e, "W3Controls.Tick"); }
                try { Intro.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Intro.Tick"); }
                try { SafeZones.Tick(); } catch (Exception e) { Plugin.Quiet(e, "SafeZones.Tick"); }
                try { Senses.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Senses.Tick"); }
                try { Interact.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Interact.Tick"); }
                try { Shots.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Shots.Tick"); }
                try { Grass.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Grass.Tick"); }
                try { Avatar.Tick(); } catch (Exception e) { Plugin.Quiet(e, "Avatar.Tick"); }
                try { OotPlay.Tick(); } catch (Exception e) { Plugin.Quiet(e, "OotPlay.Tick"); }
                try { CreatureSkins.Tick(); } catch (Exception e) { Plugin.Quiet(e, "CreatureSkins.Tick"); }

                if (cut != null && loading) { TickLoading(); return; }
                if (cut != null && track != null)
                {
                    if (!track.NoSkip && Input.GetKeyDown(KeyCode.Space) && track.T > 0.5f) { EndCut(); return; }        // ganze Cutscene ueberspringen
                    try { track.Tick(this); } catch (Exception e) { Plugin.Quiet(e, "Cutscene.Tick"); }
                    if (track != null && track.Done) { if (track.Loop) track.Restart(); else EndCut(); }
                    return;
                }
                if (cut != null)
                {
                    if (Input.GetKeyDown(KeyCode.Space) && Time.unscaledTime - shotT > 0.3f) StartShot(shot + 1);
                    else if (Time.unscaledTime - shotT > CutDur()) StartShot(shot + 1);
                    return;
                }
                if (dlg != null)
                {
                    var go = Npcs.Get(npc);
                    if (Player.m_localPlayer == null || (go != null && Vector3.Distance(go.transform.position, Player.m_localPlayer.transform.position) > 8f)) { Close(); return; }
                    if (Time.unscaledTime - openT < 0.3f) return;
                    int n = Mathf.Max(1, choices.Count);
                    if (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.UpArrow)) sel = (sel + n - 1) % n;
                    if (Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.DownArrow)) sel = (sel + 1) % n;
                    for (int i = 0; i < 9 && i < choices.Count; i++) if (Input.GetKeyDown(KeyCode.Alpha1 + i)) { sel = i; Confirm(); return; }
                    if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Confirm();
                    return;
                }
                if (Pack.Current != null && Player.m_localPlayer != null && Input.GetKeyDown(KeyCode.J) && (Chat.instance == null || !Chat.instance.HasFocus()) && !global::Console.IsVisible() && !TextInput.IsVisible() && !Menu.IsVisible())
                    journal = !journal;
                // F8: Cutscene des Pakets ansehen (pack.json "preview_cutscene", sonst die erste) - zum Anschauen ohne Konsole
                if (Pack.Current != null && Player.m_localPlayer != null && cut == null && Input.GetKeyDown(KeyCode.F8) && Pack.Current.Cutscenes.Count > 0)
                {
                    string id = Pack.Current.Meta != null ? Json.S(Pack.Current.Meta, "preview_cutscene", "") : "";
                    if (string.IsNullOrEmpty(id) || !Pack.Current.Cutscenes.ContainsKey(id)) foreach (var k in Pack.Current.Cutscenes.Keys) { id = k; break; }
                    PlayCutscene(id);
                }
            }
            catch (Exception e) { Plugin.Quiet(e, "StoryUi.Update"); }
        }

        static void TakeInputPost(ref bool __result) { if (Busy || WitcherInv.Open || FastTravel.Open || Shots.Active) __result = false; }

        public static void Patch(Harmony h)
        {
            try
            {
                foreach (var t in new[] { typeof(PlayerController), typeof(Player) })
                    foreach (var m in AccessTools.GetDeclaredMethods(t))
                        if (m.Name == "TakeInput" && m.ReturnType == typeof(bool)) h.Patch(m, postfix: new HarmonyMethod(typeof(StoryUi), nameof(TakeInputPost)));
                var cl = AccessTools.Method(typeof(GameCamera), "LateUpdate");
                if (cl != null) h.Patch(cl, postfix: new HarmonyMethod(typeof(StoryUi), nameof(CameraPost)) { priority = Priority.Last - 1 });
            }
            catch (Exception e) { Plugin.Warn("Story-UI-Patch: " + e.Message); }
        }

        // ---------- Zeichnen ----------
        GUIStyle box, nameSt, text, opt, optSel, small, title;
        Texture2D bg, bgSel, black;
        float lastScale;

        void Styles()
        {
            float k = Mathf.Max(0.75f, Screen.height / 1080f);
            if (box != null && Mathf.Approximately(k, lastScale)) return;
            lastScale = k;
            bg = Tex(new Color(0.06f, 0.05f, 0.04f, 0.82f)); bgSel = Tex(new Color(0.55f, 0.42f, 0.18f, 0.55f)); black = Tex(Color.black);
            box = new GUIStyle { normal = { background = bg }, padding = new RectOffset((int)(24 * k), (int)(24 * k), (int)(16 * k), (int)(16 * k)) };
            nameSt = new GUIStyle { fontSize = (int)(24 * k), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.95f, 0.78f, 0.4f) } };
            text = new GUIStyle { fontSize = (int)(23 * k), wordWrap = true, richText = true, normal = { textColor = new Color(0.96f, 0.94f, 0.9f) } };
            opt = new GUIStyle { fontSize = (int)(21 * k), wordWrap = true, padding = new RectOffset((int)(10 * k), (int)(10 * k), (int)(5 * k), (int)(5 * k)), normal = { textColor = new Color(0.85f, 0.82f, 0.76f) } };
            optSel = new GUIStyle(opt) { normal = { background = bgSel, textColor = Color.white } };
            small = new GUIStyle { fontSize = (int)(18 * k), wordWrap = true, richText = true, normal = { textColor = new Color(0.92f, 0.9f, 0.86f) } };
            title = new GUIStyle(small) { fontSize = (int)(20 * k), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.95f, 0.78f, 0.4f) } };
        }

        static Texture2D Tex(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.HideAndDontSave; return t; }

        void OnGUI()
        {
            try { Witcher.Gui(); } catch (Exception e) { Plugin.Quiet(e, "Witcher.Gui"); }
            try { Senses.Gui(); } catch (Exception e) { Plugin.Quiet(e, "Senses.Gui"); }
            try { WitcherInv.Gui(); } catch (Exception e) { Plugin.Quiet(e, "WitcherInv.Gui"); }
            try { FastTravel.Gui(); } catch (Exception e) { Plugin.Quiet(e, "FastTravel.Gui"); }
            try { W3Controls.Gui(); } catch (Exception e) { Plugin.Quiet(e, "W3Controls.Gui"); }
            try { Interact.Gui(); } catch (Exception e) { Plugin.Quiet(e, "Interact.Gui"); }
            try { OotHud.Gui(); } catch (Exception e) { Plugin.Quiet(e, "OotHud.Gui"); }
            try
            {
                if (Pack.Current == null || Player.m_localPlayer == null) return;
                Styles();
                float k = lastScale, sw = Screen.width, sh = Screen.height;
                if (cut != null) { DrawCut(k, sw, sh); return; }
                if (dlg != null) DrawDialogue(k, sw, sh);
                else if (subText != null) DrawSubtitle(k, sw, sh);
                if (journal) DrawJournal(k, sw, sh); else DrawTracker(k, sw);
            }
            catch (Exception e) { Plugin.Quiet(e, "StoryUi.OnGUI"); }
        }

        void DrawDialogue(float k, float sw, float sh)
        {
            float w = Mathf.Min(sw * 0.62f, 1100 * k), x = (sw - w) / 2f;
            var so = string.IsNullOrEmpty(Pack.T(Json.V(nodeObj, "text"))) && shownObj != null ? shownObj : nodeObj;
            var sp = Json.S(so, "speaker");
            var body = Pack.T(Json.V(so, "text"));
            float th = text.CalcHeight(new GUIContent(body), w - 48 * k);
            float ch = 0f;
            foreach (var c in choices) ch += opt.CalcHeight(new GUIContent("0. " + Pack.T(Json.V(c, "text"))), w - 48 * k) + 4 * k;
            float h = 32 * k + 34 * k + th + (choices.Count > 0 ? 14 * k + ch : 30 * k);
            var r = new Rect(x, sh - h - 70 * k, w, h);
            GUILayout.BeginArea(r, box);
            GUILayout.Label(Pack.Current.NpcName(sp), nameSt);
            GUILayout.Space(6 * k);
            GUILayout.Label(body, text);
            if (choices.Count > 0)
            {
                GUILayout.Space(12 * k);
                for (int i = 0; i < choices.Count; i++)
                    GUILayout.Label((i + 1) + ".  " + Pack.T(Json.V(choices[i], "text")), i == sel ? optSel : opt);
            }
            else
            {
                GUILayout.Space(8 * k);
                GUILayout.Label("<color=#b9a57a>[E] " + Story.L(Json.B(nodeObj, "end") || Json.S(nodeObj, "next") == null ? "Ende" : "Weiter", Json.B(nodeObj, "end") || Json.S(nodeObj, "next") == null ? "End" : "Continue") + "</color>", small);
            }
            GUILayout.EndArea();
        }

        void DrawTracker(float k, float sw)
        {
            var pk = Pack.Current;
            Pack.Quest q = null;
            if (Story.Tracked != null && pk.Quests.ContainsKey(Story.Tracked) && Story.Cond(Story.Tracked + "@active")) q = pk.Quests[Story.Tracked];
            if (q == null) foreach (var x in pk.QuestOrder) if (Story.Cond(x.Id + "@active")) { q = x; break; }
            if (q == null) return;
            var st = Story.Current(q);
            float w = 380 * k;
            var r = new Rect(sw - w - 24 * k, 220 * k, w, 200 * k);
            GUILayout.BeginArea(r);
            GUILayout.BeginVertical(box);
            GUILayout.Label(Pack.T(q.Title), title);
            if (st != null) GUILayout.Label(Pack.T(st.Log) + Story.Progress(q, st), small);
            GUILayout.Label("<color=#8a7f6a>[J] " + Story.L("Tagebuch", "Journal") + "</color>", small);
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        void DrawJournal(float k, float sw, float sh)
        {
            var pk = Pack.Current;
            float w = Mathf.Min(760 * k, sw * 0.6f), h = sh * 0.7f;
            GUILayout.BeginArea(new Rect((sw - w) / 2f, (sh - h) / 2f, w, h), box);
            GUILayout.Label(pk.Title + " – " + Story.L("Tagebuch", "Journal"), nameSt);
            GUILayout.Space(10 * k);
            bool any = false;
            foreach (var done in new[] { false, true })
                foreach (var q in pk.QuestOrder)
                {
                    if (!Story.Cond(q.Id + (done ? "@done" : "@active"))) continue;
                    any = true;
                    var st = Story.Current(q);
                    GUILayout.Label((done ? "<color=#7d7d7d>" : "") + Pack.T(q.Title) + (done ? " ✓</color>" : ""), title);
                    if (!done && st != null) GUILayout.Label(Pack.T(st.Log) + Story.Progress(q, st), small);
                    GUILayout.Space(8 * k);
                }
            if (!any) GUILayout.Label(Story.L("Noch keine Quests.", "No quests yet."), small);
            GUILayout.FlexibleSpace();
            GUILayout.Label("<color=#8a7f6a>[J] " + Story.L("Schließen", "Close") + "</color>", small);
            GUILayout.EndArea();
        }

        void DrawCut(float k, float sw, float sh)
        {
            if (track != null && !loading)
            {
                float barH = sh * 0.11f;
                if (!track.NoBars)
                {
                    GUI.DrawTexture(new Rect(0, 0, sw, barH), black);
                    GUI.DrawTexture(new Rect(0, sh - barH, sw, barH), black);
                }
                float bl = track.Black();
                if (bl > 0.001f) { var fc = track.FadeColor; GUI.color = new Color(fc.r, fc.g, fc.b, bl); GUI.DrawTexture(new Rect(0, 0, sw, sh), black); GUI.color = Color.white; }
                if (!string.IsNullOrEmpty(track.Subtitle))
                {
                    var st = new GUIStyle(text) { alignment = TextAnchor.MiddleCenter };
                    GUI.Label(new Rect(sw * 0.15f, sh - barH, sw * 0.7f, barH), track.Subtitle, st);
                }
                if (!track.NoSkip && !OotTitle.Active)          // Startbildschirm/Intro wie OoT ohne Hinweis (Leertaste geht trotzdem)
                    GUI.Label(new Rect(sw - 260 * k, 16 * k, 240 * k, 30 * k), "<color=#777777>[" + Story.L("Leertaste", "Space") + "] " + Story.L("überspringen", "skip") + "</color>", small);
                return;
            }
            if (loading)
            {
                GUI.DrawTexture(new Rect(0, 0, sw, sh), black);
                var ls = new GUIStyle(text) { alignment = TextAnchor.LowerRight };
                int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
                GUI.Label(new Rect(0, 0, sw - 40 * k, sh - 30 * k), "<color=#999999>" + Story.L("Lädt", "Loading") + new string('.', dots) + "</color>", ls);
                return;
            }
            float bar = sh * 0.11f;
            GUI.DrawTexture(new Rect(0, 0, sw, bar), black);
            GUI.DrawTexture(new Rect(0, sh - bar, sw, bar), black);
            var shotObj = Json.L(cut, "shots")[shot];
            var ttl = Pack.T(Json.V(shotObj, "title"));
            if (!string.IsNullOrEmpty(ttl))
            {
                // grosser Titel, weich ein- und ausgeblendet
                float t = Time.unscaledTime - shotT, d = CutDur();
                float a = Mathf.Clamp01(t / 1.2f) * Mathf.Clamp01((d - t) / 1.2f);
                var ts = new GUIStyle(text) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(86 * k), fontStyle = FontStyle.Bold };
                GUI.color = new Color(0f, 0f, 0f, a * 0.6f); GUI.Label(new Rect(3 * k, sh * 0.3f + 3 * k, sw, 120 * k), ttl, ts);
                GUI.color = new Color(0.95f, 0.84f, 0.6f, a); GUI.Label(new Rect(0, sh * 0.3f, sw, 120 * k), ttl, ts);
                GUI.color = Color.white;
            }
            var ln = Json.O(shotObj, "line");
            if (ln != null)
            {
                var spk = Json.S(ln, "speaker");
                var s = (string.IsNullOrEmpty(spk) ? "" : "<b><color=#f2c766>" + Pack.Current.NpcName(spk) + ":</color></b>  ") + Pack.T(Json.V(ln, "text"));
                var st = new GUIStyle(text) { alignment = TextAnchor.MiddleCenter };
                GUI.Label(new Rect(sw * 0.15f, sh - bar, sw * 0.7f, bar), s, st);
            }
            GUI.Label(new Rect(sw - 260 * k, 16 * k, 240 * k, 30 * k), "<color=#777777>[" + Story.L("Leertaste", "Space") + "] " + Story.L("weiter", "skip") + "</color>", small);
        }
    }
}
