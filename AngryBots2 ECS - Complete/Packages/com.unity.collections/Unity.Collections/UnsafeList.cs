using System;
using System.Diagnostics;
using UnityEngine.Assertions;

namespace Unity.Collections.LowLevel.Unsafe
{
    public unsafe struct UnsafeList
    {
        public void* m_pointer;
        public int m_size;
        public int m_capacity;
        
        public void Dispose<T>(Allocator allocator = Allocator.Persistent) where T : struct
        {
            SetCapacity<T>(0, allocator);
        }
        
        public void Resize<T>(int targetSize, Allocator allocator = Allocator.Persistent) where T : struct
        {
            SetCapacity<T>(targetSize, allocator);
            m_size = targetSize;
        }

        void SetCapacity(int sizeOf, int alignOf, int targetCapacity, Allocator allocator)
        {
            if (targetCapacity > 0)
            {    
                var itemsPerCacheLine = 64 / sizeOf;
                if(targetCapacity < itemsPerCacheLine)
                    targetCapacity = itemsPerCacheLine;
                targetCapacity = CollectionHelper.CeilPow2(targetCapacity);
            }
            var newCapacity = targetCapacity; 
            if (newCapacity == m_capacity)
                return;
            void* newPointer = null;
            if (newCapacity > 0)
            {
                var bytesToMalloc = sizeOf * newCapacity;
                newPointer = UnsafeUtility.Malloc(bytesToMalloc, alignOf, allocator );
                if (m_capacity > 0)
                {
                    var itemsToCopy = newCapacity < m_capacity ? newCapacity : m_capacity;
                    var bytesToCopy = itemsToCopy * sizeOf;                        
                    UnsafeUtility.MemCpy(newPointer, m_pointer, bytesToCopy);
                }
            }
            if (m_capacity > 0)
                UnsafeUtility.Free(m_pointer, allocator);
            m_pointer = newPointer;
            m_capacity = newCapacity;
            if (m_size > m_capacity)
                m_size = m_capacity;
        }
        
        public void SetCapacity<T>(int targetCapacity, Allocator allocator = Allocator.Persistent) where T : struct
        {
            SetCapacity(UnsafeUtility.SizeOf<T>(), UnsafeUtility.AlignOf<T>(), targetCapacity, allocator);
        }
        public int IndexOf<T>(T t) where T : struct, IEquatable<T>
        {
            for(int i = m_size - 1; i >= 0; --i)
                if(UnsafeUtility.ReadArrayElement<T>(m_pointer, i).Equals(t))
                    return i;
            return -1;                        
        }
        
        public bool Contains<T>(T t) where T : struct, IEquatable<T>
        {
            return IndexOf(t) != -1;
        }
        
        public void Add<T>(T t, Allocator allocator = Allocator.Persistent) where T : struct
        {
            Resize<T>(m_size + 1, allocator);
            UnsafeUtility.WriteArrayElement(m_pointer,m_size - 1, t);            
        }
        public void AddRange<T>(void* t, int count, Allocator allocator = Allocator.Persistent) where T : struct
        {
            int previousSize = m_size; 
            Resize<T>(previousSize + count, allocator);
            int sizeOf = UnsafeUtility.SizeOf<T>();
            UnsafeUtility.MemCpy((byte*)m_pointer + previousSize * sizeOf, t, count * sizeOf);
        }

        void RemoveRangeSwapBack(int sizeOf, int begin, int end)
        {
            int itemsToRemove = end - begin;
            Assert.IsTrue(itemsToRemove > 0);
            void* d = (byte*)m_pointer + begin * sizeOf;
            void* s = (byte*)m_pointer + (m_size - itemsToRemove) * sizeOf;
            UnsafeUtility.MemCpy(d, s, itemsToRemove * sizeOf);
            m_size -= itemsToRemove;
        }

        public void RemoveRangeSwapBack<T>(int begin, int end) where T : struct
        {
            RemoveRangeSwapBack(UnsafeUtility.SizeOf<T>(), begin, end);
        }

        public void RemoveAtSwapBack<T>(int index, T t) where T : struct, IEquatable<T>
        {
            Assert.IsTrue(index >= 0 && index < m_size);
            Assert.IsTrue(UnsafeUtility.ReadArrayElement<T>(m_pointer, index).Equals(t));
            RemoveAtSwapBack<T>(index);
        }


        public void RemoveAtSwapBack<T>(int index) where T : struct
        {
            RemoveRangeSwapBack<T>(index, index + 1);
        }

        void Append(int sizeOf, UnsafeList src)
        {
            var itemsToAppend = src.m_size;
            var oldDestSize = m_size;
            var newDestSize = m_size + itemsToAppend; 
            Assert.IsTrue(m_capacity >= newDestSize);
            m_size = newDestSize;
            void* d = (byte*)m_pointer + oldDestSize * sizeOf;
            void* s = (byte*)src.m_pointer;
            UnsafeUtility.MemCpy(d, s, itemsToAppend * sizeOf);
        }
        
        public void Append<T>(UnsafeList src) where T : struct
        {
            Append(UnsafeUtility.SizeOf<T>(), src);
        }
        
    }
    
    sealed class UnsafePtrListDebugView
    {
        private UnsafePtrList m_UnsafePtrList;
        public UnsafePtrListDebugView(UnsafePtrList UnsafePtrList)
        {
            m_UnsafePtrList = UnsafePtrList;
        }
        public unsafe IntPtr[] Items
        {
            get
            {
                IntPtr[] result = new IntPtr[m_UnsafePtrList.m_size];
                for (var i = 0; i < result.Length; ++i)
                    result[i] = (IntPtr)m_UnsafePtrList.m_pointer[i];
                return result;
            }
        }
    }
    
    [DebuggerTypeProxy(typeof(UnsafePtrListDebugView))]
    public unsafe struct UnsafePtrList
    {
        public void** m_pointer;
        public int m_size;
        public int m_capacity;

        public ref UnsafeList GetUnsafeList()
        {
            return ref *(UnsafeList*)UnsafeUtility.AddressOf(ref this);
        }        
        
        public void Dispose(Allocator allocator = Allocator.Persistent)
        {
            SetCapacity(0, allocator);
        }

        public void Resize(int targetSize, Allocator allocator = Allocator.Persistent)
        {
            GetUnsafeList().Resize<IntPtr>(targetSize, allocator);
        }

        public void SetCapacity(int targetCapacity, Allocator allocator = Allocator.Persistent)
        {
            GetUnsafeList().SetCapacity<IntPtr>(targetCapacity, allocator);
        }
        public int IndexOf(void *t)
        {
            for(int i = m_size - 1; i >= 0; --i)
                if(m_pointer[i] == t)
                    return i;
            return -1;                        
        }
        
        public bool Contains(void* t)
        {
            return IndexOf(t) != -1;
        }
        
        public void Add(void* t, Allocator allocator = Allocator.Persistent)
        {
            Resize(m_size + 1, allocator);
            m_pointer[m_size - 1] = t;
        }

        public void RemoveRangeSwapBack(int begin, int end)
        {
            GetUnsafeList().RemoveRangeSwapBack<IntPtr>(begin, end);
        }

        public void RemoveAtSwapBack(int index, void* expectedValue)
        {
            Assert.IsTrue(index >= 0 && index < m_size);
            Assert.IsTrue(m_pointer[index] == expectedValue);
            RemoveAtSwapBack(index);
        }

        public void RemoveAtSwapBack(int index)
        {
            RemoveRangeSwapBack(index, index + 1);
        }

        public void Append(UnsafePtrList src)
        {
            GetUnsafeList().Append<IntPtr>(src.GetUnsafeList());
        }
    }
    
}
