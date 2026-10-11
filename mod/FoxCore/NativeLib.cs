using System;
using System.Runtime.InteropServices;

namespace FoxMelee {
    /// Loads a native library by full path and resolves symbols, on Linux (libdl) and Windows (kernel32).
    /// Used instead of [DllImport] so the library can live in the mod's folder.
    static class NativeLib {
        const int RTLD_NOW = 2;
        [DllImport("libdl.so.2", EntryPoint = "dlopen")] static extern IntPtr dlopen(string path, int flags);
        [DllImport("libdl.so.2", EntryPoint = "dlsym")] static extern IntPtr dlsym(IntPtr h, string name);
        [DllImport("libdl.so.2", EntryPoint = "dlerror")] static extern IntPtr dlerror();
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr h, string name);

        static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        public static IntPtr Open(string path, out string error) {
            error = null;
            if (Windows) {
                var h = LoadLibraryW(path);
                if (h == IntPtr.Zero) error = $"cannot load {path} (error {Marshal.GetLastWin32Error()})";
                return h;
            }
            var lib = dlopen(path, RTLD_NOW);
            if (lib == IntPtr.Zero) {
                var e = dlerror();
                error = e == IntPtr.Zero ? $"cannot load {path}" : Marshal.PtrToStringAnsi(e);
                if (!error.Contains(path)) error = $"{path}: {error}";
            }
            return lib;
        }

        public static T Symbol<T>(IntPtr lib, string name) where T : Delegate {
            var p = Windows ? GetProcAddress(lib, name) : dlsym(lib, name);
            if (p == IntPtr.Zero) throw new MeleeCoreException($"symbol {name} not found in libmelee_core");
            return (T)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }
    }
}
