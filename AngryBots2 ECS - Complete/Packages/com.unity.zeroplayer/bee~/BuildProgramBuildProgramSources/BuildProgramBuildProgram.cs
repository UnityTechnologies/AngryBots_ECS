using System;
using System.Collections.Generic;
using System.Linq;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Bee.Core;
using Bee.VisualStudioSolution;
using Bee.DotNet;
using Bee.Stevedore;
using Newtonsoft.Json.Linq;
using Unity.BuildTools;
using Unity.BuildSystem.NativeProgramSupport;

class BuildProgramBuildProgram
{
    private static JObject _namesToAsmDefs;
    private static HashSet<string> _asmdefLookupCache = new HashSet<string>();

    static void Main(string[] path)
    {
        CSharpProgram.DefaultConfig = new CSharpProgramConfiguration(CSharpCodeGen.Debug, ZeroCsc.Csc73);

        var bee = new NPath(typeof(CSharpProgram).Assembly.Location);

        var asmdefsFile = new NPath("asmdefs.json").MakeAbsolute();

        var asmDefInfo = JObject.Parse(asmdefsFile.ReadAllText());

        if (!asmDefInfo.TryGetValue("asmdefs", out var asmdefs)) {
            throw new ArgumentException("asmdefs.json does not contain asmdefs section!");
        }

        _namesToAsmDefs = asmdefs as JObject;
        var asmdefNames = _namesToAsmDefs as IDictionary<string, JToken>;
        if (!asmDefInfo.TryGetValue("mainprograms", out var asmmains)) {
            throw new ArgumentException("asmdefs.json does not contain list of mainprograms!");
        }

        var mainPrograms = (asmmains as JArray).Values<string>().ToArray();

        var buildProgram = new CSharpProgram()
        {
            Path = path[0],
            Sources = {
                bee.Parent.Combine("BuildProgramSources"),
                HarvestBeeFilesFrom(asmdefNames.Keys.ToArray()).Distinct()
            },
            Framework = {Framework.Framework471},
            LanguageVersion = "7.2",
            References = { bee }
        };

        buildProgram.SetupDefault();
        
        var buildProgrambuildProgram = new CSharpProgram()
        {
            Path = path[0],
            Sources = {
                bee.Parent.Combine("BuildProgramBuildProgramSources")
            },
            LanguageVersion = "7.2",
            References = { bee }
        };
        new VisualStudioSolution()
        {
            Path = "build.gen.sln",
            Projects = { buildProgram, buildProgrambuildProgram }
        }.Setup();
    }

    private static IEnumerable<NPath> HarvestBeeFilesFrom(string[] names)
    {
        foreach (var name in names) {
            foreach (var bf in HarvestBeeFilesFrom(name))
                yield return bf;
        }
    }

    private static IEnumerable<NPath> HarvestBeeFilesFrom(string name)
    {
        if (_asmdefLookupCache.Contains(name))
            yield break;
        _asmdefLookupCache.Add(name);

        NPath asmDef = (string) _namesToAsmDefs[name];
        if (asmDef == null)
        {
            // some Unity assemblies are pointing to built-in assemblies
            // which are not associated to asmdef files on disk
            Console.WriteLine("Could not locate assembly definition file for: " + name);
            yield break;
        }

        //asmDef = asmDef.MakeAbsolute();

        var dotBee = asmDef.Parent.Directories("bee*~").FirstOrDefault();
        if (dotBee != null)
            foreach (var f in dotBee.Files("*.cs"))
                yield return f;

        var result = JObject.Parse(asmDef.ReadAllText());

        var jToken = result["references"];
        if (jToken==null)
            yield break;
        foreach (string r in jToken)
        {
            foreach (var referenceBeeFile in HarvestBeeFilesFrom(r))
                yield return referenceBeeFile;
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

