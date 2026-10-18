using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // vJoy's Inno Setup uninstall key - the same AppId for 2.1.9 and the 2.2.x forks. All
        // of them install in 64-bit mode, always copy the DLL to {app}\x64 and record that
        // folder as DllX64Location; Inno itself writes InstallLocation (C:\Program Files\vJoy\).
        private const string UninstallKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";

        private static int registered;
        // Probed once: the runtime asks again on the first call of every vJoy P/Invoke.
        private static readonly Lazy<IntPtr> Installed = new(LoadInstalled);

        // Full path of the installed DLL in use, or null when the bundled copy was loaded.
        public static string? LoadedFrom { get; private set; }
        // Why an installed DLL that exists could not be loaded, when none could.
        public static string? LoadError { get; private set; }

        // Must run before the first vJoy call (the resolver only affects later loads).
        public static void Register()
        {
            if (Interlocked.Exchange(ref registered, 1) == 1) return;
            // Best effort: if this ever fails, the bundled DLL loads exactly as before.
            try { NativeLibrary.SetDllImportResolver(typeof(vJoy).Assembly, Resolve); }
            catch { }
        }

        // 0x222 -> "2.2.2" (vJoy packs major.minor.build into one nibble each)
        public static string FormatVersion(uint v) => $"{(v >> 8) & 0xF}.{(v >> 4) & 0xF}.{v & 0xF}";

        internal static bool IsVJoyInterface(string libraryName) =>
            libraryName.Equals(DllName, StringComparison.OrdinalIgnoreCase)
            || libraryName.Equals(Path.GetFileNameWithoutExtension(DllName), StringComparison.OrdinalIgnoreCase);

        // IntPtr.Zero -> default probing, i.e. the copy bundled with the app.
        private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
            IsVJoyInterface(name) ? Installed.Value : IntPtr.Zero;

        private static IntPtr LoadInstalled()
        {
            RegistryKey? key = null;
            try { key = Registry.LocalMachine.OpenSubKey(UninstallKey); } catch { }
            using (key)
            {
                string? Read(string name)
                {
                    try { return key?.GetValue(name) as string; } catch { return null; }
                }
                var found = Probe(
                    Candidates(Read, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
                    File.Exists, NativeLibrary.Load);
                LoadedFrom = found.Path;
                LoadError = found.Error;
                return found.Handle;
            }
        }

        // Where the installed DLL may be, best first; blank values and repeats are skipped.
        internal static IReadOnlyList<string> Candidates(Func<string, string?> readValue, string programFiles)
        {
            var paths = new List<string>();
            string? dllDir = readValue("DllX64Location");
            if (!string.IsNullOrWhiteSpace(dllDir))
                paths.Add(Path.Combine(dllDir, DllName));
            string? installDir = readValue("InstallLocation");
            if (!string.IsNullOrWhiteSpace(installDir))
                paths.Add(Path.Combine(installDir, "x64", DllName));
            if (!string.IsNullOrWhiteSpace(programFiles))
                paths.Add(Path.Combine(programFiles, "vJoy", "x64", DllName));
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // The first candidate that exists and loads wins. If none does, report the first load
        // failure (e.g. a missing dependency) - "not found" would send the user the wrong way.
        internal static (IntPtr Handle, string? Path, string? Error) Probe(
            IEnumerable<string> candidates, Func<string, bool> exists, Func<string, IntPtr> load)
        {
            string? error = null;
            foreach (string path in candidates)
            {
                if (!exists(path)) continue;
                try { return (load(path), path, null); }
                catch (Exception ex) { error ??= $"{path}: {ex.Message}"; }
            }
            return (IntPtr.Zero, null, error);
        }
    }
}
