using System.Collections.Generic;
using System.Linq;
using ExtensionMethods;
using Mono.Cecil;
using System.Text;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    internal class CGenerator : ILanguageHandler
    {
        const string UTRT = "Module";

        private StringBuilder cppStr = new StringBuilder();
        private StringBuilder dtsStr = new StringBuilder();

        public string GetGeneratedSource(SourceLanguage lang) {
            if (lang == SourceLanguage.CPlusPlus)
                return cppStr.ToString();
            if (lang == SourceLanguage.TypeScriptDefinition)
                return $"declare namespace {UTRT} {{\n" + dtsStr.ToString() + "\n}\n";
            if (lang == SourceLanguage.ExportsList)
                return exportsStr.ToString();
            return null;
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            return 0;
        }

        private void cpp(string s) {
            cppStr.AppendLine(s);
        }

        private void dts(string s) {
            dtsStr.AppendLine(s);
        }

        private StringBuilder exportsStr = new StringBuilder();
        private void exportSymbol(string symbol)
        {
            exportsStr.AppendLine(symbol);
        }

        public CGenerator()
        {
            if (!BindGem.PureJS) {
                cppStr.AppendLine(
@"#include <functional>
#include ""PtrTable.h""
#ifdef __EMSCRIPTEN__
#include <emscripten/emscripten.h>
#endif");
            }
        }

        enum MakeArgFlags {
            None,
            StartWithComma
        }

        // Generate "arg0, arg1, arg2, ...arg{numParams-1}
        string MakeNArgNames(int numParams, MakeArgFlags flags = MakeArgFlags.None)
        {
            if (numParams == 0)
                return "";
            StringBuilder sb = new StringBuilder();
            if (flags == MakeArgFlags.StartWithComma)
                sb.Append(", ");
            for (int i = 0; i < numParams; ++i) {
                sb.Append($"arg{i}");
                if (i != numParams - 1)
                    sb.Append(", ");
            }
            return sb.ToString();
        }

        public void HandleInterfaceType(TypeDefinition type)
        {
            if (type.HasAttribute("PureJSService"))
                return;

            BindGem.FatalErrorIf(BindGem.PureJS,
                "Interfaces cannot be used with pure JS generation (what are you trying to bind anyway?)");

            var jsTypeName = type.JSName();
            var cppTypeName = type.FullyQualifiedCppName();
            var cppFnTypeName = type.CppFnPrefix();

            // C# interface with [SharedPtr]
            bool isSharedPtr = type.IsSharedPtrType();
            bool isNonSharedPtr = type.IsNonSharedPtrType();
            // C# interface with [Service]
            bool isService = type.IsServiceRefType();
            // C# struct
            bool isStruct = type.IsStructValueType();

            //
            // Constructors and preamble
            //
            // At some point in the future we may support multiple constructors and constructors with arguments,
            // and some way to specify them.
            // For now, we have a Constructable attribute that enables a single, no-argument constructor.
            bool isConstructable = type.HasAttribute("Constructable");
            if (isConstructable)
            {
                dts($"function _{type.CppFnPrefix()}_{type.Name}(): number;");

                exportSymbol($"{type.CppFnPrefix()}_{type.Name}");
                cpp($"UT_JSFN {cppTypeName}* {type.CppFnPrefix()}_{type.Name}() {{");
                if (isSharedPtr)
                    cpp($"  return ut::PtrTable::persist<{cppTypeName}>(std::make_shared<{cppTypeName}>());");
                else
                    cpp($"  return new {cppTypeName}();");
                cpp($"}}");
            }

            if (isSharedPtr || isNonSharedPtr)
            {
                string releaseTemplate;
                exportSymbol($"{cppFnTypeName}_shRelease");
                if (isSharedPtr)
                {
                    releaseTemplate =
@"
UT_JSFN void {{{cppFnTypeName}}}_shRelease({{{cppTypeName}}}* ptr) {
  ut::PtrTable::release<{{{cppTypeName}}}>(ptr);
}
";
                } else {
                    releaseTemplate =
                        @"UT_JSFN void {{{cppFnTypeName}}}_shRelease({{{cppTypeName}}}* ptr) {
  delete ptr;
}";
                }

                dts($"function _{type.CppFnPrefix()}_shRelease(self: number): void;");

                var releaseFnString = CppBindingsGenerator.ExpandStringTemplate(releaseTemplate,
                    "cppTypeName", cppTypeName,
                    "cppFnTypeName", cppFnTypeName);
                cpp(releaseFnString);
            }

            HandleTypeMethods(type);
        }

        void HandleTypeMethods(TypeDefinition type)
        {
            var cppTypeName = type.FullyQualifiedCppName();
            var cppFnTypeName = type.CppFnPrefix();

            // C# class with [SharedPtr]
            bool isSharedPtr = type.IsSharedPtrType();
            // C# interface with [Service]
            bool isService = type.IsServiceRefType();
            // C# class without [SharedPtr] or [Component]
            bool isStruct = type.IsStructValueType();
            // C# class with [Component]
            bool isComponent = type.IsComponentType();

            foreach (var fn in type.MemberFunctions()) {
                if (fn.HasAttribute("CppCustomImpl"))
                    continue;
                var numParams = fn.Parameters.Count;
                var cppName = fn.CppName();
                var cppWrapperName = fn.Name;
                var returnType = fn.ReturnType.Resolve();
                var isStatic = fn.IsStatic;

                var selfArgType = type.EmCppSelfFnArgType(fn);
                var comma = (fn.IsStatic || type.IsServiceRefType()) ? "" : ",";

                var argNames2 = new StringBuilder();
                //bool hasDelegate = false;
                bool stompIntoRetptr = false;
                var argList = new List<string>();
                var dtsArgs = new List<string>();

                var wrapperReturnType = type.EmCppReturnType(fn);

                // When returning something with EmCppReturnToStackPtr() == true,
                // the return value pointer is passed as the first parameter, so that we can just
                // do *retptr = funcCall() and not have to guess what the ABI is for struct returns.

                if (returnType.EmCppReturnToFirstArgPtr())
                {
                    argList.Add($"{returnType.EmCppArgTypeForRetPtr()} retptr");
                    stompIntoRetptr = true;
                    wrapperReturnType = "void";
                }

                if (selfArgType.Length > 0) {
                    argList.Add(selfArgType);
                    dtsArgs.Add("selfPtr");
                }

                for (int p = 0; p < numParams; ++p) {
                    var ptype = fn.Parameters[p].ParameterType;
                    var token = "";
                    dtsArgs.Add(fn.Parameters[p].Name);

                    if (argNames2.Length != 0) argNames2.Append(", ");
                    if (ptype.Resolve().IsDelegate())
                    {
                        //hasDelegate = true;

                        // TODO: C#
#if false
                        argNames2.Append($"{ptype.CppFnPrefix()}_call, arg{p}_token");
#else
                        argNames2.Append($"arg{p}");
#endif
                        token = "_token";
                    }
                    else
                    {
                        argNames2.Append($"arg{p}");
                    }

                    if (ptype.IsSharedPtrType() || ptype.IsNonSharedPtrType()) {
                        argList.Add($"{ptype.FullyQualifiedCppName()}* arg{p}{token}");
                    } else if (ptype.IsStructValueType() || ptype.IsComponentType())
                        argList.Add($"const {ptype.EmCppArgType()}& arg{p}{token}");
                    else
                        argList.Add($"{ptype.EmCppArgType()} arg{p}{token}");
                }

                exportSymbol($"{cppFnTypeName}_{cppWrapperName}");
                cpp($"UT_JSFN {wrapperReturnType} {cppFnTypeName}_{cppWrapperName}(");
                cpp(string.Join("\n,", argList));
                cpp($") {{");

                // Perform some conversion before calling the actual native method:
                // For delegate params, we need to convert the incoming token into a std::function using std::bind.
                for (int p = 0; p < numParams; ++p) {
                    var ptype = fn.Parameters[p].ParameterType.Resolve();
                    if (ptype == null || !ptype.IsDelegate())
                        continue;
                    var convert = new StringBuilder();
                    convert.Append($"  {ptype.CppFnPrefix()}_callType arg{p} = std::bind({ptype.CppFnPrefix()}_call, arg{p}_token");
                    for (int n = 0; n < ptype.DelegateInvokeMethod().Parameters.Count; ++n)
                        convert.Append($", std::placeholders::_{n + 1}");
                    convert.Append(");");

                    cpp(convert.ToString());
                }

                var cppSelf = type.EmCppSelfCall(fn);

// TODO: C#
//                if (hasDelegate)
//                {
//                   cppName = cppName + "TokenCall";
//                }

                var call = $"{cppSelf}{cppName}({argNames2})";
                var fnReturnType = fn.ReturnType.Resolve();
                var noReturnValue = fnReturnType.MetadataType == MetadataType.Void && !fn.ReturnType.IsPointer;
                string dtsReturnType;
                if (noReturnValue)
                {
                    cpp($"  {call};");
                    dtsReturnType = "void";
                }
                else if (stompIntoRetptr)
                {
                    cpp($"  *retptr = {call};");
                    dtsReturnType = "void";
                }
/* // TODO: C#
                else if (returnType.MetadataType == MetadataType.String)
                {
                    cpp($"  return ut::FromStringHelper({call});");
                }
*/
                else
                {
                    var cast = "";
                    if (fnReturnType.FixedSpecialType()?.MetadataType == MetadataType.IntPtr)
                        cast = "(intptr_t)";
                    else if (fnReturnType.MetadataType == MetadataType.Void && fn.ReturnType.IsPointer)
                        cast = "(void*)"; // special to cast away const-ness from c++ API

                    call = $"{cast}{cppSelf}{cppName}({argNames2})";
                    if (fnReturnType.IsSharedPtrType())
                        cpp($"  return ut::PtrTable::persist<{fnReturnType.FullyQualifiedCppName()}>({call});");
                    else
                        cpp($"  return {call};");
                    dtsReturnType = "any";
                }

                dtsArgs = dtsArgs.Select(s => $"{s}: any").ToList();
                dts($"function _{cppFnTypeName}_{cppWrapperName}({string.Join(", ", dtsArgs)}): {dtsReturnType};");
                cpp($"}}");
            }
        }

        public void HandleEnumType(TypeDefinition type)
        {
        }

        public void HandleCallbackType(TypeDefinition type)
        {
            var cppFnTypeName = type.FullyQualifiedCppName("_");

            if (BindGem.PureJS)
            {
                BindGem.FatalError(
                    "Callback (delegates) cannot be used with pure JS generation (what are you trying to bind anyway?)");
            }

            var fn = type.DelegateInvokeMethod();
            var fnParams = fn.Parameters.Select(p => p.ParameterType.Resolve()).ToList();
            var numParams = fn.Parameters.Count;

            BindGem.FatalErrorIf(fn.ReturnType.Resolve().MetadataType != MetadataType.Void, /*type,*/ $"Delegate (callback) {type.Name} returning non-void not supported (yet, fixable!)");

            // for delegates, we have to convert each parameter to its JS representation, similar to what we would do for return types
            // We only handle very simple types here

            exportSymbol($"{type.CppFnPrefix()}_call");
            cpp($"using {type.CppFnPrefix()}_callType = std::function<void({string.Join(",", fnParams.Select(p=>p.EmCppArgType()).ToArray())})>;");
            cpp($"UT_JSFN void {type.CppFnPrefix()}_call(");
            cpp($"  uint32_t self");
            for (int p = 0; p < numParams; ++p)
            {
                cpp($" ,{fnParams[p].EmCppArgType()} arg{p + 1}");
            }
            cpp($") {{");
            cpp($"#ifdef __EMSCRIPTEN__");
            cpp($"  EM_ASM({{");

            var passParams = new StringBuilder();
            var argParams = new StringBuilder();
            for (int p = 0; p < numParams; ++p)
            {
                string passParam;
                cpp($"    {fnParams[p].JSCallbackArgToEmscripten(p + 1, out passParam)}");
                passParams.Append($", {passParam}");
                argParams.Append($"${p + 1}");
                if (p != numParams - 1) argParams.Append(", ");
            }
            cpp($"    {type.JSName()}._cb.map[$0]({argParams});");
            cpp($"  }}, self{passParams});");
            cpp($"#else");
            cpp($" // TODO: Invoke callback for C#");
            cpp($"#endif");
            cpp($"}}");
        }

        public void HandleStructType(TypeDefinition type)
        {
            if (type.JavaScriptSpecialType() == JSSpecialType.None && !BindGem.PureJS)
            {
                HandleTypeMethods(type);
            }
        }

        public void HandleComponentType(TypeDefinition type)
        {
            if (!BindGem.PureJS)
            {
                exportSymbol($"{type.CppFnPrefix()}_cid");
                cpp($"UT_JSFN ComponentTypeId {type.CppFnPrefix()}_cid() {{ return ut::InitComponentId<{type.FullyQualifiedCppName()}>(); }}");

                // If the component type is generated in C++ code, export the destructor of that type for JavaScript to access.
                // (JS code needs to access component destructors e.g. after having called GetComponentData() to dispose the component it received)
                if (type.IsComplex())
                {
                    cpp($"UT_JSFN void {type.CppFnPrefix()}_dtor({type.FullyQualifiedCppName()}* ptr) {{ ptr->~{type.Name}(); }}");
                }

                HandleTypeMethods(type);
            }
        }

        public void HandleSystemType(TypeDefinition type)
        {
        }
    }
}
