using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Bee.Core;
using Bee.CSharpSupport;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building;
using Bee.VisualStudioSolution;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;

internal class BindGem
{
    private static BindGem _instance;
    public static BindGem Instance() => _instance ?? (_instance = new BindGem());

    private BindGem()
    {
        var bindgemSource = BuildProgram.BeeRoot.Parent.Combine("BindGem~");

        BindGemProgram = new CSharpProgram()
        {
            Sources = {bindgemSource},
            Path = "artifacts/bindgem/bindgem.exe",
            References = {
                new SystemReference("System.Xml.Linq"), new SystemReference("System.Xml"),
                new NPath($"{BuildProgram.LowLevelRoot}/UnsafeUtilityPatcher").Files("*.dll"),
                BuildProgram.MonoCecilAsmPaths,
                BuildProgram.EntityBuildUtils},
            LanguageVersion = "7.3",
            WarningsAsErrors = false,
            CopyReferencesNextToTarget = true,
            ProjectFilePath = $"BindGem.gen.csproj",
        };
        BuiltBindGemProgram = BindGemProgram.SetupDefault();

        Backend.Current.AddAliasDependency("BuildBindGem", BuiltBindGemProgram.Path);
    }

    public CSharpProgram BindGemProgram { get; }
    public DotNetAssembly BuiltBindGemProgram { get; }

    public static NPath BindGemOutputDir => "artifacts/bindgem_output";

    public class Result
    {
        public NPath Header;
        public NPath Cpp;

        public NPath[] Files => new[] {Header, Cpp}.Where(p => p != null).ToArray();
    }

    public Result SetupInvocation(
        string name,
        NPath assembly,
        DotNetAssembly[] referenceAssemblies,
        NPath[] additionalIncludesForBindGemCpp,
        NPath[] additionalIncludesForBindGemHeader,
        NPath modulePath,
        NPath outputPrefix,
        bool skipAction = false)
    {
        var result = new Result()
        {
            Header = outputPrefix.ChangeExtension("h"),
            Cpp = outputPrefix.ChangeExtension("cpp"),
        };

        // this is a hack to avoid adding a second invocation action
        if (skipAction)
            return result;

        var args = new List<string> {
            "-v",
            "-dots",
            referenceAssemblies.Select(a => $"-r {a.Path.InQuotes()}"),
            additionalIncludesForBindGemHeader.Select(a => $"-cppInclude {a.InQuotes()}"),
            additionalIncludesForBindGemCpp.Select(a => $"-cppSrcInclude {a.InQuotes()}"),
            $"-define_guard BUILD_{name.ToUpper().Replace(".", "_")}",
            assembly.InQuotes(),
            "-o",
            outputPrefix.InQuotes()
        };

        string mono = "";
        if (!HostPlatform.IsWindows) {
            mono = $"{Paths.MonoBleedingEdgeCLI.InQuotes()} --debug ";
        }
        
        var inputs = Unity.BuildTools.EnumerableExtensions.Append(new List<NPath>(),assembly);
        inputs = Unity.BuildTools.EnumerableExtensions.Append(inputs, BuiltBindGemProgram.Path);
        inputs = Unity.BuildTools.EnumerableExtensions.Append(inputs, referenceAssemblies.Select(r => r.Path).ToArray());
        inputs = inputs.Append(additionalIncludesForBindGemCpp).Append(additionalIncludesForBindGemHeader);

        // Note: the MakeAbsolute() below also takes care of changing slashes on Windows,
        // because Windows really hates forward slashes when used as an executable path
        // to cmd.exe
        Backend.Current.AddAction(
            actionName: "BindGem",
            targetFiles: result.Files,
            inputs: inputs.ToArray(),
            executableStringFor: $"{mono}{BuiltBindGemProgram.Path.ToString(SlashMode.Native)}",
            commandLineArguments: args.ToArray(),
            supportResponseFile: false
        );
        return result;
    }
}
