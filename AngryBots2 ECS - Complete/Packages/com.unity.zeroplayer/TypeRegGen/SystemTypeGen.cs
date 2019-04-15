//#define WRITE_LOG

using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;

namespace TypeRegGen
{
    internal static class TypeHelpers
    {
        public static bool TypeHasSuperClass(this TypeDefinition type, TypeDefinition baseClass)
        {
            while (!baseClass.Equals(type))
            {
                if (type == null || type.BaseType == null)
                    return false;
                type = type.BaseType.Resolve();
            }

            return true;
        }
    }

    internal class SystemTypeGen
    {
        public static List<TypeDefinition> GetSystems(List<AssemblyDefinition> assemblies)
        {
            var entities = assemblies.First(asm => asm.Name.Name == "Unity.Entities");
            var componentSystemBaseClass =  entities.MainModule.GetAllTypes().First(t => t.FullName == "Unity.Entities.ComponentSystemBase");

            var componentSystems = new List<TypeDefinition>();
            foreach(var asm in assemblies)
            {
                componentSystems.AddRange(asm.MainModule.GetAllTypes().Where(t =>
                    t.IsClass
                    && !t.IsAbstract
                    && t.TypeHasSuperClass(componentSystemBaseClass)
                    && !t.HasGenericParameters));
            }

            foreach(var componentSystem in componentSystems)
            {
                // If the system doesn't have a default constructor then if the system isn't labeled to disable auto-creation then
                // throw, otherwise just remove the type from our list as we can't handle it for auto-scheduling
                if(componentSystem.GetConstructors().FirstOrDefault(c => c.HasParameters == false) == null)
                {
                    if (componentSystem.CustomAttributes.Count > 0 &&
                        componentSystem.CustomAttributes.FirstOrDefault(ca => ca.Constructor.DeclaringType.Name == "DisableAutoCreationAttribute") != null)
                    {
                        // The system was marked as disabled for auto creation so just remove it from our list
                        componentSystems.Remove(componentSystem);
                    }
                    else
                    {
                        throw new ArgumentException($"The ComponentSystem '{componentSystem.FullName} does not define a default constructor necessary for automatic system scheduling. " +
                            $"If this system should not be auto-scheduled please add the [DisableAutoCreation] attribute to the system declaration.");
                    }
                }
            }

            return componentSystems;
        }

        public static List<bool> GetSystemIsGroup(List<AssemblyDefinition> assemblies, List<TypeDefinition> systems)
        {
            var entities = assemblies.First(asm => asm.Name.Name == "Unity.Entities");
            var baseClass =  entities.MainModule.GetAllTypes().First(t => t.FullName == "Unity.Entities.ComponentSystemGroup");
            var inGroup = systems.Select(s => s.TypeHasSuperClass(baseClass)).ToList();
            return inGroup;
        }

        public static List<string> GetSystemNames(List<TypeDefinition> systems)
        {
            return systems.Select(s => s.FullName).ToList();
        }

        public static MethodDefinition GenCreateSystems(List<TypeDefinition> systems, List<AssemblyDefinition> assemblies, ModuleDefinition typeRegModule,
            MethodReference getTypeFromHandleRef, MethodReference invalidOpExceptionRef)
        {
            // Check out HardcodedCreateSystem for the C# and IL of how this works.
            var createSystemsFunction = new MethodDefinition(
                "CreateSystem",
                MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig,
                typeRegModule.ImportReference(typeof(object)));

            createSystemsFunction.Parameters.Add(
                new ParameterDefinition("systemType",
                ParameterAttributes.None,
                typeRegModule.ImportReference(typeof(Type))));

            createSystemsFunction.Body.InitLocals = true;
            var bc = createSystemsFunction.Body.Instructions;

            foreach (var sys in systems)
            {
                var constructor =
                    typeRegModule.ImportReference(sys.GetConstructors()
                        .FirstOrDefault(param => param.HasParameters == false));

                bc.Add(Instruction.Create(OpCodes.Ldarg_0));
                bc.Add(Instruction.Create(OpCodes.Ldtoken, typeRegModule.ImportReference(sys)));
                bc.Add(Instruction.Create(OpCodes.Call, getTypeFromHandleRef));
                bc.Add(Instruction.Create(OpCodes.Ceq));
                int branchToNext = bc.Count;
                bc.Add(Instruction.Create(OpCodes.Nop));    // will be: Brfalse_S nextTestCase
                bc.Add(Instruction.Create(OpCodes.Newobj, constructor));
                bc.Add(Instruction.Create(OpCodes.Ret));

                var nextTest = Instruction.Create(OpCodes.Nop);
                bc.Add(nextTest);

                bc[branchToNext] = Instruction.Create(OpCodes.Brfalse_S, nextTest);
            }
            bc.Add(Instruction.Create(OpCodes.Ldstr, "FATAL: CreateSystem asked to create an unknown type. Only subclasses of ComponentSystemBase can be constructed."));
            bc.Add(Instruction.Create(OpCodes.Newobj, invalidOpExceptionRef));
            bc.Add(Instruction.Create(OpCodes.Throw));
            return createSystemsFunction;
        }

        static TypeDefinition FindClass(List<AssemblyDefinition> assemblies, string fullName)
        {
            foreach (var asm in assemblies)
            {
                var type = asm.MainModule.GetAllTypes().FirstOrDefault(t => t.FullName == fullName);
                if (type != null)
                    return type;
            }
            throw new InvalidProgramException("FindClass could not find: " + fullName);
        }

        public static MethodDefinition GenGetSystemAttributes(List<TypeDefinition> systems, List<AssemblyDefinition> assemblies, ModuleDefinition typeRegModule,
            MethodReference getTypeFromHandleRef, MethodReference invalidOpExceptionRef)
        {
            var attributeTypeRef = typeRegModule.ImportReference(typeof(Attribute));
            var attributeArrayTypeRef = attributeTypeRef.MakeArrayType();

            // Check out HardcodedGetSystemAttributes for the C# and IL of how this works.
            // Also essentially a more complex version of GenCreateSystems - so start there!
            var createSystemsFunction = new MethodDefinition(
                "GetSystemAttributes",
                MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig,
                typeRegModule.ImportReference(attributeArrayTypeRef));

            createSystemsFunction.Parameters.Add(
                new ParameterDefinition("systemType",
                ParameterAttributes.None,
                typeRegModule.ImportReference(typeof(Type))));

            createSystemsFunction.Body.InitLocals = true;

            var bc = createSystemsFunction.Body.Instructions;

            var allGroups = new string[]
            {
                "UpdateBeforeAttribute",
                "UpdateAfterAttribute",
                "UpdateInGroupAttribute",
                "DisableAutoCreationAttribute"
            };

            foreach (var sys in systems)
            {
#if WRITE_LOG
                Console.WriteLine("System: " + sys.FullName);
#endif
                bc.Add(Instruction.Create(OpCodes.Ldarg_0));
                bc.Add(Instruction.Create(OpCodes.Ldtoken, typeRegModule.ImportReference(sys)));
                bc.Add(Instruction.Create(OpCodes.Call, getTypeFromHandleRef));
                // Stack: argtype Type
                bc.Add(Instruction.Create(OpCodes.Ceq));
                // Stack: bool
                int branchToNext = bc.Count;
                bc.Add(Instruction.Create(OpCodes.Nop));    // will be: Brfalse_S nextTestCase

                // Stack: <null>
                List<CustomAttribute> attrList = new List<CustomAttribute>();
                foreach (var g in allGroups)
                {
                    var list = sys.CustomAttributes.Where(t => t.AttributeType.Name == g).ToList();
                    attrList.AddRange(list);
                }

                int arrayLen = attrList.Count;
                bc.Add(Instruction.Create(OpCodes.Ldc_I4, arrayLen));
                // Stack: arrayLen
                bc.Add(Instruction.Create(OpCodes.Newarr, attributeTypeRef));
                // Stack: array[]

                for (int i = 0; i < attrList.Count; ++i)
                {
                    var attr = attrList[i];

                    // The stelem.ref will gobble up the array ref we need to return, so dupe it.
                    bc.Add(Instruction.Create(OpCodes.Dup));
                    bc.Add(Instruction.Create(OpCodes.Ldc_I4, i));       // the index we will write
                    // Stack: array[] array[] array-index

                    // If it has a parameter, then load the Type that is the only param to the constructor.
                    if (attr.HasConstructorArguments)
                    {
                        if (attr.ConstructorArguments.Count > 1)
                            throw new InvalidProgramException("Attribute with more than one argument.");

                        var cArgName = attr.ConstructorArguments[0].Value.ToString();

                        var cArgType = FindClass(assemblies, cArgName);
                        if (cArgType == null)
                            throw new InvalidProgramException("SystemTypeGen couldn't find class: " + cArgName);

                        var arg = typeRegModule.ImportReference(cArgType);
                        bc.Add(Instruction.Create(OpCodes.Ldtoken, arg));
                        bc.Add(Instruction.Create(OpCodes.Call, getTypeFromHandleRef));

#if WRITE_LOG
                        Console.WriteLine("  Attr: {0} {1}", attr.AttributeType.Name, cArgName);
#endif
                    }
                    else
                    {
#if WRITE_LOG
                        Console.WriteLine("  Attr: {0}", attr.AttributeType.Name);
#endif
                    }
                    // Stack: array[] array[] array-index type-param OR
                    //        array[] array[] array-index

                    // Construct the attribute; push it on the list.
                    var cctor = typeRegModule.ImportReference(attr.Constructor);
                    bc.Add(Instruction.Create(OpCodes.Newobj, cctor));

                    // Stack: array[] array[] array-index value(object)
                    bc.Add(Instruction.Create(OpCodes.Stelem_Ref));
                    // Stack: array[]
                }

                // Stack: array[]
                bc.Add(Instruction.Create(OpCodes.Ret));

                // Put a no-op to start the next test.
                var nextTest = Instruction.Create(OpCodes.Nop);
                bc.Add(nextTest);

                // And go back and patch the IL to jump to the next test no-op just created.
                bc[branchToNext] = Instruction.Create(OpCodes.Brfalse_S, nextTest);
            }
            bc.Add(Instruction.Create(OpCodes.Ldstr, "FATAL: GetSystemAttributes asked to create an unknown Type."));
            bc.Add(Instruction.Create(OpCodes.Newobj, invalidOpExceptionRef));
            bc.Add(Instruction.Create(OpCodes.Throw));
            return createSystemsFunction;
        }
    }
}
