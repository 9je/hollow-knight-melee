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

    /// Fox's shield bubble (core McShield): centre and radius in Melee units; Hard 0 lightest .. 1 full press.
    [StructLayout(LayoutKind.Sequential)]
    public struct McShield { public uint Active; public float X, Y, Radius, Hard; }

    /// One of Melee's items (core McItem): Fox's lasers among them.
    [StructLayout(LayoutKind.Sequential)]
    public struct McItem { public uint Kind; public float X, Y, Vx, Vy, Facing; }

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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ShieldFn(int port, int follower, out McShield o);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ItemsFn([Out] McItem[] o, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GxCaptureFn(int on);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HitlagFn(int damage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ApplyHitFn(int port, ref McHit hit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetPercentFn(int port, float percent);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StandInSetFn(int port);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OnFn(int on);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong FrameFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int BounceFn(int port, float vy);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GatesFn(int port, uint locked);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int StandInWakeFn(float x, float y, float facing);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GxDecodeFn(ulong mask, out IntPtr verts, out int nverts, out IntPtr runs, out int nruns);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GxTextureFn(int id, out GxFrame.Texture t);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int GxAtlasDirtyFn(out uint x, out uint y, out uint w, out uint h);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReportFn(int port, int follower, uint group, float damage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SfxFn([Out] McSfx[] o, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DobjsFn(int port, int follower, byte[] hidden, byte[] joint, int max);

        const int StartSize = 0x138;
        readonly InitFn init; readonly VoidFn shutdown; readonly ErrorFn error; readonly IntFn step, stepToHits, stepToRender, finishFrame;
        readonly SoloFn solo, standinStart; readonly StandInSetFn standinSet; readonly StandInWakeFn standinWake; readonly IntFn standinSleep, standinHeld;
        readonly StartFn start; readonly OnFn edgeAssist; readonly FrameFn frameNo; readonly BounceFn bounce; readonly OnFn ko; readonly GatesFn gates; readonly InputFn input; readonly ReadFn read;
        readonly BuildFn build; readonly LoadFn load; readonly BoundsFn bounds; readonly PlaceFn place, respawn, tumble; readonly BonesFn bones; readonly DobjsFn dobjs;
        readonly HitboxFn hitboxes; readonly HurtboxFn hurtboxes; readonly ReportFn report;
        readonly ShieldFn shield; readonly ItemsFn items;
        readonly HitlagFn hitlag; readonly ApplyHitFn applyHit; readonly SetPercentFn setPercent;
        readonly GxCaptureFn gxCapture; readonly GxDecodeFn gxDecode; readonly GxTextureFn gxTexture; readonly GxAtlasDirtyFn gxAtlasDirty; readonly SfxFn sfx;
        readonly IntPtr inputBuf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(McInput)));
        string localError;

        MeleeCore(IntPtr lib) {
            init = NativeLib.Symbol<InitFn>(lib, "mc_init");
            shutdown = NativeLib.Symbol<VoidFn>(lib, "mc_shutdown");
            error = NativeLib.Symbol<ErrorFn>(lib, "mc_error");
            step = NativeLib.Symbol<IntFn>(lib, "mc_step");
            stepToHits = NativeLib.Symbol<IntFn>(lib, "mc_step_to_hits");
            stepToRender = NativeLib.Symbol<IntFn>(lib, "mc_step_to_render");
            finishFrame = NativeLib.Symbol<IntFn>(lib, "mc_finish_frame");
            solo = NativeLib.Symbol<SoloFn>(lib, "mc_solo_fox_start");
            standinStart = NativeLib.Symbol<SoloFn>(lib, "mc_fox_standin_start");
            standinSet = NativeLib.Symbol<StandInSetFn>(lib, "mc_standin_set");
            standinWake = NativeLib.Symbol<StandInWakeFn>(lib, "mc_standin_wake");
            standinSleep = NativeLib.Symbol<IntFn>(lib, "mc_standin_sleep");
            standinHeld = NativeLib.Symbol<IntFn>(lib, "mc_standin_held");
            edgeAssist = NativeLib.Symbol<OnFn>(lib, "mc_set_edge_assist");
            frameNo = NativeLib.Symbol<FrameFn>(lib, "mc_frame");
            bounce = NativeLib.Symbol<BounceFn>(lib, "mc_bounce");
            ko = NativeLib.Symbol<OnFn>(lib, "mc_ko");
            gates = NativeLib.Symbol<GatesFn>(lib, "mc_set_gates");
            start = NativeLib.Symbol<StartFn>(lib, "mc_start_match");
            input = NativeLib.Symbol<InputFn>(lib, "mc_set_input");
            read = NativeLib.Symbol<ReadFn>(lib, "mc_read_fighter");
            build = NativeLib.Symbol<BuildFn>(lib, "mc_build_collision");
            load = NativeLib.Symbol<LoadFn>(lib, "mc_load_collision");
            bounds = NativeLib.Symbol<BoundsFn>(lib, "mc_set_bounds");
            place = NativeLib.Symbol<PlaceFn>(lib, "mc_place_fighter");
            respawn = NativeLib.Symbol<PlaceFn>(lib, "mc_respawn_fighter");
            tumble = NativeLib.Symbol<PlaceFn>(lib, "mc_tumble_fighter");
            bones = NativeLib.Symbol<BonesFn>(lib, "mc_read_bones");
            dobjs = NativeLib.Symbol<DobjsFn>(lib, "mc_read_dobjs");
            hitboxes = NativeLib.Symbol<HitboxFn>(lib, "mc_read_hitboxes");
            hurtboxes = NativeLib.Symbol<HurtboxFn>(lib, "mc_read_hurtboxes");
            report = NativeLib.Symbol<ReportFn>(lib, "mc_report_hit");
            shield = NativeLib.Symbol<ShieldFn>(lib, "mc_read_shield");
            items = NativeLib.Symbol<ItemsFn>(lib, "mc_read_items");
            gxCapture = NativeLib.Symbol<GxCaptureFn>(lib, "mc_gx_capture");
            hitlag = NativeLib.Symbol<HitlagFn>(lib, "mc_hitlag_frames");
            applyHit = NativeLib.Symbol<ApplyHitFn>(lib, "mc_apply_hit");
            setPercent = NativeLib.Symbol<SetPercentFn>(lib, "mc_set_percent");
            gxDecode = NativeLib.Symbol<GxDecodeFn>(lib, "mc_gx_decode");
            gxTexture = NativeLib.Symbol<GxTextureFn>(lib, "mc_gx_texture");
            gxAtlasDirty = NativeLib.Symbol<GxAtlasDirtyFn>(lib, "mc_gx_atlas_dirty");
            sfx = NativeLib.Symbol<SfxFn>(lib, "mc_read_sfx");
        }

        public static MeleeCore Load(string libraryPath) {
            var lib = NativeLib.Open(libraryPath, out var why);
            if (lib == IntPtr.Zero) throw new MeleeCoreException(why);
            return new MeleeCore(lib);
        }

        public string Error => localError ?? Marshal.PtrToStringAnsi(error()) ?? "";
        bool Ok(int rc) { localError = null; return rc == 0; }

        public bool Init(string mem1Path, string isoPath) { Sync(); return Ok(init(mem1Path, isoPath)); }
        public bool StartSoloFox() {
            Sync();
            var s = new byte[StartSize];
            solo(s);
            return Ok(start(s, 1, 0));
        }
        /// Fox at port 0 and the grab/push stand-in's fighter at port 1 (plan 7).
        public bool StartFoxWithStandIn() {
            Sync();
            var s = new byte[StartSize];
            standinStart(s);
            return Ok(start(s, 1, 0));
        }
        /// The stand-in (plan 7): asleep from the next frame; woken at (x, y) facing ±1; held in a grab (1), not (0), none (-1).
        public bool SetStandIn(int port) { Sync(); return Ok(standinSet(port)); }
        public bool StandInWake(float x, float y, float facing) { Sync(); return Ok(standinWake(x, y, facing)); }
        public bool StandInSleep() { Sync(); return Ok(standinSleep()); }
        public int StandInHeld() { Sync(); return standinHeld(); }
        /// Edge assist (not Melee): port 0's walk teeters at an edge whatever the tilt, as Melee's walk does below 75% tilt.
        public bool SetEdgeAssist(bool on) { Sync(); return Ok(edgeAssist(on ? 1 : 0)); }
        public void SetInput(int port, McInput? value) {
            Sync();
            if (value is McInput v) { Marshal.StructureToPtr(v, inputBuf, false); input(port, inputBuf); }
            else input(port, IntPtr.Zero);
        }
        /// Runs one frame (or the rest of one paused at the hit pause). A frame left paused at Melee's drawing
        /// (StepToRender) is finished first, then a whole frame runs.
        public bool Step() { Sync(); if (finishFrame() != 0) return Ok(-1); return Ok(step()); }
        /// Hazard pogo (spec §5.4, not Melee): an airborne fighter goes up at vy, fast fall ended, double jump back.
        /// 0 done, 1 not airborne, -1 error.
        public int Bounce(int port, float vy) { Sync(); localError = null; return bounce(port, vy); }
        /// Hollow Knight's death: Melee's KO blast and voice where the fighter stands; it stays hidden until its next placement.
        public bool Ko(int port) { Sync(); return Ok(ko(port)); }
        /// Move gating (spec §6): MoveGates bits of the moves that don't start.
        public bool SetGates(int port, uint locked) { Sync(); return Ok(gates(port, locked)); }
        /// Frame boundaries reached since Init.
        public ulong Frame { get { Sync(); return frameNo(); } }
        /// Runs the next frame up to Melee's hit resolution for port 0, where ReportHit lands in this frame; Step()
        /// finishes the frame. 0 paused there, 1 the frame ended without reaching it, -1 error.
        public int StepToHits() { Sync(); localError = null; return stepToHits(); }
        /// Runs the frame up to Melee's drawing: its logic is done, and reads report what it ends with. 0 paused there,
        /// 1 the frame ended first, -1 error. FinishFrameAsync (or any step) finishes it.
        public int StepToRender() { Sync(); localError = null; return stepToRender(); }
        /// Finishes a frame paused by StepToRender (Melee's drawing, about three quarters of a frame's time) on another
        /// thread; nothing if none is paused. Every other call waits for it first; a failure shows in the next call.
        public void FinishFrameAsync() {
            Sync();
            pending = System.Threading.Tasks.Task.Run(() => { finishFrame(); });
        }
        public bool ReadFighter(int port, out McFighter f) { Sync(); return Ok(read(port, 0, out f)); }
        public bool LoadCollision(IntPtr collision) { Sync(); return Ok(load(collision)); }
        public bool SetBounds(McBox blast, McBox camera) { Sync(); return Ok(bounds(ref blast, ref camera)); }
        public bool PlaceFighter(int port, float x, float y) { Sync(); return Ok(place(port, x, y)); }
        /// Places the fighter for a respawn: falling, with no velocity, knockback or hitlag.
        public bool RespawnFighter(int port, float x, float y) { Sync(); return Ok(respawn(port, x, y)); }
        /// As RespawnFighter, tumbling (DamageFall): a knockdown on landing unless he techs.
        public bool TumbleFighter(int port, float x, float y) { Sync(); return Ok(tumble(port, x, y)); }
        /// Melee's joint world matrices (12 floats each, 3x4 row-major) for port's fighter; 0 = no fighter.
        public int ReadBones(int port, float[] out12xN) { Sync(); return bones(port, 0, out12xN, out12xN.Length / 12); }
        /// Mesh parts (DObjs) in the bones' order: hidden flag and joint index per part; returns the count.
        public int ReadDobjs(int port, byte[] hidden, byte[] joint) { Sync(); return dobjs(port, 0, hidden, joint, Math.Min(hidden.Length, joint.Length)); }
        /// Port 0's active hitboxes; returns the count (-1 on error).
        public int ReadHitboxes(McHitbox[] o) { Sync(); return hitboxes(0, 0, o, o.Length); }
        public int ReadHurtboxes(McHurtbox[] o) { Sync(); return hurtboxes(0, 0, o, o.Length); }
        /// Fox's hit in `group` landed outside Melee: Melee records it and gives Fox its attacker hitlag next frame.
        public bool ReportHit(uint group, float damage) { Sync(); return Ok(report(0, 0, group, damage)); }
        /// Melee's ItemKind for Fox's laser.
        public const uint FoxLaser = 54;
        public bool ReadShield(out McShield s) { Sync(); return Ok(shield(0, 0, out s)); }
        /// Melee's items (lasers...); returns the count, -1 on error.
        public int ReadItems(McItem[] o) { Sync(); return items(o, o.Length); }
        /// Melee's hitlag frames for a hit of `damage` (its formula and cap); -1 on error.
        public int HitlagFrames(int damage) { Sync(); return hitlag(damage); }
        /// A hit from outside Melee on port 0, at the hit pause (StepToHits): McHit.Landed / Shielded / Intangible, -1 error.
        public int ApplyHit(McHit hit) { Sync(); localError = null; return applyHit(0, ref hit); }
        /// Port 0's percent (Focus, benches and deaths reset it).
        public bool SetPercent(float percent) { Sync(); return Ok(setPercent(0, percent)); }
        public bool SetPercent(int port, float percent) { Sync(); return Ok(setPercent(port, percent)); }
        /// Capture Melee's graphics each frame (off by default; it changes nothing in the game).
        public bool GxCapture(bool on) { Sync(); return Ok(gxCapture(on ? 1 : 0)); }
        /// Decodes the last frame's drawing by the objects in `plinkMask` (GxFrame.FoxAndEffects) into `frame`.
        public bool GxDecode(ulong plinkMask, GxFrame frame) {
            Sync();
            return Ok(DecodeInto(plinkMask, frame) ? 0 : -1);
        }

        /// The same on another thread (it takes milliseconds): every other call waits for it first, so Melee does not
        /// move on while the decode reads its memory, and the frame decoded is the one it started on (after a frame
        /// FinishFrameAsync is still drawing).
        public System.Threading.Tasks.Task<bool> GxDecodeAsync(ulong plinkMask, GxFrame frame) {
            var before = pending;   // a frame still drawing (FinishFrameAsync): decoded once it is done
            var t = before == null ? System.Threading.Tasks.Task.Run(() => DecodeInto(plinkMask, frame))
                                   : before.ContinueWith(_ => DecodeInto(plinkMask, frame), System.Threading.Tasks.TaskScheduler.Default);
            pending = t;
            return t;
        }

        unsafe bool DecodeInto(ulong plinkMask, GxFrame frame) {
            if (gxDecode(plinkMask, out var v, out int nv, out var r, out int nr) != 0) return false;
            frame.Fill((GxFrame.Vert*)v, nv, (GxFrame.Run*)r, nr);
            return true;
        }

        System.Threading.Tasks.Task pending;
        /// Waits for a decode still running on another thread.
        public void Sync() {
            var p = pending;
            if (p == null) return;
            pending = null;
            try { p.Wait(); } catch (AggregateException) { }
        }
        public bool GxTexture(int id, out GxTexture t) {
            Sync();
            t = default;
            if (gxTexture(id, out var raw) != 0) { localError = "no such texture"; return false; }
            t = new GxTexture { W = (int)raw.W, H = (int)raw.H, Rgba = raw.Rgba, Gen = raw.Gen };
            localError = null;
            return true;
        }
        /// The atlas texels changed since the last call (x, y, w, h); false when none did.
        public bool GxAtlasDirty(out int x, out int y, out int w, out int h) {
            Sync();
            bool any = gxAtlasDirty(out uint ux, out uint uy, out uint uw, out uint uh) == 1;
            x = (int)ux; y = (int)uy; w = (int)uw; h = (int)uh;
            return any;
        }
        /// Melee's sound effect calls since the last read, oldest first (drains them); count, -1 on error.
        public int ReadSfx(McSfx[] o) { Sync(); return sfx(o, o.Length); }

        public bool BuildCollision(IReadOnlyList<RoomShape> shapes, out IntPtr collision) {
            Sync();
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
            Sync();
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
            Sync();
            shutdown();
            Marshal.FreeHGlobal(inputBuf);
        }
    }
}
