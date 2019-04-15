using System;
using System.Collections.Generic;
using System.Linq;
using Bee.Core;
using Bee.DotNet;
using NiceIO;
//using Unity.BuildTools;

public abstract class BurstCompiler // : ObjectFileProducer
{
    public static NPath ProjectPath { get; } = "..";
    public static NPath BurstExecutable { get; set; } = BuildProgram.BurstExecutablePath;
    public static NPath BurstPatcherExecutable { get; } = BuildProgram.BurstPatcherProgram.Path;

    public abstract string TargetPlatform { get; }
    public abstract string TargetArchitecture { get; }
    public abstract string ObjectFormat { get; }
    public abstract string ObjectFileExtension { get; }

    // Options
    public virtual bool SafetyChecks { get; } = false;
    public virtual bool DisableVectors { get; } = false;
    public virtual bool Link { get; } = true;
    public abstract string FloatPrecision { get; }

    public NPath[] AssemblyFolders { get; } =
    {
        ProjectPath.Combine("Library/PackageCache/com.unity.mathematics@0.0.12-preview.20"),
        ProjectPath.Combine("Library/ScriptAssemblies/")
    };

    public void SetupInvocation(NPath objectFile, NPath rspFile, IEnumerable<NPath> inputAssemblies)
    {
        //var env = new Dictionary<string, string>();
        //env["UNITY_BURST_DEBUG"] = "1";
        Backend.Current.AddAction(
            "Burst",
            new[] {objectFile},
            inputAssemblies.Append(BurstExecutable).Append(rspFile).ToArray(),
            BurstExecutable.ToString(SlashMode.Native),
            new[]
            {
                $"@{rspFile}",
                $"--assembly-folder=\"{String.Join(";", AssemblyFolders.Concat(inputAssemblies.Select(asm => asm.Parent)))}\"",
                $"--platform={TargetPlatform}",
                $"--backend=burst-llvm",
                $"--target={TargetArchitecture}",
                //$"--format={ObjectFormat}",
                SafetyChecks ? "--safety-checks" : "",
                $"--dump=\"None\"",//\"IROptimized\"",
                DisableVectors ? "--disable-vectors" : "",
                Link ? "" : "--nolink",
                $"--float-precision={FloatPrecision}",
                $"--keep-intermediate-files",
                $"--output={objectFile.Parent.Combine(objectFile.FileNameWithoutExtension)}"
            } //,
            //environmentVariables: env
        );
    }


    public static DotNetAssembly SetupPatcherInvocation(DotNetAssembly _inputAssembly, NPath outDir, NPath rspToWrite)
    {
        var inputAssembly = _inputAssembly.Path;
        var patchedAssemblyPath = outDir.Combine(inputAssembly.FileName);
        var patchedDebugSymbolPath = _inputAssembly.DebugSymbolPath!= null ? patchedAssemblyPath.ChangeExtension(".pdb") : null;

        var targetFiles = new[] {patchedAssemblyPath, rspToWrite};
        var inputFiles = new[] {inputAssembly, BurstPatcherExecutable};

        if (patchedDebugSymbolPath != null)
        {
            inputFiles = inputFiles.Append(_inputAssembly.DebugSymbolPath).ToArray();
            targetFiles = targetFiles.Append(patchedDebugSymbolPath).ToArray();
        }

        Backend.Current.AddAction(
            "BurstPatcher",
            targetFiles,
            inputFiles,
            BurstPatcherExecutable.ToString(SlashMode.Native),
            new[]
            {
                inputAssembly.ToString(SlashMode.Native),
                patchedAssemblyPath.ToString(SlashMode.Native),
                rspToWrite.ToString(SlashMode.Native)
            });
        return new DotNetAssembly(
            patchedAssemblyPath,
            _inputAssembly.Framework,
            _inputAssembly.DebugFormat,
            patchedDebugSymbolPath,
            _inputAssembly.RuntimeDependencies,
            _inputAssembly.ReferenceAssemblyPath,
            _inputAssembly.XmlDocPath);
    }
}

public class BurstCompilerForEmscripten : BurstCompiler
{
    public override string TargetPlatform { get; } = "Wasm";
    public override string TargetArchitecture { get; } = "WASM32";
    public override string ObjectFormat { get; } = "Wasm";
    public override string FloatPrecision { get; } = "High";
    public override bool SafetyChecks { get; } = true;
    public override bool DisableVectors { get; } = true;
    public override bool Link { get; } = false;
    public override string ObjectFileExtension { get; } = ".ll";
}

public class BurstCompilerForWindows : BurstCompiler
{
    public override string TargetPlatform { get; } = "Windows";

    //--target=VALUE         Target CPU <Auto|X86_SSE2|X86_SSE4|X64_SSE2|X64_
    //    SSE4|AVX|AVX2|AVX512|WASM32|ARMV7A_NEON32|ARMV8A_
    //    AARCH64|THUMB2_NEON32> Default: Auto
    public override string TargetArchitecture { get; } = "X64_SSE2";
    public override string ObjectFormat { get; } = "Coff";
    public override string FloatPrecision { get; } = "High";
    public override bool SafetyChecks { get; } = true;
    public override bool DisableVectors { get; } = false;
    public override bool Link { get; } = false;//true;
    public override string ObjectFileExtension { get; } = ".obj";
}
