using UnityEngine;

namespace FoxMelee {
    /// Fox's extracted model, skinned on the CPU every frame with Melee's joint matrices and placed in the
    /// room through RoomTransform. Unlit and double-sided (Melee mirrors Fox with negative-scale matrices).
    public class FoxRenderer : MonoBehaviour {
        FoxModel model;
        Mesh mesh;
        Vector3[] verts;
        float[] posed;
        const float DepthScale = 0.02f;   // Melee z to Hollow Knight depth: keep Fox flat-ish in front of the scenery

        public static FoxRenderer Create(FoxModel model) {
            var go = new GameObject("FoxMelee Model");
            DontDestroyOnLoad(go);
            var r = go.AddComponent<FoxRenderer>();
            r.model = model;
            r.Build();
            return r;
        }

        void Build() {
            int n = model.Pos.Length / 3;
            verts = new Vector3[n];
            posed = new float[n * 3];
            var uv = new Vector2[n];
            for (int i = 0; i < n; i++) uv[i] = new Vector2(model.Uv[2 * i], 1f - model.Uv[2 * i + 1]);
            mesh = new Mesh { vertices = verts, uv = uv, subMeshCount = model.Parts.Count };
            mesh.MarkDynamic();
            var mats = new Material[model.Parts.Count];
            var textures = new Texture2D[model.Textures.Count];
            for (int t = 0; t < textures.Length; t++) {
                var (w, h, rgba) = model.Textures[t];
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
                var flipped = new byte[rgba.Length];   // fox.model rows are top first; Unity's are bottom first
                for (int y = 0; y < h; y++) System.Buffer.BlockCopy(rgba, y * w * 4, flipped, (h - 1 - y) * w * 4, w * 4);
                tex.LoadRawTextureData(flipped);
                tex.Apply();
                textures[t] = tex;
            }
            var shader = Shader.Find("Sprites/Default");
            for (int p = 0; p < model.Parts.Count; p++) {
                var part = model.Parts[p];
                var idx = new int[part.Count];
                System.Array.Copy(model.Indices, part.First, idx, 0, part.Count);
                mesh.SetTriangles(idx, p);
                var c = part.Colour;
                mats[p] = new Material(shader) {
                    mainTexture = part.Texture >= 0 ? textures[part.Texture] : Texture2D.whiteTexture,
                    color = part.Texture >= 0 ? Color.white : new Color32(c[0], c[1], c[2], 255),
                };
            }
            gameObject.AddComponent<MeshFilter>().mesh = mesh;
            gameObject.AddComponent<MeshRenderer>().materials = mats;
        }

        public void Draw(float[] bones, int n, RoomTransform room) {
            if (n != model.BoneCount) { Hide(); return; }
            gameObject.SetActive(true);
            model.Pose(bones, n, posed);
            for (int i = 0; i < verts.Length; i++) {
                var hk = room.ToHk(new V2(posed[3 * i], posed[3 * i + 1]));
                verts[i] = new Vector3((float)hk.X, (float)hk.Y, -posed[3 * i + 2] * DepthScale);
            }
            mesh.vertices = verts;
            mesh.RecalculateBounds();
        }

        public void Hide() => gameObject.SetActive(false);
    }
}
