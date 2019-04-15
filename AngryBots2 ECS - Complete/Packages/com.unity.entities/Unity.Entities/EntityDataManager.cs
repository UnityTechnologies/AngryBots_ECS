//#define USE_BURST_DESTROY

using System;
using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;

namespace Unity.Entities
{
    public unsafe struct ComponentTypes
    {
        ResizableArray64Byte<int> m_sorted;

        public struct Masks
        {
            public UInt16 m_BufferMask;
            public UInt16 m_SystemStateComponentMask;
            public UInt16 m_SharedComponentMask;
            public UInt16 m_ZeroSizedMask;

            public bool IsSharedComponent(int index)
            {
                return (m_SharedComponentMask & (1 << index)) != 0;
            }

            public bool IsZeroSized(int index)
            {
                return (m_ZeroSizedMask & (1 << index)) != 0;
            }

            public int Buffers => math.countbits((UInt32)m_BufferMask);
            public int SystemStateComponents => math.countbits((UInt32)m_SystemStateComponentMask);
            public int SharedComponents => math.countbits((UInt32)m_SharedComponentMask);
            public int ZeroSizeds => math.countbits((UInt32)m_ZeroSizedMask);
        }
        public Masks m_masks;

        private void ComputeMasks()
        {
            for (var i = 0; i < m_sorted.Length; ++i)
            {
                var typeIndex = m_sorted[i];
                var mask = (UInt16)(1 << i);
                if (TypeManager.IsBuffer(typeIndex))
                    m_masks.m_BufferMask |= mask;
                if (TypeManager.IsSystemStateComponent(typeIndex))
                    m_masks.m_SystemStateComponentMask |= mask;
                if (TypeManager.IsSharedComponent(typeIndex))
                    m_masks.m_SharedComponentMask |= mask;
                if (TypeManager.IsZeroSized(typeIndex))
                    m_masks.m_ZeroSizedMask |= mask;
            }
        }

        public int Length
        {
            get => m_sorted.Length;
        }

        public int GetTypeIndex(int index)
        {
            return m_sorted[index];
        }
        public ComponentType GetComponentType(int index)
        {
            return TypeManager.GetType(m_sorted[index]);
        }

        public ComponentTypes(ComponentType a)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = 1;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            SortingUtilities.InsertSorted(pointer, 0, a.TypeIndex);
            ComputeMasks();
        }

        public ComponentTypes(ComponentType a, ComponentType b)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = 2;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            SortingUtilities.InsertSorted(pointer, 0, a.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 1, b.TypeIndex);
            ComputeMasks();
        }

        public ComponentTypes(ComponentType a, ComponentType b, ComponentType c)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = 3;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            SortingUtilities.InsertSorted(pointer, 0, a.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 1, b.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 2, c.TypeIndex);
            ComputeMasks();
        }

        public ComponentTypes(ComponentType a, ComponentType b, ComponentType c, ComponentType d)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = 4;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            SortingUtilities.InsertSorted(pointer, 0, a.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 1, b.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 2, c.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 3, d.TypeIndex);
            ComputeMasks();
        }

        public ComponentTypes(ComponentType a, ComponentType b, ComponentType c, ComponentType d, ComponentType e)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = 5;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            SortingUtilities.InsertSorted(pointer, 0, a.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 1, b.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 2, c.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 3, d.TypeIndex);
            SortingUtilities.InsertSorted(pointer, 4, e.TypeIndex);
            ComputeMasks();
        }

        public ComponentTypes(ComponentType[] componentType)
        {
            m_sorted = new ResizableArray64Byte<int>();
            m_masks = new Masks();
            m_sorted.Length = componentType.Length;
            var pointer = (int*)m_sorted.GetUnsafePointer();
            for(var i = 0; i < componentType.Length; ++i)
                SortingUtilities.InsertSorted(pointer, i, componentType[i].TypeIndex);
            ComputeMasks();
        }

    }

    internal struct CleanupEntity : IComponentData
    {
    }

    internal unsafe struct EntityDataManager
    {
#if USE_BURST_DESTROY
        private delegate Chunk* DeallocateDataEntitiesInChunkDelegate(EntityDataManager* entityDataManager, Entity* entities, int count, out int indexInChunk, out int batchCount);
        static DeallocateDataEntitiesInChunkDelegate ms_DeallocateDataEntitiesInChunkDelegate;
#endif

        private struct EntityChunkData
        {
            public Chunk* Chunk;
            public int IndexInChunk;
        }

        private struct EntityData
        {
            public int* Version;
            public Archetype** Archetype;
            public EntityChunkData* ChunkData;
#if UNITY_EDITOR
            public NumberedWords* Name;
#endif
        }

        private EntityData m_Entities;
        private int m_EntitiesCapacity;
        private int m_EntitiesFreeIndex;

        private int* m_ComponentTypeOrderVersion;
        public uint GlobalSystemVersion;

        public int Version => GetComponentTypeOrderVersion(TypeManager.GetTypeIndex<Entity>());

        public void IncrementGlobalSystemVersion()
        {
            ChangeVersionUtility.IncrementGlobalSystemVersion(ref GlobalSystemVersion);
        }

        private EntityData CreateEntityData(int newCapacity)
        {
            EntityData entities = new EntityData();

            var versionBytes = (newCapacity * sizeof(int) + 63) & ~63;
            var archetypeBytes = (newCapacity * sizeof(Archetype*) + 63) & ~63;
            var chunkDataBytes = (newCapacity * sizeof(EntityChunkData) + 63) & ~63;
            var bytesToAllocate = versionBytes + archetypeBytes + chunkDataBytes;
#if UNITY_EDITOR
            var nameBytes = (newCapacity * sizeof(NumberedWords) + 63) & ~63;
            bytesToAllocate += nameBytes;
#endif

            var bytes = (byte*) UnsafeUtility.Malloc(bytesToAllocate, 64, Allocator.Persistent);

            entities.Version = (int*) (bytes);
            entities.Archetype = (Archetype**) (bytes + versionBytes);
            entities.ChunkData = (EntityChunkData*) (bytes + versionBytes + archetypeBytes);
#if UNITY_EDITOR
            entities.Name = (NumberedWords*) (bytes + versionBytes + archetypeBytes + chunkDataBytes);
#endif

            return entities;
        }

        private void FreeEntityData(ref EntityData entities)
        {
            UnsafeUtility.Free(entities.Version, Allocator.Persistent);

            entities.Version = null;
            entities.Archetype = null;
            entities.ChunkData = null;
#if UNITY_EDITOR
            entities.Name = null;
#endif
        }

        private void CopyEntityData(ref EntityData dstEntityData, EntityData srcEntityData, long copySize)
        {
            UnsafeUtility.MemCpy(dstEntityData.Version, srcEntityData.Version, copySize * sizeof(int));
            UnsafeUtility.MemCpy(dstEntityData.Archetype, srcEntityData.Archetype, copySize * sizeof(Archetype*));
            UnsafeUtility.MemCpy(dstEntityData.ChunkData, srcEntityData.ChunkData, copySize * sizeof(EntityChunkData));
#if UNITY_EDITOR
            UnsafeUtility.MemCpy(dstEntityData.Name, srcEntityData.Name, copySize * sizeof(NumberedWords));
#endif
        }

        public void OnCreate()
        {
            m_EntitiesCapacity = 10;
            m_Entities = CreateEntityData(m_EntitiesCapacity);
            m_EntitiesFreeIndex = 0;
            GlobalSystemVersion = ChangeVersionUtility.InitialGlobalSystemVersion;
            InitializeAdditionalCapacity(0);

#if USE_BURST_DESTROY
            if (ms_DeallocateDataEntitiesInChunkDelegate == null)
            {
                ms_DeallocateDataEntitiesInChunkDelegate = DeallocateDataEntitiesInChunk;
                ms_DeallocateDataEntitiesInChunkDelegate =
 Burst.BurstDelegateCompiler.CompileDelegate(ms_DeallocateDataEntitiesInChunkDelegate);
            }
#endif

            const int componentTypeOrderVersionSize = sizeof(int) * TypeManager.MaximumTypesCount;
            m_ComponentTypeOrderVersion = (int*) UnsafeUtility.Malloc(componentTypeOrderVersionSize,
                UnsafeUtility.AlignOf<int>(), Allocator.Persistent);
            UnsafeUtility.MemClear(m_ComponentTypeOrderVersion, componentTypeOrderVersionSize);
        }

        public void OnDestroy()
        {
            FreeEntityData(ref m_Entities);
            m_EntitiesCapacity = 0;

            UnsafeUtility.Free(m_ComponentTypeOrderVersion, Allocator.Persistent);
            m_ComponentTypeOrderVersion = null;
        }

        private void InitializeAdditionalCapacity(int start)
        {
            for (var i = start; i != m_EntitiesCapacity; i++)
            {
                m_Entities.ChunkData[i].IndexInChunk = i + 1;
                m_Entities.Version[i] = 1;
                m_Entities.ChunkData[i].Chunk = null;
#if UNITY_EDITOR
                m_Entities.Name[i] = new NumberedWords();
#endif
            }

            // Last entity indexInChunk identifies that we ran out of space...
            m_Entities.ChunkData[m_EntitiesCapacity - 1].IndexInChunk = -1;
        }

        void IncreaseCapacity()
        {
            Capacity = 2 * Capacity;
        }

        public int Capacity
        {
            get { return m_EntitiesCapacity; }
            set
            {
                if (value <= m_EntitiesCapacity)
                    return;

                var newEntities = CreateEntityData(value);
                CopyEntityData(ref newEntities, m_Entities, m_EntitiesCapacity);
                FreeEntityData(ref m_Entities);

                var startNdx = m_EntitiesCapacity - 1;
                m_Entities = newEntities;
                m_EntitiesCapacity = value;

                InitializeAdditionalCapacity(startNdx);
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        private void ValidateEntity(Entity entity)
        {
            if (entity.Index < 0)
                throw new ArgumentException($"All entities created using EntityCommandBuffer.CreateEntity must be realized via playback(). One of the entities is still deferred (Index: {entity.Index}).");
            if ((uint) entity.Index >= (uint) m_EntitiesCapacity)
                throw new ArgumentException("All entities passed to EntityManager must exist. One of the entities has already been destroyed or was never created.");
        }

        public bool Exists(Entity entity)
        {
            int index = entity.Index;

            ValidateEntity(entity);

            var versionMatches = m_Entities.Version[index] == entity.Version;
            var hasChunk = m_Entities.ChunkData[index].Chunk != null;

            return versionMatches && hasChunk;
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntitiesExist(Entity* entities, int count)
        {
            for (var i = 0; i != count; i++)
            {
                var entity = entities + i;

                ValidateEntity(*entity);

                int index = entity->Index;
                var exists = m_Entities.Version[index] == entity->Version && m_Entities.ChunkData[index].Chunk != null;
                if (!exists)
                    throw new ArgumentException("All entities passed to EntityManager must exist. One of the entities has already been destroyed or was never created.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(Entity* entities, int count)
        {
            for (var i = 0; i != count; i++)
            {
                var entity = entities + i;
                if (!Exists(*entity))
                    continue;

                int index = entity->Index;
                var chunk = m_Entities.ChunkData[index].Chunk;
                if (chunk->Locked || chunk->LockedEntityOrder)
                {
                    throw new InvalidOperationException("Cannot destroy entities in locked Chunks. Unlock Chunk first.");
                }
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, ComponentType componentType)
        {
            if (HasComponent(entity, componentType))
                return;

            if (!Exists(entity))
                throw new ArgumentException("The entity does not exist");

            throw new ArgumentException($"A component with type:{componentType} has not been added to the entity.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertEntityHasComponent(Entity entity, int componentType)
        {
            AssertEntityHasComponent(entity, ComponentType.FromTypeIndex(componentType));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, ComponentType componentType)
        {
            if (!Exists(entity))
                throw new ArgumentException("The entity does not exist");

            if (!componentType.IgnoreDuplicateAdd && HasComponent(entity, componentType))
                throw new ArgumentException($"The component of type:{componentType} has already been added to the entity.");

            var chunk = GetComponentChunk(entity);
            if (chunk->Locked || chunk->LockedEntityOrder)
                throw new InvalidOperationException("Cannot add components to locked Chunks. Unlock Chunk first.");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(Entity entity, ComponentType componentType)
        {
            if (HasComponent(entity, componentType))
            {
                var chunk = GetComponentChunk(entity);
                if (chunk->Locked || chunk->LockedEntityOrder)
                    throw new ArgumentException($"The component of type:{componentType} has already been added to the entity.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(Entity entity, int componentType)
        {
            AssertCanAddComponent(entity, ComponentType.FromTypeIndex(componentType));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponents(Entity entity, ComponentTypes types)
        {
            for(int i=0;i<types.Length;++i)
                AssertCanAddComponent(entity, ComponentType.FromTypeIndex(types.GetTypeIndex(i)));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponents(Entity entity, ComponentTypes types)
        {
            for(int i=0;i<types.Length;++i)
                AssertCanRemoveComponent(entity, ComponentType.FromTypeIndex(types.GetTypeIndex(i)));
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                    throw new ArgumentException($"A component with type:{componentType} has already been added to the chunk.");
                if(chunk->Locked)
                    throw new InvalidOperationException("Cannot add components to locked Chunks. Unlock Chunk first.");
                if(chunk->LockedEntityOrder && !componentType.IsZeroSized)
                    throw new InvalidOperationException("Cannot add non-zero sized components to LockedEntityOrder Chunks. Unlock Chunk first.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanRemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                {
                    if(chunk->Locked)
                        throw new InvalidOperationException("Cannot remove components from locked Chunks. Unlock Chunk first.");
                    if(chunk->LockedEntityOrder && !componentType.IsZeroSized)
                        throw new InvalidOperationException("Cannot remove non-zero sized components to LockedEntityOrder Chunks. Unlock Chunk first.");
                }
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanDestroy(NativeArray<ArchetypeChunk> chunkArray)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (chunk->Locked)
                    throw new InvalidOperationException("Cannot destroy entities from locked Chunks. Unlock Chunk first.");
            }
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void AssertCanAddChunkComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType componentType)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                if (ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, componentType.TypeIndex) != -1)
                    throw new ArgumentException($"A chunk component with type:{componentType} has already been added to the chunk.");
                if(chunk->Locked)
                    throw new InvalidOperationException("Cannot add chunk components to locked Chunks. Unlock Chunk first.");
                if((chunk->metaChunkEntity != Entity.Null) && GetComponentChunk(chunk->metaChunkEntity)->Locked)
                    throw new InvalidOperationException("Cannot add chunk components if Meta Chunk is locked. Unlock Meta Chunk first.");
                if((chunk->metaChunkEntity != Entity.Null) && GetComponentChunk(chunk->metaChunkEntity)->LockedEntityOrder)
                    throw new InvalidOperationException("Cannot add chunk components if Meta Chunk is LockedEntityOrder. Unlock Meta Chunk first.");
            }
        }

        static Chunk* EntityChunkBatch(EntityDataManager* entityDataManager, Entity* entities, int count, out int indexInChunk, out int batchCount)
        {
            /// This is optimized for the case where the array of entities are allocated contigously in the chunk
            /// Thus the compacting of other elements can be batched

            // Calculate baseEntityIndex & chunk
            var baseEntityIndex = entities[0].Index;

            var versions = entityDataManager->m_Entities.Version;
            var chunkData = entityDataManager->m_Entities.ChunkData;

            var chunk = versions[baseEntityIndex] == entities[0].Version ? entityDataManager->m_Entities.ChunkData[baseEntityIndex].Chunk : null;
            indexInChunk = chunkData[baseEntityIndex].IndexInChunk;
            batchCount = 0;

            while (batchCount < count)
            {
                var entityIndex = entities[batchCount].Index;
                var curChunk = chunkData[entityIndex].Chunk;
                var curIndexInChunk = chunkData[entityIndex].IndexInChunk;

                if (versions[entityIndex] == entities[batchCount].Version)
                {
                    if (curChunk != chunk || curIndexInChunk != indexInChunk + batchCount)
                        break;
                }
                else
                {
                    if (chunk != null)
                        break;
                }

                batchCount++;
            }

            return chunk;
        }

        static void DeallocateDataEntitiesInChunk(EntityDataManager* entityDataManager, Entity* entities, Chunk* chunk, int indexInChunk, int batchCount)
        {
            DeallocateBuffers(entityDataManager, entities, chunk, batchCount);

            var freeIndex = entityDataManager->m_EntitiesFreeIndex;

            for (var i = batchCount - 1; i >= 0; --i)
            {
                var entityIndex = entities[i].Index;

                entityDataManager->m_Entities.ChunkData[entityIndex].Chunk = null;
                entityDataManager->m_Entities.Version[entityIndex]++;
                entityDataManager->m_Entities.ChunkData[entityIndex].IndexInChunk = freeIndex;
#if UNITY_EDITOR
                entityDataManager->m_Entities.Name[entityIndex] = new NumberedWords();
#endif
                freeIndex = entityIndex;
            }

            entityDataManager->m_EntitiesFreeIndex = freeIndex;

            // Compute the number of things that need to moved and patched.
            int patchCount = Math.Min(batchCount, chunk->Count - indexInChunk - batchCount);

            if (0 == patchCount)
                return;

            // updates EntitityData->indexInChunk to point to where the components will be moved to
            //Assert.IsTrue(chunk->archetype->sizeOfs[0] == sizeof(Entity) && chunk->archetype->offsets[0] == 0);
            var movedEntities = (Entity*) chunk->Buffer + (chunk->Count - patchCount);
            for (var i = 0; i != patchCount; i++)
                entityDataManager->m_Entities.ChunkData[movedEntities[i].Index].IndexInChunk = indexInChunk + i;

            // Move component data from the end to where we deleted components
            ChunkDataUtility.Copy(chunk, chunk->Count - patchCount, chunk, indexInChunk, patchCount);
        }

        public static void DeallocateBuffers(EntityDataManager* entityDataManager, Entity* entities, Chunk* chunk, int batchCount)
        {
            var archetype = chunk->Archetype;

            for (var ti = 0; ti < archetype->TypesCount; ++ti)
            {
                var type = archetype->Types[ti];

                if (!type.IsBuffer)
                    continue;

                var basePtr = chunk->Buffer + archetype->Offsets[ti];
                var stride = archetype->SizeOfs[ti];

                for (int i = 0; i < batchCount; ++i)
                {
                    Entity e = entities[i];
                    int indexInChunk = entityDataManager->m_Entities.ChunkData[e.Index].IndexInChunk;
                    byte* bufferPtr = basePtr + stride * indexInChunk;
                    BufferHeader.Destroy((BufferHeader*)bufferPtr);
                }
            }
        }

        public static void DeallocateBuffers(EntityDataManager* entityDataManager, Chunk* chunk)
        {
            var archetype = chunk->Archetype;

            for (var ti = 0; ti < archetype->TypesCount; ++ti)
            {
                var type = archetype->Types[ti];

                if (!type.IsBuffer)
                    continue;

                var basePtr = chunk->Buffer + archetype->Offsets[ti];
                var stride = archetype->SizeOfs[ti];

                for (int i = 0; i < chunk->Count; ++i)
                {
                    byte* bufferPtr = basePtr + stride * i;
                    BufferHeader.Destroy((BufferHeader*)bufferPtr);
                }
            }
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public int CheckInternalConsistency()
        {
            var aliveEntities = 0;
            var entityType = TypeManager.GetTypeIndex<Entity>();

            for (var i = 0; i != m_EntitiesCapacity; i++)
            {
                var chunk = m_Entities.ChunkData[i].Chunk;
                if (chunk == null)
                    continue;

                aliveEntities++;
                var archetype = m_Entities.Archetype[i];
				Assert.AreEqual((IntPtr)archetype, (IntPtr)chunk->Archetype);
                Assert.AreEqual(entityType, archetype->Types[0].TypeIndex);
                var entity =
                    *(Entity*) ChunkDataUtility.GetComponentDataRO(m_Entities.ChunkData[i].Chunk, m_Entities.ChunkData[i].IndexInChunk, 0);
                Assert.AreEqual(i, entity.Index);
                Assert.AreEqual(m_Entities.Version[i], entity.Version);

                Assert.IsTrue(Exists(entity));
            }

            return aliveEntities;
        }
#endif

        public void AllocateConsecutiveEntitiesForLoading(int count)
        {
            int newCapacity = count + 1; // make room for Entity.Null
            Capacity = newCapacity + 1; // the last entity is used to indicate we ran out of space
            m_EntitiesFreeIndex = newCapacity;
            for (int i = 1; i < newCapacity; ++i)
            {
                if (m_Entities.ChunkData[i].Chunk != null)
                {
                    throw new ArgumentException("loading into non-empty entity manager is not supported");
                }

                m_Entities.ChunkData[i].IndexInChunk = 0;
                m_Entities.Version[i] = 0;
#if UNITY_EDITOR
                m_Entities.Name[i] = new NumberedWords();
#endif
            }
        }

        internal void AddExistingChunk(Chunk* chunk)
        {
            for (int iEntity = 0; iEntity < chunk->Count; ++iEntity)
            {
                var entity = (Entity*)ChunkDataUtility.GetComponentDataRO(chunk, iEntity, 0);
                m_Entities.ChunkData[entity->Index].Chunk = chunk;
                m_Entities.ChunkData[entity->Index].IndexInChunk = iEntity;
                m_Entities.Archetype[entity->Index] = chunk->Archetype;
            }
        }

        public void AllocateEntities(Archetype* arch, Chunk* chunk, int baseIndex, int count, Entity* outputEntities)
        {
            Assert.AreEqual(chunk->Archetype->Offsets[0], 0);
            Assert.AreEqual(chunk->Archetype->SizeOfs[0], sizeof(Entity));

            var entityInChunkStart = (Entity*) chunk->Buffer + baseIndex;

            for (var i = 0; i != count; i++)
            {
                var entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                if (entityIndexInChunk == -1)
                {
                    IncreaseCapacity();
                    entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                }

                var entityVersion = m_Entities.Version[m_EntitiesFreeIndex];

                if (outputEntities != null)
                {
                    outputEntities[i].Index = m_EntitiesFreeIndex;
                    outputEntities[i].Version = entityVersion;
                }

                var entityInChunk = entityInChunkStart + i;

                entityInChunk->Index = m_EntitiesFreeIndex;
                entityInChunk->Version = entityVersion;

                m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk = baseIndex + i;
                m_Entities.Archetype[m_EntitiesFreeIndex] = arch;
                m_Entities.ChunkData[m_EntitiesFreeIndex].Chunk = chunk;
#if UNITY_EDITOR
                m_Entities.Name     [m_EntitiesFreeIndex] = new NumberedWords();
#endif

                m_EntitiesFreeIndex = entityIndexInChunk;
            }
        }

        public void AllocateEntitiesForRemapping(EntityDataManager* srcEntityDataManager, ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var srcEntityData = srcEntityDataManager->m_Entities;
            var count = srcEntityDataManager->m_EntitiesCapacity;
            for (var i = 0; i != count; i++)
            {
                if (srcEntityData.ChunkData[i].Chunk != null)
                {
                    var entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                    if (entityIndexInChunk == -1)
                    {
                        IncreaseCapacity();
                        entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                    }

                    var entityVersion = m_Entities.Version[m_EntitiesFreeIndex];

                    EntityRemapUtility.AddEntityRemapping(ref entityRemapping, new Entity { Version = srcEntityData.Version[i], Index = i }, new Entity { Version = entityVersion, Index = m_EntitiesFreeIndex });
                    m_EntitiesFreeIndex = entityIndexInChunk;
                }
            }
        }

        public void AllocateEntitiesForRemapping(Chunk* chunk, ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            var count = chunk->Count;
            var entities = (Entity*)chunk->Buffer;
            for (var i = 0; i != count; i++)
            {
                var entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                if (entityIndexInChunk == -1)
                {
                    IncreaseCapacity();
                    entityIndexInChunk = m_Entities.ChunkData[m_EntitiesFreeIndex].IndexInChunk;
                }

                var entityVersion = m_Entities.Version[m_EntitiesFreeIndex];

                EntityRemapUtility.AddEntityRemapping(ref entityRemapping, new Entity { Version = entities[i].Version, Index = entities[i].Index }, new Entity { Version = entityVersion, Index = m_EntitiesFreeIndex });
                m_EntitiesFreeIndex = entityIndexInChunk;
            }
        }

        public void RemapChunk(Archetype* arch, Chunk* chunk, int baseIndex, int count, ref NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
            Assert.AreEqual(chunk->Archetype->Offsets[0], 0);
            Assert.AreEqual(chunk->Archetype->SizeOfs[0], sizeof(Entity));

            var entityInChunkStart = (Entity*)(chunk->Buffer) + baseIndex;

            for (var i = 0; i != count; i++)
            {
                var entityInChunk = entityInChunkStart + i;
                var target = EntityRemapUtility.RemapEntity(ref entityRemapping, *entityInChunk);
                var entityVersion = m_Entities.Version[target.Index];

                Assert.AreEqual(entityVersion, target.Version);

                entityInChunk->Index = target.Index;
                entityInChunk->Version = entityVersion;
                m_Entities.ChunkData[target.Index].IndexInChunk = baseIndex + i;
                m_Entities.Archetype[target.Index] = arch;
                m_Entities.ChunkData[target.Index].Chunk = chunk;
            }
            if (chunk->metaChunkEntity != Entity.Null)
            {
                chunk->metaChunkEntity = EntityRemapUtility.RemapEntity(ref entityRemapping, chunk->metaChunkEntity);
            }
        }

        public void FreeAllEntities()
        {
            for (var i = 0; i != m_EntitiesCapacity; i++)
            {
                m_Entities.ChunkData[i].IndexInChunk = i + 1;
                m_Entities.Version[i] += 1;
                m_Entities.ChunkData[i].Chunk = null;
#if UNITY_EDITOR
                m_Entities.Name[i] = new NumberedWords();
#endif
            }

            // Last entity indexInChunk identifies that we ran out of space...
            m_Entities.ChunkData[m_EntitiesCapacity - 1].IndexInChunk = -1;

            m_EntitiesFreeIndex = 0;
        }

        public void FreeEntities(Chunk* chunk)
        {
            var count = chunk->Count;
            var entities = (Entity*)chunk->Buffer;
            int freeIndex = m_EntitiesFreeIndex;
            for (var i = 0; i != count; i++)
            {
                int index = entities[i].Index;
                m_Entities.Version[index] += 1;
                m_Entities.ChunkData[index].Chunk = null;
                m_Entities.ChunkData[index].IndexInChunk = freeIndex;
#if UNITY_EDITOR
                m_Entities.Name[index] = new NumberedWords();
#endif
                freeIndex = index;
            }

            m_EntitiesFreeIndex = freeIndex;
        }

#if UNITY_EDITOR
        public string GetName(Entity entity)
        {
            return m_Entities.Name[entity.Index].ToString();
        }
        public void SetName(Entity entity, string name)
        {
            m_Entities.Name[entity.Index].SetString(name);
        }
#endif

        public bool HasComponent(Entity entity, int type)
        {
            if (!Exists(entity))
                return false;

            var archetype = m_Entities.Archetype[entity.Index];
            return ChunkDataUtility.GetIndexInTypeArray(archetype, type) != -1;
        }

        public bool HasComponent(Entity entity, ComponentType type)
        {
            if (!Exists(entity))
                return false;

            var archetype = m_Entities.Archetype[entity.Index];

            return ChunkDataUtility.GetIndexInTypeArray(archetype, type.TypeIndex) != -1;
        }

        public int GetSizeInChunk(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            return ChunkDataUtility.GetSizeInChunk(entityChunk, typeIndex, ref typeLookupCache);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = m_Entities.ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRO(entityChunk, entityIndexInChunk, typeIndex);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = m_Entities.ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRW(entityChunk, entityIndexInChunk, typeIndex,
                globalVersion);
        }

        public byte* GetComponentDataWithTypeRO(Entity entity, int typeIndex, ref int typeLookupCache)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = m_Entities.ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRO(entityChunk, entityIndexInChunk, typeIndex,
                ref typeLookupCache);
        }

        public byte* GetComponentDataWithTypeRW(Entity entity, int typeIndex, uint globalVersion,
            ref int typeLookupCache)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = m_Entities.ChunkData[entity.Index].IndexInChunk;

            return ChunkDataUtility.GetComponentDataWithTypeRW(entityChunk, entityIndexInChunk, typeIndex,
                globalVersion, ref typeLookupCache);
        }

        public Chunk* GetComponentChunk(Entity entity)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;

            return entityChunk;
        }

        public void GetComponentChunk(Entity entity, out Chunk* chunk, out int chunkIndex)
        {
            var entityChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var entityIndexInChunk = m_Entities.ChunkData[entity.Index].IndexInChunk;

            chunk = entityChunk;
            chunkIndex = entityIndexInChunk;
        }

        public Archetype* GetArchetype(Entity entity)
        {
            return m_Entities.Archetype[entity.Index];
        }

        public void SetArchetype(ArchetypeManager typeMan, Entity entity, Archetype* archetype, SharedComponentValues sharedComponentValues)
        {
            var chunk = typeMan.GetChunkWithEmptySlots(archetype, sharedComponentValues);
            var chunkIndex = typeMan.AllocateIntoChunk(chunk);

            var oldArchetype = m_Entities.Archetype[entity.Index];
            var oldChunk = m_Entities.ChunkData[entity.Index].Chunk;
            var oldChunkIndex = m_Entities.ChunkData[entity.Index].IndexInChunk;
            ChunkDataUtility.Convert(oldChunk, oldChunkIndex, chunk, chunkIndex);
            if (chunk->ManagedArrayIndex >= 0 && oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, oldChunkIndex, chunk, chunkIndex, 1);

            m_Entities.Archetype[entity.Index] = archetype;
            m_Entities.ChunkData[entity.Index].Chunk = chunk;
            m_Entities.ChunkData[entity.Index].IndexInChunk = chunkIndex;

            var lastIndex = oldChunk->Count - 1;
            // No need to replace with ourselves
            if (lastIndex != oldChunkIndex)
            {
                var lastEntity = (Entity*) ChunkDataUtility.GetComponentDataRO(oldChunk, lastIndex, 0);
                m_Entities.ChunkData[lastEntity->Index].IndexInChunk = oldChunkIndex;

                ChunkDataUtility.Copy(oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
                if (oldChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
            }

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, oldChunk, lastIndex, 1);

            --oldArchetype->EntityCount;

            chunk->SetAllChangeVersions(GlobalSystemVersion);
            oldChunk->SetAllChangeVersions(GlobalSystemVersion);

            typeMan.SetChunkCount(oldChunk, lastIndex);
        }

        public void SetArchetype(ArchetypeManager typeMan, Chunk* chunk, Archetype* archetype, SharedComponentValues sharedComponentValues)
        {
            var srcChunk = chunk;
            var srcArchetype = srcChunk->Archetype;
            var srcEntities = (Entity*)srcChunk->Buffer;
            var srcEntitiesCount = srcChunk->Count;
            var srcRemainingCount = srcEntitiesCount;
            var srcOffset = 0;

            var dstArchetype = archetype;

            while(srcRemainingCount > 0)
            {
                var dstChunk = typeMan.GetChunkWithEmptySlots(archetype, sharedComponentValues);
                int dstIndexBase;
                var dstCount = typeMan.AllocateIntoChunk(dstChunk, srcRemainingCount, out dstIndexBase);

                ChunkDataUtility.Convert(srcChunk, srcOffset, dstChunk, dstIndexBase, dstCount);

                for(int i = 0; i < dstCount; i++)
                {
                    var entity = srcEntities[srcOffset + i];
                    m_Entities.Archetype[entity.Index] = dstArchetype;
                    m_Entities.ChunkData[entity.Index].Chunk = dstChunk;
                    m_Entities.ChunkData[entity.Index].IndexInChunk = dstIndexBase + i;
                }

                if (srcChunk->ManagedArrayIndex >= 0 && dstChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, srcChunk, srcOffset, dstChunk, dstIndexBase, dstCount);

                srcRemainingCount -= dstCount;
                srcOffset += dstCount;
            }

            srcArchetype->EntityCount -= srcEntitiesCount;

            if (srcChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, srcChunk, 0, srcEntitiesCount);
            typeMan.SetChunkCount(srcChunk, 0);
        }

        public void AddComponents(Entity entity, ComponentTypes types, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            var oldArchetype = GetArchetype(entity);
            var oldTypes = oldArchetype->Types;

            var newTypesCount = oldArchetype->TypesCount + types.Length;
            ComponentTypeInArchetype* newTypes = stackalloc ComponentTypeInArchetype[newTypesCount];


            var indexOfNewTypeInNewArchetype = stackalloc int[types.Length];

            // zipper the two sorted arrays "type" and "componentTypeInArchetype" into "componentTypeInArchetype"
            // because this is done in-place, it must be done backwards so as not to disturb the existing contents.

            var unusedIndices = 0;
            {
                var oldThings = oldArchetype->TypesCount;
                var newThings = types.Length;
                var mixedThings = oldThings + newThings;
                while (oldThings > 0 && newThings > 0) // while both are still zippering,
                {
                    var oldThing = oldTypes[oldThings - 1];
                    var newThing = types.GetComponentType(newThings - 1);
                    if (oldThing.TypeIndex > newThing.TypeIndex) // put whichever is bigger at the end of the array
                    {
                        newTypes[--mixedThings] = oldThing;
                        --oldThings;
                    }
                    else
                    {
                        if (oldThing.TypeIndex == newThing.TypeIndex && newThing.IgnoreDuplicateAdd)
                            --oldThings;

                        var componentTypeInArchetype = new ComponentTypeInArchetype(newThing);
                        newTypes[--mixedThings] = componentTypeInArchetype;
                        --newThings;
                        indexOfNewTypeInNewArchetype[newThings] = mixedThings; // "this new thing ended up HERE"
                    }
                }

                Assert.AreEqual(0, newThings); // must not be any new things to copy remaining, oldThings contain entity

                while (oldThings > 0) // if there are remaining old things, copy them here
                {
                    newTypes[--mixedThings] = oldTypes[--oldThings];
                }

                unusedIndices = mixedThings; // In case we ignored duplicated types, this will be > 0
            }

            var newArchetype = archetypeManager.GetOrCreateArchetype(newTypes + unusedIndices, newTypesCount, groupManager);

            var sharedComponentValues = GetComponentChunk(entity)->SharedComponentValues;
            if (types.m_masks.m_SharedComponentMask != 0)
            {
                int* alloc2 = stackalloc int[newArchetype->NumSharedComponents];
                var oldSharedComponentValues = sharedComponentValues;
                sharedComponentValues = alloc2;
                BuildSharedComponentIndicesWithAddedComponents(oldArchetype, newArchetype, oldSharedComponentValues, alloc2);
            }

            SetArchetype(archetypeManager, entity, newArchetype, sharedComponentValues);
            IncrementComponentOrderVersion(newArchetype, GetComponentChunk(entity), sharedComponentDataManager);
        }

        public void AddComponent(Entity entity, ComponentType type, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager,
            EntityGroupManager groupManager)
        {
            var archetype = GetArchetype(entity);
            var chunk = m_Entities.ChunkData[entity.Index].Chunk;

            int indexInTypeArray=0;
            var newType = archetypeManager.GetArchetypeWithAddedComponentType(archetype, type, groupManager, &indexInTypeArray);

            if (newType == null)
            {
                // This can happen if we are adding a tag component to an entity that already has it.
                return;
            }

            var sharedComponentValues = GetComponentChunk(entity)->SharedComponentValues;
            if (type.IsSharedComponent)
            {
                int* temp = stackalloc int[newType->NumSharedComponents];
                int indexOfNewSharedComponent = indexInTypeArray - newType->FirstSharedComponent;
                BuildSharedComponentIndicesWithAddedComponent(indexOfNewSharedComponent, 0, newType->NumSharedComponents, sharedComponentValues, temp);
                sharedComponentValues = temp;
            }

            SetArchetype(archetypeManager, entity, newType, sharedComponentValues);
            IncrementComponentOrderVersion(newType, GetComponentChunk(entity), sharedComponentDataManager);
        }

        public void MoveChunkToNewArchetype(Chunk* chunk, Archetype* newArchetype, uint globalVersion, ArchetypeManager archetypeManager, SharedComponentValues sharedComponentValues)
        {
            var oldArchetype = chunk->Archetype;
            Assert.IsTrue(oldArchetype != newArchetype);
            ChunkDataUtility.AssertAreLayoutCompatible(oldArchetype, newArchetype);
            var count = chunk->Count;
            bool hasEmptySlots = count < chunk->Capacity;

            if(hasEmptySlots)
                ArchetypeManager.EmptySlotTrackingRemoveChunk(chunk);

            int chunkIndexInOldArchetype = chunk->ListIndex;

            var newTypes = newArchetype->Types;
            var oldTypes = oldArchetype->Types;

            chunk->Archetype = newArchetype;
            //Change version is overriden below
            newArchetype->AddToChunkList(chunk, sharedComponentValues, 0);
            int chunkIndexInNewArchetype = chunk->ListIndex;

            //Copy change versions from old to new archetype
            for (int iOldType = oldArchetype->TypesCount-1, iNewType = newArchetype->TypesCount-1; iNewType >=0; --iNewType)
            {
                var newType = newTypes[iNewType];
                while (oldTypes[iOldType] > newType)
                    --iOldType;
                var version = oldTypes[iOldType] == newType
                    ? oldArchetype->Chunks.GetChangeVersion(iOldType, chunkIndexInOldArchetype)
                    : globalVersion;
                newArchetype->Chunks.SetChangeVersion(iNewType, chunkIndexInNewArchetype, version);
            }

            chunk->ListIndex = chunkIndexInOldArchetype;
            oldArchetype->RemoveFromChunkList(chunk);
            chunk->ListIndex = chunkIndexInNewArchetype;

            if(hasEmptySlots)
                ArchetypeManager.EmptySlotTrackingAddChunk(chunk);

            var entities = (Entity*)chunk->Buffer;
            for (int i = 0; i < count; ++i)
            {
                m_Entities.Archetype[entities[i].Index] = newArchetype;
            }

            oldArchetype->EntityCount -= count;
            newArchetype->EntityCount += count;

            if (oldArchetype->MetaChunkArchetype != newArchetype->MetaChunkArchetype)
            {
                if (oldArchetype->MetaChunkArchetype == null)
                {
                    CreateMetaEntityForChunk(archetypeManager, chunk);
                }
                else if (newArchetype->MetaChunkArchetype == null)
                {
                    archetypeManager.DestroyMetaChunkEntity(chunk->metaChunkEntity);
                    chunk->metaChunkEntity = Entity.Null;
                }
                else
                {
                    var metaChunk = GetComponentChunk(chunk->metaChunkEntity);
                    var sharedComponentDataIndices = metaChunk->SharedComponentValues;
                    SetArchetype(archetypeManager, chunk->metaChunkEntity, newArchetype->MetaChunkArchetype, sharedComponentDataIndices);
                }
            }
        }

        public void AddChunkComponent<T>(NativeArray<ArchetypeChunk> chunkArray, T componentData, ArchetypeManager archetypeManager,
            EntityGroupManager groupManager) where T : struct, IComponentData
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            Archetype* prevOldArchetype = null;
            Archetype* newArchetype = null;
            var type = ComponentType.ReadWrite<T>();
            var chunkType = ComponentType.FromTypeIndex(TypeManager.MakeChunkComponentTypeIndex(type.TypeIndex));
            var chunkCount = chunkArray.Length;
            for(int i=0;i<chunkCount;++i)
            {
                var chunk = chunks[i].m_Chunk;
                var oldArchetype = chunk->Archetype;
                if (oldArchetype != prevOldArchetype)
                {
                    newArchetype = archetypeManager.GetArchetypeWithAddedComponentType(oldArchetype, chunkType, groupManager);
                    prevOldArchetype = oldArchetype;
                }

                MoveChunkToNewArchetype(chunk, newArchetype, GlobalSystemVersion, archetypeManager, chunk->SharedComponentValues);
                if (!type.IsZeroSized)
                {
                    var ptr = GetComponentDataWithTypeRW(chunk->metaChunkEntity, TypeManager.GetTypeIndex<T>(), GlobalSystemVersion);
                    UnsafeUtility.CopyStructureToPtr(ref componentData, ptr);
                }
            }
        }

        public void AddSharedComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType type,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager, int sharedComponentIndex)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            Archetype* prevOldArchetype = null;
            Archetype* newArchetype = null;
            int indexInTypeArray=0;
            for (int i = 0; i < chunkArray.Length; ++i)
            {
                var chunk = chunks[i].m_Chunk;
                var oldArchetype = chunk->Archetype;
                if (oldArchetype != prevOldArchetype)
                {
                    newArchetype = archetypeManager.GetArchetypeWithAddedComponentType(oldArchetype, type, groupManager, &indexInTypeArray);
                    //Assert layout compatible
                    prevOldArchetype = oldArchetype;
                }

                int* temp = stackalloc int[newArchetype->NumSharedComponents];
                int indexOfNewSharedComponent = indexInTypeArray - newArchetype->FirstSharedComponent;
                BuildSharedComponentIndicesWithAddedComponent(indexOfNewSharedComponent, sharedComponentIndex, newArchetype->NumSharedComponents, chunk->SharedComponentValues, temp);

                MoveChunkToNewArchetype(chunk, newArchetype, GlobalSystemVersion, archetypeManager, temp);
            }
        }

        public void AddComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType type,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            if (type.IsZeroSized)
            {
                if (type.IsSharedComponent)
                {
                    AddSharedComponent(chunkArray, type, archetypeManager, groupManager, 0);
                    return;
                }

                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                int indexInTypeArray=0;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        newArchetype = archetypeManager.GetArchetypeWithAddedComponentType(oldArchetype, type, groupManager, &indexInTypeArray);
                        prevOldArchetype = oldArchetype;
                    }
                    MoveChunkToNewArchetype(chunk, newArchetype, GlobalSystemVersion, archetypeManager, chunk->SharedComponentValues);
                }
            }
            else
            {
                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        int indexInTypeArray=0;
                        newArchetype = archetypeManager.GetArchetypeWithAddedComponentType(oldArchetype, type, groupManager, &indexInTypeArray);
                        prevOldArchetype = oldArchetype;
                    }

                    SetArchetype(archetypeManager, chunk, newArchetype, chunk->SharedComponentValues);
                }
            }
        }

        public void RemoveComponent(NativeArray<ArchetypeChunk> chunkArray, ComponentType type,
            ArchetypeManager archetypeManager, EntityGroupManager groupManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            if (type.IsZeroSized)
            {
                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                int indexInOldTypeArray=0;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        if (ChunkDataUtility.GetIndexInTypeArray(oldArchetype, type.TypeIndex) != -1)
                            newArchetype = archetypeManager.GetArchetypeWithRemovedComponentType(oldArchetype, type, groupManager, &indexInOldTypeArray);
                        else
                            newArchetype = null;
                        prevOldArchetype = oldArchetype;
                    }

                    if (newArchetype == null)
                        continue;

                    if (newArchetype->SystemStateCleanupComplete)
                    {
                        DeleteChunkAfterSystemStateCleanupIsComplete(chunk, archetypeManager, groupManager, sharedComponentDataManager);
                        continue;
                    }

                    var sharedComponentValues = chunk->SharedComponentValues;
                    if (type.IsSharedComponent)
                    {
                        int* temp = stackalloc int[newArchetype->NumSharedComponents];
                        int indexOfRemovedSharedComponent = indexInOldTypeArray - oldArchetype->FirstSharedComponent;
                        var sharedComponentDataIndex = chunk->GetSharedComponentValue(indexOfRemovedSharedComponent);
                        sharedComponentDataManager.RemoveReference(sharedComponentDataIndex);
                        BuildSharedComponentIndicesWithRemovedComponent(indexOfRemovedSharedComponent, newArchetype->NumSharedComponents, sharedComponentValues, temp);
                        sharedComponentValues = temp;
                    }

                    MoveChunkToNewArchetype(chunk, newArchetype, GlobalSystemVersion, archetypeManager, sharedComponentValues);
                }
            }
            else
            {
                Archetype* prevOldArchetype = null;
                Archetype* newArchetype = null;
                for (int i = 0; i < chunkArray.Length; ++i)
                {
                    var chunk = chunks[i].m_Chunk;
                    var oldArchetype = chunk->Archetype;
                    if (oldArchetype != prevOldArchetype)
                    {
                        if (ChunkDataUtility.GetIndexInTypeArray(oldArchetype, type.TypeIndex) != -1)
                            newArchetype = archetypeManager.GetArchetypeWithRemovedComponentType(oldArchetype, type, groupManager);
                        else
                            newArchetype = null;
                        prevOldArchetype = oldArchetype;
                    }
                    if(newArchetype != null)
                        if (newArchetype->SystemStateCleanupComplete)
                        {
                            DeleteChunkAfterSystemStateCleanupIsComplete(chunk, archetypeManager, groupManager, sharedComponentDataManager);
                        }
                        else
                        {
                            SetArchetype(archetypeManager, chunk, newArchetype, chunk->SharedComponentValues);
                        }
                }
            }
        }

        public void DeleteChunkAfterSystemStateCleanupIsComplete(Chunk* chunk, ArchetypeManager archetypeManager,
            EntityGroupManager groupManager, SharedComponentDataManager sharedComponentDataManager)
        {
            fixed(EntityDataManager* manager  = &this)
            {
                var entityCount = chunk->Count;
                DeallocateDataEntitiesInChunk(manager, (Entity*)chunk->Buffer, chunk, 0, chunk->Count);
                manager->IncrementComponentOrderVersion(chunk->Archetype, chunk, sharedComponentDataManager);
                chunk->Archetype->EntityCount -= entityCount;
                archetypeManager.SetChunkCount(chunk, 0);
            }
        }

        public static void DeleteChunks(NativeArray<ArchetypeChunk> chunkArray, EntityDataManager* manager, ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var chunks = (ArchetypeChunk*)chunkArray.GetUnsafeReadOnlyPtr();
            for (int i = 0; i != chunkArray.Length; i++)
            {
                var chunk = chunks[i].m_Chunk;
                DestroyBatch((Entity*)chunk->Buffer, manager, archetypeManager, sharedComponentDataManager, chunk, 0, chunk->Count);
            }
        }

        public static void TryRemoveEntityId(Entity* entities, int count, EntityDataManager* manager, ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager)
        {
            var entityIndex = 0;

            var additionalDestroyList = new UnsafeList();
            int minDestroyStride = int.MaxValue;
            int maxDestroyStride = 0;

            while (entityIndex != count)
            {
                int indexInChunk, batchCount;
                var chunk = EntityChunkBatch(manager, entities + entityIndex, count - entityIndex, out indexInChunk, out batchCount);

                if (chunk == null)
                {
                    entityIndex += batchCount;
                    continue;
                }

                AddToDestroyList(chunk, indexInChunk, batchCount, count, ref additionalDestroyList, ref minDestroyStride, ref maxDestroyStride);

                DestroyBatch(entities + entityIndex, manager, archetypeManager, sharedComponentDataManager, chunk, indexInChunk, batchCount);

                entityIndex += batchCount;
            }

            // Apply additional destroys from any LinkedEntityGroup
            if (additionalDestroyList.m_pointer != null)
            {
                var additionalDestroyPtr = (Entity*)additionalDestroyList.m_pointer;
                // Optimal for destruction speed is if entities with same archetype/chunk are followed one after another.
                // So we lay out the to be destroyed objects assuming that the destroyed entities are "similar":
                // Reorder destruction by index in entityGroupArray...

                //@TODO: This is a very specialized fastpath that is likely only going to give benefits in the stress test.
                ///      Figure out how to make this more general purpose.
                if (minDestroyStride == maxDestroyStride)
                {
                    var reordered = (Entity*)UnsafeUtility.Malloc(additionalDestroyList.m_size * sizeof(Entity), 16, Allocator.TempJob);
                    int batchCount = additionalDestroyList.m_size / minDestroyStride;
                    for (int i = 0; i != batchCount; i++)
                    {
                        for (int j = 0; j != minDestroyStride; j++)
                            reordered[j * batchCount + i] = additionalDestroyPtr[i * minDestroyStride + j];
                    }
                    TryRemoveEntityId(reordered, additionalDestroyList.m_size, manager, archetypeManager, sharedComponentDataManager);
                    UnsafeUtility.Free(reordered, Allocator.TempJob);
                }
                else
                {
                    TryRemoveEntityId(additionalDestroyPtr, additionalDestroyList.m_size, manager, archetypeManager, sharedComponentDataManager);
                }

                UnsafeUtility.Free(additionalDestroyPtr, Allocator.TempJob);
            }
        }

        static void DestroyBatch(Entity* entities, EntityDataManager* manager, ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Chunk* chunk, int indexInChunk, int batchCount)
        {
            var archetype = chunk->Archetype;
            if (!archetype->SystemStateCleanupNeeded)
            {
                DeallocateDataEntitiesInChunk(manager, entities, chunk, indexInChunk, batchCount);
                manager->IncrementComponentOrderVersion(archetype, chunk, sharedComponentDataManager);

                if (chunk->ManagedArrayIndex >= 0)
                {
                    // We can just chop-off the end, no need to copy anything
                    if (chunk->Count != indexInChunk + batchCount)
                        ChunkDataUtility.CopyManagedObjects(archetypeManager, chunk, chunk->Count - batchCount, chunk,
                            indexInChunk, batchCount);

                    ChunkDataUtility.ClearManagedObjects(archetypeManager, chunk, chunk->Count - batchCount,
                        batchCount);
                }

                chunk->Archetype->EntityCount -= batchCount;
                archetypeManager.SetChunkCount(chunk, chunk->Count - batchCount);
            }
            else
            {
                var newType = archetype->SystemStateResidueArchetype;

                var sharedComponentValues = chunk->SharedComponentValues;

                if (RequiresBuildingResidueSharedComponentIndices(archetype, newType))
                {
                    var tempAlloc = stackalloc int[newType->NumSharedComponents];
                    BuildResidueSharedComponentIndices(archetype, newType, sharedComponentValues, tempAlloc);
                    sharedComponentValues = tempAlloc;
                }

                for (var i = 0; i < batchCount; i++)
                {
                    var entity = entities[i];
                    manager->IncrementComponentOrderVersion(archetype, manager->GetComponentChunk(entity), sharedComponentDataManager);
                    manager->SetArchetype(archetypeManager, entity, newType, sharedComponentValues);
                }
            }
        }

        static void AddToDestroyList(Chunk* chunk, int indexInChunk, int batchCount, int inputDestroyCount, ref UnsafeList entitiesList, ref int minBufferLength, ref int maxBufferLength)
        {
            var linkedGroupType = TypeManager.GetTypeIndex<LinkedEntityGroup>();
            int indexInArchetype = ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, linkedGroupType);
            if (indexInArchetype != -1)
            {
                var baseHeader = ChunkDataUtility.GetComponentDataWithTypeRO(chunk, indexInChunk, linkedGroupType);
                var stride = chunk->Archetype->SizeOfs[indexInArchetype];
                for (int i = 0; i != batchCount; i++)
                {
                    var header = (BufferHeader*) (baseHeader + stride * i);

                    var entityGroupCount = header->Length - 1;
                    if (entityGroupCount == 0)
                        continue;

                    var entityGroupArray = (Entity*)BufferHeader.GetElementPointer(header) + 1;

                    if (entitiesList.m_capacity == 0)
                        entitiesList.SetCapacity<Entity>(inputDestroyCount * entityGroupCount, Allocator.TempJob);
                    entitiesList.AddRange<Entity>(entityGroupArray, entityGroupCount, Allocator.TempJob);

                    minBufferLength = math.min(minBufferLength, entityGroupCount);
                    maxBufferLength = math.max(maxBufferLength, entityGroupCount);
                }
            }
        }


        public static void RemoveComponent(Entity entity, ComponentType type, EntityDataManager* entityDataManager, ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager, EntityGroupManager groupManager)
        {
            if (!entityDataManager->HasComponent(entity, type))
                return;

            var archetype = entityDataManager->GetArchetype(entity);
            var chunk = entityDataManager->m_Entities.ChunkData[entity.Index].Chunk;
            if (chunk->Locked || chunk->LockedEntityOrder)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                throw new InvalidOperationException(
                    "Cannot remove components in locked Chunks. Unlock Chunk first.");
#else
                    return;
#endif
            }

            int indexInOldTypeArray = -1;
            var newType =
                archetypeManager.GetArchetypeWithRemovedComponentType(archetype, type, groupManager, &indexInOldTypeArray);

            var sharedComponentValues = entityDataManager->GetComponentChunk(entity)->SharedComponentValues;

            if (type.IsSharedComponent)
            {
                int* temp = stackalloc int[newType->NumSharedComponents];
                int indexOfRemovedSharedComponent = indexInOldTypeArray - archetype->FirstSharedComponent;
                BuildSharedComponentIndicesWithRemovedComponent(indexOfRemovedSharedComponent, newType->NumSharedComponents, sharedComponentValues, temp);
                sharedComponentValues = temp;
            }

            entityDataManager->IncrementComponentOrderVersion(archetype, entityDataManager->GetComponentChunk(entity), sharedComponentDataManager);

            entityDataManager->SetArchetype(archetypeManager, entity, newType, sharedComponentValues);

            // Cleanup residue component
            if (newType->SystemStateCleanupComplete)
                TryRemoveEntityId(&entity, 1, entityDataManager, archetypeManager, sharedComponentDataManager);
        }

        public void MoveEntityToChunk(ArchetypeManager typeMan, Entity entity, Chunk* newChunk, int newChunkIndex)
        {
            var oldChunk = m_Entities.ChunkData[entity.Index].Chunk;
            Assert.IsTrue(oldChunk->Archetype == newChunk->Archetype);

            var oldChunkIndex = m_Entities.ChunkData[entity.Index].IndexInChunk;

            ChunkDataUtility.Copy(oldChunk, oldChunkIndex, newChunk, newChunkIndex, 1);

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, oldChunkIndex, newChunk, newChunkIndex, 1);

            m_Entities.ChunkData[entity.Index].Chunk = newChunk;
            m_Entities.ChunkData[entity.Index].IndexInChunk = newChunkIndex;

            var lastIndex = oldChunk->Count - 1;
            // No need to replace with ourselves
            if (lastIndex != oldChunkIndex)
            {
                var lastEntity = (Entity*) ChunkDataUtility.GetComponentDataRO(oldChunk, lastIndex, 0);
                m_Entities.ChunkData[lastEntity->Index].IndexInChunk = oldChunkIndex;

                ChunkDataUtility.Copy(oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
                if (oldChunk->ManagedArrayIndex >= 0)
                    ChunkDataUtility.CopyManagedObjects(typeMan, oldChunk, lastIndex, oldChunk, oldChunkIndex, 1);
            }

            if (oldChunk->ManagedArrayIndex >= 0)
                ChunkDataUtility.ClearManagedObjects(typeMan, oldChunk, lastIndex, 1);

            newChunk->SetAllChangeVersions(GlobalSystemVersion);
            oldChunk->SetAllChangeVersions(GlobalSystemVersion);

            newChunk->Archetype->EntityCount--;
            typeMan.SetChunkCount(oldChunk, oldChunk->Count - 1);
        }

        public void CreateEntities(ArchetypeManager archetypeManager, Archetype* archetype, Entity* entities, int count)
        {
            var sharedComponentValues = stackalloc int[archetype->NumSharedComponents];
            UnsafeUtility.MemClear(sharedComponentValues, archetype->NumSharedComponents*sizeof(int));

            while (count != 0)
            {
                var chunk = archetypeManager.GetChunkWithEmptySlots(archetype, sharedComponentValues);
                int allocatedIndex;
                var allocatedCount = archetypeManager.AllocateIntoChunk(chunk, count, out allocatedIndex);
                AllocateEntities(archetype, chunk, allocatedIndex, allocatedCount, entities);
                ChunkDataUtility.InitializeComponents(chunk, allocatedIndex, allocatedCount);
                chunk->SetAllChangeVersions(GlobalSystemVersion);
                entities += allocatedCount;
                count -= allocatedCount;
            }

            IncrementComponentTypeOrderVersion(archetype);
        }

        public void CreateMetaEntityForChunk(ArchetypeManager archetypeManager, Chunk* chunk)
        {
            CreateEntities(archetypeManager, chunk->Archetype->MetaChunkArchetype, &chunk->metaChunkEntity, 1);
            var typeIndex = TypeManager.GetTypeIndex<ChunkHeader>();
            var chunkHeader = (ChunkHeader*)GetComponentDataWithTypeRW(chunk->metaChunkEntity, typeIndex, GlobalSystemVersion);
            chunkHeader->chunk = chunk;
        }


        public void LockChunks(Chunk** chunks, int count, ChunkFlags flags)
        {
            for (int i = 0; i < count; i++)
            {
                var chunk = chunks[i];

                Assert.IsFalse(chunk->Locked);

                chunk->Flags |= (uint)flags;
                if (chunk->Count < chunk->Capacity && (flags & ChunkFlags.Locked) != 0)
                    ArchetypeManager.EmptySlotTrackingRemoveChunk(chunk);
            }
        }

        public void UnlockChunks(Chunk** chunks, int count, ChunkFlags flags)
        {
            for (int i = 0; i < count; i++)
            {
                var chunk = chunks[i];

                Assert.IsTrue(chunk->Locked);

                chunk->Flags &= ~(uint)flags;
                if (chunk->Count < chunk->Capacity && (flags & ChunkFlags.Locked) != 0)
                    ArchetypeManager.EmptySlotTrackingAddChunk(chunk);
            }
        }

        public void CreateChunks(ArchetypeManager archetypeManager, Archetype* archetype, ArchetypeChunk* chunks, int count)
        {
            int* sharedComponentValues = stackalloc int[archetype->NumSharedComponents];
            UnsafeUtility.MemClear(sharedComponentValues, archetype->NumSharedComponents*sizeof(int));

            Chunk* lastChunk = null;
            int chunkIndex = 0;
            while (count != 0)
            {
                var chunk = archetypeManager.GetCleanChunk(archetype, sharedComponentValues);
                int allocatedIndex;
                var allocatedCount = archetypeManager.AllocateIntoChunk(chunk, count, out allocatedIndex);
                AllocateEntities(archetype, chunk, allocatedIndex, allocatedCount, null);
                ChunkDataUtility.InitializeComponents(chunk, allocatedIndex, allocatedCount);
                chunk->SetAllChangeVersions(GlobalSystemVersion);
                chunks[chunkIndex] = new ArchetypeChunk {m_Chunk = chunk};
                lastChunk = chunk;

                count -= allocatedCount;
                chunkIndex++;
            }

            IncrementComponentTypeOrderVersion(archetype);
        }

        static void BuildResidueSharedComponentIndices(Archetype* srcArchetype, Archetype* dstArchetype, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            int oldFirstShared = srcArchetype->FirstSharedComponent;
            int newFirstShared = dstArchetype->FirstSharedComponent;
            int newCount = dstArchetype->NumSharedComponents;

            for (int oldIndex = 0,newIndex = 0; newIndex < newCount; ++newIndex, ++oldIndex)
            {
                var t = dstArchetype->Types[newIndex+newFirstShared];
                while (t != srcArchetype->Types[oldIndex+oldFirstShared])
                    ++oldIndex;
                dstSharedComponentValues[newIndex] = srcSharedComponentValues[oldIndex];
            }
        }

        static void BuildSharedComponentIndicesWithAddedComponents(Archetype* srcArchetype, Archetype* dstArchetype, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            int oldFirstShared = srcArchetype->FirstSharedComponent;
            int newFirstShared = dstArchetype->FirstSharedComponent;
            int oldCount = srcArchetype->NumSharedComponents;
            int newCount = dstArchetype->NumSharedComponents;

            for (int oldIndex = oldCount-1,newIndex = newCount-1; newIndex >= 0; --newIndex)
            {
                // oldIndex might become -1 which is ok since oldFirstShared is always at least 1. The comparison will then always be false
                if (dstArchetype->Types[newIndex + newFirstShared] == srcArchetype->Types[oldIndex + oldFirstShared])
                    dstSharedComponentValues[newIndex] = srcSharedComponentValues[oldIndex--];
                else
                    dstSharedComponentValues[newIndex] = 0;
            }
        }

        static void BuildSharedComponentIndicesWithAddedComponent(int indexOfNewSharedComponent, int value, int newCount, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            srcSharedComponentValues.CopyTo(dstSharedComponentValues, 0,indexOfNewSharedComponent);
            dstSharedComponentValues[indexOfNewSharedComponent] = value;
            srcSharedComponentValues.CopyTo(dstSharedComponentValues + indexOfNewSharedComponent + 1, indexOfNewSharedComponent, newCount-indexOfNewSharedComponent-1);
        }

        static void BuildSharedComponentIndicesWithRemovedComponent(int indexOfRemovedSharedComponent, int newCount, SharedComponentValues srcSharedComponentValues, int* dstSharedComponentValues)
        {
            srcSharedComponentValues.CopyTo(dstSharedComponentValues, 0,  indexOfRemovedSharedComponent);
            srcSharedComponentValues.CopyTo(dstSharedComponentValues + indexOfRemovedSharedComponent, indexOfRemovedSharedComponent + 1, newCount-indexOfRemovedSharedComponent);
        }

        static bool RequiresBuildingResidueSharedComponentIndices(Archetype* srcArchetype, Archetype* dstArchetype)
        {
            return dstArchetype->NumSharedComponents > 0 && dstArchetype->NumSharedComponents != srcArchetype->NumSharedComponents;
        }

        struct InstantiateRemapChunk
        {
            public Chunk*  Chunk;
            public int     IndexInChunk;
            public int     AllocatedCount;
            public int     InstanceBeginIndex;
        }

        public void InstantiateEntities(ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager, Entity srcEntity, Entity* outputEntities, int instanceCount)
        {
            var linkedType = TypeManager.GetTypeIndex<LinkedEntityGroup>();

            if (HasComponent(srcEntity, linkedType))
            {
                var header = (BufferHeader*) GetComponentDataWithTypeRO(srcEntity, linkedType);
                var entityPtr = (Entity*) BufferHeader.GetElementPointer(header);
                var entityCount = header->Length;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (entityCount == 0 || entityPtr[0] != srcEntity)
                    throw new ArgumentException("LinkedEntityGroup[0] must always be the Entity itself.");
                for (int i = 0; i < entityCount; i++)
                {
                    if (!Exists(entityPtr[i]))
                        throw new ArgumentException("The srcEntity's LinkedEntityGroup references an entity that is invalid. (Entity at index {i} on the LinkedEntityGroup.)");

                    if (GetArchetype(entityPtr[i])->InstantiableArchetype == null)
                        throw new ArgumentException("The srcEntity's LinkedEntityGroup references an entity that has already been destroyed. (Entity at index {i} on the LinkedEntityGroup. Only system state components are left on the entity)");
                }
#endif

                InstantiateEntitiesGroup(archetypeManager, sharedComponentDataManager, entityPtr, entityCount, outputEntities, instanceCount);
            }
            else
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if (!Exists(srcEntity))
                    throw new ArgumentException("srcEntity is not a valid entity");

                if (GetArchetype(srcEntity)->InstantiableArchetype == null)
                    throw new ArgumentException("srcEntity is not instantiable because it has already been destroyed. (Only system state components are left on it)");
#endif

                InstantiateEntitiesOne(archetypeManager, sharedComponentDataManager, srcEntity, outputEntities, instanceCount, null, 0);
            }
        }

        int InstantiateEntitiesOne(ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager, Entity srcEntity, Entity* outputEntities, int instanceCount, InstantiateRemapChunk* remapChunks, int remapChunksCount)
        {
            var src = m_Entities.ChunkData[srcEntity.Index];
            var srcArchetype = src.Chunk->Archetype;
            var dstArchetype = srcArchetype->InstantiableArchetype;

            var temp = stackalloc int[dstArchetype->NumSharedComponents];
            if (RequiresBuildingResidueSharedComponentIndices(srcArchetype, dstArchetype))
            {
                BuildResidueSharedComponentIndices(srcArchetype, dstArchetype, src.Chunk->SharedComponentValues, temp);
            }
            else
            {
                // Always copy shared component indices since GetChunkWithEmptySlots might reallocate the storage of SharedComponentValues
                src.Chunk->SharedComponentValues.CopyTo(temp,0, dstArchetype->NumSharedComponents);
            }
            SharedComponentValues sharedComponentValues = temp;

            Chunk* chunk = null;

            int instanceBeginIndex = 0;
            while (instanceBeginIndex != instanceCount)
            {
                chunk = archetypeManager.GetChunkWithEmptySlots(dstArchetype, sharedComponentValues);
                int indexInChunk;
                var allocatedCount = archetypeManager.AllocateIntoChunk(chunk, instanceCount - instanceBeginIndex, out indexInChunk);
                ChunkDataUtility.ReplicateComponents(src.Chunk, src.IndexInChunk, chunk, indexInChunk, allocatedCount);
                AllocateEntities(dstArchetype, chunk, indexInChunk, allocatedCount, outputEntities + instanceBeginIndex);
                chunk->SetAllChangeVersions(GlobalSystemVersion);

#if UNITY_EDITOR
                for (var i = 0; i < allocatedCount; ++i)
                    m_Entities.Name[outputEntities[i + instanceBeginIndex].Index] = m_Entities.Name[srcEntity.Index];
#endif

                if (remapChunks != null)
                {
                    remapChunks[remapChunksCount].Chunk = chunk;
                    remapChunks[remapChunksCount].IndexInChunk = indexInChunk;
                    remapChunks[remapChunksCount].AllocatedCount = allocatedCount;
                    remapChunks[remapChunksCount].InstanceBeginIndex = instanceBeginIndex;
                    remapChunksCount++;
                }


                instanceBeginIndex += allocatedCount;
            }

            if (chunk != null)
                IncrementComponentOrderVersion(dstArchetype, chunk, sharedComponentDataManager);

            return remapChunksCount;
        }

        void InstantiateEntitiesGroup(ArchetypeManager archetypeManager, SharedComponentDataManager sharedComponentDataManager, Entity* srcEntities, int srcEntityCount, Entity* outputRootEntities, int instanceCount)
        {
            int totalCount = srcEntityCount * instanceCount;

            var tempAllocSize = sizeof(EntityRemapUtility.SparseEntityRemapInfo) * totalCount + sizeof(InstantiateRemapChunk) * totalCount + sizeof(Entity) * instanceCount;
            byte* allocation;
            const int kMaxStackAllocSize = 16 * 1024;

            if (tempAllocSize > kMaxStackAllocSize)
            {
                allocation = (byte*)UnsafeUtility.Malloc(tempAllocSize, 16, Allocator.Temp);
            }
            else
            {
                var temp = stackalloc byte[tempAllocSize];
                allocation = temp;
            }


            var entityRemap = (EntityRemapUtility.SparseEntityRemapInfo*)allocation;
            var remapChunks = (InstantiateRemapChunk*)(entityRemap + totalCount);
            var outputEntities = (Entity*)(remapChunks + totalCount);

            var remapChunksCount = 0;

            for (int i = 0; i != srcEntityCount;i++)
            {
                var srcEntity = srcEntities[i];
                remapChunksCount = InstantiateEntitiesOne(archetypeManager, sharedComponentDataManager, srcEntity, outputEntities, instanceCount, remapChunks, remapChunksCount);

                for (int r = 0; r != instanceCount; r++)
                {
                    var ptr = entityRemap + (r * srcEntityCount + i);
                    ptr->Src = srcEntity;
                    ptr->Target = outputEntities[r];
                }

                if (i == 0)
                {
                    for (int r = 0; r != instanceCount; r++)
                        outputRootEntities[r] = outputEntities[r];
                }
            }

            for (int i = 0; i != remapChunksCount; i++)
            {
                var chunk = remapChunks[i].Chunk;
                var dstArchetype = chunk->Archetype;
                var allocatedCount = remapChunks[i].AllocatedCount;
                var indexInChunk = remapChunks[i].IndexInChunk;
                var instanceBeginIndex = remapChunks[i].InstanceBeginIndex;

                var localRemap = entityRemap + instanceBeginIndex * srcEntityCount;
                EntityRemapUtility.PatchEntitiesForPrefab(dstArchetype->ScalarEntityPatches + 1, dstArchetype->ScalarEntityPatchCount - 1, dstArchetype->BufferEntityPatches, dstArchetype->BufferEntityPatchCount, chunk->Buffer, indexInChunk, allocatedCount, localRemap, srcEntityCount);
            }

            if (tempAllocSize > kMaxStackAllocSize)
                UnsafeUtility.Free(allocation, Allocator.Temp);
        }


        public int GetSharedComponentDataIndex(Entity entity, int typeIndex)
        {
            var archetype = GetArchetype(entity);
            var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(archetype, typeIndex);

            var chunk = m_Entities.ChunkData[entity.Index].Chunk;
            var sharedComponentValueArray = chunk->SharedComponentValues;
            var sharedComponentOffset = indexInTypeArray - archetype->FirstSharedComponent;
            return sharedComponentValueArray[sharedComponentOffset];
        }

        public void SetSharedComponentDataIndex(ArchetypeManager archetypeManager,
            SharedComponentDataManager sharedComponentDataManager, Entity entity, int typeIndex,
            int newSharedComponentDataIndex)
        {
            var archetype = GetArchetype(entity);

            var indexInTypeArray = ChunkDataUtility.GetIndexInTypeArray(archetype, typeIndex);

            var srcChunk = GetComponentChunk(entity);
            var srcSharedComponentValueArray = srcChunk->SharedComponentValues;
            var sharedComponentOffset = indexInTypeArray - archetype->FirstSharedComponent;
            var oldSharedComponentDataIndex = srcSharedComponentValueArray[sharedComponentOffset];

            if (newSharedComponentDataIndex == oldSharedComponentDataIndex)
                return;

            var sharedComponentIndices = stackalloc int[archetype->NumSharedComponents];

            srcSharedComponentValueArray.CopyTo(sharedComponentIndices, 0, archetype->NumSharedComponents);

            sharedComponentIndices[sharedComponentOffset] = newSharedComponentDataIndex;

            var newChunk = archetypeManager.GetChunkWithEmptySlots(archetype, sharedComponentIndices);
            var newChunkIndex = archetypeManager.AllocateIntoChunk(newChunk);

            IncrementComponentOrderVersion(archetype, srcChunk, sharedComponentDataManager);

            MoveEntityToChunk(archetypeManager, entity, newChunk, newChunkIndex);
        }

        internal void IncrementComponentOrderVersion(Archetype* archetype, Chunk* chunk, SharedComponentDataManager sharedComponentDataManager)
        {
            // Increment shared component version
            var sharedComponentValues = chunk->SharedComponentValues;
            for (var i = 0; i < archetype->NumSharedComponents; i++)
                sharedComponentDataManager.IncrementSharedComponentVersion(sharedComponentValues[i]);

            IncrementComponentTypeOrderVersion(archetype);
        }

        internal void IncrementComponentTypeOrderVersion(Archetype* archetype)
        {
            // Increment type component version
            for (var t = 0; t < archetype->TypesCount; ++t)
            {
                var typeIndex = archetype->Types[t].TypeIndex;
                m_ComponentTypeOrderVersion[typeIndex & TypeManager.ClearFlagsMask]++;
            }
        }

        public int GetComponentTypeOrderVersion(int typeIndex)
        {
            return m_ComponentTypeOrderVersion[typeIndex & TypeManager.ClearFlagsMask];
        }
    }
}
