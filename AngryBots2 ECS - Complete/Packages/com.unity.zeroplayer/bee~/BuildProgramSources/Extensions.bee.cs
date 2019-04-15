using System;
using System.Collections.Generic;
using System.Linq;
using Bee.Core;
using NiceIO;
using Unity.BuildTools;

static class Extensions
{
    public static NPath[] FilesIfExists(this NPath path, string pattern = null, bool recurse = false)
    {
        if (!path.DirectoryExists())
            return new NPath[0];
        if (pattern != null)
            return path.Files(pattern, recurse);
        return path.Files(recurse);
    }

    public static NPath[] CombineMany(this NPath path, string[] files)
    {
        return files.Select(s => path.Combine(s)).ToArray();
    }

    public static void SetupConcatAction(NPath outputFile, IEnumerable<NPath> sourceFiles)
    {
        SetupConcatAction("Concat", outputFile, sourceFiles);
    }

    public static void SetupConcatAction(string actionName, NPath outputFile, IEnumerable<NPath> sourceFiles)
    {
        var srcFiles = sourceFiles.ToArray();
        if (HostPlatform.IsWindows) {
            Backend.Current.AddAction(
                actionName: actionName,
                targetFiles: new[] {outputFile},
                inputs: srcFiles,
                executableStringFor: $"copy /B",
                commandLineArguments: new[]
                {
                    String.Join("+", srcFiles.Select(s => s.ToString(SlashMode.Native))),
                    outputFile.ToString(SlashMode.Native)
                },
                supportResponseFile: false
            );
        } else {
            var args = new List<string>();
            args.Add(srcFiles.Select(s => s.ToString(SlashMode.Native)));
            args.Add(">");
            args.Add(outputFile.ToString(SlashMode.Native));
            Backend.Current.AddAction(
                actionName: actionName,
                targetFiles: new[] {outputFile},
                inputs: srcFiles,
                executableStringFor: $"cat",
                commandLineArguments: args.ToArray(),
                supportResponseFile: false
            );
        }
    }
}
