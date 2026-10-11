using System.Collections.Generic;

namespace FoxMelee {
    /// Fox inside solid ground (terrain that changed under him, a placement gone wrong) cannot get out by himself:
    /// after Frames Melee frames inside the room's collision he goes back to where he last stood on safe ground.
    public sealed class StuckGuard {
        public const int Frames = 20;
        V2? safe;
        int inside;

        public void Reset() { safe = null; inside = 0; }

        /// One Melee frame: Fox's middle (Melee units) inside a solid, standing or not. Returns where to put him back.
        public V2? Frame(bool inside, bool grounded, double x, double y) {
            if (!inside) {
                this.inside = 0;
                if (grounded) safe = new V2(x, y);
                return null;
            }
            if (++this.inside < Frames || safe == null) return null;
            this.inside = 0;
            return safe;
        }

        /// The room's solids and holes nest (Clipper's tree): a point is in solid ground when an odd number of them
        /// contain it. Platforms are not solid.
        public static bool InsideSolid(IList<RoomShape> shapes, double x, double y) {
            bool solid = false;
            foreach (var s in shapes)
                if (s.Kind != ShapeKind.Platform && Contains(s.XY, x, y)) solid = !solid;
            return solid;
        }

        static bool Contains(float[] xy, double x, double y) {
            bool c = false;
            int n = xy.Length / 2;
            for (int i = 0, j = n - 1; i < n; j = i++) {
                double xi = xy[2 * i], yi = xy[2 * i + 1], xj = xy[2 * j], yj = xy[2 * j + 1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) c = !c;
            }
            return c;
        }
    }
}
