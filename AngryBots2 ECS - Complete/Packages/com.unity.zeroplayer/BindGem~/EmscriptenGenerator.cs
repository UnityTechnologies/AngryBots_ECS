using ExtensionMethods;
using Mono.Cecil;
using System;
using System.CodeDom;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UTiny;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    //
    // Emscripten ABI notes
    //
    //  struct-return is handled by a generated first parameter to a function, pointing
    //  to a callee-allocated struct.  EXCEPT if the struct contains one and only member,
    //  and the only real data member (e.g. in a nested struct) fits in a 32-bit integer
    //  or a float/double, then it is returned directly.
    //
    //  structs are passed packed into a value if and only if the single descendant member
    //  fits into a 32-bit integer.  Everything else, including floats and doubles, is
    //  passed as a pointer (on the stack/temp heap).
    //

    internal class EmscriptenGenerator : ILanguageHandler
    {
        const string UTRT = "Module";

        private StringBuilder jsStr = new StringBuilder();
        private int jsStrMark = 0, jsTempHeapMark = -1;
        private StringBuilder cppStr = new StringBuilder();
        private bool metaTypesRegistered = false;

        public string GetGeneratedSource(SourceLanguage lang) {
            if (lang == SourceLanguage.JavaScript)
            {
                var jsSrc = new StringBuilder();
                MakeJsForNamespace(NamespaceRoot, jsSrc);
                jsSrc.Append(jsStr);
                return jsSrc.ToString();
            }
            if (lang == SourceLanguage.CPlusPlus)
                return cppStr.ToString();
            return null;
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            return 0;
        }

        private void js(string s) {
            jsStr.AppendLine(s);
        }

        private void jsMark() {
            jsStrMark = jsStr.Length;
        }

        private void jsAtMark(string s) {
            jsStr.Insert(jsStrMark, s + "\n");
        }

        private void jsMarkTempHeap() {
            jsTempHeapMark = jsStr.Length;
        }

        private void jsSetNeedsTempHeap(bool needsTempHeap) {
            if (needsTempHeap && jsTempHeapMark != -1) {
                jsStr.Insert(jsTempHeapMark, "  ut.prepareTempHeap();\n");
                jsTempHeapMark = -1;
            }
        }

        public EmscriptenGenerator()
        {
            if (!BindGem.PureJS) {
                cppStr.AppendLine(
@"#ifdef __EMSCRIPTEN__
#include <emscripten/emscripten.h>
#endif
#include <functional>
#include ""PtrTable.h""
");
            }

            // Make sure we have C++ meta types registered before first access to them.
            if (BindGem.PureJS && BindGem.Development)
            {
                RegisterMetaTypes();
            }
        }

        public void RegisterMetaTypes()
        {
            // Pull in meta types from compiled code on first occassion if/when we see any types registered that need them
            if (metaTypesRegistered) return;
            metaTypesRegistered = true;

            string registerTypes = "// In order to process bindings, we first need type registry from compiled code to be available.\n";
            registerTypes += "ut.meta.registerTypes();\n";
            registerTypes += jsStr.ToString();

            jsStr = new StringBuilder();
            jsStr.Append(registerTypes);
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
            if (type.HasAttribute("JSHide") || type.HasAttribute("PureJSService"))
                return;

            BindGem.FatalErrorIf(BindGem.PureJS,
                "Interfaces cannot be used with pure JS generation (what are you trying to bind anyway?)");

            GenerateNamespace(type.Namespace);

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
                js($"{jsTypeName} = function() {{");
                js($"  this.ptr = {UTRT}._{type.EmCppCtorName()}();");
                js($"}};");
            }
            else
            {
                string noConstructingAllowed =
@"{{{jsTypeName}}} = function() {
  throw new Error('Constructing {{{jsTypeName}}} is not allowed');
};";
                js(CppBindingsGenerator.ExpandStringTemplate(noConstructingAllowed, "jsTypeName", jsTypeName));
            }

            string prototype =
@"{{{jsTypeName}}}.prototype = Object.create(null);
{{{jsTypeName}}}.prototype.constructor = {{{jsTypeName}}};";

            js(CppBindingsGenerator.ExpandStringTemplate(prototype, "jsTypeName", jsTypeName));

            var wrapName = type.FullyQualifiedCppName(".");
            if (isSharedPtr || isNonSharedPtr)
            {
                // for _wrap, the object that we create IGNORES JSName -- JSName/TSName gets used
                // to define a base object that will later be extended.  So we generate the real/final name
                // here.  This will likely bite someone at some point.
                string sharedPtrJs =
@"{{{jsTypeName}}}._wrap = function(ptr) {
  var obj = Object.create({{{wrapName}}}.prototype);
  obj.ptr = ptr;
  return obj;
};

// define a release function
{{{jsTypeName}}}.prototype.release = function {{{type.Name}}}_release() {
  {{{Module}}}._{{{cppFnTypeName}}}_shRelease(this.ptr);
  this.ptr = 0;
};";

                sharedPtrJs = CppBindingsGenerator.ExpandStringTemplate(sharedPtrJs,
                    "jsTypeName", jsTypeName,
                    "Module", UTRT,
                    "cppFnTypeName", cppFnTypeName,
                    "jsTypeName", jsTypeName,
                    "type.Name", type.Name,
                    "wrapName", wrapName);
                js(sharedPtrJs);
            }

            HandleTypeMethods(type);
        }

        void HandleTypeMethods(TypeDefinition type)
        {
            var jsTypeName = type.JSName();
            var cppTypeName = type.FullyQualifiedCppName();
            var cppFnTypeName = type.CppFnPrefix();

            // C# class with [SharedPtr]
            bool isSharedPtr = type.IsSharedPtrType();
            bool isNonSharedPtr = type.IsNonSharedPtrType();
            // C# interface with [Service]
            bool isService = type.IsServiceRefType();
            // C# class without [SharedPtr] or [Component]
            bool isStruct = type.IsStructValueType();

            //
            // Member methods
            //
            foreach (var fn in type.MemberFunctions("JSHide")) {
                // For static methods and for service types, put the methods directly on the type, not on
                // the prototype
                var maybeProto = ".prototype";
                if (fn.IsStatic || isService)
                    maybeProto = "";

                var argNames = new StringBuilder();

                js(
                    $"{jsTypeName}{maybeProto}.{fn.JSName()} = function({MakeNArgNames(fn.Parameters.Count)}) {{");
                bool needTempHeap = false;
                jsMarkTempHeap();

                // generate code to convert params to C++ argument forms
                var fnParams = fn.Parameters.ToArray();
                for (int p = 0; p < fnParams.Length; ++p)
                {
                    js($"  {fnParams[p].JSConvertParamToEmscripten("arg" + p, ref needTempHeap)};");

                    argNames.Append($"arg{p}");
                    if (p != fnParams.Length - 1)
                        argNames.Append(",");
                }

                string self, selfPostamble;
                string selfPreamble = type.JSConvertSelfToEmscripten(fn, out self, out selfPostamble, ref needTempHeap);
                // if there's a "self", prepend it to the arg names
                if (self.Length > 0)
                {
                    if (argNames.Length > 0) argNames.Insert(0, ", ");
                    argNames.Insert(0, self);
                }

                js($"  {selfPreamble}");

                var returnType = fn.ReturnType.Resolve();
                if (returnType.MetadataType == MetadataType.Void)
                {
                    js($"  {UTRT}._{type.EmCppFunctionName(fn)}({argNames});");
                    js($"  {selfPostamble}");
                }
                else if (returnType.EmCppReturnToFirstArgPtr())
                {
                    needTempHeap = true;
                    // Prepend "retptr" to argnames to hold the temporary return allocation
                    if (argNames.Length > 0) argNames.Insert(0, ", ");
                    argNames.Insert(0, "retptr");

                    js($"  {returnType.JSMakeRetPtr()}");
                    js($"  {UTRT}._{type.EmCppFunctionName(fn)}({argNames});");
                    js($"  {selfPostamble}");
                    js($"  {returnType.JSMakeReturnFromRetPtr()}");
                }
                else
                {
                    js($"  var ret = {UTRT}._{type.EmCppFunctionName(fn)}({argNames});");
                    js($"  {selfPostamble}");
                    js($"  {returnType.JSMakeReturnFromEmscripten("ret", ref needTempHeap)}");
                }

                jsSetNeedsTempHeap(needTempHeap);
                js($"}};");
            }

            foreach (var fn in type.MemberFunctions()) {
                var numParams = fn.Parameters.Count;
                var cppName = fn.CppName();
                var cppWrapperName = fn.Name;

                var selfArgType = type.EmCppSelfFnArgType(fn);
                var comma = (fn.IsStatic || type.IsServiceRefType()) ? "" : ",";

                var argNames2 = new StringBuilder();

                for (int p = 0; p < numParams; ++p) {
                    var ptypeRef = fn.Parameters[p].ParameterType;
                    var ptype = ptypeRef.Resolve();

                    if (argNames2.Length != 0) argNames2.Append(", ");
                    if (ptype.IsSharedPtrType())
                        argNames2.Append($"ut::PtrTable::getSharedPtr<{ptype.FullyQualifiedCppName()}>(arg{p})");
                    else
                        argNames2.Append($"arg{p}");
                    comma = ",";
                }

                // Perform some conversion before calling the actual native method:
                // For delegate params, we need to convert the incoming token into a std::function using std::bind.
                for (int p = 0; p < numParams; ++p) {
                    var ptype = fn.Parameters[p].ParameterType.Resolve();
                    if (ptype == null || !ptype.IsDelegate())
                        continue;
                    var convert = new StringBuilder();
                    convert.Append($"  auto arg{p} = std::bind({ptype.CppFnPrefix()}_call, arg{p}_token");
                    for (int n = 0; n < ptype.DelegateInvokeMethod().Parameters.Count; ++n)
                        convert.Append($", std::placeholders::_{n + 1}");
                    convert.Append(");");
                }
            }
        }

        public void HandleEnumType(TypeDefinition type)
        {
            if (type.HasAttribute("JSHide"))
                return;

            GenerateNamespace(type.CppNamespace());

            string enumTemplate =
@"{{{var}}}{{{typeName}}} = {
  {{{name=value}}}
};";
            var jsTypeName = type.JSName();
            var enumList = type.GetEnumKeyValues();
            var enumValues = enumList.Select(e => $"{e.key}: {e.value}");
            var code = CppBindingsGenerator.ExpandStringTemplate(enumTemplate,
                "var", type.IsInGlobalNamespace() ? "var " : "",
                "typeName", jsTypeName,
                "name=value", string.Join(",\n", enumValues));
            js(code);

            // Generate enum TypeDesc in pure JS development builds
            if (BindGem.PureJS && BindGem.Development)
            {
                js($"{jsTypeName}._typeDesc = (function() {{");
                var typeTrait = (int)BindGem.TypeDescTrait.Enum;
                var typeName = $"'{jsTypeName}'";
                var typeSize = sizeof(int);
                var members = "";
                foreach (var e in enumList)
                {
                    var memberName = $"'{e.key}'";
                    var memberOffset = e.value;
                    var memberType = "enumType";
                    var memberNext = !e.Equals(enumList.Last()) ? "," : "\n  ";
                    members += $"\n    {{name: {memberName}, offset: {memberOffset}, type: {memberType}}}{memberNext}";
                }
                if (enumList.Count > 0)
                {
                    js($"  var enumType = ut.meta.getType('int32');");
                }
                js($"  return ut.meta.allocType({typeTrait}, {typeName}, {typeSize}, [{members}]);");
                js($"}})();");
            }
        }

        public void HandleCallbackType(TypeDefinition type)
        {
            if (type.HasAttribute("JSHide"))
                return;

            GenerateNamespace(type.CppNamespace());

            var jsTypeName = type.FullyQualifiedCppName(".");
            var jsTypeDeclaration = type.IsInGlobalNamespace() ? "var " : "";
            var cppFnTypeName = type.FullyQualifiedCppName("_");

            if (BindGem.PureJS)
            {
                BindGem.FatalError(
                    "Callback (delegates) cannot be used with pure JS generation (what are you trying to bind anyway?)");
            }

            js($"{jsTypeDeclaration}{jsTypeName} = {{}};");
            js($"{jsTypeName}._cb = {{");
            js($"  next_token: 1,");
            js($"  map: {{}},");
            js($"  token_for: function _{cppFnTypeName}_tokenFn(fn) {{");
            js($"    if (typeof(fn) != 'function') throw new Error('Not a function');");
            js($"    var token = fn.token;");
            js($"    if (!token) {{");
            js($"        token = {jsTypeName}._cb.next_token++;");
            js($"        {jsTypeName}._cb.map[token] = fn;");
            js($"        fn.token = token;");
            js($"    }}");
            js($"    return token;");
            js($"  }}");
            js($"}};");

            var fn = type.DelegateInvokeMethod();
            var numParams = fn.Parameters.Count;
            var fnParams = fn.Parameters.Select(p => p.ParameterType.Resolve()).ToList();

            BindGem.FatalErrorIf(fn.ReturnType.Resolve().MetadataType != MetadataType.Void, /*type,*/ $"Delegate (callback) {type.Name} returning non-void not supported (yet, fixable!)");
        }

        public void HandleStructType(TypeDefinition type)
        {
            if (type.HasAttribute("JSHide"))
                return;

            GenerateNamespace(type.Namespace);

            var jsTypeName = type.JSName();
            var jsTypeDeclaration = type.IsInGlobalNamespace() ? "var " : "";

            var sizeInfo = TypeUtils.AlignAndSizeOfType(type, 32);

            var proto = $"{jsTypeName}.prototype";

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
            {
                js($"// Not emitting JS code for JSSpecialType {type}");
                return;
            }

            // For structs/components, we create a default constructor that initializes fields
            // from arguments.
            js($"{jsTypeDeclaration}{jsTypeName} = function({MakeNArgNames(type.Fields.Count())}) {{");
            int index = 0;
            foreach (var field in type.Fields)
            {
                js($"  {field.FieldType.Resolve().JSCoerce("arg" + index, $"this._{field.Name}", true)};");
                index++;
            }
            js($"}};");
            js($"{proto} = Object.create(null);");
            js($"{proto}.constructor = {jsTypeName};");

            // For each field, define a setter and getter from the internal storage.
            // @TODO - this may or may not be the best thing for performance.  Might need to go back to regular fields.
            js($"Object.defineProperties({proto}, {{");
            foreach (var field in type.Fields)
            {
                js($"  {field.Name}: {{");
                js($"    get: function() {{ return this._{field.Name}; }},");
                if (!field.IsInitOnly)
                {
                    js($"    set: function(v) {{ {field.FieldType.Resolve().JSCoerce("v", $"this._{field.Name}")}; }},");
                }
                js($"  }},");
            }
            js($"}});");

            js($"{jsTypeName}._size = {TypeUtils.AlignAndSizeOfType(type, 32).size};");

            js($"{jsTypeName}._fromPtr = function(ptr, v) {{");
            js($"  v = v || Object.create({jsTypeName}.prototype);");
            foreach (var field in type.Fields) {
                var offs = TypeUtils.AlignAndSizeOfField(field, 32);
                js($"  v._{field.Name} = {field.FieldType.JSHeapGet($"ptr+{offs.offset}")};");
            }
            js($"  return v;");
            js($"}};");

            // If we have a complex (non-POD) type that is serialized to the temp heap ("scratch memory area") that
            // is intended to not require freeing, we need to use different functions to perform the serialization,
            // so that a component that contains an array of strings will cause the array and the string memory
            // to also be serialized to the scratch memory area. (otherwise only the component memory would live
            // in scratch memory area, but the array or the string data would not, and they would leak)
            // To accommodate both permanent/persistent and scratch memory serialization to temp heap, generate
            // two serializers _toPtr() and _toTempHeapPtr() that each know whether the data that is being serialized
            // is to be permanent or temporary.
            for (int toScratchMemory = 0; toScratchMemory <= 1; ++toScratchMemory)
            {
                string funcName = (toScratchMemory == 0 ? "_toPtr" : "_toTempHeapPtr");
                js($"{jsTypeName}.{funcName} = function(ptr, v) {{");
                foreach (var field in type.Fields)
                {
                    // Note that we write InitOnly fields as well -- even
                    // though the user can't write them, they need to be
                    // correct when we call functions

                    var offs = TypeUtils.AlignAndSizeOfField(field, 32);
                    // NB! This can't use the internal _ fields, it has to use the getter field to
                    // ensure that it works identically with actual object-structs and ref-structs (where
                    // there's an internal _ptr, during forEach())
                    js($"  {field.FieldType.JSHeapSet($"v.{field.Name}", $"ptr+{offs.offset}", toScratchMemory != 0)};");
                }
                js($"}};");
            }
            js($"{jsTypeName}._tempHeapPtr = function(v) {{");
            js($"  var ptr = ut.tempHeapPtrBufferZero({sizeInfo.size});");
            js($"  if (v) {jsTypeName}._toTempHeapPtr(ptr, v);");
            js($"  return ptr;");
            js($"}};");

            // Generate struct TypeDesc in pure JS development builds
            if (BindGem.PureJS && BindGem.Development && !type.IsComponentType())
            {
                js($"{jsTypeName}._typeDesc = (function() {{");
                var typeTrait = (int)BindGem.TypeDescTrait.Struct;
                var typeName = $"'{jsTypeName}'";
                var typeSize = TypeUtils.AlignAndSizeOfType(type, 32).size;
                var members = "";
                foreach (var field in type.Fields)
                {
                    var memberName = $"'{field.Name}'";
                    var memberOffset = TypeUtils.AlignAndSizeOfField(field, 32).offset;
                    var memberType = $"ut.meta.getType('{field.FieldType.TypeDescName()}')";
                    var memberNext = !field.Equals(type.Fields.Last()) ? "," : "\n  ";
                    members += $"\n    {{name: {memberName}, offset: {memberOffset}, type: {memberType}}}{memberNext}";
                }
                js($"  return ut.meta.allocType({typeTrait}, {typeName}, {typeSize}, [{members}]);");
                js($"}})();");
            }

            if (!BindGem.PureJS)
            {
                HandleTypeMethods(type);
            }
        }

        public void OutputMetaDataRecursive(FieldDefinition field, string jsTypeName, string prefix="", int baseoffset=0)
        {
            // one single field
            var offs = TypeUtils.AlignAndSizeOfField(field, 32).offset + baseoffset;
            js($"{jsTypeName}{prefix}.{field.Name} = {{ $ofs:{offs}, $t:\"{field.FieldType.JSMetaName()}\", $c:{jsTypeName} }};");
            switch (field.FieldType.JavaScriptSpecialType())
            {
                // special offsets in vectors, so we can tween individual parts as floats
                case JSSpecialType.Quaternion:
                case JSSpecialType.Vector4:
                    js($"{jsTypeName}{prefix}.{field.Name}.w = {{ $ofs:{offs + 12}, $t:\"float\", $c:{jsTypeName} }};");
                    goto case JSSpecialType.Vector3;
                case JSSpecialType.Vector3:
                    js($"{jsTypeName}{prefix}.{field.Name}.z = {{ $ofs:{offs + 8}, $t:\"float\", $c:{jsTypeName} }};");
                    goto case JSSpecialType.Vector2;
                case JSSpecialType.Vector2:
                    js($"{jsTypeName}{prefix}.{field.Name}.y = {{ $ofs:{offs + 4}, $t:\"float\", $c:{jsTypeName} }};");
                    js($"{jsTypeName}{prefix}.{field.Name}.x = {{ $ofs:{offs + 0}, $t:\"float\", $c:{jsTypeName} }};");
                    break;
                default:
                    // recurse structures
                    if (field.FieldType.IsStructValueType() && !field.FieldType.IsDynamicArray())
                    {
                        foreach (var f2 in field.FieldType.Resolve().Fields)
                        {
                            OutputMetaDataRecursive(f2, jsTypeName, prefix + "." + field.Name, offs );
                        }
                    }
                    break;
            }
        }

        // User defined components created in Editor need destructors to be generated for them in JavaScript, since the C++ runtime
        // does not know about these types.
        public void GenerateComponentDestructor(string jsTypeName, TypeDefinition componentType)
        {
            if (!componentType.IsComplex())
            {
                js($"{jsTypeName}._dtorFn = function dtor(ptr) {{ /* POD, no-op */ }}");
                return; // POD types do not need destructors called on them
            }

            js($"{jsTypeName}._dtorFn = function dtor(ptr) {{");
            js("  if (!ptr) return; "); // free(0) is a no-op semantics

            if (BindGem.PureJS)
            {
                // This type is defined on editor side and does not exist in C++ code: generate a JavaScript side destructor function for it
                foreach (var f in componentType.Fields)
                {
                    var field = TypeUtils.AlignAndSizeOfField(f, 32/*JS is always 32-bit*/);
                    if (f.FieldType.MetadataType == MetadataType.String)
                    {
                        js($"  {UTRT}._ut_nativestring_placement_delete(ptr + {field.offset});");
                    }
                    else if (f.FieldType.IsDynamicArray())
                    {
                        if (f.FieldType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            js($"  {UTRT}._ut_nativebuffer_nativestring_placement_delete(ptr + {field.offset});");
                        else
                            js($"  {UTRT}._ut_nativebuffer_pod_placement_delete(ptr + {field.offset});");
                    }
                }
            }
            else
            {
                // This type is defined in C++ side, so there exists a destructor function for the type in C++ code, call that.
                js($"  {UTRT}._{componentType.CppFnPrefix()}_dtor(ptr);");
            }
            js($"}};");
        }

        public void GenerateComponentCopyConstructor(string jsTypeName, TypeDefinition componentType)
        {
            if (!componentType.IsComplex())
            {
                js($"// {jsTypeName} is a POD type, so a JavaScript side copy constructor {jsTypeName}._copyFn = function copy(src, dst) {{ ... }} does not need to be generated for it");
                return;
            }

            js($"{jsTypeName}._copyFn = function copy(src, dst) {{");
            if (BindGem.Development)
            {
                js("  if (!src) throw 'copy function src ptr is null!';");
                js("  if (!dst) throw 'copy function dst ptr is null!';");
            }

            if (BindGem.PureJS)
            {
                // This type is defined on editor side and does not exist in C++ code: generate a JavaScript side destructor function for it
                foreach (var f in componentType.Fields)
                {
                    var field = TypeUtils.AlignAndSizeOfField(f, 32/*JS is always 32-bit*/);
                    if (f.FieldType.MetadataType == MetadataType.String)
                    {
                        js($"  {UTRT}._ut_nativestring_copy_construct(dst + {field.offset}, src + {field.offset});");
                    }
                    else if (f.FieldType.IsDynamicArray())
                    {
                        if (f.FieldType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            js($"  {UTRT}._ut_nativebuffer_nativestring_copy_construct(dst + {field.offset}, src + {field.offset});");
                        else
                        {
                            var arrayElementSize = TypeUtils.AlignAndSizeOfType(f.FieldType.DynamicArrayElementType(), 32);
                            js($"  {UTRT}._ut_nativebuffer_pod_copy_construct(dst + {field.offset}, src + {field.offset}, {arrayElementSize.size});");
                        }
                    }
                    else
                    {
                        js($"  for(var i = 0; i < {field.size}; ++i) HEAPU8[dst+{field.offset}+i] = HEAPU8[src+{field.offset}+i];");
                    }
                }
            }
            js($"}};");
        }

        public void HandleComponentType(TypeDefinition type)
        {
            if (type.HasAttribute("JSHide"))
                return;

            // first generate it as a struct
            HandleStructType(type);

            var jsTypeName = type.JSName();
            var cppTypeName = type.FullyQualifiedCppName();
            var cppFnTypeName = type.CppFnPrefix();
            var storageViewName = $"{jsTypeName}.StorageView";

            var proto = $"{jsTypeName}.prototype";
            var storageProto = $"{storageViewName}.prototype";

            // For components, we have a struct that is a helper over a raw view on the storage
            // in the Emscripten heap.
            js($"{storageViewName} = function(ptr) {{");
            js($"  this._ptr = ptr;");
            js($"}};");
            js($"{storageProto} = Object.create(null);");
            js($"{storageProto}.constructor = {storageViewName};");
            js($"{jsTypeName}._view = {storageViewName};");
            js($"{storageViewName}._isSharedComp = {jsTypeName}._isSharedComp = {((type.AllInterfaces().FirstOrDefault(f => f.Name == "ISharedComponentData") == null) ? "false" : "true")};");

            // duplicate these so that views behave similarly to the real version
            js($"{storageViewName}._fromPtr = {jsTypeName}._fromPtr;");
            js($"{storageViewName}._toPtr = {jsTypeName}._toPtr;");
            js($"{storageViewName}._tempHeapPtr = {jsTypeName}._tempHeapPtr;");
            js($"{storageViewName}._size = {jsTypeName}._size;");

            // the advance method increments the pointer by the size of this type;
            // so that a StorageView can be used to walk a pointer to an array of instances of this type
            js($"{storageProto}.$advance = function() {{");
            js($"  this._ptr += {TypeUtils.AlignAndSizeOfType(type, 32).size};");
            js($"}};");

            // Create a getter/setter for each field that reads/writes the heap directly
            js($"Object.defineProperties({storageProto}, {{");
            foreach (var field in type.Fields)
            {
                var offs = TypeUtils.AlignAndSizeOfField(field, 32);

                js($"  {field.Name}: {{");
                js($"    get: function() {{ return {field.FieldType.JSHeapGet($"this._ptr+{offs.offset}")}; }},");
                if (!field.IsInitOnly)
                {
                    js($"    set: function(v) {{ {field.FieldType.Resolve().JSCheckType("v")}{field.FieldType.JSHeapSet("v", $"this._ptr+{offs.offset}", false)}; }},");
                }
                js($"  }},");
            }
            js($"}});");

            // Alias type instance methods in from the non-proto view
            foreach (var fn in type.MemberFunctions("JSHide")) {
                if (fn.IsStatic)
                    continue;
                js($"{storageProto}.{fn.JSName()} = {proto}.{fn.JSName()};");
            }

            // Do we have any heap pointers in this component that need a destructor?
            GenerateComponentDestructor(jsTypeName, type);
            GenerateComponentCopyConstructor(jsTypeName, type);
            string dtorToken = "0";
            string copyToken = "0";

            if (type.IsComplex())
            {
                dtorToken = $"ut.DestructorFn._cb.token_for({jsTypeName}._dtorFn)";
                copyToken = $"ut.CopyFn._cb.token_for({jsTypeName}._copyFn)";
            }

            // Generate component TypeDesc in pure JS development builds
            if (BindGem.PureJS && BindGem.Development)
            {
                js($"{jsTypeName}._typeDesc = (function() {{");
                var typeTrait = (int)BindGem.TypeDescTrait.Component;
                var typeName = $"'{jsTypeName}'";
                var typeSize = TypeUtils.AlignAndSizeOfType(type, 32).size;
                var members = "";
                foreach (var field in type.Fields)
                {
                    var memberName = $"'{field.Name}'";
                    var memberOffset = TypeUtils.AlignAndSizeOfField(field, 32).offset;
                    var memberType = $"ut.meta.getType('{field.FieldType.TypeDescName()}')";
                    var memberNext = !field.Equals(type.Fields.Last()) ? "," : "\n  ";
                    members += $"\n    {{name: {memberName}, offset: {memberOffset}, type: {memberType}}}{memberNext}";
                }
                js($"  return ut.meta.allocType({typeTrait}, {typeName}, {typeSize}, [{members}]);");
                js($"}})();");
            }

            string cidGetExtra = "";
            string cidGetSrc;
            if (BindGem.PureJS)
            {
                var typeAlignment = TypeUtils.AlignAndSizeOfType(type, 32);
                var entityOffsets = TypeUtils.GetEntityFieldOffsets(type, 32);

                if (entityOffsets.Count > 0) {
                    cidGetExtra = $"var offsetsPtr = ut.tempHeapPtrI32([{String.Join(",", entityOffsets)}]); var offsetsCount = {entityOffsets.Count};";
                } else {
                    cidGetExtra = "var offsetsPtr = 0, offsetsCount = 0;";
                }

                var flags = 0;
                if (type.IsSharedComponentType())
                {
                    const int kIsSharedComponent = 1 << 1;
                    flags |= kIsSharedComponent;
                }
                if (type.IsSystemStateComponentType())
                {
                    const int kIsSystemStateComponent = 1 << 3;
                    flags |= kIsSystemStateComponent;
                }

                if (BindGem.Development)
                {
                    cidGetSrc = $"{UTRT}._ut_component_register_cid_with_type({jsTypeName}._typeDesc, {typeAlignment.align}, {flags}, offsetsPtr, offsetsCount, {dtorToken}, {copyToken})";
                }
                else
                {
                    cidGetSrc = $"{UTRT}._ut_component_register_cid(/*{jsTypeName}*/ {typeAlignment.size}, {typeAlignment.align}, {flags}, offsetsPtr, offsetsCount, 0/*\"{cppTypeName}\"*/, {dtorToken}, {copyToken})";
                }
            }
            else
            {
                cidGetSrc = $"{UTRT}._{cppFnTypeName}_cid()";
            }
            js($"Object.defineProperties({jsTypeName}, {{ cid: {{ configurable: true, get: function() {{ delete {jsTypeName}.cid; {cidGetExtra} return {jsTypeName}.cid = {cidGetSrc}; }} }} }});");
            js($"Object.defineProperties({storageViewName}, {{ cid: {{ configurable: true, get: function() {{ return {jsTypeName}.cid; }} }} }});");

            // for tweening (and maybe other things?)
            foreach (var field in type.Fields)
            {
                OutputMetaDataRecursive(field, jsTypeName, "");
            }
        }

        public void HandleSystemType(TypeDefinition type)
        {
            if (type.HasAttribute("JSHide"))
                return;

            GenerateNamespace(type.Namespace);

            if (!BindGem.PureJS)
            {
                // if it's not PureJS, we just need to generate a singleton property here
                js($"Object.defineProperties({type.FullyQualifiedNamespaceName(".")}, {{ {type.Name}: {{");
                js($"  configurable: true, enumerable: true, get: function() {{");
                js($"    delete this.{type.Name};");
                js($"    var sh = {UTRT}._{type.CppFnPrefix()}();");
                js($"    return this.{type.Name} = ut.System._wrap(sh);");
                js($"}}}}}});");

                return;
            }

            // If it is PureJS, then we create a call to System.define() which will create
            // a Fake System object for us.
            var beforeDeps = type.GetSystemRunsBefore();
            var afterDeps = type.GetSystemRunsAfter();

            js($"{type.FullyQualifiedCppName(".")} = ut.System.define({{");
            js($"  name: \"{type.FullSystemName()}\"");
            if (beforeDeps.Length > 0)
            {
                js($" ,updatesBefore: [{string.Join(",", beforeDeps.Select(c => "\"" + c.FullSystemName() + "\"").ToArray())}]");
            }

            if (afterDeps.Length > 0)
            {
                js($" ,updatesAfter: [{string.Join(",", afterDeps.Select(c => "\"" + c.FullSystemName() + "\"").ToArray())}]");
            }

            js($"}});");
        }

        internal class NamespaceAndChildren {
            internal NamespaceAndChildren(string nsName = null) {
                Name = nsName;
            }

            internal string Name;
            internal List<NamespaceAndChildren> Children = new List<NamespaceAndChildren>();
        }

        // Tracks a list of already generated namespaces in JavaScript output.
        List<string> GeneratedNamespaces = new List<string>();

        NamespaceAndChildren NamespaceRoot = new NamespaceAndChildren();

        // Recursively generates the namespace tree for the given type, if it hasn't yet been defined.
        void GenerateNamespace(string ns)
        {
            if (ns == null || ns.Trim().Length == 0) return;
            if (ns.StartsWith("UTiny.")) ns = "ut." + ns.Substring("UTiny.".Length);
            if (ns == "UTiny") ns = "ut";
            if (GeneratedNamespaces.Contains(ns)) return;
            if (ns.Length == 0) return;

            var components = ns.Split('.');

            // Define first parent namespaces if they don't yet exist.
            if (components.Length > 0)
                GenerateNamespace(String.Join(".", components.Take(components.Count() - 1)));

            if (components.Length == 1)
                js($"var {ns} = {ns} || {{}};");
            else
                js($"{ns} = {ns} || {{}};");
            GeneratedNamespaces.Add(ns);
        }

        void MakeJsForNamespace(NamespaceAndChildren nc, StringBuilder sb, string before = null)
        {
            string thisNs = null;
            if (nc.Name != null) {
                if (before == null) {
                    thisNs = BindGem.TranslateJsTopLevelNamespace(nc.Name);
                    sb.AppendLine($"var {thisNs} = {thisNs} || {{}};");
                } else {
                    thisNs = before + "." + nc.Name;
                    sb.AppendLine($"{thisNs} = {thisNs} || {{}};");
                }
            }

            foreach (var child in nc.Children)
                MakeJsForNamespace(child, sb, thisNs);
        }
    }

    internal static class EmscriptenExtensionMethods
    {
        const string UTRT = "Module";

        internal static bool JSIsSimpleType(this TypeDefinition type)
        {
            // TODO: Remove this function and call below on call site
            return type.IsCppBasicType() || type.MetadataType == MetadataType.IntPtr || type.IsComponentTypeId() || type.IsEntityIdType();
        }

        internal static string JSCoerceSimple(this TypeDefinition type, string src)
        {
            if (type.MetadataType == MetadataType.Boolean) return $"({src} ? true : false)";
            if (type.MetadataType == MetadataType.SByte || type.MetadataType == MetadataType.Byte
                || type.MetadataType == MetadataType.Int16 || type.MetadataType == MetadataType.UInt16 || type.MetadataType == MetadataType.Char
                || type.MetadataType == MetadataType.Int32 || type.MetadataType == MetadataType.UInt32 || type.IsComponentTypeId())
                return $"({src}|0)";
            if (type.MetadataType == MetadataType.Single) return $"(+({src}===undefined ? 0 : {src}))"; // TODO: Math.fround()?
            if (type.MetadataType == MetadataType.Double) return $"(+({src}===undefined ? 0 : {src}))";
            if (type.MetadataType == MetadataType.Int64 || type.MetadataType == MetadataType.UInt64) return $"(/*64BIT*/{src}|0)";
            if (type.MetadataType == MetadataType.IntPtr || type.MetadataType == MetadataType.UIntPtr || type.IsEntityIdType()) return $"(/*PTR*/{src}|0)";
            if (type.IsEnum) return $"({src}|0)";
            return null;
        }

        internal static string JSCoerceSpecial(this TypeDefinition type, string itemName, string dst, bool construct)
        {
            string assign = dst == null ? "" : (dst + " = ");
            // The math types are implemented using the threejs types imported into the ut namespace;
            // we need to make sure we know how to move stuff to/from the heap using those.
            if (dst == null)
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"(({itemName}) === undefined ? new ut.Math.Vector2() : ({itemName}).clone())";
                    case JSSpecialType.Vector3:
                        return $"(({itemName}) === undefined ? new ut.Math.Vector3() : ({itemName}).clone())";
                    case JSSpecialType.Vector4:
                        return $"(({itemName}) === undefined ? new ut.Math.Vector4() : ({itemName}).clone())";
                    case JSSpecialType.Quaternion:
                        return $"(({itemName}) === undefined ? new ut.Math.Quaternion() : ({itemName}).clone())";
                    case JSSpecialType.Matrix3:
                        return $"(({itemName}) === undefined ? new ut.Math.Matrix3x3() : ({itemName}).clone())";
                    case JSSpecialType.Matrix4:
                        return $"(({itemName}) === undefined ? new ut.Math.Matrix4x4() : ({itemName}).clone())";
                }
            }
            else if (construct)
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"{dst} = new ut.Math.Vector2(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                    case JSSpecialType.Vector3:
                        return $"{dst} = new ut.Math.Vector3(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                    case JSSpecialType.Vector4:
                        return $"{dst} = new ut.Math.Vector4(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                    case JSSpecialType.Quaternion:
                        return $"{dst} = new ut.Math.Quaternion(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                    case JSSpecialType.Matrix3:
                        return $"{dst} = new ut.Math.Matrix3x3(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                    case JSSpecialType.Matrix4:
                        return $"{dst} = new ut.Math.Matrix4x4(); if (({itemName}) !== undefined) {{ {dst}.copy({itemName}); }}";
                }
            }
            else
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"{dst}.copy({itemName})";
                    case JSSpecialType.Vector3:
                        return $"{dst}.copy({itemName})";
                    case JSSpecialType.Vector4:
                        return $"{dst}.copy({itemName})";
                    case JSSpecialType.Quaternion:
                        return $"{dst}.copy({itemName})";
                    case JSSpecialType.Matrix3:
                        return $"{dst}.copy({itemName})";
                    case JSSpecialType.Matrix4:
                        return $"{dst}.copy({itemName})";
                }
            }

            BindGem.FatalError($"Type {type} is not JSSpecial");//, type.Locations[0]);
            return null;
        }

        internal static string JSCoerce(this TypeDefinition type, string itemName, string dst = null, bool construct = false)
        {
            string assign = dst == null ? "" : (dst + " = ");
            string coerce = type.JSCoerceSimple(itemName);
            if (coerce != null)
                return $"{assign}{coerce}";

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
                return type.JSCoerceSpecial(itemName, dst, construct);

            if (type.IsDynamicArray())
                return $"{assign}({itemName} === undefined ? new Array() : (({itemName} instanceof Array) ? {itemName} : (function() {{ throw new Error('Assigning non-array to array field'); }})()))";
            if (type.IsStructValueType() || type.IsComponentType())
                return $"{assign}({itemName} === undefined ? new {type.JSName()} : {itemName})";
            if (type.MetadataType == MetadataType.String)
                return $"{assign}({itemName} === undefined ? '' : {itemName})";

            BindGem.FatalError($"Failed to JSCoerce type {type}");
            return null;
        }

        internal static string JSCheckType(this TypeDefinition type, string value)
        {
            if (type.IsStructValueType())
            {
                return $"if (typeof({value}) !== 'object') {{ throw new Error('expected an object'); }} ";
            }
            return "";
        }
        internal static string JSHeapSetSpecial(this TypeDefinition type, string value, string dst)
        {
            switch (type.JavaScriptSpecialType())
            {
                case JSSpecialType.Vector2:
                    return $"ut._utils.vec2ToHeap({value}, {dst})";
                case JSSpecialType.Vector3:
                    return $"ut._utils.vec3ToHeap({value}, {dst})";
                case JSSpecialType.Vector4:
                    return $"ut._utils.vec4ToHeap({value}, {dst})";
                case JSSpecialType.Quaternion:
                    return $"ut._utils.quatToHeap({value}, {dst})";
                case JSSpecialType.Matrix3:
                    return $"ut._utils.mat3ToHeap({value}, {dst})";
                case JSSpecialType.Matrix4:
                    return $"ut._utils.mat4ToHeap({value}, {dst})";
            }
            BindGem.FatalError($"Type {type} is not JSSpecial");//, type.Locations[0]);
            return null;
        }

        internal static string JSHeapSetArray(this TypeReference type, string value, string destExpr, bool toScratchMemory)
        {
            var elementType = type.DynamicArrayElementType();
            if (elementType.FixedSpecialType()?.MetadataType == MetadataType.String)
            {
                if (toScratchMemory)
                    return $"ut.jsArrayToExistingScratchNativeBuffer_string({value}, {destExpr}, function(p, v) {{ ut.toExistingScratchNativeString(p, v); }})";
                else
                    return $"ut.jsArrayToExistingNativeBuffer_string({value}, {destExpr}, function(p, v) {{ ut.writeHeapNativeString(p, v); }})";
            }
            else
            {
                var sz = TypeUtils.AlignAndSizeOfType(elementType, 32);
                var convFn = $"function(p, v) {{ {elementType.JSHeapSet("v", "p", toScratchMemory)}; }}";
                var serializeFunction = toScratchMemory ? "jsArrayToExistingScratchNativeBuffer_pod" : "jsArrayToExistingNativeBuffer_pod";
                return $"ut.{serializeFunction}({value}, {destExpr}, {sz.size}, {convFn})";
            }
        }

        internal static string JSHeapSet(this TypeReference type, string value, string destExpr, bool toScratchMemory)
        {
            TypeDefinition fixedSpecialType = type.FixedSpecialType();
            if (fixedSpecialType != null)
            {
                if (fixedSpecialType.MetadataType == MetadataType.Boolean) return $"HEAP8[{destExpr}] = ({value})?1:0";
                if (fixedSpecialType.MetadataType == MetadataType.SByte) return $"HEAP8[{destExpr}] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Byte) return $"HEAPU8[{destExpr}] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Int16) return $"HEAP16[({destExpr})>>1] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.UInt16) return $"HEAPU16[({destExpr})>>1] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Char) return $"HEAPU16[({destExpr})>>1] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Int32) return $"HEAP32[({destExpr})>>2] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.UInt32) return $"HEAPU32[({destExpr})>>2] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Single) return $"HEAPF32[({destExpr})>>2] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Double) return $"HEAPF64[({destExpr})>>3] = {value}";
                if (fixedSpecialType.MetadataType == MetadataType.Int64) return $"HEAP32[({destExpr})>>2] = /*64BIT*/{value}";
                if (fixedSpecialType.MetadataType == MetadataType.UInt64) return $"HEAPU32[({destExpr})>>2] = /*64BIT*/{value}";
                if (fixedSpecialType.MetadataType == MetadataType.IntPtr) return $"HEAP32[({destExpr})>>2] = /*PTR*/{value}";
                if (fixedSpecialType.MetadataType == MetadataType.UIntPtr) return $"HEAPU32[({destExpr})>>2] = /*PTR*/{value}";
                if (fixedSpecialType.MetadataType == MetadataType.String) return toScratchMemory ? $"ut.toExistingScratchNativeString({destExpr}, {value})" : $"ut.newHeapNativeString({destExpr}, {value})";
            }

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
                return JSHeapSetSpecial(type.Resolve(), value, destExpr);

            if (type.Resolve().IsDynamicArray())
                return JSHeapSetArray(type, value, destExpr, toScratchMemory);

            if (type.IsStructValueType() || type.IsComponentType())
                return $"{type.JSName()}._toPtr({destExpr}, {value})";

            if (type.Resolve().IsEnum)
                return $"HEAP32[({destExpr})>>2] = {value}";

            BindGem.FatalError($"Don't know how to JSHeapSet for {type}");
            return null;
        }

        internal static string JSHeapGetSpecial(this TypeReference type, string src)
        {
            switch (type.JavaScriptSpecialType())
            {
                case JSSpecialType.Vector2:
                    return $"ut._utils.vec2FromHeap(null, {src})";
                case JSSpecialType.Vector3:
                    return $"ut._utils.vec3FromHeap(null, {src})";
                case JSSpecialType.Vector4:
                    return $"ut._utils.vec4FromHeap(null, {src})";
                case JSSpecialType.Quaternion:
                    return $"ut._utils.quatFromHeap(null, {src})";
                case JSSpecialType.Matrix3:
                    return $"ut._utils.mat3FromHeap(null, {src})";
                case JSSpecialType.Matrix4:
                    return $"ut._utils.mat4FromHeap(null, {src})";
            }
            BindGem.FatalError($"Type {type} is not JSSpecial");//, type.Locations[0]);
            return null;
        }

        internal static string JSHeapGetArray(this TypeReference type, string srcExpr)
        {
            var elementType = type.DynamicArrayElementType();
            var sz = TypeUtils.AlignAndSizeOfType(elementType, 32);
            var convFn = $"function(p) {{ return {elementType.JSHeapGet("p")}; }}";
            return $"ut.nativeBufferToJsArray({srcExpr}, {sz.size}, {convFn})";
        }

        internal static string JSHeapGet(this TypeReference type, string srcExpr)
        {
            TypeDefinition fixedSpecialType = type.FixedSpecialType();

            if (fixedSpecialType != null)
            {
                if (fixedSpecialType.MetadataType == MetadataType.Boolean) return $"(HEAP8[{srcExpr}]?true:false)";
                if (fixedSpecialType.MetadataType == MetadataType.SByte) return $"HEAP8[{srcExpr}]";
                if (fixedSpecialType.MetadataType == MetadataType.Byte) return $"HEAPU8[{srcExpr}]";
                if (fixedSpecialType.MetadataType == MetadataType.Int16) return $"HEAP16[({srcExpr})>>1]";
                if (fixedSpecialType.MetadataType == MetadataType.UInt16) return $"HEAPU16[({srcExpr})>>1]";
                if (fixedSpecialType.MetadataType == MetadataType.Char) return $"HEAPU16[({srcExpr})>>1]";
                if (fixedSpecialType.MetadataType == MetadataType.Int32) return $"HEAP32[({srcExpr})>>2]";
                if (fixedSpecialType.MetadataType == MetadataType.UInt32) return $"HEAPU32[({srcExpr})>>2]";
                if (fixedSpecialType.MetadataType == MetadataType.Single) return $"HEAPF32[({srcExpr})>>2]";
                if (fixedSpecialType.MetadataType == MetadataType.Double) return $"HEAPF64[({srcExpr})>>3]";
                if (fixedSpecialType.MetadataType == MetadataType.Int64) return $"(/*64BIT*/HEAP32[({srcExpr})>>2])";
                if (fixedSpecialType.MetadataType == MetadataType.UInt64) return $"(/*64BIT*/HEAPU32[({srcExpr})>>2])";
                if (fixedSpecialType.MetadataType == MetadataType.IntPtr) return $"(/*PTR*/HEAP32[({srcExpr})>>2])";
                if (fixedSpecialType.MetadataType == MetadataType.UIntPtr) return $"(/*PTR*/HEAPU32[({srcExpr})>>2])";
                if (fixedSpecialType.MetadataType == MetadataType.String) return $"({UTRT}._ut_nativestring_data({srcExpr}) ? UTF8ToString({UTRT}._ut_nativestring_data({srcExpr})) : \"\")";
            }

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
                return JSHeapGetSpecial(type, srcExpr);

            if (type.Resolve().IsDynamicArray())
                return JSHeapGetArray(type, srcExpr);

            if (type.IsStructValueType() || type.IsComponentType())
                return $"{type.JSName()}._fromPtr({srcExpr})";

            if (type.Resolve().IsEnum)
                return $"HEAP32[({srcExpr})>>2]";

            BindGem.FatalError($"Don't know how to JSHeapGet for {type}");
            return null;
        }

        internal static string JSConvertParamToEmscripten(this ParameterDefinition param, string src, ref bool needTempHeap)
        {
            var type = param.ParameterType.Resolve();
            string simple = type.JSCoerceSimple(src);
            if (simple != null)
            {
                return param.HasDefault
                    ? $"{src} = ({src}===undefined ? ({param.DefaultValueToJSString()}) : {simple})"
                    : $"{src} = {simple}";
            }

            if (type.MetadataType == MetadataType.String)
            {
                needTempHeap = true;
                if (param.HasDefault)
                {
                    string defaultValue = param.DefaultValueToJSString();
                    if (defaultValue != null && defaultValue.Length > 0)
                        return $"{src} = ut.toNewScratchNativeString({src}===undefined ? \"{param.DefaultValueToJSString()}\" : {src})";
                    else // If default value is empty string, we don't need to compare against undefined, just to anything truthy, to save a few characters in the generated JS.
                        return $"{src} = ut.toNewScratchNativeString({src} ? {src} : \"\")";
                }
                else
                    return $"{src} = ut.toNewScratchNativeString({src})";
            }

            return JSConvertComplexToEmscripten(param.ParameterType, src, src, ref needTempHeap, param.HasDefault, param.HasDefault ? param.Constant : null);
        }

        internal static string JSConvertSelfToEmscripten(this TypeDefinition selfType, MethodDefinition method, out string self,
    out string selfPostamble, ref bool needTempHeap)
        {
            if (method.IsStatic || selfType.IsServiceRefType())
            {
                self = "";
                selfPostamble = "";
                return "";
            }

            BindGem.FatalErrorIf(selfType.JavaScriptSpecialType() != JSSpecialType.None,
                "Can't have methods on JSCustomImpl types");

            self = "self";
            // non-Pure struct methods need to convert their values back into the value type
            if (selfType.IsStructValueType() && !method.IsPure())
            {
                selfPostamble = $"if (self != this._ptr) {{ {selfType.JSName()}._fromPtr(self, this); }}";
            }
            else
            {
                selfPostamble = "";
            }
            return "var self = this._ptr; if (self === undefined) { " + JSConvertComplexToEmscripten(selfType, "this", "self", ref needTempHeap) + "; }";
        }

        internal static string JSConvertSpecialToEmscripten(this TypeDefinition type, string src, string dst,
    ref bool needTempHeap)
        {
            needTempHeap = true;
            switch (type.JavaScriptSpecialType())
            {
                case JSSpecialType.Vector2:
                    return $"{dst} = ut._utils.vec2ToTempHeap({src})";
                case JSSpecialType.Vector3:
                    return $"{dst} = ut._utils.vec3ToTempHeap({src})";
                case JSSpecialType.Vector4:
                    return $"{dst} = ut._utils.vec4ToTempHeap({src})";
                case JSSpecialType.Quaternion:
                    return $"{dst} = ut._utils.quatToTempHeap({src})";
                case JSSpecialType.Matrix3:
                    return $"{dst} = ut._utils.mat3ToTempHeap({src})";
                case JSSpecialType.Matrix4:
                    return $"{dst} = ut._utils.mat4ToTempHeap({src})";
            }
            BindGem.FatalError($"Type {type} is not JSSpecial");//, type.Locations[0]);
            return null;
        }

        internal static string JSConvertComplexToEmscripten(this TypeReference type, string src, string dst,
    ref bool needTempHeap, bool hasDefaultValue = false, object defaultValue = null)
        {
            if (type.IsServiceRefType() || type.IsSharedPtrType() || type.IsNonSharedPtrType()) {
                if (hasDefaultValue) {
                    BindGem.FatalErrorIf(defaultValue != null, "ptr type arguments can only have a default value of null");
                    return $"{dst} = {src} === undefined ? null : {src}.ptr;";
                }

                return $"{dst} = {src}.ptr";
            }

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
                return JSConvertSpecialToEmscripten(type.Resolve(), src, dst, ref needTempHeap);

            if (type.Resolve().IsDynamicArray())
            {
                needTempHeap = true;
                var elementType = type.DynamicArrayElementType();
                if (elementType.FixedSpecialType().MetadataType == MetadataType.String)
                    return $"{dst} = ut.jsArrayToNewScratchNativeBuffer_string({src}, function(p, v) {{ ut.toExistingScratchNativeString(p, v); }})";
                else
                {
                    var sz = TypeUtils.AlignAndSizeOfType(elementType, 32);
                    var convFn = $"function(p, v) {{ {elementType.JSHeapSet("v", "p", true)}; }}";
                    return $"{dst} = ut.jsArrayToNewScratchNativeBuffer_pod({src}, {sz.size}, {convFn})";
                }
            }

            if (type.IsStructValueType() || type.IsComponentType())
            {
                needTempHeap = true;
                return $"{dst} = {type.JSName()}._tempHeapPtr({src})";
            }

            if (type.Resolve().IsDelegate())
                return $"{dst} = {type.JSName()}._cb.token_for({src})";

            BindGem.FatalError($"Can't convert complex type {type} to Emscripten");
            return null;
        }

        internal static string JSStaticArrayToTempHeap(this TypeDefinition arrayType, string src)
        {
            TypeDefinition fixedSpecialType = arrayType.FixedSpecialType();
            if (fixedSpecialType.MetadataType == MetadataType.Boolean
                || fixedSpecialType.MetadataType == MetadataType.SByte
                || fixedSpecialType.MetadataType == MetadataType.Byte) return $"ut.tempHeapPtrI8({src})";
            if (fixedSpecialType.MetadataType == MetadataType.Int16
                || fixedSpecialType.MetadataType == MetadataType.UInt16
                || fixedSpecialType.MetadataType == MetadataType.Char) return $"ut.tempHeapPtrI16({src})";
            if (fixedSpecialType.MetadataType == MetadataType.Int32
                || fixedSpecialType.MetadataType == MetadataType.UInt32) return $"ut.tempHeapPtrI32({src})";
            if (fixedSpecialType.MetadataType == MetadataType.Single) return $"ut.tempHeapPtrF32({src})";
            if (fixedSpecialType.MetadataType == MetadataType.Double) return $"ut.tempHeapPtrF64({src})";
            if (fixedSpecialType.MetadataType == MetadataType.IntPtr
                || fixedSpecialType.MetadataType == MetadataType.UIntPtr) return $"ut.tempHeapPtrI32({src})";

            // TODO: InterfaceReflector.SInt64, InterfaceReflector.UInt64, InterfaceReflector.String
            BindGem.FatalError($"Can't do arrays of {arrayType}");//, arrayType.Locations[0]);
            return null;
        }

        internal static string JSCallbackArgToEmscripten(this TypeDefinition type, int pnum, out string passParam)
        {
            passParam = $"arg{pnum}";
            if (type.JSIsSimpleType())
            {
                return "";
            }

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
            {
                passParam = $"&arg{pnum}";
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"${pnum} = ut._utils.vec2FromHeap(null, {pnum})";
                    case JSSpecialType.Vector3:
                        return $"${pnum} = ut._utils.vec3FromHeap(null, {pnum})";
                    case JSSpecialType.Vector4:
                        return $"${pnum} = ut._utils.vec4FromHeap(null, {pnum})";
                    case JSSpecialType.Quaternion:
                        return $"${pnum} = ut._utils.quatFromHeap(null, {pnum})";
                    case JSSpecialType.Matrix3:
                        return $"${pnum} = ut._utils.mat3FromHeap(null, {pnum})";
                    case JSSpecialType.Matrix4:
                        return $"${pnum} = ut._utils.mat4FromHeap(null, {pnum})";
                }
            }

            if (type.IsStructValueType() || type.IsComponentType()) {
                return $"${pnum} = {type.JSName()}._fromPtr(${pnum});";
            }

            if (type.IsSharedPtrType() || type.IsNonSharedPtrType())
            {
                return $"${pnum} = {type.JSName()}._wrap(${pnum});";
            }

            BindGem.FatalError($"Don't know how to convert arg{pnum} of type {type}");
            return null;
        }


        internal static string JSMakeReturnFromEmscripten(this TypeDefinition type, string src, ref bool needsTempHeap)
        {
            var specialType = type.FixedSpecialType();
            if (specialType?.MetadataType == MetadataType.Void)
                return "";
            if (specialType?.MetadataType == MetadataType.Boolean)
                return $"return !!({src});";
            if (type.JSIsSimpleType() || type.IsEnum)
                return $"return {src};";

            if (type.JavaScriptSpecialType() != JSSpecialType.None)
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"return ut._utils.vec2FromHeap(null, {src})";
                    case JSSpecialType.Vector3:
                        return $"return ut._utils.vec3FromHeap(null, {src})";
                    case JSSpecialType.Vector4:
                        return $"return ut._utils.vec4FromHeap(null, {src})";
                    case JSSpecialType.Quaternion:
                        return $"return ut._utils.quatFromHeap(null, {src})";
                    case JSSpecialType.Matrix3:
                        return $"return ut._utils.mat3FromHeap(null, {src})";
                    case JSSpecialType.Matrix4:
                        return $"return ut._utils.mat4FromHeap(null, {src})";
                }
            }

            // For strings, the C++ return type is a 'char *', and the caller is expected
            // to free the allocated string.
            // @TODO We stringify this immediately after the call; we should be able to avoid the malloc for some calls
            if (type.MetadataType == MetadataType.String)
                return $"var {src}_s = (({src}) ? UTF8ToString({src}) : \"\"); {UTRT}._free({src}); return {src}_s;";

            if (type.IsStructValueType())
            {
                if (type.EmCppReturnStructByValue())
                {
                    return $"var {src}_s = new {type.JSName()}(); {src}_s{type.JSPathToDeepDataMember()} = {src}; return {src}_s;";
                }
                BindGem.FatalError($"Shouldn't have gotten here -- this needs to be handled at the callsite");
            }
            if (type.IsSharedPtrType() || type.IsNonSharedPtrType())
            {
                return $"return {type.JSName()}._wrap({src});";
            }

            BindGem.FatalError($"Can't return values of type {type} yet -- is that a valid type name?");
            return null;
        }

        internal static string JSPathToDeepDataMember(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            if (!type.IsStructValueType() || type.Fields.Count > 1)
            {
                BindGem.FatalError($"{type.Name} isn't a one-member struct, how did we get here?");
            }

            var firstField = type.Fields[0];
            var fieldStr = $".{firstField.Name}";
            if (firstField.FieldType.Resolve().JSIsSimpleType())
                return fieldStr;

            return fieldStr + firstField.FieldType.Resolve().JSPathToDeepDataMember();
        }

        internal static string FullSystemName(this TypeDefinition type)
        {
            return type.Namespace + "." + type.Name;
        }

        internal static string JSName(this TypeReference type)
        {
            if (type.Resolve().HasAttribute("JSName")) {
                return type.FullyQualifiedNamespaceName(".") + "." + type.Resolve().GetValueOfAttribute("JSName");
            }

            return type.FullyQualifiedCppName(".");
        }

        internal static string JSMetaName(this TypeReference type)
        {
            var tc = EmCppSimpleType(type.FixedSpecialType());
            if (tc != null)
                return tc;
            return type.FullyQualifiedCppName(".");
        }

        internal static string CppName(this TypeDefinition type)
        {
            if (type.HasAttribute("CppName"))
                return type.GetValueOfAttribute("CppName");
            return type.Name;
        }

        internal static string FullyQualifiedCppName(this TypeReference type)
        {
            if (type.Resolve().HasAttribute("CppName"))
                return type.Resolve().GetValueOfAttribute("CppName");
            return type.FullyQualifiedCppName("::");
        }

        internal static string JSName(this MethodDefinition fn)
        {
            return fn.Name.WithLowercaseFirstLetter();
        }

        internal static string CppName(this MethodDefinition fn)
        {
            if (fn.HasAttribute("CppName"))
                return fn.GetValueOfAttribute("CppName");
            return fn.Name.WithLowercaseFirstLetter();
        }

        internal static string CppFnPrefix(this TypeDefinition type)
        {
            return type.FullyQualifiedCppName("_");
        }

        internal static string EmCppCtorName(this TypeDefinition type)
        {
            var methodName = type.Name;
            return $"{type.CppFnPrefix()}_{methodName}";
        }

        internal static string EmCppFunctionName(this TypeDefinition type, MethodDefinition m)
        {
            var methodName = m.IsConstructor ? type.Name : m.Name;
            return $"{type.CppFnPrefix()}_{methodName}";
        }

        internal static TypeDefinition SpecialTypeOfDeepestSingleMemberStruct(this TypeDefinition baseType)
        {
            if (!baseType.IsStructValueType())
                return null;

            var type = baseType;
            while (type != null)
            {
                if (type.Fields.Count != 1)
                    return null;

                var firstField = type.Fields.First();
                var special = firstField.FieldType.Resolve().FixedSpecialType();
                if (special != null)
                    return special;
                if (firstField.FieldType.Resolve().IsEnum)
                    return type.Module.TypeSystem.Int32.Resolve();
                if (!firstField.FieldType.Resolve().IsStructValueType())
                    return null;
                type = firstField.FieldType.Resolve();
            }
            return null;
        }

        internal static bool EmCppReturnStructByValue(this TypeDefinition type)
        {
            // TODO: C# dropped the following two lines
            //  vlad: we need to double check whether this is actually part of the ABI or if I was mistaken
            //  this also might be different for wasm vs. asmjs
            var special = type.SpecialTypeOfDeepestSingleMemberStruct();
            if (special == null) return false;

            return special.MetadataType == MetadataType.Boolean
                || special.MetadataType == MetadataType.SByte
                || special.MetadataType == MetadataType.Byte
                || special.MetadataType == MetadataType.Int16
                || special.MetadataType == MetadataType.UInt16
                || special.MetadataType == MetadataType.Char
                || special.MetadataType == MetadataType.Int32
                || special.MetadataType == MetadataType.UInt32
                || special.MetadataType == MetadataType.Single
                || special.MetadataType == MetadataType.Double
                || special.MetadataType == MetadataType.IntPtr
                || special.MetadataType == MetadataType.UIntPtr;
        }

        internal static string EmCppSimpleType(this TypeReference type)
        {
            if (type == null) return null;
            type = type.FixedSpecialType();
            if (type == null) return null;
            if (type.MetadataType == MetadataType.Boolean) return "bool";
            if (type.MetadataType == MetadataType.SByte) return "int8_t";
            if (type.MetadataType == MetadataType.Byte) return "uint8_t";
            if (type.MetadataType == MetadataType.Int16) return "int16_t";
            if (type.MetadataType == MetadataType.UInt16) return "uint16_t";
            if (type.MetadataType == MetadataType.Char) return "uint16_t";
            if (type.MetadataType == MetadataType.Int32) return "int32_t";
            if (type.MetadataType == MetadataType.UInt32) return "uint32_t";
            if (type.MetadataType == MetadataType.Single) return "float";
            if (type.MetadataType == MetadataType.Double) return "double";
            if (type.MetadataType == MetadataType.IntPtr) return "intptr_t";
            if (type.MetadataType == MetadataType.UIntPtr) return "uintptr_t";
            if (type.MetadataType == MetadataType.Void) return "void";
            return null;
        }

        internal static string EmCppReturnType(this TypeReference type)
        {
            if (type.IsSharedPtrType() || type.IsNonSharedPtrType()) {
                var cppName = type.Resolve().GetValueOfAttribute("CppName");
                if (cppName != null)
                    return cppName + "*";
                return type.FullyQualifiedCppName() + "*";
            }

            if (type.MetadataType == MetadataType.Void) {
                if (type.IsPointer)
                    return "void*";
                return "void";
            }
            if (type.MetadataType == MetadataType.String)
                return "ut::NativeString";

            if (type.IsDynamicArray())
                return $"ut::NativeBuffer<{type.DynamicArrayElementType().EmCppReturnType()}>";

// TODO: C# used this - resolve for JS?:
//  vlad: I think we did this because we wanted the return to be the actual struct (thus getting the magic retptr treatment),
//  whereas corresponding code in EmCppArgType tried to pass structs by const reference.  I don't think either is actually
//  needed, though const ref is definitely more correct; any structs coming in are going to be just temporaries.
//            if (type.IsStructValueType())
//                return type.Resolve().FullyQualifiedName();

            return type.EmCppArgType();
        }

        internal static string EmCppReturnType(this TypeReference type, MethodDefinition method)
        {
            if (!method.IsConstructor)
                type = method.ReturnType;
            return type.EmCppReturnType();
        }

        internal static string EmCppArgTypeForRetPtr(this TypeReference type)
        {
            if (type.IsStructValueType()) {
                var cppName = type.Resolve().GetValueOfAttribute("CppName");
                if (cppName != null)
                    return cppName + "*";

                return $"{type.FullyQualifiedCppName()}*";
            }

            if (type.FixedSpecialType()?.MetadataType == MetadataType.String)
                return $"ut::NativeString*";
            return type.EmCppArgType() + "*";
        }

        internal static string EmCppArgType(this TypeReference typeRef)
        {
            bool isPointer = typeRef.IsPointer;
            TypeDefinition type = typeRef.Resolve();
            var simple = type.EmCppSimpleType();
            if (simple != null) {
                if (isPointer)
                    return $"{simple}*";
                return simple;
            }

            if (type.IsDynamicArray())
            {
                if (typeRef.DynamicArrayElementType().FixedSpecialType().MetadataType == MetadataType.String)
                    return $"const ut::NativeBuffer<ut::NativeString>&";
                return $"const ut::NativeBuffer<{typeRef.DynamicArrayElementType().EmCppArgType()}>&";
            }

            if (type.IsSharedPtrType() || type.IsNonSharedPtrType()) {
                var cppName = type.Resolve().GetValueOfAttribute("CppName");
                if (cppName != null)
                    return cppName + "*";
                return $"{type.FullyQualifiedCppName()}*";
            }

            if (type.IsServiceRefType())
                return $"{type.FullyQualifiedCppName()}*";

            if (type.FixedSpecialType()?.MetadataType == MetadataType.String)
                return $"const ut::NativeString&";

// TODO: C# used this - resolve for JS?:
//   vlad: see note above
//            if (type.IsStructValueType())
//                return $"const {type.FullyQualifiedName()}&";

            if (type.IsEnum) {
                if (isPointer)
                    return $"{type.FullyQualifiedCppName()}*";
                return $"{type.FullyQualifiedCppName()}";
            }

            if (type.IsStructValueType() || type.IsComponentType()) {
                if (isPointer)
                    return $"{type.FullyQualifiedCppName()}*";
                return $"{type.FullyQualifiedCppName()}";
            }

            // delegates are a token index
            if (type.IsDelegate())
                return "uint32_t";

            BindGem.FatalError($"UNKNOWN_TYPE<{type} - kind {type}>"); // TODO .TypeKind?
            return null;
        }

        internal static string EmCppSelfFnArgType(this TypeDefinition type, MethodDefinition fn = null)
        {
            if (type.IsServiceRefType())
                return "";
            if (fn != null && fn.IsStatic)
                return "";
            if (type.IsStructValueType() || type.IsComponentType())
                return $"{type.FullyQualifiedCppName()}& self";
            if (type.IsSharedPtrType() || type.IsNonSharedPtrType())
                return $"{type.FullyQualifiedCppName()}* self";

            BindGem.FatalError($"Unknown self type {type} in method {fn}");
            return null;
        }

        internal static string EmCppSelfCall(this TypeDefinition type, MethodDefinition fn)
        {
            if (type.IsServiceRefType())
                return $"{type.FullyQualifiedCppName()}::get()->";
            if (fn.IsStatic)
                return $"{type.FullyQualifiedCppName()}::";
            if (type.IsStructValueType() || type.IsComponentType())
                return "self.";
            return "self->";
        }

        internal static bool EmCppReturnToFirstArgPtr(this TypeReference type)
        {
            if (type.IsComponentType())
                return true;
            if (type.IsDynamicArray() ||
                type.MetadataType == MetadataType.String)
                return true;
            if (type.IsStructValueType())
                return true;
            return false;
        }

        internal static string JSMakeRetPtr(this TypeDefinition type)
        {
            if (type.IsDynamicArray())
            {
                // a std::vector
                return $"var retptr = ut.tempHeapPtrI32([0,0,0]);";
            }
            if (type.MetadataType == MetadataType.String)
            {
                // The generated string will be used as a target for move assignment operator from C++ side,
                // so it is important here to clear the bytes for the NativeString with tempHeapPtrBufferZero()
                // (essentially to construct a zero-length NativeString)
                return $"var retptr = ut.tempHeapPtrBufferZero({TypeUtils.AlignAndSizeOfType(MetadataType.String, 32).size});";
            }
            if (type.JavaScriptSpecialType() != JSSpecialType.None)
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*2);";
                    case JSSpecialType.Vector3:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*3);";
                    case JSSpecialType.Vector4:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*4);";
                    case JSSpecialType.Quaternion:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*4);";
                    case JSSpecialType.Matrix3:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*9);";
                    case JSSpecialType.Matrix4:
                        return $"var retptr = ut.tempHeapPtrBuffer(4*16);";
                }
            }

            return $"var retptr = {type.JSName()}._tempHeapPtr();";
        }

        internal static string JSMakeReturnFromRetPtr(this TypeDefinition type)
        {
            if (type.IsDynamicArray())
            {
                return $"var ret = {type.JSHeapGetArray("retptr")}; {UTRT}._free(HEAP32[retptr>>2]); return ret;";
            }
            if (type.MetadataType == MetadataType.String)
            {
                return $"var ret = ({UTRT}._ut_nativestring_data(retptr) ? UTF8ToString({UTRT}._ut_nativestring_data(retptr)) : \"\"); {UTRT}._ut_nativestring_placement_delete(retptr); return ret;";
            }
            if (type.JavaScriptSpecialType() != JSSpecialType.None)
            {
                switch (type.JavaScriptSpecialType())
                {
                    case JSSpecialType.Vector2:
                        return $"return ut._utils.vec2FromHeap(null, retptr);";
                    case JSSpecialType.Vector3:
                        return $"return ut._utils.vec3FromHeap(null, retptr);";
                    case JSSpecialType.Vector4:
                        return $"return ut._utils.vec4FromHeap(null, retptr);";
                    case JSSpecialType.Quaternion:
                        return $"return ut._utils.quatFromHeap(null, retptr);";
                    case JSSpecialType.Matrix3:
                        return $"return ut._utils.mat3FromHeap(null, retptr);";
                    case JSSpecialType.Matrix4:
                        return $"return ut._utils.mat4FromHeap(null, retptr);";
                }
            }

            return $"return {type.JSName()}._fromPtr(retptr);";
        }

        internal static bool IsPure(this MethodDefinition fn)
        {
            return fn.HasAttribute("Pure");
        }

        internal static string TypeDescBasicTypeName(this TypeDefinition type)
        {
            switch (type.MetadataType)
            {
                case MetadataType.Boolean:  return "bool";
                case MetadataType.SByte:    return "int8";
                case MetadataType.Int16:    return "int16";
                case MetadataType.Int32:    return "int32";
                case MetadataType.Int64:    return "int64";
                case MetadataType.Byte:     return "uint8";
                case MetadataType.UInt16:   return "uint16";
                case MetadataType.UInt32:   return "uint32";
                case MetadataType.UInt64:   return "uint64";
                case MetadataType.Single:   return "float";
                case MetadataType.Double:   return "double";
                case MetadataType.String:   return "string";
                default:                    return null;
            }
        }

        internal static string TypeDescName(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            var basicType = type.TypeDescBasicTypeName();
            if (basicType != null)
                return basicType;

            if (type.IsComponentTypeId())
                return "uint32";

            if (type.IsDynamicArray())
                return $"ut.NativeBuffer<{typeRef.DynamicArrayElementType().TypeDescName()}>";

            if (type.IsStructValueType() || type.IsComponentType() || type.IsEnum)
                return type.JSName();

            BindGem.FatalError($"Don't know how to make a TypeDesc name from {type}");
            return null;
        }
    }
}
