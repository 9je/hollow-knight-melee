using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FoxMelee {
    /// fox.model v2 (written by ssbmhk-extract-fox from the player's ISO): model-space vertices with up to four
    /// bone weights, inverse binds, one texture atlas (wrap modes baked in), triangles tagged with their mesh
    /// part. Pose = linear-blend skinning with Melee's joint world matrices (mc_read_bones); parts Melee hides
    /// (mc_read_dobjs) are skipped and the rest drawn back to front (no depth-writing shader needed).
    public sealed class FoxModel {
        public int BoneCount;
        public float[] InvBind, Pos, Nrm, Uv, Weight;
        public byte[] Bone;
        public int AtlasW, AtlasH;
        public byte[] AtlasRgba;
        public byte[] PartJoint, PartLocal;
        public int[] Tris;          // 3 per triangle
        public ushort[] TriPart;
        float[] skin, keys;
        int[] triIds;
        Dictionary<int, int> partOf;   // joint << 8 | local -> part

        public static FoxModel Load(string path) {
            using var r = new BinaryReader(File.OpenRead(path));
            try {
                if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "FOXMODEL" || r.ReadUInt32() != 2)
                    throw new InvalidDataException($"{path} is not a FOXMODEL v2 file");
                var m = new FoxModel { BoneCount = (int)r.ReadUInt32() };
                m.InvBind = Floats(r, m.BoneCount * 12);
                int n = (int)r.ReadUInt32();
                m.Pos = new float[n * 3]; m.Nrm = new float[n * 3]; m.Uv = new float[n * 2]; m.Bone = new byte[n * 4]; m.Weight = new float[n * 4];
                for (int i = 0; i < n; i++) {
                    for (int k = 0; k < 3; k++) m.Pos[3 * i + k] = r.ReadSingle();
                    for (int k = 0; k < 3; k++) m.Nrm[3 * i + k] = r.ReadSingle();
                    for (int k = 0; k < 2; k++) m.Uv[2 * i + k] = r.ReadSingle();
                    for (int k = 0; k < 4; k++) m.Bone[4 * i + k] = r.ReadByte();
                    for (int k = 0; k < 4; k++) m.Weight[4 * i + k] = r.ReadSingle();
                    for (int k = 0; k < 4; k++) if (m.Weight[4 * i + k] != 0 && m.Bone[4 * i + k] >= m.BoneCount) throw new InvalidDataException($"vertex {i} names bone {m.Bone[4 * i + k]}");
                }
                m.AtlasW = (int)r.ReadUInt32(); m.AtlasH = (int)r.ReadUInt32();
                m.AtlasRgba = Exactly(r, m.AtlasW * m.AtlasH * 4);
                int np = (int)r.ReadUInt32();
                m.PartJoint = new byte[np]; m.PartLocal = new byte[np];
                m.partOf = new Dictionary<int, int>();
                for (int p = 0; p < np; p++) { m.PartJoint[p] = r.ReadByte(); m.PartLocal[p] = r.ReadByte(); m.partOf[m.PartJoint[p] << 8 | m.PartLocal[p]] = p; }
                int nt = (int)r.ReadUInt32();
                m.Tris = new int[3 * nt]; m.TriPart = new ushort[nt];
                for (int t = 0; t < nt; t++) {
                    for (int k = 0; k < 3; k++) { m.Tris[3 * t + k] = (int)r.ReadUInt32(); if (m.Tris[3 * t + k] >= n) throw new InvalidDataException($"triangle {t} out of range"); }
                    m.TriPart[t] = r.ReadUInt16(); r.ReadUInt16();
                    if (m.TriPart[t] >= np) throw new InvalidDataException($"triangle {t} names part {m.TriPart[t]}");
                }
                m.skin = new float[m.BoneCount * 12];
                m.keys = new float[nt];
                m.triIds = new int[nt];
                return m;
            } catch (EndOfStreamException) {
                throw new InvalidDataException($"{path} is truncated (not a complete FOXMODEL file)");
            }
        }

        static float[] Floats(BinaryReader r, int n) { var f = new float[n]; for (int i = 0; i < n; i++) f[i] = r.ReadSingle(); return f; }
        static byte[] Exactly(BinaryReader r, int n) { var b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return b; }

        /// outPos[3i..] = sum_k w_k * (bones[b_k] * invBind[b_k]) * pos[i] (3x4 row-major matrices).
        public void Pose(float[] bones, int boneCount, float[] outPos) {
            if (boneCount != BoneCount) throw new ArgumentException($"skeleton has {boneCount} bones, the model {BoneCount}");
            for (int b = 0; b < BoneCount; b++)
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 4; j++) {
                        float s = j == 3 ? bones[12 * b + 4 * i + 3] : 0f;
                        for (int k = 0; k < 3; k++) s += bones[12 * b + 4 * i + k] * InvBind[12 * b + 4 * k + j];
                        skin[12 * b + 4 * i + j] = s;
                    }
            int n = Pos.Length / 3;
            for (int v = 0; v < n; v++) {
                float x = Pos[3 * v], y = Pos[3 * v + 1], z = Pos[3 * v + 2], ox = 0, oy = 0, oz = 0;
                for (int k = 0; k < 4; k++) {
                    float w = Weight[4 * v + k];
                    if (w == 0) continue;
                    int o = 12 * Bone[4 * v + k];
                    ox += w * (skin[o] * x + skin[o + 1] * y + skin[o + 2] * z + skin[o + 3]);
                    oy += w * (skin[o + 4] * x + skin[o + 5] * y + skin[o + 6] * z + skin[o + 7]);
                    oz += w * (skin[o + 8] * x + skin[o + 9] * y + skin[o + 10] * z + skin[o + 11]);
                }
                outPos[3 * v] = ox; outPos[3 * v + 1] = oy; outPos[3 * v + 2] = oz;
            }
        }

        /// The core's DObj flags (mc_read_dobjs: hidden, joint per entry) as one flag per model part. The game
        /// appends its own parts after a joint's file parts, so (joint, index within the joint) names a part.
        public bool[] MapHidden(byte[] hidden, byte[] joint, int n) {
            var partHidden = new bool[PartJoint.Length];
            var local = new int[256];
            for (int i = 0; i < n; i++) {
                int key = joint[i] << 8 | local[joint[i]]++;
                if (partOf.TryGetValue(key, out int p)) partHidden[p] = hidden[i] != 0;
            }
            return partHidden;
        }

        /// Visible triangles' indices into outIdx, far (Melee -z) to near: drawn in this order with an alpha-blended,
        /// non-depth-writing material, nearer triangles cover farther ones. Returns the number of indices.
        public int DrawOrder(float[] posed, bool[] partHidden, int[] outIdx) {
            int m = 0;
            for (int t = 0; t < TriPart.Length; t++) {
                if (partHidden != null && partHidden[TriPart[t]]) continue;
                keys[m] = posed[3 * Tris[3 * t] + 2] + posed[3 * Tris[3 * t + 1] + 2] + posed[3 * Tris[3 * t + 2] + 2];
                triIds[m++] = t;
            }
            Array.Sort(keys, triIds, 0, m);
            for (int i = 0; i < m; i++)
                for (int k = 0; k < 3; k++) outIdx[3 * i + k] = Tris[3 * triIds[i] + k];
            return 3 * m;
        }
    }
}
