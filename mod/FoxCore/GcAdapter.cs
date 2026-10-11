using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace FoxMelee {
    /// The Wii U / Mayflash GameCube adapter's input report (as Dolphin and Slippi read it): 0x21, then per port
    /// status (type in the top nibble: 1 wired, 2 WaveBird), buttons1, buttons2, stick x/y, C x/y, L, R (raw, 0-255).
    public struct GcRaw { public ushort Buttons; public byte StickX, StickY, CX, CY, L, R; }

    public static class GcAdapterReport {
        public const int Size = 37;

        public static bool Parse(byte[] r, int port, out GcRaw raw) {
            raw = default;
            if (r == null || r.Length < Size || r[0] != 0x21 || port < 0 || port > 3) return false;
            int o = 1 + 9 * port;
            int type = r[o] >> 4;
            if (type != 1 && type != 2) return false;
            byte b1 = r[o + 1], b2 = r[o + 2];
            ushort b = 0;
            if ((b1 & 0x01) != 0) b |= GcButtons.A;
            if ((b1 & 0x02) != 0) b |= GcButtons.B;
            if ((b1 & 0x04) != 0) b |= GcButtons.X;
            if ((b1 & 0x08) != 0) b |= GcButtons.Y;
            if ((b1 & 0x10) != 0) b |= GcButtons.DLeft;
            if ((b1 & 0x20) != 0) b |= GcButtons.DRight;
            if ((b1 & 0x40) != 0) b |= GcButtons.DDown;
            if ((b1 & 0x80) != 0) b |= GcButtons.DUp;
            if ((b2 & 0x01) != 0) b |= GcButtons.Start;
            if ((b2 & 0x02) != 0) b |= GcButtons.Z;
            if ((b2 & 0x04) != 0) b |= GcButtons.R;
            if ((b2 & 0x08) != 0) b |= GcButtons.L;
            raw = new GcRaw { Buttons = b, StickX = r[o + 3], StickY = r[o + 4], CX = r[o + 5], CY = r[o + 6], L = r[o + 7], R = r[o + 8] };
            return true;
        }
    }

    /// One adapter port as a console's pad library sees it: analog values relative to the origin the controller
    /// reported when it connected (the first report), so the game gets what it would on a GameCube.
    public sealed class GcPad {
        GcRaw origin;
        bool connected;

        /// The controller's McInput, or null when no controller is in that port.
        public McInput? Update(byte[] report, int port) {
            if (!GcAdapterReport.Parse(report, port, out var raw)) { connected = false; return null; }
            if (!connected) { origin = raw; connected = true; }
            return new McInput {
                Buttons = raw.Buttons,
                StickX = Stick(raw.StickX, origin.StickX), StickY = Stick(raw.StickY, origin.StickY),
                CStickX = Stick(raw.CX, origin.CX), CStickY = Stick(raw.CY, origin.CY),
                TriggerL = Trigger(raw.L, origin.L), TriggerR = Trigger(raw.R, origin.R),
            };
        }

        static sbyte Stick(byte v, byte o) => (sbyte)Math.Max(-128, Math.Min(127, v - o));
        static byte Trigger(byte v, byte o) => (byte)Math.Max(0, Math.Min(255, v - o));
    }

    /// The adapter's four ports. Port 0 means "the first port with a controller"; 1-4 pick one.
    public sealed class GcPads {
        readonly GcPad[] pads = { new GcPad(), new GcPad(), new GcPad(), new GcPad() };
        readonly McInput?[] now = new McInput?[4];

        public void Update(byte[] report) { for (int i = 0; i < 4; i++) now[i] = pads[i].Update(report, i); }

        /// The chosen controller as the adapter reports it (Hollow Knight's menus see all of it).
        public McInput? Current(int port) {
            if (port >= 1 && port <= 4) return now[port - 1];
            foreach (var v in now) if (v != null) return v;
            return null;
        }

        /// The chosen controller for Melee: unmapped, except Start, which stays with Hollow Knight (spec §3.2).
        public McInput? Melee(int port) {
            var v = Current(port);
            if (v is McInput m) { m.Buttons &= unchecked((ushort)~GcButtons.Start); return m; }
            return null;
        }
    }

    /// Reads the adapter over libusb on its own thread, as Slippi Dolphin does (the kernel's HID driver is detached
    /// while the game runs). Latest() returns the newest report; null while no adapter is open.
    public sealed class GcAdapter : IDisposable {
        const string Lib = "libusb-1.0.so.0";
        const ushort Vendor = 0x057E, Product = 0x0337;
        const byte EndpointIn = 0x81, EndpointOut = 0x02;

        [DllImport(Lib)] static extern int libusb_init(out IntPtr ctx);
        [DllImport(Lib)] static extern void libusb_exit(IntPtr ctx);
        [DllImport(Lib)] static extern IntPtr libusb_open_device_with_vid_pid(IntPtr ctx, ushort vendor, ushort product);
        [DllImport(Lib)] static extern int libusb_set_auto_detach_kernel_driver(IntPtr handle, int enable);
        [DllImport(Lib)] static extern int libusb_claim_interface(IntPtr handle, int iface);
        [DllImport(Lib)] static extern int libusb_release_interface(IntPtr handle, int iface);
        [DllImport(Lib)] static extern void libusb_close(IntPtr handle);
        [DllImport(Lib)] static extern int libusb_interrupt_transfer(IntPtr handle, byte endpoint, byte[] data, int length, out int transferred, uint timeoutMs);

        readonly object gate = new object();
        readonly byte[] latest = new byte[GcAdapterReport.Size];
        bool have;
        volatile bool stop;
        Thread thread;
        IntPtr ctx;

        public string Status { get; private set; } = "starting";

        /// Starts the reader; false (with Status saying why) when libusb is missing.
        public bool Start() {
            try { if (libusb_init(out ctx) != 0) { Status = "libusb_init failed"; return false; } }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException) { Status = "libusb not found: " + e.Message; return false; }
            thread = new Thread(Run) { IsBackground = true, Name = "GC adapter" };
            thread.Start();
            return true;
        }

        public byte[] Latest() {
            lock (gate) return have ? (byte[])latest.Clone() : null;
        }

        void Run() {
            var buf = new byte[GcAdapterReport.Size];
            while (!stop) {
                var h = libusb_open_device_with_vid_pid(ctx, Vendor, Product);
                if (h == IntPtr.Zero) { Status = "no GameCube adapter (or no permission: udev rule for 057e:0337)"; Thread.Sleep(1000); continue; }
                libusb_set_auto_detach_kernel_driver(h, 1);
                int rc = libusb_claim_interface(h, 0);
                if (rc != 0) { Status = $"cannot claim the adapter ({rc}): is Dolphin using it?"; libusb_close(h); Thread.Sleep(1000); continue; }
                libusb_interrupt_transfer(h, EndpointOut, new byte[] { 0x13 }, 1, out _, 100);   // start reporting
                Status = "reading";
                while (!stop) {
                    rc = libusb_interrupt_transfer(h, EndpointIn, buf, buf.Length, out int n, 100);
                    if (rc == -7) continue;                       // LIBUSB_ERROR_TIMEOUT
                    if (rc != 0) { Status = $"adapter read failed ({rc})"; break; }
                    if (n == GcAdapterReport.Size) lock (gate) { Buffer.BlockCopy(buf, 0, latest, 0, n); have = true; }
                }
                lock (gate) have = false;
                libusb_release_interface(h, 0);
                libusb_close(h);
                if (!stop) Thread.Sleep(500);
            }
        }

        public void Dispose() {
            stop = true;
            thread?.Join(1000);
            if (ctx != IntPtr.Zero) { libusb_exit(ctx); ctx = IntPtr.Zero; }
        }
    }
}
