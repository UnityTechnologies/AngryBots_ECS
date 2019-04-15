using BindGem;
using ExtensionMethods;
using Unity.Entities.BuildUtils;
using Microsoft.CSharp;
using Mono.Cecil;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ExtensionMethods
{
    public static class TypeExtensions
    {
        public static bool HasAttribute(this TypeDefinition type, string attribute)
        {
            attribute += "Attribute";
            return type.HasCustomAttributes && type.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name == attribute) != null;
        }

        public static bool HasAttribute(this MethodDefinition function, string attribute)
        {
            attribute += "Attribute";
            return function.HasCustomAttributes && function.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name == attribute) != null;
        }

        public static string GetValueOfAttribute(this TypeDefinition type, string attribute)
        {
            if (!type.HasCustomAttributes) return null;
            attribute += "Attribute";
            return type.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name == attribute)?.ConstructorArguments[0].Value.ToString();
        }

        public static string GetValueOfAttribute(this MethodDefinition function, string attribute)
        {
            if (!function.HasCustomAttributes) return null;
            attribute += "Attribute";
            return function.CustomAttributes.FirstOrDefault(attr => attr.AttributeType.Name == attribute)?.ConstructorArguments[0].Value.ToString();
        }

        // http://mikehadlow.blogspot.com/2010/03/how-to-tell-if-type-is-delegate.html
        public static bool IsDelegate(this TypeDefinition type)
        {
            return type.BaseType != null && type.BaseType.FullName == "System.MulticastDelegate";
        }

        internal static bool IsInGlobalNamespace(this TypeDefinition type)
        {
            return type.CppNamespace() == null || type.CppNamespace().Length == 0;
        }

        internal static string CppBasicType(this TypeDefinition type)
        {
            if (type.MetadataType == MetadataType.Boolean) return "bool";
            if (type.MetadataType == MetadataType.SByte) return "int8_t";
            if (type.MetadataType == MetadataType.Byte) return "uint8_t";
            if (type.MetadataType == MetadataType.Int16) return "int16_t";
            if (type.MetadataType == MetadataType.UInt16) return "uint16_t";
            if (type.MetadataType == MetadataType.Char) return "uint16_t";
            if (type.MetadataType == MetadataType.Int32) return "int32_t";
            if (type.MetadataType == MetadataType.UInt32) return "uint32_t";
            if (type.MetadataType == MetadataType.Int64) return "int64_t";
            if (type.MetadataType == MetadataType.UInt64) return "uint64_t";
            if (type.MetadataType == MetadataType.Single) return "float";
            if (type.MetadataType == MetadataType.Double) return "double";
            if (type.MetadataType == MetadataType.IntPtr) return "intptr_t";
            if (type.MetadataType == MetadataType.UIntPtr) return "uintptr_t";
            if (type.MetadataType == MetadataType.Void) return "void";
            return null;
        }

        // Returns a list of all base interfaces this type implements, including its own.
        internal static List<TypeReference> AllInterfaces(this TypeDefinition type)
        {
            var allInterfaces = new List<TypeReference>();
            allInterfaces.AddRange(type.Interfaces.Select(t => t.InterfaceType));
            return allInterfaces;
        }

        internal static List<MethodDefinition> MemberFunctions(this TypeDefinition type, string ignoreAttribute = null)
        {
            List<MethodDefinition> methods = new List<MethodDefinition>();
            foreach (var member in type.Methods)
            {
                if (ignoreAttribute != null && member.HasAttribute(ignoreAttribute))
                    continue;
                if (member.IsConstructor)
                    continue;
                methods.Add(member);
            }
            foreach (var iface in type.AllInterfaces())
            {
                var r = iface.Resolve();
                if (ignoreAttribute != null && r.HasAttribute(ignoreAttribute))
                    continue;
                foreach (var member in r.Methods)
                {
                    if (ignoreAttribute != null && member.HasAttribute(ignoreAttribute))
                        continue;
                    if (member.IsConstructor)
                        continue;
                    methods.Add(member);
                }
            }
            return methods;
        }

        internal static List<MethodDefinition> Constructors(this TypeDefinition type)
        {
            List<MethodDefinition> methods = new List<MethodDefinition>();
            foreach (var member in type.Methods)
            {
                if (member.IsConstructor)
                    methods.Add(member);
            }
            return methods;
        }

        internal static MethodDefinition DelegateInvokeMethod(this TypeDefinition type)
        {
            if (!type.IsDelegate())
                throw new Exception("type.DelegateInvokeMethod() can only be called on a type that is a delegate!");

            foreach(var memberFunction in type.MemberFunctions())
            {
                if (memberFunction.Name == "Invoke")
                    return memberFunction;
            }

            throw new Exception("Internal error: type.DelegateInvokeMethod() called on a delegate, but its Invoke() function was not found!");
        }

        internal static string CppNamespace(this TypeReference type)
        {
            if (type.DeclaringType != null) return type.DeclaringType.CppNamespace();
            return type.Namespace;
        }

        internal static string CppName(this TypeDefinition type)
        {
            var name = type.GetValueOfAttribute("CppName");
            if (name != null)
                return name;
            return type.FullyQualifiedCppName();
        }

        internal static string CppFieldType(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            var basicType = type.CppBasicType();
            if (basicType != null)
                return basicType;

            if (type.IsSharedPtrType())
                return $"std::shared_ptr<{type.CppName()}>";

            if (type.MetadataType == MetadataType.String)
            {
                if (BindGem.BindGem.DOTS) {
                    return $"/* TODO NATIVE STRING */ intptr_t";
                } else {
                    return $"ut::NativeString";
                }
            }

            if (type.IsComponentTypeId())
                return $"uint32_t";

            if (type.IsEntityIdType())
                return $"intptr_t";

            if (type.IsDynamicArray())
            {
                var elementType = typeRef.DynamicArrayElementType();
                if (BindGem.BindGem.DOTS) {
                    return $"/* TODO DYNAMIC ARRAY OF {elementType.CppFieldType()} */ intptr_t";
                } else {
                    return $"ut::NativeBuffer<{elementType.CppFieldType()}>";
                }
            }

            if (type.IsStructValueType() || type.IsComponentType() || type.IsEnum)
                return type.CppName();

            BindGem.BindGem.FatalError($"Don't know how to make a C++ type from {type}");
            return null;
        }


        internal static bool IsStructValueType(this TypeDefinition type)
        {
            if (type.FixedSpecialType() != null)
                return false;
            if (!type.IsValueType)
                return false;
            if (type.IsComponentType())
                return false;
            if (type.IsEntityIdType() || type.IsComponentTypeId() || type.MetadataType == MetadataType.IntPtr)
                return false;
            return true;
        }

        internal static bool IsEntityType(this TypeReference typeRef)
        {
            return (typeRef.FullName == "UTiny.Entity");
        }

        internal static bool IsSharedPtrType(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            return type.HasAttribute("SharedPtr");
        }

        internal static bool IsNonSharedPtrType(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            return type.HasAttribute("NonSharedPtr");
        }

        internal static bool IsServiceRefType(this TypeReference typeRef)
        {
            TypeDefinition type = typeRef.Resolve();
            return type.HasAttribute("Service");
        }

/*
        internal static bool IsComponentTypeId(this TypeDefinition type)
        {
            // SIMPLIFY
            return type == InterfaceReflector.ComponentTypeId;
        }
        internal static bool IsEntityIdType(this TypeDefinition type)
        {
            // SIMPLIFY
            return type == InterfaceReflector.EntityId;
        }
        */

        public static Type GetSystemReflectionType(this TypeReference type)
        {
            return Type.GetType(type.GetReflectionName(), true);
        }

        private static string GetReflectionName(this TypeReference type)
        {
            if (type.IsGenericInstance)
            {
                var genericInstance = (GenericInstanceType)type;
                return string.Format("{0}.{1}[{2}]", genericInstance.Namespace, type.Name, String.Join(",", genericInstance.GenericArguments.Select(p => p.GetReflectionName()).ToArray()));
            }
            return type.FullName;
        }

        public static TypeDefinition GetCecilType(this Type type)
        {
            foreach(var t in InterfaceReflector.bindGem.MainModule.Types)
            {
                if (t.Name == type.Name)
                    return t;
            }
            return null;

        }

        // Given a type Foo.Bar.TypeDefinition, returns its namespace-qualified name in C++, with
        // the namespace elements separated with the given separator string. Also
        // renames the top level namespace name to the one used in C++ side.
        // e.g. FullyQualifiedCppName("UTiny.Foo.Bar", "::") -> "ut::Foo::Bar",
        //      FullyQualifiedCppName("UTiny.Foo.Bar", "_") -> "ut_Foo_Bar",
        internal static string FullyQualifiedCppName(this TypeReference type, string separator = "::")
        {
            var fullName = type.CppNamespace().Split('.').ToList();
            fullName[0] = BindGem.BindGem.TranslateCppTopLevelNamespace(fullName[0]);
            if (fullName[0].Length == 0) fullName[0] = type.Name;
            else fullName.Add(type.Name);
            return string.Join(separator, fullName);
        }

        internal static string FullyQualifiedCSharpName(this TypeReference type, string separator = ".")
        {
            var fullName = type.CppNamespace().Split('.').ToList();
            if (fullName[0].Length == 0) fullName[0] = type.Name;
            else fullName.Add(type.Name);
            return string.Join(separator, fullName);
        }

        internal static string FullyQualifiedNamespaceName(this TypeReference type, string separator = "::")
        {
            if (type.CppNamespace() == null || type.CppNamespace().Length == 0) return "";
            var fullName = type.CppNamespace().Split('.');
            fullName[0] = BindGem.BindGem.TranslateCppTopLevelNamespace(fullName[0]);
            return string.Join(separator, fullName);
        }

        internal static int GetEnumSize(this TypeDefinition enumType)
        {
            var type = enumType.Fields.First(f => f.Name == "value__").FieldType;
            if (type.MetadataType == MetadataType.Byte)
                return 8;
            if (type.MetadataType == MetadataType.Int16)
                return 16;
            if (type.MetadataType == MetadataType.Int32)
                return 32;
            if (type.MetadataType == MetadataType.Int64)
                return 64;

            throw new InvalidOperationException($"Unhandled enum base type {type}");
        }

        public struct EnumKeyValue
        {
            public string documentation;
            public string key;
            public long value;
        }

        internal static List<EnumKeyValue> GetEnumKeyValues(this TypeDefinition enumType)
        {
            List<EnumKeyValue> enumKeyValues = new List<EnumKeyValue>();
            int enumSize = enumType.GetEnumSize();

            foreach (var enumName in enumType.Fields)
            {
                if (enumName.FieldType.Resolve() != enumType) continue;

                long val = 0;
                if (enumSize == 8)
                    val = (long)(System.Byte)enumName.Constant;
                else if (enumSize == 16)
                    val = (long)(System.Int16)enumName.Constant;
                else if (enumSize == 32)
                    val = (long)(System.Int32)enumName.Constant;
                else if (enumSize == 64)
                    val = (long)(System.Int64)enumName.Constant;

                enumKeyValues.Add(new EnumKeyValue
                {
                    documentation = enumName.JSDocComment(),
                    key = enumName.Name,
                    value = val
                });
            }
            return enumKeyValues;
        }
    }
}

namespace BindGem
{
    public class CecilAssemblyResolver : BaseAssemblyResolver
    {
        public readonly IDictionary<string, AssemblyDefinition> cache;

        public CecilAssemblyResolver()
        {
            cache = new Dictionary<string, AssemblyDefinition>(StringComparer.Ordinal);
        }

        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (name.Name.ToLower() == "bindgem")
                return InterfaceReflector.bindGem;
            AssemblyDefinition assembly;
            if (cache.TryGetValue(name.FullName, out assembly))
                return assembly;
            assembly = base.Resolve(name);
            cache[name.FullName] = assembly;
            return assembly;
        }

        protected void RegisterAssembly(AssemblyDefinition assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException("assembly");
            var name = assembly.Name.FullName;
            if (cache.ContainsKey(name))
                return;
            cache[name] = assembly;
        }

        protected override void Dispose(bool disposing)
        {
            foreach (var assembly in cache.Values)
                assembly.Dispose();
            cache.Clear();
            base.Dispose(disposing);
        }
    }

    // InterfaceReflector can be used to introspect contents of Tiny Unity C# .cs IDL files.
    internal class InterfaceReflector
    {
        public static AssemblyDefinition bindGem;
        public static Dictionary<string, string> generatedCodeDocumentationXmlElements = new Dictionary<string, string>();

        private AssemblyDefinition dependenciesAssembly;
        private AssemblyDefinition compiledAssembly;

        public List<TypeDefinition> Structs;
        public List<TypeDefinition> Enums;
        public List<TypeDefinition> Interfaces;
        public List<TypeDefinition> Delegates;
        public List<TypeDefinition> Classes;

        public List<TypeDefinition> ExportedStructs;
        public List<TypeDefinition> ExportedEnums;
        public List<TypeDefinition> ExportedInterfaces;
        public List<TypeDefinition> ExportedDelegates;
        public List<TypeDefinition> ExportedClasses;

        private InterfaceReflector()
        {
        }

        public static InterfaceReflector FromCSharpSource(List<string> sourceFiles, List<string> referenceSourceFiles, List<string> referenceDllFiles, bool referenceBindGem)
        {
            var ir = new InterfaceReflector();
            ir.CompileCSharpSource(sourceFiles, referenceSourceFiles, referenceDllFiles, referenceBindGem);
            return ir;
        }

        public static InterfaceReflector FromAssembly(string assembly, List<string> referenceAssemblies)
        {
            var ir = new InterfaceReflector();
            ir.LoadFromAssembly(assembly, referenceAssemblies);
            return ir;
        }

        delegate bool TypeFilter(TypeDefinition type);

        private static List<TypeDefinition> GetStructs(AssemblyDefinition assembly, TypeFilter filter = null)
        {
            if (filter == null)
                filter = (type) => true;
            var structs = new List<TypeDefinition>();
            foreach (var module in assembly.Modules)
                structs.AddRange(module.GetTypes().Where(type => type.IsValueType && !type.IsEnum).Where(type => filter(type)));
            return structs;
        }

        private static List<TypeDefinition> GetEnums(AssemblyDefinition assembly, TypeFilter filter = null)
        {
            if (filter == null)
                filter = (type) => true;
            var enums = new List<TypeDefinition>();
            foreach (var module in assembly.Modules)
                enums.AddRange(module.GetTypes().Where(type => type.IsEnum).Where(type => filter(type)));
            return enums;
        }

        private static List<TypeDefinition> GetInterfaces(AssemblyDefinition assembly, TypeFilter filter = null)
        {
            if (filter == null)
                filter = (type) => true;
            var interfaces = new List<TypeDefinition>();
            foreach (var module in assembly.Modules)
                interfaces.AddRange(module.GetTypes().Where(type => type.IsInterface).Where(type => filter(type)));
            return interfaces;
        }

        private static List<TypeDefinition> GetDelegates(AssemblyDefinition assembly, TypeFilter filter = null)
        {
            if (filter == null)
                filter = (type) => true;
            var delegates = new List<TypeDefinition>();
            foreach (var module in assembly.Modules)
                delegates.AddRange(module.GetTypes().Where(type => type.IsDelegate()).Where(type => filter(type)));
            return delegates;
        }

        private static List<TypeDefinition> GetClasses(AssemblyDefinition assembly, TypeFilter filter = null)
        {
            if (filter == null)
                filter = (type) => true;
            var classes = new List<TypeDefinition>();
            foreach (var module in assembly.Modules)
                classes.AddRange(module.GetTypes().Where(type => type.IsClass).Where(type => filter(type)));
            return classes;
        }

        private static List<TypeDefinition> GetAllTypes(AssemblyDefinition assembly)
        {
            var types = new List<TypeDefinition>();
            if (assembly == null) return types;
            foreach (var module in assembly.Modules)
                types.AddRange(module.GetTypes());
            return types;
        }

        private static HashSet<string> Names(System.Collections.IEnumerable list)
        {
            var names = new HashSet<string>();
            foreach(var i in list)
            {
                names.Add(i.ToString());
            }
            return names;
        }

        private static string ExtractJsDoc(XElement root)
        {
            var sb = new StringBuilder();
            ExtractJsDocFragment(root, sb);
            var doc = sb.ToString().Trim().Replace("/*", "").Replace("*/", "");
            return doc.Length == 0 ? doc : $"/** {doc}*/{Environment.NewLine}";
        }

        private static string ExtractJsCRef(XElement e)
        {
            // expected: canonical name, prefixed with kind "(F|T|M):",
            // and potentially containing method parameter types

            var r = e.Attribute(XName.Get("cref"))?.Value ?? string.Empty;
            if (r.Length < 2)
            {
                return r;
            }
            r = r.Substring(2); // skip F|T|M: prefix
            r = r.Replace("UTiny.", "ut."); // replace root namespace
            var lParenIndex = r.IndexOf('(');
            if (lParenIndex >= 0)
            {
                // trim the method parameters, if any
                r = r.Substring(0, lParenIndex);
            }
            return r;
        }

        private static string ReduceWhiteSpace(string s)
        {
            var sb = new StringBuilder();
            var isWS = false;
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\r':
                        break;
                    case ' ':
                    case '\t':
                        if (!isWS)
                        {
                            sb.Append(c);
                            isWS = true;
                        }
                        break;
                    case '\n':
                        sb.AppendLine();
                        sb.Append("    "); // indent new line
                        isWS = true;
                        break;
                    default:
                        sb.Append(c);
                        isWS = false;
                        break;
                }
            }

            return sb.ToString();
        }

        private static void ExtractJsDocFragment(XElement element, StringBuilder sb)
        {
            var closeElement = false;
            var appendLine = true;
            var isXmlElement = false;

            switch (element.Name.LocalName)
            {
                case "member":
                    appendLine = false;
                    break;
                case "summary":
                    break;
                case "see":
                    var cref = ExtractJsCRef(element);
                    if (cref.Length > 0)
                    {
                        sb.Append($"{{@link {cref}}}");
                    }
                    else
                    {
                        var href = element.Attribute(XName.Get("href"))?.Value ?? string.Empty;
                        if (href.Length > 0)
                        {
                            sb.Append($"{{@link {href}");
                            if (element.IsEmpty)
                            {
                                sb.Append("}");
                            }
                            else
                            {
                                sb.Append("|");
                                closeElement = true;
                            }
                        }
                    }
                    appendLine = false;
                    break;
                case "param":
                    sb.Append($"@param {element.Attribute(XName.Get("name"))?.Value} ");
                    break;
                case "paramref":
                    sb.Append($"{{@link {element.Attribute(XName.Get("name"))?.Value}}}");
                    appendLine = false;
                    break;
                case "code":
                    sb.Append($"{{@link ");
                    appendLine = false;
                    closeElement = true;
                    break;
                case "exception":
                    sb.Append($"@throws {ExtractJsCRef(element)}");
                    break;
                case "returns":
                    sb.Append($"@returns ");
                    break;
                default:
                    // preserve other tags
                    sb.Append($"<{element.Name.LocalName}");
                    foreach (var attr in element.Attributes())
                    {
                        sb.Append($" {attr.Name}=\"{attr.Value}\"");
                    }
                    if (element.IsEmpty)
                    {
                        sb.Append(" />");
                        return;
                    }

                    sb.Append(">");
                    closeElement = true;
                    isXmlElement = true;
                    break;
            }

            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText xtext:
                        sb.Append(ReduceWhiteSpace(xtext.Value));
                        continue;
                    case XElement xelem:
                        ExtractJsDocFragment(xelem, sb);
                        break;
                }
            }

            if (closeElement)
            {
                sb.Append(isXmlElement ? $"</{element.Name.LocalName}>" : "}");
            }

            if (appendLine)
            {
                sb.AppendLine();
            }
        }

        private void CompileCSharpSource(List<string> sourceFiles, List<string> referenceSourceFiles, List<string> referenceDLLFiles, bool referenceBindGem)
        {
            var parameters = new CompilerParameters
            {
                GenerateInMemory = false,
                OutputAssembly = Path.GetTempPath() + Path.GetRandomFileName() + ".dll",
                TreatWarningsAsErrors = false,
                GenerateExecutable = false
            };

            var docXmlFilename = Path.GetTempPath() + Path.GetRandomFileName() + ".xml";
            parameters.CompilerOptions = "-unsafe -doc:" + docXmlFilename;

            string[] references = { "System.dll" };
            parameters.ReferencedAssemblies.AddRange(references);

            var codeDom = new CSharpCodeProvider();
            bindGem = AssemblyDefinition.ReadAssembly(typeof(BindGem).Assembly.Location);

            // Since reflection is performed by compiling the .cs files, a basic requirement for
            // reflection is that the reflected code must be valid code that compiles successfully.
            // Each reflected .cs IDL file can reference the following assemblies built-in:
            parameters.ReferencedAssemblies.Add(typeof(System.Object).Assembly.Location); // mscorlib
            if (referenceBindGem)
                parameters.ReferencedAssemblies.Add(typeof(BindGem).Assembly.Location); // All types in the BindGem project itself

            foreach (var dllFile in referenceDLLFiles)
            {
                parameters.ReferencedAssemblies.Add(dllFile);
            }

            if (referenceSourceFiles.Count > 0)
            {
                var dependenciesResults = codeDom.CompileAssemblyFromFile(parameters, referenceSourceFiles.ToArray());
                if (dependenciesResults.Errors.HasErrors)
                {
                    foreach (var error in dependenciesResults.Errors)
                        Console.Error.WriteLine(error);
                    throw new Exception(dependenciesResults.Errors[0].ToString());
                }
                dependenciesAssembly = AssemblyDefinition.ReadAssembly(parameters.OutputAssembly);
            }

            parameters.OutputAssembly = Path.GetTempPath() + Path.GetRandomFileName() + ".dll";
            sourceFiles.AddRange(referenceSourceFiles);
            var results = codeDom.CompileAssemblyFromFile(parameters, sourceFiles.ToArray());
            if (results.Errors.HasErrors)
            {
                foreach (var error in results.Errors)
                    Console.Error.WriteLine(error);
                throw new Exception(results.Errors[0].ToString());
            }

            // Handle XMLDoc
            var xdoc = XDocument.Load(docXmlFilename);
            var members = xdoc.Descendants("member");
            foreach (var member in members)
            {
                var str = ExtractJsDoc(member);
                generatedCodeDocumentationXmlElements[member.Attribute("name").Value] = str;
            }
            File.Delete(docXmlFilename);

            LoadFromAssembly(parameters.OutputAssembly, referenceDLLFiles);
        }

        private void LoadFromAssembly(string assembly, List<string> referenceAssemblies)
        {
            var assemblyResolver = new CecilAssemblyResolver();
            var dependencyTypes = new HashSet<TypeDefinition>();
            var rp = new ReaderParameters() { AssemblyResolver = assemblyResolver };
            foreach (var dllFile in referenceAssemblies)
            {
                var def = AssemblyDefinition.ReadAssembly(dllFile, rp);
                assemblyResolver.cache.Add(def.FullName, def);

                dependencyTypes.UnionWith(GetAllTypes(def));
            }

            var readerParameters = new ReaderParameters();
            readerParameters.InMemory = true; // Load the whole input file to memory so that the temporary file can be easily removed without dependencies.
            readerParameters.AssemblyResolver = assemblyResolver;
            compiledAssembly = AssemblyDefinition.ReadAssembly(assembly, readerParameters);

            dependencyTypes.UnionWith(GetAllTypes(dependenciesAssembly));

            if (BindGem.DOTS) {
                var foundStructs = new HashSet<TypeDefinition>();
                var foundEnums = new HashSet<TypeDefinition>();
                var seenStructs = new HashSet<TypeDefinition>();

                var structsToProcess = GetStructs(compiledAssembly, (type) => type.IsComponentType());

                while (structsToProcess.Count > 0) {
                    var s = structsToProcess[structsToProcess.Count - 1];
                    structsToProcess.RemoveAt(structsToProcess.Count - 1);

                    if (foundStructs.Contains(s))
                        continue;
                    foundStructs.Add(s);

                    foreach (var f in s.Fields) {
                        if (f.IsStatic)
                            continue;
                        if (!f.FieldType.IsValueType) {
                            Console.WriteLine($"Field {f.Name} of {s.FullName} has non-value-type {f.FieldType}");
                        }

                        var rf = f.FieldType.Resolve();
                        if (rf.IsEnum) {
                            foundEnums.Add(rf);
                        } else if (!rf.IsCppBasicType()) {
                            structsToProcess.Add(rf);
                        }
                    }
                }

                Structs = foundStructs.ToList();
                Enums = GetEnums(compiledAssembly).Union(foundEnums).ToList();
                //Enums = foundEnums.ToList();
                //Interfaces = GetInterfaces(compiledAssembly);
                Interfaces = new List<TypeDefinition>();
                //Delegates = GetDelegates(compiledAssembly);
                Delegates = new List<TypeDefinition>();
                //Classes = GetClasses(compiledAssembly);
                Classes = new List<TypeDefinition>();
            } else {
                Structs = GetStructs(compiledAssembly);
                Enums = GetEnums(compiledAssembly);
                Interfaces = GetInterfaces(compiledAssembly);
                Delegates = GetDelegates(compiledAssembly);
                Classes = GetClasses(compiledAssembly);
            }

            ExportedStructs = Structs.Where(x => !dependencyTypes.Contains(x)).ToList();
            ExportedEnums = Enums.Where(x => !dependencyTypes.Contains(x)).ToList();
            ExportedInterfaces = Interfaces.Where(x => !dependencyTypes.Contains(x)).ToList();
            ExportedDelegates = Delegates.Where(x => !dependencyTypes.Contains(x)).ToList();
            ExportedClasses = Classes.Where(x => !dependencyTypes.Contains(x)).ToList();
        }

        public List<TypeDefinition> AllTypes
        {
            get
            {
                List<TypeDefinition> types = new List<TypeDefinition>();
                foreach (var module in compiledAssembly.Modules)
                    types.InsertRange(types.Count, module.GetTypes());
                return types;
            }
        }
    }
}
