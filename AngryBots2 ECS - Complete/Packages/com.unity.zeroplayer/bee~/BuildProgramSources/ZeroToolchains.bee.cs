using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bee.Core;
using Bee.CSharpSupport;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building.FluentSyntaxHelpers;
using Bee.Stevedore;
using Bee.Toolchain.Emscripten;
using Bee.Toolchain.Linux;
using Bee.Toolchain.Windows;
using NiceIO;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;
using Unity.BuildSystem.CSharpSupport;
using Unity.Dots.Build;

public static class ZeroToolchains
{
    public static ZeroToolchain Emscripten_AsmJs => new ZeroToolchain
    {
        tc = MakeEmscripten(new AsmJsArchitecture()),
        burstable = false, 
        hasDotnet = false
    };
    public static ToolChain Emscripten_Wasm { get; } = MakeEmscripten(new WasmArchitecture());

    public static NPath NodeExe;

    static EmscriptenToolchain MakeEmscripten(EmscriptenArchitecture arch)
    {
        var emscripten = StevedoreArtifact.Testing("emscripten/1.38.28-unity_f379303a2883cabf196c2c62ba33ff282cbd94ca7f9ad68b5d19c9ccf0967f9e.7z");
        var emscriptenVersion = new Version(1, 38, 28);
        var emscriptenRoot = emscripten.Path.Combine("emscripten-nightly-1.38.28-2019_03_08_12_42");

        EmscriptenSdk sdk = null;

        if (Environment.GetEnvironmentVariable("EMSDK") != null)
        {
            Console.WriteLine("Using pre-set environment EMSDK=" + Environment.GetEnvironmentVariable("EMSDK") + ". This should only be used for local development. Unset EMSDK env. variable to use tagged Emscripten version from Stevedore.");
            NodeExe = Environment.GetEnvironmentVariable("EMSDK_NODE");
            return new EmscriptenToolchain(new EmscriptenSdk(
                Environment.GetEnvironmentVariable("EMSCRIPTEN"),
                llvmRoot: Environment.GetEnvironmentVariable("LLVM_ROOT"),
                pythonExe: Environment.GetEnvironmentVariable("EMSDK_PYTHON"),
                nodeExe: Environment.GetEnvironmentVariable("EMSDK_NODE"),
                architecture: arch,
                // Use a dummy/hardcoded version string to represent Emscripten "incoming" branch (it should be always considered
                // a "dirty" branch that does not correspond to any tagged release)
                version: new Version(9, 9, 9),
                isDownloadable: false
                ));
        }

        if (HostPlatform.IsWindows)
        {
            var llvm = StevedoreArtifact.Testing("emscripten-llvm-win-x64/1.38.28-unity_11a020e80732ca8679a6fb3ec4ad0de4d207f0c20cfbc9c686d0709b4cf036b3.zip");
            var python = StevedoreArtifact.Public("winpython2-x64/2.7.13.1Zero_740e3bbd4c2384963a0944dec446dc36ce7513df2786c243b417b93a2dff851e.zip");
            var node = StevedoreArtifact.Public("node-win-x64/8.11.2_8bbd03b041f8326aba5ab754e4619eb3322907ddbfd77b93ddbcdaa435533ce0.7z");
            var firefox = StevedoreArtifact.Testing("browsers/firefox6101_a07c51a7341a164d85beb5b9d35bdfc5af5515b8c0bc55a09d41fc4710b9e9b1.zip");
            var firefox_webdriver = StevedoreArtifact.Testing("webdrivers/geckodriver-win-x64-0210_d69db48b75b365b833c690435743b7ca4863e6491f989c8cb3d01e7e5c31779c.zip");
            NodeExe = node.Path.Combine("node-v8.11.2-win-x64/node.exe");

            sdk = new EmscriptenSdk(
                emscriptenRoot,
                llvmRoot: llvm.Path,
                pythonExe: python.Path.Combine("WinPython-64bit-2.7.13.1Zero/python-2.7.13.amd64/python.exe"),
                nodeExe: NodeExe,
                architecture: arch,
                version: emscriptenVersion,
                isDownloadable: true,
                backendRegistrables: new[] {emscripten, llvm, python, node, firefox, firefox_webdriver});
        }

        if (HostPlatform.IsLinux)
        {
            var llvm = StevedoreArtifact.Testing("emscripten-llvm-linux-x64/1.38.28-unity_400cbaaee10d3ef678ab435fb2f949e63b9301fcd694b029cddfed82acb4e430.7z");
            var node = StevedoreArtifact.Public("node-linux-x64/8.11.2_a4fccf17e141ddf01dee7fb8e6bf7cd59adb38f86927b7ec6a96d1e455d7197f.7z");
            NodeExe = node.Path.Combine("node-v8.11.2-linux-x64/bin/node");

            sdk = new EmscriptenSdk(
                emscriptenRoot,
                llvmRoot: llvm.Path.Combine("emscripten-llvm-e1.38.28-2019_03_07_23_26"),
                pythonExe: "/usr/bin/python2",
                nodeExe: NodeExe,
                architecture: arch,
                version: emscriptenVersion,
                isDownloadable: true,
                backendRegistrables: new[] {emscripten, llvm, node});
        }

        if (HostPlatform.IsOSX)
        {
            var llvm = StevedoreArtifact.Testing("emscripten-llvm-mac-x64/1.38.28-unity_a6801ef22eaa94ebbde847c8c871decde73f153e6557550eccef0c7216cadfc4.7z");
            var node = StevedoreArtifact.Public("node-mac-x64/8.11.2_3bb1156b6cba0f9d96e78f256d3c88a3dd8b8e38922995874e1fb02863d1abf2.7z");
            NodeExe = node.Path.Combine("node-v8.11.2-darwin-x64/bin/node");

            sdk = new EmscriptenSdk(
                emscriptenRoot: emscriptenRoot,
                llvmRoot: llvm.Path.Combine("emscripten-llvm-e1.38.28-2019_03_08_12_42"),
                pythonExe: "/usr/bin/python",
                nodeExe: NodeExe,
                architecture: arch,
                version: emscriptenVersion,
                isDownloadable: true,
                backendRegistrables: new[] {emscripten, llvm, node});
        }

        if (sdk == null)
            return null;

        return new EmscriptenToolchain(sdk);
    }

    public static bool UseHostToolchain = true;

    public static ZeroToolchain Windows => new ZeroToolchain
    {
        tc = (HostPlatform.IsWindows && UseHostToolchain)
            ? (WindowsToolchain) ToolChain.Store.Host()
            : ToolChain.Store.Windows().VS2017().Sdk_17134().x64(),
        burstable = false,
        hasDotnet = true
    };
    public static ZeroToolchain Windows32 =>
        new ZeroToolchain
        {
            tc = UseHostToolchain
                ? new WindowsToolchain(sdk: WindowsSdk.Locatorx86.UserDefaultOrLatest)
                : ToolChain.Store.Windows().VS2017().Sdk_17134().x86
                    (),
            burstable = false,
            hasDotnet = true
        };

    public static ZeroToolchain MacOSX => new ZeroToolchain
    {
        tc = ToolChain.Store.Host() /* TODO mac downloaded sdk */,
        burstable = false, 
        hasDotnet = true
    };
    public static ZeroToolchain Linux => new ZeroToolchain
    {
        tc = ToolChain.Store.Host() /* TODO linux downloaded sdk */,
        burstable = false, 
        hasDotnet = true
    }; 

    private static ZeroToolchain[] ToolchainList;

    public class ZeroToolchain
    {
        public ToolChain tc { get; set; }
        public bool burstable { get; set; }
        public bool hasDotnet { get; set; }
    }

    public static IEnumerable<DotsNativeConfiguration> AllPossibleRuntimeVariations()
    {
        var ret = new List<DotsNativeConfiguration>();
        foreach (var sb in new[] {ScriptingBackend.TinyIl2cpp, ScriptingBackend.Dotnet})
        {
            foreach (var t in All)
            {
                if (sb == ScriptingBackend.Dotnet && !t.hasDotnet)
                    continue;
                foreach (var cg in new[] {CodeGen.Debug, CodeGen.Release})
                {
                    foreach (var dev in new[] {true, false})
                    {
                        //debug nondev is stupid
                        if (cg == CodeGen.Debug && !dev) continue;
                                
                        // burst broke again, @elliotc is supposed to get a bee.cs in burst to stop this happening
                        var burst = false;
                        ret.Add(new DotsNativeConfiguration(cg,
                            t.tc,
                            false,
                            new DotsPlayerConfiguration(sb, burst, true, dev, t.tc)));
                    }
                }
            }
        }

        return ret;
    }

    public static ZeroToolchain[] All
    {
        get 
        {
            if (ToolchainList == null) {
                var tc = new List<ZeroToolchain>();
                if (HostPlatform.IsWindows)
                {
                    tc.Add(Windows);
                    tc.Add(Windows32);
                }
                if (HostPlatform.IsOSX)
                    tc.Add(MacOSX); 
                if (HostPlatform.IsLinux)
                    tc.Add(Linux);
                tc.Add(Emscripten_AsmJs);
                // TODO:
                //tc.Add(Emscripten_Wasm);
                ToolchainList = tc.ToArray();
            }

            return ToolchainList;
        }
    }
}

public static class ZeroCsc
{
    public static Csc Csc73 { get; } = new ZeroDownloadedCsc73();
}

class ZeroDownloadedCsc73 : ZeroDownloadedCsc
{
    public override string ActionName { get; } = "Csc";

    public ZeroDownloadedCsc73() : base(
        "roslyn-csc-linux/9d34608e19dfe308a46d51aeaa3670746272bff9_63b0fcc30e1a0939903e5073cd300d4c988d24e05621216e4c795967f45e606e.7z",
        "roslyn-csc-mac/9d34608e19dfe308a46d51aeaa3670746272bff9_a54346d2d197e8eb35114d1cb6b3797d4371743d0fbea3da406c67e5f386a952.7z",
        "roslyn-csc-win64/9d34608e19dfe308a46d51aeaa3670746272bff9_7bcbee41a6c94681ce50890758f3a052e09c165fde67e6069d83537ba5697d61.7z")
    {
    }
}

class ZeroDownloadedCsc : Csc
{
    private RunnableProgram _compilerProgram;
    private string LinuxArtifact { get; }
    private string WindowsArtifact { get; }
    private string OsxArtifact { get; }

    public ZeroDownloadedCsc(string linuxArtifact, string osxArtifact, string windowsArtifact)
    {
        if (!CanBuild())
            return;
        LinuxArtifact = linuxArtifact;
        OsxArtifact = osxArtifact;
        WindowsArtifact = windowsArtifact;
    }

    private string RoslynArtifact
    {
        get
        {
            if (HostPlatform.IsLinux)
                return LinuxArtifact;
            if (HostPlatform.IsWindows)
                return WindowsArtifact;
            if (HostPlatform.IsOSX)
                return OsxArtifact;
            throw new Exception("Unkown platform");
        }
    }

    protected override RunnableProgram CompilerProgram
    {
        get
        {
            if (_compilerProgram == null)
            {
                var artifact = StevedoreArtifact.Testing(RoslynArtifact);
                Backend.Current.Register(artifact);
                _compilerProgram = new NativeRunnableProgram(artifact.Path.Combine(HostPlatform.IsWindows ? "csc.exe" : "csc"));
            }

            return _compilerProgram;
        }
    }

    public override int PreferredUseScore { get; } = 1;
    public override bool CanBuild() => !(Backend.Current is IJamBackend);
}
