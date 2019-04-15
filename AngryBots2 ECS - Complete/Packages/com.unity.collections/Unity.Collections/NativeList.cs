using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using System.Diagnostics;
using Unity.Burst;

namespace Unity.Collections
{
    /// <summary>
    /// What is this : struct that contains the data for a native list, that gets allocated using native memory allocation.
    /// Motivation(s): Need a single container struct to hold a native lists collection data.
    /// </summary>
    unsafe struct NativeListData
    {
        public void*                            buffer;
        public int								length;
        public int								capacity;
    }

    [StructLayout (LayoutKind.Sequential)]
	[NativeContainer]
	[DebuggerDisplay("Length = {Length}")]
	[DebuggerTypeProxy(typeof(NativeListDebugView < >))]
	public unsafe struct NativeList<T> : IDisposable
        where T : struct
	{

#if ENABLE_UNITY_COLLECTIONS_CHECKS
	    internal AtomicSafetyHandle m_Safety;
        [NativeSetClassTypeToNullOnSchedule]
        DisposeSentinel m_DisposeSentinel;
#endif
        [NativeDisableUnsafePtrRestriction]
        internal NativeListData* m_ListData;
        private Allocator m_Allocator;


        public unsafe NativeList(Allocator i_label) : this (1, i_label, 2) { }
	    public unsafe NativeList(int capacity, Allocator i_label) : this (capacity, i_label, 2) { }

	    unsafe NativeList(int capacity, Allocator i_label, int stackDepth)
        {
            //@TODO: Find out why this is needed?
            capacity = Math.Max(1, capacity);

            var totalSize = UnsafeUtility.SizeOf<T>() * (long)capacity;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            // Native allocation is only valid for Temp, Job and Persistent.
            if (i_label <= Allocator.None)
                throw new ArgumentException("Allocator must be Temp, TempJob or Persistent", nameof(i_label));
            if (capacity < 0)
                throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be >= 0");

            CollectionHelper.CheckIsUnmanaged<T>();

            // Make sure we cannot allocate more than int.MaxValue (2,147,483,647 bytes)
            // because the underlying UnsafeUtility.Malloc is expecting a int.
            // TODO: change UnsafeUtility.Malloc to accept a UIntPtr length instead to match C++ API
            if (totalSize > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(capacity), $"Capacity * sizeof(T) cannot exceed {int.MaxValue} bytes");
#endif
            m_Allocator = i_label;
            m_ListData = (NativeListData*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<NativeListData>(), UnsafeUtility.AlignOf<NativeListData>(), m_Allocator);

            m_ListData->buffer = UnsafeUtility.Malloc (totalSize, UnsafeUtility.AlignOf<T>(), m_Allocator);

            m_ListData->length = 0;
            m_ListData->capacity = capacity;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Create(out m_Safety, out m_DisposeSentinel, stackDepth, m_Allocator);
#endif
	    }

	    public T this [int index]
		{
            get
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
                if ((uint)index >= (uint)m_ListData->length)
                    throw new IndexOutOfRangeException($"Index {index} is out of range in NativeList of '{m_ListData->length}' Length.");
#endif
                return UnsafeUtility.ReadArrayElement<T>(m_ListData->buffer, index);
            }
	        set
	        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	            AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
                if ((uint)index >= (uint)m_ListData->length)
                    throw new IndexOutOfRangeException($"Index {index} is out of range in NativeList of '{m_ListData->length}' Length.");
#endif
                UnsafeUtility.WriteArrayElement(m_ListData->buffer, index, value);
	        }
		}

	    public int Length
	    {
	        get
	        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return m_ListData->length;
	        }
	    }

	    public int Capacity
	    {
	        get
	        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	            AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
                return m_ListData->capacity;
	        }

	        set
	        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
                if (value < m_ListData->length)
                    throw new ArgumentException("Capacity must be larger than the length of the NativeList.");
#endif

                if (m_ListData->capacity == value)
                    return;

                void* newData = UnsafeUtility.Malloc (value * UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), m_Allocator);
                UnsafeUtility.MemCpy (newData, m_ListData->buffer, m_ListData->length * UnsafeUtility.SizeOf<T>());
                UnsafeUtility.Free (m_ListData->buffer, m_Allocator);
                m_ListData->buffer = newData;
                m_ListData->capacity = value;
	        }
	    }

		public void Add(T element)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		    AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            if (m_ListData->length >= m_ListData->capacity)
                Capacity = m_ListData->length + m_ListData->capacity * 2;

            this[m_ListData->length++] = element;
		}

        public void AddRange(NativeArray<T> elements)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            AddRange(elements.GetUnsafeReadOnlyPtr(), elements.Length);
        }

        public unsafe void AddRange(void* elements, int count)
        {
            if (m_ListData->length + count > m_ListData->capacity)
                Capacity = (m_ListData->length + count) * 2;

            var sizeOf = UnsafeUtility.SizeOf<T> ();
            UnsafeUtility.MemCpy((byte*)m_ListData->buffer + m_ListData->length * sizeOf, elements, sizeOf * count);

            m_ListData->length += count;
        }

		public void RemoveAtSwapBack(int index)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		    AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);

            if( index < 0 || index >= Length )
                throw new ArgumentOutOfRangeException(index.ToString());
#endif
            var newLength = m_ListData->length - 1;
            this[index] = this[newLength];
            m_ListData->length = newLength;
		}

		public bool IsCreated => m_ListData != null;

	    public void Dispose()
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            DisposeSentinel.Dispose(ref m_Safety, ref m_DisposeSentinel);
#endif
            if (m_ListData != null)
            {
                UnsafeUtility.Free(m_ListData->buffer, m_Allocator);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                m_ListData->buffer = (void*)0xDEADF00D;
#endif
                UnsafeUtility.Free(m_ListData, m_Allocator);
                m_ListData = null;
            }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            else
                throw new Exception("NativeList has yet to be allocated or has been dealocated!");
#endif
		}

		public void Clear()
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		    AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            ResizeUninitialized(0);
		}

	    public static implicit operator NativeArray<T> (NativeList<T> nativeList)
	    {
	        return nativeList.AsArray();
	    }

	    public NativeArray<T> AsArray()
	    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	        AtomicSafetyHandle.CheckGetSecondaryDataPointerAndThrow(m_Safety);
	        var arraySafety = m_Safety;
	        AtomicSafetyHandle.UseSecondaryVersion(ref arraySafety);
#endif

	        var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T> (m_ListData->buffer, m_ListData->length, Collections.Allocator.Invalid);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
	        NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, arraySafety);
#endif
	        return array;
	    }


	    [Obsolete("Please use AsDeferredJobArray")]
	    public NativeArray<T> ToDeferredJobArray()
	    {
	        return AsDeferredJobArray();
	    }

	    public unsafe NativeArray<T> AsDeferredJobArray()
	    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
	        AtomicSafetyHandle.CheckExistsAndThrow(m_Safety);
#endif

	        byte* buffer = (byte*)m_ListData;
	        // We use the first bit of the pointer to infer that the array is in list mode
	        // Thus the job scheduling code will need to patch it.
	        buffer += 1;
	        var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T> (buffer, 0, Allocator.Invalid);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
	        NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, m_Safety);
#endif

	        return array;
	    }


		public T[] ToArray()
		{
		    NativeArray<T> nativeArray = this;
		    return nativeArray.ToArray();
		}

		public NativeArray<T> ToArray(Allocator allocator)
		{
		    NativeArray<T> result = new NativeArray<T>(Length, allocator, NativeArrayOptions.UninitializedMemory);
		    result.CopyFrom(this);
		    return result;
		}

		public void CopyFrom(T[] array)
		{
		    //@TODO: Thats not right... This doesn't perform a resize
		    Capacity = array.Length;
		    NativeArray<T> nativeArray = this;
		    nativeArray.CopyFrom(array);
		}

		public void ResizeUninitialized(int length)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		    AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
#endif
            Capacity = Math.Max(length, Capacity);
            m_ListData->length = length;
		}
	}


    sealed class NativeListDebugView<T> where T : struct
    {
        NativeList<T> m_Array;

        public NativeListDebugView(NativeList<T> array)
        {
            m_Array = array;
        }

        public T[] Items => m_Array.ToArray();
    }
}
namespace Unity.Collections.LowLevel.Unsafe
{
    public static class NativeListUnsafeUtility
    {
        public static unsafe void* GetUnsafePtr<T>(this NativeList<T> nativeList) where T : struct
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.CheckWriteAndThrow(nativeList.m_Safety);
#endif
            var data = nativeList.m_ListData;
            return data->buffer;
        }

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        public static AtomicSafetyHandle GetAtomicSafetyHandle<T>(ref NativeList<T> nativeList) where T : struct
        {
            return nativeList.m_Safety;
        }
#endif

        public static unsafe void* GetInternalListDataPtrUnchecked<T>(ref NativeList<T> nativeList) where T : struct
        {
            return nativeList.m_ListData;
        }
    }
}
