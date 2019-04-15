using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JamSharp.Runtime;
using Unity.ZeroPlayer.NiceIO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditorInternal;
using UnityEngine;

using UnityEngine.Windows;
using Debug = UnityEngine.Debug;
using File = System.IO.File;
using PackageInfo = UnityEditor.PackageInfo;

public class ZeroPlayerBuildWindow : EditorWindow {

    [MenuItem("Window/ZeroPlayerBuild")]
    static void Open()
    {
        GetWindow<ZeroPlayerBuildWindow>().Show();
    }

    private AssemblyDefinitionAsset _gameToBuild;

    private static string[] validTargets;
    private int selectedTarget;

    [Serializable]
    private class AsmDefJsonObject
    {
        [SerializeField] public string name;
        [SerializeField] public string[] defineConstraints;

        public AsmDefJsonObject()
        {
            name = null;
            defineConstraints = null;
        }
    }

    private static NPath ZeroPlayerPackage;
    private static NPath BeeRoot;
    private static NPath BeePath;
    private static NPath BootstrapFolder;

    static IEnumerable<NPath> AllAsmDefs()
    {
#if false
        // FindAssets only returns toplevel package assets (roots of project dependencies).
        // There's a fix coming at some point to allow it to return assets from all loaded packages.
        string[] guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");
        foreach (var g in guids) {
            string asmdefPath = AssetDatabase.GUIDToAssetPath(g);
            yield return new NPath(Path.GetFullPath(asmdefPath));
        }
#else
        // Workaround for above issue.  Do both this and the CompilationPipeline path
        // to try to discover asmdefs that aren't part of the current compilation.
        var paths = new HashSet<NPath>();
        string[] guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");
        foreach (var g in guids) {
            string asmdefPath = AssetDatabase.GUIDToAssetPath(g);
            paths.Add(Path.GetFullPath(asmdefPath));
        }

        foreach (var asm in CompilationPipeline.GetAssemblies()) {
            var path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(asm.name);
            if (path == null)
                continue;
            paths.Add(Path.GetFullPath(path));
        }

        foreach (var path in paths)
            yield return path;
#endif
    }

    [InitializeOnLoadMethod]
    static void InitValidTargets()
    {
        ZeroPlayerPackage = new NPath(Path.GetFullPath(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Unity.ZeroPlayer"))).Parent.Parent;
        BeeRoot = ZeroPlayerPackage.Combine("bee~");
        BeePath = BeeRoot.Combine("bee.exe");
        BootstrapFolder = new NPath(Application.dataPath).Parent.Combine("Bootstrap");

        var targets = new List<string>();

        foreach (var asmdefPath in AllAsmDefs()) {
            try {
                var asmdefjson = JsonUtility.FromJson<AsmDefJsonObject>(asmdefPath.ReadAllText());
                if (asmdefjson.defineConstraints != null &&
                    asmdefjson.defineConstraints.Contains("UNITY_BOOTSTRAP_MAIN")) {
                    targets.Add(asmdefjson.name);
                }
            } catch (Exception e) {
                Debug.LogWarning($"asmdef {asmdefPath} couldn't be parsed as Json: {e}");
            }
        }

        targets.Sort();
        validTargets = targets.ToArray();
    }

    static void UpdateAsmDefsJson()
    {
        // Create BootstrapFolder
        if (!BootstrapFolder.DirectoryExists())
            BootstrapFolder.CreateDirectory();


        // First write out bee.config
        using (StreamWriter sw = new StreamWriter(BootstrapFolder.Combine("bee.config").ToString()))
        {  
            sw.NewLine = "\n";
            sw.WriteLine("{");
            sw.WriteLine("  \"BuildProgramBuildProgramFiles\": [");
            sw.WriteLine($"\"{BeeRoot.Combine("BuildProgramBuildProgramSources").RelativeTo(BootstrapFolder).ToString(SlashMode.Forward)}\"");
            sw.WriteLine("  ]");
            sw.WriteLine("}");
        } 
        
        // Then write out some helper runbee/runbee.cmd scripts
        using (StreamWriter sw = new StreamWriter(BootstrapFolder.Combine("runbee").ToString()))  
        {  
            sw.NewLine = "\n";
            sw.WriteLine($@"#!/bin/sh");
            sw.WriteLine();
            sw.WriteLine("MONO=");
            sw.WriteLine($@"BEE=""$PWD/{BeePath.RelativeTo(BootstrapFolder).ToString(SlashMode.Forward)}""");
            sw.WriteLine($@"if [ ""$APPDATA"" == """" ] ; then");
            sw.WriteLine("    MONO=mono");
            sw.WriteLine("fi");
            sw.WriteLine("if [ $# -eq 0 ]; then");
            sw.WriteLine("    ${MONO} $BEE -t");
            sw.WriteLine("  else");
            sw.WriteLine("    ${MONO} $BEE $*");
            sw.WriteLine("fi");
        } 
        using (StreamWriter sw = new StreamWriter(BootstrapFolder.Combine("runbee.cmd").ToString()))  
        {  
            sw.NewLine = "\n";
            sw.WriteLine("@ECHO OFF");
            sw.WriteLine($@"set bee=%~dp0{BeePath.RelativeTo(BootstrapFolder).ToString(SlashMode.Backward)}");
            sw.WriteLine($@"if [%1] == [] (%bee% -t) else (%bee% %1 %2 %3 %4 %5 %6 %7 %8 %9)");
        } 

        // Then write out asmdefs.json
        using (StreamWriter sw = new StreamWriter(BootstrapFolder.Combine("asmdefs.json").ToString()))  
        {  
            sw.NewLine = "\n";
            sw.WriteLine("{");
            sw.WriteLine("\"asmdefs\": {");
            var comma = false;
            foreach (var asmdefPath in AllAsmDefs()) {
                var asmdefjson = JsonUtility.FromJson<AsmDefJsonObject>(asmdefPath.ReadAllText());
                sw.WriteLine(comma ? ", " : "  ");
                sw.WriteLine($"\"{asmdefjson.name}\": \"{asmdefPath.ToString(SlashMode.Forward)}\"");
                comma = true;
            }
            sw.WriteLine("}, ");
            sw.WriteLine("\"mainprograms\": [");
            sw.WriteLine(string.Join(", ", validTargets.Select(s => $"\"{s}\"")));
            sw.WriteLine("]");
            sw.WriteLine("}");
        }
    }

    void RunBee(string args, bool quiet = false)
    {
        ProcessStartInfo pi;
        var filename = BeePath.ToString(SlashMode.Native);
#if !UNITY_EDITOR_WIN
        args = $"\"{filename}\" {args}";
        filename = Path.Combine(EditorApplication.applicationPath, "Contents/MonoBleedingEdge/bin/mono");
#endif
        pi = new ProcessStartInfo
        {
            Arguments = args,
            WorkingDirectory = BootstrapFolder.ToString(SlashMode.Native),
            FileName = filename,
            UseShellExecute = !quiet
        };
        Process.Start(pi).WaitForExit();
    }

    void OnGUI()
    {
        GUI.skin.label.wordWrap = true;
        GUILayout.Label("Welcome to the Bootstrap Build window. This is going to be a rocky ride, buckle up.");

        if (validTargets == null || validTargets.Length == 0) {
            GUILayout.Label("No asmdefs with UNITY_BOOTSTRAP_MAIN define constraint found!");
            return;
        }

        GUILayout.Label("Update/create asmdefs.json and solution in the toplevel project directory.  Only needed when the packages included in the project change:");
        if (GUILayout.Button("Update asmdefs.json")) {
            UpdateAsmDefsJson();
        }
        if (GUILayout.Button("Generate DotsBootstrap.gen.sln")) {
            UpdateAsmDefsJson();
            RunBee("ProjectFiles", quiet: true);
        }

        GUILayout.Space(5.0f);

        GUILayout.Label("Build a selected Bootstrap standalone project:");

        GUILayout.BeginHorizontal();
        selectedTarget = EditorGUILayout.Popup("Target:", selectedTarget, validTargets);
        var tgt = validTargets[selectedTarget];

        if (GUILayout.Button("Build")) {
            RunBee(tgt);
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("To build from the command line (in the project/Bootstrap directory):");
#if UNITY_EDITOR_WIN
        GUILayout.Label($"   runbee.cmd {tgt}");
#else
        GUILayout.Label($"   ./runbee {tgt}");
#endif
        GUILayout.Label("It's strongly recommended to do this from the command line instead!");
        GUILayout.Label($"Other valid targets look like {tgt}-debug_win64 or {tgt}-release_webgl");
        GUILayout.Label("Discoverable targets have UNITY_BOOTSTRAP_MAIN in their asmdef define constraints.");

/*
        var sb = new StringBuilder();
            bool first = true;
            sb.Append("{\n");
            foreach (var a in CompilationPipeline.GetAssemblies()) {
                string assemblyDefinitionFilePathFromAssemblyName = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name);

                if (assemblyDefinitionFilePathFromAssemblyName == null)
                    continue;
                if (!first)
                    sb.Append(",\n");
                first = false;

                var fullPath = new NPath(Path.GetFullPath(assemblyDefinitionFilePathFromAssemblyName));
                sb.Append($"\"{a.name}\" : \"{fullPath}\"");
            }
            sb.Append("\n}");

            var buildFolder = new NPath(Application.dataPath).Parent.Combine("Build", _gameToBuild.name);

            NPath json = $"{buildFolder}/asmdefs.json";
            json.EnsureParentDirectoryExists().WriteAllText(sb.ToString());

            var zeroPlayerPackage = new NPath(Path.GetFullPath(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName("Unity.ZeroPlayer"))).Parent.Parent;

            var beeRoot = zeroPlayerPackage.Combine("bee~");
            buildFolder.Combine("bee.config").WriteAllText($@"{{ ""BuildProgramBuildProgramFiles"" : [""{beeRoot}/BuildProgramBuildProgramSources""] }}");

            var defaultTargets = "debug_webgl release_webgl";

#if UNITY_EDITOR_WIN
            buildFolder.Combine("build.bat").WriteAllText($@"
{beeRoot.ToString(SlashMode.Native)}\bee.exe {defaultTargets}
@pause
");

            var pi = new ProcessStartInfo
            {
                Arguments = "/c "+buildFolder.Combine("build.bat").ToString(SlashMode.Native),
                WorkingDirectory = buildFolder.ToString(SlashMode.Native),
                FileName = "cmd.exe",
                UseShellExecute = true,
            };
#elif UNITY_EDITOR_OSX
            buildFolder.Combine("build.command").WriteAllText($@"
cd {buildFolder.ToString(SlashMode.Native)}
mono {beeRoot.ToString(SlashMode.Native)}/bee.exe {defaultTargets}
");

            var chmod = new ProcessStartInfo
            {
                WorkingDirectory = buildFolder.ToString(SlashMode.Native),
                FileName = "/bin/chmod",
                Arguments = $"+x {buildFolder.Combine("build.command").ToString(SlashMode.Native)}",
                UseShellExecute = true
            };

            Process.Start(chmod).WaitForExit();

            var pi = new ProcessStartInfo
            {
                WorkingDirectory = buildFolder.ToString(SlashMode.Native),
                Arguments = buildFolder.Combine("build.command").ToString(SlashMode.Native),
                FileName = "/usr/bin/open",
                UseShellExecute = true,
            };
#else
#error Don't know how to launch on this platform
#endif
            var p = Process.Start(pi);
            p.WaitForExit();
        }
*/
    }
}
