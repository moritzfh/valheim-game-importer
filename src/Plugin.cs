using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Game Importer (privat): holt Welten anderer Spiele nach Valheim - Gelaende, Biome, Waelder und Gebaeude
    // aus einer .vmap/.sites-Datei, dazu aus einem Spielpaket (docs/PACK_FORMAT.md) NPCs, Dialoge, Quests und Cutscenes.
    // Eigenstaendig: laeuft mit oder ohne Valheim City Builder.
    [BepInPlugin(Guid, "Game Importer", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "rusty.gameimporter", Version = "0.2.0";
        static BepInEx.Logging.ManualLogSource log;
        public static ConfigEntry<string> MapWorlds;
        public static ConfigEntry<bool> MapFarView;
        public static ConfigEntry<float> MapFogMax;
        public static ConfigEntry<bool> SkipIntroOn, SkipValkyrie;

        void Awake()
        {
            log = Logger;
            // Valheim selbst beachtet -savedir nur auf dem Server. Fuer Test-Instanzen: eigener Speicherordner,
            // damit Test-Welten/-Charaktere nie im normalen Spielstand-Ordner landen.
            try
            {
                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i + 1 < args.Length; i++)
                    if (args[i] == "-savedir" && args[i + 1].Length > 0) { Utils.SetSaveDataPath(args[i + 1]); Logger.LogInfo("Speicherordner: " + args[i + 1]); }
            }
            catch (Exception e) { Logger.LogWarning("Speicherordner: " + e.Message); }
            MapWorlds = Config.Bind("Map", "MapWorlds", "", "Worlds that get their terrain from a map file in BepInEx/config/GameImporter/maps (e.g. MyWorld=mymap uses mymap.vmap). Worlds named map-<name> use <name>.vmap automatically. Only for NEW worlds!");
            MapFarView = Config.Bind("Map", "FarView", true, "Show the whole imported map up to the horizon (coarse far terrain, less fog, long view distance)");
            MapFogMax = Config.Bind("Map", "FogMax", 0.0012f, "Imported maps: upper limit for the fog density (Valheim's own fog hides everything beyond a few hundred meters)");
            SkipIntroOn = Config.Bind("General", "SkipIntro", true, "Skip Valheim's intro film and the long logo animation of the main menu");
            SkipValkyrie = Config.Bind("General", "SkipValkyrie", true, "New characters in imported game worlds start right away (no Valkyrie flight); the game's own opening plays instead");
            var h = new Harmony(Guid);
            MapImport.Patch(h); TerrainHoles.Patch(h); WaterFix.Patch(h); SkipIntro.Patch(h); Foliage.Patch(h);
            Story.Patch(h);
            StoryUi.Patch(h);
            Witcher.Patch(h); W3Controls.Patch(h); Intro.Patch(h); OotPlay.Patch(h); OotTitle.Patch(h);
            gameObject.AddComponent<Remote>();
            gameObject.AddComponent<StoryUi>();
            Music.Ensure(gameObject);
            OotSfx.Ensure(gameObject);
            OotTitle.Ensure(gameObject);
            Log("Game Importer " + Version + " geladen");
        }

        public static void Log(string s) { log?.LogInfo(s); }
        public static void Warn(string s) { log?.LogWarning(s); }
        public static void Quiet(Exception e, string where) { log?.LogDebug(where + ": " + e.Message); }
    }

    // Fernsteuerung fuer Tests: Befehle in BepInEx/config/GameImporter/cmd.txt, Ausgabe in log.txt
    public class Remote : MonoBehaviour
    {
        public static string Dir { get { return Path.Combine(Paths.ConfigPath, "GameImporter"); } }
        float t;
        static bool godWanted; static Player godFor;
        static GameObject poseGo;
        static readonly List<GameObject> lineup = new List<GameObject>();

        static float F(string s, float d) { float v; return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : d; }
        public static void Out(string s) { try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "log.txt"), DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\n"); } catch (Exception e) { Plugin.Quiet(e, "Remote.Out"); } }

        void Update()
        {
            Autostart.Tick();
            if (Time.unscaledTime < t) return;
            t = Time.unscaledTime + 0.5f;
            // god gilt auch nach Tod/Neuerscheinen weiter (neues Player-Objekt)
            if (godWanted && Player.m_localPlayer != null && Player.m_localPlayer != godFor)
            {
                var gm = HarmonyLib.AccessTools.Method(typeof(Player), "SetGodMode");
                if (gm != null) { gm.Invoke(Player.m_localPlayer, new object[] { true }); godFor = Player.m_localPlayer; }
            }
            AutoDumpVTex();
            var f = Path.Combine(Dir, "cmd.txt");
            if (!File.Exists(f)) return;
            string[] lines;
            try { lines = File.ReadAllLines(f); File.Delete(f); } catch (Exception e) { Plugin.Quiet(e, "Remote.Read"); return; }
            foreach (var l in lines)
            {
                var a = l.Trim().Split(' ');
                if (a.Length == 0 || a[0].Length == 0) continue;
                Out("> " + l.Trim());
                try { Run(a); } catch (Exception e) { Out("error " + e.Message); }
            }
        }

        // Fuer den Paketbauer (tools/oot/oot_build.py): die benoetigten Valheim-Texturen einmalig automatisch exportieren,
        // sobald man in irgendeiner Welt steht (-> config/GameImporter/vtex + vmat). Ersetzt das manuelle "statics dumpvtex".
        static float vtexAt = -1f; static bool vtexDone;
        static void AutoDumpVTex()
        {
            if (vtexDone || Player.m_localPlayer == null) return;
            var vm = Path.Combine(Dir, "vmat");
            if (Directory.Exists(vm) && Directory.GetFiles(vm, "*.png").Length > 0) { vtexDone = true; return; }
            if (vtexAt < 0f) { vtexAt = Time.unscaledTime + 8f; return; }          // Gelaende und Modelle erst laden lassen
            if (Time.unscaledTime < vtexAt) return;
            vtexDone = true;
            try
            {
                var r = Glb.DumpVTex();
                Out("auto dumpvtex: " + r.Split('\n')[0]);
                if (MessageHud.instance != null)
                    MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, "Game Importer: Valheim textures exported for the pack builder");
            }
            catch (Exception e) { Plugin.Quiet(e, "AutoDumpVTex"); }
        }

        static void Run(string[] a)
        {
            var pl = Player.m_localPlayer;
            var me = pl != null ? pl.transform.position : Vector3.zero;
            switch (a[0])
            {
                case "mapinfo":
                    if (a.Length > 2) me = new Vector3(F(a[1], 0f), 0f, F(a[2], 0f));
                    Out(MapImport.Debug(me));
                    break;
                case "mapsites": Out(MapImport.SitesInfo(me)); break;
                case "fog": Out("fog " + RenderSettings.fog + " density " + RenderSettings.fogDensity.ToString("F5") + " cap " + Plugin.MapFogMax.Value); break;
                case "fogmax": Plugin.MapFogMax.Value = F(a[1], Plugin.MapFogMax.Value); Out("fogmax " + Plugin.MapFogMax.Value); break;
                case "look":
                    // look px py pz lx ly lz [fov] | look off   (Hoehe mit + davor = ueber dem Kartenboden)
                    if (a.Length > 1 && a[1] == "off") { MapImport.LookOn = false; Out("look off"); break; }
                    {
                        Func<float, float, string, float> hy = (x, z, s) => s.StartsWith("+") ? MapImport.Height(x, z) + F(s.Substring(1), 0f) : F(s, 0f);
                        float px = F(a[1], 0f), pz = F(a[3], 0f), lx = F(a[4], 0f), lz = F(a[6], 0f);
                        MapImport.LookPos = new Vector3(px, hy(px, pz, a[2]), pz); MapImport.LookAt = new Vector3(lx, hy(lx, lz, a[5]), lz);
                        MapImport.LookFov = a.Length > 7 ? F(a[7], 60f) : 60f; MapImport.LookOn = true;
                        Out("look " + MapImport.LookPos + " -> " + MapImport.LookAt);
                    }
                    break;

                // ---- Spielpaket / Geschichte ----
                case "time":
                    // time 0.5 = Mittag (fest) | time off = normale Uhr. Nur fuer Testbilder.
                    if (EnvMan.instance == null) { Out("time: keine Welt"); break; }
                    {
                        var tv = Traverse.Create(EnvMan.instance);
                        bool on = a.Length > 1 && a[1] != "off";
                        tv.Field("m_debugTimeOfDay").SetValue(on);
                        if (on) tv.Field("m_debugTime").SetValue(F(a[1], 0.5f));
                        Out("time " + (on ? a[1] : "off"));
                    }
                    break;
                case "face":
                    // face x z: Spieler schaut dorthin
                    if (pl != null) { var d = new Vector3(F(a[1], 0f) - me.x, 0f, F(a[2], 0f) - me.z); if (d.sqrMagnitude > 0.01f) { pl.transform.rotation = Quaternion.LookRotation(d); var m = AccessTools.Method(typeof(Character), "SetLookDir"); if (m != null) { var ar = new object[m.GetParameters().Length]; ar[0] = d.normalized; for (int i = 1; i < ar.Length; i++) ar[i] = 0f; m.Invoke(pl, ar); } } Out("face ok"); }
                    break;
                case "oot": Out(OotPlay.Remote(a)); break;
                case "pos": Out("pos " + me.x.ToString("F1") + " " + me.y.ToString("F1") + " " + me.z.ToString("F1") + (ZNet.instance != null ? " world " + ZNet.instance.GetWorldName() : "")); break;
                case "pack": Out(Pack.Current != null ? Pack.Current.Info() : "kein Spielpaket aktiv"); break;
                case "npcs": Out(Npcs.Info()); break;
                case "glb":
                    // glb <pfad im Paket> [abstand]: ein Modell ohne Drehung/Skalierung vor den Spieler stellen (Fehlersuche)
                    if (pl != null && Pack.Current != null && a.Length > 1)
                    {
                        var gp = System.IO.Path.Combine(Pack.Current.Dir, a[1].Replace('/', System.IO.Path.DirectorySeparatorChar));
                        if (!System.IO.File.Exists(gp)) { Out("glb fehlt: " + gp); break; }
                        var go = Glb.Instantiate(gp, true);
                        go.transform.position = pl.transform.position + pl.transform.forward * (a.Length > 2 ? F(a[2], 15f) : 15f);
                        go.transform.rotation = Quaternion.identity; go.transform.localScale = Vector3.one;
                        int nv = 0, nt = 0; float maxE = 0f; string bnd = "";
                        foreach (var mf in go.GetComponentsInChildren<MeshFilter>())
                        {
                            var sm = mf.sharedMesh; if (sm == null) continue;
                            var vv = sm.vertices; var tt = sm.triangles; nv += vv.Length; nt += tt.Length / 3;
                            for (int i = 0; i + 2 < tt.Length; i += 3) maxE = Mathf.Max(maxE, (vv[tt[i]] - vv[tt[i + 1]]).magnitude, (vv[tt[i + 1]] - vv[tt[i + 2]]).magnitude, (vv[tt[i + 2]] - vv[tt[i]]).magnitude);
                            bnd += " bounds " + sm.bounds.min + ".." + sm.bounds.max + " sub " + sm.subMeshCount + " idx " + sm.indexFormat;
                        }
                        // optional: anderer Shader (Fehlersuche), z. B. "glb <pfad> 15 Standard"
                        if (a.Length > 3)
                        {
                            var sh = Glb.FindPrefabShader(string.Join(" ", a, 3, a.Length - 3));
                            if (sh == null) Out("Shader fehlt: " + string.Join(" ", a, 3, a.Length - 3));
                            else foreach (var r in go.GetComponentsInChildren<Renderer>()) { var ms = r.materials; foreach (var mm in ms) mm.shader = sh; r.materials = ms; }
                        }
                        string shn = ""; foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.sharedMaterial != null) { shn = r.sharedMaterial.shader.name; break; }
                        Out("glb " + a[1] + " bei " + go.transform.position + ": " + nv + " Ecken, " + nt + " Dreiecke, laengste Kante " + maxE.ToString("0.0") + " m," + bnd + " shader " + shn);
                    }
                    break;
                case "safe": Out(SafeZones.Info()); break;
                case "shaderprops":
                    // shaderprops <shader>: Eigenschaften eines Shaders (z. B. Custom/Piece) samt aktueller globaler Werte
                    {
                        var sh = Glb.FindPrefabShader(string.Join(" ", a, 1, a.Length - 1)) ?? Shader.Find(string.Join(" ", a, 1, a.Length - 1));
                        if (sh == null) { Out("shader nicht gefunden"); break; }
                        var sb2 = new System.Text.StringBuilder(sh.name + ":");
                        for (int i = 0; i < sh.GetPropertyCount(); i++) sb2.Append(" " + sh.GetPropertyName(i) + "(" + sh.GetPropertyType(i) + ")");
                        foreach (var g in new[] { "_GlobalSnow", "_SnowLevel", "_Snow", "_Wet", "_GlobalWetness", "_SnowAmount" }) sb2.Append(" | global " + g + "=" + Shader.GetGlobalFloat(g));
                        Out(sb2.ToString());
                    }
                    break;
                case "weather":
                    // weather <Umgebung> | weather off: Valheim-Wetter festhalten (z. B. Clear, Misty, Rain)
                    if (EnvMan.instance != null)
                    {
                        var wn = a.Length > 1 ? string.Join(" ", a, 1, a.Length - 1) : "off";     // Namen mit Leerzeichen ("DeepForest Mist")
                        EnvMan.instance.SetForceEnvironment(wn != "off" ? wn : ""); Out("weather " + wn);
                    }
                    break;
                case "mats":
                    // mats <prefab> | mats static: Materialien (Shader, Schalter, Werte) eines Prefabs bzw. des naechsten festen Modells
                    {
                        GameObject src = null;
                        if (a.Length > 1 && a[1] == "static" && pl != null)
                        {
                            // Boden unter dem Spieler (bzw. unter der festen Kamera)
                            var o = MapImport.LookOn ? MapImport.LookPos : pl.transform.position + Vector3.up;
                            var dir = MapImport.LookOn ? (MapImport.LookAt - MapImport.LookPos).normalized : Vector3.down;
                            RaycastHit hit;
                            if (Physics.Raycast(o, dir, out hit, 2000f, ~0, QueryTriggerInteraction.Ignore))
                            {
                                var rr = hit.collider.GetComponentInParent<Renderer>() ?? hit.collider.GetComponentInChildren<Renderer>();
                                if (rr == null && hit.collider.transform.parent != null) rr = hit.collider.transform.parent.GetComponentInChildren<Renderer>();
                                if (rr != null) src = rr.gameObject;
                                Out("Treffer " + hit.collider.name + " bei " + hit.point);
                            }
                        }
                        else if (a.Length > 1 && ZNetScene.instance != null) src = ZNetScene.instance.GetPrefab(a[1]);
                        if (src == null) { Out("nichts gefunden"); break; }
                        var sb3 = new System.Text.StringBuilder(src.name + ":");
                        int nr = 0;
                        foreach (var r in src.GetComponentsInChildren<Renderer>(true))
                        {
                            if (nr++ > 8) break;
                            sb3.Append("\n  " + r.name + " (" + r.GetType().Name + ")");
                            foreach (var m in r.sharedMaterials)
                            {
                                if (m == null) continue;
                                sb3.Append("\n    " + m.name + " [" + m.shader.name + "] q" + m.renderQueue + " kw " + string.Join(",", m.shaderKeywords) + " :");
                                for (int i = 0; i < m.shader.GetPropertyCount(); i++)
                                {
                                    var pn = m.shader.GetPropertyName(i); var pt = m.shader.GetPropertyType(i);
                                    if (pt == UnityEngine.Rendering.ShaderPropertyType.Float || pt == UnityEngine.Rendering.ShaderPropertyType.Range) sb3.Append(" " + pn + "=" + m.GetFloat(pn).ToString("0.###"));
                                    else if (pt == UnityEngine.Rendering.ShaderPropertyType.Color) { var c = m.GetColor(pn); sb3.Append(" " + pn + "=(" + c.r.ToString("0.##") + "," + c.g.ToString("0.##") + "," + c.b.ToString("0.##") + "," + c.a.ToString("0.##") + ")"); }
                                    else if (pt == UnityEngine.Rendering.ShaderPropertyType.Texture) { var t = m.GetTexture(pn); sb3.Append(" " + pn + "=" + (t != null ? t.name + "(" + t.width + ")" : "-")); }
                                    else if (pt == UnityEngine.Rendering.ShaderPropertyType.Vector) sb3.Append(" " + pn + "=" + m.GetVector(pn));
                                }
                            }
                        }
                        Out(sb3.ToString());
                    }
                    break;
                case "foliage":
                    // foliage | foliage near <m> | foliage far <m>
                    if (a.Length > 2 && a[1] == "near") Foliage.NearR = F(a[2], Foliage.NearR);
                    if (a.Length > 2 && a[1] == "far") Foliage.FarR = F(a[2], Foliage.FarR);
                    if (a.Length > 2 && a[1] == "coarse") Foliage.CoarseD = F(a[2], Foliage.CoarseD);
                    if (a.Length > 2 && a[1] == "tint") Foliage.Retint(F(a[2], 1f));
                    if (a.Length > 1 && a[1] == "rebuild") Foliage.Rebuild();
                    if (a.Length > 1 && a[1] == "atlas") Out(Foliage.DumpAtlas(Path.Combine(Dir, "foliage_atlas.png")));
                    Out(Foliage.Info());
                    break;
                case "posetest":
                    // posetest <bool-param|trigger:name>: Test-Figur vor den Spieler, Pose setzen; "posetest" ohne Wert misst Kopfhoehe
                    if (pl != null)
                    {
                        if (poseGo == null) poseGo = Npcs.SpawnTemp("posetest", new Dictionary<string, object> { { "model", "male" } }, pl.transform.position + pl.transform.forward * 2f, 0f);
                        var an1 = poseGo.GetComponentInChildren<Animator>();
                        if (a.Length > 1 && an1 != null)
                        {
                            foreach (var b in new[] { "emote_sit", "emote_sitchair", "attach_chair", "attach_bed", "lying_down" }) an1.SetBool(b, false);
                            if (a[1].StartsWith("trigger:")) an1.SetTrigger(a[1].Substring(8)); else if (a[1] != "stand") an1.SetBool(a[1], true); else an1.SetTrigger("emote_stop");
                        }
                        Transform head = null; foreach (var tt in poseGo.GetComponentsInChildren<Transform>(true)) if (tt.name == "Head") { head = tt; break; }
                        Out("posetest: Kopf " + (head != null ? (head.position.y - poseGo.transform.position.y).ToString("F2") : "?") + " m ueber Wurzel, Huefte " +
                            (an1 != null && an1.isHuman ? (an1.GetBoneTransform(HumanBodyBones.Hips).position.y - poseGo.transform.position.y).ToString("F2") : "?"));
                    }
                    break;
                case "lineup":
                    // lineup <x> <y> <z> <yaw> <model> <slot> <item,item,...> | lineup off: Figuren nebeneinander (Kleidung vergleichen)
                    foreach (var g in lineup) if (g != null) UnityEngine.Object.Destroy(g);
                    lineup.Clear();
                    if (a.Length > 7)
                    {
                        var p0 = new Vector3(F(a[1], 0f), F(a[2], 0f), F(a[3], 0f)); float yw = F(a[4], 0f);
                        var right = Quaternion.Euler(0f, yw, 0f) * Vector3.right;
                        var items = a[7].Split(',');
                        for (int i = 0; i < items.Length; i++)
                        {
                            var lk = new Dictionary<string, object> { { "model", a[5] }, { "chest", "" }, { "legs", "" } };
                            foreach (var sl in a[6].Split('+')) lk[sl] = items[i];
                            lineup.Add(Npcs.SpawnTemp("lineup" + i, lk, p0 + right * ((i - (items.Length - 1) * 0.5f) * 0.9f), yw));
                        }
                    }
                    Out("lineup " + lineup.Count);
                    break;
                case "cutseek":
                    // cutseek <sekunden> | cutseek off: laufende Cutscene-Spur an eine Stelle setzen und dort anhalten
                    if (StoryUi.I != null && StoryUi.I.track != null)
                    {
                        if (a.Length > 1 && a[1] == "off") StoryUi.I.track.Seek(StoryUi.I.track.T, false);
                        else if (a.Length > 1) StoryUi.I.track.Seek(F(a[1], 0f), true);
                        Out("cutseek " + StoryUi.I.track.T.ToString("F2"));
                    }
                    else Out("cutseek: keine Spur aktiv");
                    break;
                case "animparams":
                    // animparams: Parameter von Valheims Spieler-Animator (Emotes, Laufen) - Fehlersuche Cutscene-Darsteller
                    if (pl != null)
                    {
                        var an0 = pl.GetComponentInChildren<Animator>(); var sb0 = new System.Text.StringBuilder("animparams:");
                        if (an0 != null) foreach (var p0 in an0.parameters) sb0.Append(" " + p0.name + "(" + p0.type.ToString()[0] + ")");
                        Out(sb0.ToString());
                    }
                    break;
                case "farview": Plugin.MapFarView.Value = a.Length < 2 || a[1] != "off"; Out("farview " + Plugin.MapFarView.Value); break;
                case "reflect":
                    // reflect <Typ> [filter]: Felder/Methoden einer Valheim-Klasse zur Laufzeit (Fehlersuche bei Spielversionen)
                    try
                    {
                        var rt = typeof(Player).Assembly.GetType(a.Length > 1 ? a[1] : "Heightmap");
                        if (rt == null) { Out("reflect: Typ fehlt"); break; }
                        var flt = a.Length > 2 ? a[2].ToLowerInvariant() : "";
                        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
                        var sbr = new System.Text.StringBuilder("reflect " + rt.FullName + ":");
                        foreach (var mi in rt.GetMembers(bf))
                        {
                            if (flt.Length > 0 && !mi.Name.ToLowerInvariant().Contains(flt)) continue;
                            var fi = mi as System.Reflection.FieldInfo; var mth = mi as System.Reflection.MethodInfo;
                            sbr.Append(" | " + (fi != null ? "F " + fi.FieldType.Name + " " : mth != null ? "M " + mth.ReturnType.Name + " " : mi.MemberType + " ") + mi.Name);
                        }
                        Out(sbr.ToString());
                    }
                    catch (Exception e) { Out("reflect: " + e.Message); }
                    break;
                case "water": Out(WaterFix.Info(a.Length > 2 ? new Vector3(F(a[1], 0f), 0f, F(a[2], 0f)) : me)); break;
                case "die":
                    // die: Test-Charakter sterben lassen (Wiedereinstieg pruefen)
                    if (pl != null)
                    {
                        var gm = HarmonyLib.AccessTools.Method(typeof(Player), "SetGodMode"); if (gm != null) gm.Invoke(pl, new object[] { false });
                        godWanted = false;
                        var hit = new HitData(); hit.m_damage.m_damage = 1e6f; hit.m_point = pl.transform.position;
                        pl.Damage(hit); Out("die");
                    }
                    break;
                case "holes": Out(TerrainHoles.Info(a.Length > 2 ? new Vector3(F(a[1], 0f), 0f, F(a[2], 0f)) : me)); break;
                case "statics":
                    if (a.Length > 2 && a[1] == "hide") Out(Statics.Hide(a[2], true));
                    else if (a.Length > 1 && a[1] == "float") Out(Statics.Floating(a.Length > 2 ? F(a[2], 8f) : 8f));
                    else if (a.Length > 2 && a[1] == "shader") Out(Glb.SetStaticShader(string.Join(" ", a, 2, a.Length - 2)));
                    else if (a.Length > 1 && a[1] == "shaders") Out(Glb.ListShaders());
                    else if (a.Length > 2 && a[1] == "matinfo") Out(Glb.MatInfo(a[2]));
                    else if (a.Length > 2 && a[1] == "dumptex") Out(Glb.DumpTex(a[2]));
                    else if (a.Length > 2 && a[1] == "dumpmat") Out(Glb.DumpMat(a[2], a.Length > 3 ? a[3] : null));
                    else if (a.Length > 1 && a[1] == "dumpvtex") Out(Glb.DumpVTex());
                    else if (a.Length > 2 && a[1] == "vcshader") Out(Glb.SetVcShader(string.Join(" ", a, 2, a.Length - 2)));
                    else if (a.Length > 1 && a[1] == "show") Out(Statics.Hide(null, false));
                    else if (a.Length > 1 && a[1] == "near") Out(Statics.Near(a.Length > 4 ? new Vector3(F(a[2], 0f), F(a[3], 0f), F(a[4], 0f)) : me, a.Length > 5 ? F(a[5], 6f) : (a.Length == 3 ? F(a[2], 6f) : 6f)));
                    else Out(Statics.Info());
                    break;
                case "horse": Out(Horse.Test(a)); break;
                case "witcher": Out(Witcher.Test(a)); break;
                case "creatures": Out(CreatureSkins.Info()); break;
                case "god":
                    // god [off]: Test-Charakter unverwundbar (nur fuer Testbilder)
                    if (pl != null) { var on = a.Length < 2 || a[1] != "off"; var m = HarmonyLib.AccessTools.Method(typeof(Player), "SetGodMode"); if (m != null) m.Invoke(pl, new object[] { on }); godWanted = on; godFor = on ? pl : null; Out("god " + on); }
                    break;
                case "killnear":
                    // killnear [Radius]: Gegner im Umkreis besiegen (Test von Kill-Zielen)
                    if (pl != null)
                    {
                        float rad = a.Length > 1 ? F(a[1], 20f) : 20f; int n = 0;
                        foreach (var c in new List<Character>(Character.GetAllCharacters()))
                        {
                            if (c == null || c.IsPlayer() || c.IsDead() || c.IsTamed() || Vector3.Distance(c.transform.position, pl.transform.position) > rad) continue;
                            var hit = new HitData(); hit.m_damage.m_damage = 1e6f; hit.m_point = c.transform.position; hit.SetAttacker(pl);
                            c.Damage(hit); n++;
                        }
                        Out("killnear " + n);
                    }
                    break;
                case "near":
                    // near [Radius]: Kreaturen im Umkreis auflisten
                    if (pl != null)
                    {
                        float rad = a.Length > 1 ? F(a[1], 30f) : 30f; var sb = new System.Text.StringBuilder("near:");
                        foreach (var c in Character.GetAllCharacters())
                            if (c != null && !c.IsPlayer() && Vector3.Distance(c.transform.position, pl.transform.position) <= rad)
                                sb.Append(" " + Utils.GetPrefabName(c.gameObject) + "@" + Vector3.Distance(c.transform.position, pl.transform.position).ToString("F0") + (c.IsDead() ? "(tot)" : ""));
                        Out(sb.ToString());
                    }
                    break;
                case "api":
                    // api <Typ> [Filter]: Methoden/Felder eines Spieltyps zur Laufzeit (fuer Mod-Entwicklung)
                    {
                        var ty = a.Length > 1 ? HarmonyLib.AccessTools.TypeByName(a[1]) : null;
                        if (ty == null) { Out("api: Typ?"); break; }
                        var flt = a.Length > 2 ? a[2].ToLowerInvariant() : "";
                        var sb = new System.Text.StringBuilder("api " + ty.FullName + " : " + (ty.BaseType != null ? ty.BaseType.Name : ""));
                        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly;
                        foreach (var m in ty.GetMembers(bf))
                        {
                            if (m.Name.Contains("<") || (flt != "" && !m.Name.ToLowerInvariant().Contains(flt))) continue;
                            var mi = m as System.Reflection.MethodInfo; var fi = m as System.Reflection.FieldInfo;
                            if (mi != null) { var ps = new List<string>(); foreach (var p0 in mi.GetParameters()) ps.Add(p0.ParameterType.Name + " " + p0.Name); sb.Append("\n  M " + mi.ReturnType.Name + " " + mi.Name + "(" + string.Join(", ", ps.ToArray()) + ")"); }
                            else if (fi != null) sb.Append("\n  F " + fi.FieldType.Name + " " + fi.Name);
                        }
                        Out(sb.ToString());
                    }
                    break;
                case "buttons":
                    // buttons: alle Eingabe-Namen von ZInput (z. B. "Inventory", "Hide") mit Taste
                    {
                        var zi = HarmonyLib.AccessTools.TypeByName("ZInput");
                        var inst = zi != null ? HarmonyLib.AccessTools.Property(zi, "instance")?.GetValue(null, null) : null;
                        var sb = new System.Text.StringBuilder("buttons:");
                        if (inst != null)
                            foreach (var f0 in zi.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
                            {
                                var v = f0.GetValue(inst) as System.Collections.IDictionary;
                                if (v == null) continue;
                                sb.Append("\n[" + f0.Name + "]");
                                foreach (System.Collections.DictionaryEntry e in v)
                                {
                                    string key = "";
                                    try { var val = e.Value; var kf = val.GetType().GetField("m_key") ?? val.GetType().GetField("Key"); var pathF = val.GetType().GetProperty("Path") ?? val.GetType().GetProperty("path"); key = kf != null ? "" + kf.GetValue(val) : pathF != null ? "" + pathF.GetValue(val, null) : val.ToString(); } catch { }
                                    sb.Append(" " + e.Key + "=" + key + ";");
                                }
                            }
                        else sb.Append(" ZInput.instance fehlt");
                        Out(sb.ToString());
                    }
                    break;
                case "mount": if (pl != null) { Horse.Call(); Out(ValheimMount.Info(pl)); } break;
                case "keys":
                    // keys <taste> : Witcher-Steuerung testen (q, r, x, 1, 2, alt, tab)
                    if (a.Length > 1 && a[1] == "quick") W3Controls.QuickOpen = !W3Controls.QuickOpen;
                    if (a.Length > 1 && a[1] == "draw") W3Controls.TestDraw(a.Length > 2 ? (int)F(a[2], 0f) : 0);
                    Out("keys: Witcher-Steuerung " + (W3Controls.On ? "an" : "aus") + ", Waffe gezogen: " + W3Controls.WeaponDrawn() + ", Schnellmenue " + W3Controls.QuickOpen);
                    break;
                case "senses": Senses.Force = a.Length < 2 || a[1] != "off"; Out("senses " + Senses.Force); break;
                case "travel":
                    // travel | travel <id> | travel menu: Schnellreise testen
                    if (a.Length < 2) Out(FastTravel.List());
                    else if (a[1] == "menu") { FastTravel.Open = !FastTravel.Open; Out("menu " + FastTravel.Open); }
                    else Out(FastTravel.Travel(a[1]));
                    break;
                case "inv": WitcherInv.Open = a.Length < 2 || a[1] != "off"; Out("inv " + WitcherInv.Open); break;
                case "avatar":
                    if (a.Length > 1) Avatar.Enabled = a[1] != "off";
                    Out(Avatar.Info());
                    break;
                case "story": Out(Story.Info()); break;
                case "dlg": Out(StoryUi.Debug()); break;
                case "tp":
                    // tp x z  | tp place <id>  | tp npc <id>
                    {
                        Vector3 p = Vector3.zero; bool ok = false;
                        if (a.Length > 2 && (a[1] == "place" || a[1] == "npc") && Pack.Current != null)
                        {
                            Pack.Place pc; Pack.Npc n;
                            if (a[1] == "place" && Pack.Current.Places.TryGetValue(a[2], out pc)) { p = new Vector3(pc.X + 3f, 0f, pc.Z - 8f); ok = true; }
                            if (a[1] == "npc" && Pack.Current.NpcById.TryGetValue(a[2], out n) && Pack.Current.Pos(n.Pos, out p)) { p += new Vector3(0f, 0f, -3f); ok = true; }
                        }
                        else if (a.Length > 3) { p = new Vector3(F(a[1], 0f), F(a[2], 0f), F(a[3], 0f)); ok = true; }      // tp x y z: genau (Stockwerke)
                        else if (a.Length > 2) { p = new Vector3(F(a[1], 0f), float.NaN, F(a[2], 0f)); ok = true; }
                        if (!ok || pl == null) { Out("tp: Ziel unbekannt"); break; }
                        if (float.IsNaN(p.y) || p.y == 0f) p.y = Pack.Ground(p.x, p.z) + 1.5f;
                        if (a.Length > 3 && a[1] != "place" && a[1] != "npc")
                        {
                            // ohne Ladebildschirm direkt hinstellen (im Gebaeude, Gegend ist geladen); Boden darunter suchen
                            RaycastHit fh;
                            if (Physics.Raycast(p + Vector3.up * 1.2f, Vector3.down, out fh, 6f, LayerMask.GetMask("Default", "static_solid", "terrain", "piece"), QueryTriggerInteraction.Ignore)) p.y = fh.point.y + 0.1f;
                            var body = pl.GetComponent<Rigidbody>(); if (body != null) { body.position = p; body.velocity = Vector3.zero; }
                            pl.transform.position = p;
                            try { HarmonyLib.AccessTools.FieldRefAccess<Character, float>("m_maxAirAltitude")(pl) = p.y; } catch { }   // kein Sturzschaden durchs Hinstellen
                        }
                        else pl.TeleportTo(p, pl.transform.rotation, true);
                        Out("tp " + p);
                    }
                    break;
                case "talk": Out(StoryUi.StartDialogue(a[1]) ? StoryUi.Debug() : "talk: geht nicht"); break;
                case "pick": StoryUi.Pick(a.Length > 1 ? (int)F(a[1], 1f) - 1 : 0); Out(StoryUi.Debug()); break;
                case "endtalk": if (StoryUi.I != null) StoryUi.I.Close(); Out("dialog zu"); break;
                case "cut": StoryUi.PlayCutscene(a[1]); Out(StoryUi.Debug()); break;
                case "cutend": StoryUi.StopCut(); Out(StoryUi.Debug()); break;
                case "clips": Out(Npcs.Clips(a[1])); break;
                case "anim": Out(Npcs.Play(Npcs.Get(a[1]), a.Length > 2 ? a[2] : null, a.Length > 3 && a[3] == "loop") ? "anim ok" : "anim: geht nicht"); break;
                case "quest":
                    if (a.Length > 2) Story.SetStage(a[1], a[2]); else Story.StartQuest(a[1]);
                    Out(Story.Info()); break;
                case "flag": Story.Flags.Add(a[1]); Story.Save(); Out(Story.Info()); break;
                case "storyreset": Story.Reset(); Out("story reset"); break;
                case "act":
                    // act <json-aktion>: Aktion wie aus dem Paket ausfuehren, z. B. act {"door":"q001_bedroom_door_left"}
                    { var o = Json.Parse(string.Join(" ", a, 1, a.Length - 1)); Story.Do(o is List<object> ? (List<object>)o : new List<object> { o }); Out(Story.Info()); }
                    break;
                case "interact": Out(Interact.Debug(true)); break;
                case "take":
                    // take <id> [rec] [breite hoehe] | take stop | take: Einstellung aus shots/<id>.json abspielen/aufnehmen
                    if (a.Length < 2) { Out(Shots.Info()); break; }
                    if (a[1] == "stop") { Shots.Stop(); Out(Shots.Info()); break; }
                    Out(Shots.Play(a[1], a.Length > 2 && a[2] == "rec", a.Length > 4 ? (int)F(a[3], 1920f) : 1920, a.Length > 4 ? (int)F(a[4], 1080f) : 1080));
                    break;
                case "hover": Out(Interact.Debug(false)); break;
                case "give":
                    {
                        var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(a[1]) : null;
                        if (go != null && pl != null) pl.GetInventory().AddItem(go, a.Length > 2 ? (int)F(a[2], 1f) : 1);
                        Out("give " + a[1] + (go == null ? " (unbekannt)" : ""));
                    }
                    break;
                case "tree":
                    // tree <prefab> [tiefe]: Aufbau eines Prefabs (Kinder + Komponenten)
                    if (ZNetScene.instance != null && a.Length > 1)
                    {
                        var pf = ZNetScene.instance.GetPrefab(a[1]) ?? (ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(a[1]) : null);
                        if (pf == null) { Out("tree: gibt's nicht"); break; }
                        int maxD = a.Length > 2 ? (int)F(a[2], 3f) : 3; var sb = new System.Text.StringBuilder("tree " + a[1]);
                        System.Action<Transform, int> walk = null;
                        walk = (t, d) =>
                        {
                            var cs = new List<string>(); foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) cs.Add(c.GetType().Name);
                            sb.Append("\n" + new string(' ', d * 2) + t.name + " [" + string.Join(",", cs.ToArray()) + "]");
                            if (d < maxD) foreach (Transform ch in t) walk(ch, d + 1);
                        };
                        walk(pf.transform, 0);
                        Out(sb.ToString());
                    }
                    break;
                case "items":
                    // items <teil> : Gegenstands-Prefabs (ObjectDB) mit Typ
                    if (ObjectDB.instance != null)
                    {
                        var sb = new System.Text.StringBuilder("items:"); var flt = a.Length > 1 ? a[1].ToLowerInvariant() : "";
                        foreach (var it in ObjectDB.instance.m_items)
                        {
                            if (it == null || (flt != "" && !it.name.ToLowerInvariant().Contains(flt))) continue;
                            var idr = it.GetComponent<ItemDrop>();
                            sb.Append(" " + it.name + (idr != null ? "(" + idr.m_itemData.m_shared.m_itemType + ")" : ""));
                        }
                        Out(sb.ToString());
                    }
                    break;
                case "prefabs":
                    // prefabs <teil1> [teil2 ...]: Prefab-Namen (z. B. Effekte) suchen
                    if (ZNetScene.instance != null)
                    {
                        var hits = new List<string>();
                        foreach (var pf in ZNetScene.instance.m_prefabs)
                        {
                            if (pf == null) continue; var n = pf.name.ToLowerInvariant();
                            for (int i = 1; i < a.Length; i++) if (n.Contains(a[i].ToLowerInvariant())) { hits.Add(pf.name); break; }
                        }
                        Out("prefabs " + hits.Count + ": " + string.Join(", ", hits.ToArray()));
                    }
                    break;
                case "spawn":
                    // spawn <Prefab> [Abstand]: z. B. Greydwarf vor den Spieler setzen (Test der Zeichen)
                    if (pl != null && ZNetScene.instance != null)
                    {
                        var pf = ZNetScene.instance.GetPrefab(a[1]);
                        if (pf != null) UnityEngine.Object.Instantiate(pf, pl.transform.position + pl.transform.forward * (a.Length > 2 ? F(a[2], 5f) : 5f) + Vector3.up, Quaternion.identity);
                        Out("spawn " + a[1] + (pf == null ? " (unbekannt)" : ""));
                    }
                    break;
                case "equip":
                    // equip <Prefabname>: Gegenstand aus dem Inventar ausruesten (zum Testen)
                    if (pl != null)
                    {
                        ItemDrop.ItemData it = null;
                        foreach (var x in pl.GetInventory().GetAllItems()) if (x.m_dropPrefab != null && x.m_dropPrefab.name == a[1]) { it = x; break; }
                        if (it != null) pl.EquipItem(it, true);
                        Out("equip " + a[1] + (it == null ? " (nicht im Inventar)" : " ok"));
                    }
                    break;
                case "attack":
                    // attack: einmal zuschlagen (zum Testen der Angriffs-Animation)
                    if (pl != null) { var m = HarmonyLib.AccessTools.Method(typeof(Humanoid), "StartAttack"); if (m != null) { var ps = m.GetParameters(); var args = new object[ps.Length]; for (int i = 0; i < ps.Length; i++) args[i] = ps[i].ParameterType == typeof(bool) ? (object)false : null; Out("attack " + m.Invoke(pl, args)); } }
                    break;
                case "shot":
                    {
                        var path = Path.Combine(Dir, a.Length > 1 ? a[1] : "shot.png");
                        ScreenCapture.CaptureScreenshot(path);
                        Out("shot " + path);
                    }
                    break;
                case "save": if (ZNet.instance != null) ZNet.instance.SaveWorldAndPlayerProfiles(); Out("gespeichert"); break;
                case "quit":
                    // nur fuer die eigene Test-Instanz: speichern und sauber beenden
                    if (ZNet.instance != null) ZNet.instance.SaveWorldAndPlayerProfiles();
                    Out("quit"); Application.Quit(); break;
                case "packreload":
                    if (Pack.Current != null) { var map = Pack.Current.Id; Glb.ForgetModels(); Npcs.Clear(); CreatureSkins.Clear(); Pack.OnWorld(map); }
                    Out(Pack.Current != null ? Pack.Current.Info() : "kein Paket");
                    break;
                default: Out("unknown " + a[0]); break;
            }
            Out("done");
        }
    }

    // Test-Autostart: BepInEx/config/GameImporter/autostart.txt mit "Welt" und "Charakter" (je eine Zeile).
    // Startet direkt aus dem Hauptmenue in die Welt, ohne Klicken. Die Datei wird dabei geloescht (einmalig).
    public static class Autostart
    {
        static bool done; static float at = -1f;

        public static void Tick()
        {
            if (done || FejdStartup.instance == null) return;
            var f = Path.Combine(Remote.Dir, "autostart.txt");
            if (!File.Exists(f)) { done = true; return; }
            if (at < 0f) { at = Time.unscaledTime + 3f; return; }
            if (Time.unscaledTime < at) return;
            done = true;
            try
            {
                var lines = File.ReadAllLines(f); File.Delete(f);
                string world = lines.Length > 0 ? lines[0].Trim() : "", ch = lines.Length > 1 ? lines[1].Trim() : "GITest";
                if (world.Length == 0) return;
                Start(world, ch);
            }
            catch (Exception e) { Plugin.Warn("Autostart: " + e); Remote.Out("autostart error " + e.Message); }
        }

        static void Start(string world, string ch)
        {
            var src = FileHelpers.FileSource.Local;
            PlayerProfile prof = null;
            foreach (var p in SaveSystem.GetAllPlayerProfiles()) if (string.Equals(p.GetName(), ch, StringComparison.OrdinalIgnoreCase)) { prof = p; break; }
            if (prof == null)
            {
                prof = new PlayerProfile(ch.ToLowerInvariant(), src);
                prof.SetName(ch);
                var save = AccessTools.Method(typeof(PlayerProfile), "Save") ?? AccessTools.Method(typeof(PlayerProfile), "SavePlayerToDisk");
                try { if (save != null && save.GetParameters().Length == 0) save.Invoke(prof, null); } catch (Exception e) { Plugin.Warn("Autostart: Charakter speichern: " + e.Message); }
                Plugin.Log("Autostart: neuer Test-Charakter " + ch);
            }
            Game.SetProfile(prof.GetFilename(), prof.m_fileSource);
            var w = World.GetCreateWorld(world, src);
            var fs = FejdStartup.instance;
            AccessTools.Field(typeof(FejdStartup), "m_world")?.SetValue(fs, w);
            // wie im Menue "Welt starten", aber immer privat: kein offener/oeffentlicher Server, kein Crossplay
            // (die Einstellungen werden danach zurueckgesetzt, damit Moritz' normales Menue unveraendert bleibt)
            var restore = new List<KeyValuePair<UnityEngine.UI.Toggle, bool>>();
            foreach (var tn in new[] { "m_openServerToggle", "m_publicServerToggle", "m_crossplayServerToggle" })
            {
                var tf = AccessTools.Field(typeof(FejdStartup), tn);
                var tg = tf != null ? tf.GetValue(fs) as UnityEngine.UI.Toggle : null;
                if (tg != null) { restore.Add(new KeyValuePair<UnityEngine.UI.Toggle, bool>(tg, tg.isOn)); tg.isOn = false; }
            }
            Remote.Out("autostart " + world + " / " + ch);
            Plugin.Log("Autostart: Welt " + world + ", Charakter " + ch);
            try { fs.OnWorldStart(); }
            finally { foreach (var kv in restore) if (kv.Key != null) kv.Key.isOn = kv.Value; }
        }
    }
}
