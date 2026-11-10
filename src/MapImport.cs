using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Karten-Import: eine Welt bekommt ihr Gelaende, ihre Biome und ihre Waelder aus einer .vmap-Datei
    // (Heightmap, Biom-Karte, Waldkarte - z. B. aus der Karte eines anderen Spiels umgerechnet).
    // Wir liefern nur das Werkzeug; die Kartendaten bringt jeder selbst mit.
    // Ersetzt wird direkt die Hoehe, die Valheims Weltgenerator ausrechnet: deshalb gibt es keine 8-m-Grenze,
    // Berge koennen beliebig hoch sein. Nur fuer NEUE Welten (Orte wie Dungeons werden beim Anlegen verteilt).
    // Aktiv fuer Welten, deren Name mit "map-" beginnt (map-hyrule -> maps/hyrule.vmap) oder die in MapWorlds stehen.
    public static class MapImport
    {
        public static bool Active;
        public static string Name, Error;

        static int W, H, BW, BH;
        static float x0, z0, cell, hMin, hStep, bx0, bz0, bcell;
        static ushort[] hts;
        static byte[] bio, forest, colr, paint;
        const float OutsideHeight = -40f, OpenForest = 1.6f;

        static readonly Heightmap.Biome[] Codes =
        {
            Heightmap.Biome.Ocean, Heightmap.Biome.Meadows, Heightmap.Biome.BlackForest, Heightmap.Biome.Swamp,
            Heightmap.Biome.Mountain, Heightmap.Biome.Plains, Heightmap.Biome.Mistlands, Heightmap.Biome.AshLands, Heightmap.Biome.DeepNorth
        };

        public static string Dir { get { return Path.Combine(Paths.ConfigPath, "GameImporter", "maps"); } }

        // Welche Karte gehoert zu dieser Welt?
        public static string MapFor(string world)
        {
            if (string.IsNullOrEmpty(world) || world == "menu") return null;
            foreach (var e in (Plugin.MapWorlds.Value ?? "").Split(','))
            {
                int i = e.IndexOf('=');
                if (i > 0 && string.Equals(e.Substring(0, i).Trim(), world, StringComparison.OrdinalIgnoreCase)) return e.Substring(i + 1).Trim();
            }
            // map-<karte> oder map-<karte>~<irgendwas> (mehrere Welten mit derselben Karte)
            if (world.StartsWith("map-", StringComparison.OrdinalIgnoreCase) && world.Length > 4) { var m = world.Substring(4); int t = m.IndexOf('~'); return t > 0 ? m.Substring(0, t) : m; }
            // Valheim laesst im Weltnamen kein "-" oder "~" zu: "Skellige", "Skellige2", "SkelligeMoritz", "MapSkellige" gehen auch
            var w2 = world.StartsWith("map", StringComparison.OrdinalIgnoreCase) ? world.Substring(3) : world;
            string best = null;
            foreach (var id in KnownMaps())
                if (w2.StartsWith(id, StringComparison.OrdinalIgnoreCase) && (best == null || id.Length > best.Length)) best = id;
            return best;
        }

        // Karten, die es gibt: Spielpakete mit pack.json und lose .vmap-Dateien
        static List<string> KnownMaps()
        {
            var l = new List<string>();
            try { if (Directory.Exists(Pack.Root)) foreach (var d in Directory.GetDirectories(Pack.Root)) if (File.Exists(Path.Combine(d, "pack.json"))) l.Add(Path.GetFileName(d)); } catch { }
            try { if (Directory.Exists(Dir)) foreach (var f in Directory.GetFiles(Dir, "*.vmap")) l.Add(Path.GetFileNameWithoutExtension(f)); } catch { }
            return l;
        }

        // Vor WorldGenerator.Initialize: alles, was der Generator danach berechnet, nutzt schon die Karte
        public static void OnWorld(World w)
        {
            Active = false; Error = null; Name = null;
            Unload(); TerrainHoles.Clear();
            var map = MapFor(w != null ? w.m_name : null);
            Pack.OnWorld(map);
            if (map == null) return;
            var pk = Pack.Current;
            var path = pk != null && !string.IsNullOrEmpty(pk.MapFile) ? Path.Combine(pk.Dir, pk.MapFile) : Path.Combine(Dir, map + ".vmap");
            try
            {
                Load(path);
                TerrainHoles.Load(Path.ChangeExtension(path, ".holes"));
                Name = map; Active = true;
                ClearCaches();
                LoadSites(map, w.m_name);
                Plugin.Log("Karten-Import: Welt '" + w.m_name + "' nutzt " + path + " (" + W + "x" + H + ", " + cell.ToString("F2") + " m, Hoehe " + hMin.ToString("F0") + ".." + (hMin + hStep * 65535f).ToString("F0") + " m)");
            }
            catch (Exception e)
            {
                Error = e.Message;
                Plugin.Warn("Karten-Import: " + path + " nicht lesbar: " + e.Message);
            }
        }

        static void Load(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var br = new BinaryReader(new BufferedStream(gz, 1 << 16)))
            {
                if (new string(br.ReadChars(4)) != "VMAP") throw new Exception("keine .vmap-Datei");
                int ver = br.ReadInt32();
                if (ver != 1) throw new Exception("Version " + ver + " unbekannt");
                W = br.ReadInt32(); H = br.ReadInt32();
                x0 = br.ReadSingle(); z0 = br.ReadSingle(); cell = br.ReadSingle(); hMin = br.ReadSingle(); hStep = br.ReadSingle();
                var raw = br.ReadBytes(W * H * 2);
                hts = new ushort[W * H];
                Buffer.BlockCopy(raw, 0, hts, 0, raw.Length);
                BW = br.ReadInt32(); BH = br.ReadInt32();
                bx0 = br.ReadSingle(); bz0 = br.ReadSingle(); bcell = br.ReadSingle();
                bio = br.ReadBytes(BW * BH);
                forest = br.ReadBytes(BW * BH);
                if (raw.Length != W * H * 2 || bio.Length != BW * BH || forest.Length != BW * BH) throw new Exception("Datei abgeschnitten");
                // optional: Bodenfarben des Originals (fuer die Fernsicht)
                colr = null; paint = null;
                // optionale Bloecke: COLR (Bodenfarben, Biom-Raster) und PNT1 (Erd-Farbe fuer Wege, Hoehen-Raster)
                for (int guard = 0; guard < 4; guard++)
                {
                    byte[] tag;
                    try { tag = br.ReadBytes(4); } catch (EndOfStreamException) { break; }
                    if (tag.Length < 4) break;
                    string tn = System.Text.Encoding.ASCII.GetString(tag);
                    if (tn == "COLR") { colr = br.ReadBytes(BW * BH * 3); if (colr.Length != BW * BH * 3) { colr = null; break; } }
                    else if (tn == "PNT1") { paint = br.ReadBytes(W * H); if (paint.Length != W * H) { paint = null; break; } }
                    else break;
                }
                if (paint != null) Plugin.Log("Karte: Erd-Farbe fuer Wege geladen");
            }
        }

        // Valheim merkt sich Biome pro Gebiet; nach dem Laden einer anderen Karte duerfen keine alten Werte drinstehen
        static void ClearCaches()
        {
            foreach (var n in new[] { "s_cachedBiomes", "s_cachedBiomeAreas" })
            {
                try { var f = AccessTools.Field(typeof(WorldGenerator), n); var d = f != null ? f.GetValue(null) as System.Collections.IDictionary : null; if (d != null) lock (d) d.Clear(); }
                catch (Exception e) { Plugin.Quiet(e, "MapImport.ClearCaches"); }
            }
        }

        // ---------- Abfragen (laufen auch in Valheims Generator-Threads: nur lesen) ----------
        public static float Height(float x, float z)
        {
            float u = (x - x0) / cell, v = (z - z0) / cell;
            if (u < 0f || v < 0f || u > W - 1 || v > H - 1) return OutsideHeight;
            int i = (int)u, j = (int)v;
            if (i >= W - 1) i = W - 2; if (j >= H - 1) j = H - 2;
            float fu = u - i, fv = v - j;
            int k = j * W + i;
            float a = hts[k], b = hts[k + 1], c = hts[k + W], d = hts[k + W + 1];
            float h = (a + (b - a) * fu) * (1f - fv) + (c + (d - c) * fu) * fv;
            return hMin + h * hStep;
        }

        // Erd-Anteil 0..1 an einer Stelle (Wege des Originals), 0 ohne PNT1-Block
        public static float Paint(float x, float z)
        {
            if (paint == null) return 0f;
            float u = (x - x0) / cell, v = (z - z0) / cell;
            if (u < 0f || v < 0f || u > W - 1 || v > H - 1) return 0f;
            int i = Mathf.Min((int)u, W - 2), j = Mathf.Min((int)v, H - 2);
            float fu = u - i, fv = v - j; int k = j * W + i;
            float a = paint[k], b = paint[k + 1], c = paint[k + W], d = paint[k + W + 1];
            return ((a + (b - a) * fu) * (1f - fv) + (c + (d - c) * fu) * fv) / 255f;
        }

        // Wege in Valheims Boden malen: vor jedem Neuaufbau des Gelaende-Netzes die Erd-Maske (r) mit unserer mischen
        static System.Reflection.FieldInfo fPaint, fHmW, fHmS;
        static int paintLogged;
        static void PaintPre(Heightmap __instance)
        {
            if (!Active || paint == null || __instance == null) return;
            try
            {
                if (fPaint == null) { fPaint = AccessTools.Field(typeof(Heightmap), "m_paintMask"); fHmW = AccessTools.Field(typeof(Heightmap), "m_width"); fHmS = AccessTools.Field(typeof(Heightmap), "m_scale"); }
                var tex = fPaint != null ? fPaint.GetValue(__instance) as Texture2D : null;
                if (tex == null) { if (paintLogged++ == 0) Plugin.Warn("Wege: m_paintMask fehlt (" + (fPaint == null ? "Feld" : "Textur") + ")"); return; }
                int w = (int)fHmW.GetValue(__instance); float sc = (float)fHmS.GetValue(__instance);
                var p0 = __instance.transform.position; float half = w * sc * 0.5f;
                var px = tex.GetPixels(); int tw = tex.width, th = tex.height; bool any = false;
                for (int y = 0; y < th; y++)
                    for (int x = 0; x < tw; x++)
                    {
                        float wx = p0.x - half + (x + 0.5f) * (w * sc / tw), wz = p0.z - half + (y + 0.5f) * (w * sc / th);
                        float pv = Paint(wx, wz); if (pv <= 0.01f) continue;
                        int k = y * tw + x; if (px[k].r < pv) { px[k].r = pv; any = true; }
                    }
                if (any) { tex.SetPixels(px); tex.Apply(); if (paintLogged++ < 3) Plugin.Log("Wege: Erd-Maske gemalt bei " + p0 + " (" + tw + "x" + th + ")"); }
            }
            catch (Exception e) { if (paintLogged++ < 3) Plugin.Warn("Wege: " + e.Message); }
        }

        public static Heightmap.Biome Biome(float x, float z)
        {
            int i = Mathf.FloorToInt((x - bx0) / bcell + 0.5f), j = Mathf.FloorToInt((z - bz0) / bcell + 0.5f);
            if (i < 0 || j < 0 || i >= BW || j >= BH) return Heightmap.Biome.Ocean;
            int c = bio[j * BW + i];
            return c < Codes.Length ? Codes[c] : Heightmap.Biome.Meadows;
        }

        public static float Forest(float x, float z)
        {
            float u = (x - bx0) / bcell, v = (z - bz0) / bcell;
            if (u < 0f || v < 0f || u > BW - 1 || v > BH - 1) return OpenForest;
            int i = Mathf.Min((int)u, BW - 2), j = Mathf.Min((int)v, BH - 2);
            float fu = u - i, fv = v - j;
            int k = j * BW + i;
            float a = forest[k], b = forest[k + 1], c = forest[k + BW], d = forest[k + BW + 1];
            return ((a + (b - a) * fu) * (1f - fv) + (c + (d - c) * fu) * fv) / 100f;
        }

        public static string Debug(Vector3 p)
        {
            if (!Active) return "map import off" + (Error != null ? " (" + Error + ")" : "") + " world=" + (ZNet.instance != null ? ZNet.instance.GetWorldName() : "-");
            return "map " + Name + " at " + p.x.ToString("F0") + "," + p.z.ToString("F0") + ": height " + Height(p.x, p.z).ToString("F1") + " biome " + Biome(p.x, p.z) + " forest " + Forest(p.x, p.z).ToString("F2")
                 + (WorldGenerator.instance != null ? " | gen height " + WorldGenerator.instance.GetHeight(p.x, p.z).ToString("F1") + " gen biome " + WorldGenerator.instance.GetBiome(p.x, p.z) : "");
        }



        // ---------- Gebaeude ----------
        // Zu jeder Karte kann eine .sites-Datei gehoeren: "x z drehung Valheim-Ort art" pro Zeile.
        // Sobald man in die Naehe kommt, setzt der Host dort einen Valheim-Ort (Holzhaeuser, Steintuerme, Ruinen) hin.
        class Site { public Vector3 P; public float Yaw; public string Loc, Kind; public bool Done; }
        static readonly System.Collections.Generic.List<Site> sites = new System.Collections.Generic.List<Site>();
        static string doneFile; static float siteT; public static int SitesSpawned;

        static void LoadSites(string map, string world)
        {
            sites.Clear(); SitesSpawned = 0;
            var pk = Pack.Current;
            var path = pk != null && !string.IsNullOrEmpty(pk.SitesFile) ? Path.Combine(pk.Dir, pk.SitesFile) : Path.Combine(Dir, map + ".sites");
            if (!File.Exists(path)) return;
            doneFile = Path.Combine(Dir, world + ".built");
            var done = new System.Collections.Generic.HashSet<int>();
            try { if (File.Exists(doneFile)) foreach (var l in File.ReadAllLines(doneFile)) { int i; if (int.TryParse(l.Trim(), out i)) done.Add(i); } } catch (Exception e) { Plugin.Quiet(e, "MapImport.done"); }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var l in File.ReadAllLines(path))
            {
                var a = l.Split(' ');
                if (a.Length < 4) continue;
                try { sites.Add(new Site { P = new Vector3(float.Parse(a[0], inv), 0f, float.Parse(a[1], inv)), Yaw = float.Parse(a[2], inv), Loc = a[3], Kind = a.Length > 4 ? a[4] : "", Done = done.Contains(sites.Count) }); }
                catch (Exception e) { Plugin.Quiet(e, "MapImport.site"); }
            }
            Plugin.Log("Karten-Import: " + sites.Count + " Gebaeude-Standorte (" + done.Count + " schon gebaut)");
        }

        static void TickSites()
        {
            if (sites.Count == 0 || Time.time < siteT) return;
            siteT = Time.time + 1.5f;
            var zs = ZoneSystem.instance; var zn = ZNet.instance; var pl = Player.m_localPlayer;
            if (zs == null || zn == null || pl == null || !zn.IsServer()) return;
            var me = pl.transform.position;
            for (int i = 0; i < sites.Count; i++)
            {
                var s = sites[i];
                if (s.Done) continue;
                float dx = s.P.x - me.x, dz = s.P.z - me.z;
                if (dx * dx + dz * dz > 160f * 160f) continue;
                if (!zs.IsZoneLoaded(ZoneSystem.GetZone(s.P))) continue;
                s.Done = true;
                bool ok = false;
                try { ClearAround(s.P, s.Kind == "castle" ? 16f : 11f); ok = SpawnAt(s.Loc, new Vector3(s.P.x, Height(s.P.x, s.P.z), s.P.z), s.Yaw); }
                catch (Exception e) { Plugin.Quiet(e, "MapImport.spawn"); }
                if (ok) SitesSpawned++;
                Plugin.Log("Karten-Import: " + s.Kind + " " + s.Loc + " bei " + s.P.x.ToString("F0") + "," + s.P.z.ToString("F0") + (ok ? "" : " - nicht moeglich"));
                try { File.AppendAllText(doneFile, i + "\n"); } catch (Exception e) { Plugin.Quiet(e, "MapImport.doneWrite"); }
                return;   // einer pro Durchlauf, damit es nicht ruckelt
            }
        }


        // Ort genau an die Stelle aus dem Originalspiel setzen (Valheims eigene Test-Funktion schiebt ihn in die Zonenmitte
        // und dreht ihn zufaellig) - so stehen Doerfer so dicht wie im Original, mehrere Haeuser pro 64-m-Zone
        static System.Reflection.MethodInfo getLoc, spawnLoc;
        static bool SpawnAt(string name, Vector3 pos, float yaw)
        {
            var zs = ZoneSystem.instance;
            if (getLoc == null) getLoc = AccessTools.Method(typeof(ZoneSystem), "GetLocation", new[] { typeof(string) });
            if (spawnLoc == null) spawnLoc = AccessTools.Method(typeof(ZoneSystem), "SpawnLocation");
            var loc = getLoc != null ? getLoc.Invoke(zs, new object[] { name }) : null;
            if (loc == null || spawnLoc == null) return false;
            var mode = Enum.Parse(spawnLoc.GetParameters()[4].ParameterType, "Full");
            var args = new object[] { loc, UnityEngine.Random.Range(0, 99999), pos, Quaternion.Euler(0f, yaw, 0f), mode, new System.Collections.Generic.List<GameObject>(), false };
            spawnLoc.Invoke(zs, args);
            return true;
        }


        // Baeume, Buesche und Felsen weg, wo das Gebaeude hinkommt (Valheim raeumt nur beim Erzeugen einer Zone)
        static void ClearAround(Vector3 p, float r)
        {
            var zn = ZNetScene.instance; if (zn == null) return;
            p.y = Height(p.x, p.z);
            var hit = new System.Collections.Generic.HashSet<ZNetView>();
            foreach (var c in Physics.OverlapSphere(p, r, ~0, QueryTriggerInteraction.Ignore))
            {
                var nv = c.GetComponentInParent<ZNetView>();
                if (nv == null || !nv.IsValid() || hit.Contains(nv)) continue;
                var go = nv.gameObject;
                if (go.GetComponent<TreeBase>() == null && go.GetComponent<TreeLog>() == null && go.GetComponent<Destructible>() == null
                    && go.GetComponent<MineRock>() == null && go.GetComponent<MineRock5>() == null && go.GetComponent<Pickable>() == null) continue;
                hit.Add(nv);
            }
            foreach (var nv in hit) { try { nv.ClaimOwnership(); zn.Destroy(nv.gameObject); } catch (Exception e) { Plugin.Quiet(e, "MapImport.Clear"); } }
        }

        public static string SitesInfo(Vector3 p)
        {
            int done = 0; Site near = null; float best = float.MaxValue;
            foreach (var s in sites) { if (s.Done) done++; float d = (s.P - new Vector3(p.x, 0f, p.z)).sqrMagnitude; if (d < best) { best = d; near = s; } }
            return "sites " + sites.Count + " built " + done + " this session " + SitesSpawned + (near != null ? " | nearest " + near.Kind + " " + near.Loc + " at " + near.P.x.ToString("F0") + "," + near.P.z.ToString("F0") + " (" + Mathf.Sqrt(best).ToString("F0") + " m)" : "");
        }

        // Fernsteuerung: Kamera fest auf einen Punkt (fuer Bilder), "look off" gibt sie zurueck
        public static bool LookOn; public static Vector3 LookPos, LookAt; public static float LookFov = 60f;

        // ---------- Fernsicht ----------
        // Valheim baut echtes Gelaende nur ein paar hundert Meter um den Spieler. Damit man auf einer importierten
        // Karte trotzdem bis zum Horizont sieht (Todesberg von der Ebene aus), legen wir ein grobes Netz der
        // ganzen Karte darunter: immer etwas tiefer als das echte Gelaende, so dass es nur dort sichtbar ist,
        // wo noch nichts geladen ist. Dazu weniger Nebel und eine weitere Sichtweite der Kamera.
        static GameObject far;
        static bool farFailed;

        public static void Tick(Camera cam)
        {
            if (!Active) { if (far != null) far.SetActive(false); return; }
            if (!Plugin.MapFarView.Value) { if (far != null) far.SetActive(false); try { TickSites(); } catch (Exception e) { Plugin.Quiet(e, "MapImport.TickSites"); } return; }
            if (cam != null && cam.farClipPlane < 14000f) cam.farClipPlane = 14000f;
            if (RenderSettings.fog && RenderSettings.fogDensity > Plugin.MapFogMax.Value) RenderSettings.fogDensity = Plugin.MapFogMax.Value;
            if (far == null && !farFailed && Player.m_localPlayer != null)
            {
                try { BuildFar(); } catch (Exception e) { farFailed = true; Plugin.Warn("Fernsicht: " + e.Message); }
            }
            if (far != null && !far.activeSelf) far.SetActive(true);
            if (LookOn && cam != null) { cam.transform.position = LookPos; cam.transform.rotation = Quaternion.LookRotation(LookAt - LookPos); cam.fieldOfView = LookFov; }
            try { TickSites(); } catch (Exception e) { Plugin.Quiet(e, "MapImport.TickSites"); }
        }

        public static void Unload()
        {
            if (far != null) UnityEngine.Object.Destroy(far);
            far = null; farFailed = false;
        }

        static Color BiomeCol(Heightmap.Biome b)
        {
            switch (b)
            {
                case Heightmap.Biome.Meadows: return new Color(0.36f, 0.52f, 0.20f);
                case Heightmap.Biome.BlackForest: return new Color(0.17f, 0.30f, 0.15f);
                case Heightmap.Biome.Swamp: return new Color(0.30f, 0.30f, 0.17f);
                case Heightmap.Biome.Mountain: return new Color(0.88f, 0.90f, 0.95f);
                case Heightmap.Biome.Plains: return new Color(0.74f, 0.64f, 0.33f);
                case Heightmap.Biome.Mistlands: return new Color(0.22f, 0.28f, 0.27f);
                case Heightmap.Biome.AshLands: return new Color(0.25f, 0.13f, 0.10f);
                case Heightmap.Biome.DeepNorth: return new Color(0.94f, 0.96f, 1.00f);
                default: return new Color(0.62f, 0.58f, 0.45f);
            }
        }

        static void BuildFar()
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            if (shader == null) throw new Exception("kein Shader");
            var mat = new Material(shader);
            const float water = 30f;
            int stride = 8;                                  // ca. 31 m pro Masche
            float step = cell * stride;
            int nx = (W - 1) / stride + 1, nz = (H - 1) / stride + 1;
            int chunk = 96;                                  // Maschen pro Teilnetz (< 65k Punkte)
            far = new GameObject("AnnoMapFarView");
            UnityEngine.Object.DontDestroyOnLoad(far);
            var sun = new Vector3(-0.45f, 0.75f, 0.35f).normalized;
            int parts = 0;
            for (int cz = 0; cz < nz - 1; cz += chunk)
                for (int cx = 0; cx < nx - 1; cx += chunk)
                {
                    int w = Mathf.Min(chunk, nx - 1 - cx) + 1, h = Mathf.Min(chunk, nz - 1 - cz) + 1;
                    var verts = new Vector3[w * h]; var cols = new Color[w * h]; var tris = new int[(w - 1) * (h - 1) * 6];
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            int gi = Mathf.Min((cx + i) * stride, W - 1), gj = Mathf.Min((cz + j) * stride, H - 1);
                            float x = x0 + gi * cell, z = z0 + gj * cell;
                            // tiefster Punkt aller angrenzenden Maschen (ganze Breite), dann noch etwas tiefer:
                            // so bleibt das grobe Netz auch an steilen Klippen (Skellige) unter dem echten Gelaende
                            float lo = float.MaxValue;
                            for (int dj = -stride; dj <= stride; dj += 2)
                                for (int di = -stride; di <= stride; di += 2)
                                {
                                    int a = Mathf.Clamp(gi + di, 0, W - 1), b = Mathf.Clamp(gj + dj, 0, H - 1);
                                    float v = hMin + hts[b * W + a] * hStep; if (v < lo) lo = v;
                                }
                            float hc = Height(x, z);
                            float y = lo - 4f;
                            float hx = Height(x + step, z) - Height(x - step, z), hz = Height(x, z + step) - Height(x, z - step);
                            var n = new Vector3(-hx, 2f * step, -hz).normalized;
                            float shade = Mathf.Clamp01(0.35f + 0.75f * Vector3.Dot(n, sun));
                            Color c;
                            if (colr != null)
                            {
                                // Bodenfarbe aus dem Original (sRGB -> linear, damit sie im Shader so aussieht wie in Witcher)
                                int bi = Mathf.Clamp((int)((x - bx0) / bcell + 0.5f), 0, BW - 1), bj = Mathf.Clamp((int)((z - bz0) / bcell + 0.5f), 0, BH - 1);
                                int o = (bj * BW + bi) * 3;
                                c = new Color(colr[o] / 255f, colr[o + 1] / 255f, colr[o + 2] / 255f).linear * 1.6f;
                            }
                            else
                            {
                                c = BiomeCol(Biome(x, z));
                                if (n.y < 0.78f) c = Color.Lerp(c, new Color(0.42f, 0.40f, 0.38f), Mathf.InverseLerp(0.78f, 0.55f, n.y));
                            }
                            // Meer: Farbe Wasser, aber Hoehe wie an Land unter dem echten Grund. Vorher lag das Netz knapp unter
                            // der Wasseroberflaeche, also UEBER dem echten Meeresgrund -> durch Valheims Wasser sah man es als
                            // helle Flaeche mit harten Kanten.
                            if (hc < water) { c = new Color(0.16f, 0.32f, 0.42f); shade = 1f; }
                            verts[j * w + i] = new Vector3(x, y, z);
                            var col = c * shade; col.a = 1f; cols[j * w + i] = col;
                        }
                    int t = 0;
                    for (int j = 0; j < h - 1; j++)
                        for (int i = 0; i < w - 1; i++)
                        {
                            int a = j * w + i;
                            tris[t++] = a; tris[t++] = a + w; tris[t++] = a + 1;
                            tris[t++] = a + 1; tris[t++] = a + w; tris[t++] = a + w + 1;
                        }
                    var m = new Mesh(); m.vertices = verts; m.colors = cols; m.triangles = tris; m.RecalculateBounds();
                    var go = new GameObject("part"); go.transform.SetParent(far.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = m;
                    var r = go.AddComponent<MeshRenderer>(); r.sharedMaterial = mat;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; r.receiveShadows = false;
                    parts++;
                }
            Plugin.Log("Fernsicht: " + parts + " Teilnetze, " + nx + "x" + nz + " Punkte, Shader " + shader.name);
        }

        // ---------- Harmony ----------
        public static void Patch(Harmony h)
        {
            try
            {
                var init = AccessTools.Method(typeof(WorldGenerator), "Initialize", new[] { typeof(World) });
                h.Patch(init, prefix: new HarmonyMethod(typeof(MapImport), nameof(InitPre)));
                var gb = AccessTools.Method(typeof(WorldGenerator), "GetBiome", new[] { typeof(float), typeof(float), typeof(float), typeof(bool) });
                h.Patch(gb, prefix: new HarmonyMethod(typeof(MapImport), nameof(BiomePre)));
                foreach (var m in AccessTools.GetDeclaredMethods(typeof(WorldGenerator)))
                    if (m.Name == "GetBiomeHeight" && m.ReturnType == typeof(float))
                        h.Patch(m, postfix: new HarmonyMethod(typeof(MapImport), nameof(HeightPost)) { priority = Priority.Last });
                var cl = AccessTools.Method(typeof(GameCamera), "LateUpdate");
                if (cl != null) h.Patch(cl, postfix: new HarmonyMethod(typeof(MapImport), nameof(CameraPost)) { priority = Priority.Last });
                try
                {
                    var rb = AccessTools.Method(typeof(Heightmap), "RebuildRenderMesh");
                    if (rb != null) h.Patch(rb, prefix: new HarmonyMethod(typeof(MapImport), nameof(PaintPre)));
                    else Plugin.Warn("Wege: Heightmap.RebuildRenderMesh fehlt");
                }
                catch (Exception e) { Plugin.Warn("Wege-Patch: " + e.Message); }
                var ff = AccessTools.Method(typeof(WorldGenerator), "GetForestFactor", new[] { typeof(Vector3) });
                h.Patch(ff, postfix: new HarmonyMethod(typeof(MapImport), nameof(ForestPost)));
                // Valheim sucht fuer manche Orte einen Zufallspunkt je Biom; fehlt ein Biom auf der Karte
                // (Skellige hat z. B. keinen Sumpf), stuerzt die Ortsverteilung ab -> dann eben kein Ort
                // (Prefix: gar nicht erst suchen, wenn das Biom keine Punkte hat; Finalizer als Netz)
                try
                {
                    var gr = AccessTools.Method(typeof(AltBiomeWorldData), "GetRandomPointByBiome");
                    if (gr != null) h.Patch(gr, prefix: new HarmonyMethod(typeof(MapImport), nameof(PointPre)), finalizer: new HarmonyMethod(typeof(MapImport), nameof(PointFin)));
                    var gs = AccessTools.Method(typeof(AltBiomeWorldData), "GetRandomSectorByBiome");
                    if (gs != null) h.Patch(gs, prefix: new HarmonyMethod(typeof(MapImport), nameof(SectorPre)));
                }
                catch (Exception e) { Plugin.Warn("Karten-Import (Biom-Punkte): " + e.Message); }
            }
            catch (Exception e) { Plugin.Warn("Karten-Import-Patch: " + e.Message); }
        }

        static void InitPre(World __0) { try { OnWorld(__0); } catch (Exception e) { Plugin.Warn("Karten-Import: " + e.Message); } }

        static bool BiomePre(float __0, float __1, ref Heightmap.Biome __result)
        {
            if (!Active) return true;
            __result = Biome(__0, __1);
            return false;
        }

        static void ForestPost(Vector3 __0, ref float __result) { if (Active) __result = Forest(__0.x, __0.z); }
        static BiomeTypeInfo Info(AltBiomeWorldData w, Heightmap.Biome b)
        {
            BiomeTypeInfo bi = null;
            if (w != null && w.Biomes != null) w.Biomes.TryGetValue(b, out bi);
            return bi;
        }
        // Punkt (0,0) = Kartenecke im Meer -> der Ort passt dort nicht hin und Valheim versucht es weiter / gibt auf
        static bool PointPre(AltBiomeWorldData __instance, Heightmap.Biome __0, ref BiomePointCoordinate __result)
        {
            if (!Active) return true;
            var bi = Info(__instance, __0);
            if (bi != null && bi.AllPoints != null && bi.AllPoints.Count > 0) return true;
            __result = new BiomePointCoordinate(0, 0);
            return false;
        }
        static Exception PointFin(Exception __exception, ref BiomePointCoordinate __result)
        {
            if (__exception != null && Active) { __result = new BiomePointCoordinate(0, 0); return null; }
            return __exception;
        }
        static bool SectorPre(AltBiomeWorldData __instance, Heightmap.Biome __0, ref BiomeSector __result)
        {
            if (!Active || BiomeSector.Empty == null) return true;
            var bi = Info(__instance, __0);
            if (bi != null && bi.Sectors != null && bi.Sectors.Count > 0) return true;
            __result = BiomeSector.Empty;
            return false;
        }
        static void HeightPost(ref float __result, float __1, float __2) { if (Active) __result = Height(__1, __2); }
        static void CameraPost(GameCamera __instance) { try { Tick(__instance.GetComponent<Camera>()); } catch (Exception e) { Plugin.Quiet(e, "MapImport.Tick"); } }
    }
}
