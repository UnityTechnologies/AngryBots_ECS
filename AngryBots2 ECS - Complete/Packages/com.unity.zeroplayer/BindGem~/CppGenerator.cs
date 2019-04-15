using ExtensionMethods;
using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    internal class CppBindingsGenerator : ILanguageHandler
    {
        private StringBuilder hppSrc = new StringBuilder();
        private StringBuilder cppSrc = new StringBuilder();
        private StringBuilder exportsStr = new StringBuilder();

        int componentIndex = 1;

        public CppBindingsGenerator()
        {
            if (!BindGem.PureJS && !BindGem.DOTS)
            {
                cppSrc.AppendLine(@"#include ""PtrTable.h""");
            }
        }

        public string GetGeneratedSource(SourceLanguage lang) {
            if (lang == SourceLanguage.CPlusPlus)
                return cppSrc.ToString();
            if (lang == SourceLanguage.CPlusPlusHeader)
                return hppSrc.ToString();
            if (lang == SourceLanguage.ExportsList)
                return exportsStr.ToString();
            return null;
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            if (lang == SourceLanguage.CPlusPlus ||
                lang == SourceLanguage.CPlusPlusHeader)
                return 100;
            return 0;
        }

        public void cpp(string s) {
            cppSrc.AppendLine(s);
        }

        public void hpp(string s) {
            hppSrc.AppendLine(s);
        }

        public string InsertEmscriptenStaticAssertsForStruct(TypeDefinition type, bool generateStaticAssertsForOffsets, int bits)
        {
            string code =
@"
#ifdef {{{bits}}}
{{{beginNamespace}}}
static_assert(sizeof({{{structName}}}) == {{{sizeofStruct}}}, ""{{{structName}}} size mismatch"");
{{{static_assert(offsetof(...));}}}
{{{endNamespace}}}
#endif";

            // In C++, all types, even a "struct {}", must have a nonzero size. However when such a struct is
            // derived from, in the derived class its size is generally 0 (Empty Base Optimization). Because
            // of this duality, we don't store in TypeUtils.ValueTypeAlignment the non-zero size since that
            // is also used for inherited size computations, but instead here when generating final code,
            // generate sizes of empty structs as 1.
            int sizeOfStruct = Math.Max(TypeUtils.AlignAndSizeOfType(type, bits).size, 1);

            code = WrapInNamespace(code, type.Namespace);
            code = ExpandStringTemplate(code,
                "bits", bits == 32 ? "UT_32BIT" : "UT_64BIT",
                "structName", type.CppName(),
                "sizeofStruct", sizeOfStruct.ToString());

            if (generateStaticAssertsForOffsets)
            {
                List<string> fieldsChecks = new List<string>();
                foreach (var field in type.Fields) {
                    if (field.IsStatic)
                        continue;
                    if (field.FieldType.IsDynamicArray())
                        continue;
                    fieldsChecks.Add(
                        $"static_assert(offsetof({type.Name}, {field.Name}) == {TypeUtils.AlignAndSizeOfField(field, bits).offset}, \"{type.Name}.{field.Name} offset mismatch\");");
                }

                // Anything that's internal has the C++ side handled by hand
                code = ExpandStringTemplate(code, "static_assert(offsetof(...));", fieldsChecks);
            }
            else
                code = ExpandStringTemplate(code, "static_assert(offsetof(...));", "");
            return code;
        }

        public void HandleStructType(TypeDefinition type)
        {
            string cppCode = "{{{sizeCheckNoteForInternalStructTypes}}}"
                + InsertEmscriptenStaticAssertsForStruct(type, !type.HasAttribute("CppCustomImpl"), 32)
                + InsertEmscriptenStaticAssertsForStruct(type, !type.HasAttribute("CppCustomImpl"), 64);

            if (type.HasAttribute("CppCustomImpl"))
            {
                // insert static asserts for offsets
                cppCode = ExpandStringTemplate(cppCode,
                    "sizeCheckNoteForInternalStructTypes", $"/* Generating only size check for internal struct {type.FullName} */\n");
            }
            else
            {
                // Anything that's internal has the C++ side handled by hand
                cppCode = ExpandStringTemplate(cppCode,
                    "sizeCheckNoteForInternalStructTypes", "");
                BindGem.WarningIf(type.HasAttribute("CppName"),
                    $"Type {type} has CppName but is not CppCustomImpl -- CppName will be ignored for fully generated types");
            }
            cpp(cppCode);

            if (!type.HasAttribute("CppCustomImpl"))
            {
                BindGem.FatalErrorIf(type.HasAttribute("SharedPtrAttribute")/*, type*/,
                    $"Can't have a SharedPtr type {type} that's not also [Internal]");
                var allFields = type.Fields.Where(f => !f.IsStatic);
                var fields = allFields.Select(field => (field.FieldType.IsDynamicArray() ? $"/* DYNAMIC ARRAY -- {field.Name}; */" : $"{field.FieldType.CppFieldType()} {field.Name};"));

                string hppTemplate =
@"struct {{{structName}}} {
  {{{type field;}}}
};";
                var hppCode = ExpandStringTemplate(hppTemplate,
                    "structName", type.Name,
                    "type field;", fields);

                if (!BindGem.DOTS) {
                    // Register Struct
                    var name = type.Name;
                    var namespacedName = type.JSName();
                    var identifier = type.FullyQualifiedCppName("_").ToLower();
                    var members = type.Fields.Select(f => $"  ut::meta::member(\"{f.Name}\", &{name}::{f.Name})");
                    var body = $"{(members.Count() > 0 ? ",\n" : "")}{string.Join(",\n", members)}";
                    hppCode += ExpandStringTemplate(
                        "\nREFLECT_STRUCT({{{name}}}, \"{{{namespacedName}}}\", {{{identifier}}}{{{body}}})",
                        "name", name,
                        "namespacedName", namespacedName,
                        "identifier", identifier,
                        "body", body);
                }

                hpp(WrapInNamespace(hppCode, type.Namespace));
            }
        }

        static List<string> UnnamedParameterList(IEnumerable<ParameterDefinition> parameters)
        {
            List<string> args = new List<string>();
            int i = 0;
            foreach(var p in parameters)
            {
                args.Add(p.ParameterType.EmCppArgType() + " arg" + i++.ToString());
            }
            return args;
        }


        static List<string> UnnamedArgumentList(IEnumerable<ParameterDefinition> parameters)
        {
            List<string> args = new List<string>();
            int i = 0;
            foreach (var p in parameters)
            {
                args.Add(" arg" + i++.ToString());
            }
            return args;
        }

        public void HandleComponentType(TypeDefinition type)
        {
            if (type.HasAttribute("CppCustomImpl"))
            {
                cpp($"/* Skipped generating code for internal component {type} */");
                return;
            }

            BindGem.WarningIf(type.HasAttribute("CppName"),
                $"Type {type} has CppName but is not CppCustomImpl -- CppName will be ignored for fully generated types");

            // Validate the field names
            if (!BindGem.DOTS)
                foreach (var f in type.Fields)
                    BindGem.FatalErrorIf(!Char.IsLower(f.Name[0]), /*f, TODO*/ $"Field {f.Name} of {type.Name} must start with a lowercase letter!");

	    // g++ has a bug where it doesn't actually emit this function unless it's explicitly marked as used.
	    // But we must not use this for emscripten, because (used) means export it to JS as well.
            string entityFixupTemplate =
@"  struct ComponentReflectionData {
    using ReflectionDataType_ = {{{name}}};
    static const std::initializer_list<int>&
    #if defined(__GNUC__) && !defined(__EMSCRIPTEN__) && !defined(__clang__)
    __attribute__((noinline, used))
    #endif
    EntityOffsets() {
      static const std::initializer_list<int> l = {
        {{{entityfixups}}}
      };
      return l;
    }
  };";

            string hppTemplate =
@"struct {{{name}}} {{{maybeExtends}}}{
  {{{fields}}}
  {{{constructors}}}
  {{{memberFunctions}}}
  {{{entityOffsetDecl}}}
};";

            // Data fields
            var fields = type.Fields.Where(f => !f.IsStatic).Select(field => $"{field.FieldType.CppFieldType()} {field.Name};").ToList();

            string entityOffsetDecl = null;
            List<string> ctors = new List<string>();
            List<string> memberFunctions = new List<string>();

            if (!BindGem.DOTS) {
                // Internal entity field offsets, if any
                // Note we are assuming 32-bit platform here for offset calculations
                var entityOffsets = TypeUtils.GetEntityFieldOffsets(type, 32);
                if (entityOffsets.Count != 0) {
                    entityOffsetDecl = ExpandStringTemplate(entityFixupTemplate,
                        "name", type.Name,
                        "entityfixups", string.Join(",", entityOffsets));
                }

                // Constructors
                bool hasZeroArgConstructor = false;
                foreach (var ctor in type.Constructors()) {
                    //var args = ctor.Parameters.Select(arg => $"{arg.ParameterType.Resolve().EmCppArgType()} {arg.Name}");
                    var args = UnnamedParameterList(ctor.Parameters);
                    if (args.Count() == 0) hasZeroArgConstructor = true;
                    ctors.Add($"{type.Name}({string.Join(",", args)});");
                }

                if (ctors.Count > 0 && !hasZeroArgConstructor)
                    ctors.Insert(0, $"{type.Name}() = default;");

                // Member functions
                foreach (var method in type.MemberFunctions()) {
                    // TODO: I think this is better to keep the argument names when generating, rather than an unnamed list "arg0,arg1", but keep the output
                    // identical for easy verification for now.
                    //                var args = method.Parameters.Select(arg => $"{arg.ParameterType.Resolve().EmCppArgType()} {arg.Name}");
                    var args = UnnamedParameterList(method.Parameters);
                    memberFunctions.Add(
                        $"{type.EmCppReturnType(method)} {method.CppName()}({string.Join(",", args)});");
                }
            }

            var extends = "";
            var ns = "ut";
            if (BindGem.DOTS)
                ns = "Unity::Entities";

            if (type.IsComplex())
                extends = $": {ns}::IComplexComponentData ";
            else if (type.IsSharedComponentType())
                extends = $": {ns}::ISharedComponentData ";
            else if (type.IsSystemStateComponentType())
                extends = $": {ns}::ISystemStateComponentData ";
            else if (type.IsBufferElementComponentType())
                extends = $": {ns}::IBufferElementData ";

            string hppCode = ExpandStringTemplate(hppTemplate,
                "name", type.Name,
                "maybeExtends", extends,
                "fields", fields,
                "constructors", ctors,
                "memberFunctions", memberFunctions,
                "entityOffsetDecl", entityOffsetDecl);

            if (!BindGem.DOTS) {
                // Register Component
                var name = type.Name;
                var namespacedName = type.JSName();
                var identifier = type.FullyQualifiedCppName("_").ToLower();
                var members = type.Fields.Select(f => $"  ut::meta::member(\"{f.Name}\", &{name}::{f.Name})");
                var body = $"{(members.Count() > 0 ? ",\n" : "")}{string.Join(",\n", members)}";
                hppCode += ExpandStringTemplate(
                    "\nREFLECT_COMPONENT({{{name}}}, \"{{{namespacedName}}}\", {{{identifier}}}{{{body}}})",
                    "name", name,
                    "namespacedName", namespacedName,
                    "identifier", identifier,
                    "body", body);
            }

            hpp(WrapInNamespace(hppCode, type.Namespace));

            // insert static asserts for field offsets in the C++
            cpp(InsertEmscriptenStaticAssertsForStruct(type, true, 32));
            cpp(InsertEmscriptenStaticAssertsForStruct(type, true, 64));

            // Make the component info templated functions extern for compile time speedups
            // Explicitly don't do this for ComponentId<T>, since we want that to get inlined
            // as much as possible
            // extern template ComponentTypeId ComponentId<::{nsn}>();
            // template ComponentTypeId ComponentId<::{nsn}>();

            var nsn = type.FullyQualifiedCppName();
            var thisprivate = $"priv_{BindGem.BaseOutputName}_{componentIndex}";
            componentIndex++;

            if (!BindGem.DOTS) {
                hpp($@"
namespace ut {{
#if !defined({BindGem.DefineGuard})
extern template ComponentInfo& InitComponentInfoFor<::{nsn}>();
#endif
}}
");
            }
            else
            {
                hpp($@"
#if !defined({BindGem.DefineGuard})
    extern DLLIMPORT ComponentTypeId {thisprivate}_cid;
#else
    extern DLLEXPORT ComponentTypeId {thisprivate}_cid;
#endif

template<> inline ComponentTypeId ComponentId<::{nsn}>() {{
    return {thisprivate}_cid;
}}

template<> inline ComponentTypeId InitComponentId<::{nsn}>()
{{
    if ({thisprivate}_cid == -1) {{
        {thisprivate}_cid = Unity::Entities::TypeManager::TypeIndexForStableTypeHash({type.CalculateStableTypeHash()}ull);
    }}
    return {thisprivate}_cid;
}}
");
            }

            if (!BindGem.DOTS) {
                cpp($@"
namespace ut {{
template ComponentInfo& InitComponentInfoFor<::{nsn}>();
ComponentTypeId {thisprivate}_cid = -1;
template<> ComponentTypeId InitComponentId<::{nsn}>()
{{
    if ({thisprivate}_cid == -1) {{
        {thisprivate}_cid = InitComponentInfoFor<::{nsn}>().cid;
    }}
    return {thisprivate}_cid;
}}
}}
");
            } else {
                cpp($@"
DLLEXPORT ComponentTypeId {thisprivate}_cid = -1;
");
            }
        }

        public void HandleCallbackType(TypeDefinition type)
        {
        }

        public void HandleInterfaceType(TypeDefinition type)
        {
            if (type.HasAttribute("CppCustomImpl"))
            {
                cpp($"/* Skipped generating code for internal interface {type} */");
                return;
            }
        }

        // Given an index to the given code string, returns the substring before it that represents the indentation up to that position.
        // Used to replicate same indentation level on multiple lines of code.
        public static string GetIndentation(string code, int index)
        {
            string indentation = "";
            --index;
            while (index > 0 && (code[index] == ' ' || code[index] == '\t'))
            {
                indentation = code[index] + indentation;
                --index;
            }
            return indentation;
        }

        // Performs a single substitution of a {{{pattern}}} -> expansion search-replace. If 'expansion'
        // is a multiline block of text, preserve the indentation of the expanded text across all lines
        // to make the expanded code more readable.
        public static string ExpandSingleTemplateVar(string template, string pattern, string expansion)
        {
            for(; ;)
            {
                int pos = template.IndexOf(pattern);
                if (pos == -1) return template;
                string indentation = GetIndentation(template, pos);
                expansion = expansion.Replace("\n", "\n" + indentation);
                template = template.Substring(0, pos) + expansion + template.Substring(pos + pattern.Length);
            }
        }
        public static string ExpandStringTemplate(string template, params object[] expansions)
        {
            for (var i = 0; i < expansions.Length; i += 2)
            {
                string key = (string)expansions[i];
                object expansion = expansions[i + 1];
                if (expansion == null || (expansion is string && ((string)expansion).Length == 0) || (expansion is IEnumerable<string> && ((IEnumerable<string>)expansion).Count() == 0))
                {
                    // If expanding with an empty string, and the pattern to expand is on its own line, swallow the whole line:
                    string pattern = "\n\\s*{{{\\s*" + Regex.Escape(key) + "\\s*}}}\\s*\r?\n";
                    Regex regex = new Regex(pattern);
                    template = regex.Replace(template, "\n");

                    // And also do the regular expansion if the pattern to expand with is not self-contained on its own line.
                    template = template.Replace("{{{" + expansions[i] + "}}}", "");
                }
                else if (expansion is string)
                {
                    template = ExpandSingleTemplateVar(template, "{{{" + expansions[i] + "}}}", (string)expansion);
                }
                else if (expansion is IEnumerable<string>)
                {
                    template = ExpandSingleTemplateVar(template, "{{{" + expansions[i] + "}}}", String.Join("\n", ((IEnumerable<string>)expansion)));
                }
                else throw new Exception("Invalid expansion parameter type " + expansion.ToString());
            }
            return template;
        }

        public void HandleEnumType(TypeDefinition type)
        {
            if (type.HasAttribute("CppCustomImpl"))
                return;

            BindGem.WarningIf(type.HasAttribute("CppName"),
                $"Type {type} has CppName but is not CppCustomImpl -- CppName will be ignored for fully generated types");

            var enumSize = type.GetEnumSize();
            string baseTypeStr = null;
            if (enumSize == 8)
                baseTypeStr = "int8_t";
            else if (enumSize == 16)
                baseTypeStr = "int16_t";
            else if (enumSize == 32)
                baseTypeStr = "int32_t";
            else if (enumSize == 64)
                baseTypeStr = "int64_t";

            string enumTemplate =
@"enum class {{{typeName}}} : {{{baseEnumType}}} {
{{{name=value}}}
};";

            var enumList = type.GetEnumKeyValues();
            var enumValues = enumList.Select(e => $"  {e.key} = {e.value}");
            var code = ExpandStringTemplate(enumTemplate,
                "typeName", type.Name,
                "baseEnumType", baseTypeStr,
                "name=value", string.Join(",\n", enumValues));

            if (!BindGem.DOTS) {
                // Register Enum
                var name = type.Name;
                var namespacedName = type.JSName();
                var identifier = type.FullyQualifiedCppName("_").ToLower();
                var members = enumList.Select(e => $"  ut::meta::enumv(\"{e.key}\", {name}::{e.key})");
                var body = $"{(members.Count() > 0 ? ",\n" : "")}{string.Join(",\n", members)}";
                code += ExpandStringTemplate(
                    "\nREFLECT_ENUM({{{name}}}, \"{{{namespacedName}}}\", {{{identifier}}}{{{body}}})",
                    "name", name,
                    "namespacedName", namespacedName,
                    "identifier", identifier,
                    "body", body);
            }

            hpp(WrapInNamespace(code, type.CppNamespace()));
        }

        public void HandleSystemType(TypeDefinition type)
        {
            if (BindGem.DOTS)
                return;

            // If it's PureJS, we can't construct this system from native code
            if (BindGem.PureJS)
                return;

            if (!type.HasAttribute("CppCustomImpl"))
            {
                // generate a shell if it's not already expected to exist
                string hppTemplate =
@"class {{{name}}} : public System {
public:
    virtual ~{{{name}}}() = default;
    static std::shared_ptr<System> get() { return std::shared_ptr<{{{name}}}>(new {{{name}}}()); }
    {{{updateFunction}}}
    {{{addedFunction}}}
    {{{removedFunction}}}
protected:
    {{{name}}}() : System(""{{{fullName}}}"") {
        {{{updateBefores}}}
        {{{updateAfters}}}
    }
};";
                hppTemplate = WrapInNamespace(hppTemplate, type.Namespace);

                var updateBefores = type.GetSystemRunsBefore().Select(value => $"updateBefore(\"{value.FullName}\");");
                var updateAfters = type.GetSystemRunsAfter().Select(value => $"updateAfter(\"{value.FullName}\");");

                string hppCode = ExpandStringTemplate(hppTemplate,
                    "name", type.Name,
                    "fullName", type.FullName,
                    "updateFunction", type.IsSystemFenceType()
                        ? "virtual void update(Scheduler&, ManagerWorld&) override {}"
                        : "virtual void update(Scheduler& s, ManagerWorld& w) override;",
                    "addedFunction", type.IsSystemFenceType()
                        ? "virtual void added(Scheduler&, ManagerWorld&) override {}"
                        : "virtual void added(Scheduler& s, ManagerWorld& w) override;",
                    "removedFunction", type.IsSystemFenceType()
                        ? "virtual void removed(Scheduler&, ManagerWorld&) override {}"
                        : "virtual void removed(Scheduler& s, ManagerWorld& w) override;",
                    "updateBefores", updateBefores,
                    "updateAfters", updateAfters);
                hpp(hppCode);
            }

            exportSymbol(type.FullyQualifiedCppName("_"));

            string cppTemplate =
@"
UT_JSFN ut::System* {{{cppName}}}() {
  return ut::PtrTable::persist<ut::System>({{{fullName}}}::get());
}
";
        string cppCode = ExpandStringTemplate(cppTemplate,
                "cppName", type.FullyQualifiedCppName("_"),
                "fullName", type.FullyQualifiedCppName());
            cpp(cppCode);
        }

        // Outputs the given block of code wrapped inside nested
        // "namespace A { namespace B { /* code */ } }" format.
        // Use C# period "." to address nested namespaces, instead of C++ "::",
        // i.e. "A.B" instead of "A::B".
        // If the code contains template blocks {{{beginNamespace}}} or {{{endNamespace}}},
        // the namespace is expanded in their place. Otherwise the whole code block
        // is wrapped inside the namespace.
        internal static string WrapInNamespace(string code, string nameSpace)
        {
            if (nameSpace == null || nameSpace.Length == 0)
            {
                code = code.Replace("{{{beginNamespace}}}", "");
                code = code.Replace("{{{endNamespace}}}", "");
                return code;
            }
            var namespaces = nameSpace.Split('.');
            namespaces[0] = BindGem.TranslateCppTopLevelNamespace(namespaces[0]);
            string pre = "", post = "";
            foreach (var name in namespaces)
            {
                pre += $"namespace {name} {{ ";
                post += "}";
            }
            if (code.Contains("{{{beginNamespace}}}")) code = code.Replace("{{{beginNamespace}}}", pre);
            else code = pre + '\n' + code;

            if (code.Contains("{{{endNamespace}}}")) code = code.Replace("{{{endNamespace}}}", post);
            else code = code + '\n' + post;

            return code;
        }

        internal static string WrapInIfdef(string code, string ifdef)
        {
            return $"#ifdef {ifdef}\n" + code.TrimEnd() + "\n#endif";
        }

        private void exportSymbol(string symbol)
        {
            exportsStr.AppendLine(symbol);
        }
    }

    internal static class CppExtensionMethods
    {
        internal static string WithUppercaseFirstLetter(this string str)
        {
            if (str == null)
                return null;

            if (str.Length > 1)
                return char.ToUpper(str[0]) + str.Substring(1);

            return str.ToUpper();
        }

        internal static string WithLowercaseFirstLetter(this string str)
        {
            if (str == null)
                return null;

            if (str.Length > 1)
                return char.ToLower(str[0]) + str.Substring(1);

            return str.ToLower();
        }
    }
}
