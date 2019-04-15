using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.Remoting.Messaging;
using Bee.Core;
using Bee.CSharpSupport;
using Bee.DotNet;
using Bee.NativeProgramSupport.Building;
using Bee.Toolchain.Emscripten;
using Bee.Toolchain.Linux;
using Bee.Toolchain.Xcode;
using Bee.Toolchain.VisualStudio;
using Bee.Toolchain.Windows;
using Bee.VisualStudioSolution;
using Newtonsoft.Json.Linq;
using NiceIO;
using Unity.BuildSystem.CSharpSupport;
using Unity.BuildSystem.NativeProgramSupport;
using Unity.BuildTools;

namespace Unity.Dots.Build
{
    public class DotsRuntimeBuildCode
    {
        private static Type[] _moduleTypes = typeof(Module).Assembly.GetTypes()
            .Where(Extensions.IsModuleDerivedClass)
            .ToArray();

        private static JObject _namesToAsmDefs;

        private static Dictionary<string, CSharpProgram> _cSharpProgramsForModules =
            new Dictionary<string, CSharpProgram>();

        private static Dictionary<string, NativeProgram> _nativeProgramsForModules =
            new Dictionary<string, NativeProgram>();

        private static Dictionary<string, Dictionary<DotsCSharpConfiguration, DotNetAssembly>>
            _typeregdAssembliesForModules =
                new Dictionary<string, Dictionary<DotsCSharpConfiguration, DotNetAssembly>>();

        //this maybe should be a KeyedValueWithConditions
        private static Dictionary<string, Dictionary<DotsCSharpConfiguration, BindGem.Result>> _bindGemResults =
            new Dictionary<string, Dictionary<DotsCSharpConfiguration, BindGem.Result>>();

        private static NPath BeeRootValue = null;

        public static NPath BeeRoot
        {
            get
            {
                if (BeeRootValue == null)
                    throw new InvalidOperationException("BeeRoot accessed before it has been initialized");
                return BeeRootValue;
            }
        }

        static readonly Dictionary<string, Module> _namesToModules = new Dictionary<string, Module>();
        private PrecompiledLibrary _libIl2CppLackeyDll;
        private static string[] _mainProgramNames;
        private static JObject m_BuildSettings;
        private NPath _il2CppExe { get; } = Il2Cpp.Distribution.Path.Combine("build/il2cpp.exe");
        public static IEnumerable<DotsNativeConfiguration> AllPossibleVariations { get; set; }

        public static VisualStudioSolution VSSolution { get; set; }
        public static NPath BurstExecutablePath { get; set; }

        public BurstCompiler BurstCompilerForPlatform(DotsNativeConfiguration c)
        {
            if (c.Platform is WindowsPlatform)
                return new BurstCompilerForWindows();
            if (c.Platform is WebGLPlatform)
                return new BurstCompilerForEmscripten();
            throw new ArgumentException(
                $"{c.Platform} does not yet have tiny burst support! Badger @elliotc to fix this!");
        }

        public static CSharpProgram CSharpProgramForModule(Module m, bool doExe)
        {
            if (_cSharpProgramsForModules.TryGetValue(m.Name, out var ret))
                return ret;

            ret = new CSharpProgram
            {
                CopyReferencesNextToTarget = false,
                ProjectFilePath = $"{m.Name}.gen.csproj",
                Unsafe = m.Unsafe,
                LanguageVersion = "7.3",
                FileName = (doExe || m.IsTestModule) ? $"{m.Name}.exe" : $"{m.Name}.dll",
            };
            ret.Framework.Add(c=>!(c is DotsCSharpConfiguration), Framework.Framework46);

            if (m.IsTestModule)
                ret.Framework.Add(
                    c => c is DotsCSharpConfiguration,
                    Framework.Framework46);
            else
                ret.Framework.Add(
                    c => c is DotsCSharpConfiguration,
                    Framework.FrameworkNone);
            ret.Sources.Add(c =>
            {
                if (c is DotsCSharpConfiguration dcc)
                    return m.Cs.For(dcc);
                else
                    return m.Cs.ForAny();
            });
            ret.Defines.Add(c =>
            {
                if (c is DotsCSharpConfiguration dcc)
                    return m.CSharpDefines.For(dcc);
                else
                    return m.CSharpDefines.ForAny();
            });
            ret.References.Add(c =>
            {if (c is DotsCSharpConfiguration dcc)
                    return m.ManagedReferences.For(dcc);
                else
                    return m.ManagedReferences.ForAny();
            });

            if (m.Name == "Unity.ZeroJobs" ||
                m.Name == "Unity.Entities" ||
                m.Name == "Unity.Tiny.Image2D" ||
                m.Name == "Unity.Tiny.Core2D" || 
                m.Name == "Unity.Entities.Tests" || 
                m.Name.Contains("StaticTypeRegistry")) 
                ret.WarningsAsErrors = false;

            _cSharpProgramsForModules.Add(m.Name, ret);
            return ret;
        }

        public static NativeProgram NativeProgramForModule(Module m)
        {
            if (_nativeProgramsForModules.TryGetValue(m.Name, out var ret))
                return ret;

            var name = m.NativeProgramName;
            ret = new NativeProgram(name);
            ret.Sources.Add(c => m.Cpp.For((DotsNativeConfiguration) c));
            ret.Defines.Add(c => m.NativeDefines.For((DotsNativeConfiguration) c));
            ret.IncludeDirectories.Add(c => m.IncludeDirectories.For((DotsNativeConfiguration) c));
            if (m.UseBindGem)
                ret.ExtraDependenciesForAllObjectFiles.Add(
                    v => _bindGemResults[m.Name][ManagedFromNativeConfig((DotsNativeConfiguration) v)].Header);
            ret.Libraries.Add(c => m.NativeLibraries.For((DotsNativeConfiguration) c));
            ret.RTTI.Set(c => ((DotsNativeConfiguration) c).dpc.ScriptingBackend != ScriptingBackend.TinyIl2cpp);
            
            
            ret.DynamicLinkerSettingsForEmscripten()
                .Add(c => c.CodeGen == CodeGen.Debug,
                    c => Il2CppProgram.ModifiedLinkerFor(c, CodeGen.Debug, true, m_BuildSettings).WithShellFile(BuildProgram.BeeRoot.Combine("shell.html"))
                        .WithSeparateAsm(true)
                        .WithMemoryInitFile(true));
            foreach (var development in new[] {true, false})
                ret.DynamicLinkerSettingsForEmscripten()
                    .Add(
                        c => c.CodeGen == CodeGen.Release &&
                             ((DotsNativeConfiguration) c).dpc.Development == development,
                        c => Il2CppProgram.ModifiedLinkerFor(c, CodeGen.Release, development, m_BuildSettings)
                            .WithShellFile(BuildProgram.BeeRoot.Combine("shell.html"))
                            .WithSeparateAsm(true)
                            .WithMemoryInitFile(true));
            if (m.Name == "Unity.LowLevel")
                ret.DynamicLinkerSettingsForMac().Add(l => l.WithInstallName("liballocators.dylib"));
            
            ret.DynamicLinkerSettingsForMac()
                .Add(c => c.WithDynamicLibraryLoadPath_workaround(".").WithInstallName(ret.Name + ".dylib"));

            ret.DynamicLinkerSettingsForLinux().Add(c=>c.WithCustomFlags_workaround(new string[]{"-ldl"}));

            ret.Libraries.Add(c => ((DotsNativeConfiguration) c).dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp,
                Il2CppProgram.TinyLibIl2cpp);
            ret.CompilerSettingsForEmscripten().Add(c=>c.WithRTTI(true).WithExceptions(true));
            
            _nativeProgramsForModules.Add(m.Name, ret);
            return ret;
        }

        public static Module ModuleFromAsmDefName(string asmDefName, bool forceExe = false, bool tinyCorlib = true)
        {
            if (_namesToModules.TryGetValue(asmDefName, out var previousResult))
                return previousResult;

            NPath asmDef = (string) _namesToAsmDefs[asmDefName];

            var customType = Extensions.FindCorrectType(asmDefName, _moduleTypes);

            var result = (Module) Activator.CreateInstance(customType, JObject.Parse(asmDef.ReadAllText()), asmDef);
            _namesToModules[asmDefName] = result;
            return result;
        }



        public static DotsCSharpConfiguration ManagedFromNativeConfig(DotsNativeConfiguration dnc)
        {
            return new DotsCSharpConfiguration(
                BuildProgram.CodeGenToCSCodeGen(dnc.CodeGen),
                ZeroCsc.Csc73,
                dnc.dpc);
        }

        public void Setup()
        {
            var asmDefInfo = JObject.Parse(new NPath("asmdefs.json").MakeAbsolute().ReadAllText());

            if (!asmDefInfo.TryGetValue("asmdefs", out var asmdefs))
            {
                throw new ArgumentException("asmdefs.json does not contain asmdefs section!");
            }
            
            // read buildsettings.json to get custom build settings like emscripten linker flags
            var buildSettingsFile = new NPath("buildsettings.json");
            m_BuildSettings = buildSettingsFile.Exists() ? JObject.Parse(buildSettingsFile.MakeAbsolute().ReadAllText()) : null;

            _namesToAsmDefs = asmdefs as JObject;

            // figure out where Unity.ZeroPlayer is located, so that we can build paths based on it
            if (!_namesToAsmDefs.TryGetValue("Unity.ZeroPlayer", out var zpAsmDef))
            {
                throw new ArgumentException(
                    "Couldn't find Unity.ZeroPlayer asmdef in asmdefs.json, was it generated properly?");
            }

            // figure out the set of main programs (as recorded by Unity in asmdefs.json, which did the work
            // of hunting through asmdefs to look for UNITY_BOOTSTRAP_MAIN define).  Then set up the main
            // programs to build for each config.
            
            if (!asmDefInfo.TryGetValue("mainprograms", out var asmmains))
            {
                throw new ArgumentException("asmdefs.json does not contain list of mainprograms!");
            }

            _mainProgramNames = (asmmains as JArray).Values<string>().ToArray();

            // finally set the bee root based on zpAsmDef

            BeeRootValue = new NPath((string) zpAsmDef).Parent.Parent.Combine("bee~");


            if (!_namesToAsmDefs.TryGetValue("Unity.Burst", out var burstpath))
                throw new ArgumentException(
                    "Couldn't find Unity.Burst asmdef in asmdefs.json, was it generated properly?");

            BurstExecutablePath = new NPath(burstpath.ToString()).Parent.Parent.Combine(".Runtime/bcl.exe");

            //not gonna rejigger this right now
            BuildProgram.SetupSupportTools();

            BuildProgram.SetupTestFramework();

            var modules = new List<Module>();
            var testModules = new List<Module>();
            var mainModules = new List<Module>();
            modules.Add(BuildProgram.UnityLowLevel);
            _namesToModules[BuildProgram.UnityLowLevel.Name] = BuildProgram.UnityLowLevel;
            _bindGemResults[BuildProgram.UnityLowLevel.Name] =
                new Dictionary<DotsCSharpConfiguration, BindGem.Result>();
           

            foreach (var k in _namesToAsmDefs)
            {
                var m = ModuleFromAsmDefName(k.Key);
                if (m.IsTestModule)
                {
                    m.Cs.Add(BeeRoot.Combine("CSharpSupport", "NUnitLiteMain.cs"));
                    m.ManagedReferences.Add(BuildProgram.NUnitLite);
                    if (m.WantsTestFramework)
                        m.ManagedReferences.Add(BuildProgram.TestFramework);
                    
                    testModules.Add(m);
                    mainModules.Add(m);
                }

                if (!m.Cs.IsEmpty)
                {
                    modules.Add(m);
                    if (_mainProgramNames.Contains(m.Name))
                        mainModules.Add(m);
                    _bindGemResults[m.Name] = new Dictionary<DotsCSharpConfiguration, BindGem.Result>();
                }
                else
                {
                    Console.WriteLine($"Warning: {m.Name} had no C# source files at all.");
                }
            }

            foreach (var m in mainModules)
                m.IsMainModule = true;

            var cplusplusinclude = modules.First(m => m.Name == "Unity.Entities.CPlusPlus");

            foreach (var m in modules)
            {
                //remove bogus deps
                m.Dependencies = m.Dependencies.Where(name => modules.Any(module => module.Name == name))
                    .Distinct()
                    .ToList();
            }

            modules = modules.OrderByDependencies().ToList();

            foreach (var m in modules)
            {
                CSharpProgramForModule(m, _mainProgramNames.Contains(m.Name));

                foreach (var name in m.Dependencies)
                {
                    var depModule = _namesToModules[name];

                    m.ManagedReferences.Add(
                        c => depModule.SupportedOn(c.dpc.Platform),
                        CSharpProgramForModule(depModule, _mainProgramNames.Contains(name)));
                }
            }

            /*
             * ok now we have a bunch of csharpprograms, which have not been set up for any specific config,
             * but which do have all their references correctly set up, ish.
             *
             * we have no native anything set up, except that the modules have their authored cpp set up
             * for all platforms, and defines and includes and stuff.
             *
             * now for bindgem.
             *
             * first, we need to set up vanilla compilations for all our variations, and then we will have
             * something to run bindgem on. 
             */

            AllPossibleVariations = ZeroToolchains.AllPossibleRuntimeVariations();

            var allPossibleManagedVariations = AllPossibleVariations.Select(ManagedFromNativeConfig).Distinct();

            foreach (var m in modules)
            {
                if (!m.UseBindGem)
                    continue;
                
                var csp = _cSharpProgramsForModules[m.Name];
                foreach (var v in allPossibleManagedVariations)
                {
                    var headersForDeps = m.AllRecursiveDependencies(_namesToModules).Where(name =>
                        {
                            var depModule = _namesToModules[name];
                            return depModule.UseBindGem && depModule.SupportedOn(v.dpc.Platform);
                        })
                        .Select(
                            moduleName =>
                                _bindGemResults[moduleName][v].Header);
                   
                    var dna = csp.SetupSpecificConfiguration(v);
                    NPath assembly = dna.Path;
                    var bindgemResult = BindGem.Instance()
                        .SetupInvocation(
                            m.Name,
                            assembly,
                            dna.RuntimeDependencies.Where(d => d is DotNetAssembly).ToArray(),
                            Array.Empty<NPath>(),
                            headersForDeps.ToArray(),
                            m.Path,
                            BindGem.BindGemOutputDir.Combine(v.Identifier,
                                $"bind-{assembly.FileNameWithoutExtension.Replace(".", "_")}"));

                    _bindGemResults[m.Name][v] = bindgemResult;
                    m.Cpp.Add(c => ManagedFromNativeConfig(c).Equals(v), bindgemResult.Cpp);
                    m.IncludeDirectories.Add(c => ManagedFromNativeConfig(c).Equals(v),
                        Enumerable.Append(headersForDeps.Select(h => h.Parent),
                            bindgemResult.Header.Parent));
                }
            }

            /*
             * okay now we have bindgem setup, and we are ready to make a nativeprogram for each module. 
             */
            foreach (var m in modules)
            {
                //everybody includes entitytypes.h
                m.IncludeDirectories.Add(cplusplusinclude.Path.Combine("cpp~/include"));

                foreach (var name in m.Dependencies)
                {
                    var depModule = _namesToModules[name];
                    if (!depModule.Cpp.ForAny().Empty())
                    {
                        var depnp = NativeProgramForModule(depModule);

                        m.NativeLibraries.Add(c => depModule.SupportedOn(c.Platform),
                            c=>BuiltNPForConfig(c,depnp));
                        m.IncludeDirectories.Add(c => depModule.SupportedOn(c.Platform),
                            depModule.Path.Combine("cpp~/include"));
                    }
                }
                var np = NativeProgramForModule(m);
            }

            if (HostPlatform.IsWindows)
            {
                _libIl2CppLackeyDll = (PrecompiledLibrary) Il2Cpp.SetupLibIl2CppLackey(
                    _il2CppExe.Parent.Parent.Combine("Libil2CppLackey"),
                    (WindowsToolchain)ZeroToolchains.Windows.tc);
            }


            /*
             * now we have a nativeprogram for every module, and we are ready to set them up for particular variations.
             * we also need to organize things into main programs, and also do our various patching steps.
             */
            foreach (var game in mainModules)
            {
                Console.WriteLine($"{game.Name} setup part 1");
                var csp = _cSharpProgramsForModules[game.Name]; //CSharpProgramForModule(game, true);

                foreach (var v in AllPossibleVariations)
                {
                    if (!game.SupportedOn(v.Platform) ||
                        (game.IsTestModule && v.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp)) 
                        continue;

                    /*
                     * this part doesn't get recreated if the managed variation for this native variation has already
                     * been set up
                     */
                    var baseGame = csp.SetupSpecificConfiguration(ManagedFromNativeConfig(v));

                    /*
                     * this part is going to suck.
                     *
                     * basically, today, typereg finds things like memcmp to generate code for equals and gethashcode
                     * based on random facts about the native toolchain, and it has to redo the entire setup
                     * for ALL assemblies ALL OVER AGAIN for EVERY GAME. someday (tm), we'd like to reduce the number
                     * of these, and possibly cache some per-assembly artifacts or something so we don't have
                     * this insane explosion of versions of assemblies.
                     *
                     * this is worse because burst patching ALSO has to produce a new version of all the assemblies,
                     * and that HAS to run AFTER typereg.
                     *
                     * and it's even worse because UnityLinker has to run after BOTH of those and IT has to do
                     * EVERYTHING to ALL THE ASSEMBLIES ALL OVER AGAIN EVERY TIME, which is legitimate because it
                     * has to decide what to strip on a per-game basis. 
                     */
                    
                    NPath typeRegDir = null;

                    typeRegDir = $"artifacts/{game.Name}/{v.Identifier}/typereg";



                    //todo: make it return a single assembly instead of a list of them.
                    var replacements = BuildProgram.GenerateTypeReg(game.Name,
                        baseGame,
                        typeRegDir,
                        v.CodeGen,
                        v.ToolChain.Architecture.Bits,
                        v.dpc.ScriptingBackend == ScriptingBackend.Dotnet);

                    var typeregdGame = replacements.First(asm => asm.Path.FileName == baseGame.Path.FileName);
                    typeregdGame = typeregdGame.WithDeployables(replacements
                        .Where(asm => asm.Path.FileName != baseGame.Path.FileName)
                        .SelectMany(asm=>asm.RecursiveRuntimeDependenciesIncludingSelf.Concat(asm.Deployables))
                        .Distinct()
                        .ToArray());
                    if (!_typeregdAssembliesForModules.ContainsKey(game.Name))
                        _typeregdAssembliesForModules[game.Name] =
                            new Dictionary<DotsCSharpConfiguration, DotNetAssembly>();
                    _typeregdAssembliesForModules[game.Name][ManagedFromNativeConfig(v)] = typeregdGame;

                    /*
                     * now we have a typereg'd game that is ready to be run by dotnet.
                     */
                    if (v.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp)
                    {
                        /*
                         * if we are on il2cpp, buckle up.
                         *
                         * first, setup burst patching. 
                         */
                        var burstPatchedAssembly = typeregdGame;
                        BagOfObjectFilesLibrary burstLib = null;
                        if (v.dpc.UseBurst)
                        {
                            List<NPath> rspFiles;
                            burstPatchedAssembly = NativePlaybackMode.SetupBurstPatching(
                                typeregdGame,
                                typeregdGame.Path.Parent.Parent.Combine("burstpatched"),
                                out rspFiles);

                            NPath finalRsp = rspFiles[0].Parent.Combine("final_burst_rsp.txt");
                            Backend.Current.AddAction(
                                "Concat",
                                new[] {finalRsp},
                                rspFiles.ToArray(),
                                Unity.BuildTools.HostPlatform.IsWindows ? "type" : "cat",
                                new[]
                                {
                                    string.Join(
                                        " ",
                                        Enumerable.Append(rspFiles.Select(f => f.ToString(SlashMode.Native)),
                                            $"> {finalRsp.MakeAbsolute().ToString(SlashMode.Native)}"))
                                });
                            /*
                             * then, set up burst itself.
                             */
                            burstLib =
                                NativePlaybackMode.SetupBurstCompilationForAssemblies(BurstCompilerForPlatform(v),
                                    typeregdGame,
                                    finalRsp,
                                    burstPatchedAssembly.Path.Parent.Combine($"burstedObjectFilesFor{v.Platform}"));
                        }

                        /*
                         * then, set up the linker. 
                         */
                        var linkerOutput = v.dpc.UseLinker
                            ? Il2Cpp.SetupLinker(burstPatchedAssembly, v)
                            : burstPatchedAssembly;

                        var il2cppOutputFiles = SetupIl2cppInvocation(linkerOutput, v.dpc.ScriptingBackend);

                        var np = NativeProgramForModule(game);
                        np.Sources.Add(c => c.Equals(v), il2cppOutputFiles);
                        
                        if (v.dpc.UseBurst)
                            np.Libraries.Add(c => c.Equals(v), burstLib);
                        
                        //todo: don't add these things here, add them outside
                        if (HostPlatform.IsWindows)
                            np.Libraries.Add(
                                c =>
                                    c.Platform is WindowsPlatform &&
                                    ((DotsNativeConfiguration) c).dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp,
                                _libIl2CppLackeyDll);
                        
                        np.Libraries.Add(c => c.ToolChain.Platform is WindowsPlatform, new SystemLibrary("kernel32.lib"));

                        np.DynamicLinkerSettingsForMsvc()
                            .Add(l => l.WithSubSystemType(SubSystemType.Console).WithEntryPoint("wWinMainCRTStartup"));
                        np.Libraries.Add(c => c.Platform is WebGLPlatform,
                            new PreJsLibrary(BuildProgram.BeeRoot.Combine("tiny_runtime.js")));
                    }
                }
            }
            
            /*
             * okay now, EVERYTHING has been set up, and we just need to call SetupSpecificConfiguration() and
             * DeployTo() a bunch of times, set up some alias dependencies, and stuff like that.
             *
             * Note that NOBODY is supposed to call SetupSpecificConfiguration or For() on ANYTHING before this line,
             * and NOBODY is supposed to call .Add on ANYTHING, AFTER this line.
             * ==================================================================
             * capeesh?
             */
           
            
            foreach (var game in mainModules)
            {
                var np = NativeProgramForModule(game);
                Console.WriteLine($"{game.Name} part 2");

                foreach (var v in AllPossibleVariations)
                {
                    if (!game.SupportedOn(v.Platform) ||
                        (v.dpc.ScriptingBackend == ScriptingBackend.TinyIl2cpp && game.IsTestModule))
                        continue;
                    var humanTarget = HumanTargetNameFor(game.Name, v);
                    var destinationPath = $"build/{game.Name}/{humanTarget}".ToNPath();

                    if (v.dpc.ScriptingBackend == ScriptingBackend.Dotnet)
                    {
                        var finalAsm = AddDeployablesForDependencies(
                            _typeregdAssembliesForModules[game.Name][ManagedFromNativeConfig(v)],
                            game,
                            v);
                        var gamePath = finalAsm.DeployTo(destinationPath).Path;

                        if (game.Name.EndsWith("Tests"))
                        {
                            var prefix = "AllTests";
                            if (v.ToolChain.Architecture.Bits == 32)
                                prefix += 32;
                            Backend.Current.AddAliasDependency(prefix, gamePath);
                            Backend.Current.AddAliasDependency($"{prefix}-{v.CodeGen.ToString().ToLower()}", gamePath);
                        }

                        Backend.Current.AddAliasDependency(v.Identifier, gamePath);
                        Backend.Current.AddAliasDependency(game.Name + "-all", gamePath);
                        Backend.Current.AddAliasDependency(humanTarget, gamePath);
                        CopyAdditionalResources(game, v, gamePath);
                    }

                    else
                    {
                        var toolChainExecutableFormat = v.ToolChain.ExecutableFormat;

                        if (v.Platform is WebGLPlatform)
                        {
                            toolChainExecutableFormat = new EmscriptenExecutableFormat(
                                v.ToolChain as EmscriptenToolchain,
                                "html");
                        }

                        var built = np.SetupSpecificConfiguration(v, toolChainExecutableFormat);

                        var final = built.DeployTo(destinationPath);
                        Backend.Current.AddAliasDependency(v.Identifier, final.Path);
                        Backend.Current.AddAliasDependency(humanTarget, final.Path);
                        Backend.Current.AddAliasDependency(game.Name + "-all", final.Path);

                        CopyAdditionalResources(game, v, final.Path);
                    }
                }
            }
            
            VSSolution = new VisualStudioSolution()
            {
                Path = "DotsBootstrap.gen.sln"
            };
            foreach (var m in modules)
            {
                if (m.Name == "Unity.ZeroPlayer.TypeRegGen" || m.Name == "TinyTestFramework")
                    continue;
                if (m.IsTestModule)
                    VSSolution.Projects.Add(CSharpProgramForModule(m, _mainProgramNames.Contains(m.Name)), "tests");
                else
                    VSSolution.Projects.Add(CSharpProgramForModule(m, _mainProgramNames.Contains(m.Name)));
            }

            VSSolution.Projects.Add(BuildProgram.TypeRegGen, "support");
            VSSolution.Projects.Add(BuildProgram.BurstPatcher, "support");
            VSSolution.Projects.Add(BuildProgram.EntityBuildUtils, "support");
            VSSolution.Projects.Add(BuildProgram.TinyCorlib, "support");
            VSSolution.Projects.Add(BuildProgram.UnpatchedUnsafeUtility, "support");
            VSSolution.Projects.Add(BindGem.Instance().BindGemProgram, "support");
            var setupSln = VSSolution.Setup();
            Backend.Current.AddAliasDependency("ProjectFiles", setupSln);
        }
/*
 * for short human-readable target names, take the strategy that
 * a) the game name with no suffixes is the default variation (i.e. the first one in the list)
 * b) other variations are suffixed with only the minimal number of differences to fully specify
 *
 * so for MoleTiny, as of this writing, the variations are as follows
 *  - MoleTiny
 - MoleTiny-Dotnet
 - MoleTiny-Dotnet-release
 - MoleTiny-Dotnet-release-nondev
 - MoleTiny-WebGL  <--- does not need to specify {TinyIl2cpp, no burst} because those are defaults for webgl
 - MoleTiny-WebGL-release
 - MoleTiny-WebGL-release-nondev
 - MoleTiny-all <---- builds all variations
 - MoleTiny-release <--- does not need to specify development, because that's default
 - MoleTiny-release-nondev
 */
        private static string HumanTargetNameFor(string gamename, DotsNativeConfiguration v)
        {
            var variationsToChooseFrom = AllPossibleVariations;
            var defaultVariation = variationsToChooseFrom.First();
            var ret = gamename;
            if (v.Equals(defaultVariation)) return ret;
            
            if (!v.Platform.Equals(defaultVariation.Platform))
            {
                variationsToChooseFrom = variationsToChooseFrom.Where(c => c.Platform.Equals(v.Platform)).ToList();
                defaultVariation = variationsToChooseFrom.First();
                if (v.Platform is WebGLPlatform)
                    //later we'll differentiate between asmjs and wasm, when we have that
                    ret += "-asmjs";
                else
                    ret += "-" + v.Platform.Name.ToLower();
                if (v.Equals(defaultVariation)) return ret;
            }

            var defaultBits = defaultVariation.ToolChain.Architecture.Bits;
            if (v.ToolChain.Architecture.Bits != defaultBits)
            {
                
                variationsToChooseFrom = variationsToChooseFrom
                    .Where(c => c.ToolChain.Architecture.Bits == v.ToolChain.Architecture.Bits)
                    .ToList();
                defaultVariation = variationsToChooseFrom.First();
                ret += v.ToolChain.Architecture.Bits;
                if (v.Equals(defaultVariation)) return ret;
            }

            if (v.dpc.ScriptingBackend != defaultVariation.dpc.ScriptingBackend)
            {
                variationsToChooseFrom = variationsToChooseFrom
                    .Where(c => c.dpc.ScriptingBackend.Equals(v.dpc.ScriptingBackend))
                    .ToList();
                defaultVariation = variationsToChooseFrom.First();
                ret += "-" + v.dpc.ScriptingBackend.ToString().ToLower();
                if (v.Equals(defaultVariation)) return ret;
            }
            
            if (v.CodeGen != defaultVariation.CodeGen)
            {
                variationsToChooseFrom =
                    variationsToChooseFrom.Where(c => c.CodeGen == v.CodeGen).ToList();
                defaultVariation = variationsToChooseFrom.First();
                ret += "-" + (v.CodeGen == CodeGen.Debug ? "debug" : "release");
                if (v.Equals(defaultVariation)) return ret;
            }

            if (v.dpc.Development != defaultVariation.dpc.Development)
            {
                variationsToChooseFrom =
                    variationsToChooseFrom.Where(c => c.dpc.Development == v.dpc.Development).ToList();
                defaultVariation = variationsToChooseFrom.First();
                ret += "-" + (v.dpc.Development ? "dev" : "nondev");
                if (v.Equals(defaultVariation)) return ret;
            }

            return ret;
        }

        private static void CopyAdditionalResources(Module game, DotsNativeConfiguration v, NPath destinationPath)
        {
            foreach (var r in game.Cs.For(ManagedFromNativeConfig(v))
                .First()
                .Parent.Files()
                .Where(f => f.HasExtension(ResourceFileExtensionsFor(v).ToArray())))
                Backend.Current.AddDependency(
                    destinationPath,
                    CopyTool.Instance().Setup(destinationPath.Parent.Combine(r.FileName), r));
        }

        private static DotNetAssembly AddDeployablesForDependencies(DotNetAssembly baseGame, Module game, DotsNativeConfiguration v)
        {
            return baseGame.WithDeployables(
                game.AllRecursiveDependencies(_namesToModules)
                    .Where(
                        name => _namesToModules[name].SupportedOn(v.Platform) &&
                                !_nativeProgramsForModules[name].Sources.For(v).Empty())
                    .Select(
                        name => _nativeProgramsForModules[name]
                            .SetupSpecificConfiguration(v, v.ToolChain.DynamicLibraryFormat))
                    .ToArray());
        }
        
        public static IEnumerable<string> ResourceFileExtensionsFor(DotsNativeConfiguration c)
        {
            yield return "png";
            yield return "jpg";
            yield return "wav";
            if (c.Platform is WebGLPlatform)
            {
                yield return "webp";
                yield return "js";
            }
        }

        private static IEnumerable<ILibrary> BuiltNPForConfig(DotsNativeConfiguration c, NativeProgram depnp)
        {
            var nativeProgramAsLibrary = new NativeProgramAsLibrary(depnp);
            if (c.ToolChain.DynamicLibraryFormat == null && c.dpc.ScriptingBackend != ScriptingBackend.Dotnet)
                nativeProgramAsLibrary.BuildMode = NativeProgramLibraryBuildMode.StaticLibrary;
            else
                nativeProgramAsLibrary.BuildMode = NativeProgramLibraryBuildMode.Dynamic;
            return new[] {nativeProgramAsLibrary};

        }

        NPath[] SetupIl2cppInvocation(DotNetAssembly inputAssembly, ScriptingBackend sb)
        {
            var il2CppCommand = _il2CppExe.ToString(SlashMode.Native);
            if (!HostPlatform.IsWindows)
                il2CppCommand = $"mono --debug {il2CppCommand}";

            var profile = "unitydots";
            var il2CppTargetDir =
                inputAssembly.Path.Parent.Combine(inputAssembly.Path.FileName + "-il2cpp-sources");


            var args = new List<string>()
            {
                "--convert-to-cpp",
                "--disable-cpp-chunks",

                //  "--directory", $"{InputAssembly.Path.Parent}",
                "--generatedcppdir",
                $"{il2CppTargetDir}",

                // Make settings out of these
                $"--dotnetprofile={profile}", // Resolve from DotNetAssembly
                "--libil2cpp-static",
                "--emit-null-checks=0",
                "--enable-array-bounds-check=0",
                "--enable-predictable-output",
                //"--enable-stacktrace=1"
                //"--profiler-report",
                //"--enable-stats",
            };

            var iarrdis = inputAssembly.RecursiveRuntimeDependenciesIncludingSelf;
            args.AddRange(
                iarrdis.SelectMany(a =>
                    new[] {"--assembly", a.Path.ToString()}));

            var il2cppOutputFiles = new[]
                {
                    // static files
                    //"Il2CppComCallableWrappers.cpp",
                    //"Il2CppProjectedComCallableWrapperMethods.cpp",
                    "DotsTypes.cpp", "driver.cpp", "StaticConstructors.cpp", "StringLiterals.cpp",
                    "GenericMethods.cpp",
                    "Generics.cpp", "ReversePInvokeWrappers.cpp",
                    "StaticInitialization.cpp"
                }.Concat(iarrdis.Select(asm => asm.Path.FileNameWithoutExtension + ".cpp"))
                .Select(il2CppTargetDir.Combine)
                .ToArray();

            var il2cppInputs = Il2Cpp.Distribution.GetFileList("build")
                .Concat(iarrdis.SelectMany(a => a.Paths))
                .Concat(new[] {Il2Cpp.Distribution.Path.Combine("dots", "dots.icalls").ToString()}
                    .ToNPaths());

            Backend.Current.AddAction(
                "Il2Cpp",
                targetFiles: il2cppOutputFiles,
                inputs: il2cppInputs.ToArray(),
                il2CppCommand,
                args.ToArray());

            return il2cppOutputFiles;
        }
    }
}
