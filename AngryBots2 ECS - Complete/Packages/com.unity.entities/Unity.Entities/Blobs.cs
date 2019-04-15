using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Assertions;


namespace Unity.Entities
{
    unsafe public struct BlobAllocator : IDisposable
    {
        byte* m_RootPtr;
        byte* m_Ptr;

        long m_Size;

        //@TODO: handle alignment correctly in the allocator
        public BlobAllocator(int sizeHint)
        {
            //@TODO: Use virtual alloc to make it unnecessary to know the size ahead of time...
            // Should only need 256 MB on large mesh etc
#if UNITY_IPHONE || UNITY_ANDROID || UNITY_SWITCH
        int size = 1024 * 1024 * 16;
#else
            int size = 1024 * 1024 * 256;
#endif

            m_RootPtr = m_Ptr = (byte*) UnsafeUtility.Malloc(size, 16, Allocator.Persistent);
            m_Size = size;
        }

        public void Dispose()
        {
            UnsafeUtility.Free(m_RootPtr, Allocator.Persistent);
        }

        public ref T ConstructRoot<T>() where T : struct
        {
            byte* returnPtr = m_Ptr;
            m_Ptr += UnsafeUtility.SizeOf<T>();
            return ref UnsafeUtilityEx.AsRef<T>(returnPtr);
        }

        int Allocate(long size, void* ptrAddr)
        {
            long offset = (byte*) ptrAddr - m_RootPtr;
            if (m_Ptr - m_RootPtr > m_Size)
                throw new System.ArgumentException("BlobAllocator.preallocated size not large enough");

            if (offset < 0 || offset + size > m_Size)
                throw new System.ArgumentException("Ptr must be part of root compound");

            byte* returnPtr = m_Ptr;
            m_Ptr += size;

            long relativeOffset = returnPtr - (byte*) ptrAddr;
            if (relativeOffset > int.MaxValue || relativeOffset < int.MinValue)
                throw new System.ArgumentException("BlobPtr uses 32 bit offsets, and this offset exceeds it.");

            return (int) relativeOffset;
        }

        public void Allocate<T>(int length, ref BlobArray<T> ptr) where T : struct
        {
            ptr.m_OffsetPtr = Allocate(UnsafeUtility.SizeOf<T>() * length, UnsafeUtility.AddressOf(ref ptr));
            ptr.m_Length = length;
        }

        public void Allocate<T>(ref BlobPtr<T> ptr) where T : struct
        {
            ptr.m_OffsetPtr = Allocate(UnsafeUtility.SizeOf<T>(), UnsafeUtility.AddressOf(ref ptr));
        }

        public BlobAssetReference<T> CreateBlobAssetReference<T>(Allocator allocator) where T : struct
        {
            Assert.AreEqual(16, sizeof(BlobAssetHeader));

            long dataSize = (m_Ptr - m_RootPtr);
            Assertions.Assert.IsTrue(dataSize <= 0x7FFFFFFF);

            byte* buffer = (byte*) UnsafeUtility.Malloc(sizeof(BlobAssetHeader) + dataSize, 16, allocator);
            UnsafeUtility.MemCpy(buffer + sizeof(BlobAssetHeader), m_RootPtr, dataSize);

            BlobAssetHeader* header = (BlobAssetHeader*) buffer;
            *header = new BlobAssetHeader();
            header->Length = (int)dataSize;
            header->Allocator = allocator;

            BlobAssetReference<T> blobAssetReference;
            header->ValidationPtr = blobAssetReference.m_data.m_Ptr = buffer + sizeof(BlobAssetHeader);

            return blobAssetReference;
        }

        public long DataSize
        {
            get { return (m_Ptr - m_RootPtr); }
        }
    }

    public unsafe struct BlobAssetOwner : ISharedComponentData, IDisposable
    {
        public BlobAssetOwner(byte* data, int totalSize)
        {
            this.data = data;
            this.totalSize = totalSize;
        }

        private byte* data;
        private int totalSize;

        public void Dispose()
        {
            var end = data + totalSize;
            var header = (BlobAssetHeader*)data;
            while (header < end)
            {
                header->Invalidate();
                header = (BlobAssetHeader*)(((byte*) (header+1)) + header->Length);
            }

            UnsafeUtility.Free(data, Allocator.Persistent);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    unsafe struct BlobAssetHeader
    {
        [FieldOffset(0)] public void* ValidationPtr;
        [FieldOffset(8)] public int Length;
        [FieldOffset(12)] public Allocator Allocator;

        public void Invalidate()
        {
            ValidationPtr = (void*)0xdddddddddddddddd;

        }
    }

    internal unsafe struct BlobAssetReferenceData
    {
        [NativeDisableUnsafePtrRestriction]
        public byte* m_Ptr;

        internal BlobAssetHeader* Header => ((BlobAssetHeader*) m_Ptr) - 1;

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void Validate()
        {
            if(m_Ptr != null)
                if(Header->ValidationPtr != m_Ptr)
                    throw new InvalidOperationException("The BlobAssetReference is not valid. Likely it has already been unloaded or released");
        }

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        public void ValidateNotNull()
        {
            if(m_Ptr == null)
                throw new InvalidOperationException("The BlobAssetReference is null.");
            Validate();
        }
    }

    public unsafe struct BlobAssetReference<T> : IEquatable<BlobAssetReference<T>> where T : struct
    {
        internal BlobAssetReferenceData m_data;

        public void* GetUnsafePtr()
        {
            m_data.Validate();
            return m_data.m_Ptr;
        }

        public void Release()
        {
            m_data.ValidateNotNull();
            var header = m_data.Header;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if(header->Allocator == Allocator.None)
                throw new InvalidOperationException("It's not possible to release a blob asset reference that was deserialized. It will be automatically released when the scene is unloaded ");
            m_data.Header->Invalidate();
#endif

            UnsafeUtility.Free(header, header->Allocator);
            m_data.m_Ptr = null;
        }

        public ref T Value
        {
            get
            {
                m_data.ValidateNotNull();
                return ref UnsafeUtilityEx.AsRef<T>(m_data.m_Ptr);
            }
        }

        public static BlobAssetReference<T> Create(void* ptr, int length)
        {
            byte* buffer =
                (byte*) UnsafeUtility.Malloc(sizeof(BlobAssetHeader) + length, 16, Allocator.Persistent);
            UnsafeUtility.MemCpy(buffer + sizeof(BlobAssetHeader), ptr, length);

            BlobAssetHeader* header = (BlobAssetHeader*) buffer;
            *header = new BlobAssetHeader();

            header->Length = length;
            header->Allocator = Allocator.Persistent;

            BlobAssetReference<T> blobAssetReference;
            header->ValidationPtr = blobAssetReference.m_data.m_Ptr = buffer + sizeof(BlobAssetHeader);
            return blobAssetReference;
        }

        public static BlobAssetReference<T> Create(byte[] data)
        {
            fixed (byte* ptr = &data[0])
            {
                return Create(ptr, data.Length);
            }
        }

        public static BlobAssetReference<T> Create(T value)
        {
            return Create(UnsafeUtility.AddressOf(ref value), UnsafeUtility.SizeOf<T>());
        }

        public static BlobAssetReference<T> Null => new BlobAssetReference<T>();

        public static bool operator ==(BlobAssetReference<T> lhs, BlobAssetReference<T> rhs)
        {
            return lhs.m_data.m_Ptr == rhs.m_data.m_Ptr;
        }

        public static bool operator !=(BlobAssetReference<T> lhs, BlobAssetReference<T> rhs)
        {
            return lhs.m_data.m_Ptr != rhs.m_data.m_Ptr;
        }

        public bool Equals(BlobAssetReference<T> other)
        {
            return m_data.Equals(other.m_data);
        }

        public override bool Equals(object obj)
        {
            return this == (BlobAssetReference<T>)obj;
        }

        public override int GetHashCode()
        {
            return m_data.GetHashCode();
        }
    }

    unsafe public struct BlobPtr<T> where T : struct
    {
        internal int m_OffsetPtr;

        public ref T Value
        {
            get
            {
                fixed (int* thisPtr = &m_OffsetPtr)
                {
                    return ref UnsafeUtilityEx.AsRef<T>((byte*) thisPtr + m_OffsetPtr);
                }
            }
        }

        public void* GetUnsafePtr()
        {
            fixed (int* thisPtr = &m_OffsetPtr)
            {
                return (byte*) thisPtr + m_OffsetPtr;
            }
        }
    }

    unsafe public struct BlobArray<T> where T : struct
    {
        internal int m_OffsetPtr;
        internal int m_Length;

        public int Length
        {
            get { return m_Length; }
        }

        public void* GetUnsafePtr()
        {
            fixed (int* thisPtr = &m_OffsetPtr)
            {
                return (byte*) thisPtr + m_OffsetPtr;
            }
        }

        public ref T this[int index]
        {
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                if ((uint) index >= (uint) m_Length)
                    throw new System.IndexOutOfRangeException(string.Format("Index {0} is out of range Length {1}",
                        index, m_Length));
#endif

                fixed (int* thisPtr = &m_OffsetPtr)
                {
                    return ref UnsafeUtilityEx.ArrayElementAsRef<T>((byte*) thisPtr + m_OffsetPtr, index);
                }
            }
        }
    }
}
