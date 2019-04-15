using System;
using System.Linq;
using System.Text;
using ExtensionMethods;
using Mono.Cecil;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    internal static class TSHelpers
    {
        public static string TSName(this TypeDefinition type)
        {
            return type.GetValueOfAttribute("TSName") ?? type.Name;
        }

        public static string TSNamespaceName(this TypeDefinition type)
        {
            return type.FullyQualifiedNamespaceName(".");
        }

        public static string TSFullyQualifiedName(this TypeDefinition type)
        {
            /*
            // note Name here -- if something has a TSName that's expected to
            // be a name so that we can inject additional declarations in a subclass
            // with the actual name
            return type.TSNamespaceName() + "." + type.Name;
            */
            // The above doesn't hold true if the real Name won't be defined until later.
            // We'll deal with having SchedulerBase/WorldBase etc as params.
            return type.TSNamespaceName() + "." + type.TSName();
        }
    }

    internal class TypeScriptDefinitionGenerator : ILanguageHandler
    {
        private StringBuilder tsStr = new StringBuilder();

        void ts(string s)
        {
            tsStr.AppendLine(s);
        }

        void tsNs(TypeDefinition td, string s)
        {
            tsStr.AppendLine($"declare namespace {td.TSNamespaceName()} {{");
            tsStr.AppendLine(s);
            tsStr.AppendLine($"}}");
        }

        private static string InnerTypeFor(TypeReference type)
        {
            var rtype = type.Resolve();

            if (rtype.HasAttribute("TSName")) {
                return rtype.TSFullyQualifiedName();
            }

            switch (rtype.MetadataType) {
                case MetadataType.Boolean:
                    return "boolean";
                case MetadataType.SByte:
                case MetadataType.Byte:
                case MetadataType.Int16:
                case MetadataType.UInt16:
                case MetadataType.Int32:
                case MetadataType.UInt32:
                case MetadataType.Int64:
                case MetadataType.UInt64:
                    return "number";
                case MetadataType.Single:
                case MetadataType.Double:
                    return "number";
                case MetadataType.Char:
                case MetadataType.IntPtr:
                case MetadataType.UIntPtr:
                case MetadataType.String:
                    return "string";
                case MetadataType.Void:
                    return "void";
            }

            return rtype.TSName();
        }

        private string TypeFor(TypeReference type)
        {
            var rtype = type.Resolve();
            if (rtype.IsDynamicArray()) {
                return InnerTypeFor(type.DynamicArrayElementType()) + "[]";
            }

            return InnerTypeFor(type);
        }

        private string ParamDeclFor(ParameterDefinition p)
        {
            var maybeOptional = p.HasDefault ? "?" : "";
            return $"{p.Name}{maybeOptional}: {TypeFor(p.ParameterType)}";
        }

        private string MethodTypeDeclarationFor(MethodDefinition m)
        {
            var args = m.Parameters.Select(ParamDeclFor).ToArray();
            var rtype = TypeFor(m.ReturnType);

            return
                $"({string.Join(", ", args)}) => {rtype}";
        }

        private string MethodDeclarationFor(MethodDefinition m)
        {
            var maybeStatic = (m.IsStatic || m.DeclaringType.IsServiceRefType()) ? "static " : "";
            var name = m.Name.WithLowercaseFirstLetter();
            var args = m.Parameters.Select(ParamDeclFor).ToArray();
            var rtype = TypeFor(m.ReturnType);

            return
                $"{m.JSDocComment()}{maybeStatic}{name}({String.Join(", ", args)}): {rtype};";
        }

        public void HandleInterfaceType(TypeDefinition type)
        {
            var tmpl =
                @"{{{doc}}}class {{{class}}} {
  {{{methods}}}
}";
            var methods = type.MemberFunctions("JSHide").Select(MethodDeclarationFor).ToArray();
            tsNs(type, CppBindingsGenerator.ExpandStringTemplate(tmpl,
                "doc", type.JSDocComment(),
                "class", type.TSName(),
                "methods", methods));
        }

        public void HandleCallbackType(TypeDefinition type)
        {
            var tmpl =
@"{{{doc}}}type {{{cb}}} = {{{cbproto}}};";

            var name = type.TSName();
            var cbproto = MethodTypeDeclarationFor(type.DelegateInvokeMethod());
            tsNs(type, CppBindingsGenerator.ExpandStringTemplate(tmpl,
                "doc", type.JSDocComment(),
                "cb", name,
                "cbproto", cbproto));
        }

        string StructFieldDeclFor(FieldDefinition field)
        {
            return $"{field.JSDocComment()}{field.Name}: {TypeFor(field.FieldType)};";
        }

        bool IsTweenOrWatchable(FieldDefinition field)
        {
            TypeDefinition type = field.FieldType.Resolve();
            if (type.MetadataType == MetadataType.Single) return true;
            if (type.MetadataType == MetadataType.Boolean) return true;
            if (type.MetadataType == MetadataType.Int32) return true;
            if (field.FieldType.JavaScriptSpecialType() == JSSpecialType.Quaternion) return true;
            if (field.FieldType.JavaScriptSpecialType() == JSSpecialType.Vector2) return true;
            if (field.FieldType.JavaScriptSpecialType() == JSSpecialType.Vector3) return true;
            if (field.FieldType.JavaScriptSpecialType() == JSSpecialType.Vector4) return true;
            return false;
        }

        string StaticStructFieldDeclFor(FieldDefinition field)
        {
            if (field.FieldType.IsStructValueType())
                return $"static readonly {field.Name}: {TypeFor(field.FieldType)}ComponentFieldDesc;";
            else if (IsTweenOrWatchable(field))
                return $"static readonly {field.Name}: ComponentFieldDesc;";
            else
                return "";
        }

        string ConstructorForValueType(TypeDefinition type)
        {
            var sb = new StringBuilder();
            sb.Append("constructor(");
            sb.Append(string.Join(", ", type.Fields.Select(field => $"{field.Name}?: {TypeFor(field.FieldType)}")));
            sb.Append(");");
            return sb.ToString();
        }

        public void HandleStructType(TypeDefinition type)
        {
            if (type.HasAttribute("JSCustomImpl")) {
                string aliasName = null;
                switch (type.FullName) {
                    case "UTiny.Math.Vector2": aliasName = "utmath.Vector2"; break;
                    case "UTiny.Math.Vector3": aliasName = "utmath.Vector3"; break;
                    case "UTiny.Math.Vector4": aliasName = "utmath.Vector4"; break;
                    case "UTiny.Math.Quaternion": aliasName = "utmath.Quaternion"; break;
                    case "UTiny.Math.Matrix3x3": aliasName = "utmath.Matrix3"; break;
                    case "UTiny.Math.Matrix4x4": aliasName = "utmath.Matrix4"; break;
                    default:
                        throw new InvalidOperationException($"Don't know how to generate TypeScript custom def for {type.FullName}");
                }
                tsNs(type, $"export import {type.TSName()} = {aliasName};");
                return;
            }

            var tmpl =
@"
{{{doc}}}class {{{class}}} {{{extends}}}{
  {{{constructor}}}
  {{{fields}}}
  {{{methods}}}

  static _size: number;
  static _fromPtr(p: number, v?: {{{class}}}): {{{class}}};
  static _toPtr(p: number, v: {{{class}}}): void;
  static _tempHeapPtr(v: {{{class}}}): number;
}
interface {{{class}}}ComponentFieldDesc extends ut.ComponentFieldDesc {
  {{{staticfields}}}
}
";

            var constructor = ConstructorForValueType(type);
            var extends = type.GetValueOfAttribute("TSExtends");
            var fields = type.Fields.Select(StructFieldDeclFor).ToArray();
            var staticfields = type.Fields.Select(StaticStructFieldDeclFor).ToArray();
            var methods = type.MemberFunctions("JSHide").Select(MethodDeclarationFor).ToArray();
            tsNs(type, CppBindingsGenerator.ExpandStringTemplate(tmpl,
                "doc", type.JSDocComment(),
                "constructor", constructor,
                "class", type.TSName(),
                "extends", extends != null ? $"extends {extends} " : "",
                "fields", fields,
                "methods", methods,
                "staticfields", staticfields));
        }

        public void HandleComponentType(TypeDefinition type)
        {
            var tmpl =
@"
{{{doc}}}class {{{class}}} extends ut.Component {
  {{{constructor}}}
  {{{fields}}}
  {{{staticfields}}}
  {{{methods}}}

  static readonly cid: number;
  static readonly _view: any;
  static readonly _isSharedComp: boolean;

  static _size: number;
  static _fromPtr(p: number, v?: {{{class}}}): {{{class}}};
  static _toPtr(p: number, v: {{{class}}}): void;
  static _tempHeapPtr(v: {{{class}}}): number;
  static _dtorFn(v: {{{class}}}): void;
}
";

            var constructor = ConstructorForValueType(type);
            var fields = type.Fields.Select(StructFieldDeclFor).ToArray();
            var staticfields = type.Fields.Select(StaticStructFieldDeclFor).ToArray();
            var methods = type.MemberFunctions("JSHide").Select(MethodDeclarationFor).ToArray();
            tsNs(type, CppBindingsGenerator.ExpandStringTemplate(tmpl,
                "doc", type.JSDocComment(),
                "constructor", constructor,
                "class", type.TSName(),
                "fields", fields,
                "methods", methods,
                "staticfields", staticfields));
        }

        public void HandleEnumType(TypeDefinition type)
        {
            var tmpl =
                @"{{{doc}}}enum {{{enum}}} {
  {{{values}}}
}";

            var values = type.GetEnumKeyValues().Select(m => $"{m.documentation}{m.key} = {m.value},").ToArray();
            tsNs(type, CppBindingsGenerator.ExpandStringTemplate(tmpl,
                "doc", type.JSDocComment(),
                "enum", type.TSName(),
                "values", values));
        }

        public void HandleSystemType(TypeDefinition type)
        {
            // non-PureJS systems are all implemented in native code
            if (BindGem.PureJS) {
                tsNs(type, $"{type.JSDocComment()}var {type.TSName()}: ut.SystemJS;");
            } else {
                tsNs(type, $"{type.JSDocComment()}var {type.TSName()}: ut.System;");
            }
        }

        public string GetGeneratedSource(SourceLanguage lang)
        {
            return lang != SourceLanguage.TypeScriptDefinition ? string.Empty : tsStr.ToString();
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            if (lang != SourceLanguage.TypeScriptDefinition)
                return 0;
            return 100;
        }
    }
}
