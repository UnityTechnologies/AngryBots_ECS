using System;
using System.Collections.Generic;
using System.Diagnostics;
using Bee.Core;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building;
using Bee.NativeProgramSupport.Building.FluentSyntaxHelpers;
using Bee.Toolchain.Emscripten;
using Bee.Toolchain.VisualStudio;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;

public class Il2CppProgram
{

    // for a future where we support il2cpp debugging in dots runtime
    private IEnumerable<string> Il2CppStaticDebugOutputFiles =>
        new[]
        {
            "Il2CppDebuggerMetadataRegistration.cpp", "Il2CppMethodExecutionInfoIndexTable.cpp",
            "Il2CppMethodExecutionInfoStringTable.cpp", "Il2CppMethodExecutionInfoTable.cpp",
            "Il2CppMethodExecutionInfoTableData.cpp", "Il2CppMethodHeaderInfoTable.cpp", "Il2CppMethodScopeTable.cpp",
            "Il2CppSequencePointIndexTable.cpp",
            "Il2CppSequencePointSourceFileTable.cpp", "Il2CppSequencePointTable.cpp",
            "Il2CppSequencePointTableData.cpp", "Il2CppTypeSourceFileTable.cpp",
        };

    public DotNetAssembly InputAssembly { get; }
    public string Profile { get; }
    public bool EnableDebugger { get; }

    private static NativeProgram BoehmGCLib { get; } = Il2Cpp.CreateBoehmGcProgram(Il2Cpp.Distribution.Path.Combine("external/bdwgc"));

    public static NativeProgram TinyLibIl2cpp { get; } =
        Il2Cpp.CreateLibIl2CppProgram(useExceptions: false, boehmGcProgram: BoehmGCLib);

    private static NativeProgram BigLibIl2cpp { get; } =
        Il2Cpp.CreateLibIl2CppProgram(useExceptions: true, boehmGcProgram: BoehmGCLib, libil2cppname: "libil2cpp");

    public static EmscriptenDynamicLinker ModifiedLinkerFor(
        EmscriptenDynamicLinker e,
        CodeGen codeGen,
        bool isDevelopmentBuild,
        JObject buildSettings = null,
        bool forRuntimeBuild = false)
    {
        var linkflags = new Dictionary<string, string>
        {
            // Bee defaults to PRECISE_F32=2, which is not an interesting feature for Dots. In Dots asm.js builds, we don't
            // care about single-precision floats, but care more about code size.
            {"PRECISE_F32", "0"},
            // No exceptions machinery needed, saves code size
            {"DISABLE_EXCEPTION_CATCHING", "1"},
            // No virtual filesystem needed, saves code size
            {"NO_FILESYSTEM", "1"},
            // Make generated builds only ever executable from web, saves code size.
            // TODO: if/when we are generating a build for node.js test harness purposes, remove this line.
            {"ENVIRONMENT", "web"},
            // In -Oz builds, Emscripten does compile time global initializer evaluation in hope that it can
            // optimize away some ctors that can be compile time executed. This does not really happen often,
            // and with MINIMAL_RUNTIME we have a better "super-constructor" approach that groups all ctors
            // together into one, and that saves more code size. Unfortunately grouping constructors is
            // not possible if EVAL_CTORS is used, so disable EVAL_CTORS to enable grouping.
            {"EVAL_CTORS", "0"},
            // We don't want malloc() failures to trigger program exit and abort handling, but instead behave
            // like C runtimes do, and make malloc() return null. This saves code size and lets our code
            // handle oom failures.
            { "ABORTING_MALLOC", "0"},
            // By default the musl C runtime used by Emscripten is POSIX errno aware. We do not care about
            // errno, so opt out from errno management to save a tiny bit of performance and code size.
            {"SUPPORT_ERRNO", "0"}
        };
        


        bool wasm = (e.Toolchain == ZeroToolchains.Emscripten_Wasm);
        e = e.WithWasm(wasm);
        if (!wasm)
        {
            linkflags["LEGACY_VM_SUPPORT"] = "1";
            e = e.WithSeparateAsm(true);
        }

        if (codeGen == CodeGen.Debug || isDevelopmentBuild)
        {
            linkflags["ASSERTIONS"] = "2";
            linkflags["DEMANGLE_SUPPORT"] = "1";
        }
        else
        {
            linkflags["ASSERTIONS"] = "0";
            linkflags["AGGRESSIVE_VARIABLE_ELIMINATION"] = "1";
            linkflags["ELIMINATE_DUPLICATE_FUNCTIONS"] = "1";
        }
        
        if (buildSettings != null &&
            buildSettings.TryGetValue("emscriptenLinkSettings", out var emscriptenLinkSettingsToken)
            && emscriptenLinkSettingsToken is JObject emscriptenLinkSettings)
        {
            foreach (var setting in emscriptenLinkSettings)
            {
                e = e.WithEmscriptenSetting(setting.Key, setting.Value.Value<string>());
            }
        }
        e = e.WithEmscriptenSettings(linkflags);
        e = e.WithNoExitRuntime(true);
        if (codeGen == CodeGen.Debug)
        {
            e = e.WithDebugLevel("3");
        }
        else
        {
            e = e.WithDebugLevel("0").WithOptLevel("z");
            e = e.WithLinkTimeOptLevel(3);
            e = e.WithEmitSymbolMap(true);
            // TODO: Enable Closure when Java JRE distribution issue is resolved
            // (currently Closure requires Java)
//            e = e.WithClosure(EmscriptenClosureMode.Enable);
        }
        if (isDevelopmentBuild)
        {
            e = e.WithDebugLevel("2");
            e = e.WithLinkTimeOptLevel(1);
            e = e.WithEmitSymbolMap(false);
        }
        e = e.WithMinimalRuntime(EmscriptenMinimalRuntimeMode.EnableDangerouslyAggressive);

        // Bee is not yet aware of the new --closure-externs (and --closure-annotations) linker flags, so add them using the generic
        // escape hatch hook.
        e = e.WithCustomFlags_workaround(new string[] {"--closure-externs", BuildProgram.BeeRoot.Combine("closure_externs.js").ToString() });

        // TODO: Remove this line once Bee fix is in to support SystemLibrary() objects on web builds. Then restore
        // the line Libraries.Add(c => c.ToolChain.Platform is WebGLPlatform, new SystemLibrary("GL")); at the top of this file
        e = e.WithCustomFlags_workaround(new string[] {"-lGL" });

        return e;
    }
}
