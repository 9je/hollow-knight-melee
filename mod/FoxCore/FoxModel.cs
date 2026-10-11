using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FoxMelee {
    /// fox.model (written by ssbmhk-extract-fox from the player's ISO): model-space vertices with up to four
    /// bone weights, inverse binds, RGBA textures, parts. Pose = linear-blend skinning with Melee's joint
    /// world matrices (mc_read_bones).
    public sealed class FoxModel {
        public int BoneCount;
        public float[] InvBind, Pos, Nrm, Uv, Weight;
        public byte[] Bone;
        public List<(int W, int H, byte[] Rgba)> Textures = new List<(int, int, byte[])>();
        public List<(int Texture, int First, int Count, byte[] Colour)> Parts = new List<(int, int, int, byte[])>();
        public int[] Indices;
        float[] skin;

        public static FoxModel Load(string path) {
            using var r = new BinaryReader(File.OpenRead(path));
            try {
                if (Encoding.ASCII.GetString(r.ReadBytes(8)) != "FOXMODEL" || r.ReadUInt32() != 1)
                    throw new InvalidDataException($"{path} is not a FOXMODEL v1 file");
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
                int nt = (int)r.ReadUInt32();
                for (int i = 0; i < nt; i++) { int w = (int)r.ReadUInt32(), h = (int)r.ReadUInt32(); m.Textures.Add((w, h, r.ReadBytes(w * h * 4))); }
                int np = (int)r.ReadUInt32();
                for (int i = 0; i < np; i++) m.Parts.Add((r.ReadInt32(), (int)r.ReadUInt32(), (int)r.ReadUInt32(), r.ReadBytes(4)));
                int ni = (int)r.ReadUInt32();
                m.Indices = new int[ni];
                for (int i = 0; i < ni; i++) { m.Indices[i] = (int)r.ReadUInt32(); if (m.Indices[i] >= n) throw new InvalidDataException($"index {i} out of range"); }
                m.skin = new float[m.BoneCount * 12];
                return m;
            } catch (EndOfStreamException) {
                throw new InvalidDataException($"{path} is truncated (not a complete FOXMODEL file)");
            }
        }

        static float[] Floats(BinaryReader r, int n) { var f = new float[n]; for (int i = 0; i < n; i++) f[i] = r.ReadSingle(); return f; }

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
    }
}
