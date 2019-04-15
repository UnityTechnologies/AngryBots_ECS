//#define DEBUG_DUMMY
#if ENABLE_UNITY_COLLECTIONS_CHECKS
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Unity.Jobs;

namespace Unity.Collections.LowLevel.Unsafe
{
    public enum EnforceJobResult
    {
        AllJobsAlreadySynced = 0,
        DidSyncRunningJobs = 1,
        HandleWasAlreadyDeallocated = 2,
    }

    // AtomicSafetyHandle is used by the C# job system to provide validation and full safety
    // for read / write permissions to access the buffers represented by each handle.
    // Each AtomicSafetyHandle represents a single container.
    // Since all Native containers are written using structs,
    // it also provides checks against destroying a container
    // and accessing from another struct pointing to the same buffer.

    internal struct AtomicSafetyHandleInternal
    {
        internal int version1;
        internal int version2;
        internal int permissions;
    }

    internal struct AtomicSafetyHandleVersionMask
    {
        internal static readonly int Read = 1 << 0;
        internal static readonly int Write = 1 << 1;
        internal static readonly int Dispose = 1 << 2;
        internal static readonly int Allow2ndVersion = 1 << 3;
        internal static readonly int ReadAndWrite = Read | Write;
        internal static readonly int ReadWriteAndDispose = Read | Write | Dispose;

        internal static readonly int WriteInv = ~Write;
        internal static readonly int ReadInv = ~Read;
        internal static readonly int ReadAndWriteInv = ~ReadAndWrite;
        internal static readonly int ReadWriteAndDisposeInv = ~ReadWriteAndDispose;
        internal static readonly int ClearAllMasks = ~(Allow2ndVersion | ReadWriteAndDispose);
    }

    // Stores the handles.
    // Since handles can be accessed while 'released' that chunk of memory can only be reused
    // by another handle (and use the version(s) to verify its validity)
    internal unsafe class AtomicSafetyHandlePool
    {
        internal const int kHandlesPerChunk = 500;
        internal unsafe struct AtomicSafetyHandleNode
        {
            internal AtomicSafetyHandleInternal handle;
            internal AtomicSafetyHandleNode* nextAvail;
        }

        private unsafe struct ChunkHeader
        {
            internal ChunkHeader* m_nextChunk;
            internal AtomicSafetyHandleNode* m_chunk;
            internal AtomicSafetyHandleNode* m_curChunk;

            static internal ChunkHeader* GetHeader(void* mem)
            {
                return (ChunkHeader*) ((ulong) mem + esz * kHandlesPerChunk);
            }
            internal ChunkHeader* GetHeader()
            {
                return GetHeader(m_chunk);
            }

        }

        private ChunkHeader* m_headChunk;
        private AtomicSafetyHandleNode* m_nextAvail;

        internal static ulong esz = (ulong) sizeof(AtomicSafetyHandleNode);

        private void addChunk()
        {

            var mem =
                (AtomicSafetyHandleNode*) Unity.Collections.LowLevel.Unsafe.UnsafeUtility.Malloc(
                    (int) esz * kHandlesPerChunk + sizeof(ChunkHeader),
                    0,
                    Unity.Collections.Allocator.Persistent);

            var h = (ChunkHeader*) ((ulong) mem + esz * kHandlesPerChunk);
            h->m_chunk = h->m_curChunk = mem;
            h->m_nextChunk = m_headChunk;
            m_headChunk = h;
        }

        internal AtomicSafetyHandlePool()
        {
            m_headChunk = null;
            m_nextAvail = null;
            addChunk();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal AtomicSafetyHandleInternal* alloc()
        {
            if (m_nextAvail != null)
            {
                AtomicSafetyHandleInternal* r = &m_nextAvail->handle;
                m_nextAvail = m_nextAvail->nextAvail;
                return r;
            }

            AtomicSafetyHandleNode* h = m_headChunk->m_curChunk;
            if ((IntPtr) h == (IntPtr) m_headChunk->GetHeader())
            {
                addChunk();
                h = m_headChunk->m_curChunk;
            }
            m_headChunk->m_curChunk =  (AtomicSafetyHandleNode*) ((ulong) m_headChunk->m_curChunk + esz);
            h->handle.version1 = h->handle.version2 = h->handle.permissions = 0;

            return &h->handle;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void release(AtomicSafetyHandleInternal* handle)
        {

            ((AtomicSafetyHandleNode*) handle)->nextAvail = m_nextAvail;
            m_nextAvail = (AtomicSafetyHandleNode*) handle;
        }
    }

    public struct AtomicSafetyHandle
    {

#pragma warning disable 649
        internal IntPtr versionNode;
        internal int version;
#pragma warning restore 649

        static AtomicSafetyHandlePool pool = new AtomicSafetyHandlePool();
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe AtomicSafetyHandleInternal* alloc()
        {
            return pool.alloc();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void release(AtomicSafetyHandleInternal* handle)
        {
            pool.release(handle);
        }

        internal static readonly int AtomicSafetyVersionIncrease = 1 << 4;
        internal static readonly int AllowReadOrWrite = 1 << 1;
        internal static readonly int SecondaryWritingEnabled = 1 << 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool IsSecondaryVersion()
        {
            return (version & AtomicSafetyHandleVersionMask.Allow2ndVersion) == AtomicSafetyHandleVersionMask.Allow2ndVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe int load()
        {
            if (versionNode == (IntPtr) 0)
                throw new InvalidOperationException("Trying to dereference a null versionNode here!");

            AtomicSafetyHandleInternal* hInternal = (AtomicSafetyHandleInternal*) versionNode;
            if (hInternal == null)
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");
            return IsSecondaryVersion()? hInternal->version2 : hInternal->version1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe AtomicSafetyHandleInternal* GetInternal()
        {
            if (!IsValid()) return null;
            //if (IsSecondaryVersion())
            //    return (AtomicSafetyHandleInternal*) (IntPtr) ((ulong) versionNode - sizeof(int));
            return (AtomicSafetyHandleInternal*) versionNode;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe AtomicSafetyHandleInternal* GetInternalWithCheck()
        {
            var hInternal = GetInternal();
            
            if (hInternal == null)
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");

            return hInternal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool IsValid()
        {
            return (versionNode != (IntPtr) 0) &&
                   (load() & AtomicSafetyHandleVersionMask.ReadWriteAndDisposeInv) ==
                   (version & AtomicSafetyHandleVersionMask.ClearAllMasks);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool ValidateIsAllowedToWriteEarlyOut()
        {
            return versionNode != (IntPtr) 0 &&
                   (CheckVersionForFlag(AtomicSafetyHandleVersionMask.Write) ||
                   CheckAgainstVersion(AtomicSafetyHandleVersionMask.ReadInv));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool ValidateIsAllowedToReadEarlyOut()
        {
            return versionNode != (IntPtr) 0 &&
                   (CheckVersionForFlag(AtomicSafetyHandleVersionMask.Read) ||
                   CheckAgainstVersion(AtomicSafetyHandleVersionMask.WriteInv));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool CheckVersionForFlag(int flag)
        {
            return (version & flag) == flag;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void BumpPrimaryVersion()
        {
            ((AtomicSafetyHandleInternal*) versionNode)->version1 += AtomicSafetyVersionIncrease;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void BumpSecondaryVersion()
        {
            ((AtomicSafetyHandleInternal*) versionNode)->version2 += AtomicSafetyVersionIncrease;
        }



#if DEBUG_DUMMY
        public override string ToString()
        {
            unsafe {
                var hInternal = GetInternal();
                return  hInternal == null? 
                              $"[{(ulong) versionNode:X} = {{ Invalid Invalid Invalid, version: {version:X} }}]"
                            : $"[{(ulong) versionNode:X} = {{ {hInternal->version1:X} {hInternal->version2:X} {hInternal->permissions:X}, version: {version:X} }}]";
            }
        }
#endif

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool CheckAgainstVersion(int flag)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"    CheckAgainstVersion({flag:X}) load:{load()} load&flag:{load()&flag} == version:{version}");
#endif
            return (load() & flag) == (version & ~AtomicSafetyHandleVersionMask.Allow2ndVersion);
        }

        // Creates a new AtomicSafetyHandle that is valid until Release is called.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AtomicSafetyHandle Create()
        {
            unsafe
            {
                AtomicSafetyHandleInternal* node = alloc();
                AtomicSafetyHandle handle = new AtomicSafetyHandle()
                {
                    versionNode = (IntPtr) node,
                };
                //node->version1 = node->version2 = node->permissions = 0;
                handle.version = node->version1;
                UnityEngine.Assertions.Assert.IsFalse(handle.IsSecondaryVersion());
                UnityEngine.Assertions.Assert.IsTrue((handle.version & AtomicSafetyHandleVersionMask.ReadWriteAndDispose) == 0);
                UnityEngine.Assertions.Assert.IsTrue((node->version1 & AtomicSafetyHandleVersionMask.ReadWriteAndDispose) == 0);
                UnityEngine.Assertions.Assert.IsTrue((node->version2 & AtomicSafetyHandleVersionMask.ReadWriteAndDispose) == 0);
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx Create() = {handle}");
#endif
                return handle;
            }
        }

        internal static unsafe AtomicSafetyHandle CreateTempMemoryHandle(bool slice) => Create();

        internal static AtomicSafetyHandle s_TempSliceHandle = CreateTempMemoryHandle(true);
        public static AtomicSafetyHandle GetTempUnsafePtrSliceHandle() => s_TempSliceHandle;

        // TODO - this should return a safety handle for the current bump allocator scope,
        // that should be marked as released when the bump allocator is reset.  It should
        // not be a constant.
        internal static AtomicSafetyHandle s_TempMemSafetyHandle = CreateTempMemoryHandle(false);
        public static AtomicSafetyHandle GetTempMemoryHandle() => s_TempMemSafetyHandle;

        public static bool IsTempMemoryHandle(AtomicSafetyHandle handle) =>
            handle.versionNode == s_TempMemSafetyHandle.versionNode;

        // Releases a previously Created AtomicSafetyHandle.
        // You must call CheckDeallocateAndThrow before calling Release to avoid double free

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Release(AtomicSafetyHandle handle)
        {
            unsafe
            {
                AtomicSafetyHandleInternal* hInternal = handle.GetInternal();
                if (hInternal != null)
                {
#if DEBUG_DUMMY
                    UnityEngine.Debug.Log($"xxx Release({handle}");
#endif
                    release(hInternal);
                    hInternal->version1 &= ~AtomicSafetyHandleVersionMask.ReadWriteAndDispose;
                    handle.BumpPrimaryVersion();
                    hInternal->version2 &= ~AtomicSafetyHandleVersionMask.ReadWriteAndDispose;
                    handle.BumpSecondaryVersion();
                    hInternal->permissions = 0;
                }
            }
        }

        // Marks the AtomicSafetyHandle so that it cannot be disposed of.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void PrepareUndisposable(ref AtomicSafetyHandle handle)
        {
            throw new System.NotImplementedException("DummyAtomicSafetyHandle.PrepareUndisposable is not used");
        }

        // Switches the AtomicSafetyHandle to the secondary version number.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void UseSecondaryVersion(ref AtomicSafetyHandle handle)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx UseSecondaryVersion({handle})");
#endif
            unsafe
            {
                handle.version = AtomicSafetyHandleVersionMask.Allow2ndVersion |
                        (handle.GetInternalWithCheck()->version2 & AtomicSafetyHandleVersionMask.ReadWriteAndDisposeInv) |
                        (handle.version & AtomicSafetyHandleVersionMask.ReadWriteAndDispose);
            }
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx     UseSecondaryVersion({handle})");
#endif
        }

        // Sets whether the secondary version is readonly (allowWriting = false) or readwrite (allowWriting= true)
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SetAllowSecondaryVersionWriting(AtomicSafetyHandle handle, bool allowWriting)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx SetAllowSecondaryVersionWriting({handle}, {allowWriting})");
#endif
            unsafe
            {
                AtomicSafetyHandleInternal* hInternal = handle.GetInternalWithCheck();

                hInternal->version2 |= AtomicSafetyHandleVersionMask.Write;
                if (allowWriting)
                    hInternal->permissions |= SecondaryWritingEnabled;
                else
                    hInternal->permissions &= ~SecondaryWritingEnabled;
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx      SetAllowSecondaryVersionWriting() = {handle}");
#endif
            }
        }


        // There is no writeonly case (to date), let's assume this is always readOrWrite
        // and skip those
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void SetAllowReadOrWriteAccess(AtomicSafetyHandle handle, bool allowReadWriteAccess)
        {
            throw new System.NotImplementedException("DummyAtomicSafetyHandle.SetAllowReadOrWriteAccess is not used");
        }

        public static bool GetAllowReadOrWriteAccess(AtomicSafetyHandle handle) => true;


        // Performs CheckWriteAndThrow and then bumps the secondary version.
        // This allows for example a NativeArray that becomes invalid if the Length of a List
        // is changed to be invalidated, while the NativeList handle itself remains valid.

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWriteAndBumpSecondaryVersion(AtomicSafetyHandle handle)
        {
            UnityEngine.Assertions.Assert.IsTrue(handle.IsValid());
            UnityEngine.Assertions.Assert.IsFalse(handle.IsSecondaryVersion());
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx CheckWriteAndBumpSecondaryVersion({handle})");
#endif
            if (!handle.ValidateIsAllowedToWriteEarlyOut())
            {
                CheckWriteAndThrowNoEarlyOut(handle);
            }
            handle.BumpSecondaryVersion();
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx      CheckWriteAndBumpSecondaryVersion() out = {handle}");
#endif
        }

        // Same as CheckReadAndThrow but the early out has already been performed in the call site for performance reasons.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckReadAndThrowNoEarlyOut(AtomicSafetyHandle handle)
        {
            unsafe {
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx CheckReadAndThrowNoEarlyOut({handle})");
#endif
            }
            if (!handle.IsValid())
            {
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx    CheckReadAndThrowNoEarlyOut() THROWS {handle}");
#endif
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");
            }
            unsafe {
                var hInternal = handle.GetInternalWithCheck();
                if ((hInternal->permissions & AllowReadOrWrite) == 0)
                    throw new System.InvalidOperationException("The NativeArray can no longer be accessed, since its owner has been invalidated. You can simply Dispose() the container and create a new one.");
                hInternal->version1 &= AtomicSafetyHandleVersionMask.ReadInv;
                hInternal->version2 &= AtomicSafetyHandleVersionMask.ReadInv;
            }
        }

        // Same as CheckWriteAndThrow but the early out has already been performed in the call site for performance reasons.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckWriteAndThrowNoEarlyOut(AtomicSafetyHandle handle)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx CheckWriteAndThrowNoEarlyOut({handle})");
#endif
            if (!handle.IsValid())
            {
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx CheckWriteAndThrowNoEarlyOut() THROWS {handle}");
#endif
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");
            }
            unsafe {
                var hInternal = handle.GetInternal();
                if (handle.IsSecondaryVersion() && ((hInternal->permissions & SecondaryWritingEnabled) == 0))
                {
#if DEBUG_DUMMY
                    UnityEngine.Debug.Log($"xxx CheckWriteAndThrowNoEarlyOut() THROWS {handle}");
#endif
                    throw new System.InvalidOperationException("The native container has been declared as [ReadOnly], but you are writing to it.");
                }
            }
        }

        // Checks if the handle can be deallocated.
        // If not (already destroyed) throws an exception.

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckDeallocateAndThrow(AtomicSafetyHandle handle)
        {
            if (!handle.IsValid())
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");
        }


        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public static void CheckGetSecondaryDataPointerAndThrow(AtomicSafetyHandle handle)
        {
            throw new System.NotImplementedException("DummyAtomicSafetyHandle.CheckGetSecondaryDataPointerAndThrow is not used");
        }

        // Checks if the handle can be read from
        // If not (already destroyed, job currently writing to the data) throws an exception.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckReadAndThrow(AtomicSafetyHandle handle)
        {
            unsafe
            {
#if DEBUG_DUMMY
                UnityEngine.Debug.Log($"xxx CheckWriteAndThrow({handle})");
#endif
                if (!handle.ValidateIsAllowedToReadEarlyOut())
                {
                    CheckReadAndThrowNoEarlyOut(handle);
                }
            }
        }

        // Checks if the handle can be written to
        // If not (already destroyed, currently reading or writing to the data) throws an exception.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CheckWriteAndThrow(AtomicSafetyHandle handle)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx CheckWriteAndThrow({handle})");
#endif
            if (!handle.ValidateIsAllowedToWriteEarlyOut())
            {
                CheckWriteAndThrowNoEarlyOut(handle);
            }
        }

        // Checks if the handle is still valid.
        // If not (already destroyed) throws an exception.
        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void CheckExistsAndThrow(AtomicSafetyHandle handle)
        {
#if DEBUG_DUMMY
            UnityEngine.Debug.Log($"xxx CheckExistsAndThrow({handle})");
#endif
            if (!handle.IsValid())
                throw new System.InvalidOperationException("The NativeArray has been deallocated, it is not allowed to access it");
        }

        // For debugging purposes in unit tests we need to sometimes just sync
        // all jobs against an handle before shutting down. Since we don't have jobs in
        // the ZeroPlayer context, it's always 'All Done!'
        public static EnforceJobResult EnforceAllBufferJobsHaveCompleted(AtomicSafetyHandle handle) =>
            EnforceJobResult.AllJobsAlreadySynced;

        public static EnforceJobResult EnforceAllBufferJobsHaveCompletedAndRelease(AtomicSafetyHandle handle) =>
            EnforceJobResult.AllJobsAlreadySynced;

        public static EnforceJobResult
            EnforceAllBufferJobsHaveCompletedAndDisableReadWrite(AtomicSafetyHandle handle) =>
                EnforceJobResult.AllJobsAlreadySynced;


        // Not supported (yet?) in the ZeroPlayer context
        public static unsafe int GetReaderArray(AtomicSafetyHandle handle, int maxCount, IntPtr output) => 0;
        public static JobHandle GetWriter(AtomicSafetyHandle handle) => new JobHandle();
        public static string GetReaderName(AtomicSafetyHandle handle, int readerIndex) => "(not implemented)";
        public static string GetWriterName(AtomicSafetyHandle handle) => "(not implemented)";
    }
}
#endif // ENABLE_UNITY_COLLECTIONS_CHECKS
