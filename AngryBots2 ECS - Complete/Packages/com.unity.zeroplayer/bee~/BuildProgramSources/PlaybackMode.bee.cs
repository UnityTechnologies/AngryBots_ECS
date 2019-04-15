using System.Collections.Generic;
using System.Linq;
using Bee.DotNet;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.NativeProgramSupport;


public class NativePlaybackMode
{
    public NativeProgramConfiguration NativeProgramConfiguration { get; }
    public bool UseLinker { get; }

    public NativePlaybackMode(NativeProgramConfiguration nativeProgramConfiguration, bool useLinker = true, JObject buildSettings = null)
    {
        NativeProgramConfiguration = nativeProgramConfiguration;
        UseLinker = useLinker;
    }

/*
 * run the patcher on the assembly and all its references
 * return a new assembly that has been patched, with references that are also patched
 * write into outputLibrary the object files produced by the compilation
 * write into rspFiles the --method arguments for the burst compiler
 * someday, this might be rolled into burst or UnityLinker, which will simplify parts of it
 */
    public static DotNetAssembly SetupBurstPatching(
        DotNetAssembly inputMainAssembly,
        NPath outputDir,
        out List<NPath> rspFiles)
    {
        var mainRspFile =
            outputDir.Combine(inputMainAssembly.Path.FileNameWithoutExtension + "_rsp.txt");
        var patchedMainAssembly = BurstCompiler.SetupPatcherInvocation(
            inputMainAssembly,
            outputDir,
            mainRspFile);
        
        var nonMainAssembliesToPatch =
            inputMainAssembly.RecursiveRuntimeDependenciesIncludingSelf.Where(d => d != inputMainAssembly).ToArray();
        var patchedNonMainAssemblies = new List<DotNetAssembly>();

        rspFiles = new List<NPath>();
        rspFiles.Add(mainRspFile);
  
        foreach (var d in nonMainAssembliesToPatch)
        {
            if (d.Path.FileName.StartsWith("System"))
            {
                patchedNonMainAssemblies.Add(d);
                continue;
            }
            var rspToWrite = outputDir.Combine(d.Path.FileNameWithoutExtension + "_rsp.txt");
            var patchedAsm = BurstCompiler.SetupPatcherInvocation(
                d,
                outputDir,
                rspToWrite);
            patchedNonMainAssemblies.Add(
                patchedAsm);
            rspFiles.Add(rspToWrite);
        }

        var ret = new DotNetAssembly(
            patchedMainAssembly.Path,
            patchedMainAssembly.Framework,
            patchedMainAssembly.DebugFormat,
            patchedMainAssembly.DebugSymbolPath,
            patchedNonMainAssemblies.ToArray(),
            patchedMainAssembly.ReferenceAssemblyPath,
            patchedMainAssembly.XmlDocPath);
        ret = ret.WithDeployables(inputMainAssembly.Deployables.Where(d => !(d is DotNetAssembly)).ToArray());
        return ret;
    }

    public static BagOfObjectFilesLibrary SetupBurstCompilationForAssemblies(
        BurstCompiler compiler,
        DotNetAssembly unpatchedInputAssembly,
        NPath rspFile,
            
        NPath outputDirForObjectFiles)
    {
        var objectFile = outputDirForObjectFiles.Combine("lib_burst_generated_part_0" + compiler.ObjectFileExtension);
        compiler.SetupInvocation(
            objectFile,
            rspFile,
            unpatchedInputAssembly.RecursiveRuntimeDependenciesIncludingSelf.Select(f => f.Path));

        return new BagOfObjectFilesLibrary(new[] {objectFile});
    }
}
