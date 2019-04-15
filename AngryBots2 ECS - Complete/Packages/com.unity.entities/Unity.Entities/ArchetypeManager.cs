using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace Unity.Entities
{
    internal unsafe struct SharedComponentValues
    {
        public int* firstIndex;
        public int stride;
        public ref int this[int i] => ref *(int*)(((byte*)firstIndex) + i*stride);

        public static implicit operator SharedComponentValues(int* p)
        {
            return new SharedComponentValues {firstIndex = p, stride=sizeof(int)};
        }

        public bool EqualTo(SharedComponentValues otherValues, int sharedComponentCount)
        {
            for(int i=0; i<sharedComponentCount; ++i)
                if (otherValues[i] != this[i])
                    return false;
            return true;
        }

        public void CopyTo(int* dest, int startIndex, int count)
        {
            for (int i = 0; i < count; ++i)
                dest[i] = this[startIndex + i];
        }
    }

    internal struct ComponentTypeInArchetype
    {
        public readonly int TypeIndex;

        public bool IsBuffer => (TypeIndex & TypeManager.BufferComponentTypeFlag) != 0;
        public bool IsSystemStateComponent => (TypeIndex & TypeManager.SystemStateTypeFlag) != 0;
        public bool IsSystemStateSharedComponent => (TypeIndex & TypeManager.SystemStateSharedComponentTypeFlag) == TypeManager.SystemStateSharedComponentTypeFlag;
        public bool IsSharedComponent => (TypeIndex & TypeManager.SharedComponentTypeFlag) != 0;
        public bool IsZeroSized => (TypeIndex & TypeManager.ZeroSizeInChunkTypeFlag) != 0;
        public bool IsChunkComponent => (TypeIndex & TypeManager.ChunkComponentTypeFlag) != 0;
        public bool HasEntityReferences => (TypeIndex & TypeManager.HasNoEntityReferencesFlag) == 0;

        public ComponentTypeInArchetype(ComponentType type)
        {
            TypeIndex = type.TypeIndex;
        }

        public static bool operator == (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return lhs.TypeIndex == rhs.TypeIndex;
        }

        public static bool operator != (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return lhs.TypeIndex != rhs.TypeIndex;
        }

        // The comparison of ComponentTypeInArchetype is used to sort the type arrays in Archetypes
        // The type flags in the upper bits of the type index force the component types into the following order:
        // 1. Entity (Always has type index = 1)
        // 2. Non zero sized IComponentData
        // 3. Non zero sized ISystemStateComponentData
        // 4. Dynamic buffer components (IBufferElementData)
        // 5. System state dynamic buffer components (ISystemStateBufferElementData)
        // 6. Zero sized IComponentData
        // 7. Zero sized ISystemStateComponentData
        // 8. Shared components (ISharedComponentData)
        // 9. Shared system state components (ISystemStateSharedComponentData)
        //10. Chunk IComponentData
        //11. Chunk ISystemStateComponentData
        //12. Chunk Dynamic buffer components (IBufferElementData)
        //13. Chunk System state dynamic buffer components (ISystemStateBufferElementData)

        public static bool operator < (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return lhs.TypeIndex < rhs.TypeIndex;
        }

        public static bool operator > (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return lhs.TypeIndex > rhs.TypeIndex;
        }

        public static bool operator <= (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return !(lhs > rhs);
        }

        public static bool operator >= (ComponentTypeInArchetype lhs, ComponentTypeInArchetype rhs)
        {
            return !(lhs < rhs);
        }

        public static unsafe bool CompareArray(ComponentTypeInArchetype* type1, int typeCount1,
            ComponentTypeInArchetype* type2, int typeCount2)
        {
            if (typeCount1 != typeCount2)
                return false;
            for (var i = 0; i < typeCount1; ++i)
                if (type1[i] != type2[i])
                    return false;
            return true;
        }

        public ComponentType ToComponentType()
        {
            ComponentType type;
            type.TypeIndex = TypeIndex;
            type.AccessModeType = ComponentType.AccessMode.ReadWrite;
            return type;
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public override string ToString()
        {
            return ToComponentType().ToString();
        }
#endif
        public override bool Equals(object obj)
        {
            if (obj is ComponentTypeInArchetype) return (ComponentTypeInArchetype) obj == this;

            return false;
        }

        public override int GetHashCode()
        {
            return (TypeIndex * 5819);
        }
    }

    [Flags]
    internal enum ChunkFlags
    {
        None = 0,
        Locked = 1 << 0,
        LockedEntityOrder = 1 << 1
    }

    [StructLayout(LayoutKind.Explicit)]
    internal unsafe struct Chunk
    {
        // Chunk header START
        [FieldOffset(0)]
        public Archetype* Archetype;
        // 4-byte padding on 32-bit architectures here

        [FieldOffset(8)]
        public Entity metaChunkEntity;

        // This is meant as read-only.
        // ArchetypeManager.SetChunkCount should be used to change the count.
        [FieldOffset(16)]
        public int Count;
        [FieldOffset(20)]
        public int Capacity;

        // In hybrid mode, archetypes can contain non-ECS-type components which are managed objects.
        // In order to access them without a lot of overhead we conceptually store an Object[] in each chunk which contains the managed components.
        // The chunk does not really own the array though since we cannot store managed references in unmanaged memory,
        // so instead the ArchetypeManager has a list of Object[]s and the chunk just has an int to reference an Object[] by index in that list.
        [FieldOffset(24)]
        public int ManagedArrayIndex;

        [FieldOffset(28)]
        public int ListIndex;
        [FieldOffset(32)]
        public int ListWithEmptySlotsIndex;

        // Incrementing automatically for each chunk
        [FieldOffset(36)]
        public uint SequenceNumber;

        // Special chunk behaviors
        [FieldOffset(40)]
        public uint Flags;

        // 4-byte padding here, available for an int

        // Chunk header END

        // Component data buffer
        // This is where the actual chunk data starts.
        // It's declared like this so we can skip the header part of the chunk and just get to the data.
        [FieldOffset(48)]                         // (must be multiple of 16)
        public fixed byte Buffer[4];

        public const int kChunkSize = 16 * 1024 - 256; // allocate a bit less to allow for header overhead
        public const int kMaximumEntitiesPerChunk = kChunkSize / 8;

        public uint GetChangeVersion(int typeIndex)
        {
            return Archetype->Chunks.GetChangeVersion(typeIndex, ListIndex);
        }

        public void SetChangeVersion(int typeIndex, uint version)
        {
            Archetype->Chunks.SetChangeVersion(typeIndex, ListIndex, version);
        }

        public void SetAllChangeVersions(uint version)
        {
            Archetype->Chunks.SetAllChangeVersion(ListIndex, version);
        }

        public int GetSharedComponentValue(int typeOffset)
        {
            return Archetype->Chunks.GetSharedComponentValue(typeOffset, ListIndex);
        }

        public SharedComponentValues SharedComponentValues => Archetype->Chunks.GetSharedComponentValues(ListIndex);

        public static int GetChunkBufferSize()
        {
            return kChunkSize - (sizeof(Chunk) - 4);
        }

        public bool MatchesFilter(MatchingArchetype* match, ref EntityQueryFilter filter)
        {
            if ((filter.Type & FilterType.SharedComponent) != 0)
            {
                var sharedComponentsInChunk = SharedComponentValues;
                var filteredCount = filter.Shared.Count;

                fixed (int* indexInEntityQueryPtr = filter.Shared.IndexInEntityQuery, sharedComponentIndexPtr =
                    filter.Shared.SharedComponentIndex)
                {
                    for (var i = 0; i < filteredCount; ++i)
                    {
                        var indexInEntityQuery = indexInEntityQueryPtr[i];
                        var sharedComponentIndex = sharedComponentIndexPtr[i];
                        var componentIndexInArcheType = match->IndexInArchetype[indexInEntityQuery];
                        var componentIndexInChunk = componentIndexInArcheType - match->Archetype->FirstSharedComponent;
                        if (sharedComponentsInChunk[componentIndexInChunk] != sharedComponentIndex)
                            return false;
                    }
                }

                return true;
            }

            if ((filter.Type & FilterType.Changed) != 0)
            {
                var changedCount = filter.Changed.Count;

                var requiredVersion = filter.RequiredChangeVersion;
                fixed (int* indexInEntityQueryPtr = filter.Changed.IndexInEntityQuery)
                {
                    for (var i = 0; i < changedCount; ++i)
                    {
                        var indexInArchetype = match->IndexInArchetype[indexInEntityQueryPtr[i]];

                        var changeVersion = GetChangeVersion(indexInArchetype);
                        if (ChangeVersionUtility.DidChange(changeVersion, requiredVersion))
                            return true;
                    }
                }

                return false;
            }

            return true;
        }

        public int GetSharedComponentIndex(MatchingArchetype* match, int indexInEntityQuery)
        {
            var componentIndexInArcheType = match->IndexInArchetype[indexInEntityQuery];
            var componentIndexInChunk = componentIndexInArcheType - match->Archetype->FirstSharedComponent;
            return GetSharedComponentValue(componentIndexInChunk);
        }

        /// <summary>
        /// Returns true if Chunk is Locked
        /// </summary>
        public bool Locked => (Flags & (uint) ChunkFlags.Locked) != 0;
        public bool LockedEntityOrder => (Flags & (uint) ChunkFlags.LockedEntityOrder) != 0;
    }

    [DebuggerTypeProxy(typeof(ChunkListDebugView))]
    internal unsafe struct ChunkList
    {
        public Chunk** p;
        public int Count;
        public int Capacity;
    }

    // Stores change version numbers, shared component indices, and entity count for all chunks belonging to an archetype in SOA layout
    [DebuggerTypeProxy(typeof(ArchetypeChunkDataDebugView))]
    internal unsafe struct ArchetypeChunkData
    {
        public Chunk** p;
        public int* data;
        public int Capacity;
        public int Count;
        public readonly int SharedComponentCount;
        public readonly int EntityCountIndex;
        public readonly int Channels;

        public ArchetypeChunkData(int componentTypeCount, int sharedComponentCount)
        {
            data = null;
            p = null;
            Capacity = 0;
            Count = 0;
            SharedComponentCount = sharedComponentCount;
            EntityCountIndex = componentTypeCount + sharedComponentCount;
            Channels = componentTypeCount + sharedComponentCount + 1; // +1 for entity count per-chunk
        }

        public void Grow(int newCapacity)
        {
            Assert.IsTrue(newCapacity > Capacity);
            Chunk** newChunkData = (Chunk**)UnsafeUtility.Malloc(newCapacity*(Channels*sizeof(int) + sizeof(Chunk*)), 16, Allocator.Persistent);
            var newData = (int*) (newChunkData + newCapacity);

            UnsafeUtility.MemCpy(newChunkData, p, sizeof(Chunk*)*Count);

            for(int i=0;i<Channels;++i)
                UnsafeUtility.MemCpy(newData + i*newCapacity, data + i*Capacity, sizeof(int)*Count);

            UnsafeUtility.Free(p, Allocator.Persistent);
            data = newData;
            p = newChunkData;
            Capacity = newCapacity;
        }

        // typeOffset 0 is first shared component
        public int GetSharedComponentValue(int typeOffset, int chunkIndex)
        {
            return data[typeOffset*Capacity+chunkIndex];
        }

        public int* GetSharedComponentValueArrayForType(int typeOffset)
        {
            return data + typeOffset*Capacity;
        }

        public void SetSharedComponentValue(int typeOffset, int chunkIndex, int value)
        {
            data[typeOffset*Capacity+chunkIndex] = value;
        }

        public SharedComponentValues GetSharedComponentValues(int iChunk)
        {
            return new SharedComponentValues
            {
                firstIndex = data + iChunk,
                stride = Capacity*sizeof(int)
            };
        }

        public uint GetChangeVersion(int typeOffset, int chunkIndex)
        {
            return (uint)data[(typeOffset+SharedComponentCount)*Capacity+chunkIndex];
        }
        public void SetChangeVersion(int typeOffset, int chunkIndex, uint version)
        {
            data[(typeOffset+SharedComponentCount)*Capacity+chunkIndex] = (int)version;
        }
        public uint* GetChangeVersionArrayForType(int typeOffset)
        {
            return (uint*)data + (typeOffset+SharedComponentCount)*Capacity;
        }

        public int GetChunkEntityCount(int chunkIndex)
        {
            return data[(EntityCountIndex)*Capacity+chunkIndex];
        }
        public void SetChunkEntityCount(int chunkIndex, int count)
        {
            data[(EntityCountIndex)*Capacity+chunkIndex] = (int)count;
        }
        public int* GetChunkEntityCountArray()
        {
            return data + (EntityCountIndex)*Capacity;
        }

        public void Add(Chunk* chunk, SharedComponentValues sharedComponentIndices)
        {
            var chunkIndex = Count++;

            p[chunkIndex] = chunk;

            int* dst = data + chunkIndex;
            int i = 0;
            for (; i < SharedComponentCount; ++i)
            {
                *dst = sharedComponentIndices[i];
                dst += Capacity;
            }

            for (; i < EntityCountIndex; ++i)
            {
                *dst = 0;
                dst += Capacity;
            }

            *dst = chunk->Count;
        }

        public void RemoveAtSwapBack(int iChunk)
        {
            if (iChunk == --Count)
                return;

            p[iChunk] = p[Count];

            int* dst = data + iChunk;
            int* src = data + Count;

            for (int i = 0; i < Channels; ++i)
            {
                *dst = *src;
                dst += Capacity;
                src += Capacity;
            }
        }

        public void Dispose()
        {
            UnsafeUtility.Free(p, Allocator.Persistent);
            p = null;
            data = null;
            Capacity = 0;
            Count = 0;
        }

        public void SetAllChangeVersion(int chunkIndex, uint version)
        {
            for (int i = SharedComponentCount; i < EntityCountIndex; ++i)
                data[i * Capacity + chunkIndex] = (int)version;
        }
    }


    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Archetype
    {
        public ArchetypeChunkData Chunks;
        public ChunkList ChunksWithEmptySlots;

        public ChunkListMap FreeChunksBySharedComponents;

        public int EntityCount;
        public int ChunkCapacity;
        public int BytesPerInstance;

        public ComponentTypeInArchetype* Types;
        public int TypesCount;
        public int NonZeroSizedTypesCount;

        // Index matches archetype types
        public int* Offsets;
        public int* SizeOfs;
        public int* BufferCapacities;

        // TypesCount indices into Types/Offsets/SizeOfs in the order that the
        // components are laid out in memory.
        public int* TypeMemoryOrder;

        public int* ManagedArrayOffset;
        public int NumManagedArrays;

        public int FirstSharedComponent;
        public int NumSharedComponents;

        public Archetype* InstantiableArchetype;
        public Archetype* SystemStateResidueArchetype;
        public Archetype* MetaChunkArchetype;

        public EntityDataManager* EntityDataManager;

        public EntityRemapUtility.EntityPatchInfo* ScalarEntityPatches;
        public int                                 ScalarEntityPatchCount;

        public EntityRemapUtility.BufferEntityPatchInfo* BufferEntityPatches;
        public int                                       BufferEntityPatchCount;

        public bool SystemStateCleanupComplete;
        public bool SystemStateCleanupNeeded;
        public bool Disabled;
        public bool Prefab;
        public bool HasChunkComponents;
        public bool HasChunkHeader;
        public bool ContainsBlobAssetRefs;

        public override string ToString()
        {
            var info = "";
            for (var i = 0; i < TypesCount; i++)
            {
                var componentTypeInArchetype = Types[i];
                info += $"  - {componentTypeInArchetype}";
            }

            return info;
        }

        public ref UnsafePtrList ChunksWithEmptySlotsUnsafePtrList
        {
            get { return ref *(UnsafePtrList*)UnsafeUtility.AddressOf(ref ChunksWithEmptySlots); }
        }

        public void AddToChunkList(Chunk *chunk, SharedComponentValues sharedComponentIndices, uint changeVersion)
        {
            chunk->ListIndex = Chunks.Count;
            if (Chunks.Count == Chunks.Capacity)
            {
                int newCapacity = Chunks.Capacity == 0 ? 1 : Chunks.Capacity * 2;
                if (Chunks.data <= sharedComponentIndices.firstIndex &&
                    sharedComponentIndices.firstIndex < Chunks.data + Chunks.Count)
                {
                    int sourceChunk = (int)(sharedComponentIndices.firstIndex - Chunks.data);
                    // The shared component indices we are inserting belong to the same archetype so they need to be adjusted after reallocation
                    Chunks.Grow(newCapacity);
                    sharedComponentIndices = Chunks.GetSharedComponentValues(sourceChunk);
                }
                else
                    Chunks.Grow(newCapacity);
            }

            Chunks.Add(chunk, sharedComponentIndices);
        }
        public void RemoveFromChunkList(Chunk *chunk)
        {
            Chunks.RemoveAtSwapBack(chunk->ListIndex);
            var chunkThatMoved = Chunks.p[chunk->ListIndex];
            chunkThatMoved->ListIndex = chunk->ListIndex;
        }
        public void AddToChunkListWithEmptySlots(Chunk *chunk)
        {
            chunk->ListWithEmptySlotsIndex = ChunksWithEmptySlots.Count;
            ChunksWithEmptySlotsUnsafePtrList.Add(chunk);
        }
        public void RemoveFromChunkListWithEmptySlots(Chunk *chunk)
        {
            ChunksWithEmptySlotsUnsafePtrList.RemoveAtSwapBack(chunk->ListWithEmptySlotsIndex, chunk);
            if (chunk->ListWithEmptySlotsIndex < ChunksWithEmptySlots.Count)
            {
                var chunkThatMoved = ChunksWithEmptySlots.p[chunk->ListWithEmptySlotsIndex];
                chunkThatMoved->ListWithEmptySlotsIndex = chunk->ListWithEmptySlotsIndex;
            }
        }
    }

    [DebuggerTypeProxy(typeof(ArchetypeListDebugView))]
    internal unsafe struct ArchetypeList
    {
        public Archetype** p;
        public int Count;
        public int Capacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe class ArchetypeManager : IDisposable
    {
        private ChunkAllocator m_ArchetypeChunkAllocator;

        private const int kMaximumEmptyChunksInPool = 16; // can't alloc forever

        internal ChunkList m_EmptyChunks;

        private readonly SharedComponentDataManager m_SharedComponentManager;
        private readonly EntityGroupManager m_groupManager;

        internal ArchetypeList m_Archetypes;

        private ManagedArrayStorage[] m_ManagedArrays = new ManagedArrayStorage[1];
        private ArchetypeListMap m_TypeLookup;

        private Archetype* m_entityOnlyArchetype;
        private Archetype* m_metaChunkRootArchetype;

        private static uint ms_SequenceNumber;

        private NativeHashMap<uint, IntPtr> m_ChunksBySequenceNumber;

        private EntityDataManager* m_Entities;

        public ref UnsafePtrList EmptyChunksUnsafePtrList
        {
            get { return ref *(UnsafePtrList*)UnsafeUtility.AddressOf(ref m_EmptyChunks); }
        }

        public ref UnsafePtrList ArchetypeUnsafePtrList
        {
            get { return ref *(UnsafePtrList*)UnsafeUtility.AddressOf(ref m_Archetypes); }
        }


        public ArchetypeManager(SharedComponentDataManager sharedComponentManager, EntityDataManager* entityDataManager, EntityGroupManager groupManager)
        {
            m_ArchetypeChunkAllocator = new ChunkAllocator();

            m_SharedComponentManager = sharedComponentManager;
            m_groupManager = groupManager;
            m_Entities = entityDataManager;
            m_TypeLookup = new ArchetypeListMap();
            m_TypeLookup.Init(16);
            m_ChunksBySequenceNumber = new NativeHashMap<uint, IntPtr>(4096, Allocator.Persistent);
            m_EmptyChunks = new ChunkList();
            m_Archetypes = new ArchetypeList();

            // Sanity check a few alignments
#if UNITY_ASSERTIONS
            // Buffer should be 16 byte aligned to ensure component data layout itself can gurantee being aligned
            var offset = UnsafeUtility.GetFieldOffset(typeof(Chunk).GetField("Buffer"));
            Assert.IsTrue(offset % TypeManager.MaximumSupportedAlignment == 0, $"Chunk buffer must be {TypeManager.MaximumSupportedAlignment} byte aligned (buffer offset at {offset})");
            Assert.IsTrue(sizeof(Entity) == 8, $"Unity.Entities.Entity is expected to be 8 bytes in size (is {sizeof(Entity)}); if this changes, update Chunk explicit layout");
#endif
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var bufHeaderSize = UnsafeUtility.SizeOf<BufferHeader>();
            Assert.IsTrue(bufHeaderSize % TypeManager.MaximumSupportedAlignment == 0,
                $"BufferHeader total struct size must be a multiple of the max supported alignment ({TypeManager.MaximumSupportedAlignment})");
#endif
        }

        public void Dispose()
        {
            // Move all chunks to become pooled chunks
            for (var i = 0; i < m_Archetypes.Count; i++)
            {
                var archetype = m_Archetypes.p[i];

                for (int c = 0; c != archetype->Chunks.Count; c++)
                {
                    var chunk = archetype->Chunks.p[c];

                    EntityDataManager.DeallocateBuffers(m_Entities, chunk);
                    UnsafeUtility.Free(archetype->Chunks.p[c], Allocator.Persistent);
                }

                archetype->Chunks.Dispose();
                archetype->ChunksWithEmptySlotsUnsafePtrList.Dispose();
                archetype->FreeChunksBySharedComponents.Dispose();
            }

            ArchetypeUnsafePtrList.Dispose();

            // And all pooled chunks
            for (var i = 0; i != m_EmptyChunks.Count; ++i)
            {
                var chunk = m_EmptyChunks.p[i];
                UnsafeUtility.Free(chunk, Allocator.Persistent);
            }
            EmptyChunksUnsafePtrList.Dispose();

            m_ManagedArrays = null;
            m_TypeLookup.Dispose();
            m_ChunksBySequenceNumber.Dispose();
            m_ArchetypeChunkAllocator.Dispose();
        }

        private void DeallocateManagedArrayStorage(int index)
        {
            Assert.IsTrue(m_ManagedArrays[index].ManagedArray != null);
            m_ManagedArrays[index].ManagedArray = null;
        }

        private int AllocateManagedArrayStorage(int length)
        {
            for (var i = 0; i < m_ManagedArrays.Length; i++)
                if (m_ManagedArrays[i].ManagedArray == null)
                {
                    m_ManagedArrays[i].ManagedArray = new object[length];
                    return i;
                }

            var oldLength = m_ManagedArrays.Length;
            Array.Resize(ref m_ManagedArrays, m_ManagedArrays.Length * 2);

            m_ManagedArrays[oldLength].ManagedArray = new object[length];

            return oldLength;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void AssertArchetypeComponents(ComponentTypeInArchetype* types, int count)
        {
            if (count < 1)
                throw new ArgumentException($"Invalid component count");
            if (types[0].TypeIndex == 0)
                throw new ArgumentException($"Component type may not be null");
            if (types[0].TypeIndex != TypeManager.GetTypeIndex<Entity>())
                throw new ArgumentException($"The Entity ID must always be the first component");

            for (var i = 1; i < count; i++)
            {
                if (types[i - 1].TypeIndex == types[i].TypeIndex)
                    throw new ArgumentException(
                        $"It is not allowed to have two components of the same type on the same entity. ({types[i - 1]} and {types[i]})");
            }
        }

        public Archetype* GetExistingArchetype(ComponentTypeInArchetype* typesSorted, int count)
        {
            return m_TypeLookup.TryGet(typesSorted, count);
        }

        public Archetype* GetEntityOnlyArchetype(EntityGroupManager groupManager)
        {
            if (m_entityOnlyArchetype == null)
            {
                ComponentTypeInArchetype entityType = new ComponentTypeInArchetype(ComponentType.ReadWrite<Entity>());
                m_entityOnlyArchetype = GetOrCreateArchetype(&entityType, 1, groupManager);
            }
            return m_entityOnlyArchetype;
        }

        public Archetype* GetArchetypeWithAddedComponentType(Archetype* archetype, ComponentType addedComponentType, EntityGroupManager groupManager, int* indexInTypeArray = null)
        {
            var componentType = new ComponentTypeInArchetype(addedComponentType);
            ComponentTypeInArchetype* newTypes = stackalloc ComponentTypeInArchetype[archetype->TypesCount + 1];

            var t = 0;
            while (t < archetype->TypesCount && archetype->Types[t] < componentType)
            {
                newTypes[t] = archetype->Types[t];
                ++t;
            }

            if(indexInTypeArray != null)
                *indexInTypeArray = t;

            if(archetype->Types[t] == componentType)
            {
                Assert.IsTrue(addedComponentType.IgnoreDuplicateAdd, $"{addedComponentType} is already part of the archetype.");
                // Tag component type is already there, no new archetype required.
                return null;
            }

            newTypes[t] = componentType;
            while (t < archetype->TypesCount)
            {
                newTypes[t + 1] = archetype->Types[t];
                ++t;
            }

            return GetOrCreateArchetype(newTypes,archetype->TypesCount + 1, groupManager);
        }

        public Archetype* GetArchetypeWithRemovedComponentType(Archetype* archetype, ComponentType addedComponentType, EntityGroupManager groupManager, int* indexInOldTypeArray = null)
        {
            var componentType = new ComponentTypeInArchetype(addedComponentType);
            ComponentTypeInArchetype* newTypes = stackalloc ComponentTypeInArchetype[archetype->TypesCount];

            var removedTypes = 0;
            for (var t = 0; t < archetype->TypesCount; ++t)
                if (archetype->Types[t].TypeIndex == componentType.TypeIndex)
                {
                    if(indexInOldTypeArray != null)
                        *indexInOldTypeArray = t;
                    ++removedTypes;
                }
                else
                    newTypes[t - removedTypes] = archetype->Types[t];

            return GetOrCreateArchetype(newTypes,archetype->TypesCount - removedTypes, groupManager);
        }

        public Archetype* GetOrCreateArchetype(ComponentTypeInArchetype* inTypesSorted, int count, EntityGroupManager groupManager)
        {
            var srcArchetype = GetExistingArchetype(inTypesSorted, count);
            if (srcArchetype != null)
                return srcArchetype;

            srcArchetype = CreateArchetypeInternal(inTypesSorted, count, groupManager);
            var types = stackalloc ComponentTypeInArchetype[count + 1];

            // Setup Instantiable archetype
            {
                UnsafeUtility.MemCpy(types, inTypesSorted, sizeof(ComponentTypeInArchetype) * count);

                var hasCleanup = false;
                var removedTypes = 0;
                var prefabTypeIndex = TypeManager.GetTypeIndex<Prefab>();
                var cleanupTypeIndex = TypeManager.GetTypeIndex<CleanupEntity>();
                for (var t = 0; t < srcArchetype->TypesCount; ++t)
                {
                    var type = srcArchetype->Types[t];

                    hasCleanup |= type.TypeIndex == cleanupTypeIndex;

                    var skip = type.IsSystemStateComponent || type.TypeIndex == prefabTypeIndex;
                    if (skip)
                        ++removedTypes;
                    else
                        types[t - removedTypes] = srcArchetype->Types[t];
                }

                // Entity has already been destroyed, so it shouldn't be instantiated anymore
                if (hasCleanup)
                {
                    srcArchetype->InstantiableArchetype = null;
                }
                else if (removedTypes > 0)
                {
                    var instantiableArchetype = GetOrCreateArchetype(types, count - removedTypes, groupManager);

                    srcArchetype->InstantiableArchetype = instantiableArchetype;
                    Assert.IsTrue(instantiableArchetype->InstantiableArchetype == instantiableArchetype);
                    Assert.IsTrue(instantiableArchetype->SystemStateResidueArchetype == null);
                }
                else
                {
                    srcArchetype->InstantiableArchetype = srcArchetype;
                }
            }

            // Setup System state cleanup archetype
            if (srcArchetype->SystemStateCleanupNeeded)
            {
                var cleanupEntityType = new ComponentTypeInArchetype(ComponentType.ReadWrite<CleanupEntity>());
                bool cleanupAdded = false;

                types[0] = inTypesSorted[0];
                var newTypeCount = 1;

                for (var t = 1; t < srcArchetype->TypesCount; ++t)
                {
                    var type = srcArchetype->Types[t];

                    if (type.IsSystemStateComponent)
                    {
                        if (!cleanupAdded && (cleanupEntityType < srcArchetype->Types[t]))
                        {
                            types[newTypeCount++] = cleanupEntityType;
                            cleanupAdded = true;
                        }

                        types[newTypeCount++] = srcArchetype->Types[t];
                    }
                }

                if (!cleanupAdded)
                {
                    types[newTypeCount++] = cleanupEntityType;
                }

                var systemStateResidueArchetype = GetOrCreateArchetype(types, newTypeCount, groupManager);
                srcArchetype->SystemStateResidueArchetype = systemStateResidueArchetype;

                Assert.IsTrue(systemStateResidueArchetype->SystemStateResidueArchetype == systemStateResidueArchetype);
                Assert.IsTrue(systemStateResidueArchetype->InstantiableArchetype == null);
            }

            // Setup meta chunk archetype
            if (count > 1)
            {
                types[0] = new ComponentTypeInArchetype(typeof(Entity));
                int metaArchetypeTypeCount = 1;
                for (int i = 1; i < count; ++i)
                {
                    var t = inTypesSorted[i];
                    ComponentType typeToInsert;
                    if (inTypesSorted[i].IsChunkComponent)
                    {
                        typeToInsert = new ComponentType
                        {
                            TypeIndex = TypeManager.ChunkComponentToNormalTypeIndex(t.TypeIndex)
                        };
                        SortingUtilities.InsertSorted(types, metaArchetypeTypeCount++, typeToInsert);
                    }
                }

                if (metaArchetypeTypeCount > 1)
                {
                    SortingUtilities.InsertSorted(types, metaArchetypeTypeCount++, new ComponentType(typeof(ChunkHeader)));
                    srcArchetype->MetaChunkArchetype = GetOrCreateArchetype(types, metaArchetypeTypeCount, groupManager);
                }
            }
            return srcArchetype;
        }

        void ChunkAllocate<T>(void* pointer, int count = 1) where T : struct
        {
            void** pointerToPointer = (void**)pointer;
            *pointerToPointer =
                m_ArchetypeChunkAllocator.Allocate(UnsafeUtility.SizeOf<T>() * count, UnsafeUtility.AlignOf<T>());
        }

        private Archetype* CreateArchetypeInternal(ComponentTypeInArchetype* types, int count,
            EntityGroupManager groupManager)
        {
            AssertArchetypeComponents(types, count);

            // Compute how many IComponentData types store Entities and need to be patched.
            // Types can have more than one entity, which means that this count is not necessarily
            // the same as the type count.
            var scalarEntityPatchCount = 0;
            var bufferEntityPatchCount = 0;
            var NumManagedArrays = 0;
            var NumSharedComponents = 0;
            for (var i = 0; i < count; ++i)
            {
                var ct = TypeManager.GetTypeInfo(types[i].TypeIndex);
                switch (ct.Category)
                {
                    case TypeManager.TypeCategory.ISharedComponentData:
                        ++NumSharedComponents;
                        break;
                    case TypeManager.TypeCategory.Class:
                        ++NumManagedArrays;
                        break;
                }
                var entityOffsets = ct.EntityOffsets;
                if (entityOffsets == null)
                    continue;
                if (ct.BufferCapacity >= 0)
                    bufferEntityPatchCount += ct.EntityOffsetCount;
                else if (ct.SizeInChunk > 0)
                    scalarEntityPatchCount += ct.EntityOffsetCount;
            }

            Archetype* type = null;
            ChunkAllocate<Archetype>(&type);
            ChunkAllocate<ComponentTypeInArchetype>(&type->Types, count);
            ChunkAllocate<int>(&type->Offsets, count);
            ChunkAllocate<int>(&type->SizeOfs, count);
            ChunkAllocate<int>(&type->BufferCapacities, count);
            ChunkAllocate<int>(&type->TypeMemoryOrder, count);
            ChunkAllocate<EntityRemapUtility.EntityPatchInfo>(&type->ScalarEntityPatches, scalarEntityPatchCount);
            ChunkAllocate<EntityRemapUtility.BufferEntityPatchInfo>(&type->BufferEntityPatches, bufferEntityPatchCount);
            type->ManagedArrayOffset = null;
            if (NumManagedArrays > 0)
                ChunkAllocate<int>(&type->ManagedArrayOffset, count);

            type->TypesCount = count;
            UnsafeUtility.MemCpy(type->Types, types, sizeof(ComponentTypeInArchetype) * count);
            type->EntityCount = 0;
            type->Chunks = new ArchetypeChunkData(count, NumSharedComponents);
            type->ChunksWithEmptySlots = new ChunkList();
            type->InstantiableArchetype = null;
            type->MetaChunkArchetype = null;
            type->SystemStateResidueArchetype = null;
            type->NumSharedComponents = 0;

            type->EntityDataManager = m_Entities;

            var disabledTypeIndex = TypeManager.GetTypeIndex<Disabled>();
            var prefabTypeIndex = TypeManager.GetTypeIndex<Prefab>();
            var chunkHeaderTypeIndex = TypeManager.GetTypeIndex<ChunkHeader>();
            type->Disabled = false;
            type->Prefab = false;
            type->HasChunkHeader = false;
            type->HasChunkComponents = false;
            type->ContainsBlobAssetRefs = false;
            type->NonZeroSizedTypesCount = 0;
            for (var i = 0; i < count; ++i)
            {
                if (!types[i].IsZeroSized)
                    type->NonZeroSizedTypesCount++;
                if (types[i].IsSharedComponent)
                    ++type->NumSharedComponents;
                if (types[i].TypeIndex == disabledTypeIndex)
                    type->Disabled = true;
                if (types[i].TypeIndex == prefabTypeIndex)
                    type->Prefab = true;
                if (types[i].TypeIndex == chunkHeaderTypeIndex)
                    type->HasChunkHeader = true;
                if (types[i].IsChunkComponent)
                    type->HasChunkComponents = true;
                if (TypeManager.GetTypeInfo(types[i].TypeIndex).BlobAssetRefOffsetCount > 0)
                    type->ContainsBlobAssetRefs = true;
            }

            var chunkDataSize = Chunk.GetChunkBufferSize();

            type->ScalarEntityPatchCount = scalarEntityPatchCount;
            type->BufferEntityPatchCount = bufferEntityPatchCount;

            type->BytesPerInstance = 0;

            // number of bytes we'll reserve for potential alignment
            int alignExtraSpace = 0;
            var alignments = stackalloc int[count];

            int maxCapacity = TypeManager.MaximumChunkCapacity;
            for (var i = 0; i < count; ++i)
            {
                var cType = TypeManager.GetTypeInfo(types[i].TypeIndex);
                var sizeOf = cType.SizeInChunk; // Note that this includes internal capacity and header overhead for buffers.
                if (types[i].IsChunkComponent)
                {
                    sizeOf = 0;
                }
                type->SizeOfs[i] = sizeOf;
                type->BufferCapacities[i] = cType.BufferCapacity;

                type->BytesPerInstance += sizeOf;
                maxCapacity = math.min(cType.MaximumChunkCapacity, maxCapacity);

                // explicitly 0 here for sizeof == 0, so that the usedBytes
                // calculation below properly ignores 0-sized components
                alignments[i] = sizeOf == 0 ? 0 : cType.AlignmentInChunkInBytes;
                alignExtraSpace += alignments[i];
            }

            Assert.IsTrue(maxCapacity >= 1, "MaximumChunkCapacity must be larger than 1");

            type->ChunkCapacity = math.min((chunkDataSize - alignExtraSpace) / type->BytesPerInstance, maxCapacity);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (type->BytesPerInstance > chunkDataSize)
                throw new ArgumentException(
                    $"Entity archetype component data is too large. The maximum component data is {chunkDataSize} but the component data is {type->BytesPerInstance}");

            Assert.IsTrue(Chunk.kMaximumEntitiesPerChunk >= type->ChunkCapacity);
#endif

            // For serialization a stable ordering of the components in the
            // chunk is desired. The type index is not stable, since it depends
            // on the order in which types are added to the TypeManager.
            // A permutation of the types ordered by a TypeManager-generated
            // memory ordering is used instead.
            var memoryOrderings = stackalloc UInt64[count];
            for (int i = 0; i < count; ++i)
                memoryOrderings[i] = TypeManager.GetTypeInfo(types[i].TypeIndex).MemoryOrdering;
            for (int i = 0; i < count; ++i)
            {
                int index = i;
                while (index > 1 && memoryOrderings[i] < memoryOrderings[type->TypeMemoryOrder[index - 1]])
                {
                    type->TypeMemoryOrder[index] = type->TypeMemoryOrder[index - 1];
                    --index;
                }
                type->TypeMemoryOrder[index] = i;
            }

            var usedBytes = 0;
            for (var i = 0; i < count; ++i)
            {
                var index = type->TypeMemoryOrder[i];
                var sizeOf = type->SizeOfs[index];

                // align usedBytes upwards (eating into alignExtraSpace) so that
                // this component actually starts at its required alignment.
                // Assumption is that the start of the entire data segment is at the
                // maximum possible alignment.
                usedBytes = TypeManager.AlignUp(usedBytes, alignments[index]);
                type->Offsets[index] = usedBytes;

                usedBytes += sizeOf * type->ChunkCapacity;
            }

            type->NumManagedArrays = NumManagedArrays;
            if (type->NumManagedArrays > 0)
            {
                var mi = 0;
                for (var i = 0; i < count; ++i)
                {
                    var index = type->TypeMemoryOrder[i];
                    var cType = TypeManager.GetTypeInfo(types[index].TypeIndex);
                    if (cType.Category == TypeManager.TypeCategory.Class)
                        type->ManagedArrayOffset[index] = mi++;
                    else
                        type->ManagedArrayOffset[index] = -1;
                }
            }

            type->NumSharedComponents = NumSharedComponents;

            type->FirstSharedComponent = -1;
            if (type->NumSharedComponents > 0)
            {
                int firstSharedComponent = 0;
                while (!types[++firstSharedComponent].IsSharedComponent);
                type->FirstSharedComponent = firstSharedComponent;
            }

            // Fill in arrays of scalar and buffer entity patches
            var scalarPatchInfo = type->ScalarEntityPatches;
            var bufferPatchInfo = type->BufferEntityPatches;
            for (var i = 0; i != count; i++)
            {
                var ct = TypeManager.GetTypeInfo(types[i].TypeIndex);
                #if !NET_DOTS
                    ulong handle = ~0UL;
                    var offsets = ct.EntityOffsets == null ? null : (TypeManager.EntityOffsetInfo*) UnsafeUtility.PinGCArrayAndGetDataAddress(ct.EntityOffsets, out handle);
                    var offsetCount = ct.EntityOffsetCount;
                #else
                    var offsets = ct.EntityOffsets;
                    var offsetCount = ct.EntityOffsetCount;
                #endif

                if (ct.BufferCapacity >= 0)
                {
                    bufferPatchInfo = EntityRemapUtility.AppendBufferEntityPatches(bufferPatchInfo, offsets, offsetCount, type->Offsets[i], type->SizeOfs[i], ct.ElementSize);
                }
                else if (ct.SizeInChunk > 0)
                {
                    scalarPatchInfo = EntityRemapUtility.AppendEntityPatches(scalarPatchInfo, offsets, offsetCount, type->Offsets[i], type->SizeOfs[i]);
                }

                #if !NET_DOTS
                    if(offsets != null)
                        UnsafeUtility.ReleaseGCObject(handle);
                #endif
            }
            Assert.AreEqual(scalarPatchInfo - type->ScalarEntityPatches, scalarEntityPatchCount);

            type->ScalarEntityPatchCount = scalarEntityPatchCount;
            type->BufferEntityPatchCount = bufferEntityPatchCount;

            // Update the list of all created archetypes
            ArchetypeUnsafePtrList.Add(type);

            type->FreeChunksBySharedComponents = new ChunkListMap();
            type->FreeChunksBySharedComponents.Init(16);

            m_TypeLookup.Add(type);

            type->SystemStateCleanupComplete = ArchetypeSystemStateCleanupComplete(type);
            type->SystemStateCleanupNeeded = ArchetypeSystemStateCleanupNeeded(type);

            groupManager.AddArchetypeIfMatching(type);

            return type;
        }

        private bool ArchetypeSystemStateCleanupComplete(Archetype* archetype)
        {
            if (archetype->TypesCount == 2 && archetype->Types[1].TypeIndex == TypeManager.GetTypeIndex<CleanupEntity>()) return true;
            return false;
        }

        private bool ArchetypeSystemStateCleanupNeeded(Archetype* archetype)
        {
            for (var t = 1; t < archetype->TypesCount; ++t)
            {
                var type = archetype->Types[t];
                if (type.IsSystemStateComponent)
                {
                    return true;
                }
            }

            return false;
        }

        public void AddExistingChunk(Chunk* chunk, int* sharedComponentIndices)
        {
            var archetype = chunk->Archetype;
            archetype->AddToChunkList(chunk, sharedComponentIndices, m_Entities->GlobalSystemVersion);
            archetype->EntityCount += chunk->Count;

            for (var i = 0; i < archetype->NumSharedComponents; ++i)
                m_SharedComponentManager.AddReference(sharedComponentIndices[i]);

            if (chunk->Count < chunk->Capacity)
                EmptySlotTrackingAddChunk(chunk);
        }

        public void ConstructChunk(Archetype* archetype, Chunk* chunk, SharedComponentValues sharedComponentValues)
        {
            chunk->Archetype = archetype;

            chunk->Count = 0;
            chunk->Capacity = archetype->ChunkCapacity;
            chunk->SequenceNumber = ms_SequenceNumber++;
            chunk->metaChunkEntity = Entity.Null;

            var numSharedComponents = archetype->NumSharedComponents;

            if (numSharedComponents > 0)
            {
                for (var i = 0; i < archetype->NumSharedComponents; ++i)
                {
                    var sharedComponentIndex = sharedComponentValues[i];
                    m_SharedComponentManager.AddReference(sharedComponentIndex);
                }
            }

            archetype->AddToChunkList(chunk, sharedComponentValues, m_Entities->GlobalSystemVersion);

            Assert.IsTrue(archetype->Chunks.Count != 0);

            // Chunk can't be locked at at construction time
            EmptySlotTrackingAddChunk(chunk);

            if (numSharedComponents == 0)
            {
                Assert.IsTrue(archetype->ChunksWithEmptySlots.Count != 0);
            }
            else
            {
                Assert.IsTrue(archetype->FreeChunksBySharedComponents.TryGet(chunk->SharedComponentValues, archetype->NumSharedComponents) != null);
            }

            if (archetype->NumManagedArrays > 0)
                chunk->ManagedArrayIndex = AllocateManagedArrayStorage(archetype->NumManagedArrays * chunk->Capacity);
            else
                chunk->ManagedArrayIndex = -1;

            chunk->Flags = 0;

            bool insertResult = m_ChunksBySequenceNumber.TryAdd(chunk->SequenceNumber, (IntPtr) chunk);
            Assert.IsTrue(insertResult);

            if (archetype->MetaChunkArchetype != null)
            {
                m_Entities->CreateMetaEntityForChunk(this, chunk);
            }
        }

        public Chunk* GetCleanChunk(Archetype* archetype, SharedComponentValues sharedComponentValues)
        {
            Chunk* newChunk;
            // Try empty chunk pool
            if (m_EmptyChunks.Count == 0)
            {
                // Allocate new chunk
                newChunk = (Chunk*)UnsafeUtility.Malloc(Chunk.kChunkSize, 64, Allocator.Persistent);
            }
            else
            {
                Assert.IsTrue(m_EmptyChunks.Count > 0);
                var back = m_EmptyChunks.Count - 1;
                newChunk = m_EmptyChunks.p[back];
                EmptyChunksUnsafePtrList.Resize(back);
            }

            ConstructChunk(archetype, newChunk, sharedComponentValues);

            return newChunk;
        }

        public Chunk* GetChunkWithEmptySlots(Archetype* archetype, SharedComponentValues sharedComponentValues)
        {
            if (archetype->NumSharedComponents == 0)
            {
                if (archetype->ChunksWithEmptySlots.Count != 0)
                {
                    var chunk = archetype->ChunksWithEmptySlots.p[0];
                    Assert.AreNotEqual(chunk->Count, chunk->Capacity);
                    return chunk;
                }
            }
            else
            {
                var chunk = archetype->FreeChunksBySharedComponents.TryGet(sharedComponentValues,
                    archetype->NumSharedComponents);
                if (chunk != null)
                {
                    return chunk;
                }
            }

            return GetCleanChunk(archetype, sharedComponentValues);
        }

        public int AllocateIntoChunk(Chunk* chunk)
        {
            int outIndex;
            var res = AllocateIntoChunk(chunk, 1, out outIndex);
            Assert.AreEqual(1, res);
            return outIndex;
        }

        public int AllocateIntoChunk(Chunk* chunk, int count, out int outIndex)
        {
            var allocatedCount = Math.Min(chunk->Capacity - chunk->Count, count);
            outIndex = chunk->Count;
            SetChunkCount(chunk, chunk->Count + allocatedCount);
            chunk->Archetype->EntityCount += allocatedCount;
            return allocatedCount;
        }

        /// <summary>
        /// Remove chunk from archetype tracking of chunks with available slots.
        /// - Does not check if chunk has space.
        /// - Does not check if chunk is locked.
        /// </summary>
        /// <param name="chunk"></param>
        internal static void EmptySlotTrackingRemoveChunk(Chunk* chunk)
        {
            if (chunk->Archetype->NumSharedComponents == 0)
                chunk->Archetype->RemoveFromChunkListWithEmptySlots(chunk);
            else
                chunk->Archetype->FreeChunksBySharedComponents.Remove(chunk);
        }


        /// <summary>
        /// Add chunk to archetype tracking of chunks with available slots.
        /// - Does not check if chunk has space.
        /// - Does not check if chunk is locked.
        /// </summary>
        /// <param name="chunk"></param>
        internal static void EmptySlotTrackingAddChunk(Chunk* chunk)
        {
            if (chunk->Archetype->NumSharedComponents == 0)
                chunk->Archetype->AddToChunkListWithEmptySlots(chunk);
            else
                chunk->Archetype->FreeChunksBySharedComponents.Add(chunk);
        }

        public void DestroyMetaChunkEntity(Entity entity)
        {
            EntityDataManager.RemoveComponent(entity, ComponentType.ReadWrite<ChunkHeader>(), m_Entities, this, m_SharedComponentManager, m_groupManager);
            EntityDataManager.TryRemoveEntityId(&entity, 1, m_Entities, this, m_SharedComponentManager);
        }

        public void SetChunkCount(Chunk* chunk, int newCount)
        {
            Assert.AreNotEqual(newCount, chunk->Count);
            Assert.IsFalse(chunk->Locked);
            Assert.IsTrue(!chunk->LockedEntityOrder || newCount == 0);

            var capacity = chunk->Capacity;

            // Chunk released to empty chunk pool
            if (newCount == 0)
            {
                m_ChunksBySequenceNumber.Remove(chunk->SequenceNumber);

                // Remove references to shared components
                if (chunk->Archetype->NumSharedComponents > 0)
                {
                    var sharedComponentValueArray = chunk->SharedComponentValues;

                    for (var i = 0; i < chunk->Archetype->NumSharedComponents; ++i)
                        m_SharedComponentManager.RemoveReference(sharedComponentValueArray[i]);
                }

                if (chunk->ManagedArrayIndex != -1)
                {
                    DeallocateManagedArrayStorage(chunk->ManagedArrayIndex);
                    chunk->ManagedArrayIndex = -1;
                }

                if (chunk->metaChunkEntity != Entity.Null)
                    DestroyMetaChunkEntity(chunk->metaChunkEntity);

                // this chunk is going away, so it shouldn't be in the empty slot list.
                if (chunk->Count < chunk->Capacity)
                  EmptySlotTrackingRemoveChunk(chunk);

                chunk->Archetype->RemoveFromChunkList(chunk);

                chunk->Archetype = null;
                if (m_EmptyChunks.Count == kMaximumEmptyChunksInPool)
                    UnsafeUtility.Free(chunk, Allocator.Persistent);
                else
                {
                    EmptyChunksUnsafePtrList.Add(chunk);
                    chunk->Count = newCount;
                }
                return;
            }
            // Chunk is now full
            else if (newCount == capacity)
            {
                // this chunk no longer has empty slots, so it shouldn't be in the empty slot list.
                EmptySlotTrackingRemoveChunk(chunk);
            }
            // Chunk is no longer full
            else if (chunk->Count == capacity)
            {
                Assert.IsTrue(newCount < chunk->Count);
                EmptySlotTrackingAddChunk(chunk);
            }

            chunk->Count = newCount;
            chunk->Archetype->Chunks.SetChunkEntityCount(chunk->ListIndex, newCount);
        }

        public object GetManagedObject(Chunk* chunk, ComponentType type, int index)
        {
            var typeOfs = ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, type.TypeIndex);
            if (typeOfs < 0 || chunk->Archetype->ManagedArrayOffset[typeOfs] < 0)
                throw new InvalidOperationException("Trying to get managed object for non existing component");
            return GetManagedObject(chunk, typeOfs, index);
        }

        internal object GetManagedObject(Chunk* chunk, int type, int index)
        {
            var managedStart = chunk->Archetype->ManagedArrayOffset[type] * chunk->Capacity;
            return m_ManagedArrays[chunk->ManagedArrayIndex].ManagedArray[index + managedStart];
        }

        public object[] GetManagedObjectRange(Chunk* chunk, int type, out int rangeStart, out int rangeLength)
        {
            rangeStart = chunk->Archetype->ManagedArrayOffset[type] * chunk->Capacity;
            rangeLength = chunk->Count;
            return m_ManagedArrays[chunk->ManagedArrayIndex].ManagedArray;
        }

        public void SetManagedObject(Chunk* chunk, int type, int index, object val)
        {
            var managedStart = chunk->Archetype->ManagedArrayOffset[type] * chunk->Capacity;
            m_ManagedArrays[chunk->ManagedArrayIndex].ManagedArray[index + managedStart] = val;
        }

        public void SetManagedObject(Chunk* chunk, ComponentType type, int index, object val)
        {
            var typeOfs = ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, type.TypeIndex);
            if (typeOfs < 0 || chunk->Archetype->ManagedArrayOffset[typeOfs] < 0)
                throw new InvalidOperationException("Trying to set managed object for non existing component");
            SetManagedObject(chunk, typeOfs, index, val);
        }

        public int CountEntities()
        {
            int entityCount = 0;
            for (var i = m_Archetypes.Count - 1; i >= 0; --i)
            {
                var archetype = m_Archetypes.p[i];
                entityCount += archetype->EntityCount;
            }

            return entityCount;
        }

        [BurstCompile]
        struct MoveAllChunksJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* srcEntityDataManager;
            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* dstEntityDataManager;
            public NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping;

            public void Execute()
            {
                dstEntityDataManager->AllocateEntitiesForRemapping(srcEntityDataManager, ref entityRemapping);
                srcEntityDataManager->FreeAllEntities();
            }
        }

        struct RemapChunk
        {
            public Chunk* chunk;
            public Archetype* dstArchetype;
        }

        [BurstCompile]
        struct RemapAllChunksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping;
            [ReadOnly] public NativeArray<RemapChunk> remapChunks;

            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* dstEntityDataManager;

            public void Execute(int index)
            {
                Chunk* chunk = remapChunks[index].chunk;
                Archetype* dstArchetype = remapChunks[index].dstArchetype;

                dstEntityDataManager->RemapChunk(dstArchetype, chunk, 0, chunk->Count, ref entityRemapping);
                EntityRemapUtility.PatchEntities(dstArchetype->ScalarEntityPatches + 1, dstArchetype->ScalarEntityPatchCount - 1, dstArchetype->BufferEntityPatches, dstArchetype->BufferEntityPatchCount, chunk->Buffer, chunk->Count, ref entityRemapping);
                chunk->Archetype = dstArchetype;
                chunk->ListIndex += dstArchetype->Chunks.Count;
                chunk->ListWithEmptySlotsIndex += dstArchetype->ChunksWithEmptySlots.Count;
            }
        }

        struct RemapArchetype
        {
            public Archetype* srcArchetype;
            public Archetype* dstArchetype;
        }

        [BurstCompile]
        struct RemapArchetypesJob : IJobParallelFor
        {
            [DeallocateOnJobCompletion] [ReadOnly] public NativeArray<RemapArchetype> remapArchetypes;

            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* dstEntityDataManager;

            [ReadOnly] public NativeArray<int> remapShared;

            public int chunkHeaderType;

            // This must be run after chunks have been remapped since FreeChunksBySharedComponents needs the shared component
            // indices in the chunks to be remapped
            public void Execute(int index)
            {
                var srcArchetype = remapArchetypes[index].srcArchetype;
                int srcChunkCount = srcArchetype->Chunks.Count;

                var dstArchetype = remapArchetypes[index].dstArchetype;
                int dstChunkCount = dstArchetype->Chunks.Count;

                if(dstArchetype->Chunks.Capacity < srcChunkCount + dstChunkCount)
                    dstArchetype->Chunks.Grow(srcChunkCount + dstChunkCount);

                UnsafeUtility.MemCpy(dstArchetype->Chunks.p + dstChunkCount, srcArchetype->Chunks.p,
                    sizeof(Chunk*) * srcChunkCount);

                if (srcArchetype->NumSharedComponents == 0)
                {
                    if (srcArchetype->ChunksWithEmptySlots.Count != 0)
                    {
                        dstArchetype->ChunksWithEmptySlotsUnsafePtrList.SetCapacity(srcArchetype->ChunksWithEmptySlots.Count + dstArchetype->ChunksWithEmptySlots.Count);
                        dstArchetype->ChunksWithEmptySlotsUnsafePtrList.Append(srcArchetype->ChunksWithEmptySlotsUnsafePtrList);
                        srcArchetype->ChunksWithEmptySlotsUnsafePtrList.Resize(0);
                    }
                }
                else
                {
                    for (int i = 0; i < dstArchetype->NumSharedComponents; ++i)
                    {
                        var srcArray = srcArchetype->Chunks.GetSharedComponentValueArrayForType(i);
                        var dstArray = dstArchetype->Chunks.GetSharedComponentValueArrayForType(i) + dstChunkCount;
                        for (int j = 0; j < srcChunkCount; ++j)
                        {
                            int srcIndex = srcArray[j];
                            int remapped = remapShared[srcIndex];
                            dstArray[j] = remapped;
                        }
                    }

                    for (int i = 0; i < srcChunkCount; ++i)
                    {
                        var chunk = dstArchetype->Chunks.p[i + dstChunkCount];
                        if(chunk->Count < chunk->Capacity)
                            dstArchetype->FreeChunksBySharedComponents.Add(dstArchetype->Chunks.p[i + dstChunkCount]);
                    }

                    srcArchetype->FreeChunksBySharedComponents.Init(16);
                }

                var globalSystemVersion = dstEntityDataManager->GlobalSystemVersion;
                // Set change versions to GlobalSystemVersion
                for (int iType = 0; iType < dstArchetype->TypesCount; ++iType)
                {
                    var dstArray = dstArchetype->Chunks.GetChangeVersionArrayForType(iType) + dstChunkCount;
                    for (int i = 0; i < srcChunkCount; ++i)
                    {
                        dstArray[i] = globalSystemVersion;
                    }
                }

                // Copy chunk count array
                var dstCountArray = dstArchetype->Chunks.GetChunkEntityCountArray() + dstChunkCount;
                UnsafeUtility.MemCpy(dstCountArray, srcArchetype->Chunks.GetChunkEntityCountArray(), sizeof(int) * srcChunkCount);

                // Fix up chunk pointers in ChunkHeaders
                if (dstArchetype->HasChunkComponents)
                {
                    var metaArchetype = dstArchetype->MetaChunkArchetype;
                    var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(metaArchetype, chunkHeaderType);
                    var offset = metaArchetype->Offsets[indexInTypeArray];
                    var sizeOf = metaArchetype->SizeOfs[indexInTypeArray];

                    for (int i = 0; i < srcChunkCount; ++i)
                    {
                        // Set chunk header without bumping change versions since they are zeroed when processing meta chunk
                        // modifying them here would be a race condition
                        var chunk = dstArchetype->Chunks.p[i + dstChunkCount];
                        var metaChunkEntity = chunk->metaChunkEntity;
                        dstEntityDataManager->GetComponentChunk(metaChunkEntity, out var metaChunk, out var indexInMetaChunk);
                        var chunkHeader = (ChunkHeader*)(metaChunk->Buffer + (offset + sizeOf * indexInMetaChunk));
                        chunkHeader->chunk = chunk;
                    }
                }
                dstArchetype->EntityCount += srcArchetype->EntityCount;
                dstArchetype->Chunks.Count += srcChunkCount;
                srcArchetype->Chunks.Dispose();
                srcArchetype->EntityCount = 0;
            }
        }

        public static void MoveChunks(EntityManager srcEntities, ArchetypeManager dstArchetypeManager,
            EntityGroupManager dstGroupManager, EntityDataManager* dstEntityDataManager,
            SharedComponentDataManager dstSharedComponents)
        {
            var entityRemapping = new NativeArray<EntityRemapUtility.EntityRemapInfo>(srcEntities.Entities->Capacity, Allocator.TempJob);
            MoveChunks(srcEntities, dstArchetypeManager, dstGroupManager, dstEntityDataManager, dstSharedComponents, entityRemapping);
            entityRemapping.Dispose();
        }

        static readonly ProfilerMarker k_ProfileMoveSharedComponents = new ProfilerMarker("MoveSharedComponents");

        public static void MoveChunks(EntityManager srcEntities, ArchetypeManager dstArchetypeManager,
            EntityGroupManager dstGroupManager, EntityDataManager* dstEntityDataManager,
            SharedComponentDataManager dstSharedComponents, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var srcArchetypeManager = srcEntities.ArchetypeManager;
            var srcEntityDataManager = srcEntities.Entities;
            var srcSharedComponents = srcEntities.m_SharedComponentManager;

            var moveChunksJob = new MoveAllChunksJob
            {
                srcEntityDataManager = srcEntityDataManager,
                dstEntityDataManager = dstEntityDataManager,
                entityRemapping = entityRemapping
            }.Schedule();

            JobHandle.ScheduleBatchedJobs();

            int chunkCount = 0;
            for (var i = srcArchetypeManager.m_Archetypes.Count - 1; i >= 0; --i)
            {
                var srcArchetype = srcArchetypeManager.m_Archetypes.p[i];
                chunkCount += srcArchetype->Chunks.Count;
            }

            var remapChunks = new NativeArray<RemapChunk>(chunkCount, Allocator.TempJob);
            var remapArchetypes = new NativeArray<RemapArchetype>(srcArchetypeManager.m_Archetypes.Count, Allocator.TempJob);

            int chunkIndex = 0;
            int archetypeIndex = 0;
            for (var i = srcArchetypeManager.m_Archetypes.Count - 1; i >= 0; --i)
            {
                var srcArchetype = srcArchetypeManager.m_Archetypes.p[i];
                if (srcArchetype->Chunks.Count != 0)
                {
                    if (srcArchetype->NumManagedArrays != 0)
                        throw new ArgumentException("MoveEntitiesFrom is not supported with managed arrays");

                    var dstArchetype = dstArchetypeManager.GetOrCreateArchetype(srcArchetype->Types, srcArchetype->TypesCount, dstGroupManager);

                    remapArchetypes[archetypeIndex] = new RemapArchetype {srcArchetype = srcArchetype, dstArchetype = dstArchetype};

                    for (var j = 0; j < srcArchetype->Chunks.Count; ++j)
                    {
                        var srcChunk = srcArchetype->Chunks.p[j];
                        remapChunks[chunkIndex] = new RemapChunk { chunk = srcChunk, dstArchetype = dstArchetype };
                        chunkIndex++;
                    }

                    archetypeIndex++;

                    dstEntityDataManager->IncrementComponentTypeOrderVersion(dstArchetype);
                }
            }

            moveChunksJob.Complete();

            k_ProfileMoveSharedComponents.Begin();
            var remapShared = dstSharedComponents.MoveAllSharedComponents(srcSharedComponents, Allocator.TempJob);
            k_ProfileMoveSharedComponents.End();

            var remapAllChunksJob = new RemapAllChunksJob
            {
                dstEntityDataManager = dstEntityDataManager,
                remapChunks = remapChunks,
                entityRemapping = entityRemapping
            }.Schedule(remapChunks.Length, 1);


            var remapArchetypesJob = new RemapArchetypesJob
            {
                remapArchetypes = remapArchetypes,
                remapShared = remapShared,
                dstEntityDataManager = dstEntityDataManager,
                chunkHeaderType = TypeManager.GetTypeIndex<ChunkHeader>()
            }.Schedule(archetypeIndex, 1, remapAllChunksJob);

            remapArchetypesJob.Complete();
            remapShared.Dispose();
            remapChunks.Dispose();
        }

        [BurstCompile]
        struct MoveChunksJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* srcEntityDataManager;
            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* dstEntityDataManager;
            public NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping;
            [ReadOnly] public NativeArray<ArchetypeChunk> chunks;

            public void Execute()
            {
                int chunkCount = chunks.Length;
                for (int i = 0; i < chunkCount; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    dstEntityDataManager->AllocateEntitiesForRemapping(chunk, ref entityRemapping);
                    srcEntityDataManager->FreeEntities(chunk);
                }
            }
        }

        [BurstCompile]
        struct RemapChunksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping;
            [ReadOnly] public NativeArray<RemapChunk> remapChunks;

            [NativeDisableUnsafePtrRestriction]
            public EntityDataManager* dstEntityDataManager;

            public void Execute(int index)
            {
                Chunk* chunk = remapChunks[index].chunk;
                Archetype* dstArchetype = remapChunks[index].dstArchetype;

                dstEntityDataManager->RemapChunk(dstArchetype, chunk, 0, chunk->Count, ref entityRemapping);
                EntityRemapUtility.PatchEntities(dstArchetype->ScalarEntityPatches + 1, dstArchetype->ScalarEntityPatchCount - 1, dstArchetype->BufferEntityPatches, dstArchetype->BufferEntityPatchCount, chunk->Buffer, chunk->Count, ref entityRemapping);
            }
        }

        [BurstCompile]
        struct MoveChunksBetweenArchetypeJob : IJob
        {
            [ReadOnly] public NativeArray<RemapChunk> remapChunks;
            [ReadOnly] public NativeArray<int> remapShared;
            public uint globalSystemVersion;

            public void Execute()
            {
                int chunkCount = remapChunks.Length;
                for (int iChunk = 0; iChunk < chunkCount; ++iChunk)
                {
                    var chunk = remapChunks[iChunk].chunk;
                    var dstArchetype = remapChunks[iChunk].dstArchetype;
                    var srcArchetype = chunk->Archetype;

                    int numSharedComponents = dstArchetype->NumSharedComponents;

                    var sharedComponentValues = chunk->SharedComponentValues;

                    if (numSharedComponents != 0)
                    {
                        var alloc = stackalloc int[numSharedComponents];
                        for (int i = 0; i < numSharedComponents; ++i)
                            alloc[i] = remapShared[sharedComponentValues[i]];
                        sharedComponentValues = alloc;
                    }

                    if (chunk->Count < chunk->Capacity)
                        EmptySlotTrackingRemoveChunk(chunk);
                    srcArchetype->RemoveFromChunkList(chunk);
                    srcArchetype->EntityCount -= chunk->Count;

                    chunk->Archetype = dstArchetype;

                    dstArchetype->EntityCount += chunk->Count;
                    dstArchetype->AddToChunkList(chunk, sharedComponentValues, globalSystemVersion);
                    if (chunk->Count < chunk->Capacity)
                        EmptySlotTrackingAddChunk(chunk);
                }
            }
        }


        public static void MoveChunks(EntityManager srcEntities, NativeArray<ArchetypeChunk> chunks, ArchetypeManager dstArchetypeManager,
            EntityGroupManager dstGroupManager, EntityDataManager* dstEntityDataManager, SharedComponentDataManager dstSharedComponents,
            NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var srcArchetypeManager = srcEntities.ArchetypeManager;
            var srcEntityDataManager = srcEntities.Entities;
            var srcSharedComponents = srcEntities.m_SharedComponentManager;

            new MoveChunksJob
            {
                srcEntityDataManager = srcEntityDataManager,
                dstEntityDataManager = dstEntityDataManager,
                entityRemapping = entityRemapping,
                chunks = chunks
            }.Run();

            int chunkCount = chunks.Length;
            var remapChunks = new NativeArray<RemapChunk>(chunkCount, Allocator.TempJob);
            for (int i = 0; i < chunkCount; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                var archetype = chunk->Archetype;

                //TODO: this should not be done more than once for each archetype
                var dstArchetype = dstArchetypeManager.GetOrCreateArchetype(archetype->Types, archetype->TypesCount, dstGroupManager);

                remapChunks[i] = new RemapChunk { chunk = chunk, dstArchetype = dstArchetype };

                if (archetype->MetaChunkArchetype != null)
                {
                    Entity srcEntity = chunk->metaChunkEntity;
                    Entity dstEntity;
                    dstEntityDataManager->CreateEntities(dstArchetypeManager, dstArchetype->MetaChunkArchetype, &dstEntity, 1);
                    srcEntityDataManager->GetComponentChunk(srcEntity, out var srcChunk, out var srcIndex);
                    dstEntityDataManager->GetComponentChunk(dstEntity, out var dstChunk, out var dstIndex);

                    ChunkDataUtility.SwapComponents(srcChunk, srcIndex, dstChunk, dstIndex, 1, srcEntityDataManager->GlobalSystemVersion, dstEntityDataManager->GlobalSystemVersion);
                    EntityRemapUtility.AddEntityRemapping(ref entityRemapping, srcEntity, dstEntity);

                    srcEntities.DestroyEntity(srcEntity);
                }
            }

            k_ProfileMoveSharedComponents.Begin();
            var remapShared = dstSharedComponents.MoveSharedComponents(srcSharedComponents, chunks, entityRemapping, Allocator.TempJob);
            k_ProfileMoveSharedComponents.End();

            var remapChunksJob = new RemapChunksJob
            {
                dstEntityDataManager = dstEntityDataManager,
                remapChunks = remapChunks,
                entityRemapping = entityRemapping
            }.Schedule(remapChunks.Length, 1);

            var moveChunksBetweenArchetypeJob = new MoveChunksBetweenArchetypeJob
            {
                remapChunks = remapChunks,
                remapShared = remapShared,
                globalSystemVersion = dstEntityDataManager->GlobalSystemVersion
            }.Schedule(remapChunksJob);

            moveChunksBetweenArchetypeJob.Complete();

            remapShared.Dispose();
            remapChunks.Dispose();
        }

        internal Chunk* CloneChunkForDiffing(Chunk* chunk, EntityDataManager* entities, EntityGroupManager groupManager, SharedComponentDataManager srcSharedManager)
        {
            int* sharedIndices = stackalloc int[chunk->Archetype->NumSharedComponents];
            chunk->SharedComponentValues.CopyTo(sharedIndices, 0, chunk->Archetype->NumSharedComponents);

            m_SharedComponentManager.CopySharedComponents(srcSharedManager, sharedIndices, chunk->Archetype->NumSharedComponents);

            // Allocate a new chunk
            Archetype* arch = GetOrCreateArchetype(chunk->Archetype->Types, chunk->Archetype->TypesCount, groupManager);

            Chunk* targetChunk = GetCleanChunk(arch, sharedIndices);

            // GetCleanChunk & CopySharedComponents both acquire a ref, once chunk owns, release CopySharedComponents ref
            for (int i = 0; i < chunk->Archetype->NumSharedComponents; ++i)
                m_SharedComponentManager.RemoveReference(sharedIndices[i]);

            UnityEngine.Assertions.Assert.AreEqual(0, targetChunk->Count);
            UnityEngine.Assertions.Assert.IsTrue(targetChunk->Capacity >= chunk->Count);

            int copySize = Chunk.GetChunkBufferSize();
            UnsafeUtility.MemCpy(targetChunk->Buffer, chunk->Buffer, copySize);

            SetChunkCount(targetChunk, chunk->Count);
            targetChunk->Archetype->EntityCount += chunk->Count;

            BufferHeader.PatchAfterCloningChunk(targetChunk);

            var tempEntities = new NativeArray<Entity>(targetChunk->Count, Allocator.Temp);
            entities->AllocateEntities(targetChunk->Archetype, targetChunk, 0, targetChunk->Count, (Entity*)tempEntities.GetUnsafePtr());
            tempEntities.Dispose();

            return targetChunk;
        }

        internal void DestroyChunkForDiffing(Chunk* chunk, EntityDataManager* entities)
        {
            chunk->Archetype->EntityCount -= chunk->Count;
            entities->FreeEntities(chunk);
            SetChunkCount(chunk, 0);
        }

        public int CheckInternalConsistency(EntityDataManager* entities)
        {
            var totalCount = 0;
            for(var i = m_Archetypes.Count - 1; i >= 0; --i)
            {
                var archetype = m_Archetypes.p[i];

                var countInArchetype = 0;
                for (var j = 0; j < archetype->Chunks.Count; ++j)
                {
                    var chunk = archetype->Chunks.p[j];
                    Assert.IsTrue(chunk->Archetype == archetype);
                    Assert.IsTrue(chunk->Capacity >= chunk->Count);
                    Assert.AreEqual(chunk->Count, archetype->Chunks.GetChunkEntityCount(j));

                    var chunkEntities = (Entity*)chunk->Buffer;
                    entities->AssertEntitiesExist(chunkEntities, chunk->Count);

                    if (!chunk->Locked)
                    {
                        if (chunk->Count < chunk->Capacity)
                            if (archetype->NumSharedComponents == 0)
                            {
                                Assert.IsTrue(chunk->ListWithEmptySlotsIndex >= 0 && chunk->ListWithEmptySlotsIndex < archetype->ChunksWithEmptySlots.Count);
                                Assert.IsTrue(chunk == archetype->ChunksWithEmptySlots.p[chunk->ListWithEmptySlotsIndex]);
                            }
                            else
                                Assert.IsTrue(archetype->FreeChunksBySharedComponents.Contains(chunk));
                    }
                    countInArchetype += chunk->Count;

                    if (chunk->Archetype->HasChunkHeader) // Chunk entities with chunk components are not supported
                    {
                        Assert.IsFalse(chunk->Archetype->HasChunkComponents);
                    }

                    Assert.AreEqual(chunk->Archetype->HasChunkComponents, chunk->metaChunkEntity != Entity.Null);
                    if (chunk->metaChunkEntity != Entity.Null)
                    {
                        var chunkHeaderTypeIndex = TypeManager.GetTypeIndex<ChunkHeader>();
                        m_Entities->AssertEntitiesExist(&chunk->metaChunkEntity, 1);
                        m_Entities->AssertEntityHasComponent(chunk->metaChunkEntity, chunkHeaderTypeIndex);
                        var chunkHeader = *(ChunkHeader*)m_Entities->GetComponentDataWithTypeRO(chunk->metaChunkEntity, chunkHeaderTypeIndex);
                        Assert.IsTrue(chunk == chunkHeader.chunk);
                        var metaChunk = m_Entities->GetComponentChunk(chunk->metaChunkEntity);
                        Assert.IsTrue(metaChunk->Archetype == chunk->Archetype->MetaChunkArchetype);
                    }
                }

                Assert.AreEqual(countInArchetype, archetype->EntityCount);

                totalCount += countInArchetype;
            }

            return totalCount;
        }

        internal SharedComponentDataManager GetSharedComponentDataManager()
        {
            return m_SharedComponentManager;
        }

        private struct ManagedArrayStorage
        {
            public object[] ManagedArray;
        }

        internal Chunk* GetChunkBySequenceNumber(uint seqno)
        {
            IntPtr result = default(IntPtr);
            if (m_ChunksBySequenceNumber.TryGetValue(seqno, out result))
                return (Chunk*) result;
            else
                return null;
        }
    }
}
