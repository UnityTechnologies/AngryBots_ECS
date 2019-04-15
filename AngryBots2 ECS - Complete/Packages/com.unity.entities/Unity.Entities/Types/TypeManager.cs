using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;

namespace Unity.Entities
{
    /// <summary>
    /// [WriteGroup] Can exclude components which are unknown at the time of creating the query that have been declared
    /// to write to the same component.
    ///
    /// This allows for extending systems of components safely without editing the previously existing systems.
    ///
    /// The goal is to have a way for systems that expect to transform data from one set of components (inputs) to
    /// another (output[s]) be able to declare that explicit transform, and they exclusively know about one set of
    /// inputs. If there are other inputs that want to write to the same output, the query shouldn't match because it's
    /// a nonsensical/unhandled setup. It's both a way to guard against nonsensical components (having two systems write
    /// to the same output value), and a way to "turn off" existing systems/queries by putting a component with the same
    /// write lock on an entity, letting another system handle it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class WriteGroupAttribute : Attribute
    {
        public WriteGroupAttribute(Type targetType)
        {
            TargetType = targetType;
        }

        public Type TargetType;
    }

    public static unsafe class TypeManager
    {
        [AttributeUsage(AttributeTargets.Struct)]
        public class ForcedMemoryOrderingAttribute : Attribute
        {
            public ForcedMemoryOrderingAttribute(ulong ordering)
            {
                MemoryOrdering = ordering;
            }

            public ulong MemoryOrdering;
        }

        [AttributeUsage(AttributeTargets.Struct)]
        public class TypeVersionAttribute : Attribute
        {
            public TypeVersionAttribute(int version)
            {
                TypeVersion = version;
            }

            public int TypeVersion;
        }

        public enum TypeCategory
        {
            ComponentData,
            BufferData,
            ISharedComponentData,
            EntityData,
            Class
        }

        public const int HasNoEntityReferencesFlag = 1 << 25; // this flag is inverted to ensure the type id of Entity can still be 1
        public const int SystemStateTypeFlag = 1 << 26;
        public const int BufferComponentTypeFlag = 1 << 27;
        public const int SharedComponentTypeFlag = 1 << 28;
        public const int ChunkComponentTypeFlag = 1<<29;
        public const int ZeroSizeInChunkTypeFlag = 1<<30;

        public const int ClearFlagsMask = 0x00FFFFFF;
        public const int SystemStateSharedComponentTypeFlag = SystemStateTypeFlag | SharedComponentTypeFlag;

        public const int MaximumChunkCapacity = int.MaxValue;
        public const int MaximumSupportedAlignment = 16;
        public const int MaximumTypesCount = 1024 * 10;

        private static volatile int s_Count;
        private static int s_InitCount = 0;
#if !NET_DOTS
        private static SpinLock s_CreateTypeLock;
#endif
        public static int ObjectOffset;

#if !NET_DOTS
        public static IEnumerable<TypeInfo> AllTypes { get { return Enumerable.Take(s_TypeInfos, s_Count); } }
        private static Dictionary<Type, int> s_ManagedTypeToIndex;
#endif
        private static TypeInfo[] s_TypeInfos;
        private static Type[] s_Systems;
        private static NativeHashMap<ulong, int> s_StableTypeHashToTypeIndex;

#if NET_DOTS
        private static List<FastEquality.TypeInfo> s_FastEqualityTypeInfoList;
        private static List<Type> s_DynamicTypeList;
        private static NativeList<int> s_WriteGroupList;
        private static NativeList<EntityOffsetInfo> s_EntityOffsetList;
        private static NativeList<EntityOffsetInfo> s_BlobAssetRefOffsetList;
#endif

#if !UNITY_ZEROPLAYER
        internal static Type UnityEngineComponentType;

        public static void RegisterUnityEngineComponentType(Type type)
        {
            if (type == null || !type.IsClass || type.IsInterface || type.FullName != "UnityEngine.Component")
                throw new ArgumentException($"{type} must be typeof(UnityEngine.Component).");
            UnityEngineComponentType = type;
        }
#endif
        public struct EntityOffsetInfo
        {
            public int Offset;
        }

        public struct StaticTypeLookup<T>
        {
            public static int typeIndex;
        }

        public struct EqualityHelper<T>
        {
            public delegate bool EqualsFn(ref T left, ref T right);
            public delegate int HashFn(ref T value);

            public static new EqualsFn Equals;
            public static HashFn Hash;
        }

#if !NET_DOTS
        // https://stackoverflow.com/a/27851610
        static bool IsZeroSizeStruct(Type t)
        {
            return t.IsValueType && !t.IsPrimitive &&
                   t.GetFields((BindingFlags)0x34).All(fi => IsZeroSizeStruct(fi.FieldType));
        }
#endif

        // NOTE: This type will be moved into Unity.Entities.StaticTypeRegistry once Static Type Registry generation is hooked into #!NET_DOTS builds
        public readonly struct TypeInfo
        {
#if !NET_DOTS
            public TypeInfo(Type type, int typeIndex, int size, TypeCategory category, FastEquality.TypeInfo typeInfo, EntityOffsetInfo[] entityOffsets, EntityOffsetInfo[] blobAssetRefOffsets, ulong memoryOrdering, int bufferCapacity, int elementSize, int alignmentInBytes, ulong stableTypeHash, int* writeGroups, int writeGroupCount, int maximumChunkCapacity)
            {
                Type = type;
                TypeIndex = typeIndex;
                SizeInChunk = size;
                Category = category;
                FastEqualityTypeInfo = typeInfo;
                EntityOffsetCount = entityOffsets?.Length ?? 0;
                EntityOffsets = entityOffsets;
                BlobAssetRefOffsetCount = blobAssetRefOffsets?.Length ?? 0;
                BlobAssetRefOffsets = blobAssetRefOffsets;
                MemoryOrdering = memoryOrdering;
                BufferCapacity = bufferCapacity;
                ElementSize = elementSize;
                AlignmentInBytes = alignmentInBytes;
                StableTypeHash = stableTypeHash;
                WriteGroups = writeGroups;
                WriteGroupCount = writeGroupCount;
                MaximumChunkCapacity = maximumChunkCapacity;
                // System state shared components are also considered system state components
                bool isSystemStateSharedComponent = typeof(ISystemStateSharedComponentData).IsAssignableFrom(type);
                bool isSystemStateBufferElement = typeof(ISystemStateBufferElementData).IsAssignableFrom(type);
                bool isSystemStateComponent = isSystemStateSharedComponent || isSystemStateBufferElement || typeof(ISystemStateComponentData).IsAssignableFrom(type);

                if (typeIndex != 0)
                {
                    if (SizeInChunk == 0)
                        TypeIndex |= ZeroSizeInChunkTypeFlag;

                    if(Category == TypeCategory.ISharedComponentData)
                        TypeIndex |= SharedComponentTypeFlag;

                    if (isSystemStateComponent)
                        TypeIndex |= SystemStateTypeFlag;

                    if (isSystemStateSharedComponent)
                        TypeIndex |= SystemStateSharedComponentTypeFlag;

                    if (BufferCapacity >= 0)
                        TypeIndex |= BufferComponentTypeFlag;

                    if (EntityOffsetCount == 0)
                        TypeIndex |= HasNoEntityReferencesFlag;
                }
            }

                public readonly Type Type;
                public readonly int TypeIndex;
                // Note that this includes internal capacity and header overhead for buffers.
                public readonly int SizeInChunk;
                // Normally the same as SizeInChunk (for components), but for buffers means size of an individual element.
                public readonly int ElementSize;
                // Sometimes we need to know not only the size, but the alignment.  For buffers this is the alignment
                // of an individual element.
                public readonly int AlignmentInBytes;
                public readonly int BufferCapacity;
                public readonly FastEquality.TypeInfo FastEqualityTypeInfo;
                public readonly TypeCategory Category;
                // While this information is available in the Array for EntityOffsets this field allows us to keep Tiny vs non-Tiny code paths the same
                public readonly int EntityOffsetCount;
                public readonly EntityOffsetInfo[] EntityOffsets;
                public readonly int BlobAssetRefOffsetCount;
                public readonly EntityOffsetInfo[] BlobAssetRefOffsets;
                public readonly ulong MemoryOrdering;
                public readonly ulong StableTypeHash;
                public readonly int* WriteGroups;
                public readonly int WriteGroupCount;
                public readonly int MaximumChunkCapacity;

                // Alignment of this type in a chunk.  Normally the same
                // as AlignmentInBytes, but that might be less than this
                // for buffer elements, whereas the buffer itself must
                // be aligned to the maximum.
                public int AlignmentInChunkInBytes {
                    get {
                        if (Category == TypeCategory.BufferData)
                            return MaximumSupportedAlignment;
                        return AlignmentInBytes;
                    }
                }

                public bool IsZeroSized => SizeInChunk == 0;
                public bool HasWriteGroups => WriteGroupCount > 0;
#else
            // NOTE: Any change to this constructor prototype requires a change in the TypeRegGen to match
            public TypeInfo(int typeIndex, TypeCategory category, int entityOffsetCount, int entityOffsetStartIndex,
                ulong memoryOrdering, ulong stableTypeHash, int bufferCapacity, int typeSize, int elementSize,
                int alignmentInBytes, int maxChunkCapacity, int writeGroupCount, int writeGroupStartIndex,
                int blobAssetRefOffsetCount, int blobAssetRefOffsetStartIndex, int fastEqualityIndex, bool usesDynamicInfo)
            {
                TypeIndex = typeIndex;
                Category = category;
                EntityOffsetCount = entityOffsetCount;
                EntityOffsetStartIndex = entityOffsetStartIndex;
                MemoryOrdering = memoryOrdering;
                StableTypeHash = stableTypeHash;
                BufferCapacity = bufferCapacity;
                SizeInChunk = typeSize;
                ElementSize = elementSize;
                AlignmentInBytes = alignmentInBytes;
                MaximumChunkCapacity = maxChunkCapacity;
                WriteGroupCount = writeGroupCount;
                WriteGroupStartIndex = writeGroupStartIndex;
                BlobAssetRefOffsetCount = blobAssetRefOffsetCount;
                BlobAssetRefOffsetStartIndex = blobAssetRefOffsetStartIndex;
                FastEqualityIndex = fastEqualityIndex; // Only used for Hybrid types (should be removed once we code gen all equality cases)
                UsesDynamicInfo = usesDynamicInfo;
            }

            public readonly int TypeIndex;
            // Note that this includes internal capacity and header overhead for buffers.
            public readonly int SizeInChunk;
            // Sometimes we need to know not only the size, but the alignment.  For buffers this is the alignment
            // of an individual element.
            public readonly int AlignmentInBytes;
            // Alignment of this type in a chunk.  Normally the same
            // as AlignmentInBytes, but that might be less than this
            // for buffer elements, whereas the buffer itself must
            // be aligned to the maximum.
            public int AlignmentInChunkInBytes
            {
                get
                {
                    if (Category == TypeCategory.BufferData)
                        return MaximumSupportedAlignment;
                    return AlignmentInBytes;
                }
            }
            // Normally the same as SizeInChunk (for components), but for buffers means size of an individual element.
            public readonly int ElementSize;
            public readonly int BufferCapacity;
            public readonly TypeCategory Category;
            public readonly ulong MemoryOrdering;
            public readonly ulong StableTypeHash;
            public readonly int EntityOffsetCount;
            internal readonly int EntityOffsetStartIndex;
            public readonly int BlobAssetRefOffsetCount;
            internal readonly int BlobAssetRefOffsetStartIndex;
            public readonly int WriteGroupCount;
            internal readonly int WriteGroupStartIndex;
            public readonly int MaximumChunkCapacity;
            internal readonly int FastEqualityIndex;
            internal readonly bool UsesDynamicInfo;

            public bool IsZeroSized => !UsesDynamicInfo && SizeInChunk == 0;
            public bool HasWriteGroups => WriteGroupCount > 0;

            // NOTE: We explictly exclude Type as a member of TypeInfo so the type can remain a ValueType
            public Type Type => StaticTypeRegistry.StaticTypeRegistry.Types[TypeIndex & ClearFlagsMask];

            // To consider: pinning the static array ptr and storing the correct offset pinned ptr for TypeInfo and the nremove these getters
            public EntityOffsetInfo* EntityOffsets
            {
                get
                {
                    if (EntityOffsetCount > 0)
                    {
                        return ((EntityOffsetInfo*)UnsafeUtility.AddressOf(ref StaticTypeRegistry.StaticTypeRegistry.EntityOffsets[0])) + EntityOffsetStartIndex;
                    }

                    return null;
                }
            }
            public EntityOffsetInfo* BlobAssetRefOffsets
            {
                get
                {
                    if (BlobAssetRefOffsetCount > 0)
                    {
                        return ((EntityOffsetInfo*)UnsafeUtility.AddressOf(ref StaticTypeRegistry.StaticTypeRegistry.BlobAssetReferenceOffsets[0])) + BlobAssetRefOffsetStartIndex;
                    }

                    return null;
                }
            }
            public int* WriteGroups
            {
                get
                {
                    if (WriteGroupCount> 0)
                    {
                        return ((int*)UnsafeUtility.AddressOf(ref StaticTypeRegistry.StaticTypeRegistry.WriteGroups[0])) + WriteGroupStartIndex;
                    }

                    return null;
                }
            }
#endif
        }

        public static unsafe TypeInfo GetTypeInfo(int typeIndex)
        {
            return s_TypeInfos[typeIndex & ClearFlagsMask];
        }

        public static TypeInfo GetTypeInfo<T>() where T : struct
        {
            return s_TypeInfos[GetTypeIndex<T>() & ClearFlagsMask];
        }

        public static Type GetType(int typeIndex)
        {
            return s_TypeInfos[typeIndex & ClearFlagsMask].Type;
        }

        public static int GetTypeCount()
        {
            return s_Count;
        }

        public static bool IsBuffer(int typeIndex) => (typeIndex & BufferComponentTypeFlag) != 0;
        public static bool IsSystemStateComponent(int typeIndex) => (typeIndex & SystemStateTypeFlag) != 0;
        public static bool IsSystemStateSharedComponent(int typeIndex) => (typeIndex & SystemStateSharedComponentTypeFlag) == SystemStateSharedComponentTypeFlag;
        public static bool IsSharedComponent(int typeIndex) => (typeIndex & SharedComponentTypeFlag) != 0;
        public static bool IsZeroSized(int typeIndex) => (typeIndex & ZeroSizeInChunkTypeFlag) != 0;
        public static bool IsChunkComponent(int typeIndex) => (typeIndex & ChunkComponentTypeFlag) != 0;
        public static bool HasEntityReferences(int typeIndex) => (typeIndex & HasNoEntityReferencesFlag) == 0;

        public static bool IgnoreDuplicateAdd(int typeIndex) => (typeIndex & ZeroSizeInChunkTypeFlag) != 0 && (typeIndex & SharedComponentTypeFlag) == 0;

        public static int MakeChunkComponentTypeIndex(int typeIndex) => (typeIndex | ChunkComponentTypeFlag | ZeroSizeInChunkTypeFlag);
        public static int ChunkComponentToNormalTypeIndex(int typeIndex) => s_TypeInfos[typeIndex & ClearFlagsMask].TypeIndex;

        // TODO: this creates a dependency on UnityEngine, but makes splitting code in separate assemblies easier. We need to remove it during the biggere refactor.
        private struct ObjectOffsetType
        {
            private void* v0;
            private void* v1;
        }

#if !NET_DOTS
        private static void AddTypeInfoToTables(TypeInfo typeInfo)
        {
            s_TypeInfos[typeInfo.TypeIndex & ClearFlagsMask] = typeInfo;
            s_StableTypeHashToTypeIndex.TryAdd(typeInfo.StableTypeHash, typeInfo.TypeIndex);
            s_ManagedTypeToIndex.Add(typeInfo.Type, typeInfo.TypeIndex);
            ++s_Count;
        }
#endif

                /// <summary>
        /// Initializes the TypeManager with all ECS type information. Additional calls without a matching Shutdown() simply increment the TypeManager ref-count and return
        /// </summary>
        /// <returns>Returns the TypeManager ref-count. It is expected for each Initialize() call to be matched by a Shutdown() call.</returns>
        public static int Initialize()
        {
            int initVal = Interlocked.Increment(ref s_InitCount);
            if (initVal != 1)
                return initVal;

            ObjectOffset = UnsafeUtility.SizeOf<ObjectOffsetType>();

            #if !NET_DOTS
                s_CreateTypeLock = new SpinLock();
                s_ManagedTypeToIndex = new Dictionary<Type, int>(1000);
            #endif

            s_TypeInfos = new TypeInfo[MaximumTypesCount];

            s_Count = 0;

            #if !NET_DOTS
                s_TypeInfos[s_Count++] = new TypeInfo(null, 0, 0, TypeCategory.ComponentData, FastEquality.TypeInfo.Null, null, null, 0, -1, 0, 1, 0, null, 0, int.MaxValue);
                InitializeAllComponentTypes();
            #else
                s_FastEqualityTypeInfoList = new List<FastEquality.TypeInfo>();
                s_DynamicTypeList = new List<Type>();
                s_EntityOffsetList = new NativeList<EntityOffsetInfo>(Allocator.Persistent);
                s_BlobAssetRefOffsetList = new NativeList<EntityOffsetInfo>(Allocator.Persistent);
                s_WriteGroupList = new NativeList<int>(Allocator.Persistent);

                // Registers all types and their static info from the static type rgistry
                // Note: this will call AddStaticTypesToRegistry which will initialize s_StableTypeHashToTypeIndex
                StaticTypeRegistry.StaticTypeRegistry.RegisterStaticTypes();
            #endif

            return initVal;
        }

        /// <summary>
        /// Removes all ECS type information and any allocated memory when the TypeManager ref-count == 1. That is,
        /// the TypeManager will shutdown on the last call to Shutdown() matching the number of invocations of Initialize().
        /// </summary>
        /// <returns>Returns the TypeManager ref-count. It is expected for each Shutdown() call to match a previous Initialize() call.</returns>
        public static int Shutdown()
        {
            int initVal = Interlocked.Decrement(ref s_InitCount);

            if (initVal < 0)
                throw new Exception("Calling TypeManager.Shutdown() before TypeManager.Initialize() has ever been called");

            if (initVal == 0)
            {
                #if !NET_DOTS
                    ClearStaticTypeLookup();
                #endif

                s_TypeInfos = null;
                s_Count = 0;
                s_StableTypeHashToTypeIndex.Dispose();
                #if NET_DOTS
                    s_Systems = null;
                    s_EntityOffsetList.Dispose();
                    s_BlobAssetRefOffsetList.Dispose();
                    s_WriteGroupList.Dispose();
                #else
                    s_ManagedTypeToIndex.Clear();
                #endif
            }

            return initVal;
        }

#if !NET_DOTS
        static void ClearStaticTypeLookup()
        {
            var staticLookupGenericType = Type.GetType("Unity.Entities.TypeManager+StaticTypeLookup`1");
            for (int i = 1; i < s_Count; ++i)
            {
                var type = s_TypeInfos[i].Type;
                var staticLookupType = staticLookupGenericType.MakeGenericType(type);
                var typeIndexField = staticLookupType.GetField("typeIndex", BindingFlags.Static | BindingFlags.Public);
                typeIndexField.SetValue(null, 0);
            }
        }
#endif

#if NET_DOTS
        // Called by the StaticTypeRegistry
        internal static void AddStaticTypesFromRegistry(in TypeInfo[] typeInfoArray/*, int count*/)
        {
            if (typeInfoArray.Length >= MaximumTypesCount)
                throw new Exception("More types detected than MaximumTypesCount. Increase the static buffer size.");

            s_Count = 0;

            if (s_StableTypeHashToTypeIndex.IsCreated)
                s_StableTypeHashToTypeIndex.Dispose();

            s_StableTypeHashToTypeIndex = new NativeHashMap<ulong, int>(typeInfoArray.Length * 2, Allocator.Persistent); // Extra room added for dynamically added types

            for (int i = 0; i < typeInfoArray.Length; ++i)
            {
                TypeInfo typeInfo = typeInfoArray[i];
                s_TypeInfos[s_Count++] = typeInfo;

                if (!s_StableTypeHashToTypeIndex.TryAdd(typeInfo.StableTypeHash, typeInfo.TypeIndex))
                    throw new Exception("Failed to add hash to StableTypeHash -> typeIndex dictionary.");
            }
        }

        // Called by the StaticTypeRegistry
        internal static void AddStaticSystemsFromRegistry(in Type[] systemArray)
        {
            s_Systems = systemArray;
        }
#endif

#if !NET_DOTS

        static void InitializeAllComponentTypes()
        {
            var lockTaken = false;
            try
            {
                s_CreateTypeLock.Enter(ref lockTaken);

                var componentTypeSet = new HashSet<Type>();

                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!IsAssemblyReferencingEntities(assembly))
                        continue;

                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.IsAbstract || !type.IsValueType)
                            continue;

                        // XXX There's a bug in the Unity Mono scripting backend where if the
                        // Mono type hasn't been initialized, the IsUnmanaged result is wrong.
                        // We force it to be fully initialized by creating an instance until
                        // that bug is fixed.
                        try
                        {
                            var inst = Activator.CreateInstance(type);
                        } catch (Exception)
                        {
                            // ignored
                        }

                        if (!UnsafeUtility.IsUnmanaged(type))
                            continue;

                        if (typeof(IComponentData).IsAssignableFrom(type) ||
                            typeof(ISharedComponentData).IsAssignableFrom(type) ||
                            typeof(IBufferElementData).IsAssignableFrom(type))
                        {
                            componentTypeSet.Add(type);
                        }
                    }
                }

                s_StableTypeHashToTypeIndex = new NativeHashMap<ulong, int>(componentTypeSet.Count * 2, Allocator.Persistent); // Extra room added for dynamically added types

                // This must always be first so that Entity is always first in the archetype
                AddTypeInfoToTables(new TypeInfo(typeof(Entity), 1, sizeof(Entity), TypeCategory.EntityData,
                    FastEquality.CreateTypeInfo<Entity>(), EntityRemapUtility.CalculateEntityOffsets<Entity>(), null, 0, -1,
                    sizeof(Entity), UnsafeUtility.AlignOf<Entity>(), CalculateStableTypeHash(typeof(Entity)), null, 0, int.MaxValue));

                var componentTypeCount = componentTypeSet.Count;
                var componentTypes = new Type[componentTypeCount];
                componentTypeSet.CopyTo(componentTypes);

                var typeIndexByType = new Dictionary<Type, int>();
                var writeGroupByType = new Dictionary<int, HashSet<int>>();
                var startTypeIndex = s_Count;

                for (int i = 0; i < componentTypes.Length; i++)
                {
                    typeIndexByType[componentTypes[i]] = startTypeIndex + i;
                }

                GatherWriteGroups(componentTypes, startTypeIndex, typeIndexByType, writeGroupByType);
                AddAllComponentTypes(componentTypes, startTypeIndex, writeGroupByType);
            }
            finally
            {
                if (lockTaken)
                {
                    s_CreateTypeLock.Exit(true);
                }
            }
        }

        private static void AddAllComponentTypes(Type[] componentTypes, int startTypeIndex, Dictionary<int, HashSet<int>> writeGroupByType)
        {
            for (int i = 0; i < componentTypes.Length; i++)
            {
                var type = componentTypes[i];
                var expectedTypeIndex = startTypeIndex + i;
                var index = FindTypeIndex(type);
                if (index != -1)
                    throw new InvalidOperationException("ComponentType cannot be initialized more than once.");

                TypeInfo typeInfo;
                if (writeGroupByType.ContainsKey(expectedTypeIndex))
                {
                    var writeGroupSet = writeGroupByType[expectedTypeIndex];
                    var writeGroupCount = writeGroupSet.Count;
                    var writeGroupArray = new int[writeGroupCount];
                    writeGroupSet.CopyTo(writeGroupArray);

                    fixed (int* writeGroups = writeGroupArray)
                    {
                        typeInfo = BuildComponentType(type, writeGroups, writeGroupCount);
                    }
                }
                else
                {
                    typeInfo = BuildComponentType(type);
                }

                var typeIndex = typeInfo.TypeIndex & TypeManager.ClearFlagsMask;
                if (expectedTypeIndex != typeIndex)
                    throw new InvalidOperationException("ComponentType.TypeIndex does not match precalculated index.");

                AddTypeInfoToTables(typeInfo); //c
            }
        }

        private static void GatherWriteGroups(Type[] componentTypes, int startTypeIndex, Dictionary<Type, int> typeIndexByType,
            Dictionary<int, HashSet<int>> writeGroupByType)
        {
            for (int i = 0; i < componentTypes.Length; i++)
            {
                var type = componentTypes[i];
                var typeIndex = startTypeIndex + i;

                foreach (var attribute in type.GetCustomAttributes(typeof(WriteGroupAttribute)))
                {
                    var attr = (WriteGroupAttribute) attribute;
                    if (!typeIndexByType.ContainsKey(attr.TargetType))
                    {
                        Debug.LogError($"GatherWriteGroups: looking for {attr.TargetType} but it hasn't been set up yet");
                    }

                    int targetTypeIndex = typeIndexByType[attr.TargetType];

                    if (!writeGroupByType.ContainsKey(targetTypeIndex))
                    {
                        var targetList = new HashSet<int>();
                        writeGroupByType.Add(targetTypeIndex, targetList);
                    }

                    writeGroupByType[targetTypeIndex].Add(typeIndex);
                }
            }
        }

        private static int FindTypeIndex(Type type)
        {
            if (type == null)
                return 0;

            int res;
            if (s_ManagedTypeToIndex.TryGetValue(type, out res))
                return res;
            else
                return -1;
        }
#else
        private static int FindTypeIndex(Type type)
        {
            for (var i = 0; i != s_Count; i++)
            {
                var c = s_TypeInfos[i];
                if (c.Type == type)
                    return c.TypeIndex;
            }

            throw new ArgumentException("Tried to GetTypeIndex for type that has not been set up by the static type registry.");
        }
#endif

        public static int GetTypeIndex<T>()
        {
            var typeIndex = StaticTypeLookup<T>.typeIndex;
            // with NET_DOTS, this could be a straight return without a 0 check,
            // if the static typereg code were to set the generic field during init
            if (typeIndex != 0)
                return typeIndex;

            typeIndex = GetTypeIndex(typeof(T));

            StaticTypeLookup<T>.typeIndex = typeIndex;
            return typeIndex;
        }

        public static int GetTypeIndex(Type type)
        {
            var index = FindTypeIndex(type);
            return index != -1 ? index : CreateTypeIndexThreadSafe(type);
        }

        public static bool Equals<T>(ref T left, ref T right) where T : struct
        {
            #if !NET_DOTS
                var typeInfo = TypeManager.GetTypeInfo<T>().FastEqualityTypeInfo;
                return FastEquality.Equals(ref left, ref right, typeInfo);
            #else
                return EqualityHelper<T>.Equals(ref left, ref right);
            #endif
        }

        public static bool Equals(void* left, void* right, int typeIndex)
        {
            #if !NET_DOTS
                var typeInfo = TypeManager.GetTypeInfo(typeIndex).FastEqualityTypeInfo;
                return FastEquality.Equals(left, right, typeInfo);
            #else
                return StaticTypeRegistry.StaticTypeRegistry.Equals(left, right, typeIndex & ClearFlagsMask);
            #endif
        }

        public static bool Equals(object left, object right, int typeIndex)
        {
            #if !NET_DOTS
                var leftptr = (byte*) UnsafeUtility.PinGCObjectAndGetAddress(left, out var lhandle) + ObjectOffset;
                var rightptr = (byte*) UnsafeUtility.PinGCObjectAndGetAddress(right, out var rhandle) + ObjectOffset;

                var typeInfo = GetTypeInfo(typeIndex).FastEqualityTypeInfo;
                var result = FastEquality.Equals(leftptr, rightptr, typeInfo);

                UnsafeUtility.ReleaseGCObject(lhandle);
                UnsafeUtility.ReleaseGCObject(rhandle);
                return result;
            #else
                return StaticTypeRegistry.StaticTypeRegistry.Equals(left, right, typeIndex & ClearFlagsMask);
            #endif
        }

        public static bool Equals(object left, void* right, int typeIndex)
        {
            #if !NET_DOTS
                var leftptr = (byte*) UnsafeUtility.PinGCObjectAndGetAddress(left, out var lhandle) + ObjectOffset;

                var typeInfo = GetTypeInfo(typeIndex).FastEqualityTypeInfo;
                var result = FastEquality.Equals(leftptr, right, typeInfo);

                UnsafeUtility.ReleaseGCObject(lhandle);
                return result;
            #else
                return StaticTypeRegistry.StaticTypeRegistry.Equals(left, right, typeIndex & ClearFlagsMask);
            #endif
        }

        public static int GetHashCode<T>(ref T val) where T : struct
        {
            #if !NET_DOTS
                var typeInfo = TypeManager.GetTypeInfo<T>().FastEqualityTypeInfo;
                return FastEquality.GetHashCode(ref val, typeInfo);
            #else
                return EqualityHelper<T>.Hash(ref val);
            #endif
        }

        public static int GetHashCode(void* val, int typeIndex)
        {
            #if !NET_DOTS
                var typeInfo = TypeManager.GetTypeInfo(typeIndex).FastEqualityTypeInfo;
                return FastEquality.GetHashCode(val, typeInfo);
            #else
                return StaticTypeRegistry.StaticTypeRegistry.GetHashCode(val, typeIndex & ClearFlagsMask);
            #endif
        }

        public static int GetHashCode(object val, int typeIndex)
        {
            #if !NET_DOTS
                var ptr = (byte*) UnsafeUtility.PinGCObjectAndGetAddress(val, out var handle) + ObjectOffset;

                var typeInfo = GetTypeInfo(typeIndex).FastEqualityTypeInfo;
                var result = FastEquality.GetHashCode(ptr, typeInfo);

                UnsafeUtility.ReleaseGCObject(handle);
                return result;
            #else
                return StaticTypeRegistry.StaticTypeRegistry.BoxedGetHashCode(val, typeIndex & ClearFlagsMask);
            #endif
        }

        public static int GetTypeIndexFromStableTypeHash(ulong stableTypeHash)
        {
            if(s_StableTypeHashToTypeIndex.TryGetValue(stableTypeHash, out var typeIndex))
                return typeIndex;
            return -1;
        }

#if !NET_DOTS
        public static bool IsAssemblyReferencingEntities(Assembly assembly)
        {
            const string entitiesAssemblyName = "Unity.Entities";
            if (assembly.GetName().Name.Contains(entitiesAssemblyName))
                return true;

            var referencedAssemblies = assembly.GetReferencedAssemblies();
            foreach(var referenced in referencedAssemblies)
                if (referenced.Name.Contains(entitiesAssemblyName))
                    return true;
            return false;
        }
#endif

        /// <summary>
        /// Return an array of all the Systems in use. (They are found
        /// at compile time, and inserted by code generation.)
        /// </summary>
        public static Type[] GetSystems()
        {
            return StaticTypeRegistry.StaticTypeRegistry.Systems;
        }

        public static string[] SystemNames
        {
            get { return StaticTypeRegistry.StaticTypeRegistry.SystemName; }
        }

        public static string SystemName(Type t)
        {
            #if NET_DOTS
                int index = GetSystemTypeIndex(t);
                if (index < 0 || index >= SystemNames.Length) return "null";
                return SystemNames[index];
            #else
                return t.FullName;
            #endif
        }

        public static int GetSystemTypeIndex(Type t)
        {
            var systems = StaticTypeRegistry.StaticTypeRegistry.Systems;
            for (int i = 0; i < systems.Length; ++i)
            {
                if (t == systems[i]) return i;
            }
            throw new Exception("GetSystemTypeID invalid Type t");
        }

        public static bool IsSystemAGroup(Type t)
        {
            #if !NET_DOTS
                return t.IsSubclassOf(typeof(ComponentSystemGroup));
            #else
                int index = GetSystemTypeIndex(t);
                var isGroup = StaticTypeRegistry.StaticTypeRegistry.SystemIsGroup[index];
                return isGroup;
            #endif
        }

        /// <summary>
        /// Construct a System from a Type. Uses the same list in GetSystems()
        /// </summary>
        ///
        public static ComponentSystemBase ConstructSystem(Type systemType)
        {
            var obj = StaticTypeRegistry.StaticTypeRegistry.CreateSystem(systemType);
            if (!(obj is ComponentSystemBase))
                throw new Exception("Null casting in Construct System. Bug in TypeManager.");
            return obj as ComponentSystemBase;
        }

        public static T ConstructSystem<T>(Type systemType) where T : ComponentSystemBase
        {
            return (T) ConstructSystem(systemType);
        }

        public static T ConstructSystem<T>() where T : ComponentSystemBase
        {
            return ConstructSystem<T>(typeof(T));
        }

        /// <summary>
        /// Get all the attribute objects for a System.
        /// </summary>
        public static Attribute[] GetSystemAttributes(Type systemType)
        {
            return StaticTypeRegistry.StaticTypeRegistry.GetSystemAttributes(systemType);
        }

        /// <summary>
        /// Get all the attribute objects of Type attributeType for a System.
        /// </summary>
        public static Attribute[] GetSystemAttributes(Type systemType, Type attributeType)
        {
            #if !NET_DOTS
                var objArr = systemType.GetCustomAttributes(attributeType, true);
                var attr = new Attribute[objArr.Length];
                for (int i = 0; i < objArr.Length; i++) {
                    attr[i] = objArr[i] as Attribute;
                }
                return attr;
            #else
                Attribute[] attr = StaticTypeRegistry.StaticTypeRegistry.GetSystemAttributes(systemType);
                int count = 0;
                for (int i = 0; i < attr.Length; ++i)
                {
                    if (attr[i].GetType() == attributeType)
                    {
                        ++count;
                    }
                }
                Attribute[] result = new Attribute[count];
                count = 0;
                for (int i = 0; i < attr.Length; ++i)
                {
                    if (attr[i].GetType() == attributeType)
                    {
                        result[count++] = attr[i];
                    }
                }
                return result;
            #endif
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private static readonly Type[] s_SingularInterfaces =
        {
            typeof(IComponentData),
            typeof(IBufferElementData),
            typeof(ISharedComponentData),
        };

        internal static void CheckComponentType(Type type)
        {
            int typeCount = 0;
            foreach (Type t in s_SingularInterfaces)
            {
                if (t.IsAssignableFrom(type))
                    ++typeCount;
            }

            if (typeCount > 1)
                throw new ArgumentException($"Component {type} can only implement one of IComponentData, ISharedComponentData and IBufferElementData");
        }
#endif

        public static NativeArray<int> GetWriteGroupTypes(int typeIndex)
        {
            var type = GetTypeInfo(typeIndex);
            var writeGroups = type.WriteGroups;
            var writeGroupCount = type.WriteGroupCount;
            var arr = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<int>(writeGroups, writeGroupCount, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref arr, AtomicSafetyHandle.Create());
#endif
            return arr;
        }

        internal static int NextPowerOfTwo(int n)
        {
            if (n == 0)
                return 1;
            n--;
            n |= n >> 1;
            n |= n >> 2;
            n |= n >> 4;
            n |= n >> 8;
            n |= n >> 16;
            return n + 1;
        }

        internal static int AlignUp(int value, int alignment)
        {
            int mask = alignment - 1;
            if ((value & ~mask) == 0)
                return value;
            return (value + mask) & ~mask;
        }

        internal static bool IsPowerOfTwo(int value)
        {
            return (value & (value - 1)) == 0;
        }

#if !NET_DOTS
        //
        // The reflection-based type registration path that we can't support with tiny csharp profile.
        // A generics compile-time path is temporarily used (see later in the file) until we have
        // full static type info generation working.
        //
        static EntityOffsetInfo[] CalculatBlobAssetRefOffsets(Type type)
        {
            var offsets = new List<EntityOffsetInfo>();
            CalculatBlobAssetRefOffsetsRecurse(ref offsets, type, 0);
            if (offsets.Count > 0)
                return offsets.ToArray();
            else
                return null;
        }

        static void CalculatBlobAssetRefOffsetsRecurse(ref List<EntityOffsetInfo> offsets, Type type, int baseOffset)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BlobAssetReference<>))
            {
                offsets.Add(new EntityOffsetInfo { Offset = baseOffset });
            }
            else
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var field in fields)
                {
                    if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive)
                        CalculatBlobAssetRefOffsetsRecurse(ref offsets, field.FieldType, baseOffset + UnsafeUtility.GetFieldOffset(field));
                }
            }
        }

        // Todo: Replace with code in Entities.BuildUtils
        static ulong CalculateMemoryOrdering(Type type)
        {
            if (type == typeof(Entity))
                return 0;

            var forcedOrdering = type.GetCustomAttribute<ForcedMemoryOrderingAttribute>();
            if (forcedOrdering != null)
                return forcedOrdering.MemoryOrdering;

            var result = CalculateStableTypeHash(type);
            result = result != 0 ? result : 1;
            return result;
        }

        private static int CreateTypeIndexThreadSafe(Type type)
        {
            var lockTaken = false;
            try
            {
                s_CreateTypeLock.Enter(ref lockTaken);

                // After taking the lock, make sure the type hasn't been created
                // after doing the non-atomic FindTypeIndex
                var index = FindTypeIndex(type);
                if (index != -1)
                    return index;

                var componentType = BuildComponentType(type);

                AddTypeInfoToTables(componentType);
                return componentType.TypeIndex;
            }
            finally
            {
                if (lockTaken)
                {
                    s_CreateTypeLock.Exit(true);
                }
            }
        }

        private static ulong HashStringWithFNV1A64(string text)
        {
            // Using http://www.isthe.com/chongo/tech/comp/fnv/index.html#FNV-1a
            // with basis and prime:
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            ulong result = offsetBasis;
            foreach (var c in text)
            {
                result = prime * (result ^ (byte)(c & 255));
                result = prime * (result ^ (byte)(c >> 8));
            }
            return result;
        }

        // Todo: Replace with code in Entities.BuildUtils
        static ulong CalculateStableTypeHash(Type type)
        {
            return HashStringWithFNV1A64(type.AssemblyQualifiedName);
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void CheckIsAllowedAsComponentData(Type type, string baseTypeDesc)
        {
            if (UnsafeUtility.IsUnmanaged(type))
                return;

            // it can't be used -- so we expect this to find and throw
            ThrowOnDisallowedComponentData(type, type, baseTypeDesc);

            // if something went wrong adnd the above didn't throw, then throw
            throw new ArgumentException($"{type} cannot be used as component data for unknown reasons (BUG)");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void ThrowOnDisallowedComponentData(Type type, Type baseType, string baseTypeDesc)
        {
            if (type.IsPrimitive)
                return;

            // if it's a pointer, we assume you know what you're doing
            if (type.IsPointer)
                return;

            if (!type.IsValueType || type.IsByRef || type.IsClass || type.IsInterface || type.IsArray)
            {
                if (type == baseType)
                    throw new ArgumentException(
                        $"{type} is a {baseTypeDesc} and thus must be a struct containing only primitive or blittable members.");

                throw new ArgumentException($"{baseType} contains a field of {type}, which is neither primitive nor blittable.");
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                ThrowOnDisallowedComponentData(field.FieldType, baseType, baseTypeDesc);
            }
        }

        internal static TypeInfo BuildComponentType(Type type)
        {
            return BuildComponentType(type, null, 0);
        }

        internal static TypeInfo BuildComponentType(Type type, int* writeGroups, int writeGroupCount)
        {
            var componentSize = 0;
            TypeCategory category;
            var typeInfo = FastEquality.TypeInfo.Null;
            EntityOffsetInfo[] entityOffsets = null;
            EntityOffsetInfo[] blobAssetRefOffsets = null;
            int bufferCapacity = -1;
            var memoryOrdering = CalculateMemoryOrdering(type);
            var stableTypeHash = CalculateStableTypeHash(type);
            var maxChunkCapacity = MaximumChunkCapacity;

            var maxCapacityAttribute = type.GetCustomAttribute<MaximumChunkCapacityAttribute>();
            if (maxCapacityAttribute != null)
                maxChunkCapacity = maxCapacityAttribute.Capacity;

            int elementSize = 0;
            int alignmentInBytes = 0;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (type.IsInterface)
                throw new ArgumentException($"{type} is an interface. It must be a concrete type.");
#endif
            if (typeof(IComponentData).IsAssignableFrom(type))
            {
                CheckIsAllowedAsComponentData(type, nameof(IComponentData));

                category = TypeCategory.ComponentData;

                int sizeInBytes = UnsafeUtility.SizeOf(type);
                // TODO: Implement UnsafeUtility.AlignOf(type)
                // Assume an alignment of 16, unless the type itself is smaller and a power of two
                alignmentInBytes = MaximumSupportedAlignment;
                if (sizeInBytes < alignmentInBytes && IsPowerOfTwo(sizeInBytes))
                    alignmentInBytes = sizeInBytes;

                if (TypeManager.IsZeroSizeStruct(type))
                    componentSize = 0;
                else
                    componentSize = sizeInBytes;

                typeInfo = FastEquality.CreateTypeInfo(type);
                entityOffsets = EntityRemapUtility.CalculateEntityOffsets(type);
                blobAssetRefOffsets = CalculatBlobAssetRefOffsets(type);
            }
            else if (typeof(IBufferElementData).IsAssignableFrom(type))
            {
                CheckIsAllowedAsComponentData(type, nameof(IBufferElementData));

                category = TypeCategory.BufferData;

                int sizeInBytes = UnsafeUtility.SizeOf(type);
                // TODO: Implement UnsafeUtility.AlignOf(type)
                // Assume an alignment of 16, unless the type itself is smaller
                alignmentInBytes = MaximumSupportedAlignment;
                if (sizeInBytes < alignmentInBytes && IsPowerOfTwo(sizeInBytes))
                    alignmentInBytes = sizeInBytes;

                elementSize = sizeInBytes;

                var capacityAttribute = (InternalBufferCapacityAttribute) type.GetCustomAttribute(typeof(InternalBufferCapacityAttribute));
                if (capacityAttribute != null)
                    bufferCapacity = capacityAttribute.Capacity;
                else
                    bufferCapacity = 128 / elementSize; // Rather than 2*cachelinesize, to make it cross platform deterministic

                componentSize = sizeof(BufferHeader) + bufferCapacity * elementSize;
                typeInfo = FastEquality.CreateTypeInfo(type);
                entityOffsets = EntityRemapUtility.CalculateEntityOffsets(type);
                blobAssetRefOffsets = CalculatBlobAssetRefOffsets(type);
             }
            else if (typeof(ISharedComponentData).IsAssignableFrom(type))
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (!type.IsValueType)
                    throw new ArgumentException($"{type} is an ISharedComponentData, and thus must be a struct.");
#endif
                entityOffsets = EntityRemapUtility.CalculateEntityOffsets(type);
                category = TypeCategory.ISharedComponentData;
                typeInfo = FastEquality.CreateTypeInfo(type);
            }
            else if (type.IsClass)
            {
                category = TypeCategory.Class;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (type.FullName == "Unity.Entities.GameObjectEntity")
                    throw new ArgumentException(
                        "GameObjectEntity cannot be used from EntityManager. The component is ignored when creating entities for a GameObject.");
                if (UnityEngineComponentType == null)
                    throw new ArgumentException(
                        $"{type} cannot be used from EntityManager. If it inherits UnityEngine.Component, you must first register TypeManager.UnityEngineComponentType or include the Unity.Entities.Hybrid assembly in your build.");
                if (!UnityEngineComponentType.IsAssignableFrom(type))
                    throw new ArgumentException($"{type} must inherit {UnityEngineComponentType}.");
#endif
            }
            else
            {
                throw new ArgumentException($"{type} is not a valid component.");
            }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            CheckComponentType(type);
#endif
            int typeIndex = s_Count;
            return new TypeInfo(type, typeIndex, componentSize, category, typeInfo, entityOffsets, blobAssetRefOffsets, memoryOrdering,
                bufferCapacity, elementSize > 0 ? elementSize : componentSize, alignmentInBytes, stableTypeHash, writeGroups, writeGroupCount, maxChunkCapacity);
        }

        public static int CreateTypeIndexForComponent<T>() where T : struct, IComponentData
        {
            return GetTypeIndex(typeof(T));
        }

        public static int CreateTypeIndexForSharedComponent<T>() where T : struct, ISharedComponentData
        {
            return GetTypeIndex(typeof(T));
        }

        public static int CreateTypeIndexForBufferElement<T>() where T : struct, IBufferElementData
        {
            return GetTypeIndex(typeof(T));
        }
#else
        private static int CreateTypeIndexThreadSafe(Type type)
        {
            throw new ArgumentException("Tried to GetTypeIndex for type that has not been set up by the static registry.");
        }
#endif
    }
}
