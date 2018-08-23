using System;
using Unity.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Entities
{
    internal enum FilterType
    {
        None,
        SharedComponent,
        Changed,
        IndexList
    }

    //@TODO: Use field offset / union here... There seems to be an issue in mono preventing it...
    internal unsafe struct ComponentGroupFilter
    {
        public struct IndexList
        {
            public int Length;
            [NativeDisableUnsafePtrRestriction] public int* Indices;
        }

        public struct SharedComponentData
        {
            public int Count;
            public fixed int IndexInComponentGroup[2];
            public fixed int SharedComponentIndex[2];
        }

        public struct ChangedFilter
        {
            public const int Capacity = 2;

            public int Count;
            public fixed int IndexInComponentGroup[2];
        }


        public FilterType Type;
        public uint RequiredChangeVersion;


        public SharedComponentData Shared;
        public ChangedFilter Changed;
        public IndexList Indices;

        public bool RequiresMatchesFilter => Type == FilterType.SharedComponent || Type == FilterType.Changed;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public void AssertValid()
        {
            if (Type == FilterType.SharedComponent)
                Assert.IsTrue(Shared.Count <= 2 && Shared.Count > 0);
            else if (Type == FilterType.Changed)
                Assert.IsTrue(Changed.Count <= 2 && Changed.Count > 0);
        }
#endif
    }

    internal unsafe struct ComponentChunkCache
    {
        [NativeDisableUnsafePtrRestriction] public void* CachedPtr;
        public int CachedBeginIndex;
        public int CachedEndIndex;
        public int CachedSizeOf;
    }

    internal unsafe struct ComponentChunkIterator
    {
        [NativeDisableUnsafePtrRestriction] private readonly MatchingArchetypes* m_FirstMatchingArchetype;
        [NativeDisableUnsafePtrRestriction] private MatchingArchetypes* m_CurrentMatchingArchetype;
        public int IndexInComponentGroup;
        private int m_CurrentArchetypeIndex;
        [NativeDisableUnsafePtrRestriction] private Chunk* m_CurrentChunk;
        private int m_CurrentChunkIndex;

        private ComponentGroupFilter m_Filter;

        private readonly uint m_GlobalSystemVersion;

        internal int GetSharedComponentFromCurrentChunk(int sharedComponentIndex)
        {
            var archetype = m_CurrentMatchingArchetype->Archetype;
            var indexInArchetype = m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[sharedComponentIndex];
            var sharedComponentOffset = archetype->SharedComponentOffset[indexInArchetype];
            return m_CurrentChunk->SharedComponentValueArray[sharedComponentOffset];
        }

        public ComponentChunkIterator(MatchingArchetypes* match, uint globalSystemVersion,
            ref ComponentGroupFilter filter)
        {
            m_FirstMatchingArchetype = match;
            m_CurrentMatchingArchetype = match;
            IndexInComponentGroup = -1;
            m_CurrentChunk = null;
            m_CurrentArchetypeIndex = int.MaxValue; // This will trigger UpdateCacheResolvedIndex to update the cache on first access
            m_CurrentChunkIndex = 0;
            m_GlobalSystemVersion = globalSystemVersion;
            m_Filter = filter;
        }

        public object GetManagedObject(ArchetypeManager typeMan, int typeIndexInArchetype, int cachedBeginIndex,
            int index)
        {
            return typeMan.GetManagedObject(m_CurrentChunk, typeIndexInArchetype, index - cachedBeginIndex);
        }

        public object GetManagedObject(ArchetypeManager typeMan, int cachedBeginIndex, int index)
        {
            return typeMan.GetManagedObject(m_CurrentChunk,
                m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[IndexInComponentGroup], index - cachedBeginIndex);
        }

        public object[] GetManagedObjectRange(ArchetypeManager typeMan, int cachedBeginIndex, int index,
            out int rangeStart, out int rangeLength)
        {
            var objs = typeMan.GetManagedObjectRange(m_CurrentChunk,
                m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[IndexInComponentGroup], out rangeStart,
                out rangeLength);
            rangeStart += index - cachedBeginIndex;
            rangeLength -= index - cachedBeginIndex;
            return objs;
        }

        internal static int CalculateNumberOfChunksWithoutFiltering(MatchingArchetypes* firstMatchingArchetype)
        {
            var chunkCount = 0;

            for (var match = firstMatchingArchetype; match != null; match = match->Next)
                chunkCount += match->Archetype->ChunkCount;

            return chunkCount;
        }

        public static int CalculateLength(MatchingArchetypes* firstMatchingArchetype, ref ComponentGroupFilter filter)
        {
            if (filter.Type == FilterType.IndexList)
                return filter.Indices.Length;

            // Update the archetype segments
            var length = 0;
            if (!filter.RequiresMatchesFilter)
                for (var match = firstMatchingArchetype; match != null; match = match->Next)
                    length += match->Archetype->EntityCount;
            else
                for (var match = firstMatchingArchetype; match != null; match = match->Next)
                {
                    if (match->Archetype->EntityCount <= 0)
                        continue;

                    var archeType = match->Archetype;
                    for (var c = (Chunk*) archeType->ChunkList.Begin;
                        c != archeType->ChunkList.End;
                        c = (Chunk*) c->ChunkListNode.Next)
                    {
                        if (!c->MatchesFilter(match, ref filter))
                            continue;

                        Assert.IsTrue(c->Count > 0);

                        length += c->Count;
                    }
                }

            return length;
        }

        public static void CalculateInitialChunkIterators(MatchingArchetypes* firstMatchingArchetype,
            int indexInComponentGroup, NativeArray<int> sharedComponentIndex,
            NativeArray<IntPtr> outFirstArchetype, NativeArray<IntPtr> outFirstChunk, NativeArray<int> outLength)
        {
            var lookup = new NativeHashMap<int, int>(sharedComponentIndex.Length, Allocator.Temp);
            for (var f = 0; f < sharedComponentIndex.Length; ++f) lookup.TryAdd(sharedComponentIndex[f], f);
            for (var match = firstMatchingArchetype; match != null; match = match->Next)
            {
                if (match->Archetype->EntityCount <= 0)
                    continue;

                var archeType = match->Archetype;
                for (var c = (Chunk*) archeType->ChunkList.Begin;
                    c != archeType->ChunkList.End;
                    c = (Chunk*) c->ChunkListNode.Next)
                {
                    if (c->Count <= 0)
                        continue;

                    var chunkSharedComponentIndex = c->GetSharedComponentIndex(match, indexInComponentGroup);
                    int filterIndex;
                    if (!lookup.TryGetValue(chunkSharedComponentIndex, out filterIndex))
                        continue;


                    outLength[filterIndex] = outLength[filterIndex] + c->Count;
                    if (outFirstChunk[filterIndex] != IntPtr.Zero)
                        continue;
                    outFirstArchetype[filterIndex] = (IntPtr) match;
                    outFirstChunk[filterIndex] = (IntPtr) c;
                }
            }

            lookup.Dispose();
        }

        private void MoveToNextMatchingChunk()
        {
            var m = m_CurrentMatchingArchetype;
            var c = m_CurrentChunk;
            var e = (Chunk*) m->Archetype->ChunkList.End;

            do
            {
                c = (Chunk*) c->ChunkListNode.Next;
                while (c == e)
                {
                    m_CurrentArchetypeIndex += m_CurrentChunkIndex;
                    m_CurrentChunkIndex = 0;
                    m = m->Next;
                    if (m == null)
                    {
                        m_CurrentMatchingArchetype = null;
                        m_CurrentChunk = null;
                        return;
                    }

                    c = (Chunk*) m->Archetype->ChunkList.Begin;
                    e = (Chunk*) m->Archetype->ChunkList.End;
                }
            } while (!(c->MatchesFilter(m, ref m_Filter) && c->Capacity > 0));

            m_CurrentMatchingArchetype = m;
            m_CurrentChunk = c;
        }

        public void UpdateCacheResolvedIndex(int index, out ComponentChunkCache cache, bool isWriting)
        {
            Assert.IsTrue(-1 != IndexInComponentGroup);

            if (!m_Filter.RequiresMatchesFilter)
            {
                if (index < m_CurrentArchetypeIndex)
                {
                    m_CurrentMatchingArchetype = m_FirstMatchingArchetype;
                    m_CurrentArchetypeIndex = 0;
                    m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                    // m_CurrentChunk might point to an invalid chunk if the first matching archetype has no chunks
                    // the while loop below will move to the first archetype that has any entities
                    m_CurrentChunkIndex = 0;
                }

                while (index >= m_CurrentArchetypeIndex + m_CurrentMatchingArchetype->Archetype->EntityCount)
                {
                    m_CurrentArchetypeIndex += m_CurrentMatchingArchetype->Archetype->EntityCount;
                    m_CurrentMatchingArchetype = m_CurrentMatchingArchetype->Next;
                    m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                    m_CurrentChunkIndex = 0;
                }

                index -= m_CurrentArchetypeIndex;
                if (index < m_CurrentChunkIndex)
                {
                    m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                    m_CurrentChunkIndex = 0;
                }

                while (index >= m_CurrentChunkIndex + m_CurrentChunk->Count)
                {
                    m_CurrentChunkIndex += m_CurrentChunk->Count;
                    m_CurrentChunk = (Chunk*) m_CurrentChunk->ChunkListNode.Next;
                }
            }
            else
            {
                if (index < m_CurrentArchetypeIndex + m_CurrentChunkIndex)
                {
                    if (index < m_CurrentArchetypeIndex)
                    {
                        m_CurrentMatchingArchetype = m_FirstMatchingArchetype;
                        m_CurrentArchetypeIndex = 0;
                    }

                    m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.End;
                    // m_CurrentChunk now points to an invalid chunk but since the chunk list is circular
                    // it effectively points to the chunk before the first
                    // MoveToNextMatchingChunk will move it to a valid chunk if any exists
                    m_CurrentChunkIndex = 0;
                    MoveToNextMatchingChunk();
                }

                while (index >= m_CurrentArchetypeIndex + m_CurrentChunkIndex + m_CurrentChunk->Count)
                {
                    m_CurrentChunkIndex += m_CurrentChunk->Count;
                    MoveToNextMatchingChunk();
                }
            }

            var archetype = m_CurrentMatchingArchetype->Archetype;
            var typeIndexInArchetype = m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[IndexInComponentGroup];

            cache.CachedBeginIndex = m_CurrentChunkIndex + m_CurrentArchetypeIndex;
            cache.CachedEndIndex = cache.CachedBeginIndex + m_CurrentChunk->Count;
            cache.CachedSizeOf = archetype->SizeOfs[typeIndexInArchetype];
            cache.CachedPtr = m_CurrentChunk->Buffer + archetype->Offsets[typeIndexInArchetype] -
                              cache.CachedBeginIndex * cache.CachedSizeOf;

            if (isWriting)
                m_CurrentChunk->ChangeVersion[typeIndexInArchetype] = m_GlobalSystemVersion;
        }

        public void UpdateCache(int index, out ComponentChunkCache cache, bool isWriting)
        {
            if (m_Filter.Type == FilterType.IndexList)
            {
                var remappedIndex = m_Filter.Indices.Indices[index];

                UpdateCacheResolvedIndex(remappedIndex, out cache, isWriting);

                // Find consecutive range of indices
                var maxCount = Math.Min(cache.CachedEndIndex - remappedIndex, m_Filter.Indices.Length - index);
                var i = 1;
                while (i < maxCount)
                {
                    if (m_Filter.Indices.Indices[index + i] != remappedIndex + i)
                        break;

                    i++;
                }

                cache.CachedBeginIndex = index;
                cache.CachedEndIndex = index + i;
                cache.CachedPtr = (byte*) cache.CachedPtr + (remappedIndex - index) * cache.CachedSizeOf;
            }
            else
            {
                UpdateCacheResolvedIndex(index, out cache, isWriting);
            }
        }

        public void MoveToChunkByIndex(int index)
        {
            if (index < m_CurrentArchetypeIndex || m_CurrentChunk == null)
            {
                m_CurrentMatchingArchetype = m_FirstMatchingArchetype;
                m_CurrentArchetypeIndex = 0;
                m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                m_CurrentChunkIndex = 0;
            }

            while (index >= m_CurrentArchetypeIndex + m_CurrentMatchingArchetype->Archetype->ChunkCount)
            {
                m_CurrentArchetypeIndex += m_CurrentMatchingArchetype->Archetype->ChunkCount;
                m_CurrentMatchingArchetype = m_CurrentMatchingArchetype->Next;
                m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                m_CurrentChunkIndex = 0;
            }

            index -= m_CurrentArchetypeIndex;
            if (index < m_CurrentChunkIndex)
            {
                m_CurrentChunk = (Chunk*) m_CurrentMatchingArchetype->Archetype->ChunkList.Begin;
                m_CurrentChunkIndex = 0;
            }

            while (index >= m_CurrentChunkIndex + 1)
            {
                m_CurrentChunkIndex += 1;
                m_CurrentChunk = (Chunk*) m_CurrentChunk->ChunkListNode.Next;
            }
        }

        public bool IsCurrentChunkChanged()
        {
            var typeIndexInArchetype = m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[IndexInComponentGroup];
            return ChangeVersionUtility.DidChange(m_CurrentChunk->ChangeVersion[typeIndexInArchetype],
                m_Filter.RequiredChangeVersion);
        }

        public void UpdateCacheToCurrentChunk(out ComponentChunkCache cache, bool isWriting)
        {
            var archetype = m_CurrentMatchingArchetype->Archetype;
            var typeIndexInArchetype = m_CurrentMatchingArchetype->TypeIndexInArchetypeArray[IndexInComponentGroup];

            cache.CachedBeginIndex = 0;
            cache.CachedEndIndex = m_CurrentChunk->Count;
            cache.CachedSizeOf = archetype->SizeOfs[typeIndexInArchetype];
            cache.CachedPtr = m_CurrentChunk->Buffer + archetype->Offsets[typeIndexInArchetype];

            if (isWriting)
                m_CurrentChunk->ChangeVersion[typeIndexInArchetype] = m_GlobalSystemVersion;
        }

        public void GetCacheForType(int componentType, bool isWriting, out ComponentChunkCache cache,
            out int indexInArchetype)
        {
            var archetype = m_CurrentMatchingArchetype->Archetype;

            indexInArchetype = ChunkDataUtility.GetIndexInTypeArray(archetype, componentType);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (indexInArchetype == -1)
                throw new ArgumentException("componentType does not exist in the iterated archetype");
#endif

            cache.CachedBeginIndex = m_CurrentChunkIndex + m_CurrentArchetypeIndex;
            cache.CachedEndIndex = cache.CachedBeginIndex + m_CurrentChunk->Count;
            cache.CachedSizeOf = archetype->SizeOfs[indexInArchetype];
            cache.CachedPtr = m_CurrentChunk->Buffer + archetype->Offsets[indexInArchetype] -
                              cache.CachedBeginIndex * cache.CachedSizeOf;

            if (isWriting)
                m_CurrentChunk->ChangeVersion[indexInArchetype] = m_GlobalSystemVersion;
        }
    }
}
