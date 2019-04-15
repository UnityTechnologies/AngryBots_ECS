using ExtensionMethods;
using Mono.Cecil;
using System;
using System.Text;
using System.Linq;
using Unity.Entities.BuildUtils;

namespace BindGem
{
    internal class JSDocGenerator : ILanguageHandler
    {
        internal StringBuilder docStr = new StringBuilder();

        void doc(string s)
        {
            docStr.AppendLine(s);
        }

        void docName(TypeDefinition type)
        {
            if (type.DeclaringType != null)
                doc($"@memberof {type.DeclaringType.FullyQualifiedCppName(".")}");
            else if (type.Namespace != null && type.Namespace.Length > 0)
                doc($"@memberof {type.FullyQualifiedNamespaceName(".")}");
            doc($"@name {type.Name}");
        }

        void docName(FieldDefinition field)
        {
            doc($"@memberof {field.DeclaringType.FullyQualifiedCppName(".")}");
            doc($"@name {field.Name}");
        }

        void docName(MethodDefinition method)
        {
            doc($"@memberof {method.DeclaringType.FullyQualifiedCppName(".")}");
            if (!method.IsStatic)
                doc("@instance");
            doc($"@name {method.Name.WithLowercaseFirstLetter()}");
        }

        internal void HandleField(FieldDefinition field)
        {
            doc("/**");
            doc(field.JSDocComment());
            doc($"@member {{{field.FieldType.DocNamepath()}}}");
            docName(field);
            doc("*/");
        }

        internal void HandleMethod(MethodDefinition fn)
        {
            doc("/**");
            var content = fn.JSDocComment();
            doc(content);
            if (content.IndexOf("@param") == -1)
            {
                // no @param definitions in comments; include them manually
                foreach (var p in fn.Parameters)
                {
                    doc($"@param {{{p.DocNamepath()}}} {p.Name}");
                }
            }
            if (content.IndexOf("@returns") == -1 && fn.ReturnType.MetadataType != MetadataType.Void)
            {
                doc($"@returns {{{fn.ReturnType.DocNamepath()}}}");
            }
            doc($"@function");
            docName(fn);
            doc("*/");
        }

        public void HandleInterfaceType(TypeDefinition type)
        {
            doc("/**");
            doc(type.JSDocComment());
            if (type.IsSharedPtrType())
            {
                doc("@systemtype");
            }
            else if (type.IsServiceRefType())
            {
                doc("@service");
            }
            docName(type);
            doc("*/");

            if (type.HasAttribute("Constructable"))
            {
                var fqn = type.FullyQualifiedCppName(".");
                doc("/**");
                doc("Construct this type.");
                doc($"@memberof {fqn}#");
                doc($"@name {type.Name}");
                doc("*/");
            }

            foreach (var fn in type.MemberFunctions())
            {
                HandleMethod(fn);
            }
        }

        public void HandleCallbackType(TypeDefinition type)
        {
            // NO-OP
        }


        public void HandleStructType(TypeDefinition type)
        {
            doc("/**");
            doc(type.JSDocComment());
            doc("@struct");
            docName(type);
            doc("*/");

            foreach (var field in type.Fields)
            {
                HandleField(field);
            }

            foreach (var fn in type.MemberFunctions())
            {
                HandleMethod(fn);
            }
        }

        public void HandleComponentType(TypeDefinition type)
        {
            doc("/**");
            doc(type.JSDocComment());
            doc("@component");
            docName(type);
            doc("*/");

            foreach (var field in type.Fields)
            {
                HandleField(field);
            }

            foreach (var fn in type.MemberFunctions())
            {
                HandleMethod(fn);
            }
        }

        public void HandleEnumType(TypeDefinition type)
        {
            doc("/**");
            doc(type.JSDocComment());
            doc("@struct");
//            doc("@enum"); // JSDoc does have @enum keyword, but currently enums are generated as @structs for some reason - @enums don't show up. TODO: Review if this is intentional, and why?
            docName(type);
            doc("*/");

            foreach (var field in type.Fields)
            {
                if (field.FieldType.Resolve() != type) continue;
                HandleField(field);
            }
        }

        public void HandleSystemType(TypeDefinition type)
        {
            doc("/**");
            doc(type.JSDocComment());
            doc("@system");
            docName(type);
            doc("*/");
        }

        public string GetGeneratedSource(SourceLanguage lang)
        {
            if (lang != SourceLanguage.JSDoc)
                return String.Empty;
            return docStr.ToString();
        }

        public int GeneratedSourceWeight(SourceLanguage lang)
        {
            if (lang != SourceLanguage.JSDoc)
                return 0;
            return 100;
        }
    }

    internal static class DocHelpers
    {
        internal static string TypedName(this TypeReference field)
        {
            var simpleType = field.Resolve().SimpleSpecialType();
            if (simpleType != null)
                return simpleType;

            string name = StripGenericTypeSpecifier(field.Name);
            if (field.IsGenericInstance)
            {
                GenericInstanceType genericInstance = (GenericInstanceType)field;
                return '<' + string.Join(", ", genericInstance.GenericArguments.Select(x => GenericTypeName(x))) + '>';
            }

            return name;
        }

        internal static string GenericTypeName(TypeReference typeReference)
        {
            var simpleGenericType = SimpleSpecialType(typeReference.Resolve());
            if (simpleGenericType != null)
                return simpleGenericType;
            else
                return typeReference.FullyQualifiedCppName(".");
        }

        internal static string JSDocComment(this MethodDefinition func)
        {
            string xmlDocKey = "M:" + func.DeclaringType + "." + func.Name;
            string args = "";
            foreach(var param in func.Parameters)
            {
                if (args.Length > 0) args += ",";
                var paramType = param.ParameterType;
                if (paramType.IsGenericInstance)
                {
                    GenericInstanceType genericInstance = (GenericInstanceType)paramType;
                    if (genericInstance.GenericArguments.Count > 1)
                        throw new Exception("Multiple generic arguments in a single type not yet supported for jsdocs generation!"); // TODO
                    args += paramType.FullyQualifiedCSharpName(".").Split('`')[0] + "{" + genericInstance.GenericArguments[0].FullyQualifiedCSharpName(".") + "}";
                }
                else
                    args += paramType.ToString();
            }
            if (args.Length > 0)
                xmlDocKey += "(" + args + ")";
            if (InterfaceReflector.generatedCodeDocumentationXmlElements.ContainsKey(xmlDocKey))
                return InterfaceReflector.generatedCodeDocumentationXmlElements[xmlDocKey];
            return "";
        }

        internal static string JSDocComment(this FieldDefinition field)
        {
            string xmlDocKey = "";
            xmlDocKey = "F:" + field.DeclaringType + "." + field.Name;

            if (InterfaceReflector.generatedCodeDocumentationXmlElements.ContainsKey(xmlDocKey))
                return InterfaceReflector.generatedCodeDocumentationXmlElements[xmlDocKey];
            return "";
        }

        internal static string JSDocComment(this TypeDefinition type)
        {
            string xmlDocKey = "";
            if (type.DeclaringType != null)
                xmlDocKey = "T:" + type.DeclaringType.FullyQualifiedCSharpName(".") + "." + type.Name;
            else
                xmlDocKey = "T:" + type.FullyQualifiedCSharpName(".");

            if (InterfaceReflector.generatedCodeDocumentationXmlElements.ContainsKey(xmlDocKey))
                return InterfaceReflector.generatedCodeDocumentationXmlElements[xmlDocKey];
            return "";
        }

        internal static string SimpleSpecialType(this TypeDefinition type)
        {
            if (type.MetadataType == MetadataType.Boolean) return "bool";
            if (type.MetadataType == MetadataType.SByte) return "int8";
            if (type.MetadataType == MetadataType.Byte) return "uint8";
            if (type.MetadataType == MetadataType.Int16) return "int16";
            if (type.MetadataType == MetadataType.UInt16) return "uint16";
            if (type.MetadataType == MetadataType.Char) return "uint16";
            if (type.MetadataType == MetadataType.Int32) return "int32";
            if (type.MetadataType == MetadataType.UInt32) return "uint32";
            if (type.MetadataType == MetadataType.Int64) return "int64";
            if (type.MetadataType == MetadataType.UInt64) return "uint64";
            if (type.MetadataType == MetadataType.Single) return "float";
            if (type.MetadataType == MetadataType.Double) return "double";
            if (type.MetadataType == MetadataType.String) return "string";
            return null;
        }

        static string StripGenericTypeSpecifier(string name)
        {
            return name.Split('`')[0];
        }

        internal static string DocNamepath(this TypeReference typeRef)
        {
            var simpleType = typeRef.Resolve().SimpleSpecialType();
            if (simpleType != null)
                return simpleType;
            if (typeRef.DeclaringType != null)
                return typeRef.DeclaringType.FullyQualifiedCppName(".") + "#" + TypedName(typeRef);
            else
            {
                var ns = typeRef.FullyQualifiedNamespaceName(".");
                if (ns.Length > 0) ns += '.';
                return ns + TypedName(typeRef);
            }
        }

        internal static string DocNamepath(this ParameterDefinition param)
        {
            var simpleType = param.ParameterType.Resolve().SimpleSpecialType();
            if (simpleType != null)
                return simpleType;
            var ns = param.ParameterType.FullyQualifiedNamespaceName(".");
            if (ns.Length > 0) ns += '.';
            return ns + TypedName(param.ParameterType);
        }
    }
}
