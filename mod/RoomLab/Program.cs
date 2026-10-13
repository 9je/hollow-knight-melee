using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FoxMelee.Lab {
    static class Program {
        static string CoreSo => Environment.GetEnvironmentVariable("MELEE_CORE_SO")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../core/build/libmelee_core.so"));

        static double Opt(string[] a, string name, double dflt) { int i = Array.IndexOf(a, name); return i >= 0 ? double.Parse(a[i + 1]) : dflt; }
        static string OptS(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 ? a[i + 1] : null; }

        static int Main(string[] a) {
            if (a.Length < 2) { Console.Error.WriteLine("usage: roomlab convert <room.json> --out DIR | summary <dir> | run <room.json> ..."); return 2; }
            double scale = Opt(a, "--scale", 6.0);
            using var core = MeleeCore.Load(CoreSo);
            switch (a[0]) {
                case "convert": {
                    var room = RoomFile.Load(a[1]);
                    var c = Rooms.Convert(core, room, scale);
                    Console.WriteLine($"{c.Scene}: {(c.Ok ? "OK" : "FAIL " + c.Error)} colliders={c.Colliders} shapes={c.Shapes} lines={c.Lines}");
                    if (!c.Ok) return 1;
                    var m = core.ReadCollision(c.Collision);
                    var gates = room.Gates.Select(g => { var p = c.Transform.ToMelee(new V2(g.X, g.Y)); return new { g.Name, x = p.X, y = p.Y, w = g.W * scale, h = g.H * scale }; });
                    var outDir = OptS(a, "--out") ?? ".";
                    Directory.CreateDirectory(outDir);
                    File.WriteAllText(Path.Combine(outDir, c.Scene + ".melee.json"), JsonSerializer.Serialize(new {
                        scene = c.Scene, scale, xy = m.XY, v0 = m.V0, v1 = m.V1, hi = m.HiFlags, lo = m.LoFlags, gates }));
                    return 0;
                }
                case "summary": {
                    int ok = 0, fail = 0, empty = 0;
                    foreach (var f in Directory.GetFiles(a[1], "*.json").Where(f => !f.EndsWith(".melee.json")).OrderBy(f => f)) {
                        var c = Rooms.Convert(core, RoomFile.Load(f), scale);
                        if (c.Ok) ok++; else if (c.Error == "no terrain") empty++; else fail++;
                        if (c.Error != "no terrain")
                            Console.WriteLine($"{(c.Ok ? "OK  " : "FAIL")} {c.Scene,-28} colliders={c.Colliders,4} shapes={c.Shapes,4} lines={c.Lines,5}{(c.Ok ? "" : "  " + c.Error)}");
                    }
                    Console.WriteLine($"total: {ok} ok, {fail} failed, {empty} without terrain");
                    return 0;
                }
                case "run": {
                    string iso = OptS(a, "--iso") ?? Environment.GetEnvironmentVariable("MELEE_ISO");
                    string mem1 = OptS(a, "--mem1") ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CoreSo), "gen-core/mem1.bin"));
                    var who = Roster.All.FirstOrDefault(ch => ch.Name == OptS(a, "--char")) ?? Roster.Fox;
                    if (!core.Init(mem1, iso) || !core.StartMatch(who.MeleeId, false)) { Console.Error.WriteLine(core.Error); return 1; }
                    core.SetInput(0, new McInput());
                    for (int p = 1; p < 4; p++) core.SetInput(p, null);
                    for (int i = 0; i < 180; i++) core.Step();
                    var room = RoomFile.Load(a[1]);
                    var c = Rooms.Convert(core, room, scale);
                    if (!c.Ok) { Console.Error.WriteLine(c.Error); return 1; }
                    var gate = room.Gates.Find(g => g.Name == OptS(a, "--at")) ?? room.Gates[0];
                    double inward = Opt(a, "--inward", 3), centre = (c.Min.X + c.Max.X) / 2;
                    bool side = gate.Name.StartsWith("left") || gate.Name.StartsWith("right");   // top/bottom gates: drop straight in
                    double sx = side ? gate.X + (gate.X < centre ? inward : -inward) : gate.X, sy = gate.Y - gate.H / 2 + 1;
                    var from = OptS(a, "--from");   // x,y in Hollow Knight units: start there instead of at a gate
                    if (from != null) { var xy = from.Split(','); sx = double.Parse(xy[0], System.Globalization.CultureInfo.InvariantCulture); sy = double.Parse(xy[1], System.Globalization.CultureInfo.InvariantCulture); }
                    var trace = Rooms.Run(core, c, sx, sy, Scripts.Get(OptS(a, "--script") ?? "idle", (int)Opt(a, "--frames", 300)), still: a.Contains("--still"));
                    File.WriteAllText(OptS(a, "--trace") ?? "trace.json", JsonSerializer.Serialize(trace.Select(t => new { frame = t.Frame, x = t.X, y = t.Y, action = t.Action, air = t.Air })));
                    if (OptS(a, "--print") != null) foreach (var t in trace) if (t.Frame % (int)Opt(a, "--print", 10) == 0) { var h = c.Transform.ToHk(new V2(t.X, t.Y)); Console.WriteLine($"  f{t.Frame,3} hk ({h.X:F2}, {h.Y:F2}) action {t.Action} air {t.Air}"); }
                    Console.WriteLine($"{c.Scene}: {trace.Count} frames from ({sx:F1}, {sy:F1}) via {gate.Name}; end action {trace[^1].Action} at ({trace[^1].X:F1}, {trace[^1].Y:F1})");
                    return 0;
                }
                case "sweep": {   // sweep <rooms dir> [--chars Fox,Bowser] [--rooms Town,Crossroads_01]: every character in at every side or top door
                    string iso = OptS(a, "--iso") ?? Environment.GetEnvironmentVariable("MELEE_ISO");
                    string mem1 = OptS(a, "--mem1") ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CoreSo), "gen-core/mem1.bin"));
                    if (!core.Init(mem1, iso)) { Console.Error.WriteLine(core.Error); return 1; }
                    var names = OptS(a, "--chars")?.Split(',');
                    var chars = Roster.All.Where(ch => names == null || names.Contains(ch.Name)).ToList();
                    var only = OptS(a, "--rooms")?.Split(',');
                    var rooms = Directory.GetFiles(a[1], "*.json").Where(f => !f.EndsWith(".melee.json")).OrderBy(f => f)
                        .Select(RoomFile.Load).Where(r => (only == null || only.Contains(r.Scene)) && r.Colliders.Count > 0).ToList();   // built one at a time: a build lasts until the next
                    Console.Error.WriteLine($"{rooms.Count} rooms, {chars.Count} characters");
                    foreach (var ch in chars) {
                        if (!core.StartMatch(ch.MeleeId, false)) { Console.Error.WriteLine($"{ch.Name}: {core.Error}"); return 1; }
                        core.SetInput(0, new McInput());
                        for (int p = 1; p < 4; p++) core.SetInput(p, null);
                        for (int i = 0; i < 180; i++) core.Step();
                        foreach (var room in rooms) {
                            var c = Rooms.Convert(core, room, scale);
                            if (!c.Ok) continue;
                            foreach (var g in room.Gates) {
                                var r = Sweep.Enter(core, c, g);
                                if (r != null) Console.WriteLine($"{ch.Name}\t{c.Scene}\t{g.Name}\t{r}");
                            }
                        }
                    }
                    return 0;
                }
                case "reach": {   // reach [--chars Fox,Bowser]: each character's jump, best height and widest gap
                    string iso = OptS(a, "--iso") ?? Environment.GetEnvironmentVariable("MELEE_ISO");
                    string mem1 = OptS(a, "--mem1") ?? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CoreSo), "gen-core/mem1.bin"));
                    if (!core.Init(mem1, iso)) { Console.Error.WriteLine(core.Error); return 1; }
                    var names = OptS(a, "--chars")?.Split(',');
                    foreach (var ch in Roster.All.Where(ch => names == null || names.Contains(ch.Name))) {
                        if (!core.Init(mem1, iso) || !core.StartMatch(ch.MeleeId, false)) { Console.Error.WriteLine($"{ch.Name}: {core.Error}"); return 1; }
                        core.SetInput(0, new McInput());
                        for (int p = 1; p < 4; p++) core.SetInput(p, null);
                        for (int i = 0; i < 180; i++) core.Step();
                        var r = Reach.Measure(core, scale);
                        Console.WriteLine($"{ch.Name,-15} jump {r.Jump,5:F1}  best height {r.High,5:F1}  widest gap {r.Gap,5:F1}");
                    }
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown command {a[0]}");
                    return 2;
            }
        }
    }
}
