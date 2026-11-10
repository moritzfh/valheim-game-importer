using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GameImporter
{
    // Original-Animation eines Cutscene-Darstellers auf ein Valheim-Skelett uebertragen (Retargeting), Bild fuer Bild.
    // Daten: <cutscene>.<rolle>.bones von tools/witcher3/w3cutscene.py (actor_bones). Je Gelenk und Bild: Position (mm relativ zur
    // Huefte), gedrehte Koerper-Vorwaerts-/Hochrichtung (f, u) und Richtung vom natuerlichen Elternknochen (l). Alles in Weltkoordinaten.
    // Uebertragung ueber Richtungen statt Drehungen: so ist es egal, ob die Ruheposen (T-/A-Pose) und Knochenachsen der beiden Skelette
    // gleich sind. Rumpf/Kopf: hoch + vorwaerts. Arme/Beine: Richtung zum Kindgelenk + vorwaerts (Fuesse/Zehen: + hoch).
    public class BoneAnim
    {
        public float Fps; public int Frames; public string[] Names; public float HipH, HeadH;
        public Vector3[] Pelvis; public float[] RootY;
        public Vector3[] Pos, F, U, L;          // [frame * nj + j]

        public static BoneAnim Load(string path)
        {
            using (var br = new BinaryReader(File.OpenRead(path)))
            {
                var magic = new string(br.ReadChars(4));
                if (magic != "GIB1" && magic != "GIB2") throw new Exception("keine Knochendatei: " + path);
                var b = new BoneAnim { Fps = br.ReadSingle(), Frames = br.ReadInt32() };
                int nj = br.ReadInt32(); b.HipH = br.ReadSingle();
                if (magic == "GIB2") b.HeadH = br.ReadSingle();
                b.Names = new string[nj];
                for (int j = 0; j < nj; j++) { int len = br.ReadByte(); b.Names[j] = new string(br.ReadChars(len)); }
                int n = b.Frames;
                b.Pelvis = new Vector3[n]; b.RootY = new float[n];
                b.Pos = new Vector3[n * nj]; b.F = new Vector3[n * nj]; b.U = new Vector3[n * nj]; b.L = new Vector3[n * nj];
                const float D = 1f / 32767f;
                for (int i = 0; i < n; i++)
                {
                    b.Pelvis[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()); b.RootY[i] = br.ReadSingle();
                    for (int j = 0; j < nj; j++)
                    {
                        int k = i * nj + j;
                        b.Pos[k] = new Vector3(br.ReadInt16(), br.ReadInt16(), br.ReadInt16()) * 0.001f;
                        b.F[k] = new Vector3(br.ReadInt16(), br.ReadInt16(), br.ReadInt16()) * D;
                        b.U[k] = new Vector3(br.ReadInt16(), br.ReadInt16(), br.ReadInt16()) * D;
                        b.L[k] = new Vector3(br.ReadInt16(), br.ReadInt16(), br.ReadInt16()) * D;
                    }
                }
                return b;
            }
        }
    }

    public class BoneRig
    {
        public Vector3 Offset;
        public bool ExactHip;
        float hipW0, headW0, bHip, bHead;
        // Reiter: Groesse so, dass Huefte->Kopf so lang ist wie im Original (Beine zaehlen im Sattel nicht)
        public void SetRider()
        {
            ExactHip = true;
            if (root == null || headW0 - hipW0 < 0.2f || bHead - bHip < 0.2f) return;
            float k = Mathf.Clamp((bHead - bHip) / (headW0 - hipW0), 0.6f, 1.6f);
            root.localScale = rootScale * k;
            Info += ", Reiter x" + k.ToString("F2");
        }            // Reiter: Huefte genau auf die Originalhoehe (Sattel), nicht mit der Beinlaenge skalieren           // Verschiebung der ganzen Figur (z. B. Valheim-Koerper ist dicker -> etwas weg von der Wannenwand)
        static readonly HashSet<string> Trunk = new HashSet<string> { "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head" };
        static readonly Dictionary<string, string> ChildOf = new Dictionary<string, string> {
            { "LeftShoulder", "LeftUpperArm" }, { "LeftUpperArm", "LeftLowerArm" }, { "LeftLowerArm", "LeftHand" },
            { "RightShoulder", "RightUpperArm" }, { "RightUpperArm", "RightLowerArm" }, { "RightLowerArm", "RightHand" },
            { "LeftUpperLeg", "LeftLowerLeg" }, { "LeftLowerLeg", "LeftFoot" }, { "LeftFoot", "LeftToes" },
            { "RightUpperLeg", "RightLowerLeg" }, { "RightLowerLeg", "RightFoot" }, { "RightFoot", "RightToes" } };
        static readonly Dictionary<string, string> ParentOf = new Dictionary<string, string> {
            { "LeftShoulder", "UpperChest" }, { "LeftUpperArm", "LeftShoulder" }, { "LeftLowerArm", "LeftUpperArm" }, { "LeftHand", "LeftLowerArm" },
            { "RightShoulder", "UpperChest" }, { "RightUpperArm", "RightShoulder" }, { "RightLowerArm", "RightUpperArm" }, { "RightHand", "RightLowerArm" },
            { "LeftUpperLeg", "Hips" }, { "LeftLowerLeg", "LeftUpperLeg" }, { "LeftFoot", "LeftLowerLeg" }, { "LeftToes", "LeftFoot" },
            { "RightUpperLeg", "Hips" }, { "RightLowerLeg", "RightUpperLeg" }, { "RightFoot", "RightLowerLeg" }, { "RightToes", "RightFoot" } };

        Transform[] T; int[] child; bool[] trunk, useUp; Quaternion[] basisInv;
        int hips = -1; float scale = 1f; int nj;
        Animator anim; readonly List<Behaviour> off = new List<Behaviour>();
        Transform root; Vector3 rootScale;
        public string Info = "";

        public static BoneRig Build(GameObject go, BoneAnim b)
        {
            Animator an = null;
            foreach (var a in go.GetComponentsInChildren<Animator>(true)) if (a.isHuman && a.avatar != null) { an = a; break; }
            if (an == null) return null;
            var r = new BoneRig { anim = an, nj = b.Names.Length };
            r.T = new Transform[r.nj]; r.child = new int[r.nj]; r.trunk = new bool[r.nj]; r.useUp = new bool[r.nj]; r.basisInv = new Quaternion[r.nj];
            var idx = new Dictionary<string, int>();
            for (int j = 0; j < r.nj; j++)
            {
                idx[b.Names[j]] = j;
                HumanBodyBones hb;
                try { hb = (HumanBodyBones)Enum.Parse(typeof(HumanBodyBones), b.Names[j]); r.T[j] = an.GetBoneTransform(hb); } catch { r.T[j] = null; }
                if (b.Names[j] == "Hips") r.hips = j;
            }
            Func<string, Transform> TB = nm => { int k; return idx.TryGetValue(nm, out k) ? r.T[k] : null; };
            // Ruhepose (Bindpose) des Valheim-Koerpers im Mesh-Raum
            SkinnedMeshRenderer smr = null; var hipsT = TB("Hips");
            // Koerper selbst (Kleidungsstuecke haben eigene Bindposen/Massstaebe)
            var ve = go.GetComponentInChildren<VisEquipment>(true);
            var fb = ve != null ? HarmonyLib.AccessTools.Field(typeof(VisEquipment), "m_bodyModel") : null;
            var body = fb != null ? fb.GetValue(ve) as SkinnedMeshRenderer : null;
            if (body != null && body.sharedMesh != null && body.bones != null && Array.IndexOf(body.bones, hipsT) >= 0) smr = body;
            if (smr == null) foreach (var s in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (s.sharedMesh != null && s.bones != null && Array.IndexOf(s.bones, hipsT) >= 0 && (smr == null || s.bones.Length > smr.bones.Length)) smr = s;
            if (smr == null || hipsT == null) return null;
            var bindM = new Dictionary<Transform, Matrix4x4>();
            var bp = smr.sharedMesh.bindposes;
            for (int i = 0; i < smr.bones.Length && i < bp.Length; i++) if (smr.bones[i] != null) bindM[smr.bones[i]] = bp[i].inverse;
            Func<Transform, Matrix4x4> bind = null;
            bind = t =>
            {
                Matrix4x4 m;
                if (t == null) return Matrix4x4.identity;
                if (bindM.TryGetValue(t, out m)) return m;
                if (t.parent == null || t == go.transform) return Matrix4x4.identity;
                m = bind(t.parent) * Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
                bindM[t] = m; return m;
            };
            Func<Transform, Vector3> P = t => bind(t).GetColumn(3);
            Func<Transform, Quaternion> Q = t => bind(t).rotation;
            var headT = TB("Head");
            var up = headT != null ? (P(headT) - P(hipsT)).normalized : Vector3.up;
            Vector3 fwd = Vector3.zero;
            foreach (var s in new[] { "Left", "Right" })
            {
                var f = TB(s + "Foot"); var t = TB(s + "Toes");
                if (f != null && t != null) fwd += P(t) - P(f);
            }
            fwd = Vector3.ProjectOnPlane(fwd, up);
            if (fwd.sqrMagnitude < 1e-6f && TB("LeftUpperLeg") != null && TB("RightUpperLeg") != null)
                fwd = Vector3.Cross(P(TB("RightUpperLeg")) - P(TB("LeftUpperLeg")), up);
            fwd = Vector3.ProjectOnPlane(fwd, up).normalized;
            if (fwd.sqrMagnitude < 0.5f) return null;
            // Hueftenhoehe ueber den Fuessen -> Groessenverhaeltnis zur Originalfigur
            // Groesse: Valheim-Figuren sind kleiner als Witchers und haben kuerzere Beine. Ganze Figur so skalieren, dass der Kopf
            // so hoch ist wie im Original (dann passen Gesichter in die Kameraeinstellungen); Huefte senkrecht im Verhaeltnis der
            // Beinlaengen setzen (sonst schwebt der Kopf zu hoch).
            var lf = TB("LeftFoot"); var rf = TB("RightFoot");
            r.root = go.transform; r.rootScale = go.transform.localScale;
            if (lf != null && rf != null && b.HipH > 0.1f)
            {
                var feetM = (P(lf) + P(rf)) * 0.5f;
                float hipW = smr.transform.TransformVector(up * Vector3.Dot(P(hipsT) - feetM, up)).magnitude;
                float headW = headT != null ? smr.transform.TransformVector(up * Vector3.Dot(P(headT) - feetM, up)).magnitude : 0f;
                float k = (b.HeadH > 0.3f && headW > 0.3f) ? Mathf.Clamp(b.HeadH / headW, 0.7f, 1.5f) : 1f;
                r.hipW0 = hipW; r.headW0 = headW; r.bHip = b.HipH; r.bHead = b.HeadH;
                if (Mathf.Abs(k - 1f) > 0.02f) go.transform.localScale = r.rootScale * k;
                r.scale = Mathf.Clamp(hipW * k / b.HipH, 0.5f, 2f);
            }
            for (int j = 0; j < r.nj; j++)
            {
                r.child[j] = -1;
                var t = r.T[j]; if (t == null) continue;
                string nm = b.Names[j];
                Vector3 pv, sv;
                if (Trunk.Contains(nm)) { r.trunk[j] = true; pv = up; sv = fwd; }
                else
                {
                    string cn; int cj;
                    if (ChildOf.TryGetValue(nm, out cn) && idx.TryGetValue(cn, out cj) && r.T[cj] != null) { r.child[j] = cj; pv = P(r.T[cj]) - P(t); }
                    else
                    {
                        string pn; var pt = ParentOf.TryGetValue(nm, out pn) ? TB(pn) : null;
                        if (pt == null) pt = t.parent;
                        pv = P(t) - P(pt);
                    }
                    if (pv.sqrMagnitude < 1e-8f) { r.T[j] = null; continue; }
                    r.useUp[j] = Mathf.Abs(Vector3.Dot(pv.normalized, fwd)) > 0.7f;
                    sv = r.useUp[j] ? up : fwd;
                }
                var qi = Quaternion.Inverse(Q(t));
                var pl = qi * pv; var sl = qi * sv;
                if (Vector3.Cross(pl.normalized, sl.normalized).sqrMagnitude < 1e-4f) { r.T[j] = null; continue; }
                r.basisInv[j] = Quaternion.Inverse(Quaternion.LookRotation(pl, sl));
            }
            // Valheims eigene Animation und Kopf-Blicksteuerung aus, solange die Cutscene die Knochen fuehrt
            an.enabled = false;
            foreach (var c in go.GetComponentsInChildren<CharacterAnimEvent>(true)) if (c.enabled) { c.enabled = false; r.off.Add(c); }
            int ok = 0; foreach (var t in r.T) if (t != null) ok++;
            r.Info = ok + "/" + r.nj + " Knochen, vergroessert x" + (go.transform.localScale.y / Mathf.Max(0.01f, r.rootScale.y)).ToString("F2");
            return r;
        }

        static Vector3 N(Vector3 a, Vector3 b, float u) { var v = Vector3.Lerp(a, b, u); return v.sqrMagnitude > 1e-8f ? v.normalized : a; }

        // Pose zum Zeitpunkt zwischen Bild i und j (u = Anteil von j)
        public void Apply(BoneAnim b, int i, int j, float u)
        {
            int ni = i * nj, nj2 = j * nj;
            var pel = Vector3.Lerp(b.Pelvis[i], b.Pelvis[j], u);
            float ry = Floor(b, pel.y, Mathf.Lerp(b.RootY[i], b.RootY[j], u));
            for (int k = 0; k < nj; k++)
            {
                var t = T[k]; if (t == null) continue;
                Vector3 p, s;
                if (trunk[k]) p = N(b.U[ni + k], b.U[nj2 + k], u);
                else if (child[k] >= 0)
                {
                    int c = child[k];
                    p = Vector3.Lerp(b.Pos[ni + c] - b.Pos[ni + k], b.Pos[nj2 + c] - b.Pos[nj2 + k], u);
                    if (p.sqrMagnitude < 1e-8f) p = N(b.L[ni + c], b.L[nj2 + c], u);
                }
                else p = N(b.L[ni + k], b.L[nj2 + k], u);
                s = (trunk[k] || !useUp[k]) ? N(b.F[ni + k], b.F[nj2 + k], u) : N(b.U[ni + k], b.U[nj2 + k], u);
                if (Vector3.Cross(p, s).sqrMagnitude < 1e-6f) continue;
                t.rotation = Quaternion.LookRotation(p, s) * basisInv[k];
                if (k == hips) t.position = (ExactHip ? pel : new Vector3(pel.x, ry + scale * (pel.y - ry), pel.z)) + Offset;
            }
        }

        // Boden unter der Figur. Normal ist das der Wurzelknochen. Bleibt die Wurzel aber unten, waehrend die Figur oben
        // (Dach, oberes Stockwerk) spielt, wuerde die Koerperskalierung den ganzen Hoehenunterschied mitskalieren und die Figur
        // um Meter nach unten versetzen (z. B. 20 m * 0,92) -> dann den Boden aus der Huefthoehe schaetzen.
        public static float Floor(BoneAnim b, float pelY, float rootY)
        {
            float hh = b.HipH > 0.3f ? b.HipH : 0.95f;
            return pelY - rootY > 2f * hh ? pelY - hh : rootY;
        }

        // Kinderproportionen: Kopf groesser (Valheim hat keine Kinderkoerper)
        Transform headT; Vector3 headScale0;
        public void SetHeadScale(float k)
        {
            if (anim == null || Mathf.Abs(k - 1f) < 0.01f) return;
            headT = anim.GetBoneTransform(HumanBodyBones.Head);
            if (headT == null) return;
            headScale0 = headT.localScale; headT.localScale = headScale0 * k;
        }

        public void Restore()
        {
            if (headT != null) { headT.localScale = headScale0; headT = null; }
            if (anim != null) anim.enabled = true;
            if (root != null) root.localScale = rootScale;
            foreach (var c in off) if (c != null) c.enabled = true;
            off.Clear();
        }
    }
}
