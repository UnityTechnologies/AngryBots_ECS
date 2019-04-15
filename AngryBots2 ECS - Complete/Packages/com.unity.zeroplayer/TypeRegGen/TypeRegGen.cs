using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Unity.Entities.BuildUtils;

using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;
using TypeGenInfoList = System.Collections.Generic.List<Unity.ZeroPlayer.TypeGenInfo>;
using TypeInfoMap = System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<Unity.ZeroPlayer.TypeGenInfo>>;
using SystemTypeGen = TypeRegGen.SystemTypeGen;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Unity.ZeroPlayer
{
    // Mirrors the definition in Unity.Entities.TypeManager
    public enum TypeCategory : int
    {
        ComponentData = 0,
        BufferData,
        ISharedComponentData,
        EntityData,
        Class,

        Null // Added sentinel value
    }

    // Mirrors the definition in Unity.LowLevel.Allocator
    internal enum Allocator
    {
        Invalid = 0,
        None = 1,
        Temp = 2,
        TempJob = 3,
        Persistent = 4
    }

    public enum Profile : int
    {
        DotNet = 0,
        DOTSDotNet,
        DOTSNative
    }

    internal struct ECSTypeInfo
    {
        public TypeCategory TypeCategory;
        public string FullTypeName;
    }

    internal struct TypeGenInfo
    {
        public TypeDefinition TypeDefinition;
        public TypeCategory TypeCategory;
        public List<int> EntityOffsets;
        public int EntityOffsetIndex;
        public List<int> BlobAssetRefOffsets;
        public int BlobAssetRefOffsetIndex;
        public HashSet<int> WriteGroupTypeIndices;
        public int WriteGroupsIndex;
        public int TypeIndex;
        public bool IsManaged;
        public TypeUtils.AlignAndSize AlignAndSize;
    }

    internal static class Extensions
    {
        // Extension function to Mono.Cecil to allow for creating a reference to a method for a type with generic parameters such as NativeArray<T>
        // https://groups.google.com/forum/#!topic/mono-cecil/mCat5UuR47I
        public static MethodReference MakeHostInstanceGeneric(this MethodReference self, params TypeReference[] args)
        {
            GenericInstanceType generic = self.DeclaringType.MakeGenericInstanceType(args);
            var reference = new MethodReference(self.Name, self.ReturnType, generic)
            {
                HasThis = self.HasThis,
                ExplicitThis = self.ExplicitThis,
                CallingConvention = self.CallingConvention
            };

            foreach (var parameter in self.Parameters)
            {
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
            }

            foreach (var genericParam in self.GenericParameters)
            {
                reference.GenericParameters.Add(new GenericParameter(genericParam.Name, reference));
            }

            return reference;
        }
    }

    public class TypeRegGen
    {
        internal class AssemblyResolver : DefaultAssemblyResolver
        {
            public Dictionary<string, int> AssemblyNameToIndexMap;
            public List<AssemblyDefinition> AssemblyDefinitions;

            public AssemblyResolver(ref List<AssemblyDefinition> assemblyDefinitions)
            {
                AssemblyNameToIndexMap = new Dictionary<string, int>();
                AssemblyDefinitions = assemblyDefinitions;
            }

            public void Add(AssemblyDefinition knownAssembly)
            {
                int index = AssemblyDefinitions.Count;
                AssemblyDefinitions.Add(knownAssembly);
                AssemblyNameToIndexMap.Add(knownAssembly.Name .FullName, index);
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
            {
                if (AssemblyNameToIndexMap.TryGetValue(name.FullName, out var asmIndex))
                {
                    return AssemblyDefinitions[asmIndex];
                }

                return base.Resolve(name, parameters);
            }

            protected override void Dispose(bool disposing)
            {
                foreach (var asm in AssemblyDefinitions)
                    asm.Dispose();

                AssemblyDefinitions.Clear();
                base.Dispose(disposing);
            }
        }

        public static void Main(string[] args)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            TypeRegGen typeRegGen = new TypeRegGen();
            typeRegGen.GenerateTypeRegistry(args);

            stopwatch.Stop();
            var elapsedMs = stopwatch.ElapsedMilliseconds;
            Console.WriteLine("Static Type Registry Generation Time: {0}ms", elapsedMs);
        }

        public void GenerateTypeRegistry(string[] args)
        {
            var assemblyResolver = new AssemblyResolver(ref m_AssemblyDefs);
            var symbolReaderProvider = new DefaultSymbolReaderProvider();

            ProcessArgs(args, assemblyResolver, symbolReaderProvider);

            var assemblySet = new HashSet<AssemblyNameDefinition>();
            TypeGenInfoList typeGenInfoList = PopulateWithTypesFromAssemblies(ref assemblySet);

            // If the assembly already exists use the one we read in, otherwise create it
            m_TypeRegAssembly = m_AssemblyDefs.FirstOrDefault(asm => asm.Name.Name == kStaticRegAsmName);
            if(m_TypeRegAssembly == null)
            {
                m_TypeRegAssembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(kStaticRegAsmName, new Version()),
                kStaticRegAsmNameWithExtension, ModuleKind.Dll);
            }
            else
            {
                // We need to remove the pre-exiting StaticTypeRegistry class as we will replace it with our own and we don't want multiple definitions
                var type = m_TypeRegAssembly.MainModule.Types.FirstOrDefault(t => t.Name == kStaticRegClassName);

                if(type != null)
                    m_TypeRegAssembly.MainModule.Types.Remove(type);
            }

            // add references to all the m_AssemblyDefs that the individual component data types use
            foreach (var asm in assemblySet)
                m_TypeRegAssembly.MainModule.AssemblyReferences.Add(asm);

            InitializeTypeReferences();

            // create a class with a static function
            m_RegClass = new TypeDefinition(kStaticRegAsmName, kStaticRegClassName, TypeAttributes.Class | TypeAttributes.Public, m_TypeRegAssembly.MainModule.ImportReference(typeof(object)));

            PatchScheduleCalls();
            GenerateDeclarations();
            GenerateDefinitions(in typeGenInfoList);

            m_TypeRegAssembly.MainModule.Types.Add(m_RegClass);

            var writerParams = new WriterParameters() { WriteSymbols = m_TypeRegAssembly.MainModule.HasSymbols };
            var outPath = Path.Combine(m_OutputDir, m_TypeRegAssembly.Name.Name) + ".dll";
            m_TypeRegAssembly.Write(outPath, writerParams);

            var deallocatingJobsMap = new Dictionary<string, JobGenInfo>();
            if (m_Profile != Profile.DotNet)
            {
                var jobsAsmDef = m_AssemblyDefs.FirstOrDefault(a => a.Name.Name == "Unity.ZeroJobs");
                deallocatingJobsMap = JobsGen.GetDeallocateOnCompletionJobs(in m_AssemblyDefs);
                JobsGen.GenerateDealllocateOnJobCompletionFn(m_GetTypeFnRef, m_GetTypeFromHandleFnRef, in deallocatingJobsMap);
            }
            // Add the `typeGenInfoList` and `m_Systems` list, then remove dupes, pass
            // the now unique list in for fixup.
            var typeList = typeGenInfoList.Select(t => t.TypeDefinition).ToList();
            if(m_Systems != null && m_Systems.Count > 0)
                typeList.AddRange(m_Systems);

            foreach (var jobGenInfo in deallocatingJobsMap.Values)
            {
                var jobTypeFieldMap = jobGenInfo.TypeFieldMap;
                foreach(var jobType in jobTypeFieldMap.Keys)
                {
                    typeList.Add(jobType);

                    var deallocationMembersList = jobTypeFieldMap[jobType];
                    foreach(var memberType in deallocationMembersList)
                    {
                        var fieldType = memberType.FieldType;
                        typeList.Add(fieldType.Resolve());

                        if(fieldType.IsGenericInstance)
                        {
                            foreach(var genericArg in (fieldType as GenericInstanceType).GenericArguments)
                            {
                                typeList.Add(genericArg.Resolve());
                            }
                        }
                    }
                }
            }

            typeList = typeList.Distinct().Where(t=>t != null).ToList();
            FixupAssemblies(in typeList);

            assemblyResolver.Dispose();
            m_MscorlibAssembly.Dispose();
            m_EntityAssembly.Dispose();
            m_TypeRegAssembly.Dispose();
        }

        public TypeRegGen()
        {
            m_ArchBits = 64;
            m_Profile = Profile.DOTSNative;

            m_AssemblyDefs = new List<AssemblyDefinition>();
            m_TypeDefToTypeIndex = new Dictionary<TypeDefinition, int>();
        }

        internal void ProcessArgs(string[] args, AssemblyResolver assemblyResolver, ISymbolReaderProvider symbolReaderProvider)
        {
            m_OutputDir = Path.GetFullPath(args[0]);
            var archBitsStr = args[1];
            var profileStr = args[2];

            if (!int.TryParse(archBitsStr, out m_ArchBits) || (m_ArchBits != 32 && m_ArchBits != 64))
                throw new ArgumentException($"Invalid architecture-bits passed in as second argument. Received '{archBitsStr}', Expected '32' or '64'.");

            if (!Enum.TryParse(profileStr, out m_Profile))
                throw new ArgumentException($"Invalid Profile Type passed in as third argument. Received '{profileStr}', Expected '0' (DotNet) or '1' (DOTSDotNet) or '2' (DOTSNative).");

            for (int i = 3; i < args.Length; ++i)
            {
                string normalizedPath = Path.GetFullPath(args[i]);
                if (!File.Exists(normalizedPath))
                {
                    Console.WriteLine($"Could not find assembly '{normalizedPath}': Please check your commandline arguments.");
                    continue;
                }

                // We don't want to read in any old StaticTypeRegisty assemblies. Just skip them if passed in
                if (Path.GetFileNameWithoutExtension(normalizedPath) == kStaticRegAsmName)
                {
                    continue;
                }

                // If we know we are reading from where we will be writing, ensure we open for readwrite
                bool bReadWrite = Path.GetDirectoryName(normalizedPath) == m_OutputDir;
                // This assembly tends to be in escalated privledge directories so we only open for read (we shouldn't need to write to it anyway)
                if (Path.GetFileNameWithoutExtension(normalizedPath) == "UnityEngine.CoreModule")
                {
                    bReadWrite = false;
                }

                var pdbPath = Path.ChangeExtension(normalizedPath, "pdb");
                var readerParams = new ReaderParameters() { AssemblyResolver = assemblyResolver, ReadWrite = bReadWrite, InMemory = true };
                if (File.Exists(pdbPath))
                {
                    readerParams.SymbolReaderProvider = symbolReaderProvider;
                    readerParams.ReadSymbols = true;
                }

                var asm = AssemblyDefinition.ReadAssembly(normalizedPath, readerParams);
                assemblyResolver.Add(asm);
            }

            // Entity is special so we maintain a specific reference to it so we can ensure it is always registed as typeIndex 1 (0 being reserved for null)
            m_EntityAssembly = m_AssemblyDefs.First(asm => asm.Name.Name == "Unity.Entities");
            m_EntityTypeDef = m_EntityAssembly.MainModule.GetType("Unity.Entities.Entity");

            m_MscorlibAssembly = m_AssemblyDefs.FirstOrDefault(asm => asm.Name.Name == "mscorlib");
            if (m_MscorlibAssembly == null)
            {
                var readerParams = new ReaderParameters() { AssemblyResolver = assemblyResolver };
                m_MscorlibAssembly = AssemblyDefinition.ReadAssembly(typeof(object).Assembly.Location, readerParams);
                assemblyResolver.Add(m_MscorlibAssembly);
            }
        }

        internal TypeGenInfo CreateTypeGenInfo(TypeDefinition type, TypeCategory typeCategory)
        {
            bool isManaged = type != null && type.IsManagedType();

            TypeUtils.AlignAndSize alignAndSize = new TypeUtils.AlignAndSize();
            List<int> entityOffsets = new List<int>();
            List<int> blobAssetRefOffsets = new List<int>();

            if (type == m_EntityTypeDef)
            {
                // Entity is special. We require Entity to have an EntityOffset at position 0
                entityOffsets.Add(0);
                alignAndSize = TypeUtils.AlignAndSizeOfType(type, m_ArchBits);
            }
            else if(!isManaged && type != null)
            {
                entityOffsets = TypeUtils.GetEntityFieldOffsets(type, m_ArchBits);
                blobAssetRefOffsets = TypeUtils.GetFieldOffsetsOf("Unity.Entities.BlobAssetReference`1", type, m_ArchBits);
                alignAndSize = TypeUtils.AlignAndSizeOfType(type, m_ArchBits);
            }

            int typeIndex = m_TotalTypeCount++;

            bool isSystemStateBufferElement = DoesTypeInheritInterface(type, "Unity.Entities.ISystemStateBufferElementData");
            bool isSystemStateSharedComponent = DoesTypeInheritInterface(type, "Unity.Entities.ISystemStateSharedComponentData");
            bool isSystemStateComponent = DoesTypeInheritInterface(type, "Unity.Entities.ISystemStateComponentData") || isSystemStateSharedComponent || isSystemStateBufferElement;

            if (typeIndex != 0)
            {
                if (alignAndSize.empty || typeCategory == TypeCategory.ISharedComponentData)
                    typeIndex |= ZeroSizeInChunkTypeFlag;

                if (typeCategory == TypeCategory.ISharedComponentData)
                    typeIndex |= SharedComponentTypeFlag;

                if (isSystemStateComponent)
                    typeIndex |= SystemStateTypeFlag;

                if (isSystemStateSharedComponent)
                    typeIndex |= SystemStateSharedComponentTypeFlag;

                if (typeCategory == TypeCategory.BufferData)
                    typeIndex |= BufferComponentTypeFlag;

                if (entityOffsets.Count == 0)
                    typeIndex |= HasNoEntityReferencesFlag;
            }

            var typeGenInfo = new TypeGenInfo()
            {
                TypeDefinition = type,
                TypeIndex = typeIndex,
                TypeCategory = typeCategory,
                EntityOffsets = entityOffsets,
                EntityOffsetIndex = m_TotalEntityOffsetCount,
                BlobAssetRefOffsets = blobAssetRefOffsets,
                BlobAssetRefOffsetIndex = m_TotalBlobAssetRefOffsetCount,
                WriteGroupTypeIndices = new HashSet<int>(),
                WriteGroupsIndex = 0,
                IsManaged = isManaged,
                AlignAndSize = alignAndSize
            };

            m_TotalEntityOffsetCount += entityOffsets.Count;
            m_TotalBlobAssetRefOffsetCount += blobAssetRefOffsets.Count;

            return typeGenInfo;
        }

        /// <summary>
        /// Iterates over all assemblies and returns a TypeGenInfo list which contains, in typeIndex order, enough information per type to generate constant type information for the registry
        /// </summary>
        internal TypeGenInfoList PopulateWithTypesFromAssemblies(ref HashSet<AssemblyNameDefinition> assemblySet)
        {
            var typeGenInfoList = new TypeGenInfoList();

            // Create 'special' types and insert them before any others

            // Unity.Entities relies on this being index 0
            typeGenInfoList.Insert(0, CreateTypeGenInfo(null, TypeCategory.Null));
            // Unity.Entities relies on this being index 1
            typeGenInfoList.Insert(1, CreateTypeGenInfo(m_EntityTypeDef, TypeCategory.EntityData));

            var typesToRegister = new TypeInfoMap();

            foreach (var ecsType in ECSTypesToRegister)
            {
                typesToRegister[(int)ecsType.TypeCategory] = new List<TypeGenInfo>();
            }

            var foundGenericTypes = new List<TypeDefinition>();

            // Go through list of m_AssemblyDefs and collect all types that implement one of the interfaces
            foreach (var asm in m_AssemblyDefs)
            {
                foreach (var ecsType in ECSTypesToRegister)
                {
                    var foundList = typesToRegister[(int)ecsType.TypeCategory];
                    var interfaceType = m_EntityAssembly.MainModule.GetType(ecsType.FullTypeName).Resolve();

                    foreach (var type in asm.MainModule.GetAllTypes().Where(t => t.IsValueType &&
                        t.Interfaces.Select(f => f.InterfaceType.Resolve()).Contains(interfaceType)))
                    {
                        if (type.HasGenericParameters)
                        {
                            foundGenericTypes.Add(type);
                        }
                        else
                        {
                            foundList.Add(CreateTypeGenInfo(type, ecsType.TypeCategory));
                        }

                        assemblySet.Add(type.Module.Assembly.Name);
                    }
                }
            }

            // we need to do more work to figure out the actual type params of generic component etc. types
            foreach (var genericType in foundGenericTypes)
            {
                //foreach (var asm in m_AssemblyDefs) {
                //foreach (var type in asm.MainModule.Types.Where(t => t.IsGenericInstance(genericType)
            }

            typeGenInfoList.AddRange(typesToRegister.SelectMany(p => p.Value).ToList());
            typeGenInfoList.Sort((t1, t2) => (t1.TypeIndex & ClearFlagsMask).CompareTo((t2.TypeIndex & ClearFlagsMask)));

            if (typeGenInfoList.Count != m_TotalTypeCount)
                throw new InvalidProgramException("The TypeGenInfo list must contain the same number of types in total as found across all assemblies. There must be a bug in TypeRegGen.cs");

            foreach(var typeGenInfo in typeGenInfoList)
            {
                if (typeGenInfo.TypeDefinition == null)
                    continue;

                m_TypeDefToTypeIndex[typeGenInfo.TypeDefinition] = typeGenInfo.TypeIndex;
            }

            PopulateWriteGroups(ref typeGenInfoList);

            return typeGenInfoList;
        }


        MethodReference FindScheduleMethod(MethodDefinition methodDefinition, out bool usesEntity)
        {
            // 2 flavors:
            //    (T0...)
            //    (Entity, int, T0...)
            // Anything else is an error.

            usesEntity = false;

            if (methodDefinition.Parameters.Count == 0)
            {
                throw new InvalidOperationException("FindScheduleMethod: Execute doesn't have parameters.");
            }

            if (methodDefinition.Parameters[0].ParameterType.FullName == "Unity.Entities.Entity")
            {
                usesEntity = true;
                if (methodDefinition.Parameters[1].ParameterType.FullName != "System.Int32")
                {
                    throw new InvalidOperationException("FindScheduleMethod: Execute method specifies an Entity, but not an int index");
                }
            }

            string append = "";
            const string READ_ONLY_DATA = "rD";
            const string WRITE_READ_DATA = "wD";

            for (int i = usesEntity ? 2 : 0; i < methodDefinition.Parameters.Count; ++i)
            {
                ParameterDefinition param = methodDefinition.Parameters[i];
                if (param.HasCustomAttributes &&
                    param.CustomAttributes.FirstOrDefault(p =>
                        p.AttributeType.FullName == "Unity.Collections.ReadOnlyAttribute") != null)
                {
                    append += READ_ONLY_DATA;
                }
                else
                {
                    append += WRITE_READ_DATA;
                }
            }

            string name = (usesEntity ? "Schedule_E" : "Schedule_") + append;

            TypeDefinition extension = m_EntityAssembly.MainModule.GetAllTypes()
                .First(t => t.FullName == "Unity.Entities.JobForEachExtensions");
            MethodDefinition schedule = extension.Methods.First(m => m.Name == name);
            return schedule;
        }

        void RecursePatchScheduleCalls(TypeDefinition type, AssemblyDefinition asm)
        {
            foreach (TypeDefinition td in type.NestedTypes)
            {
                RecursePatchScheduleCalls(td, asm);
            }

            foreach (var method in type.Methods)
            {
                if (method.HasBody)
                {
                    var giList = method.Body.Instructions
                        .Where(i => i.OpCode == OpCodes.Call
                                    && (i.Operand is MethodReference)
                                    && (i.Operand as MethodReference).ContainsGenericParameter
                                    && ((i.Operand as MethodReference).FullName.Contains("Unity.Entities.JobForEachExtensions::Schedule<") ||
                                        (i.Operand as MethodReference).FullName.Contains("Unity.Entities.JobForEachExtensions::Run<") ||
                                        (i.Operand as MethodReference).FullName.Contains("Unity.Entities.JobForEachExtensions::ScheduleSingle<"))

                               );
                    foreach (var g in giList)
                    {
                        string name = (g.Operand as MethodReference).FullName;
                        int start = name.IndexOf('<');
                        int end = name.IndexOf('>');
                        if (end <= (start+1))
                        {
                            throw new Exception("Could not find the expected Schedule/Run function. This is a bug.");
                        }
                        string jobName = name.Substring(start + 1, end - (start+1));

                        try
                        {
                            TypeDefinition jobClass = asm.MainModule.GetAllTypes()
                                .First(t => t.FullName == jobName);

                            // We have found a call to Schedule. But what types do we use to specialize the
                            // generic? The Job is required to have an Execute method - with the correct
                            // parameters. It's a handy place to grab them, so find that method and use it.
                            MethodDefinition executeMethod = jobClass.Methods.First(f => f.Name == "Execute");

                            // The "open" method has generic types; the "closed" method has specific types used
                            // to call the generic.
                            bool usesEntity;
                            MethodReference openScheduleMethod = FindScheduleMethod(executeMethod, out usesEntity);
                            var closedScheduleMethod = new GenericInstanceMethod(openScheduleMethod);

                            closedScheduleMethod.GenericArguments.Add(jobClass);

                            for (int i = usesEntity ? 2 : 0; i < executeMethod.Parameters.Count; ++i)
                            {
                                closedScheduleMethod.GenericArguments.Add(executeMethod.Parameters[i].ParameterType.GetElementType());
                            }

                            // Console.WriteLine("Call site Schedule() patch {0} to {1} at {2}", jobClass.Name, closedScheduleMethod.Name, type.FullName);
                            // Only the operand of the call needs to be changed; parameters and nearby IL code are the same.
                            g.Operand = asm.MainModule.ImportReference(closedScheduleMethod);

                        }
                        catch (Exception)
                        {
                            string msg = $"Error patching IJobForEach '{jobName}' in method '{method.FullName}'";
                            throw new InvalidOperationException(msg);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// This does call-site patching of Schedule() calls.
        /// Transforming:
        ///     job.Schedule(system);
        /// into:
        ///     JobProcessComponentDataExtensions.Schedule_D<Game.RotateSpriteSystem.RotateSpritesJob, TransformLocalRotation>(system);
        ///
        /// generically the above is:
        ///     Schedule_D<TJob, T0>(TJob job, ComponentSystemBase system);
        /// </summary>
        internal void PatchScheduleCalls()
        {
            foreach (AssemblyDefinition asm in m_AssemblyDefs)
            {
                foreach (var mod in asm.Modules)
                {
                    foreach (var t in mod.Types)
                    {
                        RecursePatchScheduleCalls(t, asm);
                    }
                }
            }
        }


        /// <summary>
        /// Declares all fields and member functions for the Static Type Registry
        /// </summary>
        internal void GenerateDeclarations()
        {
            // Note: The actual construction of fields is done in the generated .CCTOR

            //////////////////
            // Declare fields
            //////////////////

            // Declares: static public readonly TypeInfo[] sTypeInfoArray
            var typeInfoArrayRef = m_TypeInfoRef.MakeArrayType();
            m_RegTypeInfoArrayDef = new FieldDefinition("TypeInfos", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, typeInfoArrayRef);
            m_RegClass.Fields.Add(m_RegTypeInfoArrayDef);

            // Declares: static public readonly int[] sEntityOffsetArray
            var entityOffsetInfoArrayRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(int)).MakeArrayType();
            m_RegEntityOffsetArrayDef = new FieldDefinition("EntityOffsets", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, entityOffsetInfoArrayRef);
            m_RegClass.Fields.Add(m_RegEntityOffsetArrayDef);

            // Declares: static public readonly int[] sBlobAssetReferenceOffsetArray
            var blobAssetReferenceOffsetsArrayRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(int)).MakeArrayType();
            m_RegBlobAssetReferneceOffsetsArrayDef = new FieldDefinition("BlobAssetReferenceOffsets", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, blobAssetReferenceOffsetsArrayRef);
            m_RegClass.Fields.Add(m_RegBlobAssetReferneceOffsetsArrayDef);

            // Declares: static public readonly int[] sWriteGroupArrayDef
            var writeGroupArrayRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(int)).MakeArrayType();
            m_WriteGroupArrayDef = new FieldDefinition("WriteGroups", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, writeGroupArrayRef);
            m_RegClass.Fields.Add(m_WriteGroupArrayDef);

            // Declares: static public readonly Type[] sTypeArray
            var systemTypeArrayRef = m_SystemTypeRef.MakeArrayType();
            m_RegSystemTypeArrayDef = new FieldDefinition("Types", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, systemTypeArrayRef);
            m_RegClass.Fields.Add(m_RegSystemTypeArrayDef);

            if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
            {
                // static public readonly Type[] Systems
                var systemSystemsArrayRef = m_SystemTypeRef.MakeArrayType();
                m_RegSystemSystemsArrayDef = new FieldDefinition("Systems", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, systemSystemsArrayRef);
                m_RegClass.Fields.Add(m_RegSystemSystemsArrayDef);

                // static public readonly bool[] SystemIsGroup
                var boolTypeRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(bool));
                var sBoolArrayRef = boolTypeRef.MakeArrayType();
                m_RegSystemIsGroupArrayDef = new FieldDefinition("SystemIsGroup", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, sBoolArrayRef);
                m_RegClass.Fields.Add(m_RegSystemIsGroupArrayDef);

                // static public readonly string[] SystemName
                var stringTypeRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(string));
                var sStringArrayRef = stringTypeRef.MakeArrayType();
                m_RegSystemNameArrayDef = new FieldDefinition("SystemName", Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Public | Mono.Cecil.FieldAttributes.InitOnly, sStringArrayRef);
                m_RegClass.Fields.Add(m_RegSystemNameArrayDef);
            }

            ///////////
            // Methods
            ///////////

            // Declares: static public StaticTypeRegistry() (the static ctor)
            m_RegCCTOR = new MethodDefinition(".cctor", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, m_TypeRegAssembly.MainModule.ImportReference(typeof(void)));
            m_RegClass.Methods.Add(m_RegCCTOR);
            m_RegClass.IsBeforeFieldInit = true;

            // Declares & implements: static public RegisterStaticTypes()
            var registerStaticTypesFn = GenerateRegisterStaticTypesFn();
            m_RegClass.Methods.Add(registerStaticTypesFn);

            if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
            {
                // Declare & implements: static public CreateSystem()
                m_Systems = SystemTypeGen.GetSystems(m_AssemblyDefs);
                var createSystemsFn = SystemTypeGen.GenCreateSystems(m_Systems, m_AssemblyDefs, m_TypeRegAssembly.MainModule, m_GetTypeFromHandleFnRef, m_InvalidOpExceptionCTORRef);
                m_RegClass.Methods.Add(createSystemsFn);

                // Declares: static public GetSystemAttributes()
                var getSystemAttributesFn = SystemTypeGen.GenGetSystemAttributes(m_Systems, m_AssemblyDefs, m_TypeRegAssembly.MainModule, m_GetTypeFromHandleFnRef, m_InvalidOpExceptionCTORRef);
                m_RegClass.Methods.Add(getSystemAttributesFn);
            }

            //if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
            {
                // Declares: static public bool Equals(void* lhs, void* rhs, int typeIndex)
                // This function is required to allow users to query for equality when a Generic <T> param isn't available but the 'int' typeIndex is
                m_RegEqualsFn = new MethodDefinition("Equals", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(bool)));
                m_RegEqualsFn.Parameters.Add(new ParameterDefinition("lhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(void*))));
                m_RegEqualsFn.Parameters.Add(new ParameterDefinition("rhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(void*))));
                m_RegEqualsFn.Parameters.Add(new ParameterDefinition("typeIndex", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(int))));
                m_RegClass.Methods.Add(m_RegEqualsFn);

                // Declares: static public bool Equals(object lhs, object rhs, int typeIndex)
                // This function is required to allow users to query for equality when a Generic <T> param isn't available but the 'int' typeIndex is
                m_RegBoxedEqualsFn = new MethodDefinition("Equals", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(bool)));
                m_RegBoxedEqualsFn.Parameters.Add(new ParameterDefinition("lhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(object))));
                m_RegBoxedEqualsFn.Parameters.Add(new ParameterDefinition("rhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(object))));
                m_RegBoxedEqualsFn.Parameters.Add(new ParameterDefinition("typeIndex", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(int))));
                m_RegClass.Methods.Add(m_RegBoxedEqualsFn);

                // Declares: static public bool Equals(object lhs, void* rhs, int typeIndex)
                // This function is required to allow users to query for equality when a Generic <T> param isn't available but the 'int' typeIndex is
                m_RegBoxedPtrEqualsFn = new MethodDefinition("Equals", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(bool)));
                m_RegBoxedPtrEqualsFn.Parameters.Add(new ParameterDefinition("lhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(object))));
                m_RegBoxedPtrEqualsFn.Parameters.Add(new ParameterDefinition("rhs", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(void*))));
                m_RegBoxedPtrEqualsFn.Parameters.Add(new ParameterDefinition("typeIndex", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(int))));
                m_RegClass.Methods.Add(m_RegBoxedPtrEqualsFn);

                // Declares: static public int GetHashCode(void* val, int typeIndex)
                // This function is required to allow users to query for equality when a Generic <T> param isn't available but the 'int' typeIndex is
                m_RegGetHashCodeFn = new MethodDefinition("GetHashCode", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
                m_RegGetHashCodeFn.Parameters.Add(new ParameterDefinition("val", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(void*))));
                m_RegGetHashCodeFn.Parameters.Add(new ParameterDefinition("typeIndex", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(int))));
                m_RegClass.Methods.Add(m_RegGetHashCodeFn);

                // Declares: static public int GetHashCode(object val, int typeIndex)
                // This function is required to allow users to query for equality when a Generic <T> param isn't available but the 'int' typeIndex is
                m_RegBoxedGetHashCodeFn = new MethodDefinition("BoxedGetHashCode", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
                m_RegBoxedGetHashCodeFn.Parameters.Add(new ParameterDefinition("val", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(object))));
                m_RegBoxedGetHashCodeFn.Parameters.Add(new ParameterDefinition("typeIndex", Mono.Cecil.ParameterAttributes.None, m_TypeRegAssembly.MainModule.ImportReference(typeof(int))));
                m_RegClass.Methods.Add(m_RegBoxedGetHashCodeFn);
            }
        }

        internal void GenerateDefinitions(in TypeGenInfoList typeGenInfoList)
        {
            // Static constructor where most logic is contained for filling the registry's readonly fields
            GenerateStaticTypeRegistryCCTOR(in typeGenInfoList);
        }

        /// <summary>
        /// Initializes all fields and static variables for the StaticTypeRegistry.
        /// For debug configs, the registry will also generate additional validation code to ensure the TypeRegGen's constant TypeInfo/EntityOffset data is correct for the target platform
        /// </summary>
        internal void GenerateStaticTypeRegistryCCTOR(in TypeGenInfoList typeGenInfoList)
        {
            var il = m_RegCCTOR.Body.GetILProcessor();

            GeneratePlatformValidation(ref il);

            // Type Information
            {
                GenerateEntityOffsetInfoArray(ref il, in typeGenInfoList);
                GenerateBlobAssetReferenceArray(ref il, in typeGenInfoList);
                GenerateWriteGroupArray(ref il, in typeGenInfoList);
                GenerateTypeArray(ref il, typeGenInfoList.Select(t => t.TypeDefinition).ToList(), m_TotalTypeCount, m_RegSystemTypeArrayDef);
                GenerateTypeInfoArray(ref il, in typeGenInfoList);

                //if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
                {
                    // We need to generate the equality functions here since we will be instantiating a EqualityHelper<T> per type whose static fields will hold our type specific equality functions
                    GenerateEqualityFunctions(ref il, typeGenInfoList);
                }
            }

            // System Information
            if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
            {
                GenerateTypeArray(ref il, m_Systems, m_Systems.Count, m_RegSystemSystemsArrayDef);

                var systemIsGroup = SystemTypeGen.GetSystemIsGroup(m_AssemblyDefs, m_Systems);
                GenerateBoolArray(ref il, m_RegSystemIsGroupArrayDef, systemIsGroup);

                var systemNames = SystemTypeGen.GetSystemNames(m_Systems);
                GenerateStringArray(ref il, m_RegSystemNameArrayDef, systemNames);
            }

            il.Emit(OpCodes.Ret); // Return from static constructor
        }

        /// <summary>
        /// Generate a small platform check to validate the runtime platform matches what the TypeRegGen thought it was targeting when generating the IL.
        /// </summary>
        internal void GeneratePlatformValidation(ref ILProcessor il)
        {
            Instruction platformValidationEnd = il.Create(OpCodes.Nop);

            // Check if IntPtr.Size == 8 when running code generated assuming a 64-bit platform.
            // If the comparison is false, throw an exception
            il.Emit(OpCodes.Call, m_IntPtrGetSizeFnRef);
            if(m_ArchBits == 64)
            {
                EmitLoadConstant(ref il, 8);
            }
            else
            {
                EmitLoadConstant(ref il, 4);
            }

            il.Emit(OpCodes.Beq, platformValidationEnd); // if check passes jump to end of function (ret)

            // These instructions only run if the comparison was false
            il.Emit(OpCodes.Ldstr, $"FATAL: Runtime platform architecture does not match the architecture targeted when generating the Static Type Registry! Are you using the correct {kStaticRegAsmNameWithExtension}?");
            il.Emit(OpCodes.Newobj, m_InvalidOpExceptionCTORRef);
            il.Emit(OpCodes.Throw);

            il.Append(platformValidationEnd);
        }

        /// <summary>
        /// Populates the registry's entityOffset int array.
        /// Offsets are laid out contiguously in memory such that the memory layout for Types A (2 entites), B (3 entities), C (0 entities) D (2 entities) is as such: aabbbdd
        /// </summary>
        internal void GenerateEntityOffsetInfoArray(ref ILProcessor il, in TypeGenInfoList typeGenInfoList)
        {
            PushNewArray(ref il, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)), m_TotalEntityOffsetCount);

            int entityOffsetIndex = 0;
            foreach (var typeGenInfo in typeGenInfoList)
            {
                foreach (var offset in typeGenInfo.EntityOffsets)
                {
                    PushNewArrayElement(ref il, entityOffsetIndex++);
                    EmitLoadConstant(ref il, offset);
                    il.Emit(OpCodes.Stelem_Any, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
                }
            }

            StoreTopOfStackToStaticField(ref il, m_RegEntityOffsetArrayDef);
        }

        /// <summary>
        /// Populates the registry's entityOffset int array.
        /// Offsets are laid out contiguously in memory such that the memory layout for Types A (2 entites), B (3 entities), C (0 entities) D (2 entities) is as such: aabbbdd
        /// </summary>
        internal void GenerateBlobAssetReferenceArray(ref ILProcessor il, in TypeGenInfoList typeGenInfoList)
        {
            PushNewArray(ref il, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)), m_TotalBlobAssetRefOffsetCount);

            int blobOffsetIndex = 0;
            foreach (var typeGenInfo in typeGenInfoList)
            {
                foreach (var offset in typeGenInfo.BlobAssetRefOffsets)
                {
                    PushNewArrayElement(ref il, blobOffsetIndex++);
                    EmitLoadConstant(ref il, offset);
                    il.Emit(OpCodes.Stelem_Any, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
                }
            }

            StoreTopOfStackToStaticField(ref il, m_RegBlobAssetReferneceOffsetsArrayDef);
        }

        internal void PopulateWriteGroups(ref TypeGenInfoList typeGenInfoList)
        {
            var writeGroupMap = new Dictionary<int, HashSet<int>>();

            foreach (var typeGenInfo in typeGenInfoList)
            {
                if (typeGenInfo.TypeDefinition == null)
                    continue;

                var typeDef = typeGenInfo.TypeDefinition;
                var typeIndex = typeGenInfo.TypeIndex;

                foreach (var attribute in typeDef.CustomAttributes.Where(a => a.AttributeType.FullName == "Unity.Entities.WriteGroupAttribute"))
                {
                    var targetType = attribute.ConstructorArguments[0].Value as TypeDefinition;
                    int targetTypeIndex = m_TypeDefToTypeIndex[targetType];

                    if (!writeGroupMap.ContainsKey(targetTypeIndex))
                    {
                        var targetList = new HashSet<int>();
                        writeGroupMap.Add(targetTypeIndex, targetList);
                    }

                    writeGroupMap[targetTypeIndex].Add(typeIndex);
                }
            }

            m_TotalWriteGroupCount = 0;
            for(int i = 0; i <  typeGenInfoList.Count; ++i)
            {
                var typeGenInfo = typeGenInfoList[i];

                if(writeGroupMap.TryGetValue(typeGenInfo.TypeIndex, out var writeGroups))
                {
                    typeGenInfo.WriteGroupTypeIndices = writeGroups;
                    typeGenInfo.WriteGroupsIndex = m_TotalWriteGroupCount;
                    typeGenInfoList[i] = typeGenInfo;
                    m_TotalWriteGroupCount += writeGroups.Count();
                }
            }
        }

        /// <summary>
        /// Populates the registry's writeGroup int array.
        /// WriteGroup TypeIndices are laid out contiguously in memory such that the memory layout for Types A (2 writegroup elements),
        /// B (3 writegroup elements), C (0 writegroup elements) D (2 writegroup elements) is as such: aabbbdd
        /// </summary>
        internal void GenerateWriteGroupArray(ref ILProcessor il, in TypeGenInfoList typeGenInfoList)
        {
            PushNewArray(ref il, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)), m_TotalWriteGroupCount);

            int writeGroupIndex = 0;
            foreach(var typeGenInfo in typeGenInfoList)
            {
                foreach (var wgTypeIndex in typeGenInfo.WriteGroupTypeIndices)
                {
                    PushNewArrayElement(ref il, writeGroupIndex++);
                    EmitLoadConstant(ref il, wgTypeIndex);
                    il.Emit(OpCodes.Stelem_Any, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
                }
            }

            StoreTopOfStackToStaticField(ref il, m_WriteGroupArrayDef);
        }


        /// <summary>
        ///  Populates the registry's System.Type array for all types in typeIndex order.
        /// </summary>
        internal void GenerateTypeArray(ref ILProcessor il, List<TypeDefinition> typeDefinitions, int typeCount,
            FieldDefinition fieldDefinition)
        {
            if (typeDefinitions.Count != typeCount)
                throw new InvalidProgramException("GenerateTypeArray counts don't match. There must be a bug in TypeRegGen.cs");
            PushNewArray(ref il, m_SystemTypeRef, typeDefinitions.Count);

            for (int typeIndex = 0; typeIndex < typeDefinitions.Count; ++typeIndex)
            {
                if (typeDefinitions[typeIndex] == null)
                {
                    PushNewArrayElement(ref il, 0);
                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Stelem_Ref);
                }
                else
                {
                    TypeReference typeRef = m_TypeRegAssembly.MainModule.ImportReference(typeDefinitions[typeIndex]);

                    PushNewArrayElement(ref il, typeIndex);
                    il.Emit(OpCodes.Ldtoken,
                        typeRef); // Push our meta-type onto the stack as it will be our arg to System.Type.GetTypeFromHandle
                    il.Emit(OpCodes.Call,
                        m_GetTypeFromHandleFnRef); // Call System.Type.GetTypeFromHandle with the above stack arg. Return value pushed on the stack
                    il.Emit(OpCodes.Stelem_Ref);
                }
            }

            StoreTopOfStackToStaticField(ref il, fieldDefinition);
        }

        internal void GenerateBoolArray(ref ILProcessor il, FieldDefinition fieldDefinition, List<bool> values)
        {
            var boolTypeDef = m_TypeRegAssembly.MainModule.ImportReference(typeof(bool));

            PushNewArray(ref il, boolTypeDef, values.Count);
            // Only need to load true values; false is default.
            for(int i=0; i<values.Count; ++i)
            {
                PushNewArrayElement(ref il, i);
                il.Emit(values[i] ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stelem_I1);
            }
            StoreTopOfStackToStaticField(ref il, fieldDefinition);
        }

        internal void GenerateStringArray(ref ILProcessor il, FieldDefinition fieldDefinition, List<string> values)
        {
            var stringTypeRef = m_TypeRegAssembly.MainModule.ImportReference(typeof(string));

            PushNewArray(ref il, stringTypeRef, values.Count);
            // Only need to load true values; false is default.
            for(int i=0; i<values.Count; ++i)
            {
                PushNewArrayElement(ref il, i);
                il.Emit(OpCodes.Ldstr, values[i]);
                il.Emit(OpCodes.Stelem_Ref);
            }
            StoreTopOfStackToStaticField(ref il, fieldDefinition);
        }

        /// <summary>
        /// Populates the registry's TypeInfo array in typeIndex order.
        /// </summary>
        internal void GenerateTypeInfoArray(ref ILProcessor il, in TypeGenInfoList typeGenInfoList)
        {
            PushNewArray(ref il, m_TypeInfoRef, m_TotalTypeCount);

            for (int i = 0; i < typeGenInfoList.Count; ++i)
            {
                var typeGenInfo = typeGenInfoList[i];

                if (i != (typeGenInfo.TypeIndex & ClearFlagsMask))
                    throw new ArgumentException("The typeGenInfo list is not in the correct order. This is a bug.");

                PushNewArrayElement(ref il, i);
                PushNewTypeInfoArray(ref il, m_RegTypeInfoArrayDef, typeGenInfo);
                il.Emit(OpCodes.Stelem_Any, m_TypeInfoRef);
            }

            StoreTopOfStackToStaticField(ref il, m_RegTypeInfoArrayDef);
        }

        internal void GenerateEqualityFunctions(ref ILProcessor il, TypeGenInfoList typeGenInfoList)
        {
            // List of instructions in an array where index == typeIndex
            var eqJumpTable = new List<Instruction>[typeGenInfoList.Count];
            var boxedEqJumpTable = new List<Instruction>[typeGenInfoList.Count];
            var boxedPtrEqJumpTable = new List<Instruction>[typeGenInfoList.Count];
            var hashJumpTable = new List<Instruction>[typeGenInfoList.Count];
            var boxedHashJumpTable = new List<Instruction>[typeGenInfoList.Count];

            // Begin iterating at 1 to skip the null type
            for (int i = 1; i < typeGenInfoList.Count; ++i)
            {
                var typeGenInfo = typeGenInfoList[i];
                var thisTypeRef = m_TypeRegAssembly.MainModule.ImportReference(typeGenInfo.TypeDefinition);

                // FIXME -- allow this for shared components that have managed fields
                if (typeGenInfo.IsManaged)
                {
                    eqJumpTable[i] = null;
                    boxedEqJumpTable[i] = null;
                    continue;
                }

                var typeRef = m_TypeRegAssembly.MainModule.ImportReference(typeGenInfo.TypeDefinition);
                var eqHelperInstance = m_EqHelperDef.MakeGenericInstanceType(typeRef).Resolve();

                // Store new equals fn to Equals member
                MethodReference equalsFn;
                {
                    equalsFn = GenerateEqualsFunction(typeGenInfo);
                    var equalsFnSpecialization = m_TypeRegAssembly.MainModule.ImportReference(eqHelperInstance.NestedTypes.First(f => f.Name == "EqualsFn")
                        .Methods.First(m => m.Name == ".ctor")
                        .MakeHostInstanceGeneric(typeRef));
                    var fieldEqualsRef = m_TypeRegAssembly.MainModule.ImportReference(MakeGenericFieldSpecialization(eqHelperInstance
                        .Fields
                        .First(f => f.Name == "Equals"), typeRef));

                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldftn, equalsFn);
                    il.Emit(OpCodes.Newobj, equalsFnSpecialization);
                    il.Emit(OpCodes.Stsfld, fieldEqualsRef);

                    // Equals function for operating on (void* lhs, void* rhs, int typeIndex) where the type isn't known by the user
                    {
                        var eqIL = m_RegEqualsFn.Body.GetILProcessor();

                        eqJumpTable[i] = new List<Instruction>();
                        var instructionList = eqJumpTable[i];

                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_0));
                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_1));
                        instructionList.Add(eqIL.Create(OpCodes.Call, equalsFn));

                        instructionList.Add(eqIL.Create(OpCodes.Ret));
                    }

                    // Equals function for operating on (object lhs, object rhs, int typeIndex) where the type isn't known by the user
                    {
                        var eqIL = m_RegBoxedEqualsFn.Body.GetILProcessor();

                        boxedEqJumpTable[i] = new List<Instruction>();
                        var instructionList = boxedEqJumpTable[i];

                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_0));
                        instructionList.Add(eqIL.Create(OpCodes.Unbox, thisTypeRef));
                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_1));
                        instructionList.Add(eqIL.Create(OpCodes.Unbox, thisTypeRef));
                        instructionList.Add(eqIL.Create(OpCodes.Call, equalsFn));

                        instructionList.Add(eqIL.Create(OpCodes.Ret));
                    }

                    // Equals function for operating on (object lhs, void* rhs, int typeIndex) where the type isn't known by the user
                    {
                        var eqIL = m_RegBoxedPtrEqualsFn.Body.GetILProcessor();

                        boxedPtrEqJumpTable[i] = new List<Instruction>();
                        var instructionList = boxedPtrEqJumpTable[i];

                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_0));
                        instructionList.Add(eqIL.Create(OpCodes.Unbox, thisTypeRef));
                        instructionList.Add(eqIL.Create(OpCodes.Ldarg_1));
                        instructionList.Add(eqIL.Create(OpCodes.Call, equalsFn));

                        instructionList.Add(eqIL.Create(OpCodes.Ret));
                    }
                }

                // Store new Hash fn to Hash member
                {
                    var hashFn = GenerateHashFunction(typeGenInfo.TypeDefinition);
                    var hashFnSpecialization = m_TypeRegAssembly.MainModule.ImportReference(eqHelperInstance.NestedTypes.First(f => f.Name == "HashFn")
                        .Methods.First(m => m.Name == ".ctor")
                        .MakeHostInstanceGeneric(typeRef));

                    var fieldHashRef = m_TypeRegAssembly.MainModule.ImportReference(MakeGenericFieldSpecialization(eqHelperInstance
                        .Fields
                        .First(f => f.Name == "Hash"), typeRef));

                    il.Emit(OpCodes.Ldnull);
                    il.Emit(OpCodes.Ldftn, hashFn);
                    il.Emit(OpCodes.Newobj, hashFnSpecialization);
                    il.Emit(OpCodes.Stsfld, fieldHashRef);

                    // Hash function for operating on (void* val, int typeIndex) where the type isn't known by the user
                    {
                        var hashIL = m_RegGetHashCodeFn.Body.GetILProcessor();
                        hashJumpTable[i] = new List<Instruction>();
                        var instructionList = hashJumpTable[i];

                        instructionList.Add(hashIL.Create(OpCodes.Ldarg_0));
                        instructionList.Add(hashIL.Create(OpCodes.Call, hashFn));
                        instructionList.Add(hashIL.Create(OpCodes.Ret));
                    }

                    // Hash function for operating on (object val, int typeIndex) where the type isn't known by the user
                    {
                        var hashIL = m_RegBoxedGetHashCodeFn.Body.GetILProcessor();
                        boxedHashJumpTable[i] = new List<Instruction>();
                        var instructionList = boxedHashJumpTable[i];

                        instructionList.Add(hashIL.Create(OpCodes.Ldarg_0));
                        instructionList.Add(hashIL.Create(OpCodes.Unbox, thisTypeRef));
                        instructionList.Add(hashIL.Create(OpCodes.Call, hashFn));
                        instructionList.Add(hashIL.Create(OpCodes.Ret));
                    }
                }
            }

            // We now have a list of instructions for each type on how to invoke the correct Equals/Hash call.
            // Now generate the void* Equals and Hash functions by making a jump table to those instructions

            // void* Equals
            {
                var eqIL = m_RegEqualsFn.Body.GetILProcessor();
                List<Instruction> jumps = new List<Instruction>(eqJumpTable.Length);
                Instruction loadTypeIndex = eqIL.Create(OpCodes.Ldarg_2);
                Instruction loadDefault = eqIL.Create(OpCodes.Ldc_I4_0); // default to false
                eqIL.Append(loadTypeIndex); // Load typeIndex

                foreach (var instructionList in eqJumpTable)
                {
                    if(instructionList == null)
                    {
                        jumps.Add(loadDefault);
                        continue;
                    }

                    // Add starting instruction to our jump table so we know which Equals IL block to execute
                    jumps.Add(instructionList[0]);

                    foreach (var instruction in instructionList)
                    {
                        eqIL.Append(instruction);
                    }
                }

                // default case
                eqIL.Append(loadDefault);
                eqIL.Append(eqIL.Create(OpCodes.Ret));

                // Since we are using InsertAfter these instructions are appended in reverse order to how they will appear
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Br, loadDefault));
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Switch, jumps.ToArray()));
            }

            // object Equals
            {
                var eqIL = m_RegBoxedEqualsFn.Body.GetILProcessor();
                List<Instruction> jumps = new List<Instruction>(boxedEqJumpTable.Length);
                Instruction loadTypeIndex = eqIL.Create(OpCodes.Ldarg_2);
                Instruction loadDefault = eqIL.Create(OpCodes.Ldc_I4_0); // default to false
                eqIL.Append(loadTypeIndex); // Load typeIndex

                foreach (var instructionList in boxedEqJumpTable)
                {
                    if(instructionList == null)
                    {
                        jumps.Add(loadDefault);
                        continue;
                    }

                    // Add starting instruction to our jump table so we know which Equals IL block to execute
                    jumps.Add(instructionList[0]);

                    foreach (var instruction in instructionList)
                    {
                        eqIL.Append(instruction);
                    }
                }

                // default case
                eqIL.Append(loadDefault);
                eqIL.Append(eqIL.Create(OpCodes.Ret));

                // Since we are using InsertAfter these instructions are appended in reverse order to how they will appear
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Br, loadDefault));
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Switch, jumps.ToArray()));
            }

            // object, void* Equals
            {
                var eqIL = m_RegBoxedPtrEqualsFn.Body.GetILProcessor();
                List<Instruction> jumps = new List<Instruction>(boxedPtrEqJumpTable.Length);
                Instruction loadTypeIndex = eqIL.Create(OpCodes.Ldarg_2);
                Instruction loadDefault = eqIL.Create(OpCodes.Ldc_I4_0); // default to false
                eqIL.Append(loadTypeIndex); // Load typeIndex

                foreach (var instructionList in boxedPtrEqJumpTable)
                {
                    if(instructionList == null)
                    {
                        jumps.Add(loadDefault);
                        continue;
                    }

                    // Add starting instruction to our jump table so we know which Equals IL block to execute
                    jumps.Add(instructionList[0]);

                    foreach (var instruction in instructionList)
                    {
                        eqIL.Append(instruction);
                    }
                }

                // default case
                eqIL.Append(loadDefault);
                eqIL.Append(eqIL.Create(OpCodes.Ret));

                // Since we are using InsertAfter these instructions are appended in reverse order to how they will appear
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Br, loadDefault));
                eqIL.InsertAfter(loadTypeIndex, eqIL.Create(OpCodes.Switch, jumps.ToArray()));
            }

            // void* Hash
            {
                var hashIL = m_RegGetHashCodeFn.Body.GetILProcessor();
                List<Instruction> jumps = new List<Instruction>(hashJumpTable.Length);
                Instruction loadTypeIndex = hashIL.Create(OpCodes.Ldarg_1);
                Instruction loadDefault = hashIL.Create(OpCodes.Ldc_I4_0); // default to 0 for the hash
                hashIL.Append(loadTypeIndex); // Load typeIndex

                foreach (var instructionList in hashJumpTable)
                {
                    if (instructionList == null)
                    {
                        jumps.Add(loadDefault);
                        continue;
                    }

                    // Add starting instruction to our jump table so we know which Equals IL block to execute
                    jumps.Add(instructionList[0]);

                    foreach (var instruction in instructionList)
                    {
                        hashIL.Append(instruction);
                    }
                }

                // default case
                hashIL.Append(loadDefault);
                hashIL.Append(hashIL.Create(OpCodes.Ret));

                // Since we are using InsertAfter these instructions are appended in reverse order to how they will appear in generated code
                hashIL.InsertAfter(loadTypeIndex, hashIL.Create(OpCodes.Br, loadDefault));
                hashIL.InsertAfter(loadTypeIndex, hashIL.Create(OpCodes.Switch, jumps.ToArray()));
            }

            // object Hash
            {
                var hashIL = m_RegBoxedGetHashCodeFn.Body.GetILProcessor();
                List<Instruction> jumps = new List<Instruction>(boxedHashJumpTable.Length);
                Instruction loadTypeIndex = hashIL.Create(OpCodes.Ldarg_1);
                Instruction loadDefault = hashIL.Create(OpCodes.Ldc_I4_0); // default to 0 for the hash
                hashIL.Append(loadTypeIndex); // Load typeIndex

                foreach (var instructionList in boxedHashJumpTable)
                {
                    if (instructionList == null)
                    {
                        jumps.Add(loadDefault);
                        continue;
                    }

                    // Add starting instruction to our jump table so we know which Equals IL block to execute
                    jumps.Add(instructionList[0]);

                    foreach (var instruction in instructionList)
                    {
                        hashIL.Append(instruction);
                    }
                }

                // default case
                hashIL.Append(loadDefault);
                hashIL.Append(hashIL.Create(OpCodes.Ret));

                // Since we are using InsertAfter these instructions are appended in reverse order to how they will appear in generated code
                hashIL.InsertAfter(loadTypeIndex, hashIL.Create(OpCodes.Br, loadDefault));
                hashIL.InsertAfter(loadTypeIndex, hashIL.Create(OpCodes.Switch, jumps.ToArray()));
            }
        }

        internal MethodReference GenerateEqualsFunction(TypeGenInfo typeGenInfo)
        {
            var typeRef = m_TypeRegAssembly.MainModule.ImportReference(typeGenInfo.TypeDefinition);
            var equalsFn = new MethodDefinition("DoEquals", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(bool)));
            var arg0 = new ParameterDefinition("i0", Mono.Cecil.ParameterAttributes.None, new ByReferenceType(typeRef));
            var arg1 = new ParameterDefinition("i1", Mono.Cecil.ParameterAttributes.None, new ByReferenceType(typeRef));
            equalsFn.Parameters.Add(arg0);
            equalsFn.Parameters.Add(arg1);
            m_RegClass.Methods.Add(equalsFn);

            var il = equalsFn.Body.GetILProcessor();

            GenerateEqualsFunctionRecurse(ref il, arg0, arg1, typeGenInfo);
            il.Emit(OpCodes.Ret);

            return equalsFn;
        }

        internal void GenerateEqualsFunctionRecurse(ref ILProcessor il, ParameterDefinition arg0, ParameterDefinition arg1, TypeGenInfo typeGenInfo)
        {
            int typeSize = typeGenInfo.AlignAndSize.size;

            // Raw memcmp of the two types
            // May need to do something more clever if this doesn't pan out for all types
            il.Emit(OpCodes.Ldarg, arg0);
            il.Emit(OpCodes.Ldarg, arg1);

            // The DotNet UnsafeUtility.MemCmp requires a long instead of an int
            if (m_Profile == Profile.DotNet)
            {
                il.Emit(OpCodes.Ldc_I8, (long)typeSize);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, typeSize);
            }

            il.Emit(OpCodes.Call, m_MemCmpFnRef);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ceq);
        }

        internal MethodReference GenerateHashFunction(TypeDefinition typeDef)
        {
            // http://www.isthe.com/chongo/tech/comp/fnv/index.html#FNV-1a
            const int FNV1a_32_OFFSET = unchecked((int)0x811C9DC5);

            var hashFn = new MethodDefinition("DoHash", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(int)));
            var arg0 = new ParameterDefinition("val", Mono.Cecil.ParameterAttributes.None, new ByReferenceType(m_TypeRegAssembly.MainModule.ImportReference(typeDef)));
            hashFn.Parameters.Add(arg0);
            m_RegClass.Methods.Add(hashFn);

            var il = hashFn.Body.GetILProcessor();
            List<Instruction> fieldLoadChain = new List<Instruction>();
            List<Instruction> hashInstructions = new List<Instruction>();

            GenerateHashFunctionRecurse(ref il, ref hashInstructions, ref fieldLoadChain, arg0, typeDef);
            if(hashInstructions.Count == 0)
            {
                // If the type doesn't contain any value types to hash we want to return 0 as the hash
                il.Emit(OpCodes.Ldc_I4_0);
            }
            else
            {
                EmitLoadConstant(ref il, FNV1a_32_OFFSET); // Initial Hash value

                foreach(var instruction in hashInstructions)
                {
                    il.Append(instruction);
                }
            }

            il.Emit(OpCodes.Ret);

            return hashFn;
        }

        internal void GenerateHashFunctionRecurse(ref ILProcessor il, ref List<Instruction> hashInstructions, ref List<Instruction> fieldLoadChain, ParameterDefinition val, TypeDefinition typeDef)
        {
            // http://www.isthe.com/chongo/tech/comp/fnv/index.html#FNV-1a
            const int FNV1a_32_PRIME = 16777619;

            foreach (var field in typeDef.Fields)
            {
                if (!field.IsStatic)
                {
                    var fieldDef = field.FieldType.Resolve();

                    // https://cecilifier.appspot.com/ outputs what you would expect here, that there is
                    // a bit in the attributes for 'Fixed.'. Specifically:
                    //     FieldAttributes.Fixed
                    // Haven't been able to find the actual numeric value. Until then, use this approach:
                    bool isFixed = fieldDef.ClassSize != -1 && fieldDef.Name.Contains(">e__FixedBuffer");
                    if (isFixed || field.FieldType.IsPrimitive || field.FieldType.IsPointer || fieldDef.IsEnum)
                    {
                        /*
                         Equivalent to:
                            hash *= FNV1a_32_PRIME;
                            hash ^= value;
                        */
                        hashInstructions.Add(il.Create(OpCodes.Ldc_I4, FNV1a_32_PRIME));
                        hashInstructions.Add(il.Create(OpCodes.Mul));


                        hashInstructions.Add(il.Create(OpCodes.Ldarg, val));
                        // Since we need to find the offset to nested members we need to chain field loads
                        hashInstructions.AddRange(fieldLoadChain);

                        if (isFixed)
                        {
                            hashInstructions.Add(il.Create(OpCodes.Ldflda, m_TypeRegAssembly.MainModule.ImportReference(field)));
                            hashInstructions.Add(il.Create(OpCodes.Ldind_I4));
                        }
                        else
                        {
                            if (field.FieldType.IsPointer && m_ArchBits == 64)
                            {
                                // Xor top and bottom of pointer
                                //
                                // Bottom 32 Bits
                                hashInstructions.Add(il.Create(OpCodes.Ldfld, m_TypeRegAssembly.MainModule.ImportReference(field)));
                                hashInstructions.Add(il.Create(OpCodes.Conv_I8)); // do I need this if we know the ptr is 64-bit
                                hashInstructions.Add(il.Create(OpCodes.Ldc_I4_M1)); // 0x00000000FFFFFFFF
                                hashInstructions.Add(il.Create(OpCodes.Conv_I8));

                                hashInstructions.Add(il.Create(OpCodes.And));

                                // Top 32 bits
                                hashInstructions.Add(il.Create(OpCodes.Ldarg, val));
                                hashInstructions.AddRange(fieldLoadChain);
                                hashInstructions.Add(il.Create(OpCodes.Ldfld, m_TypeRegAssembly.MainModule.ImportReference(field)));
                                hashInstructions.Add(il.Create(OpCodes.Conv_I8)); // do I need this if we know the ptr is 64-bit
                                hashInstructions.Add(il.Create(OpCodes.Ldc_I4, 32));
                                hashInstructions.Add(il.Create(OpCodes.Shr_Un));
                                hashInstructions.Add(il.Create(OpCodes.Ldc_I4_M1)); // 0x00000000FFFFFFFF
                                hashInstructions.Add(il.Create(OpCodes.Conv_I8));
                                hashInstructions.Add(il.Create(OpCodes.And));

                                hashInstructions.Add(il.Create(OpCodes.Xor));
                            }
                            else
                            {
                                hashInstructions.Add(il.Create(OpCodes.Ldfld, m_TypeRegAssembly.MainModule.ImportReference(field)));
                            }
                        }

                        // Subtle behavior. Aside from pointer types, we only load the first 4 bytes of the field.
                        // Makes hashing fast and simple, at the cost of more hash collisions.
                        hashInstructions.Add(il.Create(OpCodes.Conv_I4));
                        hashInstructions.Add(il.Create(OpCodes.Xor));
                    }
                    else if (field.FieldType.IsValueType)
                    {
                        fieldLoadChain.Add(Instruction.Create(OpCodes.Ldfld, m_TypeRegAssembly.MainModule.ImportReference(field)));
                        GenerateHashFunctionRecurse(ref il, ref hashInstructions, ref fieldLoadChain, val, fieldDef);
                        fieldLoadChain.RemoveAt(fieldLoadChain.Count - 1);
                    }
                }
            }
        }

        /// <summary>
        /// Function for the TypeManager to call to populate the TypeManager.s_Type type array the StaticTypeRegistry types.
        /// The logic for populating the TypeManager is done in the TypeManager.AddStaticTypesFromRegistry(ref TypeInfo[] tiArray, int count) function which we invoke here
        /// </summary>
        internal MethodDefinition GenerateRegisterStaticTypesFn()
        {
            var methodDefinition = new MethodDefinition("RegisterStaticTypes", MethodAttributes.Static | MethodAttributes.Public | MethodAttributes.HideBySig, m_TypeRegAssembly.MainModule.ImportReference(typeof(void)));

            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsflda, m_RegTypeInfoArrayDef));
            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Call, m_AddStaticTypesFromRegistryFnRef));

            if (m_Profile != Profile.DotNet) // Currently not supported in Hybrid builds
            {
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsflda, m_RegSystemSystemsArrayDef));
                methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Call, m_AddStaticSystemsFromRegistryFnRef));
            }

            methodDefinition.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            return methodDefinition;
        }

        private static bool ForceTypeAsInternalRecurse(TypeDefinition typeDef, bool updatedAsm)
        {
            if (typeDef == null)
                return updatedAsm;

            if (typeDef.IsNested)
            {
                if (!typeDef.IsNestedPublic)
                {
                    typeDef.IsNestedFamilyOrAssembly = true;
                    updatedAsm = true;
                }

                updatedAsm |= ForceTypeAsInternalRecurse(typeDef.DeclaringType, updatedAsm);
            }
            else if(!typeDef.IsPublic)
            {
                typeDef.IsNotPublic = true;
                updatedAsm = true;
            }

            return updatedAsm;
        }

        internal static bool ForceTypeAsInternal(TypeDefinition typeDef)
        {
            return ForceTypeAsInternalRecurse(typeDef, false);
        }

        internal static bool ForceTypeMembersAsInternal(TypeDefinition typeDef)
        {
            bool updatedAsm = false;

            if (typeDef == null)
                return updatedAsm;

            foreach (var field in typeDef.Fields)
            {
                if (!field.IsPublic)
                {
                    field.IsFamilyOrAssembly = true;
                    updatedAsm = true;
                }
            }

            return updatedAsm;
        }

        internal void FixupAssemblies(in List<TypeDefinition> typeDefList)
        {
            HashSet<AssemblyDefinition> changedAsmDef = new HashSet<AssemblyDefinition>();
            // Force all ECS types to be public
            foreach (var type in typeDefList)
            {
                bool assemblyUpdated = ForceTypeMembersAsInternal(type);
                assemblyUpdated |= ForceTypeAsInternal(type);

                if(assemblyUpdated)
                    changedAsmDef.Add(type.Module.Assembly);
            }

            // Write out all passed in assemblies
            foreach (var asm in m_AssemblyDefs)
            {
                if (asm.FullName.Contains(kStaticRegAsmName))
                    continue;

                if (changedAsmDef.Contains(asm))
                {
                    var internalsAccessorAttr = new CustomAttribute(asm.MainModule.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) })));
                    internalsAccessorAttr.ConstructorArguments.Add(new CustomAttributeArgument(asm.MainModule.TypeSystem.String, m_TypeRegAssembly.Name.Name));
                    asm.MainModule.Assembly.CustomAttributes.Add(internalsAccessorAttr);

                    // TODO: We can clean this up so it is all contained in the JobsGen cs for better separation
                    if (m_Profile != Profile.DotNet)
                    {
                        internalsAccessorAttr = new CustomAttribute(asm.MainModule.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) })));
                        internalsAccessorAttr.ConstructorArguments.Add(new CustomAttributeArgument(asm.MainModule.TypeSystem.String, "Unity.ZeroJobs"));
                        asm.MainModule.Assembly.CustomAttributes.Add(internalsAccessorAttr);

                        internalsAccessorAttr = new CustomAttribute(asm.MainModule.ImportReference(typeof(InternalsVisibleToAttribute).GetConstructor(new[] { typeof(string) })));
                        internalsAccessorAttr.ConstructorArguments.Add(new CustomAttributeArgument(asm.MainModule.TypeSystem.String, "Unity.Entities"));
                        asm.MainModule.Assembly.CustomAttributes.Add(internalsAccessorAttr);
                    }
                }

                string asmPath = asm.MainModule.FileName;
                string asmFileName = asmPath.Substring(asmPath.LastIndexOf(Path.DirectorySeparatorChar) + 1);
                var outPath = Path.Combine(m_OutputDir, asmFileName);

                var writerParams = new WriterParameters() { WriteSymbols = asm.MainModule.HasSymbols };

                try
                {
                    asm.MainModule.Write(outPath, writerParams);
                }catch(Exception)
                {
                    // There seems to be an issue with Mono.Cecil https://github.com/jbevain/cecil/issues/523
                    // where some type references can't be resolved on write. In which case we just copy the original file out unchanged
                    File.Copy(asm.MainModule.FileName, outPath, true);
                    if (asm.MainModule.HasSymbols)
                    {
                        Console.WriteLine($"Warning: TypeRegGen could not write new '{asm.MainModule.Name}'. Copying original instead.");
                        File.Copy(Path.ChangeExtension(asm.MainModule.FileName, "pdb"), Path.ChangeExtension(outPath, "pdb"), true);
                    }
                }
            }
        }

        internal void PushNewTypeInfoArray(ref ILProcessor il, FieldReference fieldRef, TypeGenInfo typeGenInfo)
        {
            int typeIndex = 0;
            TypeCategory typeCategory = TypeCategory.ComponentData;
            int entityCount = -1;
            int entityListIndex = -1;
            ulong memoryOrdering = 0UL;
            ulong stableHash = 0UL;
            int bufferCapacity = -1;
            int elementSize = 0;
            int sizeInChunk = 0;
            int alignment = 0;
            int maxChunkCapacity = int.MaxValue;
            int writeGroupCount = 0;
            int writeGroupListIndex = 0;
            int blobAssetRefOffsetCount = 0;
            int blobAssetRefOffsetIndex = 0;
            int fastEqualityIndex = 0; // Should always be 0 until we can remove this field altogether
            bool isManaged = typeGenInfo.IsManaged;

            if (typeGenInfo.TypeDefinition != null)
            {
                var typeDef = typeGenInfo.TypeDefinition;

                typeIndex = typeGenInfo.TypeIndex;
                typeCategory = typeGenInfo.TypeCategory;
                entityCount = typeGenInfo.EntityOffsets.Count;
                entityListIndex = typeGenInfo.EntityOffsetIndex;
                blobAssetRefOffsetCount = typeGenInfo.BlobAssetRefOffsets.Count;
                blobAssetRefOffsetIndex = typeGenInfo.BlobAssetRefOffsetIndex;
                writeGroupCount = typeGenInfo.WriteGroupTypeIndices.Count;
                writeGroupListIndex = typeGenInfo.WriteGroupsIndex;
                CalculateMemoryOrderingAndStableHash(typeGenInfo.TypeDefinition, out memoryOrdering, out stableHash);

                // Determine if there is a special buffer capacity set for the type
                if (typeCategory == TypeCategory.BufferData && typeDef.CustomAttributes.Count > 0)
                {
                    var forcedCapacityAttribute = typeDef.CustomAttributes.FirstOrDefault(ca => ca.Constructor.DeclaringType.Name == "InternalBufferCapacityAttribute");
                    if (forcedCapacityAttribute != null)
                    {
                        bufferCapacity = (int)forcedCapacityAttribute.ConstructorArguments
                            .First(arg => arg.Type.Name == "Int32")
                            .Value;
                    }
                }

                // Determine max chunk capacity constratins if any are specified
                if (typeDef.CustomAttributes.Count > 0)
                {
                    var maxChunkCapacityAttribute = typeDef.CustomAttributes.FirstOrDefault(ca => ca.Constructor.DeclaringType.Name == "MaximumChunkCapacityAttribute");
                    if (maxChunkCapacityAttribute != null)
                    {
                        maxChunkCapacity = (int)maxChunkCapacityAttribute.ConstructorArguments
                            .First(arg => arg.Type.Name == "Int32")
                            .Value;
                    }
                }

                if (!isManaged)
                {
                    var alignAndSize = typeGenInfo.AlignAndSize;

                    if (typeCategory != TypeCategory.ISharedComponentData)
                    {
                        elementSize = alignAndSize.empty ? 0 : alignAndSize.size;
                        sizeInChunk = elementSize;
                        //alignment = alignAndSize.align;

                        // We need to match what the dynamic type registry code currently does:
                        // - Default and maximum alignment is 16.
                        // - If the size of the data is a power of two, use it as the alignment.
                        // - Otherwise, 16.
                        alignment = 16;
                        if (sizeInChunk == 0)
                        {
                            alignment = 1;
                        }
                        else if (sizeInChunk < 16 && (sizeInChunk & (sizeInChunk - 1)) == 0)
                        {
                            alignment = sizeInChunk;
                        }
                    }

                    if (typeCategory == TypeCategory.BufferData)
                    {
                        // If we haven't overidden the bufferSize via an attribute
                        if (bufferCapacity == -1)
                        {
                            bufferCapacity = 128 / elementSize;
                        }

                        var bufferHeaderAlignAndSize = TypeUtils.AlignAndSizeOfType(m_BufferHeaderDef, m_ArchBits);
                        sizeInChunk = (bufferCapacity * elementSize) + 16; /*bufferHeaderAlignAndSize.size;*/
                    }
                }
            }

            // Push constructor arguments on to the stack
            EmitLoadConstant(ref il, typeIndex);
            EmitLoadConstant(ref il, (int) typeCategory);
            EmitLoadConstant(ref il, entityCount);
            EmitLoadConstant(ref il, entityCount <= 0 ? -1 : entityListIndex);
            EmitLoadConstant(ref il, memoryOrdering);
            EmitLoadConstant(ref il, stableHash);
            EmitLoadConstant(ref il, bufferCapacity);
            EmitLoadConstant(ref il, sizeInChunk);
            EmitLoadConstant(ref il, elementSize);
            EmitLoadConstant(ref il, alignment);
            EmitLoadConstant(ref il, maxChunkCapacity);
            EmitLoadConstant(ref il, writeGroupCount);
            EmitLoadConstant(ref il, writeGroupListIndex);
            EmitLoadConstant(ref il, blobAssetRefOffsetCount);
            EmitLoadConstant(ref il, blobAssetRefOffsetIndex);
            EmitLoadConstant(ref il, fastEqualityIndex);
            EmitLoadConstant(ref il, isManaged ? 1 : 0);

            il.Emit(OpCodes.Newobj, m_TypeInfoConstructorRef);
        }

        internal static void EmitLoadConstant(ref ILProcessor il, int val)
        {
            if(val >= -128 && val < 128)
            {
                switch(val)
                {
                    case -1:
                        il.Emit(OpCodes.Ldc_I4_M1); break;
                    case 0:
                        il.Emit(OpCodes.Ldc_I4_0); break;
                    case 1:
                        il.Emit(OpCodes.Ldc_I4_1); break;
                    case 2:
                        il.Emit(OpCodes.Ldc_I4_2); break;
                    case 3:
                        il.Emit(OpCodes.Ldc_I4_3); break;
                    case 4:
                        il.Emit(OpCodes.Ldc_I4_4); break;
                    case 5:
                        il.Emit(OpCodes.Ldc_I4_5); break;
                    case 6:
                        il.Emit(OpCodes.Ldc_I4_6); break;
                    case 7:
                        il.Emit(OpCodes.Ldc_I4_7); break;
                    case 8:
                        il.Emit(OpCodes.Ldc_I4_8); break;
                    default:
                        il.Emit(OpCodes.Ldc_I4_S, (sbyte) val); break;
                }
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, val);
            }
        }

        internal static void EmitLoadConstant(ref ILProcessor il, long val)
        {
            // III.3.40 ldc.<type> load numeric constant (https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf)
            long absVal = Math.Abs(val);

            // Value is represented in more than 32-bits
            if((absVal & 0x7FFFFFFF00000000) != 0)
            {
                il.Emit(OpCodes.Ldc_I8, val);
            }
            // Value is represented in 9 - 32 bits
            else if ((absVal & 0xFFFFFF00) != 0)
            {
                il.Emit(OpCodes.Ldc_I4, val);
                il.Emit(OpCodes.Conv_I8);
            }
            else
            {
                EmitLoadConstant(ref il, (int) val);
                il.Emit(OpCodes.Conv_I8);
            }
        }

        internal static void EmitLoadConstant(ref ILProcessor il, ulong val)
        {
            // III.3.40 ldc.<type> load numeric constant (https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf)

            // Value is represented in more than 32-bits
            if ((val & 0xFFFFFFFF00000000) != 0)
            {
                il.Emit(OpCodes.Ldc_I8, (long) val);
            }
            // Value is represented in 9 - 32 bits
            else if ((val & 0xFFFFFF00) != 0)
            {
                il.Emit(OpCodes.Ldc_I4, (int) val);
            }
            else
            {
                EmitLoadConstant(ref il, (int)val);
            }
            il.Emit(OpCodes.Conv_U8);
        }

        internal static void EmitInstructions(ref ILProcessor il, List<Instruction> instructions)
        {
            foreach (var i in instructions)
                il.Append(i);
        }

        internal static bool DoesTypeInheritInterface(TypeDefinition typeDef, string interfaceName)
        {
            if (typeDef == null)
                return false;

            return (typeDef.Interfaces.Any(i => i.InterfaceType.FullName.Equals(interfaceName)) || typeDef.NestedTypes.Any(t => DoesTypeInheritInterface(t, interfaceName)));
        }

        internal static void StoreTopOfStackToStaticField(ref ILProcessor il, FieldReference fieldRef)
        {
            il.Emit(OpCodes.Stsfld, fieldRef);
        }

        internal static void PushNewArray(ref ILProcessor il, TypeReference elementTypeRef, int arraySize)
        {
            EmitLoadConstant(ref il, arraySize);        // Push Array Size
            il.Emit(OpCodes.Newarr, elementTypeRef);    // Push array reference to top of stack
        }

        /// <summary>
        /// NOTE: This functions assumes the array is at the top of the stack
        /// </summary>
        internal static void PushNewArrayElement(ref ILProcessor il, int elementIndex)
        {
            il.Emit(OpCodes.Dup);                   // Duplicate top of stack (the array)
            EmitLoadConstant(ref il, elementIndex); // Push array index onto the stack
        }

        internal void InitializeTypeReferences()
        {
            // TypeManager
            var typeManagerDef = m_EntityAssembly.MainModule.Types
                    .First(t => t.FullName == "Unity.Entities.TypeManager");

            m_AddStaticTypesFromRegistryFnRef = m_TypeRegAssembly.MainModule.ImportReference(typeManagerDef
                .Methods
                .First(m => m.Name == "AddStaticTypesFromRegistry"));
            m_AddStaticSystemsFromRegistryFnRef = m_TypeRegAssembly.MainModule.ImportReference(typeManagerDef
                .Methods
                .First(m => m.Name == "AddStaticSystemsFromRegistry"));

            // TypeManager.TypeInfo
            var typeInfoDef = typeManagerDef
                    .NestedTypes
                    .First(m => m.Name == "TypeInfo");
            m_TypeInfoRef = m_TypeRegAssembly.MainModule.ImportReference(typeInfoDef);
            m_TypeInfoConstructorRef = m_TypeRegAssembly.MainModule.ImportReference(typeInfoDef
                    .Methods
                    .First(m => m.IsSpecialName && m.Name == ".ctor" && m.IsPublic));

            // TypeManager.EntityOffsetInfo
            var entityOffsetInfoDef = typeManagerDef
                    .NestedTypes
                    .First(nt => nt.Name == "EntityOffsetInfo");
            m_EntityOffsetInfoRef = m_TypeRegAssembly.MainModule.ImportReference(entityOffsetInfoDef);
            m_EntityOffsetInfoOffsetRef = m_TypeRegAssembly.MainModule.ImportReference(entityOffsetInfoDef
                    .Fields
                    .First(f => f.Name == "Offset"));

            // TypeManager.EqualityHelper
            m_EqHelperDef = typeManagerDef
                    .NestedTypes
                    .First(nt => nt.Name == "EqualityHelper`1");

            // Entities.BufferHeader
            m_BufferHeaderDef = m_EntityAssembly.MainModule.GetType("Unity.Entities.BufferHeader");

            // System.* Types
            var systemTypeRef = m_MscorlibAssembly.MainModule.GetType("System.Type");
            m_SystemTypeRef = m_TypeRegAssembly.MainModule.ImportReference(systemTypeRef);
            m_GetTypeFromHandleFnRef = m_TypeRegAssembly.MainModule.ImportReference(systemTypeRef
                .Methods
                .First(m => m.Name == "GetTypeFromHandle"));
            var objectRef = m_MscorlibAssembly.MainModule.GetType("System.Object");
            m_GetTypeFnRef = m_TypeRegAssembly.MainModule.ImportReference(objectRef
                .Methods
                .First(m => m.Name == "GetType"));

            m_IntPtrGetSizeFnRef = m_TypeRegAssembly.MainModule.ImportReference(m_MscorlibAssembly.MainModule.GetType("System.IntPtr")
                .Methods
                .First(m => m.Name == "get_Size"));
            m_InvalidOpExceptionCTORRef = m_TypeRegAssembly.MainModule.ImportReference(m_MscorlibAssembly.MainModule.GetType("System.InvalidOperationException")
                .Methods
                .First(m => m.Name == ".ctor" && m.Parameters.Count == 1 && m.Parameters[0].ParameterType.Name == "String"));

            MethodDefinition memCmpMethod;
            switch(m_Profile)
            {
                case Profile.DotNet:
                {
                    var coreModule = m_AssemblyDefs.FirstOrDefault(asm => asm.Name.Name == "UnityEngine.CoreModule");
                    memCmpMethod = coreModule.MainModule.GetType("Unity.Collections.LowLevel.Unsafe.UnsafeUtility")
                        .Methods
                        .First(m => m.Name == "MemCmp");
                    break;
                }
                case Profile.DOTSDotNet:
                {
                    var unityLowLevel = m_AssemblyDefs.FirstOrDefault(a => a.Name.Name == "Unity.LowLevel");
                    // grab the slow version from UnsafeUtility as the native version won't be linked in
                    memCmpMethod = unityLowLevel.MainModule.GetType("Unity.Collections.LowLevel.Unsafe.UnsafeUtility")
                        .Methods
                        .First(m => m.Name == "MemoryCompare");
                    break;
                }
                case Profile.DOTSNative:
                default:
                    {
                    memCmpMethod = m_MscorlibAssembly.MainModule.GetType("System.Runtime.CompilerServices.RuntimeHelpers")
                        ?.Methods
                        .FirstOrDefault(m => m.Name == "MemoryCompare");
                    break;
                }
            }

            m_MemCmpFnRef = m_TypeRegAssembly.MainModule.ImportReference(memCmpMethod);
        }

        internal static MethodReference MakeGenericMethodSpecialization(MethodReference self, params TypeReference[] arguments)
        {
            if (self.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceMethod(self);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        internal static FieldReference MakeGenericFieldSpecialization(FieldReference self, params TypeReference[] arguments)
        {
            if (self.DeclaringType.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceType(self.DeclaringType);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return new FieldReference(self.Name, self.FieldType, instance);
        }

        internal static TypeReference MakeGenericTypeSpecialization(TypeReference self, params TypeReference[] arguments)
        {
            if (self.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceType(self);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        void CalculateMemoryOrderingAndStableHash(TypeDefinition typeDef, out ulong memoryOrder, out ulong stableHash)
        {
            if (typeDef == null)
            {
                memoryOrder = 0;
                stableHash = 0;
                return;
            }

            stableHash = typeDef.CalculateStableTypeHash();
            memoryOrder = stableHash; // They are equivalent unless overridden below

            if (typeDef.GetHashCode() == m_EntityTypeDef.GetHashCode())
            {
                memoryOrder = 0;
            }
            else
            {
                if (typeDef.CustomAttributes.Count > 0)
                {
                    var forcedMemoryOrderAttribute = typeDef.CustomAttributes.FirstOrDefault(ca => ca.Constructor.DeclaringType.Name == "ForcedMemoryOrderingAttribute");
                    if (forcedMemoryOrderAttribute != null)
                    {
                        memoryOrder = (ulong) forcedMemoryOrderAttribute.ConstructorArguments
                            // despite the field being a 'ulong', Mono.Cecil says it's a UInt64. Not a big issue but a possible inconsistency so we check for either
                            .First(arg => arg.Type.Name == "UInt64" || arg.Type.Name == "ulong")
                            .Value;
                    }
                }
            }
        }

        List<ECSTypeInfo> ECSTypesToRegister = new List<ECSTypeInfo>
        {
            new ECSTypeInfo(){ TypeCategory = TypeCategory.ComponentData, FullTypeName = "Unity.Entities.IComponentData" },
            new ECSTypeInfo(){ TypeCategory = TypeCategory.ISharedComponentData, FullTypeName = "Unity.Entities.ISharedComponentData" },
            new ECSTypeInfo(){ TypeCategory = TypeCategory.BufferData, FullTypeName = "Unity.Entities.IBufferElementData" }
        };

        const string kStaticRegClassName = "StaticTypeRegistry";
        const string kStaticRegAsmName = "Unity.Entities.StaticTypeRegistry";
        const string kStaticRegAsmNameWithExtension = "Unity.Entities.StaticTypeRegistry.dll";

        // NOTE: These flags must match what is in Unity.Entities.TypeManager
        public const int HasNoEntityReferencesFlag = 1 << 25; // this flag is inverted to ensure the type id of Entity can still be 1
        public const int SystemStateTypeFlag = 1 << 26;
        public const int BufferComponentTypeFlag = 1 << 27;
        public const int SharedComponentTypeFlag = 1 << 28;
        public const int ChunkComponentTypeFlag = 1 << 29;
        public const int ZeroSizeInChunkTypeFlag = 1 << 30;
        public const int ClearFlagsMask = 0x00FFFFFF;
        public const int SystemStateSharedComponentTypeFlag = SystemStateTypeFlag | SharedComponentTypeFlag;

        int m_TotalTypeCount;
        int m_TotalEntityOffsetCount;
        int m_TotalBlobAssetRefOffsetCount;
        int m_TotalWriteGroupCount;
        int m_ArchBits;
        string m_OutputDir;
        Profile m_Profile;

        AssemblyDefinition m_TypeRegAssembly;
        AssemblyDefinition m_MscorlibAssembly;
        AssemblyDefinition m_EntityAssembly;
        TypeDefinition m_EntityTypeDef;
        List<AssemblyDefinition> m_AssemblyDefs;
        List<TypeDefinition> m_Systems;
        Dictionary<TypeDefinition, int> m_TypeDefToTypeIndex;

        // TypeReferences required for code gen
        TypeReference m_TypeInfoRef;
        MethodReference m_TypeInfoConstructorRef;
        MethodReference m_AddStaticTypesFromRegistryFnRef;
        MethodReference m_AddStaticSystemsFromRegistryFnRef;
        TypeReference m_EntityOffsetInfoRef;
        FieldReference m_EntityOffsetInfoOffsetRef;
        TypeDefinition m_EqHelperDef;
        TypeReference m_SystemTypeRef;
        MethodReference m_GetTypeFromHandleFnRef;
        MethodReference m_GetTypeFnRef;
        MethodReference m_MemCmpFnRef;
        TypeDefinition m_BufferHeaderDef;
        MethodReference m_IntPtrGetSizeFnRef;
        MethodReference m_InvalidOpExceptionCTORRef;

        TypeDefinition m_RegClass;

        // Static Registry Fields
        FieldDefinition m_RegTypeInfoArrayDef;
        FieldDefinition m_RegSystemTypeArrayDef;
        FieldDefinition m_RegSystemSystemsArrayDef;
        FieldDefinition m_RegEntityOffsetArrayDef;
        FieldDefinition m_RegBlobAssetReferneceOffsetsArrayDef;
        FieldDefinition m_WriteGroupArrayDef;
        FieldDefinition m_RegSystemIsGroupArrayDef;
        FieldDefinition m_RegSystemNameArrayDef;

        // Static Registry Methods
        MethodDefinition m_RegCCTOR;
        MethodDefinition m_RegEqualsFn;
        MethodDefinition m_RegBoxedEqualsFn;
        MethodDefinition m_RegBoxedPtrEqualsFn;
        MethodDefinition m_RegGetHashCodeFn;
        MethodDefinition m_RegBoxedGetHashCodeFn;
    }
}
