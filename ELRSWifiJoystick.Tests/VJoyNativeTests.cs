using Xunit;

namespace ELRSWifiJoystick.Tests;

public class VJoyNativeTests
{
    private static Func<string, string?> Reg(string? dllX64 = null, string? install = null) =>
        name => name switch
        {
            "DllX64Location" => dllX64,
            "InstallLocation" => install,
            _ => null,
        };

    [Fact]
    public void Candidates_PreferInstallerDllFolder_ThenInstallDir_ThenProgramFiles()
    {
        var c = VJoyNative.Candidates(Reg(@"D:\vJoy\x64", @"E:\Old\vJoy\"), @"C:\Program Files");
        Assert.Equal(new[]
        {
            @"D:\vJoy\x64\vJoyInterface.dll",
            @"E:\Old\vJoy\x64\vJoyInterface.dll",
            @"C:\Program Files\vJoy\x64\vJoyInterface.dll",
        }, c);
    }

    [Fact]
    public void Candidates_StandardInstall_ListsTheFolderOnce()
    {
        // What the vJoy installer writes: DllX64Location = InstallLocation\x64.
        var c = VJoyNative.Candidates(
            Reg(@"C:\Program Files\vJoy\x64", @"C:\Program Files\vJoy\"), @"C:\Program Files");
        Assert.Equal(new[] { @"C:\Program Files\vJoy\x64\vJoyInterface.dll" }, c);
    }

    [Fact]
    public void Candidates_NoRegistryEntry_StillTriesDefaultInstallFolder()
    {
        // vJoy not installed (or installed by hand): only the default location is probed,
        // and when that doesn't exist either, the bundled copy loads.
        var c = VJoyNative.Candidates(Reg(), @"C:\Program Files");
        Assert.Equal(new[] { @"C:\Program Files\vJoy\x64\vJoyInterface.dll" }, c);
    }

    [Fact]
    public void Candidates_BlankValues_AreSkipped()
    {
        Assert.Empty(VJoyNative.Candidates(Reg(" ", ""), ""));
    }

    [Theory]
    [InlineData("vJoyInterface.dll", true)]
    [InlineData("VJOYINTERFACE.DLL", true)]
    [InlineData("vJoyInterface", true)]        // the runtime may pass the name without .dll
    [InlineData("vJoyInterfaceWrap.dll", false)]
    [InlineData("kernel32", false)]
    public void IsVJoyInterface_MatchesOnlyTheNativeDll(string name, bool expected)
    {
        Assert.Equal(expected, VJoyNative.IsVJoyInterface(name));
    }

    [Fact]
    public void Probe_SkipsMissingFiles_AndTakesTheFirstThatLoads()
    {
        var found = VJoyNative.Probe(new[] { "a", "b", "c" },
            exists: p => p != "a", load: p => p == "b" ? new IntPtr(42) : new IntPtr(7));
        Assert.Equal((new IntPtr(42), "b", (string?)null), found);
    }

    [Fact]
    public void Probe_LoadFailure_FallsThroughToTheNextCandidate()
    {
        var found = VJoyNative.Probe(new[] { "a", "b" },
            exists: _ => true,
            load: p => p == "a" ? throw new DllNotFoundException("missing vcruntime") : new IntPtr(42));
        Assert.Equal((new IntPtr(42), "b", (string?)null), found);
    }

    [Fact]
    public void Probe_NothingLoads_ReportsTheFirstFailure_ForTheLog()
    {
        var found = VJoyNative.Probe(new[] { "a", "b" },
            exists: _ => true, load: p => throw new BadImageFormatException($"bad {p}"));
        Assert.Equal(IntPtr.Zero, found.Handle);
        Assert.Null(found.Path);                  // -> the engine logs "bundled copy"
        Assert.Equal("a: bad a", found.Error);
    }

    [Fact]
    public void Probe_NothingInstalled_FallsBackSilently()
    {
        var found = VJoyNative.Probe(new[] { "a" }, exists: _ => false, load: _ => new IntPtr(1));
        Assert.Equal((IntPtr.Zero, (string?)null, (string?)null), found);
    }

    [Theory]
    [InlineData(0x219u, "2.1.9")]   // SourceForge / jshafer817 2.1.9.1
    [InlineData(0x221u, "2.2.1")]   // njz3 2.2.1.1
    [InlineData(0x222u, "2.2.2")]   // BrunnerInnovation 2.2.2.0
    [InlineData(0u, "0.0.0")]       // DriverMatch couldn't read a version
    public void FormatVersion_UnpacksNibbles(uint packed, string expected)
    {
        Assert.Equal(expected, VJoyNative.FormatVersion(packed));
    }
}
