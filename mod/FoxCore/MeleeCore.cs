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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct McBox { public float Left, Right, Top, Bottom; }

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

        const int StartSize = 0x138;
        readonly InitFn init; readonly VoidFn shutdown; readonly ErrorFn error; readonly IntFn step;
        readonly SoloFn solo; readonly StartFn start; readonly InputFn input; readonly ReadFn read;
        readonly BuildFn build; readonly LoadFn load; readonly BoundsFn bounds; readonly PlaceFn place;
        readonly IntPtr inputBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(McInput)));
        string localError;

        MeleeCore(IntPtr lib) {
            init = NativeLib.Symbol<InitFn>(lib, "mc_init");
            shutdown = NativeLib.Symbol<VoidFn>(lib, "mc_shutdown");
            error = NativeLib.Symbol<ErrorFn>(lib, "mc_error");
            step = NativeLib.Symbol<IntFn>(lib, "mc_step");
            solo = NativeLib.Symbol<SoloFn>(lib, "mc_solo_fox_start");
            start = NativeLib.Symbol<StartFn>(lib, "mc_start_match");
            input = NativeLib.Symbol<InputFn>(lib, "mc_set_input");
            read = NativeLib.Symbol<ReadFn>(lib, "mc_read_fighter");
            build = NativeLib.Symbol<BuildFn>(lib, "mc_build_collision");
            load = NativeLib.Symbol<LoadFn>(lib, "mc_load_collision");
            bounds = NativeLib.Symbol<BoundsFn>(lib, "mc_set_bounds");
            place = NativeLib.Symbol<PlaceFn>(lib, "mc_place_fighter");
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
        public bool ReadFighter(int port, out McFighter f) => Ok(read(port, 0, out f));
        public bool LoadCollision(IntPtr collision) => Ok(load(collision));
        public bool SetBounds(McBox blast, McBox camera) => Ok(bounds(ref blast, ref camera));
        public bool PlaceFighter(int port, float x, float y) => Ok(place(port, x, y));

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

        public void Dispose() {
            shutdown();
            Marshal.FreeHGlobal(inputBuf);
        }
    }
}
