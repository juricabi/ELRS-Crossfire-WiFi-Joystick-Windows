using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using vJoyInterfaceWrap;

namespace ELRSWifiJoystick
{
    // vJoyInterface.dll must come from the same vJoy release as the installed driver. A
    // mismatched one (the bundled 2.1.9 DLL against a 2.2.x driver) still "acquires" the
    // device, but its axis writes never land - the gamepad sits centred while frames stream
    // in. So load the DLL the vJoy installer put next to its own tools, and fall back to the
    // bundled copy only when vJoy isn't installed where we can find it.
    static class VJoyNative
    {
        private const string DllName = "vJoyInterface.dll";
        // vJoy's Inno Setup uninstall key; InstallLocation points at e.g. C:\Program Files\vJoy\
        private const string UninstallKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";

        private static int registered;

        // Full path of the installed DLL in use, or null when the bundled copy was loaded.
        public static string? LoadedFrom { get; private set; }

        // Must run before the first vJoy call (the resolver only affects later loads).
        public static void Register()
        {
            if (Interlocked.Exchange(ref registered, 1) == 1) return;
            NativeLibrary.SetDllImportResolver(typeof(vJoy).Assembly, Resolve);
        }

        // 0x222 -> "2.2.2" (vJoy packs major.minor.build into one nibble each)
        public static string FormatVersion(uint v) => $"{(v >> 8) & 0xF}.{(v >> 4) & 0xF}.{v & 0xF}";

        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (!name.Equals(DllName, StringComparison.OrdinalIgnoreCase)
                && !name.Equals(Path.GetFileNameWithoutExtension(DllName), StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;

            foreach (string path in InstalledCandidates())
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                {
                    LoadedFrom = path;
                    return handle;
                }
            }
            return IntPtr.Zero; // default probing -> the copy bundled with the app
        }

        private static IEnumerable<string> InstalledCandidates()
        {
            string? dir = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
                dir = key?.GetValue("InstallLocation") as string;
            }
            catch { }
            if (!string.IsNullOrWhiteSpace(dir))
                yield return Path.Combine(dir, "x64", DllName);
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "vJoy", "x64", DllName);
        }
    }
}
