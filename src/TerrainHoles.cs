using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GameImporter
{
    // Gelaende-Loecher wie im Original (Witcher: Burgteile, Hoehleneingaenge, Tunnel sitzen in Loechern im Gelaende).
    // <karte>.holes neben der .vmap (tools/witcher3/w3holes.py): Bit = Loch. Aus Valheims Gelaende-Stuecken werden dort
    // die Dreiecke geschnitten - im Bild und in der Kollision. Was darunter liegt (Felsen, Mauern), bildet dann den Boden.
    public static class TerrainHoles
    {
        static byte[] bits;
        static int W, H, stride;
        static float x0, z0, cell;
        static bool[] block;              // grob: 32x32 Bits je Block, schneller Test "gibt es hier ueberhaupt Loecher"
        static int BW, BH;
        const int B = 32;
        public static int Cuts;

        static FieldInfo fRender, fColl, fCollider, fLod;

        public static bool On { get { return bits != null && MapImport.Active; } }

        public static void Clear() { bits = null; block = null; Cuts = 0; }

        public static void Load(string path)
        {
            Clear();
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var br = new BinaryReader(new BufferedStream(gz, 1 << 16)))
                {
                    if (new string(br.ReadChars(4)) != "VHOL") throw new Exception("keine .holes-Datei");
                    if (br.ReadInt32() != 1) throw new Exception("Version unbekannt");
                    W = br.ReadInt32(); H = br.ReadInt32(); x0 = br.ReadSingle(); z0 = br.ReadSingle(); cell = br.ReadSingle();
                    stride = (W + 7) / 8;
                    bits = br.ReadBytes(stride * H);
                    if (bits.Length != stride * H) throw new Exception("Datei abgeschnitten");
                }
                BW = (W + B - 1) / B; BH = (H + B - 1) / B;
                block = new bool[BW * BH];
                int n = 0;
                for (int j = 0; j < H; j++)
                    for (int bi = 0; bi < stride; bi++)
                    {
                        byte v = bits[j * stride + bi];
                        if (v == 0) continue;
                        for (int k = 0; k < 8; k++) if ((v & (0x80 >> k)) != 0) { n++; int i = bi * 8 + k; if (i < W) block[(j / B) * BW + i / B] = true; }
                    }
                Plugin.Log("Gelaende-Loecher: " + path + " (" + n + " Zellen, " + cell.ToString("F2") + " m)");
            }
            catch (Exception e) { Clear(); Plugin.Warn("Gelaende-Loecher " + path + ": " + e.Message); }
        }

        public static bool IsHole(float x, float z)
        {
            if (bits == null) return false;
            int i = Mathf.FloorToInt((x - x0) / cell), j = Mathf.FloorToInt((z - z0) / cell);
            if (i < 0 || j < 0 || i >= W || j >= H) return false;
            return (bits[j * stride + (i >> 3)] & (0x80 >> (i & 7))) != 0;
        }

        public static bool AnyHole(float minX, float minZ, float maxX, float maxZ)
        {
            if (block == null) return false;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((minX - x0) / cell) / B), i1 = Mathf.Min(BW - 1, Mathf.FloorToInt((maxX - x0) / cell) / B);
            int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ - z0) / cell) / B), j1 = Mathf.Min(BH - 1, Mathf.FloorToInt((maxZ - z0) / cell) / B);
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++) if (block[j * BW + i]) return true;
            return false;
        }

        public static void Patch(Harmony h)
        {
            try
            {
                var t = typeof(Heightmap);
                fRender = AccessTools.Field(t, "m_renderMesh"); fColl = AccessTools.Field(t, "m_collisionMesh");
                fCollider = AccessTools.Field(t, "m_collider"); fLod = AccessTools.Field(t, "m_isDistantLod");
                var rr = AccessTools.Method(t, "RebuildRenderMesh"); var rc = AccessTools.Method(t, "RebuildCollisionMesh");
                if (rr == null || rc == null || fRender == null || fColl == null) { Plugin.Warn("Gelaende-Loecher: Heightmap-API fehlt"); return; }
                h.Patch(rr, postfix: new HarmonyMethod(typeof(TerrainHoles), nameof(RenderPost)));
                h.Patch(rc, postfix: new HarmonyMethod(typeof(TerrainHoles), nameof(CollPost)));
            }
            catch (Exception e) { Plugin.Warn("Gelaende-Loecher-Patch: " + e.Message); }
        }

        static void RenderPost(Heightmap __instance)
        {
            try { if (On) Cut(__instance, fRender.GetValue(__instance) as Mesh, false); } catch (Exception e) { Plugin.Quiet(e, "TerrainHoles.Render"); }
        }

        static void CollPost(Heightmap __instance)
        {
            try { if (On) Cut(__instance, fColl.GetValue(__instance) as Mesh, true); } catch (Exception e) { Plugin.Quiet(e, "TerrainHoles.Coll"); }
        }

        static readonly List<Vector3> vbuf = new List<Vector3>();
        static readonly List<int> tbuf = new List<int>(), keep = new List<int>();

        static void Cut(Heightmap hm, Mesh mesh, bool collision)
        {
            if (mesh == null || hm == null) return;
            if (fLod != null && (bool)fLod.GetValue(hm)) return;              // ferne Gelaende-Stufe: grob, bleibt zu
            var tr = hm.transform;
            var b = mesh.bounds;
            Vector3 lo = tr.TransformPoint(b.min), hi = tr.TransformPoint(b.max);
            if (!AnyHole(Mathf.Min(lo.x, hi.x), Mathf.Min(lo.z, hi.z), Mathf.Max(lo.x, hi.x), Mathf.Max(lo.z, hi.z))) return;
            vbuf.Clear(); mesh.GetVertices(vbuf);
            int cut = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                tbuf.Clear(); mesh.GetTriangles(tbuf, s);
                keep.Clear();
                for (int i = 0; i + 2 < tbuf.Count; i += 3)
                {
                    var c = tr.TransformPoint((vbuf[tbuf[i]] + vbuf[tbuf[i + 1]] + vbuf[tbuf[i + 2]]) / 3f);
                    if (IsHole(c.x, c.z)) { cut++; continue; }
                    keep.Add(tbuf[i]); keep.Add(tbuf[i + 1]); keep.Add(tbuf[i + 2]);
                }
                if (keep.Count != tbuf.Count) mesh.SetTriangles(keep, s, false);
            }
            if (cut == 0) return;
            Cuts += cut;
            if (collision && fCollider != null)
            {
                var mc = fCollider.GetValue(hm) as MeshCollider;
                if (mc != null) { mc.sharedMesh = null; mc.sharedMesh = mesh; }
            }
        }

        public static string Info(Vector3 p)
        {
            return "holes " + (bits != null ? W + "x" + H + " " + cell.ToString("F2") + " m" : "keine") + ", geschnitten " + Cuts + " Dreiecke, hier Loch: " + IsHole(p.x, p.z);
        }
    }
}
