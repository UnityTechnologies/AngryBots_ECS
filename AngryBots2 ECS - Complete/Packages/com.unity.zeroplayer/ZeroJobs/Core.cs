using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Jobs.LowLevel.Unsafe;

//using Unity.Jobs.LowLevel.Unsafe;


namespace Unity.Jobs
{
    public interface IJob
    {
        void Execute();
    }

    [JobProducerType(typeof (IJobParallelForExtensions.ParallelForJobStruct<>))]
    public interface IJobParallelFor
    {
        void Execute(int index);
    }

    public struct JobHandle
    {
        public static void ScheduleBatchedJobs() {}
        public void Complete() {}
        public void CompleteAll() {}
        public static bool CheckFenceIsDependencyOrDidSyncFence(JobHandle dependency, JobHandle writer) => true;
        public static JobHandle CombineDependencies(NativeArray<JobHandle> jobHandles) => new JobHandle();
        public static JobHandle CombineDependencies(JobHandle mProducerHandle, JobHandle foo) => new JobHandle();

        public static unsafe void CompleteAll(ref JobHandle job0, ref JobHandle job1)
        {
        }
    }

    public static class IJobExtensions
    {
        public static JobHandle Schedule<T>(this T jobData, JobHandle dependsOn = default(JobHandle)) where T : struct, IJob
        {
            jobData.Execute();
            DoDeallocateOnJobCompletion(jobData);
            return new JobHandle();
        }
        public static unsafe void Run<T>(this T jobData) where T : struct, IJob
        {
            jobData.Execute();
            DoDeallocateOnJobCompletion(jobData);
        }

        static internal void DoDeallocateOnJobCompletion(object jobData)
        {
            throw new NotImplementedException("This function should have been replaced by codegen");
        }
    }

    public static class IJobParallelForExtensions
    {
        internal struct ParallelForJobStruct<T> where T : struct, IJobParallelFor
        {
            public static IntPtr jobReflectionData;

            public static IntPtr Initialize()
            {
                if (jobReflectionData == IntPtr.Zero)
                    jobReflectionData = JobsUtility.CreateJobReflectionData(typeof(T), JobType.ParallelFor,
                        (ExecuteJobFunction) Execute);
                return jobReflectionData;
            }

            public delegate void ExecuteJobFunction(ref T data, IntPtr additionalPtr, IntPtr bufferRangePatchData,
                ref JobRanges ranges, int jobIndex);

            public static unsafe void Execute(ref T jobData, IntPtr additionalPtr, IntPtr bufferRangePatchData,
                ref JobRanges ranges, int jobIndex)
            {
                while (true)
                {
                    int begin;
                    int end;
                    if (!JobsUtility.GetWorkStealingRange(ref ranges, jobIndex, out begin, out end))
                        break;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    //JobsUtility.PatchBufferMinMaxRanges(bufferRangePatchData, UnsafeUtility.AddressOf(ref jobData), begin, end - begin);
                    #endif

                    for (var i = begin; i < end; ++i)
                        jobData.Execute(i);
                    DoDeallocateOnJobCompletion(jobData);
                }
            }
        }

        public static JobHandle Schedule<T>(this T jobData, int arrayLength, int innerloopBatchCount, JobHandle dependsOn = default(JobHandle)) where T : struct, IJobParallelFor
        {
            for (int i = 0; i != arrayLength; i++)
                jobData.Execute(i);
            DoDeallocateOnJobCompletion(jobData);
            return new JobHandle();
        }

        static internal void DoDeallocateOnJobCompletion(object jobData)
        {
            throw new NotImplementedException("This function should have been replaced by codegen");
        }
    }
}


namespace Unity.Jobs.LowLevel.Unsafe
{
    public static class JobsUtility
    {
        public const int MaxJobThreadCount = 128;
        public const int CacheLineSize = 64;

        public static unsafe IntPtr CreateJobReflectionData(Type type, Type type1, object parallelFor,
            MulticastDelegate execute)
        {
            throw new NotImplementedException();
        }

        public static IntPtr CreateJobReflectionData(Type type, JobType jobType, object managedJobFunction0, object managedJobFunction1 = null, object managedJobFunction2 = null) =>
            throw new NotImplementedException();
        public static bool GetWorkStealingRange(ref JobRanges ranges, int jobIndex, out int begin, out int end) => throw new NotImplementedException();
        public static JobHandle ScheduleParallelFor(ref JobScheduleParameters scheduleParams, int i, int minIndicesPerJobCount)  {
            throw new NotImplementedException();
        }
        public static unsafe JobHandle ScheduleParallelForDeferArraySize(ref JobScheduleParameters scheduleParams, int innerloopBatchCount, void* getInternalListDataPtrUnchecked, void* atomicSafetyHandlePtr) => throw new NotImplementedException();

        public static bool JobCompilerEnabled = false;
        public static bool JobDebuggerEnabled => false;

        [DllImport("nativehelper")]
        static extern unsafe void invoke_managed_job_execute_function(IntPtr functionPointer, void* payLoad, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex);

        //ExecuteJobFunction2(ref TJobData jobData, IntPtr additionalPtr, IntPtr bufferRangePatchData, ref JobRanges ranges, int jobIndex);

        public static unsafe JobHandle Schedule<TJobData>(ref JobScheduleParameters scheduleParams) where TJobData : struct
        {
            throw new NotImplementedException();
        }

        public class JobScheduleParameters
        {
            public IntPtr JobReflectionData { get; }
            public unsafe void* _addressOfPayload;

            public unsafe JobScheduleParameters(void* addressOfPayload, IntPtr jobReflectionData, JobHandle dependsOn, object batched)
            {
                JobReflectionData = jobReflectionData;
                _addressOfPayload = addressOfPayload;
            }
        }

        public static JobHandle Schedule(ref JobScheduleParameters parameters) => throw new NotImplementedException();

        public static unsafe void PatchBufferMinMaxRanges(IntPtr bufferRangePatchData, void* jobdata, int startIndex,
            int rangeSize)
        {
        }
    }


    public static class JobHandleUnsafeUtility
    {
        public static unsafe JobHandle CombineDependencies(JobHandle* jobs, int count) => new JobHandle();
    }

    public enum JobType
    {
        Single,
        ParallelFor
    }

    public struct JobRanges {}

    public enum ScheduleMode
    {
        Run,
        Batched
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class JobProducerTypeAttribute : Attribute
    {
        public JobProducerTypeAttribute(Type producerType) => throw new NotImplementedException();
        public Type ProducerType => throw new NotImplementedException();
    }
}

namespace Unity.Collections
{

    public class NativeContainerIsAtomicWriteOnlyAttribute : Attribute {}
    public class NativeSetThreadIndexAttribute : Attribute {}
    public class ReadOnlyAttribute : Attribute {}
    public class WriteOnlyAttribute : Attribute {}
    public class NativeDisableParallelForRestriction : Attribute {}
    public class DeallocateOnJobCompletionAttribute : Attribute {}
    public class WriteAccessRequiredAttribute : Attribute {}
}

namespace Unity.Collections.LowLevel.Unsafe
{
    public sealed class NativeContainerAttribute : Attribute {}
    public class NativeDisableUnsafePtrRestrictionAttribute : Attribute {}
    public sealed class NativeContainerSupportsMinMaxWriteRestriction : Attribute {}
    public class NativeSetClassTypeToNullOnSchedule : Attribute {}
    public class NativeContainerIsReadOnly : Attribute {}
    public sealed class NativeDisableContainerSafetyRestrictionAttribute : Attribute {}
}


namespace UnityEngine.Profiling
{
    public class CustomSampler
    {
        public static CustomSampler Create(string s) => throw new NotImplementedException();
        public void Begin() => throw new NotImplementedException();
        public void End() => throw new NotImplementedException();
    }

    public static class Profiler
    {
        public static void BeginSample(string s)
        {
        }

        public static void EndSample(){}
    }
}

namespace UnityEngine.Scripting
{
    public class PreserveAttribute : Attribute {}
}

namespace UnityEngine
{
    public static class Debug
    {
        internal static string lastLog;
        internal static string lastWarning;
        internal static string lastError;

        public static void LogError(object message)
        {
            if (message is string)
            {
                lastError = (string) message;
                Console.WriteLine((string) message);
            }
            else
            {
                lastError = "UNKNOWN OBJECT TYPE LOGGED";
                Console.WriteLine(lastError);
            }
        }

        public static void LogWarning(string message)
        {
            lastWarning = message;
            Console.WriteLine(message);
        }

        public static void Log(string message)
        {
            lastLog = message;
            Console.WriteLine(message);
        }

        public static void LogException(Exception exception)
        {
            lastLog = "Exception";
            Console.WriteLine(exception.Message + "\n" + exception.StackTrace);
        }
    }

    namespace TestTools
    {
        public static class LogAssert
        {
            public static void Expect(LogType type, string message)
            {
                if (type == LogType.Log) {
                    if (!message.Equals(Debug.lastLog))
                        throw new InvalidOperationException();
                } else if (type == LogType.Warning) {
                    if (!message.Equals(Debug.lastWarning))
                        throw new InvalidOperationException();
                }
            }

            public static void NoUnexpectedReceived()
            {
            }
        }
    }
}

namespace UnityEngine.Experimental.PlayerLoop
{
    public struct Initialization {}

    public struct Update
    {
        public struct ScriptRunBehaviourUpdate
        {
        }

        public struct ScriptRunDelayedDynamicFrameRate
        {
        }
    }
}

namespace UnityEngine.Experimental.LowLevel
{
    public struct PlayerLoopSystem
    {
        public System.Type type;
        public PlayerLoopSystem[] subSystemList;
        public PlayerLoopSystem.UpdateFunction updateDelegate;

        /*
        public IntPtr updateFunction;
        public IntPtr loopConditionFunction;*/
        public delegate void UpdateFunction();
    }

    public static class PlayerLoop
    {
        private static readonly PlayerLoopSystem _default = new PlayerLoopSystem()
        {
            type = typeof(int), subSystemList = new PlayerLoopSystem[1]
            {
                new PlayerLoopSystem()
                {
                    subSystemList = Array.Empty<PlayerLoopSystem>(),
                    type = null,
                    updateDelegate = Nothing
                }
            }, updateDelegate = Tick
        };

        private static void Nothing()
        {
        }

        private static PlayerLoopSystem _current;

        public static PlayerLoopSystem GetDefaultPlayerLoop() => _default;

        public static void Tick()
        {
            ProcessSystem(_current);
        }

        private static void ProcessSystem(PlayerLoopSystem playerLoopSystem)
        {
            playerLoopSystem.updateDelegate?.Invoke();

            foreach (var subSystem in playerLoopSystem.subSystemList ?? Array.Empty<PlayerLoopSystem>())
                ProcessSystem(subSystem);
        }

        public static void SetPlayerLoop(PlayerLoopSystem loop) => _current = loop;
    }
}

namespace UnityEngine
{
    public class Component {}

    public class Random
    {
        public static void InitState(int state)
        {
        }

        public static int Range(int one, int two)
        {
            return one;
        }
    }

    // The type of the log message in the delegate registered with Application.RegisterLogCallback.
    public enum LogType
    {
        // LogType used for Errors.
        Error = 0,
        // LogType used for Asserts. (These indicate an error inside Unity itself.)
        Assert = 1,
        // LogType used for Warnings.
        Warning = 2,
        // LogType used for regular log messages.
        Log = 3,
        // LogType used for Exceptions.
        Exception = 4
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class ExecuteAlwaysAttribute : Attribute
    {
        public ExecuteAlwaysAttribute()
        {
        }
    }

    public static class Time
    {
        [DllImport("lib_unity_zerojobs")]
        public static extern long Time_GetTicksMicrosecondsMonotonic();

        public static float time => Time_GetTicksMicrosecondsMonotonic() / 1_000_000.0f;
    }
}

namespace UnityEngine.Internal
{
    public class ExcludeFromDocsAttribute : Attribute {}
}


namespace Unity.Burst
{
    //why is this not in the burst package!?
    public class BurstDiscardAttribute : Attribute{}
}

namespace UnityEngine.Assertions
{
    public static class Assert
    {
        public static void AreEqual(object one, object two)
        {
        }

        public static void AreNotEqual(object one, object two)
        {
        }

        public static void IsTrue(bool b, string msg = null)
        {
        }

        public static void IsFalse(bool b, string msg = null)
        {
        }

        public static void AreApproximatelyEqual(object one, object two, object three = null, object msg =null)
        {
        }
    }
}

namespace Unity.Profiling
{
    public class ProfilerMarker
    {
        public ProfilerMarker(string s)
        {
        }

        public void Begin()
        {
        }

        public void End()
        {
        }

        class DummyDisposable : IDisposable
        {
            public void Dispose()
            {
            }

        }

        public IDisposable Auto()
        {
            return new DummyDisposable();
        }
    }
}
