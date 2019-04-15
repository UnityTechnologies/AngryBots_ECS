using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PatchedUnsafeUtility = ForPatching.UnsafeUtility;

namespace Unity.Collections.LowLevel.Unsafe
{
    public static partial class UnsafeUtility
    {
        // Copies sizeof(T) bytes from ptr to output
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void CopyPtrToStructure<T>(void* ptr, out T output) where T : struct
        {
            PatchedUnsafeUtility.CopyPtrToStructure<T>(ptr, out output);
        }

        // Copies sizeof(T) bytes from output to ptr
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void CopyStructureToPtr<T>(ref T input, void* ptr) where T : struct
        {
            PatchedUnsafeUtility.CopyStructureToPtr<T>(ref input, ptr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static T ReadArrayElement<T>(void* source, int index)
        {
            return PatchedUnsafeUtility.ReadArrayElement<T>(source, index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static T ReadArrayElementWithStride<T>(void* source, int index, int stride)
        {
            return PatchedUnsafeUtility.ReadArrayElementWithStride<T>(source, index, stride);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static void WriteArrayElement<T>(void* destination, int index, T value)
        {
            PatchedUnsafeUtility.WriteArrayElement<T>(destination,index,value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static void WriteArrayElementWithStride<T>(void* destination, int index, int stride, T value)
        {
            PatchedUnsafeUtility.WriteArrayElementWithStride<T>(destination, index, stride, value);
        }

        // The address of the memory where the struct resides in memory
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static void* AddressOf<T>(ref T output) where T : struct
        {
            return PatchedUnsafeUtility.AddressOf<T>(ref output);
        }

        // The size of a struct
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizeOf<T>() where T : struct
        {
            return PatchedUnsafeUtility.SizeOf<T>();
        }

        // minimum alignment of a struct
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignOf<T>() where T : struct
        {
            return PatchedUnsafeUtility.AlignOf<T>();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static ref T AsRef<T>(void* p) where T : struct
        {
            return ref PatchedUnsafeUtility.AsRef<T>(p);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static void* PinGCObjectAndGetAddress(System.Object target, out ulong gcHandle)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (target == null)
                throw new ArgumentNullException(nameof(target));
#endif

            return PinSystemObjectAndGetAddress(target, out gcHandle);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        unsafe public static void* PinGCArrayAndGetDataAddress(System.Array target, out ulong gcHandle)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (target == null)
                throw new ArgumentNullException(nameof(target));
#endif

            return PinSystemArrayAndGetAddress(target, out gcHandle);
        }

        public static unsafe int MemoryCompare(void* left, void* right, int size)
        {
            byte* bl = (byte*) left;
            byte* br = (byte*) right;

            for (int i = 0; i < size; ++i)
            {
                if (bl[i] != br[i])
                    return br[i] - bl[i];
            }

            return 0;
        }

        [DllImport("liballocators", EntryPoint = "notimpl")]
        private static extern unsafe void* PinSystemArrayAndGetAddress(System.Object target, out ulong gcHandle);

        [DllImport("liballocators", EntryPoint = "notimpl")]
        private static extern unsafe void* PinSystemObjectAndGetAddress(System.Object target, out ulong gcHandle);

        [DllImport("liballocators", EntryPoint = "notimpl")]
        public static extern unsafe void ReleaseGCObject(ulong gcHandle);

        [DllImport("liballocators", EntryPoint = "notimpl")]
        public static extern unsafe void CopyObjectAddressToPtr(object target, void* dstPtr);
    }
}
