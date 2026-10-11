using System;
using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Draws Melee's own frame (Fox, his items and effects, as Melee's graphics code drew them: core GX capture) in the
    /// room: one mesh per run, drawn in the core's back-to-front order (Hollow Knight's shaders write no depth, so the
    /// order comes from sortingOrder). Textures are uploaded once and again only when the core says they changed.
    sealed class GxRenderer {
        const float Z = 0f;
        const int BaseOrder = 0;
        static readonly Color AdditiveTint = new Color(0.5f, 0.5f, 0.5f, 0.5f);   // Particles/Additive doubles colour x tint

        sealed class Slot {
            public GameObject Go; public Mesh Mesh; public MeshRenderer Renderer;
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Color32> C = new List<Color32>();
            public readonly List<Vector2> U = new List<Vector2>();
            public readonly List<int> I = new List<int>();
        }

        readonly GxFrame frame = new GxFrame();
        readonly List<Slot> slots = new List<Slot>();
        readonly Dictionary<(int id, int ws, int wt), (uint gen, Texture2D tex)> textures = new Dictionary<(int, int, int), (uint, Texture2D)>();
        readonly Dictionary<(Texture2D tex, byte blend), Material> materials = new Dictionary<(Texture2D, byte), Material>();
        readonly Shader sprite, additive;
        readonly GameObject root;
        int shown;

        public GxRenderer() {
            sprite = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("Sprites/Default shader not found");
            additive = Shader.Find("Legacy Shaders/Particles/Additive");
            if (additive == null) FoxMod.Instance.LogWarn("Particles/Additive shader not found: additive effects blend normally");
            root = new GameObject("FoxMeleeFrame");
            UnityEngine.Object.DontDestroyOnLoad(root);
        }

        /// False when the core could not decode a frame (the caller falls back to fox.model).
        public bool Draw(MeleeCore core, RoomTransform room) {
            if (!core.GxDecode(GxFrame.FoxAndEffects, frame) || frame.VertexCount == 0) { Hide(); return false; }
            root.SetActive(true);
            float inv = (float)(1.0 / room.Scale);
            float ox = (float)room.Origin.X, oy = (float)room.Origin.Y;
            int used = 0;
            foreach (var run in frame.Runs) {
                var tex = Texture(core, run);
                var mat = Material(tex, run.Blend);
                if (used == slots.Count) slots.Add(NewSlot());
                var s = slots[used];
                s.V.Clear(); s.C.Clear(); s.U.Clear(); s.I.Clear();
                for (int k = 0; k < run.Count; k++) {
                    int i = run.First + k;
                    s.V.Add(new Vector3(frame.Pos[3 * i] * inv + ox, frame.Pos[3 * i + 1] * inv + oy, Z));
                    s.C.Add(new Color32(frame.Color[4 * i], frame.Color[4 * i + 1], frame.Color[4 * i + 2],
                                        run.Blend == GxFrame.Opaque && tex == null ? (byte)255 : frame.Color[4 * i + 3]));
                    s.U.Add(new Vector2(frame.Uv[2 * i], frame.Uv[2 * i + 1]));
                    s.I.Add(k);
                }
                s.Mesh.Clear();
                s.Mesh.SetVertices(s.V); s.Mesh.SetColors(s.C); s.Mesh.SetUVs(0, s.U); s.Mesh.SetTriangles(s.I, 0);
                s.Mesh.RecalculateBounds();
                s.Renderer.sharedMaterial = mat;
                s.Renderer.sortingOrder = BaseOrder + used;
                if (!s.Go.activeSelf) s.Go.SetActive(true);
                used++;
            }
            for (int i = used; i < shown; i++) slots[i].Go.SetActive(false);
            shown = used;
            return true;
        }

        public void Hide() {
            for (int i = 0; i < shown; i++) slots[i].Go.SetActive(false);
            shown = 0;
        }

        Slot NewSlot() {
            var go = new GameObject("FoxMeleeRun" + slots.Count);
            go.transform.SetParent(root.transform, false);
            var mesh = new Mesh();
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            return new Slot { Go = go, Mesh = mesh, Renderer = go.AddComponent<MeshRenderer>() };
        }

        Texture2D Texture(MeleeCore core, GxRun run) {
            if (run.Tex < 0) return null;
            int ws = run.Tex == GxFrame.Atlas ? 0 : run.WrapS, wt = run.Tex == GxFrame.Atlas ? 0 : run.WrapT;
            if (!core.GxTexture(run.Tex, out var t)) return null;
            var key = (run.Tex, ws, wt);
            if (textures.TryGetValue(key, out var have) && have.gen == t.Gen) return have.tex;
            if (have.tex != null) Forget(have.tex);
            var tex = new Texture2D(t.W, t.H, TextureFormat.RGBA32, false) {
                filterMode = FilterMode.Bilinear,
                wrapModeU = Wrap(ws), wrapModeV = Wrap(wt),
            };
            tex.LoadRawTextureData(t.Rgba, t.W * t.H * 4);   // top row first: v = 0 is the first row, as the core's
            tex.Apply(false, false);
            textures[key] = (t.Gen, tex);
            return tex;
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
