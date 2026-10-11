using System;
using System.Collections.Generic;
using UnityEngine;

namespace FoxMelee {
    /// Melee effects Fox's model does not include, drawn as simple shapes where Melee puts them each frame: the shield
    /// bubble (Melee's own centre and size, port 1 red, more see-through when light), the shine (while Fox is in a
    /// down-B state) and his lasers (Melee's items). Stand-ins until Melee's own effect models are drawn.
    sealed class EffectsRenderer {
        const float Z = -0.3f;                                    // just in front of Fox
        const int Segments = 32;
        const uint ShineFirst = 360, ShineLast = 369, ShineEnd = 363, ShineAirEnd = 368;   // ftFx_MS_SpecialLw*
        const float ShineRadius = 8f, ShineHeight = 6.5f;         // Melee units, by eye
        const float LaserLength = 14f, LaserWidth = 1.2f;
        static readonly Color ShieldColor = new Color(1f, 0.25f, 0.25f);
        static readonly Color ShineColor = new Color(0.6f, 0.9f, 1f);
        static readonly Color LaserColor = new Color(1f, 0.3f, 0.4f);

        readonly GameObject go;
        readonly Mesh mesh = new Mesh();
        readonly List<Vector3> v = new List<Vector3>();
        readonly List<Color> col = new List<Color>();
        readonly List<int> tri = new List<int>();
        readonly McItem[] items = new McItem[32];

        public EffectsRenderer() {
            go = new GameObject("FoxEffects");
            UnityEngine.Object.DontDestroyOnLoad(go);
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().mesh = mesh;
            var shader = Shader.Find("Sprites/Default") ?? throw new InvalidOperationException("Sprites/Default shader not found");
            go.AddComponent<MeshRenderer>().material = new Material(shader) { color = Color.white };
        }

        public void Draw(MeleeCore core, McFighter fox, RoomTransform room) {
            v.Clear(); col.Clear(); tri.Clear();
            float s = (float)room.Scale;
            Vector3 At(float x, float y) { var h = room.ToHk(new V2(x, y)); return new Vector3((float)h.X, (float)h.Y, Z); }
            if (core.ReadShield(out var sh) && sh.Active != 0) {
                var c = ShieldColor; c.a = 0.3f + 0.3f * sh.Hard;
                var rim = c; rim.a = Math.Min(1f, c.a + 0.3f);
                Disc(At(sh.X, sh.Y), sh.Radius / s, Segments, c, rim);
            }
            if (fox.Action >= ShineFirst && fox.Action <= ShineLast && fox.Action != ShineEnd && fox.Action != ShineAirEnd) {
                var c = ShineColor; c.a = 0.35f;
                var rim = ShineColor; rim.a = 0.9f;
                Disc(At(fox.X, fox.Y + ShineHeight), ShineRadius / s, 6, c, rim);
            }
            int n = core.ReadItems(items);
            for (int i = 0; i < n; i++) {
                if (items[i].Kind != MeleeCore.FoxLaser) continue;
                float vx = items[i].Vx, vy = items[i].Vy, len = (float)Math.Sqrt(vx * vx + vy * vy);
                if (len < 0.01f) continue;
                float dx = vx / len, dy = vy / len;
                Quad(At(items[i].X, items[i].Y), At(items[i].X - dx * LaserLength, items[i].Y - dy * LaserLength),
                     new Vector3(-dy, dx, 0) * (LaserWidth / 2 / s), LaserColor);
            }
            mesh.Clear();
            if (tri.Count == 0) { go.SetActive(false); return; }
            go.SetActive(true);
            mesh.SetVertices(v); mesh.SetColors(col); mesh.SetTriangles(tri, 0);
            mesh.RecalculateBounds();
        }

        public void Hide() => go.SetActive(false);

        // a filled polygon: centre colour inside, rim colour on the edge (6 segments: the shine's hexagon)
        void Disc(Vector3 c, float r, int segments, Color inner, Color rim) {
            int c0 = v.Count;
            v.Add(c); col.Add(inner);
            for (int i = 0; i < segments; i++) {
                float a = (float)(2 * Math.PI * i / segments + (segments == 6 ? Math.PI / 6 : 0));
                v.Add(c + new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r, 0)); col.Add(rim);
                tri.Add(c0); tri.Add(c0 + 1 + i); tri.Add(c0 + 1 + (i + 1) % segments);
            }
        }

        void Quad(Vector3 a, Vector3 b, Vector3 side, Color c) {
            int q = v.Count;
            v.Add(a + side); v.Add(a - side); v.Add(b - side); v.Add(b + side);
            var tail = c; tail.a = 0.2f;
            col.Add(c); col.Add(c); col.Add(tail); col.Add(tail);
            tri.Add(q); tri.Add(q + 1); tri.Add(q + 2); tri.Add(q); tri.Add(q + 2); tri.Add(q + 3);
        }
    }
}
