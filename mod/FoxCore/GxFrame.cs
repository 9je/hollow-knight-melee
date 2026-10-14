using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FoxMelee {
    /// One run of Melee's decoded frame: `Count` vertices (whole triangles) from `First`, one texture and blend mode,
    /// drawn in order (the frame is sorted back to front). Tex: a texture id, GxFrame.Atlas or -1 (none).
    public struct GxRun { public int First, Count, Tex; public byte Blend, PLink, WrapS, WrapT; }

    /// A texture a run uses: RGBA8, top row first, owned by the core; Gen changes when the id's content does.
    public struct GxTexture { public int W, H; public System.IntPtr Rgba; public uint Gen; }

    /// Melee's frame as drawn by its own graphics code (core mc_gx_decode): positions in Melee world units, colours
    /// to multiply with the texture, texture coordinates (wraps already applied for the atlas).
    public sealed class GxFrame {
        public const int Atlas = 0x40000000;
        public const byte Opaque = 0, Alpha = 1, Add = 2, Multiply = 3;   // Multiply: a decal pass, the frame so far times the texture
        /// GObj p_links: fighters (8), items (9), effects (11, 12).
        public const ulong FoxAndEffects = (1ul << 8) | (1ul << 9) | (1ul << 11) | (1ul << 12);

        public float[] Pos = new float[0];    // 3 per vertex
        public byte[] Color = new byte[0];    // RGBA per vertex
        public float[] Uv = new float[0];     // 2 per vertex
        public int VertexCount;
        public readonly List<GxRun> Runs = new List<GxRun>();

        [StructLayout(LayoutKind.Sequential)]
        internal struct Vert { public float X, Y, Z; public byte R, G, B, A; public float U, V; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Run { public uint First, Count; public int Tex; public byte Blend, PLink, WrapS, WrapT; public uint Gobj; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Texture { public uint W, H; public System.IntPtr Rgba; public uint Gen; }

        internal unsafe void Fill(Vert* v, int nv, Run* r, int nr) {
            if (Pos.Length < 3 * nv) { Pos = new float[3 * nv]; Color = new byte[4 * nv]; Uv = new float[2 * nv]; }
            for (int i = 0; i < nv; i++) {
                Pos[3 * i] = v[i].X; Pos[3 * i + 1] = v[i].Y; Pos[3 * i + 2] = v[i].Z;
                Color[4 * i] = v[i].R; Color[4 * i + 1] = v[i].G; Color[4 * i + 2] = v[i].B; Color[4 * i + 3] = v[i].A;
                Uv[2 * i] = v[i].U; Uv[2 * i + 1] = v[i].V;
            }
            VertexCount = nv;
            Runs.Clear();
            for (int i = 0; i < nr; i++)
                Runs.Add(new GxRun { First = (int)r[i].First, Count = (int)r[i].Count, Tex = r[i].Tex, Blend = r[i].Blend, PLink = r[i].PLink, WrapS = r[i].WrapS, WrapT = r[i].WrapT });
        }
    }
}
