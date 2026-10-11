using System;
using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Draws Melee's own frame (Fox, his items and effects, as Melee's graphics code drew them: core GX capture) in the
    /// room: one mesh with a sub-mesh per run, which Unity draws in the core's back-to-front order (Hollow Knight's
    /// shaders write no depth). The one renderer sorts against the room as the Knight's own does (his sorting layer,
    /// order and depth), so Fox stays behind the room's foreground whatever effects are showing. Textures are uploaded
    /// once and again only when the core says they changed.
    sealed class GxRenderer {
        static readonly Color AdditiveTint = new Color(0.5f, 0.5f, 0.5f, 0.5f);   // Particles/Additive doubles colour x tint

        readonly GxFrame frame = new GxFrame();
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Color32> c = new List<Color32>();
        readonly List<Vector2> u = new List<Vector2>();
        readonly List<int> idx = new List<int>();
        Material[] mats = new Material[0];
        readonly Dictionary<(int id, int ws, int wt), (uint gen, Texture2D tex)> textures = new Dictionary<(int, int, int), (uint, Texture2D)>();
        readonly Dictionary<(Texture2D tex, byte blend), Material> materials = new Dictionary<(Texture2D, byte), Material>();
        readonly Shader sprite, additive;
        readonly GameObject root;
        readonly Mesh mesh;
        readonly MeshRenderer renderer;
        bool shown;
        public bool Shown => shown;
        public bool Glow;                       // Fox is focusing: he pulses brighter (the Knight's focus has its glow)
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public GxRenderer() {
            sprite = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("Sprites/Default shader not found");
            additive = Shader.Find("Legacy Shaders/Particles/Additive");
            if (additive == null) FoxMod.Instance.LogWarn("Particles/Additive shader not found: additive effects blend normally");
            root = new GameObject("FoxMeleeFrame");
            UnityEngine.Object.DontDestroyOnLoad(root);
            mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.MarkDynamic();
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            renderer = root.AddComponent<MeshRenderer>();
            root.SetActive(false);
        }

        /// False when the core could not decode a frame (the caller falls back to fox.model). `like`: the Knight's
        /// renderer, whose sorting Fox takes; `z`: the Knight's depth.
        public bool Draw(MeleeCore core, RoomTransform room, Renderer like, float z) {
            if (!core.GxDecode(GxFrame.FoxAndEffects, frame) || frame.VertexCount == 0) { Hide(); return false; }
            float inv = (float)(1.0 / room.Scale);
            float ox = (float)room.Origin.X, oy = (float)room.Origin.Y;
            var runs = frame.Runs;
            if (mats.Length != runs.Count) mats = new Material[runs.Count];
            v.Clear(); c.Clear(); u.Clear();
            for (int r = 0; r < runs.Count; r++) {
                var run = runs[r];
                var tex = Texture(core, run);
                mats[r] = Material(tex, run.Blend);
                for (int k = 0; k < run.Count; k++) {
                    int i = run.First + k;
                    v.Add(new Vector3(frame.Pos[3 * i] * inv + ox, frame.Pos[3 * i + 1] * inv + oy, z));
                    c.Add(new Color32(frame.Color[4 * i], frame.Color[4 * i + 1], frame.Color[4 * i + 2],
                                      run.Blend == GxFrame.Opaque && tex == null ? (byte)255 : frame.Color[4 * i + 3]));
                    u.Add(new Vector2(frame.Uv[2 * i], frame.Uv[2 * i + 1]));
                }
            }
            mesh.Clear();
            mesh.SetVertices(v); mesh.SetColors(c); mesh.SetUVs(0, u);
            mesh.subMeshCount = runs.Count;
            for (int r = 0; r < runs.Count; r++) {   // sub-meshes draw in order: the core's back-to-front runs
                idx.Clear();
                for (int k = 0; k < runs[r].Count; k++) idx.Add(runs[r].First + k);
                mesh.SetTriangles(idx, r, false);
            }
            mesh.RecalculateBounds();
            renderer.sharedMaterials = mats;
            if (like != null) { renderer.sortingLayerID = like.sortingLayerID; renderer.sortingOrder = like.sortingOrder; }
            float g = Glow ? 1.25f + 0.25f * Mathf.Sin(Time.unscaledTime * 10f) : 1f;
            block.SetColor("_Color", new Color(g, g, g, 1f));
            renderer.SetPropertyBlock(block);
            if (!shown) { root.SetActive(true); shown = true; }
            return true;
        }

        public void Hide() {
            if (shown) { root.SetActive(false); shown = false; }
        }

        Texture2D Texture(MeleeCore core, GxRun run) {
            if (run.Tex < 0) return null;
            int ws = run.Tex == GxFrame.Atlas ? 0 : run.WrapS, wt = run.Tex == GxFrame.Atlas ? 0 : run.WrapT;
            if (!core.GxTexture(run.Tex, out var t)) return null;
            var key = (run.Tex, ws, wt);
            if (textures.TryGetValue(key, out var have) && have.gen == t.Gen) return have.tex;
            var tex = have.tex;
            if (tex != null && (tex.width != t.W || tex.height != t.H)) { Forget(tex); tex = null; }
            // the atlas (4096x4096, 64 MB) changes a cell at a time: copy only that region on the GPU
            if (run.Tex == GxFrame.Atlas && tex != null && core.GxAtlasDirty(out int dx, out int dy, out int dw, out int dh)
                && SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None && UploadRegion(tex, t, dx, dy, dw, dh)) {
                textures[key] = (t.Gen, tex);
                return tex;
            }
            if (tex == null)   // a recycled id of the same size reuses its texture (and materials): no churn
                tex = new Texture2D(t.W, t.H, TextureFormat.RGBA32, false) {
                    filterMode = FilterMode.Bilinear,
                    wrapModeU = Wrap(ws), wrapModeV = Wrap(wt),
                };
            tex.LoadRawTextureData(t.Rgba, t.W * t.H * 4);   // top row first: v = 0 is the first row, as the core's
            tex.Apply(false, false);
            if (run.Tex == GxFrame.Atlas) core.GxAtlasDirty(out _, out _, out _, out _);   // all of it is up to date
            textures[key] = (t.Gen, tex);
            return tex;
        }

        byte[] region = new byte[0];
        Texture2D regionTex;
        bool UploadRegion(Texture2D atlas, GxTexture t, int x, int y, int w, int h) {
            if (w <= 0 || h <= 0 || x + w > t.W || y + h > t.H) return false;
            if (region.Length != w * h * 4) region = new byte[w * h * 4];   // LoadRawTextureData(byte[]) wants the exact size
            for (int row = 0; row < h; row++)
                System.Runtime.InteropServices.Marshal.Copy(t.Rgba + ((y + row) * t.W + x) * 4, region, row * w * 4, w * 4);
            if (regionTex == null || regionTex.width != w || regionTex.height != h) {
                if (regionTex != null) UnityEngine.Object.Destroy(regionTex);
                regionTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            }
            regionTex.LoadRawTextureData(region);
            regionTex.Apply(false, false);
            Graphics.CopyTexture(regionTex, 0, 0, 0, 0, w, h, atlas, 0, 0, x, y);
            return true;
        }

        static TextureWrapMode Wrap(int w) => w == 1 ? TextureWrapMode.Repeat : w == 2 ? TextureWrapMode.Mirror : TextureWrapMode.Clamp;

        void Forget(Texture2D tex) {
            var gone = new List<(Texture2D, byte)>();
            foreach (var k in materials.Keys) if (k.tex == tex) gone.Add(k);
            foreach (var k in gone) { UnityEngine.Object.Destroy(materials[k]); materials.Remove(k); }
            UnityEngine.Object.Destroy(tex);
        }

        Material Material(Texture2D tex, byte blend) {
            if (materials.TryGetValue((tex, blend), out var m)) return m;
            bool add = blend == GxFrame.Add && additive != null;
            m = new Material(add ? additive : sprite) { mainTexture = tex != null ? tex : Texture2D.whiteTexture };
            if (add) m.SetColor("_TintColor", AdditiveTint); else m.color = Color.white;
            materials[(tex, blend)] = m;
            return m;
        }
    }
}
