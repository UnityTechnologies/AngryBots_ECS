using System;
using System.Runtime.InteropServices;

namespace Unity.Collections.LowLevel.Unsafe
{
    public partial class UnsafeUtility
    {
        [DllImport("liballocators", EntryPoint = "unsafeutility_malloc")]
        public static extern unsafe void* Malloc(long totalSize, int alignOf, Allocator allocator);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memcpy")]
        public static extern unsafe void MemCpy(void* b, void* b1, long i);

        [DllImport("liballocators", EntryPoint = "unsafeutility_free")]
        public static extern unsafe void Free(void* mBuffer, Allocator mAllocatorLabel);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memclear")]
        public static extern unsafe void MemClear(void* mBuffer, long lize);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memcpystride")]
        public static extern unsafe void MemCpyStride(void* destination, int destinationStride, void* source, int sourceStride, int elementSize, long count);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memcmp")]
        public static extern unsafe int MemCmp(void* ptr1, void* ptr2, long size);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memcpyreplicate")]
        public static extern unsafe void MemCpyReplicate(void* destination, void* source, int size, int count);

        [DllImport("liballocators", EntryPoint = "unsafeutility_memmove")]
        public static extern unsafe void MemMove(void* destination, void* source, long size);

        [DllImport("liballocators", EntryPoint = "unsafeutility_freetemp")]
        public static extern unsafe void FreeTempMemory();
    }
}
