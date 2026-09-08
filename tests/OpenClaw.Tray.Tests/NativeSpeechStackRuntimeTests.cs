using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Guards the app-local Visual C++ runtime that local builds and test hosts
/// copy next to the native speech stack.
///
/// onnxruntime &gt;= 1.20 needs VC++ 14.38 or newer. An older app-local
/// msvcp140.dll shadows the system runtime, onnxruntime.dll then fails to
/// initialize (Win32 error 1114), and the Piper TTS client surfaces that as
/// DllNotFoundException. scripts/Test-ReleaseNativeDependencies.ps1 covers
/// published payloads; these tests cover what developers actually run.
/// </summary>
public sealed class NativeSpeechStackRuntimeTests
{
    // Same floor as scripts/Test-ReleaseNativeDependencies.ps1: VS 17.8, the first 14.38 release.
    private static readonly Version VCRuntimeMinVersion = new(14, 38, 33130, 0);

    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    [Fact]
    public void TestHost_AppLocalVCRuntime_MeetsOnnxRuntimeFloor()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var msvcp = Path.Combine(AppContext.BaseDirectory, "msvcp140.dll");
        if (!File.Exists(msvcp))
            return; // No app-local runtime: the system runtime is used and nothing can be shadowed.

        AssertMeetsFloor(msvcp);
    }

    [Fact]
    public void TestHost_OnnxRuntime_LoadsWithAppLocalVCRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var onnxRuntime = FindOnnxRuntime(AppContext.BaseDirectory);
        Assert.True(onnxRuntime != null, $"onnxruntime.dll was not found under {AppContext.BaseDirectory}.");

        AssertLoads(onnxRuntime!, AppContext.BaseDirectory);
    }

    [Fact]
    public void TrayBuildOutput_NativeTtsStack_LoadsWithAppLocalVCRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // FindTrayOutputDirectory only discovers win-x64 output, and this probe
        // loads that output into the test process. A native ARM64 test host
        // cannot load x64 DLLs, so only an x64 process can exercise it.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return;

        // CI builds src/OpenClaw.Tray.WinUI -r win-x64 before this suite. A local
        // checkout that has not built the tray has no output to guard yet.
        var outputDirectory = FindTrayOutputDirectory();
        if (outputDirectory == null)
            return;

        var msvcp = Path.Combine(outputDirectory, "msvcp140.dll");
        Assert.True(File.Exists(msvcp), $"msvcp140.dll was not copied to {outputDirectory}.");
        AssertMeetsFloor(msvcp);

        AssertLoads(Path.Combine(outputDirectory, "onnxruntime.dll"), outputDirectory);
        AssertLoads(Path.Combine(outputDirectory, "sherpa-onnx-c-api.dll"), outputDirectory);
    }

    [Fact]
    public void BuildTargets_ResolveVCRuntimeFromVSInstallForLocalBuilds()
    {
        var root = TestRepositoryPaths.GetRepositoryRoot();
        var shared = File.ReadAllText(Path.Combine(root, "Directory.Build.targets"));
        var src = File.ReadAllText(Path.Combine(root, "src", "Directory.Build.targets"));
        var tests = File.ReadAllText(Path.Combine(root, "tests", "Directory.Build.props"));

        Assert.Contains("Target Name=\"LocateOpenClawVCRuntimeFromVSInstall\"", shared);
        Assert.Contains("Microsoft.VCRedistVersion.default.txt", shared);

        Assert.Contains("Target Name=\"ResolveOpenClawVCRuntimeForBuild\"", src);
        Assert.Contains("DependsOnTargets=\"LocateOpenClawVCRuntimeFromVSInstall\"", src);
        Assert.Matches(
            "Target Name=\"CopyOpenClawVCRuntimeToOutput\"[^>]*DependsOnTargets=\"ResolveOpenClawVCRuntimeForBuild\"",
            src);

        Assert.Contains("DependsOnTargets=\"LocateOpenClawVCRuntimeFromVSInstall\"", tests);
        Assert.Contains("@(OpenClawVSVCRuntimeFiles)", tests);
    }

    private static void AssertMeetsFloor(string runtimeDll)
    {
        var info = FileVersionInfo.GetVersionInfo(runtimeDll);
        var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        Assert.True(
            version >= VCRuntimeMinVersion,
            $"{runtimeDll} is VC++ runtime {version}, older than the {VCRuntimeMinVersion} floor onnxruntime needs. " +
            "The build copied the VCRuntime.CefSharp.140 fallback instead of the Visual Studio install's runtime; " +
            "install the 'C++ Redistributable Update' Visual Studio component and rebuild.");
    }

    /// <summary>
    /// Load <paramref name="library"/> the way the app's loader would: the
    /// DLL's own directory first, then <paramref name="runtimeDirectory"/>
    /// (where the app-local CRT lives), then the default system directories.
    /// </summary>
    private static void AssertLoads(string library, string runtimeDirectory)
    {
        Assert.True(File.Exists(library), $"{library} does not exist.");

        var cookie = AddDllDirectory(runtimeDirectory);
        Assert.True(cookie != IntPtr.Zero, $"AddDllDirectory failed for {runtimeDirectory}.");
        try
        {
            var handle = LoadLibraryExW(library, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
            var error = Marshal.GetLastPInvokeError();
            if (handle == IntPtr.Zero)
            {
                var msvcp = Path.Combine(runtimeDirectory, "msvcp140.dll");
                var msvcpVersion = File.Exists(msvcp)
                    ? FileVersionInfo.GetVersionInfo(msvcp).FileVersion
                    : "(none)";
                Assert.Fail(
                    $"{Path.GetFileName(library)} failed to load from {Path.GetDirectoryName(library)} " +
                    $"(Win32 error {error}: {new Win32Exception(error).Message}). " +
                    $"App-local msvcp140.dll version: {msvcpVersion}. " +
                    "Error 1114 means the app-local VC++ runtime is older than onnxruntime requires.");
            }

            FreeLibrary(handle);
        }
        finally
        {
            RemoveDllDirectory(cookie);
        }
    }

    private static string? FindOnnxRuntime(string baseDirectory)
    {
        // A RID-specific build places the native asset at the root. A no-RID
        // build copies every runtimes/<rid>/native folder, so pick the one that
        // matches this process rather than the first one that exists.
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "onnxruntime.dll"),
            Path.Combine(baseDirectory, "runtimes", rid, "native", "onnxruntime.dll"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindTrayOutputDirectory()
    {
        var configuration = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;
        if (string.IsNullOrWhiteSpace(configuration))
            return null;

        var binDirectory = Path.Combine(TestRepositoryPaths.GetRepositoryRoot(), "src", "OpenClaw.Tray.WinUI", "bin");
        if (!Directory.Exists(binDirectory))
            return null;

        // dotnet build -r win-x64 writes bin/<Configuration>/<tfm>/win-x64;
        // Visual Studio's x64 platform writes bin/x64/<Configuration>/<tfm>/win-x64.
        var configurationDirectories = new[]
        {
            Path.Combine(binDirectory, configuration),
            Path.Combine(binDirectory, "x64", configuration),
        };

        return configurationDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateDirectories(directory, "net10.0-windows*"))
            .Select(directory => Path.Combine(directory, "win-x64"))
            .Where(directory => File.Exists(Path.Combine(directory, "OpenClaw.Tray.WinUI.exe")))
            .OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, "OpenClaw.Tray.WinUI.exe")))
            .FirstOrDefault();
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDllDirectory(IntPtr cookie);
}
