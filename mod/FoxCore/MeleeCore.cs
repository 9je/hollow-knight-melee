using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FoxMelee {
    public sealed class MeleeCoreException : Exception { public MeleeCoreException(string m) : base(m) { } }

    [StructLayout(LayoutKind.Sequential)]
    public struct McInput { public ushort Buttons; public sbyte StickX, StickY, CStickX, CStickY; public byte TriggerL, TriggerR; }

    [StructLayout(LayoutKind.Sequential)]
    public struct McFighter {
        public uint Action;
        public float X, Y, Facing, Percent, Shield, SelfVelX, SelfVelY, KbVelX, KbVelY, GroundVel;
        public uint Airborne;
        public float Hitlag;   // hitlag frames left
    }

    /// One of Fox's active Melee hitboxes (core McHitbox), in Melee units.
    [StructLayout(LayoutKind.Sequential)]
    public struct McHitbox {
        public uint Slot, Group;
        public float Damage, Radius;
        public int Angle, Growth, SetKb, BaseKb;
        public uint Element;
        public float X, Y, PrevX, PrevY;   // the hitbox sweeps from last frame's centre to this frame's
        public uint Fresh;                 // 1 = Melee has recorded no victim since it was created or cleared
        public uint Flags;                 // core MC_HIT_*: 1 hits airborne, 2 hits grounded, 4 hits fighters
    }

    /// One of Fox's hurtbox capsules (core McHurtbox); State 0 normal, 1 off, 2 intangible.
    [StructLayout(LayoutKind.Sequential)]
    public struct McHurtbox { public float Ax, Ay, Bx, By, Radius; public uint State; }

    [StructLayout(LayoutKind.Sequential)]
    public struct McBox { public float Left, Right, Top, Bottom; }

    /// A managed copy of a built Melee collision (MeleeCore.ReadCollision).
    public sealed class MeleeCollision { public float[] XY; public ushort[] V0, V1, HiFlags, LoFlags; }

    public enum ShapeKind { Solid = 0, Platform = 1, Hole = 2 }

    /// One outline in Melee units: XY = x0, y0, x1, y1, ...
    public sealed class RoomShape { public ShapeKind Kind; public float[] XY; public byte Material; }

    /// libmelee_core's C API. One per process (the core is a single instance); not thread-safe.
    public sealed class MeleeCore : IDisposable {
        [StructLayout(LayoutKind.Sequential)]
        struct McShape { public int Kind; public IntPtr Pts; public int N; public byte Material; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(string mem1, string iso);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void VoidFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ErrorFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SoloFn(byte[] start);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StartFn(byte[] start, uint seed, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void InputFn(int port, IntPtr input);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReadFn(int port, int follower, out McFighter f);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BuildFn(McShape[] shapes, int n, out IntPtr col, byte[] why, UIntPtr whySize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadFn(IntPtr col);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BoundsFn(ref McBox blast, ref McBox cam);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PlaceFn(int port, float x, float y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BonesFn(int port, int follower, float[] out12, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HitboxFn(int port, int follower, [Out] McHitbox[] o, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HurtboxFn(int port, int follower, [Out] McHurtbox[] o, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReportFn(int port, int follower, uint group, float damage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DobjsFn(int port, int follower, byte[] hidden, byte[] joint, int max);

        const int StartSize = 0x138;
        readonly InitFn init; readonly VoidFn shutdown; readonly ErrorFn error; readonly IntFn step, stepToHits;
        readonly SoloFn solo; readonly StartFn start; readonly InputFn input; readonly ReadFn read;
        readonly BuildFn build; readonly LoadFn load; readonly BoundsFn bounds; readonly PlaceFn place, respawn; readonly BonesFn bones; readonly DobjsFn dobjs;
        readonly HitboxFn hitboxes; readonly HurtboxFn hurtboxes; readonly ReportFn report;
        readonly IntPtr inputBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(McInput)));
        string localError;

        MeleeCore(IntPtr lib) {
            init = NativeLib.Symbol<InitFn>(lib, "mc_init");
            shutdown = NativeLib.Symbol<VoidFn>(lib, "mc_shutdown");
            error = NativeLib.Symbol<ErrorFn>(lib, "mc_error");
            step = NativeLib.Symbol<IntFn>(lib, "mc_step");
            stepToHits = NativeLib.Symbol<IntFn>(lib, "mc_step_to_hits");
            solo = NativeLib.Symbol<SoloFn>(lib, "mc_solo_fox_start");
            start = NativeLib.Symbol<StartFn>(lib, "mc_start_match");
            input = NativeLib.Symbol<InputFn>(lib, "mc_set_input");
            read = NativeLib.Symbol<ReadFn>(lib, "mc_read_fighter");
            build = NativeLib.Symbol<BuildFn>(lib, "mc_build_collision");
            load = NativeLib.Symbol<LoadFn>(lib, "mc_load_collision");
            bounds = NativeLib.Symbol<BoundsFn>(lib, "mc_set_bounds");
            place = NativeLib.Symbol<PlaceFn>(lib, "mc_place_fighter");
            respawn = NativeLib.Symbol<PlaceFn>(lib, "mc_respawn_fighter");
            bones = NativeLib.Symbol<BonesFn>(lib, "mc_read_bones");
            dobjs = NativeLib.Symbol<DobjsFn>(lib, "mc_read_dobjs");
            hitboxes = NativeLib.Symbol<HitboxFn>(lib, "mc_read_hitboxes");
            hurtboxes = NativeLib.Symbol<HurtboxFn>(lib, "mc_read_hurtboxes");
            report = NativeLib.Symbol<ReportFn>(lib, "mc_report_hit");
        }

        public static MeleeCore Load(string libraryPath) {
            var lib = NativeLib.Open(libraryPath, out var why);
            if (lib == IntPtr.Zero) throw new MeleeCoreException(why);
            return new MeleeCore(lib);
        }

        public string Error => localError ?? Marshal.PtrToStringAnsi(error()) ?? "";
        bool Ok(int rc) { localError = null; return rc == 0; }

        public bool Init(string mem1Path, string isoPath) => Ok(init(mem1Path, isoPath));
        public bool StartSoloFox() {
            var s = new byte[StartSize];
            solo(s);
            return Ok(start(s, 1, 0));
        }
        public void SetInput(int port, McInput? value) {
            if (value is McInput v) { Marshal.StructureToPtr(v, inputBuf, false); input(port, inputBuf); }
            else input(port, IntPtr.Zero);
        }
        public bool Step() => Ok(step());
        /// Runs the next frame up to Melee's hit resolution for port 0, where ReportHit lands in this frame; Step()
        /// finishes the frame. 0 paused there, 1 the frame ended without reaching it, -1 error.
        public int StepToHits() { localError = null; return stepToHits(); }
        public bool ReadFighter(int port, out McFighter f) => Ok(read(port, 0, out f));
        public bool LoadCollision(IntPtr collision) => Ok(load(collision));
        public bool SetBounds(McBox blast, McBox camera) => Ok(bounds(ref blast, ref camera));
        public bool PlaceFighter(int port, float x, float y) => Ok(place(port, x, y));
        /// Places the fighter for a respawn: falling, with no velocity, knockback or hitlag.
        public bool RespawnFighter(int port, float x, float y) => Ok(respawn(port, x, y));
        /// Melee's joint world matrices (12 floats each, 3x4 row-major) for port's fighter; 0 = no fighter.
        public int ReadBones(int port, float[] out12xN) => bones(port, 0, out12xN, out12xN.Length / 12);
        /// Mesh parts (DObjs) in the bones' order: hidden flag and joint index per part; returns the count.
        public int ReadDobjs(int port, byte[] hidden, byte[] joint) => dobjs(port, 0, hidden, joint, Math.Min(hidden.Length, joint.Length));
        /// Port 0's active hitboxes; returns the count (-1 on error).
        public int ReadHitboxes(McHitbox[] o) => hitboxes(0, 0, o, o.Length);
        public int ReadHurtboxes(McHurtbox[] o) => hurtboxes(0, 0, o, o.Length);
        /// Fox's hit in `group` landed outside Melee: Melee records it and gives Fox its attacker hitlag next frame.
        public bool ReportHit(uint group, float damage) => Ok(report(0, 0, group, damage));

        public bool BuildCollision(IReadOnlyList<RoomShape> shapes, out IntPtr collision) {
            var handles = new List<GCHandle>();
            try {
                var native = new McShape[shapes.Count];
                for (int i = 0; i < shapes.Count; i++) {
                    var h = GCHandle.Alloc(shapes[i].XY, GCHandleType.Pinned);
                    handles.Add(h);
                    native[i] = new McShape { Kind = (int)shapes[i].Kind, Pts = h.AddrOfPinnedObject(), N = shapes[i].XY.Length / 2, Material = shapes[i].Material };
                }
                var why = new byte[256];
                int rc = build(native, native.Length, out collision, why, (UIntPtr)why.Length);
                if (rc == 0) { localError = null; return true; }
                localError = System.Text.Encoding.ASCII.GetString(why).TrimEnd('\0');
                return false;
            } finally {
                foreach (var h in handles) h.Free();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct McCollisionNative {
            public IntPtr Verts; public int NVerts; public IntPtr Lines; public int NLines;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public short[] Start;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public short[] Count;
            public IntPtr Joints; public int NJoints; public uint X2c;
        }

        /// A managed copy of a built collision (valid only until the next build: copy at once).
        public MeleeCollision ReadCollision(IntPtr collision) {
            var c = Marshal.PtrToStructure<McCollisionNative>(collision);
            var m = new MeleeCollision { XY = new float[2 * c.NVerts], V0 = new ushort[c.NLines], V1 = new ushort[c.NLines], HiFlags = new ushort[c.NLines], LoFlags = new ushort[c.NLines] };
            Marshal.Copy(c.Verts, m.XY, 0, m.XY.Length);
            var raw = new short[8 * c.NLines];   // McLine: u16 v0, v1; s16 prev0, next0, prev1, next1; u16 hi, lo
            Marshal.Copy(c.Lines, raw, 0, raw.Length);
            for (int i = 0; i < c.NLines; i++) {
                m.V0[i] = (ushort)raw[8 * i]; m.V1[i] = (ushort)raw[8 * i + 1];
                m.HiFlags[i] = (ushort)raw[8 * i + 6]; m.LoFlags[i] = (ushort)raw[8 * i + 7];
            }
            return m;
        }

        public void Dispose() {
            shutdown();
            Marshal.FreeHGlobal(inputBuf);
        }
    }
}
