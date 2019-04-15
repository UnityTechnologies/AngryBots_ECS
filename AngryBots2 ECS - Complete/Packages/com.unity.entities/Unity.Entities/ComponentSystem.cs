using System;
using System.Reflection;
using Unity.Collections;
using Unity.Jobs;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs.LowLevel.Unsafe;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace Unity.Entities
{

    [DebuggerTypeProxy(typeof(IntListDebugView))]
    internal unsafe struct IntList
    {
        public int* p;
        public int Count;
        public int Capacity;
    }

    public unsafe abstract partial class ComponentSystemBase
    {
        EntityQuery[] m_EntityQueries;
        EntityQuery[] m_RequiredEntityQueries;

        internal IntList m_JobDependencyForReadingSystems;
        internal IntList m_JobDependencyForWritingSystems;

        uint m_LastSystemVersion;

        internal ComponentJobSafetyManager* m_SafetyManager;
        internal EntityManager m_EntityManager;
        World m_World;

        bool m_AlwaysUpdateSystem;
        internal bool m_PreviouslyEnabled;

        public bool Enabled { get; set; } = true;
        public EntityQuery[] EntityQueries => m_EntityQueries;

        public uint GlobalSystemVersion => m_EntityManager.GlobalSystemVersion;
        public uint LastSystemVersion => m_LastSystemVersion;

        // ============

#if UNITY_EDITOR
        private UnityEngine.Profiling.CustomSampler m_Sampler;
#endif

#if !NET_DOTS
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private static HashSet<Type> s_ObsoleteAPICheckedTypes = new HashSet<Type>();

        void CheckForObsoleteAPI()
        {
            var type = this.GetType();
            while (type != typeof(ComponentSystemBase))
            {
                if (s_ObsoleteAPICheckedTypes.Contains(type))
                    break;

                if (type.GetMethod("OnCreateManager", BindingFlags.DeclaredOnly | BindingFlags.Instance) != null)
                {
                    Debug.LogWarning($"The OnCreateManager overload in {type} is obsolete; please rename it to OnCreate.  OnCreateManager will stop being called in a future release.");
                }

                if (type.GetMethod("OnDestroyManager", BindingFlags.DeclaredOnly | BindingFlags.Instance) != null)
                {
                    Debug.LogWarning($"The OnDestroyManager overload in {type} is obsolete; please rename it to OnDestroy.  OnDestroyManager will stop being called in a future release.");
                }

                s_ObsoleteAPICheckedTypes.Add(type);

                type = type.BaseType;
            }
        }

        protected ComponentSystemBase()
        {
             CheckForObsoleteAPI();
        }
#endif
#endif

        internal void CreateInstance(World world)
        {
            OnBeforeCreateInternal(world);
            try
            {
                OnCreateManager(); // DELETE after obsolete period!
                OnCreate();
#if UNITY_EDITOR
                var type = GetType();
                m_Sampler = UnityEngine.Profiling.CustomSampler.Create($"{world.Name} {type.FullName}");
#endif
            }
            catch
            {
                OnBeforeDestroyInternal();
                OnAfterDestroyInternal();
                throw;
            }
        }

        internal void DestroyInstance()
        {
            OnBeforeDestroyInternal();
            OnDestroy();
            OnDestroyManager(); // DELETE after obsolete period!
            OnAfterDestroyInternal();
        }

        protected virtual void OnCreateManager()
        {
        }

        protected virtual void OnDestroyManager()
        {
        }

        /// <summary>
        /// Called when the ComponentSystem/JobComponentSystem is created.
        /// When scripts are reloaded, OnCreate will be invoked before the
        /// ComponentSystems receives its first OnUpdate call.
        /// </summary>
        protected virtual void OnCreate()
        {
        }

        /// <summary>
        /// OnStartRunning is called when the ComponentSystem starts running
        /// for the first time, and every time after it was disabled due to no matching entities.
        /// </summary>
        protected virtual void OnStartRunning()
        {
        }

        /// <summary>
        /// OnStopRunning is called when no entities would match the system's
        /// EntityQueries (and OnStartRunning was executed before).
        /// </summary>
        protected virtual void OnStopRunning()
        {
        }

        /// <summary>
        /// Called when the ComponentSystem/JobComponentSystem is destroyed.
        /// Will be called when scripts are reloaded or before Play Mode exits before
        /// the system is destroyed.
        /// </summary>
        protected virtual void OnDestroy()
        {
        }

        /// <summary>
        ///     Execute the system immediately.
        /// </summary>
        public void Update()
        {
#if UNITY_EDITOR
            m_Sampler?.Begin();
#endif
            InternalUpdate();

#if UNITY_EDITOR
            m_Sampler?.End();
#endif
        }

        protected internal EntityManager EntityManager => m_EntityManager;
        protected internal World World => m_World;

        // ===================

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        internal int                        m_SystemID;
        static internal ComponentSystemBase ms_ExecutingSystem;

        internal ComponentSystemBase GetSystemFromSystemID(World world, int systemID)
        {
            foreach(var m in world.Systems)
            {
                var system = m as ComponentSystemBase;
                if (system == null)
                    continue;
                if (system.m_SystemID == systemID)
                    return system;
            }

            return null;
        }
#endif

        ref UnsafeList JobDependencyForReadingSystemsUnsafeList =>
            ref *(UnsafeList*) UnsafeUtility.AddressOf(ref m_JobDependencyForReadingSystems);

        ref UnsafeList JobDependencyForWritingSystemsUnsafeList =>
            ref *(UnsafeList*) UnsafeUtility.AddressOf(ref m_JobDependencyForWritingSystems);

        [Conditional("ENABLE_UNITY_COLLECTIONS_CHECKS")]
        internal void CheckExists()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (World == null || !World.IsCreated)
                throw new InvalidOperationException($"{GetType()} has already been destroyed. It may not be used anymore.");
#endif
        }

        public bool ShouldRunSystem()
        {
            CheckExists();

            if (m_AlwaysUpdateSystem)
                return true;

            if (m_RequiredEntityQueries != null)
            {
                for (int i = 0; i != m_RequiredEntityQueries.Length; i++)
                {
                    if (m_RequiredEntityQueries[i].IsEmptyIgnoreFilter)
                        return false;
                }

                return true;
            }
            else
            {
                // Systems without queriesDesc should always run. Specifically,
                // IJobForEach adds its queriesDesc the first time it's run.
                var length = m_EntityQueries != null ? m_EntityQueries.Length : 0;
                if (length == 0)
                    return true;

                // If all the queriesDesc are empty, skip it.
                // (There’s no way to know what they key value is without other markup)
                for (int i = 0; i != length; i++)
                {
                    if (!m_EntityQueries[i].IsEmptyIgnoreFilter)
                        return true;
                }

                return false;
            }
        }

        internal virtual void OnBeforeCreateInternal(World world)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_SystemID = World.AllocateSystemID();
#endif
            m_World = world;
            m_EntityManager = world.EntityManager;
            m_SafetyManager = m_EntityManager.ComponentJobSafetyManager;

            m_EntityQueries = new EntityQuery[0];
#if !NET_DOTS
            m_AlwaysUpdateSystem = GetType().GetCustomAttributes(typeof(AlwaysUpdateSystemAttribute), true).Length != 0;
#else
            m_AlwaysUpdateSystem = true;
#endif
        }

        internal void OnAfterDestroyInternal()
        {
            foreach (var query in m_EntityQueries)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                query.DisallowDisposing = null;
#endif
                query.Dispose();
            }

            m_EntityQueries = null;
            m_EntityManager = null;
            m_World = null;
            m_SafetyManager = null;

            JobDependencyForReadingSystemsUnsafeList.Dispose<int>();
            JobDependencyForWritingSystemsUnsafeList.Dispose<int>();
        }

        internal abstract void InternalUpdate();

        internal virtual void OnBeforeDestroyInternal()
        {
            if (m_PreviouslyEnabled)
            {
                m_PreviouslyEnabled = false;
                OnStopRunning();
            }
        }

        internal void BeforeUpdateVersioning()
        {
            m_EntityManager.Entities->IncrementGlobalSystemVersion();
            foreach (var query in m_EntityQueries)
                query.SetFilterChangedRequiredVersion(m_LastSystemVersion);
        }

        internal void AfterUpdateVersioning()
        {
            m_LastSystemVersion = EntityManager.Entities->GlobalSystemVersion;
        }

        // TODO: this should be made part of UnityEngine?
        static void ArrayUtilityAdd<T>(ref T[] array, T item)
        {
            Array.Resize(ref array, array.Length + 1);
            array[array.Length - 1] = item;
        }

        public ArchetypeChunkComponentType<T> GetArchetypeChunkComponentType<T>(bool isReadOnly = false)
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return EntityManager.GetArchetypeChunkComponentType<T>(isReadOnly);
        }

        public ArchetypeChunkBufferType<T> GetArchetypeChunkBufferType<T>(bool isReadOnly = false)
            where T : struct, IBufferElementData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return EntityManager.GetArchetypeChunkBufferType<T>(isReadOnly);
        }

        public ArchetypeChunkSharedComponentType<T> GetArchetypeChunkSharedComponentType<T>()
            where T : struct, ISharedComponentData
        {
            return EntityManager.GetArchetypeChunkSharedComponentType<T>();
        }

        public ArchetypeChunkEntityType GetArchetypeChunkEntityType()
        {
            return EntityManager.GetArchetypeChunkEntityType();
        }

        public ComponentDataFromEntity<T> GetComponentDataFromEntity<T>(bool isReadOnly = false)
            where T : struct, IComponentData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return EntityManager.GetComponentDataFromEntity<T>(isReadOnly);
        }

        public void RequireForUpdate(EntityQuery query)
        {
            if (m_RequiredEntityQueries == null)
                m_RequiredEntityQueries = new EntityQuery[1] {query};
            else
                ArrayUtilityAdd(ref m_RequiredEntityQueries, query);
        }

        public void RequireSingletonForUpdate<T>()
        {
            var type = ComponentType.ReadOnly<T>();
            var query = GetEntityQueryInternal(&type, 1);
            RequireForUpdate(query);
        }

        public bool HasSingleton<T>()
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            var query = GetEntityQueryInternal(&type, 1);
            return !query.IsEmptyIgnoreFilter;
        }

        public T GetSingleton<T>()
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            var query = GetEntityQueryInternal(&type, 1);

            return query.GetSingleton<T>();
        }

        public void SetSingleton<T>(T value)
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            var query = GetEntityQueryInternal(&type, 1);
            query.SetSingleton(value);
        }

        public Entity GetSingletonEntity<T>()
            where T : struct, IComponentData
        {
            var type = ComponentType.ReadOnly<T>();
            var query = GetEntityQueryInternal(&type, 1);

            return query.GetSingletonEntity();
        }

        internal void AddReaderWriter(ComponentType componentType)
        {
            if (CalculateReaderWriterDependency.Add(componentType, ref JobDependencyForReadingSystemsUnsafeList,
                ref JobDependencyForWritingSystemsUnsafeList))
            {
                CompleteDependencyInternal();
            }
        }

        internal void AddReaderWriters(EntityQuery query)
        {
            if (query.AddReaderWritersToLists(ref JobDependencyForReadingSystemsUnsafeList,
                ref JobDependencyForWritingSystemsUnsafeList))
            {
                CompleteDependencyInternal();
            }
        }

        internal EntityQuery GetEntityQueryInternal(ComponentType* componentTypes, int count)
        {
            for (var i = 0; i != m_EntityQueries.Length; i++)
            {
                if (m_EntityQueries[i].CompareComponents(componentTypes, count))
                    return m_EntityQueries[i];
            }

            var query = EntityManager.CreateEntityQuery(componentTypes, count);

            AddReaderWriters(query);
            AfterQueryCreated(query);

            return query;
        }

        internal EntityQuery GetEntityQueryInternal(ComponentType[] componentTypes)
        {
            fixed (ComponentType* componentTypesPtr = componentTypes)
            {
                return GetEntityQueryInternal(componentTypesPtr, componentTypes.Length);
            }
        }

        internal EntityQuery GetEntityQueryInternal(EntityQueryDesc[] desc)
        {
            for (var i = 0; i != m_EntityQueries.Length; i++)
            {
                if (m_EntityQueries[i].CompareQuery(desc))
                    return m_EntityQueries[i];
            }

            var query = EntityManager.CreateEntityQuery(desc);

            AddReaderWriters(query);
            AfterQueryCreated(query);

            return query;
        }

        void AfterQueryCreated(EntityQuery query)
        {
            query.SetFilterChangedRequiredVersion(m_LastSystemVersion);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            query.DisallowDisposing =
 "EntityQuery.Dispose() may not be called on a EntityQuery created with ComponentSystem.GetEntityQuery. The EntityQuery will automatically be disposed by the ComponentSystem.";
#endif

            ArrayUtilityAdd(ref m_EntityQueries, query);
        }

        protected internal EntityQuery GetEntityQuery(params ComponentType[] componentTypes)
        {
            return GetEntityQueryInternal(componentTypes);
        }

        protected EntityQuery GetEntityQuery(NativeArray<ComponentType> componentTypes)
        {
            return GetEntityQueryInternal((ComponentType*) componentTypes.GetUnsafeReadOnlyPtr(),
                componentTypes.Length);
        }

        protected internal EntityQuery GetEntityQuery(params EntityQueryDesc[] queryDesc)
        {
            return GetEntityQueryInternal(queryDesc);
        }

        internal void CompleteDependencyInternal()
        {
            m_SafetyManager->CompleteDependenciesNoChecks(m_JobDependencyForReadingSystems.p,
                m_JobDependencyForReadingSystems.Count, m_JobDependencyForWritingSystems.p,
                m_JobDependencyForWritingSystems.Count);
        }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("GetEntities has been deprecated. Use Entities.ForEach to access managed components. More information: https://forum.unity.com/threads/api-deprecation-faq-0-0-23.636994/", true)]
        protected void GetEntities<T>() where T : struct { }
    }

    public abstract partial class ComponentSystem : ComponentSystemBase
    {
        EntityCommandBuffer m_DeferredEntities;
        EntityQueryCache m_EntityQueryCache;

        public EntityCommandBuffer PostUpdateCommands => m_DeferredEntities;

        protected internal void InitEntityQueryCache(int cacheSize) =>
            m_EntityQueryCache = new EntityQueryCache(cacheSize);

        internal EntityQueryCache EntityQueryCache => m_EntityQueryCache;

        internal EntityQueryCache GetOrCreateEntityQueryCache()
            => m_EntityQueryCache ?? (m_EntityQueryCache = new EntityQueryCache());

        protected internal EntityQueryBuilder Entities => new EntityQueryBuilder(this);

        unsafe void BeforeOnUpdate()
        {
            BeforeUpdateVersioning();
            CompleteDependencyInternal();

            m_DeferredEntities = new EntityCommandBuffer(Allocator.TempJob, -1);
        }

        void AfterOnUpdate()
        {
            AfterUpdateVersioning();

            JobHandle.ScheduleBatchedJobs();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            try
            {
                m_DeferredEntities.Playback(EntityManager);
            }
            catch (Exception e)
            {
                m_DeferredEntities.Dispose();
                var error = $"{e.Message}\nEntityCommandBuffer was recorded in {GetType()} using PostUpdateCommands.\n" + e.StackTrace;
                throw new System.ArgumentException(error);
            }
#else
            m_DeferredEntities.Playback(EntityManager);
#endif
            m_DeferredEntities.Dispose();
        }

        internal sealed override void InternalUpdate()
        {
            if (Enabled && ShouldRunSystem())
            {
                if (!m_PreviouslyEnabled)
                {
                    m_PreviouslyEnabled = true;
                    OnStartRunning();
                }

                BeforeOnUpdate();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var oldExecutingSystem = ms_ExecutingSystem;
                ms_ExecutingSystem = this;
#endif

                try
                {
                    OnUpdate();
                }
                finally
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    ms_ExecutingSystem = oldExecutingSystem;
#endif
                    AfterOnUpdate();
                }
            }
            else if (m_PreviouslyEnabled)
            {
                m_PreviouslyEnabled = false;
                OnStopRunning();
            }
        }

        internal sealed override void OnBeforeCreateInternal(World world)
        {
            base.OnBeforeCreateInternal(world);
        }

        internal sealed override void OnBeforeDestroyInternal()
        {
            base.OnBeforeDestroyInternal();
        }

        /// <summary>
        /// Called once per frame on the main thread when any of this system's
        /// EntityQueries would match, or if the system has the AlwaysUpdate
        /// attribute.
        /// </summary>
        protected abstract void OnUpdate();
    }

    public abstract class JobComponentSystem : ComponentSystemBase
    {
        JobHandle m_PreviousFrameDependency;

        unsafe JobHandle BeforeOnUpdate()
        {
            BeforeUpdateVersioning();

            // We need to wait on all previous frame dependencies, otherwise it is possible that we create infinitely long dependency chains
            // without anyone ever waiting on it
            m_PreviousFrameDependency.Complete();

            return GetDependency();
        }

        unsafe void AfterOnUpdate(JobHandle outputJob, bool throwException)
        {
            AfterUpdateVersioning();

            JobHandle.ScheduleBatchedJobs();

            AddDependencyInternal(outputJob);

#if ENABLE_UNITY_COLLECTIONS_CHECKS

            if (!JobsUtility.JobDebuggerEnabled)
                return;

            // Check that all reading and writing jobs are a dependency of the output job, to
            // catch systems that forget to add one of their jobs to the dependency graph.
            //
            // Note that this check is not strictly needed as we would catch the mistake anyway later,
            // but checking it here means we can flag the system that has the mistake, rather than some
            // other (innocent) system that is doing things correctly.

            //@TODO: It is not ideal that we call m_SafetyManager.GetDependency,
            //       it can result in JobHandle.CombineDependencies calls.
            //       Which seems like debug code might have side-effects

            string dependencyError = null;
            for (var index = 0; index < m_JobDependencyForReadingSystems.Count && dependencyError == null; index++)
            {
                var type = m_JobDependencyForReadingSystems.p[index];
                dependencyError = CheckJobDependencies(type);
            }

            for (var index = 0; index < m_JobDependencyForWritingSystems.Count && dependencyError == null; index++)
            {
                var type = m_JobDependencyForWritingSystems.p[index];
                dependencyError = CheckJobDependencies(type);
            }

            if (dependencyError != null)
            {
                EmergencySyncAllJobs();

                if (throwException)
                    throw new System.InvalidOperationException(dependencyError);
            }
#endif
        }

        internal sealed override void InternalUpdate()
        {
            if (Enabled && ShouldRunSystem())
            {
                if (!m_PreviouslyEnabled)
                {
                    m_PreviouslyEnabled = true;
                    OnStartRunning();
                }

                var inputJob = BeforeOnUpdate();
                JobHandle outputJob = new JobHandle();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
                var oldExecutingSystem = ms_ExecutingSystem;
                ms_ExecutingSystem = this;
#endif
                try
                {
                    outputJob = OnUpdate(inputJob);
                }
                catch
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    ms_ExecutingSystem = oldExecutingSystem;
#endif

                    AfterOnUpdate(outputJob, false);
                    throw;
                }

                AfterOnUpdate(outputJob, true);
            }
            else if (m_PreviouslyEnabled)
            {
                m_PreviouslyEnabled = false;
                OnStopRunning();
            }
        }

        internal sealed override void OnBeforeDestroyInternal()
        {
            base.OnBeforeDestroyInternal();
            m_PreviousFrameDependency.Complete();
        }

        public BufferFromEntity<T> GetBufferFromEntity<T>(bool isReadOnly = false) where T : struct, IBufferElementData
        {
            AddReaderWriter(isReadOnly ? ComponentType.ReadOnly<T>() : ComponentType.ReadWrite<T>());
            return EntityManager.GetBufferFromEntity<T>(isReadOnly);
        }

        protected abstract JobHandle OnUpdate(JobHandle inputDeps);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        unsafe string CheckJobDependencies(int type)
        {
            var h = m_SafetyManager->GetSafetyHandle(type, true);

            var readerCount = AtomicSafetyHandle.GetReaderArray(h, 0, IntPtr.Zero);
            JobHandle* readers = stackalloc JobHandle[readerCount];
            AtomicSafetyHandle.GetReaderArray(h, readerCount, (IntPtr) readers);

            for (var i = 0; i < readerCount; ++i)
            {
                if (!m_SafetyManager->HasReaderOrWriterDependency(type, readers[i]))
                    return $"The system {GetType()} reads {TypeManager.GetType(type)} via {AtomicSafetyHandle.GetReaderName(h, i)} but that type was not returned as a job dependency. To ensure correct behavior of other systems, the job or a dependency of it must be returned from the OnUpdate method.";
            }

            if (!m_SafetyManager->HasReaderOrWriterDependency(type, AtomicSafetyHandle.GetWriter(h)))
                return $"The system {GetType()} writes {TypeManager.GetType(type)} via {AtomicSafetyHandle.GetWriterName(h)} but that was not returned as a job dependency. To ensure correct behavior of other systems, the job or a dependency of it must be returned from the OnUpdate method.";

            return null;
        }

        unsafe void EmergencySyncAllJobs()
        {
            for (int i = 0;i != m_JobDependencyForReadingSystems.Count;i++)
            {
                int type = m_JobDependencyForReadingSystems.p[i];
                AtomicSafetyHandle.EnforceAllBufferJobsHaveCompleted(m_SafetyManager->GetSafetyHandle(type, true));
            }

            for (int i = 0;i != m_JobDependencyForWritingSystems.Count;i++)
            {
                int type = m_JobDependencyForWritingSystems.p[i];
                AtomicSafetyHandle.EnforceAllBufferJobsHaveCompleted(m_SafetyManager->GetSafetyHandle(type, true));
            }
        }
#endif

        unsafe JobHandle GetDependency ()
        {
            return m_SafetyManager->GetDependency(m_JobDependencyForReadingSystems.p, m_JobDependencyForReadingSystems.Count, m_JobDependencyForWritingSystems.p, m_JobDependencyForWritingSystems.Count);
        }

        unsafe void AddDependencyInternal(JobHandle dependency)
        {
            m_PreviousFrameDependency = m_SafetyManager->AddDependency(m_JobDependencyForReadingSystems.p, m_JobDependencyForReadingSystems.Count, m_JobDependencyForWritingSystems.p, m_JobDependencyForWritingSystems.Count, dependency);
        }
    }

    [Obsolete("BarrierSystem has been renamed. Use EntityCommandBufferSystem instead (UnityUpgradable) -> EntityCommandBufferSystem", true)]
    [System.ComponentModel.EditorBrowsable(EditorBrowsableState.Never)]
    public struct BarrierSystem { }

    public unsafe abstract class EntityCommandBufferSystem : ComponentSystem
    {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        private List<EntityCommandBuffer> m_PendingBuffers;
#else
        private NativeList<EntityCommandBuffer> m_PendingBuffers;
#endif
        private JobHandle m_ProducerHandle;

        /// <summary>
        /// Create an EntityCommandBuffer which will be played back during this EntityCommandBufferSystem's OnUpdate().
        /// If this command buffer is written to by job code using its Concurrent interface, the caller
        /// must call EntityCommandBufferSystem.AddJobHandleForProducer() to ensure that the EntityCommandBufferSystem waits
        /// for the job to complete before playing back the command buffer. See AddJobHandleForProducer()
        /// for a complete example.
        /// </summary>
        /// <returns></returns>
        public EntityCommandBuffer CreateCommandBuffer()
        {
            var cmds = new EntityCommandBuffer(Allocator.TempJob, -1);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            cmds.SystemID = ms_ExecutingSystem != null ? ms_ExecutingSystem.m_SystemID : 0;
#endif
            m_PendingBuffers.Add(cmds);

            return cmds;
        }

        /// <summary>
        /// Adds the specified JobHandle to this system's list of dependencies.
        ///
        /// This is usually called by a system that's building an EntityCommandBuffer created
        /// by this EntityCommandBufferSystem, to prevent the command buffer from being played back before
        /// it's complete. The general usage looks like:
        ///    MyEntityCommandBufferSystem _barrier;
        ///    // in OnCreate():
        ///    _barrier = World.GetOrCreateManager<MyEntityCommandBufferSystem>();
        ///    // in OnUpdate():
        ///    EntityCommandBuffer cmd = _barrier.CreateCommandBuffer();
        ///    var job = new MyProducerJob {
        ///        CommandBuffer = cmd,
        ///    }.Schedule(this, inputDeps);
        ///    _barrier.AddJobHandleForProducer(job);
        /// </summary>
        /// <param name="producerJob">A JobHandle which this barrier system should wait on before playing back its
        /// pending EntityCommandBuffers.</param>
        public void AddJobHandleForProducer(JobHandle producerJob)
        {
            m_ProducerHandle = JobHandle.CombineDependencies(m_ProducerHandle, producerJob);
        }

        protected override void OnCreate()
        {
            base.OnCreate();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBuffers = new List<EntityCommandBuffer>();
#else
            m_PendingBuffers = new NativeList<EntityCommandBuffer>(Allocator.Persistent);
#endif
        }

        protected override void OnDestroy()
        {
            FlushBuffers(false);

#if !ENABLE_UNITY_COLLECTIONS_CHECKS
            m_PendingBuffers.Dispose();
#endif

            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            FlushBuffers(true);
        }

        private void FlushBuffers(bool playBack)
        {
            m_ProducerHandle.Complete();
            m_ProducerHandle = new JobHandle();

            int length;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            length = m_PendingBuffers.Count;
#else
            length = m_PendingBuffers.Length;
#endif

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            List<string> playbackErrorLog = null;
#endif
            for (int i = 0; i < length; ++i)
            {
                var buffer = m_PendingBuffers[i];
                if (playBack)
                {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                    try
                    {
                        buffer.Playback(EntityManager);
                    }
                    catch (Exception e)
                    {
                        var system = GetSystemFromSystemID(World, buffer.SystemID);
                        var systemType = system != null ? system.GetType().ToString() : "Unknown";
                        var error = $"{e.Message}\nEntityCommandBuffer was recorded in {systemType} and played back in {GetType()}.\n" + e.StackTrace;
                        if (playbackErrorLog == null)
                        {
                            playbackErrorLog = new List<string>();
                        }
                        playbackErrorLog.Add(error);
                    }
#else
                    buffer.Playback(EntityManager);
#endif
                }
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                try
                {
                    buffer.Dispose();
                }
                catch (Exception e)
                {
                    var system = GetSystemFromSystemID(World, buffer.SystemID);
                    var systemType = system != null ? system.GetType().ToString() : "Unknown";
                    var error = $"{e.Message}\nEntityCommandBuffer was recorded in {systemType} and disposed in {GetType()}.\n" + e.StackTrace;
                    if (playbackErrorLog == null)
                    {
                        playbackErrorLog = new List<string>();
                    }
                    playbackErrorLog.Add(error);
                }
#else
                buffer.Dispose();
#endif
            }
            m_PendingBuffers.Clear();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (playbackErrorLog != null)
            {
#if !NET_DOTS
                string exceptionMessage = playbackErrorLog.Aggregate((str1, str2) => str1 + "\n" + str2);
#else
                foreach (var err in playbackErrorLog)
                {
                    Console.WriteLine(err);
                }
                string exceptionMessage = "Errors occurred during ECB playback; see stdout";
#endif
                Exception exception = new System.ArgumentException(exceptionMessage);
                throw exception;
            }
#endif
        }
    }
}
