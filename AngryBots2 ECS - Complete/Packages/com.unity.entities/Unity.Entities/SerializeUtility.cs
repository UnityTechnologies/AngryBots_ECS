#if !NET_DOTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace Unity.Entities.Serialization
{
    public static class SerializeUtility
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BufferPatchRecord
        {
            public int ChunkOffset;
            public int AllocSizeBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlobAssetRefPatchRecord
        {
            public int ChunkOffset;
            public int BlobDataOffset;
        }

        public static int CurrentFileFormatVersion = 18;

        public static unsafe void DeserializeWorld(ExclusiveEntityTransaction manager, BinaryReader reader, int numSharedComponents)
        {
            if (manager.ArchetypeManager.CountEntities() != 0)
            {
                throw new ArgumentException(
                    $"DeserializeWorld can only be used on completely empty EntityManager. Please create a new empty World and use EntityManager.MoveEntitiesFrom to move the loaded entities into the destination world instead.");
            }
            int storedVersion = reader.ReadInt();
            if (storedVersion != CurrentFileFormatVersion)
            {
                throw new ArgumentException(
                    $"Attempting to read a entity scene stored in an old file format version (stored version : {storedVersion}, current version : {CurrentFileFormatVersion})");
            }

            var types = ReadTypeArray(reader);
            int totalEntityCount;
            var typeCount = new NativeArray<int>(types.Length, Allocator.Temp);
            var archetypes = ReadArchetypes(reader, types, manager, out totalEntityCount, typeCount);

            int sharedComponentArraysLength = reader.ReadInt();
            var sharedComponentArrays = new NativeArray<int>(sharedComponentArraysLength, Allocator.Temp);
            reader.ReadArray(sharedComponentArrays, sharedComponentArraysLength);

            manager.AllocateConsecutiveEntitiesForLoading(totalEntityCount);

            int totalChunkCount = reader.ReadInt();
            var chunksWithMetaChunkEntities = new NativeList<ArchetypeChunk>(totalChunkCount, Allocator.Temp);

            int totalBlobAssetSize = reader.ReadInt();
            byte* allBlobAssetData = null;

            NativeList<ArchetypeChunk> blobAssetRefChunks = new NativeList<ArchetypeChunk>();
            int blobAssetOwnerIndex = -1;
            if (totalBlobAssetSize != 0)
            {
                allBlobAssetData = (byte*)UnsafeUtility.Malloc(totalBlobAssetSize, 16, Allocator.Persistent);
                reader.ReadBytes(allBlobAssetData, totalBlobAssetSize);
                blobAssetOwnerIndex = manager.SharedComponentDataManager.InsertSharedComponent(new BlobAssetOwner(allBlobAssetData, totalBlobAssetSize));
                blobAssetRefChunks = new NativeList<ArchetypeChunk>(32, Allocator.Temp);
                var end = allBlobAssetData + totalBlobAssetSize;
                var header = (BlobAssetHeader*)allBlobAssetData;
                while (header < end)
                {
                    header->ValidationPtr = header + 1;
                    header = (BlobAssetHeader*)OffsetFromPointer(header+1, header->Length);
                }
            }

            int sharedComponentArraysIndex = 0;
            for (int i = 0; i < totalChunkCount; ++i)
            {
                var chunk = (Chunk*) UnsafeUtility.Malloc(Chunk.kChunkSize, 64, Allocator.Persistent);
                reader.ReadBytes(chunk, Chunk.kChunkSize);

                var archetype = chunk->Archetype = archetypes[(int)chunk->Archetype].Archetype;
                var numSharedComponentsInArchetype = chunk->Archetype->NumSharedComponents;
                int* sharedComponentValueArray = (int*)sharedComponentArrays.GetUnsafePtr() + sharedComponentArraysIndex;

                for (int j = 0; j < numSharedComponentsInArchetype; ++j)
                {
                    // The shared component 0 is not part of the array, so an index equal to the array size is valid.
                    if (sharedComponentValueArray[j] > numSharedComponents)
                    {
                        throw new ArgumentException(
                            $"Archetype uses shared component at index {sharedComponentValueArray[j]} but only {numSharedComponents} are available, check if the shared scene has been properly loaded.");
                    }
                }

                var remapedSharedComponentValues = stackalloc int[archetype->NumSharedComponents];
                RemapSharedComponentIndices(remapedSharedComponentValues, archetype, sharedComponentValueArray);

                sharedComponentArraysIndex += numSharedComponentsInArchetype;

                // Allocate additional heap memory for buffers that have overflown into the heap, and read their data.
                int bufferAllocationCount = reader.ReadInt();
                if (bufferAllocationCount > 0)
                {
                    var bufferPatches = new NativeArray<BufferPatchRecord>(bufferAllocationCount, Allocator.Temp);
                    reader.ReadArray(bufferPatches, bufferPatches.Length);

                    // TODO: PERF: Batch malloc interface.
                    for (int pi = 0; pi < bufferAllocationCount; ++pi)
                    {
                        var target = (BufferHeader*)OffsetFromPointer(chunk->Buffer, bufferPatches[pi].ChunkOffset);

                        // TODO: Alignment
                        target->Pointer = (byte*) UnsafeUtility.Malloc(bufferPatches[pi].AllocSizeBytes, 8, Allocator.Persistent);

                        reader.ReadBytes(target->Pointer, bufferPatches[pi].AllocSizeBytes);
                    }

                    bufferPatches.Dispose();
                }

                if (totalBlobAssetSize != 0 && archetype->ContainsBlobAssetRefs)
                {
                    blobAssetRefChunks.Add(new ArchetypeChunk {m_Chunk = chunk});
                    PatchBlobAssetsInChunkAfterLoad(chunk, allBlobAssetData);
                }

                manager.AddExistingChunk(chunk, remapedSharedComponentValues);

                if (chunk->metaChunkEntity != Entity.Null)
                {
                    chunksWithMetaChunkEntities.Add(new ArchetypeChunk{ m_Chunk = chunk});
                }

            }

            if (totalBlobAssetSize != 0)
            {
                manager.DataManager->AddSharedComponent(blobAssetRefChunks, ComponentType.ReadWrite<BlobAssetOwner>(), manager.ArchetypeManager, manager.EntityGroupManager, blobAssetOwnerIndex);
                manager.SharedComponentDataManager.AddReference(blobAssetOwnerIndex, blobAssetRefChunks.Length - 1);
                blobAssetRefChunks.Dispose();
            }

            for (int i = 0; i < chunksWithMetaChunkEntities.Length; ++i)
            {
                var chunk = chunksWithMetaChunkEntities[i].m_Chunk;
                var archetype = chunk->Archetype;
                manager.SetComponentData(chunk->metaChunkEntity, new ChunkHeader{chunk = chunk});
            }

            chunksWithMetaChunkEntities.Dispose();
            sharedComponentArrays.Dispose();
            archetypes.Dispose();
            types.Dispose();
            typeCount.Dispose();
        }

        private static unsafe NativeArray<EntityArchetype> ReadArchetypes(BinaryReader reader, NativeArray<int> types, ExclusiveEntityTransaction entityManager,
            out int totalEntityCount, NativeArray<int> typeCount)
        {
            int archetypeCount = reader.ReadInt();
            var archetypes = new NativeArray<EntityArchetype>(archetypeCount, Allocator.Temp);
            totalEntityCount = 0;
            var tempComponentTypes = new NativeList<ComponentType>(Allocator.Temp);
            for (int i = 0; i < archetypeCount; ++i)
            {
                var archetypeEntityCount = reader.ReadInt();
                totalEntityCount += archetypeEntityCount;
                int archetypeComponentTypeCount = reader.ReadInt();
                tempComponentTypes.Clear();
                for (int iType = 0; iType < archetypeComponentTypeCount; ++iType)
                {
                    int typeIndexInFile = reader.ReadInt();
                    int typeIndexInFileWithoutFlags = typeIndexInFile & TypeManager.ClearFlagsMask;
                    int typeIndex = types[typeIndexInFileWithoutFlags];
                    if (TypeManager.IsChunkComponent(typeIndexInFile))
                        typeIndex = TypeManager.MakeChunkComponentTypeIndex(typeIndex);

                    typeCount[typeIndexInFileWithoutFlags] += archetypeEntityCount;
                    tempComponentTypes.Add(ComponentType.FromTypeIndex(typeIndex));
                }

                archetypes[i] = entityManager.CreateArchetype((ComponentType*) tempComponentTypes.GetUnsafePtr(),
                    tempComponentTypes.Length);
            }

            tempComponentTypes.Dispose();
            return archetypes;
        }

        private static NativeArray<int> ReadTypeArray(BinaryReader reader)
        {
            int typeCount = reader.ReadInt();
            var typeHashBuffer = new NativeArray<ulong>(typeCount, Allocator.Temp);

            reader.ReadArray(typeHashBuffer, typeCount);

            int nameBufferSize = reader.ReadInt();
            var nameBuffer = new NativeArray<byte>(nameBufferSize, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            reader.ReadBytes(nameBuffer, nameBufferSize);
            var types = new NativeArray<int>(typeCount, Allocator.Temp);
            int offset = 0;
            for (int i = 0; i < typeCount; ++i)
            {
                string typeName = StringFromNativeBytes(nameBuffer, offset);
                var type = Type.GetType(typeName);

                if (type == null)
                    throw new ArgumentException($"Type no longer exists: '{typeName}'");

                types[i] = TypeManager.GetTypeIndex(type);
                if (types[i] == 0)
                    throw new ArgumentException("Unknown type '" + typeName + "'");

                if (typeHashBuffer[i] != TypeManager.GetTypeInfo(types[i]).StableTypeHash)
                    throw new ArgumentException($"Type layout has changed: '{type.Name}'");

                offset += typeName.Length + 1;
            }

            nameBuffer.Dispose();
            typeHashBuffer.Dispose();
            return types;
        }

        internal static unsafe void GetAllArchetypes(ArchetypeManager archetypeManager, out Dictionary<EntityArchetype, int> archetypeToIndex, out EntityArchetype[] archetypeArray)
        {
            var archetypeList = new List<EntityArchetype>();
            for (var i = archetypeManager.m_Archetypes.Count - 1; i >= 0; --i)
            {
                var archetype = archetypeManager.m_Archetypes.p[i];
                if (archetype->EntityCount >= 0)
                    archetypeList.Add(new EntityArchetype{Archetype = archetype});
            }
            //todo: sort archetypes to get deterministic indices
            archetypeToIndex = new Dictionary<EntityArchetype, int>();
            for (int i = 0; i < archetypeList.Count; ++i)
            {
                archetypeToIndex.Add(archetypeList[i],i);
            }

            archetypeArray = archetypeList.ToArray();
        }

        public static unsafe void SerializeWorld(EntityManager entityManager, BinaryWriter writer, out int[] sharedComponentsToSerialize)
        {
            var entityRemapInfos = new NativeArray<EntityRemapUtility.EntityRemapInfo>(entityManager.EntityCapacity, Allocator.Temp);
            SerializeWorld(entityManager, writer, out sharedComponentsToSerialize, entityRemapInfos);
            entityRemapInfos.Dispose();
        }

        public static unsafe void SerializeWorld(EntityManager entityManager, BinaryWriter writer, out int[] sharedComponentsToSerialize, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapInfos)
        {
            writer.Write(CurrentFileFormatVersion);
            var archetypeManager = entityManager.ArchetypeManager;

            Dictionary<EntityArchetype, int> archetypeToIndex;
            EntityArchetype[] archetypeArray;
            GetAllArchetypes(archetypeManager, out archetypeToIndex, out archetypeArray);

            var typeindices = new HashSet<int>();
            foreach (var archetype in archetypeArray)
            {
                for (int iType = 0; iType < archetype.Archetype->TypesCount; ++iType)
                {
                    typeindices.Add(archetype.Archetype->Types[iType].TypeIndex & TypeManager.ClearFlagsMask);
                }
            }

            var typeArray = typeindices.Select(index =>
            {
                var type = TypeManager.GetType(index);
                var name = TypeManager.GetType(index).AssemblyQualifiedName;
                var hash = TypeManager.GetTypeInfo(index).StableTypeHash;
                return new
                {
                    index,
                    type,
                    name,
                    hash,
                    utf8Name = Encoding.UTF8.GetBytes(name)
                };
            }).OrderBy(t => t.name).ToArray();

            int typeNameBufferSize = typeArray.Sum(t => t.utf8Name.Length + 1);
            writer.Write(typeArray.Length);
            foreach (var n in typeArray)
            {
                writer.Write(n.hash);
            }

            writer.Write(typeNameBufferSize);
            foreach(var n in typeArray)
            {
                writer.Write(n.utf8Name);
                writer.Write((byte)0);
            }

            var typeIndexMap = new Dictionary<int, int>();
            for (int i = 0; i < typeArray.Length; ++i)
            {
                typeIndexMap[typeArray[i].index] = i;
            }

            WriteArchetypes(writer, archetypeArray, typeIndexMap);
            var sharedComponentMapping = GatherSharedComponents(archetypeArray, out var sharedComponentArraysTotalCount);
            var sharedComponentArrays = new NativeArray<int>(sharedComponentArraysTotalCount, Allocator.Temp);
            FillSharedComponentArrays(sharedComponentArrays, archetypeArray, sharedComponentMapping);
            writer.Write(sharedComponentArrays.Length);
            writer.WriteArray(sharedComponentArrays);
            sharedComponentArrays.Dispose();

            //TODO: ensure chunks are defragged?

            var bufferPatches = new NativeList<BufferPatchRecord>(128, Allocator.Temp);
            var totalChunkCount = GenerateRemapInfo(entityManager, archetypeArray, entityRemapInfos);

            writer.Write(totalChunkCount);

            GatherAllUsedBlobAssets(archetypeArray, out var blobAssetRefs, out var blobAssets);

            var blobAssetOffsets = new NativeArray<int>(blobAssets.Length, Allocator.Temp);
            int totalBlobAssetSize = 0;

            int Align16(int x) => (x+15)&~15;

            for(int i = 0; i<blobAssets.Length; ++i)
            {
                totalBlobAssetSize += sizeof(BlobAssetHeader);
                blobAssetOffsets[i] = totalBlobAssetSize;
                totalBlobAssetSize += Align16(blobAssets[i].header->Length);
            }

            writer.Write(totalBlobAssetSize);

            var zeroBytes = int4.zero;
            for(int i = 0; i<blobAssets.Length; ++i)
            {
                var blobAssetLength = blobAssets[i].header->Length;
                BlobAssetHeader header = new BlobAssetHeader
                    {ValidationPtr = null, Allocator = Allocator.None, Length = Align16(blobAssetLength)};
                writer.WriteBytes(&header, sizeof(BlobAssetHeader));
                writer.WriteBytes(blobAssets[i].header + 1, blobAssetLength);
                writer.WriteBytes(&zeroBytes, header.Length - blobAssetLength);
            }

            var tempChunk = (Chunk*)UnsafeUtility.Malloc(Chunk.kChunkSize, 16, Allocator.Temp);

            for(int archetypeIndex = 0; archetypeIndex < archetypeArray.Length; ++archetypeIndex)
            {
                var archetype = archetypeArray[archetypeIndex].Archetype;
                for (var ci = 0; ci < archetype->Chunks.Count; ++ci)
                {
                    var chunk = archetype->Chunks.p[ci];
                    bufferPatches.Clear();

                    UnsafeUtility.MemCpy(tempChunk, chunk, Chunk.kChunkSize);
                    tempChunk->metaChunkEntity = EntityRemapUtility.RemapEntity(ref entityRemapInfos, tempChunk->metaChunkEntity);

                    // Prevent patching from touching buffers allocated memory
                    BufferHeader.PatchAfterCloningChunk(tempChunk);

                    byte* tempChunkBuffer = tempChunk->Buffer;
                    EntityRemapUtility.PatchEntities(archetype->ScalarEntityPatches, archetype->ScalarEntityPatchCount, archetype->BufferEntityPatches, archetype->BufferEntityPatchCount, tempChunkBuffer, tempChunk->Count, ref entityRemapInfos);
                    if(archetype->ContainsBlobAssetRefs)
                        PatchBlobAssetsInChunkBeforeSave(tempChunk, chunk, blobAssetOffsets, blobAssetRefs);

                    FillPatchRecordsForChunk(chunk, bufferPatches);

                    ClearChunkHeaderComponents(tempChunk);
                    ChunkDataUtility.MemsetUnusedChunkData(tempChunk, 0);
                    tempChunk->Archetype = (Archetype*) archetypeIndex;

                    if (archetype->NumManagedArrays != 0)
                    {
                        throw new ArgumentException("Serialization of GameObject components is not supported for pure entity scenes");
                    }

                    writer.WriteBytes(tempChunk, Chunk.kChunkSize);

                    writer.Write(bufferPatches.Length);

                    if (bufferPatches.Length > 0)
                    {
                        writer.WriteList(bufferPatches);

                        // Write heap backed data for each required patch.
                        // TODO: PERF: Investigate static-only deserialization could manage one block and mark in pointers somehow that they are not indiviual
                        for (int i = 0; i < bufferPatches.Length; ++i)
                        {
                            var patch = bufferPatches[i];
                            var header = (BufferHeader*)OffsetFromPointer(tempChunk->Buffer, patch.ChunkOffset);
                            writer.WriteBytes(header->Pointer, patch.AllocSizeBytes);
                            BufferHeader.Destroy(header);
                        }
                    }
                }
            }

            blobAssetRefs.Dispose();
            blobAssets.Dispose();

            bufferPatches.Dispose();
            UnsafeUtility.Free(tempChunk, Allocator.Temp);

            sharedComponentsToSerialize = new int[sharedComponentMapping.Count-1];

            foreach (var i in sharedComponentMapping)
                if(i.Key != 0)
                    sharedComponentsToSerialize[i.Value - 1] = i.Key;
        }

        unsafe struct BlobAssetRefKey : IEquatable<BlobAssetRefKey>
        {
            public Chunk* chunk;
            public int offsetInBuffer;


            public bool Equals(BlobAssetRefKey other)
            {
                return chunk == other.chunk && offsetInBuffer == other.offsetInBuffer;
            }
        }

        unsafe struct BlobAssetPtr : IEquatable<BlobAssetPtr>
        {
            public BlobAssetPtr(BlobAssetHeader* header)
            {
                this.header = header;
            }
            public readonly BlobAssetHeader* header;
            public bool Equals(BlobAssetPtr other)
            {
                return header == other.header;
            }

            public override int GetHashCode()
            {
                BlobAssetHeader* onStack = header;
                return (int)math.hash(&onStack, sizeof(BlobAssetHeader*));
            }
        }

        private static unsafe void GatherAllUsedBlobAssets(EntityArchetype[] archetypeArray, out NativeHashMap<BlobAssetRefKey, int> blobAssetRefs, out NativeList<BlobAssetPtr> blobAssets)
        {
            var blobAssetMap = new NativeHashMap<BlobAssetPtr, int>(100, Allocator.Temp);

            blobAssetRefs = new NativeHashMap<BlobAssetRefKey, int>(100, Allocator.Temp);
            blobAssets = new NativeList<BlobAssetPtr>(100, Allocator.Temp);
            for (int archetypeIndex = 0; archetypeIndex < archetypeArray.Length; ++archetypeIndex)
            {
                var archetype = archetypeArray[archetypeIndex].Archetype;
                if (!archetype->ContainsBlobAssetRefs)
                    continue;

                var typeCount = archetype->TypesCount;
                for (var ci = 0; ci < archetype->Chunks.Count; ++ci)
                {
                    var chunk = archetype->Chunks.p[ci];
                    var entityCount = chunk->Count;
                    for (var unordered_ti = 0; unordered_ti < typeCount; ++unordered_ti)
                    {
                        var ti = archetype->TypeMemoryOrder[unordered_ti];
                        var type = archetype->Types[ti];
                        if(type.IsZeroSized)
                            continue;

                        var ct = TypeManager.GetTypeInfo(type.TypeIndex);
                        var blobAssetRefCount = ct.BlobAssetRefOffsetCount;
                        if(blobAssetRefCount == 0)
                            continue;

                        var chunkBuffer = chunk->Buffer;

                        if (type.IsBuffer)
                        {

                        }
                        else if (blobAssetRefCount > 0)
                        {
                            int subArrayOffset = archetype->Offsets[ti];
                            byte* componentArrayStart = OffsetFromPointer(chunkBuffer , subArrayOffset);
                            int size = archetype->SizeOfs[ti];
                            byte* end = componentArrayStart + size * entityCount;
                            for (var componentData = componentArrayStart; componentData < end; componentData += size)
                            {
                                for (int i = 0; i < blobAssetRefCount; ++i)
                                {
                                    var offset = ct.BlobAssetRefOffsets[i].Offset;
                                    var blobAssetRefPtr = (BlobAssetReferenceData*)(componentData + offset);
                                    if(blobAssetRefPtr->m_Ptr == null)
                                        continue;

                                    var blobAssetPtr = new BlobAssetPtr((*(BlobAssetHeader**)blobAssetRefPtr)-1);
                                    var key = new BlobAssetRefKey { chunk = chunk, offsetInBuffer = (int)((byte*)blobAssetRefPtr - chunkBuffer)};


                                    if (!blobAssetMap.TryGetValue(blobAssetPtr, out var blobAssetIndex))
                                    {
                                        blobAssetIndex = blobAssets.Length;
                                        blobAssets.Add(blobAssetPtr);
                                        blobAssetMap.TryAdd(blobAssetPtr, blobAssetIndex);
                                    }
                                    blobAssetRefs.TryAdd(key, blobAssetIndex);
                                }
                            }
                        }
                    }
                }
            }
            blobAssetMap.Dispose();
        }

        private static unsafe void PatchBlobAssetsInChunkBeforeSave(Chunk* tempChunk, Chunk* originalChunk,
            NativeArray<int> blobAssetOffsets, NativeHashMap<BlobAssetRefKey, int> blobAssetRefs)
        {
            var archetype = originalChunk->Archetype;
            var typeCount = archetype->TypesCount;
            var entityCount = originalChunk->Count;
            for (var unordered_ti = 0; unordered_ti < typeCount; ++unordered_ti)
            {
                var ti = archetype->TypeMemoryOrder[unordered_ti];
                var type = archetype->Types[ti];
                if(type.IsZeroSized)
                    continue;

                var ct = TypeManager.GetTypeInfo(type.TypeIndex);
                var blobAssetRefCount = ct.BlobAssetRefOffsetCount;
                if(blobAssetRefCount == 0)
                    continue;

                var chunkBuffer = tempChunk->Buffer;

                if (type.IsBuffer)
                {
                    throw new InvalidOperationException("BlobAssetReferences are not supported inside DynamicBuffer components");
                }
                else if (blobAssetRefCount > 0)
                {
                    int subArrayOffset = archetype->Offsets[ti];
                    byte* componentArrayStart = OffsetFromPointer(chunkBuffer , subArrayOffset);
                    int size = archetype->SizeOfs[ti];
                    byte* end = componentArrayStart + size * entityCount;
                    for (var componentData = componentArrayStart; componentData < end; componentData += size)
                    {
                        for (int i = 0; i < blobAssetRefCount; ++i)
                        {
                            var offset = ct.BlobAssetRefOffsets[i].Offset;
                            var blobAssetRefPtr = (BlobAssetReferenceData*)(componentData + offset);
                            int value = -1;
                            if (blobAssetRefPtr->m_Ptr != null)
                            {
                                var blobAssetPtr = new BlobAssetPtr((*(BlobAssetHeader**)blobAssetRefPtr)-1);
                                var key = new BlobAssetRefKey { chunk = originalChunk, offsetInBuffer = (int)((byte*)blobAssetRefPtr - chunkBuffer)};

                                bool found = blobAssetRefs.TryGetValue(key, out value);
                                value = blobAssetOffsets[value];
                                Assert.IsTrue(found);
                            }
                            blobAssetRefPtr->m_Ptr = (byte*)value;
                        }
                    }
                }
            }
        }

        private static unsafe void PatchBlobAssetsInChunkAfterLoad(Chunk* chunk, byte* allBlobAssetData)
        {
            var blobAssetMap = new NativeHashMap<BlobAssetPtr, int>(100, Allocator.Temp);

            var archetype = chunk->Archetype;
            var typeCount = archetype->TypesCount;
            var entityCount = chunk->Count;
            for (var unordered_ti = 0; unordered_ti < typeCount; ++unordered_ti)
            {
                var ti = archetype->TypeMemoryOrder[unordered_ti];
                var type = archetype->Types[ti];
                if(type.IsZeroSized)
                    continue;

                var ct = TypeManager.GetTypeInfo(type.TypeIndex);
                var blobAssetRefCount = ct.BlobAssetRefOffsetCount;
                if(blobAssetRefCount == 0)
                    continue;

                var chunkBuffer = chunk->Buffer;

                if (type.IsBuffer)
                {
                    throw new InvalidOperationException("BlobAssetReferences are not supported inside DynamicBuffer components");
                }
                else if (blobAssetRefCount > 0)
                {
                    int subArrayOffset = archetype->Offsets[ti];
                    byte* componentArrayStart = OffsetFromPointer(chunkBuffer , subArrayOffset);
                    int size = archetype->SizeOfs[ti];
                    byte* end = componentArrayStart + size * entityCount;
                    for (var componentData = componentArrayStart; componentData < end; componentData += size)
                    {
                        for (int i = 0; i < blobAssetRefCount; ++i)
                        {
                            var offset = ct.BlobAssetRefOffsets[i].Offset;
                            var blobAssetRefPtr = (BlobAssetReferenceData*)(componentData + offset);
                            int value = (int) blobAssetRefPtr->m_Ptr;
                            byte* ptr = null;
                            if (value != -1)
                            {
                                ptr = allBlobAssetData + value;
                            }
                            blobAssetRefPtr->m_Ptr = ptr;
                        }
                    }
                }
            }
        }

        private static unsafe void FillPatchRecordsForChunk(Chunk* chunk, NativeList<BufferPatchRecord> bufferPatches)
        {
            var archetype = chunk->Archetype;
            byte* tempChunkBuffer = chunk->Buffer;
            int entityCount = chunk->Count;

            // Find all buffer pointer locations and work out how much memory the deserializer must allocate on load.
            for (int ti = 0; ti < archetype->TypesCount; ++ti)
            {
                int index = archetype->TypeMemoryOrder[ti];
                var type = archetype->Types[index];
                if(type.IsZeroSized)
                    continue;

                if (type.IsBuffer)
                {
                    var ct = TypeManager.GetTypeInfo(type.TypeIndex);
                    int subArrayOffset = archetype->Offsets[index];
                    BufferHeader* header = (BufferHeader*) OffsetFromPointer(tempChunkBuffer, subArrayOffset);
                    int stride = archetype->SizeOfs[index];
                    var elementSize = ct.ElementSize;

                    for (int bi = 0; bi < entityCount; ++bi)
                    {
                        if (header->Pointer != null)
                        {
                            int capacityInBytes = elementSize * header->Capacity;
                            bufferPatches.Add(new BufferPatchRecord
                            {
                                ChunkOffset = (int) (((byte*) header) - tempChunkBuffer),
                                AllocSizeBytes = capacityInBytes
                            });
                        }

                        header = (BufferHeader*) OffsetFromPointer(header, stride);
                    }
                }
            }
        }

        static unsafe void FillSharedComponentIndexRemap(int* remapArray, Archetype* archetype)
        {
            int i = 0;
            for (int iType = 1; iType < archetype->TypesCount; ++iType)
            {
                int orderedIndex = archetype->TypeMemoryOrder[iType] - archetype->FirstSharedComponent;
                if (0 <= orderedIndex && orderedIndex < archetype->NumSharedComponents)
                    remapArray[i++] = orderedIndex;
            }
        }

        static unsafe void RemapSharedComponentIndices(int* destValues, Archetype* archetype, int* sourceValues)
        {
            int i = 0;
            for (int iType = 1; iType < archetype->TypesCount; ++iType)
            {
                int orderedIndex = archetype->TypeMemoryOrder[iType] - archetype->FirstSharedComponent;
                if (0 <= orderedIndex && orderedIndex < archetype->NumSharedComponents)
                    destValues[orderedIndex] = sourceValues[i++];
            }
        }

        private static unsafe void FillSharedComponentArrays(NativeArray<int> sharedComponentArrays, EntityArchetype[] archetypeArray, Dictionary<int, int> sharedComponentMapping)
        {
            int index = 0;
            for (int iArchetype = 0; iArchetype < archetypeArray.Length; ++iArchetype)
            {
                var archetype = archetypeArray[iArchetype].Archetype;
                int numSharedComponents = archetype->NumSharedComponents;
                if(numSharedComponents==0)
                    continue;
                var sharedComponentIndexRemap = stackalloc int[numSharedComponents];
                FillSharedComponentIndexRemap(sharedComponentIndexRemap, archetype);
                for (int iChunk = 0; iChunk < archetype->Chunks.Count; ++iChunk)
                {
                    var sharedComponents = archetype->Chunks.p[iChunk]->SharedComponentValues;
                    for (int iType = 0; iType < numSharedComponents; iType++)
                    {
                        int remappedIndex = sharedComponentIndexRemap[iType];
                        sharedComponentArrays[index++] = sharedComponentMapping[sharedComponents[remappedIndex]];
                    }
                }
            }
            Assert.AreEqual(sharedComponentArrays.Length,index);
        }

        private static unsafe Dictionary<int, int> GatherSharedComponents(EntityArchetype[] archetypeArray, out int sharedComponentArraysTotalCount)
        {
            sharedComponentArraysTotalCount = 0;
            var sharedIndexToSerialize = new Dictionary<int, int>();
            sharedIndexToSerialize[0] = 0; // All default values map to 0
            int nextIndex = 1;
            for (int iArchetype = 0; iArchetype < archetypeArray.Length; ++iArchetype)
            {
                var archetype = archetypeArray[iArchetype].Archetype;
                sharedComponentArraysTotalCount += archetype->Chunks.Count * archetype->NumSharedComponents;

                int numSharedComponents = archetype->NumSharedComponents;
                for (int iType = 0; iType < numSharedComponents; iType++)
                {
                    var sharedComponents = archetype->Chunks.GetSharedComponentValueArrayForType(iType);
                    for (int iChunk = 0; iChunk < archetype->Chunks.Count; ++iChunk)
                    {
                        int sharedComponentIndex = sharedComponents[iChunk];
                        if (!sharedIndexToSerialize.ContainsKey(sharedComponentIndex))
                        {
                            sharedIndexToSerialize[sharedComponentIndex] = nextIndex++;
                        }
                    }
                }
            }

            return sharedIndexToSerialize;
        }

        private static unsafe void ClearChunkHeaderComponents(Chunk* chunk)
        {
            int chunkHeaderTypeIndex = TypeManager.GetTypeIndex<ChunkHeader>();
            var archetype = chunk->Archetype;
            var typeIndexInArchetype = ChunkDataUtility.GetIndexInTypeArray(chunk->Archetype, chunkHeaderTypeIndex);
            if (typeIndexInArchetype == -1)
                return;

            var buffer = chunk->Buffer;
            var length = chunk->Count;
            var startOffset = archetype->Offsets[typeIndexInArchetype];
            var chunkHeaders = (ChunkHeader*)(buffer + startOffset);
            for (int i = 0; i < length; ++i)
            {
                chunkHeaders[i].chunk = null;
            }
        }

        static unsafe byte* OffsetFromPointer(void* ptr, int offset)
        {
            return ((byte*)ptr) + offset;
        }

        static unsafe void WriteArchetypes(BinaryWriter writer, EntityArchetype[] archetypeArray, Dictionary<int, int> typeIndexMap)
        {
            writer.Write(archetypeArray.Length);

            foreach (var archetype in archetypeArray)
            {
                writer.Write(archetype.Archetype->EntityCount);
                writer.Write(archetype.Archetype->TypesCount - 1);
                for (int i = 1; i < archetype.Archetype->TypesCount; ++i)
                {
                    var componentType = archetype.Archetype->Types[i];
                    int flag = componentType.IsChunkComponent ? TypeManager.ChunkComponentTypeFlag : 0;
                    writer.Write(typeIndexMap[componentType.TypeIndex & TypeManager.ClearFlagsMask] | flag);
                }
            }
        }

        static unsafe int GenerateRemapInfo(EntityManager entityManager, EntityArchetype[] archetypeArray, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapInfos)
        {
            int nextEntityId = 1; //0 is reserved for Entity.Null;

            int totalChunkCount = 0;
            for (int archetypeIndex = 0; archetypeIndex < archetypeArray.Length; ++archetypeIndex)
            {
                var archetype = archetypeArray[archetypeIndex].Archetype;
                for (int i = 0; i < archetype->Chunks.Count; ++i)
                {
                    var chunk = archetype->Chunks.p[i];
                    for (int iEntity = 0; iEntity < chunk->Count; ++iEntity)
                    {
                        var entity = *(Entity*)ChunkDataUtility.GetComponentDataRO(chunk, iEntity, 0);
                        EntityRemapUtility.AddEntityRemapping(ref entityRemapInfos, entity, new Entity { Version = 0, Index = nextEntityId });
                        ++nextEntityId;
                    }

                    totalChunkCount += 1;
                }
            }

            return totalChunkCount;
        }

        static unsafe string StringFromNativeBytes(NativeArray<byte> bytes, int offset = 0)
        {
            int len = 0;
            while (bytes[offset + len] != 0)
                ++len;
            return System.Text.Encoding.UTF8.GetString((Byte*) bytes.GetUnsafePtr() + offset, len);
        }
    }


}
#endif
