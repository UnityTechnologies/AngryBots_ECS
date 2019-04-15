using ExtensionMethods;
using Mono.Cecil;
using Mono.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Entities.BuildUtils;
#if DEBUG
using System.Diagnostics;
#endif

namespace BindGem
{

    enum SourceLanguage
    {
        JavaScript,
        CPlusPlus,
        CPlusPlusHeader,
        CSharp,
        JSDoc,
        TypeScriptDefinition,
        ExportsList
    }

    internal interface ILanguageHandler
    {
        void HandleInterfaceType(TypeDefinition type);
        void HandleCallbackType(TypeDefinition type);
        void HandleStructType(TypeDefinition type);
        void HandleComponentType(TypeDefinition type);
        void HandleEnumType(TypeDefinition type);
        void HandleSystemType(TypeDefinition namedTypeSymbol);

        string GetGeneratedSource(SourceLanguage lang);

        // a bit of a hack -- for every SourceLanguage, the generation
        // is sorted by this weight first.  Higher means rendered earlier.
        int GeneratedSourceWeight(SourceLanguage lang);
    }

    internal class BindGem
    {
        internal static List<string> ReferenceDLLFiles = new List<string>();
        internal static List<string> ReferenceSourceFiles = new List<string>();
        internal static List<string> SourceFiles = new List<string>();
        internal static List<string> CppIncludes = new List<string>();
        internal static List<string> CppSrcIncludes = new List<string>();
        internal static string OutFile;
        internal static string BaseOutputName => Path.GetFileNameWithoutExtension(OutFile).Replace("-", "_");
        internal static string DefineGuard;

        internal static Dictionary<string, string> TopLevelNamespaceJsMap = new Dictionary<string, string>()
            { { "UTiny", "ut" } };
        internal static Dictionary<string, string> TopLevelNamespaceCppMap = new Dictionary<string, string>()
            { { "UTiny", "ut" } };

        // If PureJS is true, then components are generated for usage purely from JS;
        // special component storage is used that treats the component as a raw buffer of bytes
        // internally.
        internal static bool PureJS = false;

        internal static bool Verbose = false;
        internal static bool Development = false;

        internal static bool DOTS = false;

        // WARNING: Must match ut::meta::Trait enum defined in C++
        public enum TypeDescTrait { PlainData, Pointer, Array, Buffer, Struct, Component, Enum };

        public static void Main(string[] args)
        {
            // When running debug builds outside a debugger, don't let exceptions leak out from the application,
            // or they would trigger an upload to Windows Error Reporting to find for solutions.
#if DEBUG
            if (Debugger.IsAttached)
                WrappedMain(args);
            else
            {
                try
                {
                    WrappedMain(args);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);
                }
            }
#else
            WrappedMain(args);
#endif
        }

        public static void WrappedMain(string[] args)
        {
            bool referenceBindGem = false;
            var opts = new OptionSet() {
                {"r|ref=", "Reference metadata from specified built file", v => ReferenceDLLFiles.Add(v)},
                {"R|srcref=", "Reference source file, but don't generate code for them", v => ReferenceSourceFiles.Add(Path.GetFullPath(v))},
                {"include=", "Reference source file, but don't generate code for them", v => ReferenceSourceFiles.Add(Path.GetFullPath(v))},
                {"o|out=", "Output base file, without extensions", v => OutFile = v},
                {"cppInclude=", "Cpp #include statement to generate in the header", v => CppIncludes.Add(v)},
                {"cppSrcInclude=", "Cpp #include statement to generate in the source", v => CppSrcIncludes.Add(v)},
                {"j|pure-js", "Generate pure JS bindings for components and structs only", v => PureJS = (v != null)},
                {"v|verbose", "Verbose output, including compilation errors", v => Verbose = true},
                {"internal", "Reference BindGem.exe for bindings attributes", v => referenceBindGem = true},
                {"d|devel", "Enable development build", v => Development = true},
                {"dots", "Enable DOTS mode", v => DOTS = true},
                {"define_guard=", "Define guard to use", v => DefineGuard = v},
            };

            try {
                SourceFiles = opts.Parse(args);
            }
            catch (OptionException e) {
                Console.Write("bindgen: ");
                Console.WriteLine(e.Message);
                opts.WriteOptionDescriptions(Console.Out);
                return;
            }

            if (OutFile == null) {
                opts.WriteOptionDescriptions(Console.Out);
                FatalError($"output must be specified with -o");
            }

            if (DOTS && PureJS) {
                opts.WriteOptionDescriptions(Console.Out);
                FatalError("Can't specify both DOTS mode and PureJS mode");
            }

            if (DefineGuard == null) {
                DefineGuard = $"BINDGEM_BIND_{BindGem.BaseOutputName}_IMPL";
            }

            // C# compiler fails on Windows on paths that are delimited with forward slashes, so normalize all inputs with forward
            // slashes to use backslashes instead.
            if (Path.DirectorySeparatorChar == '\\')
            {
                for (int i = 0; i < ReferenceDLLFiles.Count; ++i) ReferenceDLLFiles[i] = ReferenceDLLFiles[i].Replace('/', Path.DirectorySeparatorChar);
                for (int i = 0; i < ReferenceSourceFiles.Count; ++i) ReferenceSourceFiles[i] = ReferenceSourceFiles[i].Replace('/', Path.DirectorySeparatorChar);
                OutFile = OutFile.Replace('/', Path.DirectorySeparatorChar);
                for (int i = 0; i < CppIncludes.Count; ++i) CppIncludes[i] = CppIncludes[i].Replace('/', Path.DirectorySeparatorChar);
                for (int i = 0; i < SourceFiles.Count; ++i) SourceFiles[i] = SourceFiles[i].Replace('/', Path.DirectorySeparatorChar);
            }

            // Collapse duplicate input entries
            ReferenceSourceFiles = ReferenceSourceFiles.Distinct().ToList();
            CppIncludes = CppIncludes.Distinct().ToList();
            CppSrcIncludes = CppSrcIncludes.Distinct().ToList();

            InterfaceReflector reflector;

            if (DOTS) {
                if (SourceFiles.Count != 1 || ReferenceSourceFiles.Count != 0 || referenceBindGem) {
                    FatalError($"DOTS mode requires exactly one assembly as input source, and no -R srcrefs (only -r assembly refs allowed).  --internal is also not allowed.");
                }

                reflector = InterfaceReflector.FromAssembly(SourceFiles[0], ReferenceDLLFiles);
            } else {
                reflector = InterfaceReflector.FromCSharpSource(SourceFiles, ReferenceSourceFiles, ReferenceDLLFiles, referenceBindGem);
            }

            // Validate that the types we loaded are something we can support
            foreach (var s in reflector.Structs)
                TypeUtils.ValidateAllowedObjectType(s);
            foreach (var s in reflector.Interfaces)
                TypeUtils.ValidateAllowedObjectType(s);
            foreach (var s in reflector.Classes)
                TypeUtils.ValidateAllowedObjectType(s);

            foreach (var s in reflector.Structs) {
                TypeUtils.PreprocessTypeFields(s, 32);
                TypeUtils.PreprocessTypeFields(s, 64);
            }

            var languagesToGenerate = (SourceLanguage[]) Enum.GetValues(typeof(SourceLanguage));
            if (DOTS) {
                languagesToGenerate = new[] {
                    SourceLanguage.CPlusPlus,
                    SourceLanguage.CPlusPlusHeader
                };
            } else if (PureJS) {
                languagesToGenerate = new[] {
                    SourceLanguage.JavaScript,
                    SourceLanguage.TypeScriptDefinition,
                    SourceLanguage.JSDoc,
                    SourceLanguage.CSharp
                };
            }

            var generators = new List<ILanguageHandler>();
            if (DOTS) {
                generators.Add(new CppBindingsGenerator());
            } else {
                generators.Add(new EmscriptenGenerator());
                if (!PureJS)
                    generators.Add(new CppBindingsGenerator());
                generators.Add(new TypeScriptDefinitionGenerator());
                generators.Add(new JSDocGenerator());
                generators.Add(new CGenerator());
                generators.Add(new CsBindingsGenerator());
            }

            foreach (var generator in generators)
            {
                reflector.ExportedEnums.ForEach(t => generator.HandleEnumType(t));
                reflector.ExportedStructs.Where(t => !t.IsComponentType()).ToList().ForEach(t => generator.HandleStructType(t));
                reflector.ExportedStructs.Where(t => t.IsComponentType()).ToList().ForEach(t => generator.HandleComponentType(t));
                reflector.ExportedDelegates.ForEach(t => generator.HandleCallbackType(t));
                reflector.ExportedInterfaces.Where(t => t.HasAttribute("NonSharedPtr") || t.HasAttribute("SharedPtr") || t.HasAttribute("Service")).ToList().ForEach(t => generator.HandleInterfaceType(t));
                reflector.ExportedClasses.Where(t => t.IsSystemType() || t.IsSystemFenceType()).ToList().ForEach(t => generator.HandleSystemType(t));
            }

            foreach (var sourceLang in languagesToGenerate)
            {
                var lh = new List<ILanguageHandler>(generators);
                lh.Sort(Comparer<ILanguageHandler>.Create((l1, l2) =>
                    l2.GeneratedSourceWeight(sourceLang).CompareTo(l1.GeneratedSourceWeight(sourceLang))));
                var src = new StringBuilder();
                foreach (var gen in lh)
                {
                    var langSrc = gen.GetGeneratedSource(sourceLang);
                    if (langSrc == null)
                        continue;
                    src.AppendLine(langSrc);
                }

                if (src.Length == 0)
                    continue;

                var fileName = OutFile + ExtensionForLanguage(sourceLang);
                var f = new StreamWriter(fileName);
                src.Insert(0, PreambleForLanguage(sourceLang));
                src.Append(PostambleForLanguage(sourceLang));
                // sanitize line endings
                src = src.Replace("\r\n", "\n");
                f.Write(src);
                f.Close();
            }
        }

        internal static string PreambleForLanguage(SourceLanguage lang)
        {
            if (lang == SourceLanguage.CPlusPlus) {
                // We use struct-return with C linkage functions in Emscripten because we know what
                // the ABI is, and we need to skip function name mangling.  The warning is suppressed
                // from clang about this.
                var prefix = new StringBuilder();
                prefix.Append(
                    $"/*\n" +
                    $" * AUTO-GENERATED, DO NOT EDIT BY HAND\n" +
                    $" */\n");
                prefix.AppendLine($"#if !defined({DefineGuard})");
                prefix.AppendLine($"#define {DefineGuard} 1");
                prefix.AppendLine($"#endif");
                foreach (var include in BindGem.CppSrcIncludes) {
                    var inc = include.Replace(@"\", "/");
                    prefix.AppendLine($"#include \"{inc}\"");
                }
                prefix.AppendLine($"#include \"{Path.GetFileNameWithoutExtension(BindGem.OutFile) + ".h"}\"");
                prefix.Append(@"
#if defined(__clang__)
#pragma clang diagnostic ignored ""-Wreturn-type-c-linkage""
#elif defined(_MSC_VER)
#pragma warning(disable : 4190)
#endif
");
                return prefix.ToString();
            }

            if (lang == SourceLanguage.CPlusPlusHeader) {
                var hdr = new StringBuilder();
                hdr.Append(
                    $"#pragma once\n" +
                    $"/*\n" +
                    $" * AUTO-GENERATED, DO NOT EDIT BY HAND\n" +
                    $" */\n");

                hdr.AppendLine("#include <cstdint>");
                if (!BindGem.DOTS) {
                    hdr.AppendLine("#include \"NativeBuffer.h\"");
                    hdr.AppendLine("#include \"NativeString.h\"");
                } else {
                    hdr.AppendLine("#include \"EntityTypes.h\"");
                }

                foreach (var include in BindGem.CppIncludes) {
                    var inc = include.Replace(@"\", "/");
                    hdr.AppendLine($"#include \"{inc}\"");
                }
                return hdr.ToString();
            }

            if (lang == SourceLanguage.JavaScript) {
                return
                    $"/*\n" +
                    $" * AUTO-GENERATED, DO NOT EDIT BY HAND\n" +
                    $" */\n";
            }

            if (lang == SourceLanguage.CSharp)
            {
                return $"using Unity.Collections.LowLevel.Unsafe;\n";
            }

            return "";
        }

        internal static string PostambleForLanguage(SourceLanguage lang)
        {
            return "";
        }

        internal static void FatalError(string err)
        {
            Console.WriteLine("Error: " + err);
            System.Environment.Exit(1);
        }

        internal static void FatalErrorIf(bool condition, string err)
        {
            if (condition)
                FatalError(err);
        }

        internal static void Warning(string err)
        {
            Console.WriteLine(err);
        }

        internal static void WarningIf(bool condition, string err)
        {
            if (condition)
                Warning(err);
        }

        internal static string ExtensionForLanguage(SourceLanguage lang)
        {
            switch (lang) {
                case SourceLanguage.JavaScript:
                    return ".js";
                case SourceLanguage.CPlusPlus:
                    return ".cpp";
                case SourceLanguage.CPlusPlusHeader:
                    return ".h";
                case SourceLanguage.CSharp:
                    return ".cs";
                case SourceLanguage.JSDoc:
                    return ".jsdoc";
                case SourceLanguage.TypeScriptDefinition:
                    return ".d.ts";
                case SourceLanguage.ExportsList:
                    return ".exportlist";
            }
            throw new Exception();
        }

        internal static string TranslateJsTopLevelNamespace(string ns)
        {
            if (TopLevelNamespaceJsMap.ContainsKey(ns))
                return TopLevelNamespaceJsMap[ns];
            return ns;

        }

        internal static string TranslateCppTopLevelNamespace(string ns)
        {
            if (!BindGem.DOTS) {
                if (TopLevelNamespaceCppMap.ContainsKey(ns))
                    return TopLevelNamespaceCppMap[ns];
            }

            return ns;

        }
    }
}
