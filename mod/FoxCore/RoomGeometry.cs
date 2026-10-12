using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;

namespace FoxMelee {
    public struct V2 { public double X, Y; public V2(double x, double y) { X = x; Y = y; } }

    public sealed class ColliderOutline {
        public V2[] Points;
        public bool Closed = true;
        public bool OneWay;
    }

    /// Melee = (HK - Origin) * Scale. One scale for the whole world; only the world is scaled.
    public sealed class RoomTransform {
        public double Scale = 1;
        public V2 Origin;
        public V2 ToMelee(V2 hk) => new V2((hk.X - Origin.X) * Scale, (hk.Y - Origin.Y) * Scale);
        public V2 ToHk(V2 melee) => new V2(melee.X / Scale + Origin.X, melee.Y / Scale + Origin.Y);

        /// The room rule shared by the mod and the lab: origin at the centre of the terrain's bounds.
        public static RoomTransform ForRoom(IEnumerable<ColliderOutline> outlines, double scale) {
            var (min, max) = RoomGeometry.Bounds(outlines);
            return new RoomTransform { Scale = scale, Origin = new V2((min.X + max.X) / 2, (min.Y + max.Y) / 2) };
        }
    }

    /// Hollow Knight terrain colliders to Melee room shapes (merged outlines, holes, platforms).
    public static class RoomGeometry {
        public const double EdgeThickness = 0.2;
        public const double MinEdge = 0.1;   // HK units (0.6 Melee units): shorter edges are merged into their neighbours
        const double Eps = 1e-4;
        const int Precision = 4;   // Clipper2 decimal places for the union (0.0001 HK units)

        public static (V2 min, V2 max) Bounds(IEnumerable<ColliderOutline> colliders) {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var c in colliders)
                foreach (var p in c.Points) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
            return (new V2(x0, y0), new V2(x1, y1));
        }

        public static List<RoomShape> Build(IEnumerable<ColliderOutline> colliders, RoomTransform t) {
            var solids = new PathsD();
            var shapes = new List<RoomShape>();
            foreach (var c in colliders) {
                if (c.Points == null || c.Points.Length < 2) continue;
                var path = new PathD(c.Points.Select(p => new PointD(p.X, p.Y)));
                if (c.OneWay) {
                    double x0 = c.Points.Min(p => p.X), x1 = c.Points.Max(p => p.X), top = c.Points.Max(p => p.Y);
                    if (x1 - x0 > Eps) shapes.Add(Shape(ShapeKind.Platform, new[] { new V2(x0, top), new V2(x1, top) }, t));
                } else if ((c.Closed || EndsMeet(c.Points)) && c.Points.Length >= 3) {   // closed edge loops are solids
                    solids.Add(path);
                } else {
                    solids.AddRange(Clipper.InflatePaths(new PathsD { path }, EdgeThickness / 2, JoinType.Miter, EndType.Butt, 2, Precision));
                }
            }
            if (solids.Count > 0) {
                var tree = new PolyTreeD();
                Clipper.BooleanOp(ClipType.Union, solids, null, tree, FillRule.NonZero, Precision);
                Collect(tree, shapes, t);
            }
            return shapes;
        }

        static bool EndsMeet(V2[] p) =>
            p.Length >= 4 && System.Math.Abs(p[0].X - p[p.Length - 1].X) < Eps && System.Math.Abs(p[0].Y - p[p.Length - 1].Y) < Eps;

        static void Collect(PolyPathD node, List<RoomShape> shapes, RoomTransform t) {
            for (int i = 0; i < node.Count; i++) {
                var child = node[i];
                var pts = Simplify(child.Polygon);
                if (pts.Length >= 3) shapes.Add(Shape(child.IsHole ? ShapeKind.Hole : ShapeKind.Solid, pts, t));
                Collect(child, shapes, t);
            }
        }

        /// Drops duplicate and collinear points of a closed outline, and points closer than MinEdge to the one before:
        /// Melee's collision follows a near-zero line to the far end of its chain.
        static V2[] Simplify(PathD path) {
            var p = path.Select(q => new V2(q.x, q.y)).ToList();
            bool changed = true;
            while (changed && p.Count >= 3) {
                changed = false;
                for (int i = 0; i < p.Count && p.Count >= 3; i++) {
                    V2 a = p[(i + p.Count - 1) % p.Count], b = p[i], c = p[(i + 1) % p.Count];
                    double cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
                    bool dup = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y) < MinEdge * MinEdge;
                    double len = Math.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y));
                    if (dup || Math.Abs(cross) <= Eps * Math.Max(len, 1)) { p.RemoveAt(i); changed = true; i--; }
                }
            }
            return p.ToArray();
        }

        static RoomShape Shape(ShapeKind kind, V2[] pts, RoomTransform t) {
            var xy = new float[pts.Length * 2];
            for (int i = 0; i < pts.Length; i++) { var m = t.ToMelee(pts[i]); xy[2 * i] = (float)m.X; xy[2 * i + 1] = (float)m.Y; }
            return new RoomShape { Kind = kind, XY = xy };
        }
    }
}

namespace FoxMelee {
    /// Which terrain colliders the room's Melee collision was built from: a broken wall or a closing gate changes the
    /// set, and the collision is rebuilt (spec §4: breakables, arena doors).
    public sealed class TerrainSet {
        readonly System.Collections.Generic.HashSet<int> ids = new System.Collections.Generic.HashSet<int>();
        bool seen;

        public bool Changed(System.Collections.Generic.IEnumerable<int> live) {
            var now = new System.Collections.Generic.HashSet<int>(live);
            if (seen && now.SetEquals(ids)) return false;
            ids.Clear(); ids.UnionWith(now); seen = true;
            return true;
        }

        public void Reset() { ids.Clear(); seen = false; }
    }
}
