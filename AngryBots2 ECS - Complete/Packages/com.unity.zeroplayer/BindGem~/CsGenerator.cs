using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ExtensionMethods;
using Mono.Cecil;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    internal class CsBindingsGenerator : ILanguageHandler
    {
        private StringBuilder csharpSrc = new StringBuilder();

        public string GetGeneratedSource(SourceLanguage lang) {
            if (lang == SourceLanguage.CSharp)
                return csharpSrc.ToString();
            return null;
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            if (lang == SourceLanguage.CSharp)
                return 100;
            return 0;
        }

        public void line(string s) {
            csharpSrc.AppendLine(s);
        }

        public void HandleStructType(TypeDefinition type)
        {
            if (type.HasAttribute("CsCustomImpl"))
                return;

            WriteType(type);
        }

        public void HandleComponentType(TypeDefinition type)
        {
            if (type.HasAttribute("CsCustomImpl"))
                return;

            WriteType(type);
        }

        public void HandleCallbackType(TypeDefinition type)
        {
            var invoke = type.Methods.Single(m => m.Name == "Invoke");
            line($"namespace {type.Namespace}");
            line("{");
            line($"    public delegate {invoke.ReturnType.AsCsName()} {type.CsName()} ({ParameterList(invoke, true).JoinWithComma()});");
            line("}");
        }

        public void HandleInterfaceType(TypeDefinition type)
        {
            WriteType(type);
        }

        public void HandleEnumType(TypeDefinition type)
        {
            if (type.IsNested)
                return;

            if (type.HasAttribute("CsCustomImpl"))
                return;

            line($"namespace {type.Namespace}");
            line("{");
            line($"    public enum {type.CsName()}");
            line("    {");
            foreach (var field in type.Fields.Where(f => f.IsStatic))
            {
                line($"        {field.Name} = {field.Constant},");
            }
            line("    }");
            line("}");
        }

        public void HandleSystemType(TypeDefinition type)
        {
            if (BindGem.PureJS)
                return;

            if (type.HasAttribute("CsCustomImpl"))
                return;

            var cppFnName = type.FullyQualifiedCppName("_");

            line("namespace " + type.Namespace);
            line("{");

            line($"    public struct {type.CsName()} : IComponentSystem");
            line("    {");

            line($@"        [global::System.Runtime.InteropServices.DllImport(UTiny.Interop.NativeLibraryName)]");
            line($"        private static extern global::System.IntPtr {cppFnName}();");

            line($"        internal static bool sCreated = false;");
            line($"        internal static UTiny.SystemBase sInstance;");

            line($"        public static UTiny.SystemBase Instance() {{ if(!sCreated) {{ sInstance = new UTiny.SystemBase({cppFnName}()); sCreated = true; }} return sInstance; }}");

            line("    }");
            line("}");
        }

        // Complex component types need to be marshalled down to C++ format when transitioning them between the C# <-> C++ language boundary. This is to
        // avoid memory leaks from strings and arrays from occurring when the component type is manipulated in C# side.
        private void GenerateComponentSerializeToNativeCppMethod(TypeDefinition type)
        {
            for (int bitness = 32; bitness <= 64; bitness += 32)
            {
                var sz = TypeUtils.AlignAndSizeOfType(type, bitness);
                line($"        public unsafe NativeBuffer SerializeToNativeCpp{bitness}()");
                line("        {");

                if (type.IsPodType())
                {
                    line($"            throw new global::System.InvalidOperationException(\"This function should never get called (POD types match in ABI between C++ and C#)\"); // but so that all components adhere to same interface, even POD types need this function defined for them");
                    line("        }");
                    continue;
                }

                line($"            NativeBuffer __buffer = new NativeBuffer();");
                line($"            StringInterop.ut_nativebuffer_pod_placement_create_uninitialized((void*)&__buffer, {sz.size}, 1);");
                line($"            byte* __data = (byte*)StringInterop.ut_nativebuffer_data(&__buffer);");
                int memcpyStartOffset = -1;
                int memcpyEndOffset = -1;
                string memcpyStartField = null;
                foreach (var f in type.Fields)
                {
                    var fieldSize = TypeUtils.AlignAndSizeOfField(f, bitness);
                    var ident = CSharpIdentifierFor(f.Name);
                    if (f.FieldType.IsComplex())
                    {
                        if (memcpyStartField != null)
                        {
                            line($"            UnsafeUtility.MemCpy(__data + {memcpyStartOffset}, (byte*)UnsafeUtility.AddressOf(ref {memcpyStartField}), {memcpyEndOffset - memcpyStartOffset});");
                            memcpyStartField = null;
                        }
                        if (f.FieldType.MetadataType == MetadataType.String)
                        {
                            line($"          {{");
                            line($"            uint __len = ({ident} == null || {ident}.Length == 0) ? 0 : (uint)StringInterop.StringLengthUtf8({ident});");
                            line($"            StringInterop.ut_nativestring_placement_create((void*)(__data + {fieldSize.offset}), __len);");
                            line($"            if (__len > 0) {{");
                            line($"                StringInterop.StringToUtf8({ident}, ((NativeString*)(__data + {fieldSize.offset}))->mStr, ((NativeString*)(__data + {fieldSize.offset}))->mSize+1);");
                            line($"            }}");
                            line($"          }}");
                        }
                        else if (f.FieldType.IsDynamicArray())
                        {
                            if (f.FieldType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            {
                                line($"          {{");
                                line($"            uint __len = ({ident} == null || {ident}.Length == 0) ? 0 : (uint){ident}.Length;");
                                line($"            StringInterop.ut_nativebuffer_nativestring_placement_create((void*)(__data + {fieldSize.offset}), __len);");
                                line($"            if (__len > 0) {{");
                                line($"                StringInterop.StringListToExistingNativeBuffer((NativeBuffer*)(__data + {fieldSize.offset}), {ident});");
                                line($"            }}");
                                line($"          }}");
                            }
                            else
                            {
                                var arrayElementSize = TypeUtils.AlignAndSizeOfType(f.FieldType.DynamicArrayElementType(), bitness);
                                line($"          {{");
                                line($"            uint __len = ({ident} == null || {ident}.Length == 0) ? 0 : (uint){ident}.Length;");
                                line($"            StringInterop.ut_nativebuffer_pod_placement_create_uninitialized((void*)(__data + {fieldSize.offset}), __len, {arrayElementSize.size});");
                                line($"            if (__len > 0) {{");
                                line($"                StringInterop.PodDataListToExistingNativeBuffer((NativeBuffer*)(__data + {fieldSize.offset}), {ident}, {arrayElementSize.size});");
                                line($"            }}");
                                line($"          }}");
                            }
                        }
                    }
                    else
                    {
                        if (memcpyStartField == null)
                        {
                            memcpyStartOffset = fieldSize.offset;
                            memcpyStartField = CSharpIdentifierFor(f.Name);
                        }
                        memcpyEndOffset = fieldSize.offset + fieldSize.size;
                    }
                }
                if (memcpyStartField != null)
                {
                    line($"            UnsafeUtility.MemCpy(__data + {memcpyStartOffset}, (byte*)UnsafeUtility.AddressOf(ref {memcpyStartField}), {memcpyEndOffset - memcpyStartOffset});");
                }
                line("            return __buffer;");
                line("        }");
            }
            line($"        public unsafe NativeBuffer SerializeToNativeCpp() {{ if (global::System.IntPtr.Size == 8) return SerializeToNativeCpp64(); else return SerializeToNativeCpp32(); }}");
        }

        private void GenerateComponentDeserializeFromNativeCppMethod(TypeDefinition type)
        {
            var sz32 = TypeUtils.AlignAndSizeOfType(type, 32);
            var sz64 = TypeUtils.AlignAndSizeOfType(type, 64);
            string sizeString = (sz32.size == sz64.size) ? sz32.size.ToString() : $"global::System.IntPtr.Size == 8 ? {sz64.size} : {sz32.size}";

            line($"        public int NativeCppComponentSize() {{ return {sizeString}; }}");

            for (int bitness = 32; bitness <= 64; bitness += 32)
            {
                var sz = TypeUtils.AlignAndSizeOfType(type, bitness);

                line($"        public unsafe void DeserializeFromNativeCpp{bitness}(global::System.IntPtr __srcData, int __size)");
                line("        {");

                if (type.IsPodType())
                {
                    line($"            throw new global::System.InvalidOperationException(\"This function should never get called (POD types match in ABI between C++ and C#)\"); // but so that all components adhere to same interface, even POD types need this function defined for them");
                    line("        }");
                    continue;
                }

                line($"            byte* __src = (byte*)__srcData;");

                int memcpyStartOffset = -1;
                int memcpyEndOffset = -1;
                string memcpyStartField = null;
                foreach (var f in type.Fields)
                {
                    var fieldSize = TypeUtils.AlignAndSizeOfField(f, bitness);
                    if (f.FieldType.IsComplex())
                    {
                        if (memcpyStartField != null)
                        {
                            line($"            UnsafeUtility.MemCpy((byte*)UnsafeUtility.AddressOf(ref {memcpyStartField}), __src + {memcpyStartOffset}, {memcpyEndOffset - memcpyStartOffset});");
                            memcpyStartField = null;
                        }
                        if (f.FieldType.MetadataType == MetadataType.String)
                        {
                            line($"            {CSharpIdentifierFor(f.Name)} = StringInterop.Utf8ToString(((NativeString*)(__src + {fieldSize.offset}))->mStr, ((NativeString*)(__src + {fieldSize.offset}))->mSize);");
                        }
                        else if (f.FieldType.IsDynamicArray())
                        {
                            if (f.FieldType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            {
                                line($"          {CSharpIdentifierFor(f.Name)} = StringInterop.StringListFromExistingNativeBuffer((NativeBuffer*)(__src + {fieldSize.offset}));");
                            }
                            else
                            {
                                var arrayElementSize = TypeUtils.AlignAndSizeOfType(f.FieldType.DynamicArrayElementType(), bitness);
                                line($"            {CSharpIdentifierFor(f.Name)} = StringInterop.PodDataListFromExistingNativeBuffer<{f.FieldType.DynamicArrayElementType().AsCsName()}>((NativeBuffer*)(__src + {fieldSize.offset}), {arrayElementSize.size});");
                            }
                        }
                    }
                    else
                    {
                        if (memcpyStartField == null)
                        {
                            memcpyStartOffset = fieldSize.offset;
                            memcpyStartField = CSharpIdentifierFor(f.Name);
                        }
                        memcpyEndOffset = fieldSize.offset + fieldSize.size;
                    }
                }
                if (memcpyStartField != null)
                {
                    line($"            UnsafeUtility.MemCpy((byte*)UnsafeUtility.AddressOf(ref {memcpyStartField}), __src + {memcpyStartOffset}, {memcpyEndOffset - memcpyStartOffset});");
                }
                line("        }");
            }
            line($"        public unsafe void DeserializeFromNativeCpp(global::System.IntPtr srcData, int size) {{ if (global::System.IntPtr.Size == 8) DeserializeFromNativeCpp64(srcData, size); else DeserializeFromNativeCpp32(srcData, size); }}");
        }

        private void WriteType(TypeDefinition type)
        {
            if (type.HasAttribute("PureJSService"))
                return;

            var interfaces = type.Interfaces.Select(x => x.InterfaceType.AsCsName()).ToList();

            var isComponentData = type.Interfaces.Count(x => x.InterfaceType.Name == "IComponentData") != 0
                || type.Interfaces.Count(x => x.InterfaceType.Name == "ISharedComponentData") != 0
                || type.Interfaces.Count(x => x.InterfaceType.Name == "ISystemStateComponentData") != 0;

            if (isComponentData && !BindGem.PureJS) {
                interfaces.Add("UTiny.IComponentDataInternal");
                if (type.IsPodType())
                    interfaces.Add("UTiny.IComponentIsPodData");
            }

            var interfacesLine = isComponentData ? ": " + String.Join(", ", interfaces) : "";

            string kind;
            if (type.IsInterface) {
                kind = type.HasAttribute("Service") ? "static class" : "partial struct";
            } else {
                kind = type.HasAttribute("CsPartial") ? "partial struct" : "struct";
            }

            bool isService = type.HasAttribute("Service");
            bool isNativeClassType = type.IsInterface && !isService;
            bool isConstructable = type.HasAttribute("Constructable") && !isService;

            line("namespace " + type.Namespace);
            line("{");

            if (type.IsComponentType() || type.IsStructValueType()) {
                line(
                    "    [global::System.Runtime.InteropServices.StructLayout(global::System.Runtime.InteropServices.LayoutKind.Sequential)]");
            }

            line($"    public {kind} {type.CsName()} {interfacesLine}");
            line("    {");

            var constructorName = $"ut_{type.Name}_{type.Name}";

            var cppFnName = type.FullyQualifiedCppName("_");

            if (isNativeClassType)
            {
                line($"        public global::System.IntPtr mThis;");

                line($"        [global::System.Runtime.InteropServices.DllImport(UTiny.Interop.NativeLibraryName)]");
                line($"        private static extern global::System.IntPtr {constructorName}();");
                line($"        public {type.CsName()}(global::System.IntPtr t) {{ mThis = t; }}");
                line("");

                line($"        [global::System.Runtime.InteropServices.DllImport(UTiny.Interop.NativeLibraryName)]");
                line($"        private static extern void ut_{type.Name}_shRelease(global::System.IntPtr ptr);");
                line($"        public void _NativeRelease() {{ ut_{type.Name}_shRelease(mThis); mThis = global::System.IntPtr.Zero; }}");
            }

            if (isConstructable)
            {
                line($"        public static {type.CsName()} New{type.CsName()}() {{ return new {type.CsName()}({constructorName}()); }}");
            }

            line("");

            if (isComponentData && !BindGem.PureJS)
            {
                line($@"        [global::System.Runtime.InteropServices.DllImport(UTiny.Interop.NativeLibraryName)]");
                line($@"        internal static extern int {cppFnName}_cid();");
                line($@"        public int NativeComponentId() {{ return {cppFnName}_cid(); }}");

                GenerateComponentSerializeToNativeCppMethod(type);
                GenerateComponentDeserializeFromNativeCppMethod(type);
            }

            line("");

            foreach (var f in type.Fields)
            {
                if (f.FieldType.MetadataType == MetadataType.String)
                {
                    line($"        public string {f.Name};");
                }
                else if (f.FieldType.IsDynamicArray())
                {
                    line($"        public {f.FieldType.DynamicArrayElementType().AsCsName()}[] {f.Name};");
                }
                else
                {
                    line($"        public {f.FieldType.AsCsName()} {CSharpIdentifierFor(f.Name)};");
                }
            }

            line("");

            line("");

            foreach (var m in type.MemberFunctions())
            {
                if (m.HasAttribute("CsCustomImpl"))
                    continue;

                // Declare the DllImported C call
                line($"        [global::System.Runtime.InteropServices.DllImport(UTiny.Interop.NativeLibraryName)]");
                if (m.ReturnType.Resolve().MetadataType == MetadataType.Boolean && !m.ReturnType.IsPointer)
                    line($"        [return:global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.I1)]");
                line($"        private static unsafe extern {m.ReturnType.AsCsReturnName()} {cppFnName}_{m.Name}({ParameterListForCCall(m, isService, isService).JoinWithComma()});");

                // Then declare the public C# API
                line($"        public {(isService || m.IsStatic ? "static " : "")}unsafe {m.ReturnType.AsCsName()} {m.Name}({ParameterList(m, true, false).JoinWithComma()})");
                line($"        {{");

                var callStr =
                    $"{cppFnName}_{m.Name}({ParameterNamesForCCall(m, isNativeClassType || isService, isService).JoinWithComma()})";

                if ((type.IsStructValueType() || type.IsComponentType()) && !m.IsStatic) {
                    line($"            //fixed ({type.AsCsName()}* self = &this)");
                    line($"            void* self = UnsafeUtility.AddressOf(ref this);");
                }

                // Marshal type conversions for C# -> C++ call
                for(int i = 0; i < m.Parameters.Count; ++i)
                {
                    if (m.Parameters[i].ParameterType.MetadataType == MetadataType.String)
                    {
                        line($"            NativeString arg{i}str = new NativeString();");
                        line($"            StringInterop.ut_nativestring_placement_create(&arg{i}str, (uint)StringInterop.StringLengthUtf8(arg{i}));");
                        line($"            StringInterop.StringToUtf8(arg{i}, arg{i}str.mStr, arg{i}str.mSize+1);");
                    }
                    else if (m.Parameters[i].ParameterType.IsDynamicArray())
                    {
                        if (m.Parameters[i].ParameterType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            line($"            NativeBuffer arg{i}arr = StringInterop.StringListToNewNativeBuffer(arg{i});");
                        else
                        {
                            var sz32 = TypeUtils.AlignAndSizeOfType(m.Parameters[i].ParameterType.DynamicArrayElementType(), 32);
                            var sz64 = TypeUtils.AlignAndSizeOfType(m.Parameters[i].ParameterType.DynamicArrayElementType(), 64);
                            string sizeString = (sz32.size == sz64.size) ? sz32.size.ToString() : $"global::System.IntPtr.Size == 8 ? {sz64.size} : {sz32.size}";
                            line($"            NativeBuffer arg{i}arr = StringInterop.PodDataListToNewNativeBuffer(arg{i}, {sizeString});");
                        }
                    }
                }

                string retval = null;
                if (m.ReturnType.MetadataType == MetadataType.Void)
                {
                    line($"            {callStr};");
                }
                else if (m.ReturnType.MetadataType == MetadataType.String) {
                    line($"            NativeString retval;");
                    line($"            {callStr};");
                    line($"            string str = StringInterop.Utf8ToString(retval.mStr, retval.mSize);");
                    line($"            StringInterop.ut_nativestring_free_data(&retval);");
                    retval = "str";
                }
                else if (m.ReturnType.IsSharedPtrType() || m.ReturnType.IsNonSharedPtrType())
                {
                    line($"            var ptr = {callStr};");
                    retval = $"new {m.ReturnType.Resolve().AsCsName()}(ptr)";
                }
                else if (m.ReturnType.EmCppReturnToFirstArgPtr())
                {
                    line($"            {m.ReturnType.AsCsName()} retval;");
                    line($"            {callStr};");
                    retval = "retval";
                } else {
                    line($"            {m.ReturnType.AsCsName()} retval = {callStr};");
                    retval = "retval";
                }

                // Cleanup for marshalled type conversions for C# -> C++ calls
                for (int i = 0; i < m.Parameters.Count; ++i)
                {
                    if (m.Parameters[i].ParameterType.MetadataType == MetadataType.String)
                    {
                        line($"            StringInterop.ut_nativestring_placement_delete(&arg{i}str);");
                    }
                    else if (m.Parameters[i].ParameterType.IsDynamicArray())
                    {
                        if (m.Parameters[i].ParameterType.DynamicArrayElementType().MetadataType == MetadataType.String)
                            line($"            StringInterop.ut_nativebuffer_nativestring_placement_delete(&arg{i}arr);");
                        else
                            line($"            StringInterop.ut_nativebuffer_pod_placement_delete(&arg{i}arr);");
                    }
                }

                if (retval != null)
                {
                    line($"            return {retval};");
                }

                line($"        }}");
                line("");
            }

            line("");

            foreach(var e in type.NestedTypes.Where(x => x.IsEnum))
            {
                line($"        public enum {e.Name}");
                line("        {");
                foreach (var field in e.Fields.Where(f => f.IsStatic))
                {
                    line($"            {field.Name} = {field.Constant},");
                }
                line("        }");
            }

            line("    }");
            line("}");
        }

        string CSharpIdentifierFor(string fieldName)
        {
            if (fieldName == "checked")
                return "@checked";
            if (fieldName == "unchecked")
                return "@unchecked";
            return fieldName;
        }

        static List<string> ParameterList(MethodDefinition m, bool ignoreThis = false, bool serviceClass = false)
        {
            List<string> args = new List<string>();
            int i = 0;

            if (!m.IsStatic && !ignoreThis)
            {
                args.Add("global::" + typeof(IntPtr).FullName + " _this");
            }

            foreach (var param in m.Parameters.Select(p => p.ParameterType)) {
                var item = $"arg{i++}";
                if (param.IsDynamicArray())
                    args.Add($"global::System.{param.DynamicArrayElementType().Name}[] {item}");
                else
                    args.Add($"{param.AsCsName()} {item}");
            }

            return args;
        }

        static List<string> ParameterListForCCall(MethodDefinition m, bool ignoreThis = false,
            bool serviceClass = false)
        {
            List<string> args = new List<string>();
            int i = 0;

            if (m.ReturnType.EmCppReturnToFirstArgPtr()) {
                var retArgType = m.ReturnType.AsCsName();
                if (m.ReturnType.MetadataType == MetadataType.String)
                    retArgType = "NativeString";
                else if (m.ReturnType.IsDynamicArray())
                    retArgType = "NativeBuffer";
                args.Add($"out {retArgType} retval");
            }

            if (!m.IsStatic && !ignoreThis)
            {
                args.Add("global::System.IntPtr _this");
            }

            foreach (var param in m.Parameters.Select(p => p.ParameterType)) {
                var item = $"arg{i++}";

                if (param.MetadataType == MetadataType.String) {
                    args.Add($"NativeString* {item}");
                } else if (param.IsDynamicArray()) {
                    args.Add($"NativeBuffer* {item}");
                } else if (param.IsSharedPtrType() || param.IsNonSharedPtrType()) {
                    args.Add($"global::{typeof(IntPtr).FullName} {item}");
                } else if (param.Resolve().IsComponentTypeId()) {
                    if (param.IsPointer) {
                        args.Add($"uint* {item}");
                    } else {
                        args.Add($"uint {item}");
                    }
                } else if (param.IsStructValueType() || param.IsComponentType()) {
                    args.Add($"ref {param.AsCsName()} {item}");
                } else {
                    args.Add($"{param.AsCsName()} {item}");
                }
            }

            return args;
        }

        static List<string> ParameterNamesForCCall(MethodDefinition m, bool ignoreThis = false, bool forceIgnoreThis = false, bool addWorld = false)
        {
            List<string> args = new List<string>();
            int i = 0;

            if (m.ReturnType.EmCppReturnToFirstArgPtr()) {
                args.Add($"out retval");
            }

            if (!m.IsStatic && !ignoreThis)
            {
                args.Add("(global::System.IntPtr)self");
            }
            else if (!forceIgnoreThis && !m.IsStatic)
            {
                args.Add("mThis");
            }

            if (addWorld)
            {
                args.Add("world.BarePtr");
            }

            foreach(var p in m.Parameters) {
                var param = p.ParameterType;
                var item = $"arg{i++}";
                if (param.MetadataType == MetadataType.String) {
                    args.Add($"&{item}str");
                } else if (param.IsDynamicArray()) {
                    args.Add($"&{item}arr");
                } else if (param.IsSharedPtrType() || param.IsNonSharedPtrType()) {
                    args.Add($"{item}.mThis");
                } else if (param.IsEntityIdType()) {
                    args.Add($"{item}.eid");
                } else if (param.Resolve().IsComponentTypeId()) {
                    if (param.IsPointer) {
                        args.Add($"(uint*){item}");
                    } else {
                        args.Add($"{item}.cid");
                    }
                } else if (param.IsStructValueType() && !param.IsPointer) {
                    args.Add($"ref {item}");
                } else {
                    args.Add(item);
                }
            }

            return args;
        }

        static List<string> UnnamedParameterList(IEnumerable<ParameterDefinition> parameters)
        {
            List<string> args = new List<string>();
            int i = 0;
            foreach(var p in parameters)
            {
                args.Add(p.ParameterType.EmCppArgType() + " arg" + i++);
            }
            return args;
        }
    }


    static internal class ExtensionMethods
    {
        internal static string AsCsReturnName(this TypeReference typeRef)
        {
            if (typeRef.EmCppReturnToFirstArgPtr())
                return "void";
            if (typeRef.IsSharedPtrType() || typeRef.IsNonSharedPtrType())
                return "global::System.IntPtr";

            return GetHumanReadableTypeName(typeRef);
        }

        internal static string AsCsName(this TypeReference typeRef)
        {
            return GetHumanReadableTypeName(typeRef);
        }

        internal static string JoinWithComma(this IEnumerable<string> values)
        {
            return string.Join(", ", values);
        }

        public static string GetHumanReadableTypeName(TypeReference type)
        {
            if (type.IsPointer) {
                return GetHumanReadableTypeName(type.GetElementType()) + "*";
            }

            string name = type.CSFullName();
            switch (type.MetadataType) {
                case MetadataType.Void:
                    name = "void";
                    break;
                case MetadataType.Boolean:
                    name = "bool";
                    break;
                case MetadataType.Char:
                    name = "char";
                    break;
                case MetadataType.SByte:
                    name = "sbyte";
                    break;
                case MetadataType.Byte:
                    name = "byte";
                    break;
                case MetadataType.Int16:
                    name = "short";
                    break;
                case MetadataType.UInt16:
                    name = "ushort";
                    break;
                case MetadataType.Int32:
                    name = "int";
                    break;
                case MetadataType.UInt32:
                    name = "uint";
                    break;
                case MetadataType.Int64:
                    name = "long";
                    break;
                case MetadataType.UInt64:
                    name = "ulong";
                    break;
                case MetadataType.Single:
                    name = "float";
                    break;
                case MetadataType.Double:
                    name = "double";
                    break;
            }

            if (type.Resolve().HasAttribute("CsName")) {
                return type.Resolve().GetValueOfAttribute("CsName");
            }

            /*if (Nullable.GetUnderlyingType(type) != null && simplify)
            {
                return GetHumanReadableTypeName(Nullable.GetUnderlyingType(type), simplify, expose_template) + "?";
            }
            else*/
            if (type.IsGenericInstance) {
                var fullName = type.CSFullName();
                string typeName = fullName.Substring(0, fullName.IndexOf('`'));

                typeName = string.Format("{0}<{1}>", typeName,
                        ((GenericInstanceType)type).GenericArguments.Count == 0 ? "" : ((GenericInstanceType)type).GenericArguments
                        .Select(x => GetHumanReadableTypeName(x))
                        .Aggregate((x, y) => string.Format("{0}, {1}", x, y)));

                return typeName;
            }

            name = name.Replace('/', '.');

            if (name.StartsWith("System"))
            {
                name = "global::" + name;
            }

            if (type.IsGenericParameter)
            {
                return name;
            }

            return name;
        }
    }

    internal static class CSharpExtensionMethods
    {
        public static string CsName(this TypeReference type)
        {
            var rtype = type.Resolve();
            if (rtype.HasAttribute("CsName"))
                return rtype.GetValueOfAttribute("CsName");
            return type.Name;
        }

        public static string CSFullName(this TypeReference self)
        {
            var prefix = "";
            var name = "";
            if (!string.IsNullOrEmpty(self.Namespace))
                prefix = self.Namespace + ".";
            name = prefix + self.CsName();
            if (self.IsNested)
                name = self.DeclaringType.CSFullName() + "/" + name;
            return name;
        }
    }
}
