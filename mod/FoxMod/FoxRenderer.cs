using UnityEngine;
using UnityEngine.Rendering;

namespace FoxMelee {
    /// Fox's extracted model, skinned on the CPU every frame with Melee's joint matrices and placed in the
    /// room through RoomTransform. One atlas material (Sprites/Default: unlit, double-sided, no depth writes),
    /// so the triangles are re-ordered back to front each frame and parts Melee hides are left out.
    public class FoxRenderer : MonoBehaviour {
        FoxModel model;
        Mesh mesh;
        Vector3[] verts;
        float[] posed;
        int[] order;
        readonly byte[] hidden = new byte[256], joint = new byte[256];
        const float DepthScale = 0.02f;   // Melee z to Hollow Knight depth: Fox stays thin in front of the scenery

        public static FoxRenderer Create(FoxModel model) {
            var go = new GameObject("FoxMelee Model");
            try {
                var r = go.AddComponent<FoxRenderer>();
                r.model = model;
                r.Build();
                DontDestroyOnLoad(go);
                return r;
            } catch {
                Destroy(go);
                throw;
            }
        }

        void Build() {
            int n = model.Pos.Length / 3;
            verts = new Vector3[n];
            posed = new float[n * 3];
            order = new int[model.Tris.Length];
            var uv = new Vector2[n];
            var colours = new Color32[n];
            for (int i = 0; i < n; i++) { uv[i] = new Vector2(model.Uv[2 * i], 1f - model.Uv[2 * i + 1]); colours[i] = new Color32(255, 255, 255, 255); }
            mesh = new Mesh { indexFormat = IndexFormat.UInt32, vertices = verts, uv = uv, colors32 = colours };
            mesh.MarkDynamic();
            var tex = new Texture2D(model.AtlasW, model.AtlasH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var flipped = new byte[model.AtlasRgba.Length];   // fox.model rows are top first; Unity's are bottom first
            int row = model.AtlasW * 4;
            for (int y = 0; y < model.AtlasH; y++) System.Buffer.BlockCopy(model.AtlasRgba, y * row, flipped, (model.AtlasH - 1 - y) * row, row);
            tex.LoadRawTextureData(flipped);
            tex.Apply();
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new System.InvalidOperationException("Sprites/Default shader not found");
            gameObject.AddComponent<MeshFilter>().mesh = mesh;
            gameObject.AddComponent<MeshRenderer>().material = new Material(shader) { mainTexture = tex, color = Color.white };
        }

        /// False when the skeleton does not match the model (the caller draws the placeholder instead).
        public bool Draw(MeleeCore core, float[] bones, RoomTransform room) {
            int n = core.ReadBones(0, bones);
            if (n != model.BoneCount) { Hide(); return false; }
            gameObject.SetActive(true);
            model.Pose(bones, n, posed);
            int nd = core.ReadDobjs(0, hidden, joint);
            int count = model.DrawOrder(posed, model.MapHidden(hidden, joint, nd > 0 ? nd : 0), order);
            for (int i = 0; i < verts.Length; i++) {
                var hk = room.ToHk(new V2(posed[3 * i], posed[3 * i + 1]));
                verts[i] = new Vector3((float)hk.X, (float)hk.Y, -posed[3 * i + 2] * DepthScale);
            }
            mesh.vertices = verts;
            mesh.SetIndices(order, 0, count, MeshTopology.Triangles, 0);
            mesh.RecalculateBounds();
            return true;
        }

        public void Hide() => gameObject.SetActive(false);
    }
}
