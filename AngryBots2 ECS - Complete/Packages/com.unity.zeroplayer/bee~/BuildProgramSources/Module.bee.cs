using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bee.Core;
using Bee.Toolchain.Emscripten;
using Bee.Tools;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;

namespace Unity.Dots.Build
{
    public enum ScriptingBackend
    {
        //FullIl2cpp,
        TinyIl2cpp,
        Dotnet
    }

    public class UnityEditorPlatform : Platform
    {
        public override bool HasPosix { get; } = false;
    }
    
    public class DotsPlayerConfiguration
    {
        public ScriptingBackend ScriptingBackend { get; }
        public bool UseBurst { get; }
        public bool UseLinker { get; }
        public bool Development { get; }
        public Platform Platform { get; }
        public ToolChain ToolChain { get; }

        public DotsPlayerConfiguration(ScriptingBackend sb, bool _useBurst, bool _useLinker, bool _dev, ToolChain p)
        {
            ScriptingBackend = sb;
            UseBurst = _useBurst;
            UseLinker = _useLinker;
            Development = _dev;
            Platform = p.Platform;
            ToolChain = p;
        }
        
        public override bool Equals(object obj)
        {
            if (obj.GetType() != GetType())
                return false;
            if (obj is DotsPlayerConfiguration other)
                return (ScriptingBackend == other.ScriptingBackend &&
                        UseBurst == other.UseBurst &&
                        UseLinker == other.UseLinker &&
                        Development == other.Development &&
                        ToolChain.Equals(other.ToolChain));
            return false;
        }

        public override int GetHashCode()
        {
            return (29 * (int) ScriptingBackend) ^
                    ToolChain.GetHashCode() ^                   
                   (41 * (Development ? 1 : 0)) ^
                   (43 * (UseBurst ? 1 : 0)) ^
                   (47 * (UseLinker ? 1 : 0));
        }
        
        public string Identifier
        {
            get
            {
                var ret = ScriptingBackend.ToString();
                ret += $"{(UseBurst ? string.Empty : "_nb")}";
                ret += $"{(UseLinker ? string.Empty : "_nostrip")}";
                ret += "_" + Platform.Name + ToolChain.Architecture.Bits;
                return ret;
            }
        }
    }

    public class DotsNativeConfiguration : NativeProgramConfiguration
    {
        public DotsPlayerConfiguration dpc { get; }

        public DotsNativeConfiguration(
            CodeGen codeGen,
            ToolChain toolChain,
            bool lump,
            DotsPlayerConfiguration _dpc) : base(codeGen, toolChain, lump)
        {
            dpc = _dpc;
        }

        public override bool Equals(object obj)
        {
            if (!base.Equals(obj))
                return false;
            if (obj is DotsNativeConfiguration other)
                return dpc == other.dpc;
            return false;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ dpc.GetHashCode();
        }

        public override string Identifier => dpc.Identifier +
                                             $"{(dpc.Development ? (CodeGen == CodeGen.Debug ? "_debug" : "_dev") : "_release")}";
    }

    public class DotsCSharpConfiguration : CSharpProgramConfiguration
    {
        public DotsPlayerConfiguration dpc { get; }

        public DotsCSharpConfiguration(
            CSharpCodeGen codeGen,
            CSharpCompiler compiler,
            DotsPlayerConfiguration _dpc) : base(codeGen, compiler)
        {
            dpc = _dpc;
        }
        
        public override bool Equals(object obj)
        {
            if (!base.Equals(obj))
            {
                return false;
            }

            if (obj is DotsCSharpConfiguration other)
            {

                return dpc.Equals(other.dpc);
            }
            return false;
        }

        public override int GetHashCode()
        {
            return base.GetHashCode() ^ dpc.GetHashCode();
        }

        public override string Identifier   
        {
            get
            {
                var ret = dpc.Identifier;
                ret += $"{(dpc.Development ? (CodeGen == CSharpCodeGen.Debug ?  "_debug" : "_dev") : "_release")}";
                return ret;
            }
        }
    }
    
    public class Module
    {
        public Module(string name, NPath path)
        {
            _name = name;
            _path = path;
            IsTestModule = _name.Contains("Tests");

            NativeDefines.Add(c => c.ToolChain.DynamicLibraryFormat == null, "FORCE_PINVOKE_INTERNAL=1");
            NativeDefines.Add(c=>c.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp, "IL2CPP_DOTS=1");

            CSharpDefines.Add(c => c.dpc.Platform is WebGLPlatform, "UNITY_WEBGL");
            CSharpDefines.Add(c => c.dpc.Platform is WindowsPlatform, "UNITY_WINDOWS");
            CSharpDefines.Add(c => c.dpc.Platform is MacOSXPlatform, "UNITY_MACOSX");
            CSharpDefines.Add(c => c.dpc.Platform is LinuxPlatform, "UNITY_LINUX");
            CSharpDefines.Add(c => c.dpc.Platform is IosPlatform, "UNITY_IOS");
            CSharpDefines.Add(c => c.dpc.Platform is AndroidPlatform, "UNITY_ANDROID");

            CSharpDefines.Add(
                "UNITY_2018_3_OR_NEWER",
                "UNITY_ZEROPLAYER",
                "NET_DOTS",
                "UNITY_CSHARP_TINY",
                "UNITY_USE_TINYMATH",
                "UNITY_BINDGEM");
            CSharpDefines.Add(c=>c.dpc.UseBurst, "UNITY_USE_BURST");
            
            CSharpDefines.Add(c => c.CodeGen == CSharpCodeGen.Debug, "DEBUG");
            CSharpDefines.Add(
                c => c.CodeGen == CSharpCodeGen.Debug || c.dpc.Development,
                "ENABLE_UNITY_COLLECTIONS_CHECKS");
            CSharpDefines.Add(
                c => c.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp,
                "UNITY_ZEROPLAYER_IL2CPP");
            CSharpDefines.Add(c => c.dpc.ScriptingBackend == ScriptingBackend.Dotnet, "UNITY_ZEROPLAYER_DOTNET");
            if (!IsTestModule)
                ManagedReferences.Add(BuildProgram.TinyCorlib);

            ManagedReferences.Add(BuildProgram.UnsafeUtility);
            if (_name != "Unity.ZeroJobs" && _name != "Unity.LowLevel")
                Dependencies.Add("Unity.ZeroJobs");

            NativeLibraries.Add(
                c => c.ToolChain.Platform is WindowsPlatform && c.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp,
                new SystemLibrary("kernel32.lib"));
            if (_name != "Unity.LowLevel")
                Dependencies.Add("Unity.LowLevel");
            
            NativeDefines.Add(c => c.Platform is WebGLPlatform, "UNITY_WEBGL=1");
            NativeDefines.Add(c => c.Platform is WindowsPlatform, "UNITY_WINDOWS=1");
            NativeDefines.Add(c => c.Platform is MacOSXPlatform, "UNITY_MACOSX=1");
            NativeDefines.Add(c => c.Platform is LinuxPlatform, "UNITY_LINUX=1");
            NativeDefines.Add(c => c.Platform is IosPlatform, "UNITY_IOS=1");
            NativeDefines.Add(c => c.Platform is AndroidPlatform, "UNITY_ANDROID=1");

            // sigh
            NativeDefines.Add("BUILD_" + _name.ToUpper().Replace(".", "_") + "=1");

            NativeDefines.Add(c => c.CodeGen == CodeGen.Debug, "DEBUG=1");
            NativeDefines.Add(c => c.Platform is WebGLPlatform, "IL2CPP_DISABLE_GC=1");

            NativeDefines.Add("BINDGEM_DOTS=1");
            NativeDefines.Add(
                c => c.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp,
                "RUNTIME_IL2CPP=1",
                "NET_4_0=1");
        }

        protected NPath _path;
        protected string _name;

        public virtual NPath Path => _path;
        public virtual string Name => _name;
        private string _nativeProgramName = null;

        public virtual string NativeProgramName
        {
            get => _nativeProgramName ?? (IsMainModule ? "": "lib_") + Name.ToLower().Replace(".", "_");
            set { _nativeProgramName = value; }
        }

        public bool IsTestModule { get; set; } = false;
        public bool IsMainModule { get; set; } = false;

        public bool Unsafe { get; set; } = false;
        
        public CollectionWithConditions<NPath, DotsCSharpConfiguration> Cs = new CollectionWithConditions<NPath, DotsCSharpConfiguration>();

        //cribbed from unity buildsystem module
        /// <summary>
        /// The C++ source files in this module. By default, all files anywhere in the module folder (except the Tests subdirectory),
        /// and in the platform module folder, with extensions .c, .cpp, .h, .inc, and any other extensions recognised by the compiler.
        /// </summary>
        public CollectionWithConditions<NPath, DotsNativeConfiguration> Cpp = new CollectionWithConditions<NPath, DotsNativeConfiguration>();

        public CollectionWithConditions<string, DotsNativeConfiguration> NativeDefines = new CollectionWithConditions<string, DotsNativeConfiguration>();
        public CollectionWithConditions<string, DotsCSharpConfiguration> CSharpDefines = new CollectionWithConditions<string, DotsCSharpConfiguration>();

        /// <summary>
        /// Managed things that the managed part of this program should reference. These can be explicit paths to assemblies, DotNetAssembly instances,
        /// other CSharpPrograms, CSharpProjectFileReferences, or SystemReferences.
        /// </summary>
        public CollectionWithConditions<ICSharpReferenceable, DotsCSharpConfiguration> ManagedReferences =
            new CollectionWithConditions<ICSharpReferenceable, DotsCSharpConfiguration>();

        public CollectionWithConditions<ILibrary, DotsNativeConfiguration> NativeLibraries { get; } =
            new CollectionWithConditions<ILibrary, DotsNativeConfiguration>();
        
        public CollectionWithConditions<NPath, DotsNativeConfiguration> IncludeDirectories = new CollectionWithConditions<NPath, DotsNativeConfiguration>();

        public List<string> Dependencies = new List<string>();
        
        public IEnumerable<Platform> IncludedPlatforms { get; set; } = new List<Platform>();
        public IEnumerable<Platform> ExcludedPlatforms { get; set; } = new List<Platform>();
        public bool WantsTestFramework { get; set; } = false;

        public bool SupportedOn(Platform platform)
        {
            if (ExcludedPlatforms.Contains(platform))
                return false;
            if (IncludedPlatforms.Any() && !IncludedPlatforms.Contains(platform))
                return false;
            return true;
        }

        public bool UseBindGem { get; set; } = true;

        private IEnumerable<string> cachedDeps = null;

        public IEnumerable<string> AllRecursiveDependencies(Dictionary<string, Module> modules)
        {
            if (cachedDeps != null) return cachedDeps;
            cachedDeps = Dependencies.Concat(Dependencies.SelectMany(name =>
                    modules[name].AllRecursiveDependencies(modules)))
                .Distinct()
                .ToList();
            return cachedDeps;
        }
    }

    public class ModuleForAsmDef : Module
    {
        protected JObject Json { get; }
        protected NPath AsmDef { get; }
        
        public override string Name => (string) Json["name"];
        readonly NPath[] _pathsToIgnoreWhileGlobbing;
        protected virtual NPath[] PathsToIgnoreWhileGlobbing => Array.Empty<NPath>();

        private IEnumerable<Platform> ReadPlatformList(string jsonKey)
        {
            var platformList = Json[jsonKey] as JArray;
            if (platformList == null)
                return Array.Empty<Platform>();

            return platformList.Select(token => PlatformFromAsmDefPlatformName(token.ToString())).Where(p => p != null).ToArray();
        }

        public ModuleForAsmDef(JObject json, NPath asmDef) : base((string)json["name"], asmDef.Parent)
        {
            _pathsToIgnoreWhileGlobbing = PathsToIgnoreWhileGlobbing;
            
            Json = json;
            AsmDef = asmDef;
            
            // the included platforms on unity.entities.tests is wrong wrt tiny
            if (Name != "Unity.Entities.Tests")
                IncludedPlatforms = ReadPlatformList("includePlatforms");
            ExcludedPlatforms = ReadPlatformList("excludePlatforms");


            
            var optRefs = Json["optionalUnityReferences"];
            if (optRefs != null) {
                WantsTestFramework = ((JArray) optRefs).Any(r => (string)r == "TestAssemblies");
            }
            var unsafeOk = Json["allowUnsafeCode"];
            if (unsafeOk != null)
                Unsafe = (bool) unsafeOk;
            
            var reftoken = Json["references"];
            if (reftoken != null)
            {
                foreach (string r in reftoken)
                    Dependencies.Add(r);
            }
            
            var cppPath = AsmDef.Parent.Combine("cpp~");

            IncludeDirectories.Add(BuildProgram.BeeRoot.Combine("cppsupport/include"));
            IncludeDirectories.Add(cppPath.Combine("include"));
            IncludeDirectories.Add(BindGem.BindGemOutputDir);
            if (cppPath.Exists())
            {
                var src = cppPath.Combine("src");
                if (src.Exists())
                {
                    // Complex cpp dir with optional per-platform sources
                    Cpp.Add(src.Files(true));
                    Cpp.Add(c => c.Platform is WebGLPlatform,
                        cppPath.Combine("src-webgl").FilesIfExists(recurse: true));
                    Cpp.Add(c => c.Platform is WindowsPlatform,
                        cppPath.Combine("src-win").FilesIfExists(recurse: true));
                    Cpp.Add(c => c.Platform is MacOSXPlatform, cppPath.Combine("src-mac").FilesIfExists(recurse: true));
                    Cpp.Add(c => c.Platform is LinuxPlatform,
                        cppPath.Combine("src-linux").FilesIfExists(recurse: true));
                    Cpp.Add(c => c.Platform is IosPlatform, cppPath.Combine("src-ios").FilesIfExists(recurse: true));
                    Cpp.Add(c => c.Platform is AndroidPlatform,
                        cppPath.Combine("src-android").FilesIfExists(recurse: true));
                }
                else
                {
                    Cpp.Add(cppPath.Files(true));
                }
            }

            
            var jslibFiles = AsmDef.Parent.Files("*.jslib", true);
            NativeLibraries.Add(c => c.Platform is WebGLPlatform, jslibFiles.Select(f => new JavascriptLibrary(f)));

            if (new[]
            {
                "Unity.Entities.StaticTypeRegistry", "Unity.Mathematics.Extensions", "Unity.Burst", "Unity.Mathematics",
                "Unity.Jobs", "Unity.Collections", "Unity.Entities", "Unity.Tiny.Debugging"
            }.Contains(Name) || Name.Contains("StaticTypeRegistry"))
            {
                UseBindGem = false;
            }

            // unity.mathematics doesn't have a bee.cs right now
            Cs.Add(AsmDef.Parent.Files("*.cs", true)
                .Where(f => !_pathsToIgnoreWhileGlobbing.Contains(f) &&
                            !f.ToString().Contains("bee~") &&
                            !(Name == "Unity.Mathematics" &&
                              (f.FileName == "math_unity_conversion.cs" || f.FileName == "PropertyAttributes.cs"))));
        }
        private static Platform PlatformFromAsmDefPlatformName(string name)
        {
            switch(name)
            {
                case "macOSStandalone":
                    return new MacOSXPlatform();
                case "WindowsStandalone32":
                case "WindowsStandalone64":
                    return new WindowsPlatform();
                case "Editor":
                    return new UnityEditorPlatform();
                default:
                {
                    var typeName = $"{name}Platform";
                    var type = AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => a.GetTypes())
                        .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
                    if (type == null)
                    {
                        Console.WriteLine($"Couldn't find Platform for {name} (tried {name}Platform), ignoring it.");
                        return null;
                    }
                    return (Platform)Activator.CreateInstance(type);
                }
            }
        }
    }

    public static class Extensions
    {
        public static IEnumerable<Module> OrderByDependencies(this IEnumerable<Module> modules)
        {
            var processed = new List<Module>();
            var unprocessed = new List<Module>(modules);
            while (unprocessed.Any())
            {
                var clone = new List<Module>(unprocessed);
                bool processedOne = false;
                foreach (var module in clone)
                {
                    if (!module.Dependencies.All(d => processed.Any(m => m.Name == d)))
                        continue;
                    processedOne = true;
                    processed.Add(module);
                    unprocessed.Remove(module);
                }
                if (!processedOne)
                    throw new ArgumentException("OrderFromLeastToMostDependent: One or several of these modules have a cyclic dependency or a dependency to an unknown or blacklisted module: "
                                                + unprocessed.Select(x => x.Name).SeparateWithSpace());
            }

            return processed;
        }
        
        public static Type FindCorrectType(string name, Type[] ModuleTypes)
        {
            foreach (var type in ModuleTypes)
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
        
        public static bool IsModuleDerivedClass(Type t)
        {
            if (t.IsAbstract)
                return false;
            if (t == typeof(ModuleForAsmDef))
                return false;
            return typeof(ModuleForAsmDef).IsAssignableFrom(t);
        }
    }
}
