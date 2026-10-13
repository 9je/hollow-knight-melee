using System;

namespace FoxMelee.Lab {
    /// A character coming into a room by a door, as the mod puts it there: does it land inside the room, and can it then
    /// walk in (not wedged under a ceiling or in a wall)? One line per entry: landed, out (fell past the room's bottom), the
    /// distance walked in 90 frames (Hollow Knight units) and the action it ended in.
    public static class Sweep {
        const int Settle = 90, Walk = 90;

        public static string Enter(MeleeCore core, Conversion c, Gate g) {
            bool side = g.Name.StartsWith("left") || g.Name.StartsWith("right"), top = g.Name.StartsWith("top"), door = g.Name.StartsWith("door");
            if (!side && !top && !door) return null;   // bottom gates: Hollow Knight throws the Knight up through them
            double centre = (c.Min.X + c.Max.X) / 2;
            int dir = g.Name.StartsWith("left") ? 1 : g.Name.StartsWith("right") ? -1 : g.X < centre ? 1 : -1;   // a side door: in, away from it
            double sx = side ? g.X + 3 * dir : g.X, sy = top ? g.Y - g.H / 2 - 1 : side ? g.Y : g.Y - g.H / 2 + 1;   // side: from the door's centre down, as HeroController.EnterScene finds the ground
            sbyte inward = (sbyte)(80 * dir);
            var idle = new McInput[Settle];
            var walk = new McInput[Walk];
            for (int i = 0; i < Walk; i++) walk[i].StickX = inward;
            var t1 = Rooms.Run(core, c, sx, sy, idle, still: true);
            var land = t1[^1];
            var hk = c.Transform.ToHk(new V2(land.X, land.Y));
            bool fellOut = hk.Y < c.Min.Y - 2;
            // keep going from where it settled
            var t2 = Rooms.Continue(core, walk);
            var end = c.Transform.ToHk(new V2(t2[^1].X, t2[^1].Y));
            double moved = Math.Abs(end.X - hk.X);
            return $"{(land.Air == 0 ? "landed" : "air")}\t{(fellOut ? "OUT" : "in")}\t{moved:F1}\t{t2[^1].Action}\t({sx:F1},{sy:F1})->({hk.X:F1},{hk.Y:F1})->({end.X:F1},{end.Y:F1})";
        }
    }
}
