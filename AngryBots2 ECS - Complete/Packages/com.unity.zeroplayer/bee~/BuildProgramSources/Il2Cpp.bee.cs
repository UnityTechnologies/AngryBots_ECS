using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Bee;
using Bee.Core;
using Bee.CSharpSupport;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building;
using Bee.NativeProgramSupport.Building.FluentSyntaxHelpers;
using Bee.Stevedore;
using Bee.Toolchain.GNU;
using Bee.Toolchain.LLVM;
using Bee.Toolchain.VisualStudio;
using Bee.Toolchain.Windows;
using Bee.Toolchain.Xcode;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildSystem.VisualStudio;
using Unity.BuildTools;

public static class Il2Cpp
{
    private static NPath LocalIl2Cpp;
    private static NPath Il2CppDependencies;
    public static IFileBundle Distribution;
    public static NPath DotsRuntimeBuildFolder => Distribution.Path.Combine("libil2cppdots");

    public static void FindIl2Cpp()
    {
        NPath loc;
        if (Il2CppCustomLocation.CustomLocation != null)
        {
            loc = Il2CppCustomLocation.CustomLocation;
            if (!loc.DirectoryExists())
                throw new ArgumentException(
                    $"Il2CppCustomLocation.CustomLocation set to {loc}, but that doesn't exist");
        }
        else
        {
            loc = BuildProgram.BeeRoot.Parent.Parent.Parent.Parent.Parent.Combine("il2cpp");
        }
        if (loc.DirectoryExists() && Environment.GetEnvironmentVariable("IL2CPP_FROM_STEVE") == null) {
            Il2CppDependencies = loc.Parent.Combine("il2cpp-deps/artifacts/Stevedore");
            if (!Il2CppDependencies.DirectoryExists())
                throw new ArgumentException("We found your il2cpp checkout, but not the il2cpp-deps directory next to it.");
            LocalIl2Cpp = loc;
            Distribution = new LocalFileBundle(LocalIl2Cpp);
        }
        else
        {
            Distribution = Il2CppFromSteve();
            Il2CppDependencies = Il2CppDepsFromSteve().Path;
        }
    }

    private static IFileBundle Il2CppFromSteve()
    {                                              
        var stevedoreArtifact = StevedoreArtifact.Testing("il2cpp/19.04.03.1_0df0f38961dd90eb29656453f4b85a647b26897fe89b316df3c2bc2010038d58.7z");
        Backend.Current.Register(stevedoreArtifact);
        return stevedoreArtifact;
    }

    private static IFileBundle Il2CppDepsFromSteve()
    {
        var stevedoreArtifact = StevedoreArtifact.Testing("il2cpp-deps/19.04.03_cfd2cb9a06a084e5be5a081559c5c890325c1c00432dfb6c86005dc6cd107b3a.7z");
        Backend.Current.Register(stevedoreArtifact);
        return stevedoreArtifact;
    }

    public static BuiltNativeProgram SetupMapFileParser(NPath mapFileParserRoot, CodeGen codegen = CodeGen.Release)
    {
        var toolchain = ToolChain.Store.Host();
        var mapFileParserProgram = new NativeProgram("MapFileParser");
        mapFileParserProgram.Sources.Add(mapFileParserRoot.Files("*.cpp", true));
        mapFileParserProgram.Exceptions.Set(true);
        mapFileParserProgram.RTTI.Set(c => c.ToolChain.EnablingExceptionsRequiresRTTI);
        mapFileParserProgram.Libraries.Add(c => c.Platform is WindowsPlatform, new SystemLibrary("Shell32.lib"));
        return mapFileParserProgram.SetupSpecificConfiguration(new NativeProgramConfiguration(codegen, toolchain, false), toolchain.ExecutableFormat);
    }

    public static BuiltNativeProgram SetupLibIl2CppLackey(NPath libIl2CppLackeyRoot, WindowsToolchain toolchain)
    {
        var program = new NativeProgram("libil2cpp-lackey");
        program.Sources.Add($"{libIl2CppLackeyRoot}/DllMain.cpp");
        program.DynamicLinkerSettingsForWindows().Add(l => l.WithEntryPoint("DllMain"));
        return program.SetupSpecificConfiguration(new NativeProgramConfiguration(CodeGen.Release, toolchain, false), toolchain.DynamicLibraryFormat);
    }

    public static NPath SetupSymbolMap(NPath executableMapFile, NPath mapFileParserExe, ToolChain toolchain)
    {
        var mapFileFormat = toolchain.CppCompiler is MsvcCompiler ? "MSVC" :
            toolchain.CppCompiler is ClangCompiler ? "Clang" :
            toolchain.CppCompiler is GccCompiler ? "GCC" : throw new Exception("Unknown map file format");

        var executableSymbolMap = executableMapFile.Parent.Combine("Data/SymbolMap");
        Backend.Current.AddAction(
            "ConvertSymbolMap",
            new[] {executableSymbolMap},
            new[] {mapFileParserExe, executableMapFile},
            mapFileParserExe.InQuotes(),
            new[] {$"-format={mapFileFormat}", executableMapFile.InQuotes(), executableSymbolMap.InQuotes()});
        return executableSymbolMap;
    }

    public static NativeProgram CreateBoehmGcProgram(NPath boehmGcRoot)
    {
        var program = new NativeProgram("boehm-gc");

        program.Sources.Add($"{boehmGcRoot}/extra/gc.c");
        program.PublicIncludeDirectories.Add($"{boehmGcRoot}/include");
        program.IncludeDirectories.Add($"{boehmGcRoot}/libatomic_ops/src");
        program.Defines.Add(
            "ALL_INTERIOR_POINTERS=1",
            "GC_GCJ_SUPPORT=1",
            "JAVA_FINALIZATION=1",
            "NO_EXECUTE_PERMISSION=1",
            "GC_NO_THREADS_DISCOVERY=1",
            "IGNORE_DYNAMIC_LOADING=1",
            "GC_DONT_REGISTER_MAIN_STATIC_DATA=1",
            "NO_DEBUGGING=1",
            "GC_VERSION_MAJOR=7",
            "GC_VERSION_MINOR=7",
            "GC_VERSION_MICRO=0",
            "HAVE_BDWGC_GC",
            "HAVE_BOEHM_GC",
            "DEFAULT_GC_NAME=\"BDWGC\"",
            "NO_CRT=1",
            "DONT_USE_ATEXIT=1",
            "NO_GETENV=1");

        program.Defines.Add(c => !(c.Platform is WebGLPlatform), "GC_THREADS=1", "USE_MMAP=1", "USE_MUNMAP=1");
        program.Defines.Add(c => c.ToolChain is VisualStudioToolchain, "NOMINMAX", "WIN32_THREADS");
        //program.CompilerSettingsForMsvc().Add(l => l.WithCompilerRuntimeLibrary(CompilerRuntimeLibrary.None));
        return program;
    }

    public static NativeProgram CreateLibIl2CppProgram(bool useExceptions, NativeProgram boehmGcProgram = null, string libil2cppname = "libil2cppdots")
    {
        var fileList = Distribution.GetFileList(libil2cppname).ToArray();

        var nPaths = fileList.Where(f => f.HasExtension("cpp")).ToArray();
        var win32Sources = nPaths.Where(p => p.HasDirectory("Win32")).ToArray();
        var posixSources = nPaths.Where(p => p.HasDirectory("Posix")).ToArray();
        nPaths = nPaths.Except(win32Sources).Except(posixSources).ToArray();
      
        var program = new NativeProgram("libil2cpp")
        {
            Sources =
            {
                nPaths,
                { c => c.Platform.HasPosix, posixSources },
                { c => c.Platform is WindowsPlatform, win32Sources }
            },
            Exceptions = {useExceptions},
            PublicIncludeDirectories = {Distribution.Path.Combine(libil2cppname), Distribution.Path.Combine("libil2cpp")},
            Defines =
            {
                "NET_4_0",
                "GC_NOT_DLL",
                "RUNTIME_IL2CPP",

                "LIBIL2CPP_IS_IN_EXECUTABLE=1",
                {c => c.ToolChain is VisualStudioToolchain, "NOMINMAX", "WIN32_THREADS", "IL2CPP_TARGET_WINDOWS=1"},
                {c => c.CodeGen == CodeGen.Debug, "DEBUG", "IL2CPP_DEBUG"}
            },
            Libraries =
            {
                {
                    c => c.Platform is WindowsPlatform,
                    new[]
                    {
                        "user32.lib", "advapi32.lib", "ole32.lib", "oleaut32.lib", "Shell32.lib", "Crypt32.lib", "psapi.lib", "version.lib", "MsWSock.lib", "ws2_32.lib", "Iphlpapi.lib", "Dbghelp.lib"
                    }.Select(s=>new SystemLibrary(s))
                },
                {c => c.Platform is MacOSXPlatform, new PrecompiledLibrary[] {new SystemFramework("CoreFoundation")}},
                {c => c.Platform is LinuxPlatform, new SystemLibrary("dl")}
            }
        };
        if (boehmGcProgram != null)
            program.Libraries.Add(boehmGcProgram);
        program.RTTI.Set(c => useExceptions && c.ToolChain.EnablingExceptionsRequiresRTTI);

        if (libil2cppname == "libil2cppdots")
        {
            program.Sources.Add(Distribution.GetFileList("libil2cpp/os"));
            program.Sources.Add(Distribution.GetFileList("libil2cpp/gc"));
            program.Sources.Add(Distribution.GetFileList("libil2cpp/utils"));
            program.Sources.Add(Distribution.GetFileList("libil2cpp/vm-utils"));
            program.PublicIncludeDirectories.Add(Distribution.Path.Combine("libil2cpp"));
            program.PublicIncludeDirectories.Add(Distribution.Path.Combine("external").Combine("xxHash"));
            program.Defines.Add("IL2CPP_DOTS");
        }

        //program.CompilerSettingsForMsvc().Add(l => l.WithCompilerRuntimeLibrary(CompilerRuntimeLibrary.None));

        return program;
    }

    public static DotNetAssembly SetupLinker(DotNetAssembly inputAssembly, NativeProgramConfiguration nativeProgramConfiguration)
    {
        var linkerAssembly = new DotNetAssembly(Distribution.Path.Combine("build/UnityLinker.exe"), Framework.Framework471);
        var linker = new DotNetRunnableProgram(linkerAssembly);

        var outputDir = inputAssembly.Path.Parent.Combine("linkeroutput");

        // combine input files with overrides
        var inputFiles = inputAssembly.RecursiveRuntimeDependenciesIncludingSelf.ToList();
        var nonMainInputs = inputFiles.Exclude(inputAssembly);
        var nonMainOutputs = nonMainInputs.Select(a => Clone(outputDir, a)).ToArray();

        var newDeploy = inputFiles.SelectMany(f => f.Deployables.Where(d=>!(d is DotNetAssembly))).Distinct().ToArray();

        var mainTargetFile = Clone(outputDir, inputAssembly).WithRuntimeDependencies(nonMainOutputs)
            .WithDeployables(newDeploy);

        NPath bclDir = Il2CppDependencies.Combine("MonoBleedingEdge/builds/monodistribution/lib/mono/unityaot");

        var dotNetDeps = new[] {"mscorlib.dll", /*"System.dll",*/ "System.Configuration.dll", "System.Xml.dll", "System.Core.dll"};
        var isFrameworkNone = inputAssembly.Framework is FrameworkNone;
        var bcl = isFrameworkNone
            ? Array.Empty<DotNetAssembly>()
            : dotNetDeps
            .Select(f => new DotNetAssembly(outputDir.Combine(f), Framework.Framework46)).ToArray();

        var inputPaths = Unity.BuildTools.EnumerableExtensions.Append(inputFiles, linkerAssembly).SelectMany(a => a.Paths);
        inputPaths = Unity.BuildTools.EnumerableExtensions.Append(inputPaths, bcl.Select(d => d.Path).ToArray());

        var linkerArguments = new List<string>
        {
            $"--include-public-assembly={inputAssembly.Path.InQuotes()}",
            $"--out={outputDir.InQuotes()}",
            "--use-dots-options",
            "--dotnetprofile=" + (isFrameworkNone ? "unitydots" : "unityaot"),
            "--rule-set=experimental" // This will enable modification of method bodies to further reduce size.
        };

        foreach (var inputDirectory in inputFiles.Select(f => f.Path.Parent).Distinct())
            linkerArguments.Add($"--include-directory={inputDirectory.InQuotes()}");

        if (!isFrameworkNone)
            linkerArguments.Add($"--search-directory={bclDir.InQuotes()}");

        var targetPlatform = GetTargetPlatformForLinker(nativeProgramConfiguration);
        if (!string.IsNullOrEmpty(targetPlatform))
            linkerArguments.Add($"--platform={targetPlatform}");

        var targetArchitecture = GetTargetArchitectureForLinker(nativeProgramConfiguration);
        if (!string.IsNullOrEmpty(targetPlatform))
            linkerArguments.Add($"--architecture={targetArchitecture}");

        var targetFiles = Unity.BuildTools.EnumerableExtensions.Prepend(nonMainOutputs, mainTargetFile);
        targetFiles = Unity.BuildTools.EnumerableExtensions.Append(targetFiles, bcl);
        Backend.Current.AddAction(
            "UnityLinker",
            targetFiles: targetFiles.SelectMany(a=>a.Paths).ToArray(),
            inputs: inputPaths.ToArray(),
            executableStringFor: linker.InvocationString,
            commandLineArguments: linkerArguments.ToArray(),
            allowUnwrittenOutputFiles: false,
            allowUnexpectedOutput: false,
            allowedOutputSubstrings: new[] {"Output action"});

   
        return mainTargetFile.WithRuntimeDependencies(bcl).DeployTo(inputAssembly.Path.Parent.Combine("finaloutput"));
    }

    static string GetTargetPlatformForLinker(NativeProgramConfiguration nativeProgramConfiguration)
    {
        var platform = nativeProgramConfiguration.Platform;
        // Desktop platforms
        if (platform is WindowsPlatform)
            return "WindowsDesktop";
        if (platform is MacOSXPlatform)
            return "MacOSX";
        if (platform is LinuxPlatform)
            return "Linux";
        if (platform is UniversalWindowsPlatform)
            return "WinRT";

        // mobile
        if (platform is AndroidPlatform)
            return "Android";
        if (platform is IosPlatform)
            return "iOS";

        // consoles
        if (platform is XboxOnePlatform)
            return "XboxOne";
        if (platform is PS4Platform)
            return "PS4";
        if (platform is SwitchPlatform)
            return "Switch";

        // other
        if (platform is WebGLPlatform)
            return "WebGL";
        if (platform is LuminPlatform)
            return "Lumin";

        return null;
    }

    static string GetTargetArchitectureForLinker(NativeProgramConfiguration nativeProgramConfiguration)
    {
        var arch = nativeProgramConfiguration.ToolChain.Architecture;
        if (arch is x64Architecture)
            return "x64";
        if (arch is x86Architecture)
            return "x86";
        if (arch is ARMv7Architecture)
            return "ARMv7";
        if (arch is Arm64Architecture)
            return "ARM64";
        if (arch is EmscriptenArchitecture)
            return "EmscriptenJavaScript";

        return null;
    }

    private static DotNetAssembly Clone(NPath outputDir, DotNetAssembly a)
    {
        var debugSymbolPath = a.DebugSymbolPath == null ? null : outputDir.Combine(a.DebugSymbolPath.FileName);
        return new DotNetAssembly(outputDir.Combine(a.Path.FileName), a.Framework,a.DebugFormat, debugSymbolPath);
    }

    public static NPath SetupBurst(NPath burstPackage, DotNetAssembly inputAssembly, NPath responseFile, ToolChain toolChain)
    {
        var bcl = new DotNetRunnableProgram(new DotNetAssembly(burstPackage.Combine(".Runtime/bcl.exe"), Framework.Framework471));

        var targetFile = inputAssembly.Path.Parent.Combine($"burst_output.{toolChain.CppCompiler.ObjectExtension}");
        var inputs = Unity.BuildTools.EnumerableExtensions.Append(inputAssembly.RecursiveRuntimeDependenciesIncludingSelf.SelectMany(a => a.Paths), responseFile);

        Backend.Current.AddAction(
            "Burst",
            targetFiles: new[] {targetFile},
            inputs: inputs.ToArray(),
            executableStringFor: bcl.InvocationString,
            commandLineArguments: new[] {$"--assembly-folder={inputAssembly.Path.Parent}", $"--output={targetFile}", "--keep-intermediate-files", $"@{responseFile.ToString(SlashMode.Native)}"},
            allowUnexpectedOutput: false,
            allowedOutputSubstrings: new[] {"Link succesful", "Method:"});
        return targetFile;
    }

    public static void SetupIL2CPPTestInfrastructure()
    {
        var config = new CSharpProgramWithNativeDependenciesConfiguration(CSharpCodeGen.Debug,
            ZeroCsc.Csc73,
            new NativeProgramConfiguration(CodeGen.Debug, (WindowsToolchain)ZeroToolchains.Windows.tc, false));
        var pinvokelib = new NativeProgram("libil2cpp-pinvoke-test-target")
        {
            Sources = {Il2Cpp.Distribution.Path.Combine("libil2cpp-pinvoke-test-target")},
            Defines = {"PINVOKE_TARGET_WINDOWS"},
            Libraries =
            {
                new StaticLibrary("OleAut32.lib"),
                new StaticLibrary("Ole32.lib"),
                new StaticLibrary("User32.lib")
            }
        };
        
        var tests = new CSharpProgramWithNativeDependencies()
        {
            FileName = "TinyCorlibTests.dll",
            Sources =
            {
                Il2Cpp.Distribution.Path.Combine("Unity.IL2CPP.IntegrationTests.Tiny"),
                BuildProgram.BeeRoot.Combine("il2cpp_test_infrastructure/IL2CPPTestFrameworkStubs.cs")
            },
            Defines = {"UNITY_TINY"},
            Framework = {Framework.FrameworkNone},
            References = {BuildProgram.TinyCorlib},
            Unsafe = true,
            WarningsAsErrors = false,
            NativePrograms = { pinvokelib}
        };
        var testAssembly = tests.SetupSpecificConfiguration(config);
        
        var testProgramMakerProgram = new CSharpProgram()
        {
            FileName = "TestProgramCreator.exe",
            Sources = {BuildProgram.BeeRoot.Combine("il2cpp_test_infrastructure/TestProgramCreator.cs")},
            References = {BuildProgram.MonoCecilAsmPaths},
            CopyReferencesNextToTarget = true
        };
        var testProgramMaker = testProgramMakerProgram.SetupDefault();

        NPath generatedMainCs = "artifacts/il2cpptests/main.cs";
        Backend.Current.AddAction("CreateTestProgram", 
            targetFiles:new[]{generatedMainCs},
            inputs:new[] { testAssembly.Path, testProgramMaker.Path},
            executableStringFor:new DotNetRunnableProgram(testProgramMaker).InvocationString,
            commandLineArguments:new[]{generatedMainCs.ToString(SlashMode.Native), testAssembly.Path.InQuotes(SlashMode.Native)}
        );

        var testRunnerProgram = new CSharpProgramWithNativeDependencies()
        {
            Sources = {generatedMainCs},
            References = { testAssembly, BuildProgram.TinyCorlib },
            FileName = "TinyTestRunner.exe",
            CopyReferencesNextToTarget = true,
            Framework = {Framework.FrameworkNone},
        };
        var testRunner = testRunnerProgram.SetupSpecificConfiguration(config);
        
        NPath dotNetTestOutput = "artifacts/corlibtests/testoutput_dotnet.xml";
        Backend.Current.AddAction("RunCorlibTestsOnDotNet", 
            targetFiles:new[]{dotNetTestOutput},
            inputs: testRunner.RecursiveRuntimeDependenciesIncludingSelf.Select(d=>d.Path).ToArray(),
            executableStringFor:$"{new DotNetRunnableProgram(testRunner).InvocationString} > {dotNetTestOutput.InQuotes(SlashMode.Native)}",
            commandLineArguments:new string[]{}
        );
        /*
         * this seems like it would be useful if it weren't dead, so I'm leaving it here in the hopes that it makes
         * a comeback
         * --@elliotc 4/11/2019
         */ 
        /*var il2cppVersion = new NativePlaybackMode(config.NativeProgramConfiguration, useLinker:false).Deploy(testRunner, "il2cpp");

        NPath il2cppTestOutput = "artifacts/corlibtests/testoutput_il2cpp.xml";
        Backend.Current.AddAction("RunCorlibTestsOnIl2Cpp",
            targetFiles: new[] {il2cppTestOutput},
            inputs: new[] {il2cppVersion.Path},
            executableStringFor:$"{il2cppVersion.Path.InQuotes(SlashMode.Native)} > {il2cppTestOutput.InQuotes(SlashMode.Native)}",
            commandLineArguments:new string[]{}
        );

        var testResultsComparerProgram = new CSharpProgram()
        {
            FileName = "TinyCorlibTestResultComparer.exe",
            Sources = {BuildProgram.BeeRoot.Combine("il2cpp_test_infrastructure/TinyCorlibTestResultComparer.cs")},
            Framework = {Framework.Framework46},
            References = {new SystemReference("System.Xml"), new SystemReference("System.Xml.Linq")},
            CopyReferencesNextToTarget = true
        };
        var testResultComparer = testResultsComparerProgram.SetupDefault();
        
        NPath comparisonresultTxt = "artifacts/corlibtests/comparisonresult.txt";
        Backend.Current.AddAction("CompareIl2CPPAndDotNetTinyCorlibTestResults", 
            targetFiles:new[]{comparisonresultTxt},
            inputs: testResultComparer.RecursiveRuntimeDependenciesIncludingSelf.Select(d=>d.Path).Append(dotNetTestOutput, il2cppTestOutput).ToArray(),
            executableStringFor:$"{new DotNetRunnableProgram(testResultComparer).InvocationString}",
            commandLineArguments:new string[]{dotNetTestOutput.InQuotes(SlashMode.Native), il2cppTestOutput.InQuotes(SlashMode.Native), comparisonresultTxt.InQuotes(SlashMode.Native)}
        );
        
        Backend.Current.AddAliasDependency("il2cpptests", comparisonresultTxt);

        _ilcppTestInfrastructurePrograms = new[] {testRunnerProgram, tests, testProgramMakerProgram, testResultsComparerProgram};*/
    }

    public static CSharpProgram[] _ilcppTestInfrastructurePrograms;
}
