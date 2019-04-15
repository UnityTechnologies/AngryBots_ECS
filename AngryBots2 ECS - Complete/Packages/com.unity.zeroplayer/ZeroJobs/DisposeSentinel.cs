using System.Runtime.InteropServices;
using UnityEngine;

namespace Unity.Collections
{
    public enum NativeLeakDetectionMode
    {
        Enabled = 0,
        Disabled = 1,
        EnabledWithStackTrace = 3,
    }

    public static class NativeLeakDetection
    {
        // For performance reasons no assignment operator (static initializer cost in il2cpp)
        // and flipped enabled / disabled enum value
        static int s_NativeLeakDetectionMode;

        public static NativeLeakDetectionMode Mode {
            get {
                return (NativeLeakDetectionMode)s_NativeLeakDetectionMode;
            }
            set {
                s_NativeLeakDetectionMode = (int)value;
            }
        }
    }
}


#if ENABLE_UNITY_COLLECTIONS_CHECKS
namespace Unity.Collections.LowLevel.Unsafe
{
    [StructLayout(LayoutKind.Sequential)]
    public sealed class DisposeSentinel
    {
        int        m_IsCreated;
        string     m_Stack;

        private DisposeSentinel()
        {
        }

        public static void Dispose(ref AtomicSafetyHandle safety, ref DisposeSentinel sentinel)
        {
            AtomicSafetyHandle.CheckDeallocateAndThrow(safety);
            // If the safety handle is for a temp allocation, create a new safety handle for this instance which can be marked as invalid
            // Setting it to new AtomicSafetyHandle is not enough since the handle needs a valid node pointer in order to give the correct errors
            if (AtomicSafetyHandle.IsTempMemoryHandle(safety))
                safety = AtomicSafetyHandle.Create();
            AtomicSafetyHandle.Release(safety);
            Clear(ref sentinel);
        }

        private static string CaptureStack()
        {
            if (NativeLeakDetection.Mode == NativeLeakDetectionMode.EnabledWithStackTrace)
                return System.Environment.StackTrace;
            return null;
        }

        public static void Create(out AtomicSafetyHandle safety, out DisposeSentinel sentinel, int callSiteStackDepth, Allocator allocator)
        {
            safety = (allocator == Allocator.Temp) ? AtomicSafetyHandle.GetTempMemoryHandle() : AtomicSafetyHandle.Create();

            if (NativeLeakDetection.Mode == NativeLeakDetectionMode.Enabled && allocator != Allocator.Temp)
            {
                sentinel = new DisposeSentinel
                {
                    m_IsCreated = 1,
                    m_Stack = CaptureStack()
                };
            }
            else
            {
                sentinel = null;
            }
        }

        ~DisposeSentinel()
        {
            if (m_IsCreated != 0)
            {
                // Error throws, use Log first so the output gets written.
                Debug.Log("A Native Collection has not been disposed, resulting in a memory leak. Trace:");
                Debug.LogError(m_Stack);
            }
        }

        public static void Clear(ref DisposeSentinel sentinel)
        {
            if (sentinel != null)
            {
                sentinel.m_IsCreated = 0;
                sentinel = null;
            }
        }
    }
}
#endif // ENABLE_UNITY_COLLECTIONS_CHECKS
