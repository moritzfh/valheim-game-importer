using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameImporter
{
    // Bauwerke aus echten Valheim-Bauteilen (Steinwand, Holzwand ...), nur als Optik + Kollision, ohne Netzwerk.
    // statics_*.json: {"kit": [["stone_wall_4x2", x, y, z, gier], ...], "p": [x, y, z], "r": radius}
    //   x/y/z = Mitte des Bauteils (Mitte seiner Huelle) in Weltkoordinaten, gier in Grad um y.
    // Alle Teile einer Kachel werden je Material zu einem Mesh zusammengefasst (wenige Draw-Calls, auch bei 10 000 Steinen).
    public static class Kit
    {
        class Part { public Mesh Mesh; public int Sub; public Material Mat; public Matrix4x4 Rel; }
        static readonly Dictionary<string, List<Part>> parts = new Dictionary<string, List<Part>>();

        static List<Part> Parts(string pf)
        {
            List<Part> l;
            if (parts.TryGetValue(pf, out l)) return l;
            l = new List<Part>(); parts[pf] = l;
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(pf) : null;
            if (go == null) { Plugin.Warn("Bauteil fehlt: " + pf); return l; }
            // nur der heile Zustand (WearNTear.m_new) und davon die feinste Stufe (LOD0)
            Transform root = go.transform;
            var wnt = go.GetComponent<WearNTear>();
            if (wnt != null && wnt.m_new != null) root = wnt.m_new.transform;
            var rs = new List<Renderer>();
            var lg = root.GetComponentInChildren<LODGroup>(true);
            if (lg != null && lg.GetLODs().Length > 0) rs.AddRange(lg.GetLODs()[0].renderers);
            else foreach (var r in root.GetComponentsInChildren<MeshRenderer>(false)) rs.Add(r);
            var inv = go.transform.worldToLocalMatrix;
            var b = new Bounds(); bool hb = false;
            var tmp = new List<KeyValuePair<Renderer, Matrix4x4>>();
            foreach (var r in rs)
            {
                if (r == null || !(r is MeshRenderer)) continue;
                var n = r.gameObject.name.ToLowerInvariant();
                if (n.Contains("snow") || n.Contains("collider")) continue;
                var mf = r.GetComponent<MeshFilter>(); if (mf == null || mf.sharedMesh == null) continue;
                var m = inv * r.transform.localToWorldMatrix;
                tmp.Add(new KeyValuePair<Renderer, Matrix4x4>(r, m));
                var mb = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var c = mb.center + Vector3.Scale(mb.extents, new Vector3((k & 1) == 0 ? -1 : 1, (k & 2) == 0 ? -1 : 1, (k & 4) == 0 ? -1 : 1));
                    var w = m.MultiplyPoint3x4(c);
                    if (!hb) { b = new Bounds(w, Vector3.zero); hb = true; } else b.Encapsulate(w);
                }
            }
            var toCenter = Matrix4x4.Translate(-b.center);
            foreach (var kv in tmp)
            {
                var mesh = kv.Key.GetComponent<MeshFilter>().sharedMesh; var mats = kv.Key.sharedMaterials;
                for (int si = 0; si < mesh.subMeshCount && si < mats.Length; si++)
                {
                    if (mats[si] == null) continue;
                    l.Add(new Part { Mesh = mesh, Sub = si, Mat = NoSnowCopy(mats[si]), Rel = toCenter * kv.Value });
                }
            }
            Plugin.Log("Bauteil " + pf + ": " + l.Count + " Teile, Groesse " + b.size.ToString("F2"));
            return l;
        }

        static readonly Dictionary<Material, Material> noSnow = new Dictionary<Material, Material>();
        static Material NoSnowCopy(Material m)
        {
            Material c;
            if (noSnow.TryGetValue(m, out c)) return c;
            c = new Material(m) { name = m.name + "_gi" }; Glb.NoSnow(c); noSnow[m] = c; return c;
        }

        static float F(object o) { return o is double ? (float)(double)o : Convert.ToSingle(o); }

        public static GameObject Build(List<object> kit, Vector3 origin)
        {
            var byMat = new Dictionary<Material, List<CombineInstance>>();
            foreach (var e in kit)
            {
                var a = e as List<object>; if (a == null || a.Count < 4) continue;
                var ps = Parts(a[0] as string ?? "");
                if (ps.Count == 0) continue;
                var t = Matrix4x4.TRS(new Vector3(F(a[1]), F(a[2]), F(a[3])) - origin, Quaternion.Euler(0f, a.Count > 4 ? F(a[4]) : 0f, 0f), Vector3.one);
                foreach (var p in ps)
                {
                    List<CombineInstance> l; if (!byMat.TryGetValue(p.Mat, out l)) byMat[p.Mat] = l = new List<CombineInstance>();
                    l.Add(new CombineInstance { mesh = p.Mesh, subMeshIndex = p.Sub, transform = t * p.Rel });
                }
            }
            if (byMat.Count == 0) return null;
            var root = new GameObject("gi_kit");
            foreach (var kv in byMat)
            {
                var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32, name = "kit_" + kv.Key.name };
                m.CombineMeshes(kv.Value.ToArray(), true, true);
                var g = new GameObject(kv.Key.name); g.transform.SetParent(root.transform, false);
                g.AddComponent<MeshFilter>().sharedMesh = m;
                g.AddComponent<MeshRenderer>().sharedMaterial = kv.Key;
            }
            return root;
        }

        public static void Clear() { parts.Clear(); }
    }
}
