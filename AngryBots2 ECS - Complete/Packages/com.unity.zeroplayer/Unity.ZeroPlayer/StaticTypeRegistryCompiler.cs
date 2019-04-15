// Disabled until this is resolved: https://gitlab.cds.internal.unity3d.com/burst/burst/issues/139
#if false
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEditor;
using UnityEditorInternal;

namespace Unity.ZeroPlayer
{
    public class StaticTypeRegistryCompiler
    {
        [InitializeOnLoadMethod]
        static void OnInitializeOnLoad()
        {
            AssemblyReloadEvents.beforeAssemblyReload += GenerateStaticTypeRegistry;
            InternalEditorUtility.RequestScriptReload();
        }

        private static void GenerateStaticTypeRegistry()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            List<string> typeRegGenArgs = new List<string>();

            string projectDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string outDir = Path.Combine(projectDir, "Library", "ScriptAssemblies");
            string archBits = GetArchitectureBits(EditorUserBuildSettings.activeBuildTarget);
            string isDotNetBuild = Profile.DotNet.ToString();
            
            typeRegGenArgs.Add(outDir);
            typeRegGenArgs.Add(archBits);
            typeRegGenArgs.Add(isDotNetBuild);

            Assembly[] assemblies;
            if (BuildPipeline.isBuildingPlayer)
                assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            else
                assemblies = CompilationPipeline.GetAssemblies(); // If not building the player include everything as we want test types included in the registry

            string coreModulePath = null;
            foreach (var asm in assemblies)
            {
                if (coreModulePath == null)
                {
                    coreModulePath = asm.allReferences.FirstOrDefault(s => s.Contains("UnityEngine.CoreModule"));
                }

                if (asm.name.Contains("Editor"))
                    continue;

                typeRegGenArgs.Add(Path.GetFullPath(asm.outputPath));
            }

            if (coreModulePath == null)
                throw new Exception("Could not find UnityEngine.CoreModule assembly path");

            // We need to explicitly include the path to the UnityEngine.CoreModule assembly as TypeRegGen will need it for
            // type inspection functions during code generation
            typeRegGenArgs.Add(Path.GetFullPath(coreModulePath));

            
            // Invoke TypeRegGen which will re-write all assemblies in place
            TypeRegGen typeRegGen = new TypeRegGen();
            typeRegGen.GenerateTypeRegistry(typeRegGenArgs.Distinct().Where(s => !s.Contains("UnityEditor")).ToArray());

            stopwatch.Stop();
            //Debug.Log($"Static Type Registry Generation Time: {stopwatch.ElapsedMilliseconds}ms");
        }

        private static string GetArchitectureBits(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.iOS:
                case BuildTarget.Android:
                case BuildTarget.StandaloneLinux:
                case BuildTarget.StandaloneWindows:
                    return "32";
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                case BuildTarget.WSAPlayer:
                case BuildTarget.XboxOne:
                case BuildTarget.PS4:
                default:
                    return "64";
            }
        }
    }
}
#endif
