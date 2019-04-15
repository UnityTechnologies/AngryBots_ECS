using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Dots.Build;
using Bee.Core;
using Bee.CSharpSupport;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building;
using Bee.Stevedore;
using Bee.Toolchain.Xcode;
using Bee.VisualStudioSolution;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;
using Module = Unity.Dots.Build.Module;

public class BuildProgram
{
    private static NPath BeeRootValue = null;

    public static NPath BeeRoot
    {
        get {
            if (BeeRootValue == null)
                throw new InvalidOperationException("BeeRoot accessed before it has been initialized");
            return BeeRootValue;
        }
    }

    public static NPath LowLevelRoot { get; set; }

    public static CSharpProgram TinyCorlib { get; set; }

    public static NPath[] MonoCecilAsmPaths { get; set; }
    public static CSharpProgram EntityBuildUtils { get; set; }

    public static Module UnityLowLevel { get; set; }

    public static DotNetAssembly UnsafeUtility { get; set; }
    public static NPath BurstExecutablePath { get; set; }

    public static CSharpProgram ZeroJobs { get; set; }

    public static CSharpProgram TypeRegGen { get; set; }
    public static DotNetRunnableProgram TypeRegGenProgram { get; set; }
    public static CSharpProgram BurstPatcher { get; set; }
    public static CSharpProgram UnpatchedUnsafeUtility { get; set; }

    public static DotNetRunnableProgram BurstPatcherProgram { get; set; }

    public static DotNetAssembly TestFramework { get; set; }
    public static DotNetAssembly NUnitLite { get; set; }

    public class NameAndInfo
    {
        public string name;
        public bool forceExe;
        public bool tinyCorlib;

        public NameAndInfo(string n, bool exe, bool tiny)
        {
            name = n;
            forceExe = exe;
            tinyCorlib = tiny;
        }

        public override bool Equals(object obj)
        {
            if (!(obj is NameAndInfo))
                return false;
            var other = (NameAndInfo)obj;
            return other.name == name && other.forceExe == forceExe && other.tinyCorlib == tinyCorlib;
        }

        public override int GetHashCode()
        {
            return name.GetHashCode();
        }
    }

    static readonly Dictionary<NameAndInfo, CSharpProgram> _namesToPrograms = new Dictionary<NameAndInfo, CSharpProgram>();
    private static JObject _namesToAsmDefs;

    private static Type[] ModuleForAsmDefTypes = typeof(ModuleForAsmDef).Assembly.GetTypes()
        .Where(IsModuleForAsmDefDerivedClass).ToArray();

    //public static NPath BeeRoot = "c:/u/Zero/Packages/com.unity.zeroplayer/.bee";

    static void Main()
    {
        CSharpProgram.DefaultConfig = new CSharpProgramConfiguration(CSharpCodeGen.Debug, ZeroCsc.Csc73);    
        var asmDefInfo = JObject.Parse(new NPath("asmdefs.json").MakeAbsolute().ReadAllText());

        if (!asmDefInfo.TryGetValue("asmdefs", out var asmdefs)) {
            throw new ArgumentException("asmdefs.json does not contain asmdefs section!");
        }
        
        _namesToAsmDefs = asmdefs as JObject;

        // figure out where Unity.ZeroPlayer is located, so that we can build paths based on it

        if (!_namesToAsmDefs.TryGetValue("Unity.ZeroPlayer", out var zpAsmDef)) {
            throw new ArgumentException("Couldn't find Unity.ZeroPlayer asmdef in asmdefs.json, was it generated properly?");
        }
        BeeRootValue = new NPath((string)zpAsmDef).Parent.Parent.Combine("bee~");

        Il2Cpp.FindIl2Cpp();
        // read asmdefs.json to get our asmdef mapping

        LowLevelRoot = BeeRoot.Parent.Combine("LowLevelSupport~");
        if (!_namesToAsmDefs.TryGetValue("Unity.Burst", out var burstpath))
            throw new ArgumentException("Couldn't find Unity.Burst asmdef in asmdefs.json, was it generated properly?");

        BurstExecutablePath = new NPath(burstpath.ToString()).Parent.Parent.Combine(".Runtime/bcl.exe");

        var drbc = new DotsRuntimeBuildCode();
        drbc.Setup();
    }

    public static void SetupTestFramework()
    {
        var nunit = StevedoreArtifact.Testing(
            "nunit-framework/3.10.1_e6901c1f76494705a2c387c1446a2f16e050509fbb8583ef4c38fc51279dd31d.zip");
        Backend.Current.Register(nunit);

        NUnitLite = new DotNetAssembly(nunit.Path.Combine("bin", "net40", "nunitlite.dll"), Framework.Framework40);

        TestFramework = new DotNetAssembly(nunit.Path.Combine("bin", "net40", "nunit.framework.dll"), Framework.Framework40);
    }

    public static void SetupSupportTools()
    {
        TinyCorlib = new CSharpProgram() {
            FileName = "mscorlib.dll",
            Sources = {Il2Cpp.Distribution.GetFileList("mscorlib").Where(f => f.HasExtension("cs"))},
            Defines = {"UNITY_ZEROPLAYER"},
            ProjectFilePath = "tinycorlib.gen.csproj",
            Framework = {Framework.FrameworkNone},
            Unsafe = true,
            LanguageVersion = "7.3"
        };
        TinyCorlib.Defines.Add(c => c.CodeGen == CSharpCodeGen.Debug, "DEBUG");

        MonoCecilAsmPaths = MonoCecilAsmPathsFromStevedore();

        SetupUnityLowLevel();

        EntityBuildUtils = new CSharpProgram()
        {
            FileName = "Unity.Entities.BuildUtils.dll",
            Sources =
            {
                new NPath(_namesToAsmDefs["Unity.Entities.BuildUtils"].ToString()).Parent.Files("*.cs", recurse: false)
            },
            Unsafe = true,
            References = {MonoCecilAsmPaths},
            LanguageVersion = "7.2"
        };

        ZeroJobs = DotsRuntimeBuildCode.CSharpProgramForModule(
            DotsRuntimeBuildCode.ModuleFromAsmDefName("Unity.ZeroJobs"),
            false);

        var cecilRefs = new NPath($"{LowLevelRoot}/UnsafeUtilityPatcher").Files("*.dll");
        TypeRegGen = new CSharpProgram()
        {
            Path = "artifacts/TypeRegGen/TypeRegGen.exe",
            Sources = { BeeRoot.Parent.Combine("TypeRegGen") },
            Unsafe = true,
            Defines = { "NDESK_OPTIONS" },
            References = {
                EntityBuildUtils,
                MonoCecilAsmPaths
                },
            LanguageVersion = "7.3"
        };

        TypeRegGenProgram = new DotNetRunnableProgram(TypeRegGen.SetupDefault());

        BurstPatcher = new CSharpProgram()
        {
            Path = "artifacts/BurstPatcher/BurstPatcher.exe",
            Sources = {BeeRoot.Parent.Combine("BurstPatcher~")},
            References = {MonoCecilAsmPaths}, 
            LanguageVersion = "7.3"
        };
        
        BurstPatcherProgram = new DotNetRunnableProgram(BurstPatcher.SetupDefault());
    }

    private static NPath[] MonoCecilAsmPathsFromStevedore()
    {
        var cecilArtifact = StevedoreArtifact.Testing("unity-cecil/b093701f8ba7b54aea0c62ac08f376b783a0cf98_b3fb8db6e2d68564c9e28c3713b6f742d4c928aef8a420028d3a149af7d67152.zip");
        Backend.Current.Register(cecilArtifact);

        List<NPath> dlls = new List<NPath>();
        dlls.Add(cecilArtifact.Path.Combine("lib", "net40", "Unity.Cecil.dll"));
        dlls.Add(cecilArtifact.Path.Combine("lib", "net40", "Unity.Cecil.Rocks.dll"));
        dlls.Add(cecilArtifact.Path.Combine("lib", "net40", "Unity.Cecil.Mdb.dll"));
        dlls.Add(cecilArtifact.Path.Combine("lib", "net40", "Unity.Cecil.Pdb.dll"));

        return dlls.ToArray();
    }

    public static List<DotNetAssembly> GenerateTypeReg(string targetName,
        DotNetAssembly inputAssembly,
        NPath outdir,
        CodeGen codeGen,
        int architectureBits,
        bool isForDotNet)
    {
        var alldeps = inputAssembly.RecursiveRuntimeDependenciesIncludingSelf.Select(m => m.Path).ToArray();

        var args = new List<string>();
        args.Add(outdir.MakeAbsolute().ToString());
        args.Add(architectureBits.ToString());
        args.Add(isForDotNet ? "DOTSDotNet" : "DOTSNative");
        args.AddRange(alldeps.Select(p => p.MakeAbsolute().ToString()));

        // We will be passing in all assemblies from 'inputAssembly' to TypeRegGen which will output modified versions of each.
        // As such, create a list of DotNetAssemblies which point to the files TypeRegGen will create
        var replacements = new List<DotNetAssembly>();
        foreach (var file in inputAssembly.RecursiveRuntimeDependenciesIncludingSelf)
        {
            NPath replacementPath = outdir.Combine(file.Path.FileName);
            NPath replacementDebugPath = null;
            DotNetAssembly replacementAsm = null;

            if (file.DebugFormat != null)
            {
                replacementDebugPath = outdir.Combine(file.Path.FileName).ChangeExtension(file.DebugFormat.Extension);
            }

            if (file.Path.FileName.Contains("Unity.Entities.StaticTypeRegistry"))
                replacementAsm = new DotNetAssembly(replacementPath, file.Framework);
            else
                replacementAsm = new DotNetAssembly(replacementPath, file.Framework, file.DebugFormat, replacementDebugPath);

            var deployables = file.Deployables.Where(d => !(d is DotNetAssembly)).Distinct().ToArray();
            replacements.Add(replacementAsm.WithDeployables(deployables));
        }

        // Create list of all runtime dependencies and any pdbs/mdbs they may have. This list will be output by the TypeRegGen and act
        // as input dependencies for later steps (now dependent in TypeRegGen running first)
        var outputFiles = replacements
            .Select(asm => asm.Path)
            .Append(replacements.Select(asm => asm.DebugSymbolPath).Where(p => p != null).ToArray())
            .ToArray();
        var inputFiles = Unity.BuildTools.EnumerableExtensions.Append(alldeps, TypeRegGenProgram.Path).ToArray();

        Backend.Current.AddAction("TypeRegGen",
            outputFiles,
            inputFiles,
            TypeRegGenProgram.InvocationString,
            args.ToArray(),
            allowedOutputSubstrings: new[] {"Static Type Registry Generation Time:"});

        return replacements;
    }

    public static void SetupUnityLowLevel()
    {
        var patcher = new CSharpProgram() {
            Path = "artifacts/UnsafeUtilityPatcher/UnsafeUtilityPatcher.exe",
            Sources = {$"{LowLevelRoot}/UnsafeUtilityPatcher"},
            Defines = {"NDESK_OPTIONS"},
            References = {MonoCecilAsmPaths},
            LanguageVersion = "7.3"
        };

        var builtPatcher = patcher.SetupDefault();

        UnpatchedUnsafeUtility = new CSharpProgram() {
            Path = "artifacts/UnsafeUtilityUnpatched/UnsafeUtility.dll",
            Sources = {$"{LowLevelRoot}/UnsafeUtility"},
            LanguageVersion = "7.3",
            Unsafe = true,
            ProjectFilePath = $"UnsafeUtility.gen.csproj"
        };

        var nonPatchedUnsafeUtility = UnpatchedUnsafeUtility.SetupDefault();

        var builtPatcherProgram = new DotNetRunnableProgram(builtPatcher);
        NPath nPath = "artifacts/UnsafeUtility/UnsafeUtility.dll";
        var args = new[] {
            $"--output={nPath}",
            $"--assembly={nonPatchedUnsafeUtility.Path}",
        };

        UnsafeUtility = new DotNetAssembly(nPath, nonPatchedUnsafeUtility.Framework,
            nonPatchedUnsafeUtility.DebugFormat,
            nPath.ChangeExtension("pdb"), nonPatchedUnsafeUtility.RuntimeDependencies,
            nonPatchedUnsafeUtility.ReferenceAssemblyPath);

        Backend.Current.AddAction("Patch", UnsafeUtility.Paths,
            nonPatchedUnsafeUtility.Paths.Concat(builtPatcher.Paths).ToArray(), builtPatcherProgram.InvocationString,
            args);

        UnityLowLevel = new Module("Unity.LowLevel", LowLevelRoot);
        UnityLowLevel.Cpp.Add($"{LowLevelRoot}/Unity.LowLevel/liballocators");
        UnityLowLevel.Cs.Add($"{LowLevelRoot}/Unity.LowLevel");
        UnityLowLevel.ManagedReferences.Add(UnsafeUtility, TinyCorlib);
        UnityLowLevel.NativeProgramName = "liballocators";
        UnityLowLevel.Unsafe = true;
        UnityLowLevel.UseBindGem = false;
    }

    private static bool IsModuleForAsmDefDerivedClass(Type t)
    {
        if (t.IsAbstract)
            return false;
        if (t == typeof(ModuleForAsmDef))
            return false;
        return typeof(ModuleForAsmDef).IsAssignableFrom(t);
    }

    internal static CSharpProgram CSharpProgramFromAsmDefName(string asmDefName, bool forceExe = false, bool tinyCorlib = true)
    {
        var ninfo = new NameAndInfo(asmDefName, forceExe, tinyCorlib);
        if (_namesToPrograms.TryGetValue(ninfo, out var previousResult))
            return previousResult;

        Console.WriteLine("Generating program for assembly: " + asmDefName);

        NPath asmDef = (string)_namesToAsmDefs[asmDefName];

        if (asmDef == null)
        {
            throw new Exception("Could not find assembly definition file for assembly: " + asmDefName);
        }

        var customType = FindCorrectType(asmDefName);

        var result = (CSharpProgram) Activator.CreateInstance(customType, forceExe, JObject.Parse(asmDef.ReadAllText()), asmDef, tinyCorlib);
        _namesToPrograms[ninfo] = result;
        return result;
    }

    private static Type FindCorrectType(string name)
    {
        foreach (var type in ModuleForAsmDefTypes)
        {
            var m = (ModuleForAsmDefAttribute) type.GetCustomAttribute(typeof(ModuleForAsmDefAttribute));
            if (m == null)
                continue;
            if (m.ModuleName != name)
                continue;
            return type;
        }

        return typeof(ModuleForAsmDef);
    }

    public static CSharpCodeGen CodeGenToCSCodeGen(CodeGen codeGen)
    {
        switch (codeGen)
        {
            case CodeGen.Debug:
                return CSharpCodeGen.Debug;
            case CodeGen.Master:
            case CodeGen.Release:
                return CSharpCodeGen.Release;
            default:
                throw new Exception("Unhandled CodeGen value.");
        }
    }
}

public class ModuleForAsmDefAttribute : Attribute
{
    public ModuleForAsmDefAttribute(string moduleName)
    {
        ModuleName = moduleName;
    }

    public string ModuleName { get; }
}

