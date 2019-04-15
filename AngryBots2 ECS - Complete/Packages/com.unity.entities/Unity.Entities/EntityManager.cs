using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;
using UnityEngine.Scripting;

[assembly: InternalsVisibleTo("Unity.Entities.Hybrid")]
[assembly: InternalsVisibleTo("Unity.Entities.Properties")]

namespace Unity.Entities
{
    //@TODO: There is nothing prevent non-main thread (non-job thread) access of EntityManager.
    //       Static Analysis or runtime checks?

    //@TODO: safety?

    /// <summary>
    /// An EntityArchetype is a unique combination of component types. The <see cref="EntityManager"/>
    /// uses the archetype to group all entities that have the same sets of components.
    /// </summary>
    /// <remarks>
    /// An entity can change archetype fluidly over its lifespan. For example, when you add or
    /// remove components, the archetype of the affected entity changes.
    ///
    /// An archetype object is not a container; rather it is an identifier to each unique combination
    /// of component types that an application has created at run time, either directly or implicitly.
    ///
    /// You can create archetypes directly using <see cref="EntityManager.CreateArchetype(ComponentType[])"/>.
    /// You also implicitly create archetypes whenever you add or remove a component from an entity. An EntityArchetype
    /// object is an immutable singleton; creating an archetype with the same set of components, either directly or
    /// implicitly, results in the same archetype for a given EntityManager.
    ///
    /// The ECS framework uses archetypes to group entities that have the same structure together. The ECS framework
    /// stores component data in blocks of memory called *chunks*. A given chunk stores only entities having the same
    /// archetype. You can get the EntityArchetype object for a chunk from its <see cref="ArchetypeChunk.Archetype"/>
    /// property.
    /// </remarks>
    [DebuggerTypeProxy(typeof(EntityArchetypeDebugView))]
    public unsafe struct EntityArchetype : IEquatable<EntityArchetype>
    {
        [NativeDisableUnsafePtrRestriction] internal Archetype* Archetype;

        /// <summary>
        /// Reports whether this EntityArchetype instance references a non-null archetype.
        /// </summary>
        /// <value>True, if the archetype is valid.</value>
        public bool Valid => Archetype != null;

        /// <summary>
        /// Compares the archetypes for equality.
        /// </summary>
        /// <param name="lhs">A EntityArchetype object.</param>
        /// <param name="rhs">Another EntityArchetype object.</param>
        /// <returns>True, if these EntityArchetype instances reference the same archetype.</returns>
        public static bool operator ==(EntityArchetype lhs, EntityArchetype rhs)
        {
            return lhs.Archetype == rhs.Archetype;
        }

        /// <summary>
        /// Compares the archetypes for inequality.
        /// </summary>
        /// <param name="lhs">A EntityArchetype object.</param>
        /// <param name="rhs">Another EntityArchetype object.</param>
        /// <returns>True, if these EntityArchetype instances reference different archetypes.</returns>
        public static bool operator !=(EntityArchetype lhs, EntityArchetype rhs)
        {
            return lhs.Archetype != rhs.Archetype;
        }

        /// <summary>
        /// Reports whether this EntityArchetype references the same archetype as another object.
        /// </summary>
        /// <param name="compare">The object to compare.</param>
        /// <returns>True, if the compare parameter is a EntityArchetype instance that points to the same
        /// archetype.</returns>
        public override bool Equals(object compare)
        {
            return this == (EntityArchetype) compare;
        }

        /// <summary>
        /// Compares archetypes for equality.
        /// </summary>
        /// <param name="entityArchetype">The EntityArchetype to compare.</param>
        /// <returns>Returns true, if both EntityArchetype instances reference the same archetype.</returns>
        public bool Equals(EntityArchetype entityArchetype)
        {
            return Archetype == entityArchetype.Archetype;
        }

        /// <summary>
        /// Returns the hash of the archetype.
        /// </summary>
        /// <remarks>Two EntityArchetype instances referencing the same archetype return the same hash.</remarks>
        /// <returns>An integer hash code.</returns>
        public override int GetHashCode()
        {
            return (int) Archetype;
        }

        /// <summary>
        /// Gets the types of the components making up this archetype.
        /// </summary>
        /// <remarks>The set of component types in an archetype cannot change; adding components to an entity or
        /// removing components from an entity changes the archetype of that entity (possibly resulting in the
        /// creation of a new archetype). The original archetype remains unchanged.</remarks>
        /// <param name="allocator">The allocation type to use for the returned NativeArray.</param>
        /// <returns>A native array containing the <see cref="ComponentType"/> objects of this archetype.</returns>
        public NativeArray<ComponentType> GetComponentTypes(Allocator allocator = Allocator.Temp)
        {
            var types = new NativeArray<ComponentType>(Archetype->TypesCount, allocator);
            for (var i = 0; i < types.Length; ++i)
                types[i] = Archetype->Types[i].ToComponentType();
            return types;
        }

        /// <summary>
        /// The current number of chunks storing entities having this archetype.
        /// </summary>
        /// <value>The number of chunks.</value>
        /// <remarks>This value can change whenever structural changes occur.
        /// Structural changes include creating or destroying entities, adding components to or removing them from
        /// an entity, and changing the value of shared components, all of which alter where entities are stored.
        /// </remarks>
        public int ChunkCount => Archetype->Chunks.Count;

        /// <summary>
        /// The number of entities having this archetype that can fit into a single chunk of memory.
        /// </summary>
        /// <value>Capacity is determined by the fixed, 16KB size of the memory blocks allocated by the ECS framework
        /// and the total storage size of all the component types in the archetype.</value>
        public int ChunkCapacity => Archetype->ChunkCapacity;
    }

    /// <summary>
    /// Identifies an entity.
    /// </summary>
    /// <remarks>
    /// The entity is a fundamental part of the Entity Component System. Everything in your game that has data or an
    /// identity of its own is an entity. However, an entity does not contain either data or behavior itself. Instead,
    /// the data is stored in the components and the behavior is provided by the systems that process those
    /// components. The entity acts as an identifier or key to the data stored in components.
    ///
    /// Entities are managed by the <see cref="EntityManager"/> class and exist within a <see cref="World"/>. An
    /// Entity struct refers to an entity, but is not a reference. Rather the Entity struct contains an
    /// <see cref="Index"/> used to access entity data and a <see cref="Version"/> used to check whether the Index is
    /// still valid. Note that you generally do not use the Index or Version values directly, but instead pass the
    /// Entity struct to the relevant API methods.
    ///
    /// Pass an Entity struct to methods of the <see cref="EntityManager"/>, the <see cref="EntityCommandBuffer"/>,
    /// or the <see cref="ComponentSystem"/> in order to add or remove components, to access components, or to destroy
    /// the entity.
    /// </remarks>
    public struct Entity : IEquatable<Entity>
    {
        /// <summary>
        /// The ID of an entity.
        /// </summary>
        /// <value>The index into the internal list of entities.</value>
        /// <remarks>
        /// Entity indexes are recycled when an entity is destroyed. When an entity is destroyed, the
        /// EntityManager increments the version identifier. To represent the same entity, both the Index and the
        /// Version fields of the Entity object must match. If the Index is the same, but the Version is different,
        /// then the entity has been recycled.
        /// </remarks>
        public int Index;
        /// <summary>
        /// The generational version of the entity.
        /// </summary>
        /// <remarks>The Version number can, theoretically, overflow and wrap around within the lifetime of an
        /// application. For this reason, you cannot assume that an Entity instance with a larger Version is a more
        /// recent incarnation of the entity than one with a smaller Version (and the same Index).</remarks>
        /// <value>Used to determine whether this Entity object still identifies an existing entity.</value>
        public int Version;

        /// <summary>
        /// Entity instances are equal if they refer to the same entity.
        /// </summary>
        /// <param name="lhs">An Entity object.</param>
        /// <param name="rhs">Another Entity object.</param>
        /// <returns>True, if both Index and Version are identical.</returns>
        public static bool operator ==(Entity lhs, Entity rhs)
        {
            return lhs.Index == rhs.Index && lhs.Version == rhs.Version;
        }

        /// <summary>
        /// Entity instances are equal if they refer to the same entity.
        /// </summary>
        /// <param name="lhs">An Entity object.</param>
        /// <param name="rhs">Another Entity object.</param>
        /// <returns>True, if either Index or Version are different.</returns>
        public static bool operator !=(Entity lhs, Entity rhs)
        {
            return !(lhs == rhs);
        }

        /// <summary>
        /// Entity instances are equal if they refer to the same entity.
        /// </summary>
        /// <param name="compare">The object to compare to this Entity.</param>
        /// <returns>True, if the compare parameter contains an Entity object having the same Index and Version
        /// as this Entity.</returns>
        public override bool Equals(object compare)
        {
            return this == (Entity) compare;
        }

        /// <summary>
        /// A hash used for comparisons.
        /// </summary>
        /// <returns>A unique hash code.</returns>
        public override int GetHashCode()
        {
            return Index;
        }

        /// <summary>
        /// A "blank" Entity object that does not refer to an actual entity.
        /// </summary>
        public static Entity Null => new Entity();

        /// <summary>
        /// Entity instances are equal if they represent the same entity.
        /// </summary>
        /// <param name="entity">The other Entity.</param>
        /// <returns>True, if the Entity instances have the same Index and Version.</returns>
        public bool Equals(Entity entity)
        {
            return entity.Index == Index && entity.Version == Version;
        }

        /// <summary>
        /// Provides a debugging string.
        /// </summary>
        /// <returns>A string containing the entity index and generational version.</returns>
        public override string ToString()
        {
            return Equals(Entity.Null) ? "Entity.Null" : $"Entity({Index}:{Version})";
        }
    }

    /// <summary>
    /// The EntityManager manages entities and components in a World.
    /// </summary>
    /// <remarks>
    /// The EntityManager provides an API to create, read, update, and destroy entities.
    ///
    /// A <see cref="World"/> has one EntityManager, which manages all the entities for that World.
    ///
    /// Many EntityManager operations result in *structural changes* that change the layout of entities in memory.
    /// Before it can perform such operations, the EntityManager must wait for all running Jobs to complete, an event
    /// called a *sync point*. A sync point both blocks the main thread and prevents the application from taking
    /// advantage of all available cores as the running Jobs wind down.
    ///
    /// Although you cannot prevent sync points entirely, you should avoid them as much as possible. To this end, the ECS
    /// framework provides the <see cref="EntityCommandBuffer"/>, which allows you to queue structural changes so that
    /// they all occur at one time in the frame.
    /// </remarks>
    [Preserve]
    [DebuggerTypeProxy(typeof(EntityManagerDebugView))]
    public sealed unsafe partial class EntityManager : EntityManagerBaseInterfaceForObsolete
    {
        EntityDataManager* m_Entities;
        ComponentJobSafetyManager* m_ComponentJobSafetyManager;

        ArchetypeManager m_ArchetypeManager;
        EntityGroupManager m_GroupManager;

        internal SharedComponentDataManager m_SharedComponentManager;

        ExclusiveEntityTransaction m_ExclusiveEntityTransaction;

        World m_World;
        private EntityQuery            m_UniversalQuery; // matches all components
        /// <summary>
        /// A EntityQuery instance that matches all components.
        /// </summary>
        public EntityQuery             UniversalQuery => m_UniversalQuery;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
        int m_InsideForEach;
        internal bool IsInsideForEach => m_InsideForEach != 0;

        internal struct InsideForEach : IDisposable
        {
            EntityManager m_Manager;

            public InsideForEach(EntityManager manager)
            {
                m_Manager = manager;
                ++m_Manager.m_InsideForEach;
            }

            public void Dispose()
                => --m_Manager.m_InsideForEach;
        }
#endif

        internal EntityDataManager* Entities
        {
            get { return m_Entities; }
        }

        internal ComponentJobSafetyManager* ComponentJobSafetyManager
        {
            get { return m_ComponentJobSafetyManager; }
        }

        internal EntityGroupManager GroupManager
        {
            get { return m_GroupManager; }
        }

        internal ArchetypeManager ArchetypeManager
        {
            get { return m_ArchetypeManager; }
        }

        /// <summary>
        /// The latest entity generational version.
        /// </summary>
        /// <value>This is the version number that is assigned to a new entity. See <see cref="Entity.Version"/>.</value>
        public int Version => IsCreated ? m_Entities->Version : 0;

        /// <summary>
        /// A counter that increments after every system update.
        /// </summary>
        /// <remarks>
        /// The ECS framework uses the GlobalSystemVersion to track changes in a conservative, efficient fashion.
        /// Changes are recorded per component per chunk.
        /// </remarks>
        /// <seealso cref="ArchetypeChunk.DidChange"/>
        /// <seealso cref="ChangedFilterAttribute"/>
        public uint GlobalSystemVersion => IsCreated ? Entities->GlobalSystemVersion : 0;

        /// <summary>
        /// Reports whether the EntityManager has been initialized yet.
        /// </summary>
        /// <value>True, if the EntityManager's OnCreateManager() function has finished.</value>
        public bool IsCreated => m_Entities != null;

        /// <summary>
        /// The capacity of the internal entities array.
        /// </summary>
        /// <value>The number of entities the array can hold before it must be resized.</value>
        /// <remarks>
        /// The entities array automatically resizes itself when the entity count approaches the capacity.
        /// You should rarely need to set this value directly.
        ///
        /// **Important:** when you set this value (or when the array automatically resizes), the EntityManager
        /// first ensures that all Jobs finish. This can prevent the Job scheduler from utilizing available CPU
        /// cores and threads, resulting in a temporary performance drop.
        /// </remarks>
        public int EntityCapacity
        {
            get { return Entities->Capacity; }
            set
            {
                BeforeStructuralChange();
                Entities->Capacity = value;
            }
        }

        /// <summary>
        /// The Job dependencies of the exclusive entity transaction.
        /// </summary>
        /// <value></value>
        public JobHandle ExclusiveEntityTransactionDependency
        {
            get { return ComponentJobSafetyManager->ExclusiveTransactionDependency; }
            set { ComponentJobSafetyManager->ExclusiveTransactionDependency = value; }
        }

        EntityManagerDebug m_Debug;

        /// <summary>
        /// An object providing debugging information and operations.
        /// </summary>
        public EntityManagerDebug Debug => m_Debug ?? (m_Debug = new EntityManagerDebug(this));

        internal EntityManager(World world)
        {
            TypeManager.Initialize();

            m_World = world;

            m_Entities = (EntityDataManager*) UnsafeUtility.Malloc(sizeof(EntityDataManager), 64, Allocator.Persistent);
            m_Entities->OnCreate();

            m_SharedComponentManager = new SharedComponentDataManager();

            m_ComponentJobSafetyManager = (ComponentJobSafetyManager*) UnsafeUtility.Malloc(sizeof(ComponentJobSafetyManager), 64, Allocator.Persistent);
            m_ComponentJobSafetyManager->OnCreate();

            m_GroupManager = new EntityGroupManager(m_ComponentJobSafetyManager);

            m_ArchetypeManager = new ArchetypeManager(m_SharedComponentManager, m_Entities, m_GroupManager);

            m_ExclusiveEntityTransaction = new ExclusiveEntityTransaction(ArchetypeManager, m_GroupManager,
                m_SharedComponentManager, Entities);

            m_UniversalQuery = CreateEntityQuery(
                new EntityQueryDesc
                {
                    Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabled
                }
            );
        }

        internal void DestroyInstance()
        {
            EndExclusiveEntityTransaction();

            m_ComponentJobSafetyManager->PreDisposeCheck();

            m_UniversalQuery.Dispose();
            m_UniversalQuery = null;

            m_ComponentJobSafetyManager->Dispose();
            UnsafeUtility.Free(m_ComponentJobSafetyManager, Allocator.Persistent);
            m_ComponentJobSafetyManager = null;

            m_Entities->OnDestroy();
            UnsafeUtility.Free(m_Entities, Allocator.Persistent);
            m_Entities = null;
            m_ArchetypeManager.Dispose();
            m_ArchetypeManager = null;
            m_GroupManager.Dispose();
            m_GroupManager = null;
            m_ExclusiveEntityTransaction.OnDestroy();

            m_SharedComponentManager.Dispose();

            m_World = null;
            m_Debug = null;

            TypeManager.Shutdown();
        }

        private EntityManager()
        {
            // for tests only
        }

        internal static EntityManager CreateEntityManagerInUninitializedState()
        {
            return new EntityManager();
        }

        internal static int FillSortedArchetypeArray(ComponentTypeInArchetype* dst, ComponentType* requiredComponents, int count)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (count + 1 > 1024)
                throw new System.ArgumentException($"Archetypes can't hold more than 1024 components");
#endif

            dst[0] = new ComponentTypeInArchetype(ComponentType.ReadWrite<Entity>());
            for (var i = 0; i < count; ++i)
                SortingUtilities.InsertSorted(dst, i + 1, requiredComponents[i]);
            return count + 1;
        }

        /// <summary>
        /// Creates a EntityQuery from an array of component types.
        /// </summary>
        /// <param name="requiredComponents">An array containing the component types.</param>
        /// <returns>The EntityQuery derived from the specified array of component types.</returns>
        /// <seealso cref="EntityQueryDesc"/>
        public EntityQuery CreateEntityQuery(params ComponentType[] requiredComponents)
        {
            fixed (ComponentType* requiredComponentsPtr = requiredComponents)
            {
                return m_GroupManager.CreateEntityGroup(ArchetypeManager, Entities, requiredComponentsPtr, requiredComponents.Length);
            }
        }
        internal EntityQuery CreateEntityQuery(ComponentType* requiredComponents, int count)
        {
            return m_GroupManager.CreateEntityGroup(ArchetypeManager, Entities, requiredComponents, count);
        }
        /// <summary>
        /// Creates a EntityQuery from an EntityQueryDesc.
        /// </summary>
        /// <param name="queriesDesc">A queryDesc identifying a set of component types.</param>
        /// <returns>The EntityQuery corresponding to the queryDesc.</returns>
        public EntityQuery CreateEntityQuery(params EntityQueryDesc[] queriesDesc)
        {
            return m_GroupManager.CreateEntityGroup(ArchetypeManager, Entities, queriesDesc);
        }

        internal EntityArchetype CreateArchetype(ComponentType* types, int count)
        {
            ComponentTypeInArchetype* typesInArchetype = stackalloc ComponentTypeInArchetype[count + 1];
            var cachedComponentCount = FillSortedArchetypeArray(typesInArchetype, types, count);

            // Lookup existing archetype (cheap)
            EntityArchetype entityArchetype;
            entityArchetype.Archetype = ArchetypeManager.GetExistingArchetype(typesInArchetype, cachedComponentCount);
            if (entityArchetype.Archetype != null)
                return entityArchetype;

            // Creating an archetype invalidates all iterators / jobs etc
            // because it affects the live iteration linked lists...
            BeforeStructuralChange();

            entityArchetype.Archetype = ArchetypeManager.GetOrCreateArchetype(typesInArchetype,
                cachedComponentCount, m_GroupManager);
            return entityArchetype;
        }

        /// <summary>
        /// Creates an archetype from a set of component types.
        /// </summary>
        /// <remarks>
        /// Creates a new archetype in the ECS framework's internal type registry, unless the archetype already exists.
        /// </remarks>
        /// <param name="types">The component types to include as part of the archetype.</param>
        /// <returns>The EntityArchetype object for the archetype.</returns>
        public EntityArchetype CreateArchetype(params ComponentType[] types)
        {
            fixed (ComponentType* typesPtr = types)
            {
                return CreateArchetype(typesPtr, types.Length);
            }
        }

        /// <summary>
        /// Creates a set of entities of the specified archetype.
        /// </summary>
        /// <remarks>Fills the [NativeArray](https://docs.unity3d.com/ScriptReference/Unity.Collections.NativeArray_1.html)
        /// object assigned to the `entities` parameter with the Entity objects of the created entities. Each entity
        /// has the components specified by the <see cref="EntityArchetype"/> object assigned
        /// to the `archetype` parameter. The EntityManager adds these entities to the <see cref="World"/> entity list. Use the
        /// Entity objects in the array for further processing, such as setting the component values.</remarks>
        /// <param name="archetype">The archetype defining the structure for the new entities.</param>
        /// <param name="entities">An array to hold the Entity objects needed to access the new entities.
        /// The length of the array determines how many entities are created.</param>
        public void CreateEntity(EntityArchetype archetype, NativeArray<Entity> entities)
        {
            CreateEntityInternal(archetype, (Entity*) entities.GetUnsafePtr(), entities.Length);
        }

        /// <summary>
        /// Protects a chunk, and the entities within it, from structural changes.
        /// </summary>
        /// <remarks>
        /// When locked, entities cannot be added to or removed from the chunk; components cannot be added to or
        /// removed from the entities in the chunk; the values of shared components cannot be changed; and entities
        /// in the chunk cannot be destroyed. You can change the values of components, other than shared components.
        ///
        /// Call <see cref="UnlockChunk(ArchetypeChunk)"/> to unlock the chunk.
        ///
        /// You can lock a chunk temporarily and then unlock it, or you can lock it for the lifespan of your application.
        /// For example, if you have a gameboard with a fixed number of tiles, you may want the entities representing
        /// those tiles in a specific order. Locking the chunk prevents the ECS framework from rearranging them once you
        /// have set the desired order.
        ///
        /// Use <see cref="SwapComponents"/> to re-order entities in a chunk.
        /// </remarks>
        /// <param name="chunk">The chunk to lock.</param>
        public void LockChunk(ArchetypeChunk chunk)
        {
            LockChunksInternal(&chunk, 1, ChunkFlags.Locked);
        }

        /// <summary>
        /// Locks a set of chunks.
        /// </summary>
        /// <param name="chunks">An array of chunks to lock.</param>
        /// <seealso cref="EntityManager.LockChunk(ArchetypeChunk"/>
        public void LockChunk(NativeArray<ArchetypeChunk> chunks)
        {
            LockChunksInternal(chunks.GetUnsafePtr(), chunks.Length, ChunkFlags.Locked);
        }


        /// <summary>
        /// Unlocks a chunk
        /// </summary>
        /// <param name="chunk">The chunk to unlock.</param>
        public void UnlockChunk(ArchetypeChunk chunk)
        {
            UnlockChunksInternal(&chunk, 1, ChunkFlags.Locked);
        }

        /// <summary>
        /// Unlocks a set of chunks.
        /// </summary>
        /// <param name="chunks">An array of chunks to unlock.</param>
        public void UnlockChunk(NativeArray<ArchetypeChunk> chunks)
        {
            UnlockChunksInternal(chunks.GetUnsafePtr(), chunks.Length, ChunkFlags.Locked);
        }


        public void LockChunkOrder(EntityQuery query)
        {
            using (var chunks = query.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                LockChunksInternal(chunks.GetUnsafePtr(), chunks.Length, ChunkFlags.LockedEntityOrder);
            }
        }

        public void LockChunkOrder(ArchetypeChunk chunk)
        {
            LockChunksInternal(&chunk, 1, ChunkFlags.LockedEntityOrder);
        }


        public void UnlockChunkOrder(EntityQuery query)
        {
            using (var chunks = query.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                UnlockChunksInternal(chunks.GetUnsafePtr(), chunks.Length, ChunkFlags.LockedEntityOrder);
            }
        }

        public void UnlockChunkOrder(ArchetypeChunk chunk)
        {
            UnlockChunksInternal(&chunk, 1, ChunkFlags.LockedEntityOrder);
        }

        internal void UnlockChunksInternal(void* chunks, int count, ChunkFlags flags)
        {
            Entities->UnlockChunks((Chunk**)chunks, count, flags);
        }

        internal void LockChunksInternal(void* chunks, int count, ChunkFlags flags)
        {
            Entities->LockChunks((Chunk**)chunks, count, flags);
        }

        /// <summary>
        /// Creates a set of chunks containing the specified number of entities having the specified archetype.
        /// </summary>
        /// <remarks>
        /// The EntityManager creates enough chunks to hold the required number of entities.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating these chunks and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="archetype">The archetype for the chunk and entities.</param>
        /// <param name="chunks">An empty array to receive the created chunks.</param>
        /// <param name="entityCount">The number of entities to create.</param>
        public void CreateChunk(EntityArchetype archetype, NativeArray<ArchetypeChunk> chunks, int entityCount)
        {
            CreateChunkInternal(archetype, (ArchetypeChunk*) chunks.GetUnsafePtr(), entityCount);
        }

        /// <summary>
        /// Gets the chunk in which the specified entity is stored.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns>The chunk containing the entity.</returns>
        public ArchetypeChunk GetChunk(Entity entity)
        {
            var chunk = Entities->GetComponentChunk(entity);
            return new ArchetypeChunk {m_Chunk = chunk};
        }

        /// <summary>
        /// Creates an entity having the specified archetype.
        /// </summary>
        /// <remarks>
        /// The EntityManager creates the entity in the first available chunk with the matching archetype that has
        /// enough space.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="archetype">The archetype for the new entity.</param>
        /// <returns>The Entity object that you can use to access the entity.</returns>
        public Entity CreateEntity(EntityArchetype archetype)
        {
            Entity entity;
            CreateEntityInternal(archetype, &entity, 1);
            return entity;
        }

        /// <summary>
        /// Creates an entity having components of the specified types.
        /// </summary>
        /// <remarks>
        /// The EntityManager creates the entity in the first available chunk with the matching archetype that has
        /// enough space.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="types">The types of components to add to the new entity.</param>
        /// <returns>The Entity object that you can use to access the entity.</returns>
        public Entity CreateEntity(params ComponentType[] types)
        {
            return CreateEntity(CreateArchetype(types));
        }

        public Entity CreateEntity()
        {
            BeforeStructuralChange();
            Entity entity;
            Entities->CreateEntities(ArchetypeManager, ArchetypeManager.GetEntityOnlyArchetype(GroupManager), &entity, 1);
            return entity;
        }

        internal void CreateEntityInternal(EntityArchetype archetype, Entity* entities, int count)
        {
            BeforeStructuralChange();
            Entities->CreateEntities(ArchetypeManager, archetype.Archetype, entities, count);
        }

        internal void CreateChunkInternal(EntityArchetype archetype, ArchetypeChunk* chunks, int entityCount)
        {
            BeforeStructuralChange();
            Entities->CreateChunks(ArchetypeManager, archetype.Archetype, chunks, entityCount);
        }

        NativeArray<Entity> GetTempEntityArray(EntityQuery query)
        {
            //@TODO: Using Allocator.Temp here throws an exception...
            var entityArray = query.ToEntityArray(Allocator.TempJob);
            return entityArray;
        }

        /// <summary>
        /// Destroy all entities having a common set of component types.
        /// </summary>
        /// <remarks>Since entities in the same chunk share the same component structure, this function effectively destroys
        /// the chunks holding any entities identified by the `entityQueryFilter` parameter.</remarks>
        /// <param name="entityQueryFilter">Defines the components an entity must have to qualify for destruction.</param>
        public void DestroyEntity(EntityQuery entityQueryFilter)
        {
            //@TODO: When destroying entities with entityQueryFilter we assume that any LinkedEntityGroup also get destroyed
            //       We should have some sort of validation that everything is included, and either give an error message or have a fast enough path to handle it...

            //@TODO: Locked checks...

            Profiler.BeginSample("DestroyEntity(EntityQuery entityQueryFilter)");

            Profiler.BeginSample("GetAllMatchingChunks");
            using (var chunks = entityQueryFilter.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                Profiler.EndSample();

                Profiler.BeginSample("EditorOnlyChecks");
                Entities->AssertCanDestroy(chunks);
                Profiler.EndSample();

                Profiler.BeginSample("DeleteChunks");
                EntityDataManager.DeleteChunks(chunks, Entities, ArchetypeManager, m_SharedComponentManager);
                Profiler.EndSample();
            }

            Profiler.EndSample();
        }

        /// <summary>
        /// Destroys all entities in an array.
        /// </summary>
        /// <remarks>
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before destroying the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entities">An array containing the Entity objects of the entities to destroy.</param>
        public void DestroyEntity(NativeArray<Entity> entities)
        {
            DestroyEntityInternal((Entity*) entities.GetUnsafeReadOnlyPtr(), entities.Length);
        }

        /// <summary>
        /// Destroys all entities in a slice of an array.
        /// </summary>
        /// <remarks>
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before destroying the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entities">The slice of an array containing the Entity objects of the entities to destroy.</param>
        public void DestroyEntity(NativeSlice<Entity> entities)
        {
            DestroyEntityInternal((Entity*) entities.GetUnsafeReadOnlyPtr(), entities.Length);
        }

        /// <summary>
        /// Destroys an entity.
        /// </summary>
        /// <remarks>
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before destroying the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The Entity object of the entity to destroy.</param>
        public void DestroyEntity(Entity entity)
        {
            DestroyEntityInternal(&entity, 1);
        }

        internal void DestroyEntityInternal(Entity* entities, int count)
        {
            BeforeStructuralChange();
            Entities->AssertCanDestroy(entities, count);
            EntityDataManager.TryRemoveEntityId(entities, count, Entities, ArchetypeManager, m_SharedComponentManager);
        }

#if UNITY_EDITOR
        /// <summary>
        /// Gets the name assigned to an entity.
        /// </summary>
        /// <remarks>For performance, entity names only exist when running in the Unity Editor.</remarks>
        /// <param name="entity">The Entity object of the entity of interest.</param>
        /// <returns>The entity name.</returns>
        public string GetName(Entity entity)
        {
            return Entities->GetName(entity);
        }

        /// <summary>
        /// Sets the name of an entity.
        /// </summary>
        /// <remarks>For performance, entity names only exist when running in the Unity Editor.</remarks>
        /// <param name="entity">The Entity object of the entity to name.</param>
        /// <param name="name">The name to assign.</param>
        public void SetName(Entity entity, string name)
        {
            Entities->SetName(entity, name);
        }
#endif

        // @TODO Point to documentation for multithreaded way to check Entity validity.
        /// <summary>
        /// Reports whether an Entity object is still valid.
        /// </summary>
        /// <remarks>
        /// An Entity object does not contain a reference to its entity. Instead, the Entity struct contains an index
        /// and a generational version number. When an entity is destroyed, the EntityManager increments the version
        /// of the entity within the internal array of entities. The index of a destroyed entity is recycled when a
        /// new entity is created.
        ///
        /// After an entity is destroyed, any existing Entity objects will still contain the
        /// older version number. This function compares the version numbers of the specified Entity object and the
        /// current version of the entity recorded in the entities array. If the versions are different, the Entity
        /// object no longer refers to an existing entity and cannot be used.
        /// </remarks>
        /// <param name="entity">The Entity object to check.</param>
        /// <returns>True, if <see cref="Entity.Version"/> matches the version of the current entity at
        /// <see cref="Entity.Index"/> in the entities array.</returns>
        public bool Exists(Entity entity)
        {
            return Entities->Exists(entity);
        }

        /// <summary>
        /// Checks whether an entity has a specific type of component.
        /// </summary>
        /// <remarks>Always returns false for an entity that has been destroyed.</remarks>
        /// <param name="entity">The Entity object.</param>
        /// <typeparam name="T">The data type of the component.</typeparam>
        /// <returns>True, if the specified entity has the component.</returns>
        public bool HasComponent<T>(Entity entity)
        {
            return Entities->HasComponent(entity, ComponentType.ReadWrite<T>());
        }

        /// <summary>
        /// Checks whether an entity has a specific type of component.
        /// </summary>
        /// <remarks>Always returns false for an entity that has been destroyed.</remarks>
        /// <param name="entity">The Entity object.</param>
        /// <param name="type">The data type of the component.</param>
        /// <returns>True, if the specified entity has the component.</returns>
        public bool HasComponent(Entity entity, ComponentType type)
        {
            return Entities->HasComponent(entity, type);
        }

        /// <summary>
        /// Checks whether the chunk containing an entity has a specific type of component.
        /// </summary>
        /// <remarks>Always returns false for an entity that has been destroyed.</remarks>
        /// <param name="entity">The Entity object.</param>
        /// <typeparam name="T">The data type of the chunk component.</typeparam>
        /// <returns>True, if the chunk containing the specified entity has the component.</returns>
        public bool HasChunkComponent<T>(Entity entity)
        {
            return Entities->HasComponent(entity, ComponentType.ChunkComponent<T>());
        }

        /// <summary>
        /// Clones an entity.
        /// </summary>
        /// <remarks>
        /// The new entity has the same archetype and component values as the original.
        ///
        /// If the source entity has a <see cref="LinkedEntityGroup"/> component, the entire group is cloned as a new
        /// set of entities.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating the entity and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="srcEntity">The entity to clone</param>
        /// <returns>The Entity object for the new entity.</returns>
        public Entity Instantiate(Entity srcEntity)
        {
            Entity entity;
            InstantiateInternal(srcEntity, &entity, 1);
            return entity;
        }

        /// <summary>
        /// Makes multiple clones of an entity.
        /// </summary>
        /// <remarks>
        /// The new entities have the same archetype and component values as the original.
        ///
        /// If the source entity has a <see cref="LinkedEntityGroup"/> component, the entire group is cloned as a new
        /// set of entities.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating these entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="srcEntity">The entity to clone</param>
        /// <param name="outputEntities">An array to receive the Entity objects of the root entity in each clone.
        /// The length of this array determines the number of clones.</param>
        public void Instantiate(Entity srcEntity, NativeArray<Entity> outputEntities)
        {
            InstantiateInternal(srcEntity, (Entity*) outputEntities.GetUnsafePtr(), outputEntities.Length);
        }

        internal void InstantiateInternal(Entity srcEntity, Entity* outputEntities, int count)
        {
            BeforeStructuralChange();
            Entities->AssertEntitiesExist(&srcEntity, 1);
            Entities->InstantiateEntities(ArchetypeManager, m_SharedComponentManager, srcEntity, outputEntities, count);
        }

        /// <summary>
        /// Adds a component to an entity.
        /// </summary>
        /// <remarks>
        /// Adding a component changes the entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// The added component has the default values for the type.
        ///
        /// If the <see cref="Entity"/> object refers to an entity that has been destroyed, this function throws an ArgumentError
        /// exception.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding thes component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The Entity object.</param>
        /// <param name="componentType">The type of component to add.</param>
        public void AddComponent(Entity entity, ComponentType componentType)
        {
            AddComponent(entity, componentType, true);
        }

        internal void AddComponent(Entity entity, ComponentType componentType, bool ignoreDuplicateAdd)
        {
            if (ignoreDuplicateAdd && HasComponent(entity, componentType))
                return;

            BeforeStructuralChange();
            Entities->AssertCanAddComponent(entity, componentType);
            Entities->AddComponent(entity, componentType, ArchetypeManager, m_SharedComponentManager, m_GroupManager);
        }

        /// <summary>
        /// Adds a component to a set of entities defined by a EntityQuery.
        /// </summary>
        /// <remarks>
        /// Adding a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// The added components have the default values for the type.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entityQuery">The EntityQuery defining the entities to modify.</param>
        /// <param name="componentType">The type of component to add.</param>
        public void AddComponent(EntityQuery entityQuery, ComponentType componentType)
        {
            using (var chunks = entityQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                if (chunks.Length == 0)
                    return;
                BeforeStructuralChange();
                Entities->AssertCanAddComponent(chunks, componentType);
                Entities->AddComponent(chunks, componentType, ArchetypeManager, m_GroupManager);
            }
        }

        //@TODO: optimize for batch
        /// <summary>
        /// Adds a component to a set of entities.
        /// </summary>
        /// <remarks>
        /// Adding a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// The added components have the default values for the type.
        ///
        /// If an <see cref="Entity"/> object in the `entities` array refers to an entity that has been destroyed, this function
        /// throws an ArgumentError exception.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before creating these chunks and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entities">An array of Entity objects.</param>
        /// <param name="componentType">The type of component to add.</param>
        public void AddComponent(NativeArray<Entity> entities, ComponentType componentType)
        {
            for(int i =0;i != entities.Length;i++)
                AddComponent(entities[i], componentType);
        }

        /// <summary>
        /// Adds a set of component to an entity.
        /// </summary>
        /// <remarks>
        /// Adding components changes the entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// The added components have the default values for the type.
        ///
        /// If the <see cref="Entity"/> object refers to an entity that has been destroyed, this function throws an ArgumentError
        /// exception.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding these components and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity to modify.</param>
        /// <param name="types">The types of components to add.</param>
        public void AddComponents(Entity entity, ComponentTypes types)
        {
            BeforeStructuralChange();
            Entities->AssertCanAddComponents(entity, types);
            Entities->AddComponents(entity, types, ArchetypeManager, m_SharedComponentManager, m_GroupManager);
        }

        /// <summary>
        /// Removes a component from an entity.
        /// </summary>
        /// <remarks>
        /// Removing a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity to modify.</param>
        /// <param name="type">The type of component to remove.</param>
        public void RemoveComponent(Entity entity, ComponentType type)
        {
            BeforeStructuralChange();
            EntityDataManager.RemoveComponent(entity, type, Entities, ArchetypeManager, m_SharedComponentManager, m_GroupManager);
        }

        /// <summary>
        /// Removes a component from a set of entities defined by a EntityQuery.
        /// </summary>
        /// <remarks>
        /// Removing a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entityQuery">The EntityQuery defining the entities to modify.</param>
        /// <param name="componentType">The type of component to remove.</param>
        public void RemoveComponent(EntityQuery entityQuery, ComponentType componentType)
        {
            using (var chunks = entityQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                if (chunks.Length == 0)
                    return;
                BeforeStructuralChange();
                Entities->AssertCanRemoveComponent(chunks, componentType);
                Entities->RemoveComponent(chunks, componentType, ArchetypeManager, m_GroupManager, m_SharedComponentManager);
            }
        }

        public void RemoveComponent(EntityQuery entityQueryFilter, ComponentTypes types)
        {
            if (entityQueryFilter.CalculateLength() == 0)
                return;

            // @TODO: Opportunity to do all components in batch on a per chunk basis.
            for (int i = 0; i != types.Length; i++)
                RemoveComponent(entityQueryFilter, types.GetComponentType(i));
        }

        //@TODO: optimize for batch
        /// <summary>
        /// Removes a component from a set of entities.
        /// </summary>
        /// <remarks>
        /// Removing a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entities">An array identifying the entities to modify.</param>
        /// <param name="type">The type of component to remove.</param>
        public void RemoveComponent(NativeArray<Entity> entities, ComponentType type)
        {
            for(int i =0;i != entities.Length;i++)
                RemoveComponent(entities[i], type);
        }

        /// <summary>
        /// Removes a component from an entity.
        /// </summary>
        /// <remarks>
        /// Removing a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of component to remove.</typeparam>
        public void RemoveComponent<T>(Entity entity)
        {
            RemoveComponent(entity, ComponentType.ReadWrite<T>());
        }

        /// <summary>
        /// Adds a component to an entity and set the value of that component.
        /// </summary>
        /// <remarks>
        /// Adding a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <param name="componentData">The data to set.</param>
        /// <typeparam name="T">The type of component.</typeparam>
        public void AddComponentData<T>(Entity entity, T componentData) where T : struct, IComponentData
        {
            var type = ComponentType.ReadWrite<T>();
            AddComponent(entity, type, type.IgnoreDuplicateAdd);
            if (!type.IsZeroSized)
                SetComponentData(entity, componentData);
        }

        /// <summary>
        /// Removes a chunk component from the specified entity.
        /// </summary>
        /// <remarks>
        /// A chunk component is common to all entities in a chunk. Removing the chunk component from an entity changes
        /// that entity's archetype and results in the entity being moved to a different chunk (that does not have the
        /// removed component).
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of component to remove.</typeparam>
        public void RemoveChunkComponent<T>(Entity entity)
        {
            RemoveComponent(entity, ComponentType.ChunkComponent<T>());
        }

        /// <summary>
        /// Adds a chunk component to the specified entity.
        /// </summary>
        /// <remarks>
        /// Adding a chunk component to an entity changes that entity's archetype and results in the entity being moved
        /// to a different chunk, either one that already has an archetype containing the chunk component or a new
        /// chunk.
        ///
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk. In either case, getting
        /// or setting the component reads or writes the same data.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of component, which must implement IComponentData.</typeparam>
        public void AddChunkComponentData<T>(Entity entity) where T : struct, IComponentData
        {
            AddComponent(entity, ComponentType.ChunkComponent<T>());
        }

        /// <summary>
        /// Adds a component to each of the chunks identified by a EntityQuery and set the component values.
        /// </summary>
        /// <remarks>
        /// This function finds all chunks whose archetype satisfies the EntityQuery and adds the specified
        /// component to them.
        ///
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entityQuery">The EntityQuery identifying the chunks to modify.</param>
        /// <param name="componentData">The data to set.</param>
        /// <typeparam name="T">The type of component, which must implement IComponentData.</typeparam>
        public void AddChunkComponentData<T>(EntityQuery entityQuery, T componentData) where T : struct, IComponentData
        {
            using (var chunks = entityQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                if (chunks.Length == 0)
                    return;
                BeforeStructuralChange();
                Entities->AssertCanAddChunkComponent(chunks, ComponentType.ChunkComponent<T>());
                Entities->AddChunkComponent<T>(chunks, componentData, ArchetypeManager, m_GroupManager);
            }
        }

        /// <summary>
        /// Removes a component from the chunks identified by a EntityQuery.
        /// </summary>
        /// <remarks>
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before removing the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entityQuery">The EntityQuery identifying the chunks to modify.</param>
        /// <typeparam name="T">The type of component to remove.</typeparam>
        public void RemoveChunkComponentData<T>(EntityQuery entityQuery)
        {
            using (var chunks = entityQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                if (chunks.Length == 0)
                    return;
                BeforeStructuralChange();
                Entities->RemoveComponent(chunks, ComponentType.ChunkComponent<T>(), ArchetypeManager, m_GroupManager, m_SharedComponentManager);
            }
        }

        /// <summary>
        /// Adds a dynamic buffer component to an entity.
        /// </summary>
        /// <remarks>
        /// A buffer component stores the number of elements inside the chunk defined by the [InternalBufferCapacity]
        /// attribute applied to the buffer element type declaration. Any additional elements are stored in a separate memory
        /// block that is managed by the EntityManager.
        ///
        /// Adding a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the buffer and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of buffer element. Must implement IBufferElementData.</typeparam>
        /// <returns>The buffer.</returns>
        /// <seealso cref="InternalBufferCapacityAttribute"/>
        public DynamicBuffer<T> AddBuffer<T>(Entity entity) where T : struct, IBufferElementData
        {
            AddComponent(entity, ComponentType.ReadWrite<T>());
            return GetBuffer<T>(entity);
        }

        internal ComponentDataFromEntity<T> GetComponentDataFromEntity<T>(bool isReadOnly = false)
            where T : struct, IComponentData
        {
            var typeIndex = TypeManager.GetTypeIndex<T>();
            return GetComponentDataFromEntity<T>(typeIndex, isReadOnly);
        }

        internal ComponentDataFromEntity<T> GetComponentDataFromEntity<T>(int typeIndex, bool isReadOnly)
            where T : struct, IComponentData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new ComponentDataFromEntity<T>(typeIndex, Entities,
                ComponentJobSafetyManager->GetSafetyHandle(typeIndex, isReadOnly));
#else
            return new ComponentDataFromEntity<T>(typeIndex, m_Entities);
#endif
        }

        internal BufferFromEntity<T> GetBufferFromEntity<T>(bool isReadOnly = false)
            where T : struct, IBufferElementData
        {
            return GetBufferFromEntity<T>(TypeManager.GetTypeIndex<T>(), isReadOnly);
        }

        internal BufferFromEntity<T> GetBufferFromEntity<T>(int typeIndex, bool isReadOnly = false)
            where T : struct, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new BufferFromEntity<T>(typeIndex, Entities, isReadOnly,
                ComponentJobSafetyManager->GetSafetyHandle(typeIndex, isReadOnly),
                ComponentJobSafetyManager->GetBufferSafetyHandle(typeIndex));
#else
            return new BufferFromEntity<T>(typeIndex, m_Entities, isReadOnly);
#endif
        }

        /// <summary>
        /// Gets the value of a component for an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of component to retrieve.</typeparam>
        /// <returns>A struct of type T containing the component value.</returns>
        /// <exception cref="ArgumentException">Thrown if the component type has no fields.</exception>
        public T GetComponentData<T>(Entity entity) where T : struct, IComponentData
        {
            var typeIndex = TypeManager.GetTypeIndex<T>();

            Entities->AssertEntityHasComponent(entity, typeIndex);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ComponentType.FromTypeIndex(typeIndex).IsZeroSized)
                throw new System.ArgumentException($"GetComponentData<{typeof(T)}> can not be called with a zero sized component.");
#endif

            ComponentJobSafetyManager->CompleteWriteDependency(typeIndex);

            var ptr = Entities->GetComponentDataWithTypeRO(entity, typeIndex);

            T value;
            UnsafeUtility.CopyPtrToStructure(ptr, out value);
            return value;
        }

        /// <summary>
        /// Sets the value of a component of an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="componentData">The data to set.</param>
        /// <typeparam name="T">The component type.</typeparam>
        /// <exception cref="ArgumentException">Thrown if the component type has no fields.</exception>
        public void SetComponentData<T>(Entity entity, T componentData) where T : struct, IComponentData
        {
            var typeIndex = TypeManager.GetTypeIndex<T>();

            Entities->AssertEntityHasComponent(entity, typeIndex);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ComponentType.FromTypeIndex(typeIndex).IsZeroSized)
                throw new System.ArgumentException($"SetComponentData<{typeof(T)}> can not be called with a zero sized component.");
#endif

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

            var ptr = Entities->GetComponentDataWithTypeRW(entity, typeIndex, Entities->GlobalSystemVersion);
            UnsafeUtility.CopyStructureToPtr(ref componentData, ptr);
        }

        /// <summary>
        /// Gets the value of a chunk component.
        /// </summary>
        /// <remarks>
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk.
        /// </remarks>
        /// <param name="chunk">The chunk.</param>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>A struct of type T containing the component value.</returns>
        /// <exception cref="ArgumentException">Thrown if the ArchetypeChunk object is invalid.</exception>
        public T GetChunkComponentData<T>(ArchetypeChunk chunk) where T : struct, IComponentData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (chunk.Invalid())
                throw new System.ArgumentException($"GetChunkComponentData<{typeof(T)}> can not be called with an invalid archetype chunk.");
#endif
            var metaChunkEntity = chunk.m_Chunk->metaChunkEntity;
            return GetComponentData<T>(metaChunkEntity);
        }

        /// <summary>
        /// Gets the value of chunk component for the chunk containing the specified entity.
        /// </summary>
        /// <remarks>
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>A struct of type T containing the component value.</returns>
        public T GetChunkComponentData<T>(Entity entity) where T : struct, IComponentData
        {
            Entities->AssertEntitiesExist(&entity, 1);
            var chunk = Entities->GetComponentChunk(entity);
            var metaChunkEntity = chunk->metaChunkEntity;
            return GetComponentData<T>(metaChunkEntity);
        }

        /// <summary>
        /// Sets the value of a chunk component.
        /// </summary>
        /// <remarks>
        /// A chunk component is common to all entities in a chunk. You can access a chunk <see cref="IComponentData"/>
        /// instance through either the chunk itself or through an entity stored in that chunk.
        /// </remarks>
        /// <param name="chunk">The chunk to modify.</param>
        /// <param name="componentValue">The component data to set.</param>
        /// <typeparam name="T">The component type.</typeparam>
        /// <exception cref="ArgumentException">Thrown if the ArchetypeChunk object is invalid.</exception>
        public void SetChunkComponentData<T>(ArchetypeChunk chunk, T componentValue) where T : struct, IComponentData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (chunk.Invalid())
                throw new System.ArgumentException($"SetChunkComponentData<{typeof(T)}> can not be called with an invalid archetype chunk.");
#endif
            var metaChunkEntity = chunk.m_Chunk->metaChunkEntity;
            SetComponentData<T>(metaChunkEntity, componentValue);
        }

        /// <summary>
        /// Adds a managed [UnityEngine.Component](https://docs.unity3d.com/ScriptReference/Component.html)
        /// object to an entity.
        /// </summary>
        /// <remarks>
        /// Accessing data in a managed object forfeits many opportunities for increased performance. Adding
        /// managed objects to an entity should be avoided or used sparingly.
        ///
        /// Adding a component changes an entity's archetype and results in the entity being moved to a different
        /// chunk.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the object and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity to modify.</param>
        /// <param name="componentData">An object inheriting UnityEngine.Component.</param>
        /// <exception cref="ArgumentNullException">If the componentData object is not an instance of
        /// UnityEngine.Component.</exception>
        public void AddComponentObject(Entity entity, object componentData)
        {
            #if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (componentData == null)
                throw new ArgumentNullException(nameof(componentData));
            #endif

            ComponentType type = componentData.GetType();

            AddComponent(entity, type);
            SetComponentObject(entity, type, componentData);
        }

        /// <summary>
        /// Gets the managed [UnityEngine.Component](https://docs.unity3d.com/ScriptReference/Component.html) object
        /// from an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of the managed object.</typeparam>
        /// <returns>The managed object, cast to type T.</returns>
        public T GetComponentObject<T>(Entity entity)
        {
            var componentType = ComponentType.ReadWrite<T>();

            Entities->AssertEntityHasComponent(entity, componentType.TypeIndex);

            Chunk* chunk;
            int chunkIndex;
            Entities->GetComponentChunk(entity, out chunk, out chunkIndex);
            return (T)ArchetypeManager.GetManagedObject(chunk, componentType, chunkIndex);
        }

        internal void SetComponentObject(Entity entity, ComponentType componentType, object componentObject)
        {
            Entities->AssertEntityHasComponent(entity, componentType.TypeIndex);

            Chunk* chunk;
            int chunkIndex;
            Entities->GetComponentChunk(entity, out chunk, out chunkIndex);
            ArchetypeManager.SetManagedObject(chunk, componentType, chunkIndex, componentObject);
        }

        /// <summary>
        /// Gets the number of shared components managed by this EntityManager.
        /// </summary>
        /// <returns>The shared component count</returns>
        public int GetSharedComponentCount()
        {
            return m_SharedComponentManager.GetSharedComponentCount();
        }

        /// <summary>
        /// Gets a list of all the unique instances of a shared component type.
        /// </summary>
        /// <remarks>
        /// All entities with the same archetype and the same values for a shared component are stored in the same set
        /// of chunks. This function finds the unique shared components existing across chunks and archetype and
        /// fills a list with copies of those components.
        /// </remarks>
        /// <param name="sharedComponentValues">A List<T> object to receive the unique instances of the
        /// shared component of type T.</param>
        /// <typeparam name="T">The type of shared component.</typeparam>
        public void GetAllUniqueSharedComponentData<T>(List<T> sharedComponentValues)
            where T : struct, ISharedComponentData
        {
            m_SharedComponentManager.GetAllUniqueSharedComponents(sharedComponentValues);
        }

        /// <summary>
        /// Gets a list of all unique shared components of the same type and a corresponding list of indices into the
        /// internal shared component list.
        /// </summary>
        /// <remarks>
        /// All entities with the same archetype and the same values for a shared component are stored in the same set
        /// of chunks. This function finds the unique shared components existing across chunks and archetype and
        /// fills a list with copies of those components and fills in a separate list with the indices of those components
        /// in the internal shared component list. You can use the indices to ask the same shared components directly
        /// by calling <see cref="GetSharedComponentData{T}(int)"/>, passing in the index. An index remains valid until
        /// the shared component order version changes. Check this version using
        /// <see cref="GetSharedComponentOrderVersion{T}(T)"/>.
        /// </remarks>
        /// <param name="sharedComponentValues"></param>
        /// <param name="sharedComponentIndices"></param>
        /// <typeparam name="T"></typeparam>
        public void GetAllUniqueSharedComponentData<T>(List<T> sharedComponentValues, List<int> sharedComponentIndices)
            where T : struct, ISharedComponentData
        {
            m_SharedComponentManager.GetAllUniqueSharedComponents(sharedComponentValues, sharedComponentIndices);
        }

        /// <summary>
        /// Gets a shared component from an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of shared component.</typeparam>
        /// <returns>A copy of the shared component.</returns>
        public T GetSharedComponentData<T>(Entity entity) where T : struct, ISharedComponentData
        {
            var typeIndex = TypeManager.GetTypeIndex<T>();
            Entities->AssertEntityHasComponent(entity, typeIndex);

            var sharedComponentIndex = Entities->GetSharedComponentDataIndex(entity, typeIndex);
            return m_SharedComponentManager.GetSharedComponentData<T>(sharedComponentIndex);
        }

        /// <summary>
        /// Gets a shared component by index.
        /// </summary>
        /// <remarks>
        /// The ECS framework maintains an internal list of unique shared components. You can get the components in this
        /// list, along with their indices using
        /// <see cref="GetAllUniqueSharedComponentData{T}(List{T},List{int})"/>. An
        /// index in the list is valid and points to the same shared component index as long as the shared component
        /// order version from <see cref="GetSharedComponentOrderVersion{T}(T)"/> remains the same.
        /// </remarks>
        /// <param name="sharedComponentIndex">The index of the shared component in the internal shared component
        /// list.</param>
        /// <typeparam name="T">The data type of the shared component.</typeparam>
        /// <returns>A copy of the shared component.</returns>
        public T GetSharedComponentData<T>(int sharedComponentIndex) where T : struct, ISharedComponentData
        {
            return m_SharedComponentManager.GetSharedComponentData<T>(sharedComponentIndex);
        }

        /// <summary>
        /// Adds a shared component to an entity.
        /// </summary>
        /// <remarks>
        /// The fields of the `componentData` parameter are assigned to the added shared component.
        ///
        /// Adding a component to an entity changes its archetype and results in the entity being moved to a
        /// different chunk. The entity moves to a chunk with other entities that have the same shared component values.
        /// A new chunk is created if no chunk with the same archetype and shared component values currently exists.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity.</param>
        /// <param name="componentData">An instance of the shared component having the values to set.</param>
        /// <typeparam name="T">The shared component type.</typeparam>
        public void AddSharedComponentData<T>(Entity entity, T componentData) where T : struct, ISharedComponentData
        {
            //TODO: optimize this (no need to move the entity to a new chunk twice)
            AddComponent(entity, ComponentType.ReadWrite<T>(), false);
            SetSharedComponentData(entity, componentData);
        }

        /// <summary>
        /// Adds a shared component to a set of entities defined by a EntityQuery.
        /// </summary>
        /// <remarks>
        /// The fields of the `componentData` parameter are assigned to all of the added shared components.
        ///
        /// Adding a component to an entity changes its archetype and results in the entity being moved to a
        /// different chunk. The entity moves to a chunk with other entities that have the same shared component values.
        /// A new chunk is created if no chunk with the same archetype and shared component values currently exists.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before adding the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entityQuery">The EntityQuery defining a set of entities to modify.</param>
        /// <param name="componentData">The data to set.</param>
        /// <typeparam name="T">The data type of the shared component.</typeparam>
        public void AddSharedComponentData<T>(EntityQuery entityQuery, T componentData) where T : struct, ISharedComponentData
        {
            var componentType = ComponentType.ReadWrite<T>();
            using (var chunks = entityQuery.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                if (chunks.Length == 0)
                    return;
                BeforeStructuralChange();
                var newSharedComponentDataIndex = m_SharedComponentManager.InsertSharedComponent(componentData);
                Entities->AssertCanAddComponent(chunks, componentType);
                Entities->AddSharedComponent(chunks, componentType, ArchetypeManager, m_GroupManager, newSharedComponentDataIndex);
                m_SharedComponentManager.AddReference(newSharedComponentDataIndex, chunks.Length-1);
            }
        }

        internal void AddSharedComponentDataBoxed(Entity entity, int typeIndex, int hashCode, object componentData)
        {
            //TODO: optimize this (no need to move the entity to a new chunk twice)
            AddComponent(entity, ComponentType.FromTypeIndex(typeIndex));
            SetSharedComponentDataBoxed(entity, typeIndex, hashCode, componentData);
        }

        /// <summary>
        /// Sets the shared component of an entity.
        /// </summary>
        /// <remarks>
        /// Changing a shared component value of an entity results in the entity being moved to a
        /// different chunk. The entity moves to a chunk with other entities that have the same shared component values.
        /// A new chunk is created if no chunk with the same archetype and shared component values currently exists.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before setting the component and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="entity">The entity</param>
        /// <param name="componentData">A shared component object containing the values to set.</param>
        /// <typeparam name="T">The shared component type.</typeparam>
        public void SetSharedComponentData<T>(Entity entity, T componentData) where T : struct, ISharedComponentData
        {
            BeforeStructuralChange();

            var typeIndex = TypeManager.GetTypeIndex<T>();
            Entities->AssertEntityHasComponent(entity, typeIndex);

            var newSharedComponentDataIndex = m_SharedComponentManager.InsertSharedComponent(componentData);
            Entities->SetSharedComponentDataIndex(ArchetypeManager, m_SharedComponentManager, entity, typeIndex,
                newSharedComponentDataIndex);
            m_SharedComponentManager.RemoveReference(newSharedComponentDataIndex);
        }

        internal void SetSharedComponentDataBoxed(Entity entity, int typeIndex, object componentData)
        {
            var hashCode = TypeManager.GetHashCode(componentData, typeIndex);
            SetSharedComponentDataBoxed(entity, typeIndex, hashCode, componentData);
        }

        internal void SetSharedComponentDataBoxed(Entity entity, int typeIndex, int hashCode, object componentData)
        {
            BeforeStructuralChange();

            Entities->AssertEntityHasComponent(entity, typeIndex);

            var newSharedComponentDataIndex = 0;
            if (componentData != null) // null means default
                newSharedComponentDataIndex = m_SharedComponentManager.InsertSharedComponentAssumeNonDefault(typeIndex,
                    hashCode, componentData);

            Entities->SetSharedComponentDataIndex(ArchetypeManager, m_SharedComponentManager, entity, typeIndex,
                newSharedComponentDataIndex);
            m_SharedComponentManager.RemoveReference(newSharedComponentDataIndex);
        }

        /// <summary>
        /// Gets the dynamic buffer of an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <typeparam name="T">The type of the buffer's elements.</typeparam>
        /// <returns>The DynamicBuffer object for accessing the buffer contents.</returns>
        /// <exception cref="ArgumentException">Thrown if T is an unsupported type.</exception>
        public DynamicBuffer<T> GetBuffer<T>(Entity entity) where T : struct, IBufferElementData
        {
            var typeIndex = TypeManager.GetTypeIndex<T>();

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            Entities->AssertEntityHasComponent(entity, typeIndex);
            if (!TypeManager.IsBuffer(typeIndex))
                throw new ArgumentException(
                    $"GetBuffer<{typeof(T)}> may not be IComponentData or ISharedComponentData; currently {TypeManager.GetTypeInfo<T>().Category}");
#endif

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

            BufferHeader* header = (BufferHeader*) Entities->GetComponentDataWithTypeRW(entity, typeIndex, Entities->GlobalSystemVersion);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new DynamicBuffer<T>(header, ComponentJobSafetyManager->GetSafetyHandle(typeIndex, false), ComponentJobSafetyManager->GetBufferSafetyHandle(typeIndex), false);
#else
            return new DynamicBuffer<T>(header);
#endif
        }

        internal void* GetBufferRawRW(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

            BufferHeader* header = (BufferHeader*)Entities->GetComponentDataWithTypeRW(entity, typeIndex, Entities->GlobalSystemVersion);

            return BufferHeader.GetElementPointer(header);
        }

        internal void* GetBufferRawRO(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

            BufferHeader* header = (BufferHeader*)Entities->GetComponentDataWithTypeRO(entity, typeIndex);

            return BufferHeader.GetElementPointer(header);
        }

        internal int GetBufferLength(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

            BufferHeader* header = (BufferHeader*)Entities->GetComponentDataWithTypeRO(entity, typeIndex);

            return header->Length;
        }

        internal uint GetChunkVersionHash(Entity entity)
        {
            var chunk = Entities->GetComponentChunk(entity);
            var typeCount = chunk->Archetype->TypesCount;

            uint hash = 0;
            for (int i = 0; i < typeCount; ++i)
            {
                hash += chunk->GetChangeVersion(i);
            }

            return hash;
        }

        /// <summary>
        /// Gets all the entities managed by this EntityManager.
        /// </summary>
        /// <remarks>
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before getting the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="allocator">The type of allocation for creating the NativeArray to hold the Entity objects.</param>
        /// <returns>An array of Entity objects referring to all the entities in the World.</returns>
        public NativeArray<Entity> GetAllEntities(Allocator allocator = Allocator.Temp)
        {
            BeforeStructuralChange();

            var chunks = GetAllChunks();
            var count = ArchetypeChunkArray.CalculateEntityCount(chunks);
            var array = new NativeArray<Entity>(count, allocator);
            var entityType = GetArchetypeChunkEntityType();
            var offset = 0;

            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                var entities = chunk.GetNativeArray(entityType);
                array.Slice(offset, entities.Length).CopyFrom(entities);
                offset += entities.Length;
            }

            chunks.Dispose();
            return array;
        }

        /// <summary>
        /// Gets all the chunks managed by this EntityManager.
        /// </summary>
        /// <remarks>
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before getting these chunks and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="allocator">The type of allocation for creating the NativeArray to hold the ArchetypeChunk
        /// objects.</param>
        /// <returns>An array of ArchetypeChunk objects referring to all the chunks in the <see cref="World"/>.</returns>
        public NativeArray<ArchetypeChunk> GetAllChunks(Allocator allocator = Allocator.TempJob)
        {
            BeforeStructuralChange();

            return m_UniversalQuery.CreateArchetypeChunkArray(allocator);
        }

        /// <summary>
        /// Gets an entity's component types.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <param name="allocator">The type of allocation for creating the NativeArray to hold the ComponentType
        /// objects.</param>
        /// <returns>An array of ComponentType containing all the types of components associated with the entity.</returns>
        public NativeArray<ComponentType> GetComponentTypes(Entity entity, Allocator allocator = Allocator.Temp)
        {
            Entities->AssertEntitiesExist(&entity, 1);

            var archetype = Entities->GetArchetype(entity);

            var components = new NativeArray<ComponentType>(archetype->TypesCount - 1, allocator);

            for (var i = 1; i < archetype->TypesCount; i++)
                components[i - 1] = archetype->Types[i].ToComponentType();

            return components;
        }

        internal void SetBufferRaw(Entity entity, int componentTypeIndex, BufferHeader* tempBuffer, int sizeInChunk)
        {
            Entities->AssertEntityHasComponent(entity, componentTypeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(componentTypeIndex);

            var ptr = Entities->GetComponentDataWithTypeRW(entity, componentTypeIndex, Entities->GlobalSystemVersion);

            BufferHeader.Destroy((BufferHeader*)ptr);

            UnsafeUtility.MemCpy(ptr, tempBuffer, sizeInChunk);
        }

        /// <summary>
        /// Gets the number of component types associated with an entity.
        /// </summary>
        /// <param name="entity">The entity.</param>
        /// <returns>The number of components.</returns>
        public int GetComponentCount(Entity entity)
        {
            Entities->AssertEntitiesExist(&entity, 1);
            var archetype = Entities->GetArchetype(entity);
            return archetype->TypesCount - 1;
        }

        internal int GetComponentTypeIndex(Entity entity, int index)
        {
            Entities->AssertEntitiesExist(&entity, 1);
            var archetype = Entities->GetArchetype(entity);

            if ((uint) index >= archetype->TypesCount) return -1;

            return archetype->Types[index + 1].TypeIndex;
        }

        internal void SetComponentDataRaw(Entity entity, int typeIndex, void* data, int size)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (TypeManager.GetTypeInfo(typeIndex).SizeInChunk != size)
                throw new System.ArgumentException($"SetComponentData<{TypeManager.GetType(typeIndex)}> can not be called with a zero sized component and must have same size as sizeof(T).");
#endif

            var ptr = Entities->GetComponentDataWithTypeRW(entity, typeIndex, Entities->GlobalSystemVersion);
            UnsafeUtility.MemCpy(ptr, data, size);
        }

        internal void* GetComponentDataRawRW(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (TypeManager.GetTypeInfo(typeIndex).IsZeroSized)
                throw new System.ArgumentException($"GetComponentData<{TypeManager.GetType(typeIndex)}> can not be called with a zero sized component.");
#endif


            var ptr = Entities->GetComponentDataWithTypeRW(entity, typeIndex, Entities->GlobalSystemVersion);
            return ptr;
        }

        internal void* GetComponentDataRawRO(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            ComponentJobSafetyManager->CompleteReadAndWriteDependency(typeIndex);

#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (TypeManager.GetTypeInfo(typeIndex).IsZeroSized)
                throw new System.ArgumentException($"GetComponentDataRawRO can not be called with a zero sized component.");
#endif


            var ptr = Entities->GetComponentDataWithTypeRO(entity, typeIndex);
            return ptr;
        }

        internal object GetSharedComponentData(Entity entity, int typeIndex)
        {
            Entities->AssertEntityHasComponent(entity, typeIndex);

            var sharedComponentIndex = Entities->GetSharedComponentDataIndex(entity, typeIndex);
            return m_SharedComponentManager.GetSharedComponentDataBoxed(sharedComponentIndex, typeIndex);
        }

        /// <summary>
        /// Gets the version number of the specified component type.
        /// </summary>
        /// <remarks>This version number is incremented each time there is a structural change involving the specified
        /// type of component. Such changes include creating or destroying entities that have this component and adding
        /// or removing the component type from an entity. Shared components are not covered by this version;
        /// see <see cref="GetSharedComponentOrderVersion{T}(T)"/>.
        ///
        /// Version numbers can overflow. To compare if one version is more recent than another use a calculation such as:
        ///
        /// <code>
        /// bool VersionBisNewer = (VersionB - VersionA) > 0;
        /// </code>
        /// </remarks>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>The current version number.</returns>
        public int GetComponentOrderVersion<T>()
        {
            return Entities->GetComponentTypeOrderVersion(TypeManager.GetTypeIndex<T>());
        }

        /// <summary>
        /// Gets the version number of the specified shared component.
        /// </summary>
        /// <remarks>
        /// This version number is incremented each time there is a structural change involving entities in the chunk of
        /// the specified shared component. Such changes include creating or destroying entities or anything that changes
        /// the archetype of an entity.
        ///
        /// Version numbers can overflow. To compare if one version is more recent than another use a calculation such as:
        ///
        /// <code>
        /// bool VersionBisNewer = (VersionB - VersionA) > 0;
        /// </code>
        /// </remarks>
        /// <param name="sharedComponent">The shared component instance.</param>
        /// <typeparam name="T">The shared component type.</typeparam>
        /// <returns>The current version number.</returns>
        public int GetSharedComponentOrderVersion<T>(T sharedComponent) where T : struct, ISharedComponentData
        {
            return m_SharedComponentManager.GetSharedComponentVersion(sharedComponent);
        }

        /// <summary>
        /// Begins an exclusive entity transaction, which allows you to make structural changes inside a Job.
        /// </summary>
        /// <remarks>
        /// <see cref="ExclusiveEntityTransaction"/> allows you to create & destroy entities from a job. The purpose is
        /// to enable procedural generation scenarios where instantiation on big scale must happen on jobs. As the
        /// name implies it is exclusive to any other access to the EntityManager.
        ///
        /// An exclusive entity transaction should be used on a manually created <see cref="World"/> that acts as a
        /// staging area to construct and setup entities.
        ///
        /// After the job has completed you can end the transaction and use
        /// <see cref="MoveEntitiesFrom(EntityManager)"/> to move the entities to an active <see cref="World"/>.
        /// </remarks>
        /// <returns>A transaction object that provides an functions for making structural changes.</returns>
        public ExclusiveEntityTransaction BeginExclusiveEntityTransaction()
        {
            ComponentJobSafetyManager->BeginExclusiveTransaction();
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            m_ExclusiveEntityTransaction.SetAtomicSafetyHandle(ComponentJobSafetyManager->ExclusiveTransactionSafety);
#endif
            return m_ExclusiveEntityTransaction;
        }

        /// <summary>
        /// Ends an exclusive entity transaction.
        /// </summary>
        /// <seealso cref="ExclusiveEntityTransaction"/>
        /// <seealso cref="BeginExclusiveEntityTransaction()"/>
        public void EndExclusiveEntityTransaction()
        {
            ComponentJobSafetyManager->EndExclusiveTransaction();
        }

        private void BeforeStructuralChange()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (ComponentJobSafetyManager->IsInTransaction)
                throw new InvalidOperationException(
                    "Access to EntityManager is not allowed after EntityManager.BeginExclusiveEntityTransaction(); has been called.");

            if (m_InsideForEach != 0)
                throw new InvalidOperationException("EntityManager.AddComponent/RemoveComponent/CreateEntity/DestroyEntity are not allowed during Entities.ForEach. Please use PostUpdateCommandBuffer to delay applying those changes until after ForEach.");

#endif
            ComponentJobSafetyManager->CompleteAllJobsAndInvalidateArrays();
        }

        //@TODO: Not clear to me what this method is really for...
        /// <summary>
        /// Waits for all Jobs to complete.
        /// </summary>
        /// <remarks>Calling CompleteAllJobs() blocks the main thread until all currently running Jobs finish.</remarks>
        public void CompleteAllJobs()
        {
            ComponentJobSafetyManager->CompleteAllJobsAndInvalidateArrays();
        }

        /// <summary>
        /// Moves all entities managed by the specified EntityManager to the world of this EntityManager.
        /// </summary>
        /// <remarks>
        /// The entities moved are owned by this EntityManager.
        ///
        /// Each <see cref="World"/> has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one World to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        public void MoveEntitiesFrom(EntityManager srcEntities)
        {
            var entityRemapping = srcEntities.CreateEntityRemapArray(Allocator.TempJob);
            try
            {
                MoveEntitiesFrom(srcEntities, entityRemapping);
            }
            finally
            {
                entityRemapping.Dispose();
            }
        }
        // @TODO Proper description of remap utility.
        /// <summary>
        /// Moves all entities managed by the specified EntityManager to the <see cref="World"/> of this EntityManager.
        /// </summary>
        /// <remarks>
        /// After the move, the entities are managed by this EntityManager.
        ///
        /// Each World has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one world to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        /// <param name="entityRemapping">A set of entity transformations to make during the transfer.</param>
        /// <exception cref="ArgumentException">Thrown if you attempt to transfer entities to the EntityManager
        /// that already owns them.</exception>
        public void MoveEntitiesFrom(EntityManager srcEntities, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (srcEntities == this)
                throw new ArgumentException("srcEntities must not be the same as this EntityManager.");

            if (entityRemapping.Length < srcEntities.m_Entities->Capacity)
                throw new ArgumentException("entityRemapping.Length isn't large enough, use srcEntities.CreateEntityRemapArray");

            if (!srcEntities.m_SharedComponentManager.AllSharedComponentReferencesAreFromChunks(srcEntities.ArchetypeManager))
                throw new ArgumentException("EntityManager.MoveEntitiesFrom failed - All ISharedComponentData references must be from EntityManager. (For example EntityQuery.SetFilter with a shared component type is not allowed during EntityManager.MoveEntitiesFrom)");
#endif

            BeforeStructuralChange();
            srcEntities.BeforeStructuralChange();

            ArchetypeManager.MoveChunks(srcEntities, ArchetypeManager, m_GroupManager, Entities, m_SharedComponentManager, entityRemapping);

            //@TODO: Need to incrmeent the component versions based the moved chunks...
        }

        /// <summary>
        /// Creates a remapping array with one element for each entity in the <see cref="World"/>.
        /// </summary>
        /// <param name="allocator">The type of memory allocation to use when creating the array.</param>
        /// <returns>An array containing a no-op identity transformation for each entity.</returns>
        public NativeArray<EntityRemapUtility.EntityRemapInfo> CreateEntityRemapArray(Allocator allocator)
        {
            return new NativeArray<EntityRemapUtility.EntityRemapInfo>(m_Entities->Capacity, allocator);
        }

        /// <summary>
        /// Moves a selection of the entities managed by the specified EntityManager to the <see cref="World"/> of this EntityManager.
        /// </summary>
        /// <remarks>
        /// After the move, the entities are managed by this EntityManager.
        ///
        /// Each world has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one World to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        /// <param name="filter">A EntityQuery that defines the entities to move. Must be part of the source
        /// World.</param>
        /// <param name="entityRemapping">A set of entity transformations to make during the transfer.</param>
        /// <exception cref="ArgumentException">Thrown if the EntityQuery object used as the `filter` comes
        /// from a different world than the `srcEntities` EntityManager.</exception>
        public void MoveEntitiesFrom(EntityManager srcEntities, EntityQuery filter, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if(filter.ArchetypeManager != srcEntities.ArchetypeManager)
                throw new ArgumentException("EntityManager.MoveEntitiesFrom failed - srcEntities and filter must belong to the same World)");
#endif
            using (var chunks = filter.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                MoveEntitiesFrom(srcEntities, chunks, entityRemapping);
            }
        }

        internal void MoveEntitiesFrom(EntityManager srcEntities, NativeArray<ArchetypeChunk> chunks, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (srcEntities == this)
                throw new ArgumentException("srcEntities must not be the same as this EntityManager.");

            if (entityRemapping.Length < srcEntities.m_Entities->Capacity)
                throw new ArgumentException("entityRemapping.Length isn't large enough, use srcEntities.CreateEntityRemapArray");

            for(int i=0;i<chunks.Length;++i)
                if(chunks[i].m_Chunk->Archetype->HasChunkHeader)
                    throw new ArgumentException("MoveEntitiesFrom can not move chunks that contain ChunkHeader components.");
#endif

            BeforeStructuralChange();
            srcEntities.BeforeStructuralChange();

            ArchetypeManager.MoveChunks(srcEntities, chunks, ArchetypeManager, m_GroupManager, Entities, m_SharedComponentManager, entityRemapping);
        }

        /// <summary>
        /// Moves all entities managed by the specified EntityManager to the <see cref="World"/> of this EntityManager and fills
        /// an array with their Entity objects.
        /// </summary>
        /// <remarks>
        /// After the move, the entities are managed by this EntityManager. Use the `output` array to make post-move
        /// changes to the transferred entities.
        ///
        /// Each world has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one World to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="output">An array to receive the Entity objects of the transferred entities.</param>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        public void MoveEntitiesFrom(out NativeArray<Entity> output, EntityManager srcEntities)
        {
            var entityRemapping = srcEntities.CreateEntityRemapArray(Allocator.TempJob);
            try
            {
                MoveEntitiesFrom(out output, srcEntities, entityRemapping);
            }
            finally
            {
                entityRemapping.Dispose();
            }
        }
        /// <summary>
        /// Moves all entities managed by the specified EntityManager to the <see cref="World"/> of this EntityManager and fills
        /// an array with their <see cref="Entity"/> objects.
        /// </summary>
        /// <remarks>
        /// After the move, the entities are managed by this EntityManager. Use the `output` array to make post-move
        /// changes to the transferred entities.
        ///
        /// Each world has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one World to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="output">An array to receive the Entity objects of the transferred entities.</param>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        /// <param name="entityRemapping">A set of entity transformations to make during the transfer.</param>
        /// <exception cref="ArgumentException"></exception>
        public void MoveEntitiesFrom(out NativeArray<Entity> output, EntityManager srcEntities, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (srcEntities == this)
                throw new ArgumentException("srcEntities must not be the same as this EntityManager.");

            if (!srcEntities.m_SharedComponentManager.AllSharedComponentReferencesAreFromChunks(srcEntities.ArchetypeManager))
                throw new ArgumentException("EntityManager.MoveEntitiesFrom failed - All ISharedComponentData references must be from EntityManager. (For example EntityQuery.SetFilter with a shared component type is not allowed during EntityManager.MoveEntitiesFrom)");
#endif

            BeforeStructuralChange();
            srcEntities.BeforeStructuralChange();

            ArchetypeManager.MoveChunks(srcEntities, ArchetypeManager, m_GroupManager, Entities, m_SharedComponentManager, entityRemapping);
            EntityRemapUtility.GetTargets(out output, entityRemapping);

            //@TODO: Need to incrmeent the component versions based the moved chunks...
        }
        /// <summary>
        /// Moves a selection of the entities managed by the specified EntityManager to the <see cref="World"/> of this EntityManager
        /// and fills an array with their <see cref="Entity"/> objects.
        /// </summary>
        /// <remarks>
        /// After the move, the entities are managed by this EntityManager. Use the `output` array to make post-move
        /// changes to the transferred entities.
        ///
        /// Each world has one EntityManager, which manages all the entities in that world. This function
        /// allows you to transfer entities from one World to another.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before moving the entities and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="output">An array to receive the Entity objects of the transferred entities.</param>
        /// <param name="srcEntities">The EntityManager whose entities are appropriated.</param>
        /// <param name="filter">A EntityQuery that defines the entities to move. Must be part of the source
        /// World.</param>
        /// <param name="entityRemapping">A set of entity transformations to make during the transfer.</param>
        /// <exception cref="ArgumentException"></exception>
        public void MoveEntitiesFrom(out NativeArray<Entity> output, EntityManager srcEntities, EntityQuery filter, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if(filter.ArchetypeManager != srcEntities.ArchetypeManager)
                throw new ArgumentException("EntityManager.MoveEntitiesFrom failed - srcEntities and filter must belong to the same World)");
#endif
            using(var chunks = filter.CreateArchetypeChunkArray(Allocator.TempJob))
            {
                MoveEntitiesFrom(out output, srcEntities, chunks, entityRemapping);
            }
        }

        internal void MoveEntitiesFrom(out NativeArray<Entity> output, EntityManager srcEntities, NativeArray<ArchetypeChunk> chunks, NativeArray<EntityRemapUtility.EntityRemapInfo> entityRemapping)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            if (srcEntities == this)
                throw new ArgumentException("srcEntities must not be the same as this EntityManager.");
            for(int i=0;i<chunks.Length;++i)
                if(chunks[i].m_Chunk->Archetype->HasChunkHeader)
                    throw new ArgumentException("MoveEntitiesFrom can not move chunks that contain ChunkHeader components.");
#endif

            BeforeStructuralChange();
            srcEntities.BeforeStructuralChange();

            ArchetypeManager.MoveChunks(srcEntities, chunks, ArchetypeManager, m_GroupManager, Entities, m_SharedComponentManager, entityRemapping);
            EntityRemapUtility.GetTargets(out output, entityRemapping);
        }

        /// <summary>
        /// Gets a list of the types of components that can be assigned to the specified component.
        /// </summary>
        /// <remarks>Assignable components include those with the same compile-time type and those that
        /// inherit from the same compile-time type.</remarks>
        /// <param name="interfaceType">The type to check.</param>
        /// <returns>A new List object containing the System.Types that can be assigned to `interfaceType`.</returns>
        public List<Type> GetAssignableComponentTypes(Type interfaceType)
        {
            // #todo Cache this. It only can change when TypeManager.GetTypeCount() changes
            var componentTypeCount = TypeManager.GetTypeCount();
            var assignableTypes = new List<Type>();
            for (var i = 0; i < componentTypeCount; i++)
            {
                var type = TypeManager.GetType(i);
                if (interfaceType.IsAssignableFrom(type)) assignableTypes.Add(type);
            }

            return assignableTypes;
        }

        private bool TestMatchingArchetypeAny(Archetype* archetype, ComponentType* anyTypes, int anyCount)
        {
            if (anyCount == 0) return true;

            var componentTypes = archetype->Types;
            var componentTypesCount = archetype->TypesCount;
            for (var i = 0; i < componentTypesCount; i++)
            {
                var componentTypeIndex = componentTypes[i].TypeIndex;
                for (var j = 0; j < anyCount; j++)
                {
                    var anyTypeIndex = anyTypes[j].TypeIndex;
                    if (componentTypeIndex == anyTypeIndex) return true;
                }
            }

            return false;
        }

        private bool TestMatchingArchetypeNone(Archetype* archetype, ComponentType* noneTypes, int noneCount)
        {
            var componentTypes = archetype->Types;
            var componentTypesCount = archetype->TypesCount;
            for (var i = 0; i < componentTypesCount; i++)
            {
                var componentTypeIndex = componentTypes[i].TypeIndex;
                for (var j = 0; j < noneCount; j++)
                {
                    var noneTypeIndex = noneTypes[j].TypeIndex;
                    if (componentTypeIndex == noneTypeIndex) return false;
                }
            }

            return true;
        }

        private bool TestMatchingArchetypeAll(Archetype* archetype, ComponentType* allTypes, int allCount)
        {
            var componentTypes = archetype->Types;
            var componentTypesCount = archetype->TypesCount;
            var foundCount = 0;
            var disabledTypeIndex = TypeManager.GetTypeIndex<Disabled>();
            var prefabTypeIndex = TypeManager.GetTypeIndex<Prefab>();
            var requestedDisabled = false;
            var requestedPrefab = false;
            for (var i = 0; i < componentTypesCount; i++)
            {
                var componentTypeIndex = componentTypes[i].TypeIndex;
                for (var j = 0; j < allCount; j++)
                {
                    var allTypeIndex = allTypes[j].TypeIndex;
                    if (allTypeIndex == disabledTypeIndex)
                        requestedDisabled = true;
                    if (allTypeIndex == prefabTypeIndex)
                        requestedPrefab = true;
                    if (componentTypeIndex == allTypeIndex) foundCount++;
                }
            }

            if (archetype->Disabled && (!requestedDisabled))
                return false;
            if (archetype->Prefab && (!requestedPrefab))
                return false;

            return foundCount == allCount;
        }

        [Obsolete("This function is deprecated and will be removed in a future release.")]
        public void AddMatchingArchetypes(EntityQueryDesc queryDesc, NativeList<EntityArchetype> foundArchetypes)
        {
            var anyCount = queryDesc.Any.Length;
            var noneCount = queryDesc.None.Length;
            var allCount = queryDesc.All.Length;

            fixed (ComponentType* any = queryDesc.Any)
            {
                fixed (ComponentType* none = queryDesc.None)
                {
                    fixed (ComponentType* all = queryDesc.All)
                    {
                        for (var i = ArchetypeManager.m_Archetypes.Count - 1; i >= 0; --i)
                        {
                            var archetype = ArchetypeManager.m_Archetypes.p[i];
                            if (archetype->EntityCount == 0)
                                continue;
                            if (!TestMatchingArchetypeAny(archetype, any, anyCount))
                                continue;
                            if (!TestMatchingArchetypeNone(archetype, none, noneCount))
                                continue;
                            if (!TestMatchingArchetypeAll(archetype, all, allCount))
                                continue;

                            var entityArchetype = new EntityArchetype {Archetype = archetype};
                            var found = foundArchetypes.Contains(entityArchetype);
                            if (!found)
                                foundArchetypes.Add(entityArchetype);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Gets all the archetypes.
        /// </summary>
        /// <remarks>The function adds the archetype objects to the existing contents of the list.
        /// The list is not cleared.</remarks>
        /// <param name="allArchetypes">A native list to receive the EntityArchetype objects.</param>
        public void GetAllArchetypes(NativeList<EntityArchetype> allArchetypes)
        {
            for(var i = ArchetypeManager.m_Archetypes.Count - 1; i >= 0; --i)
            {
                var archetype = ArchetypeManager.m_Archetypes.p[i];
                var entityArchetype = new EntityArchetype() { Archetype = archetype };
                allArchetypes.Add(entityArchetype);
            }
        }

        [Obsolete("Please use EntityQuery APIs instead.")]
        public NativeArray<ArchetypeChunk> CreateArchetypeChunkArray(NativeList<EntityArchetype> archetypes,
            Allocator allocator)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var safetyHandle = AtomicSafetyHandle.Create();
            return ArchetypeChunkArray.Create(archetypes, allocator, safetyHandle);
#else
            return ArchetypeChunkArray.Create(archetypes, allocator);
#endif
        }

        [Obsolete("Please use EntityQuery APIs instead.")]
        public NativeArray<ArchetypeChunk> CreateArchetypeChunkArray(EntityQueryDesc queryDesc, Allocator allocator)
        {
            var foundArchetypes = new NativeList<EntityArchetype>(Allocator.TempJob);
            AddMatchingArchetypes(queryDesc, foundArchetypes);
            var chunkStream = CreateArchetypeChunkArray(foundArchetypes, allocator);
            foundArchetypes.Dispose();
            return chunkStream;
        }

        /// <summary>
        /// Gets the dynamic type object required to access a chunk component of type T.
        /// </summary>
        /// <remarks>
        /// To access a component stored in a chunk, you must have the type registry information for the component.
        /// This function provides that information. Use the returned <see cref="ArchetypeChunkComponentType{T}"/>
        /// object with the functions of an <see cref="ArchetypeChunk"/> object to get information about the components
        /// in that chunk and to access the component values.
        /// </remarks>
        /// <param name="isReadOnly">Specify whether the access to the component through this object is read only
        /// or read and write. </param>
        /// <typeparam name="T">The compile-time type of the component.</typeparam>
        /// <returns>The run-time type information of the component.</returns>
        public ArchetypeChunkComponentType<T> GetArchetypeChunkComponentType<T>(bool isReadOnly)
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var typeIndex = TypeManager.GetTypeIndex<T>();
            return new ArchetypeChunkComponentType<T>(
                ComponentJobSafetyManager->GetSafetyHandle(typeIndex, isReadOnly), isReadOnly,
                GlobalSystemVersion);
#else
            return new ArchetypeChunkComponentType<T>(isReadOnly, GlobalSystemVersion);
#endif
        }

        /// <summary>
        /// Gets the dynamic type object required to access a chunk buffer containing elements of type T.
        /// </summary>
        /// <remarks>
        /// To access a component stored in a chunk, you must have the type registry information for the component.
        /// This function provides that information for buffer components. Use the returned
        /// <see cref="ArchetypeChunkComponentType{T}"/> object with the functions of an <see cref="ArchetypeChunk"/>
        /// object to get information about the components in that chunk and to access the component values.
        /// </remarks>
        /// <param name="isReadOnly">Specify whether the access to the component through this object is read only
        /// or read and write. </param>
        /// <typeparam name="T">The compile-time type of the buffer elements.</typeparam>
        /// <returns>The run-time type information of the buffer component.</returns>
        public ArchetypeChunkBufferType<T> GetArchetypeChunkBufferType<T>(bool isReadOnly)
            where T : struct, IBufferElementData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            var typeIndex = TypeManager.GetTypeIndex<T>();
            return new ArchetypeChunkBufferType<T>(
                ComponentJobSafetyManager->GetSafetyHandle(typeIndex, isReadOnly),
                ComponentJobSafetyManager->GetBufferSafetyHandle(typeIndex),
                isReadOnly, GlobalSystemVersion);
#else
            return new ArchetypeChunkBufferType<T>(isReadOnly,GlobalSystemVersion);
#endif
        }

        /// <summary>
        /// Gets the dynamic type object required to access a shared component of type T.
        /// </summary>
        /// <remarks>
        /// To access a component stored in a chunk, you must have the type registry information for the component.
        /// This function provides that information for shared components. Use the returned
        /// <see cref="ArchetypeChunkComponentType{T}"/> object with the functions of an <see cref="ArchetypeChunk"/>
        /// object to get information about the components in that chunk and to access the component values.
        /// </remarks>
        /// <typeparam name="T">The compile-time type of the shared component.</typeparam>
        /// <returns>The run-time type information of the shared component.</returns>
        public ArchetypeChunkSharedComponentType<T> GetArchetypeChunkSharedComponentType<T>()
            where T : struct, ISharedComponentData
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new ArchetypeChunkSharedComponentType<T>(
                ComponentJobSafetyManager->GetEntityManagerSafetyHandle());
#else
            return new ArchetypeChunkSharedComponentType<T>(false);
#endif
        }

        /// <summary>
        /// Gets the dynamic type object required to access the <see cref="Entity"/> component of a chunk.
        /// </summary>
        /// <remarks>
        /// All chunks have an implicit <see cref="Entity"/> component referring to the entities in that chunk.
        ///
        /// To access any component stored in a chunk, you must have the type registry information for the component.
        /// This function provides that information for the implicit <see cref="Entity"/> component. Use the returned
        /// <see cref="ArchetypeChunkComponentType{T}"/> object with the functions of an <see cref="ArchetypeChunk"/>
        /// object to access the component values.
        /// </remarks>
        /// <returns>The run-time type information of the Entity component.</returns>
        public ArchetypeChunkEntityType GetArchetypeChunkEntityType()
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            return new ArchetypeChunkEntityType(
                ComponentJobSafetyManager->GetSafetyHandle(TypeManager.GetTypeIndex<Entity>(), true));
#else
            return new ArchetypeChunkEntityType(false);
#endif
        }


        /// <summary>
        /// Swaps the components of two entities.
        /// </summary>
        /// <remarks>
        /// The entities must have the same components. However, this function can swap the components of entities in
        /// different worlds, so they do not need to have identical archetype instances.
        ///
        /// **Important:** This function creates a sync point, which means that the EntityManager waits for all
        /// currently running Jobs to complete before swapping the components and no additional Jobs can start before
        /// the function is finished. A sync point can cause a drop in performance because the ECS framework may not
        /// be able to make use of the processing power of all available cores.
        /// </remarks>
        /// <param name="leftChunk">A chunk containing one of the entities to swap.</param>
        /// <param name="leftIndex">The index within the `leftChunk` of the entity and components to swap.</param>
        /// <param name="rightChunk">The chunk containing the other entity to swap. This chunk can be the same as
        /// the `leftChunk`. It also does not need to be in the same World as `leftChunk`.</param>
        /// <param name="rightIndex">The index within the `rightChunk`  of the entity and components to swap.</param>
        public void SwapComponents(ArchetypeChunk leftChunk, int leftIndex, ArchetypeChunk rightChunk, int rightIndex)
        {
            BeforeStructuralChange();
            ChunkDataUtility.SwapComponents(leftChunk.m_Chunk,leftIndex,rightChunk.m_Chunk,rightIndex,1, GlobalSystemVersion, GlobalSystemVersion);
        }

        /// <summary>
        /// The <see cref="World"/> of this EntityManager.
        /// </summary>
        /// <value>A World has one EntityManager and an EntityManager manages the entities of one World.</value>
        public World World { get { return m_World; } }

        // @TODO documentation for serialization/deserialization
        /// <summary>
        /// Prepares an empty <see cref="World"/> to load serialized entities.
        /// </summary>
        public void PrepareForDeserialize()
        {
            Assert.AreEqual(0, Debug.EntityCount);
            m_SharedComponentManager.PrepareForDeserialize();
        }

        // @TODO document EntityManagerDebug
        /// <summary>
        /// Provides information and utility functions for debugging.
        /// </summary>
        public class EntityManagerDebug
        {
            private readonly EntityManager m_Manager;

            public EntityManagerDebug(EntityManager entityManager)
            {
                m_Manager = entityManager;
            }

            public void PoisonUnusedDataInAllChunks(EntityArchetype archetype, byte value)
            {
                for(var i = 0; i < archetype.Archetype->Chunks.Count; ++i)
                {
                    var chunk = archetype.Archetype->Chunks.p[i];
                    ChunkDataUtility.MemsetUnusedChunkData(chunk, value);
                }
            }

            public void SetGlobalSystemVersion(uint version)
            {
                m_Manager.Entities->GlobalSystemVersion = version;
            }

            public bool IsSharedComponentManagerEmpty()
            {
                return m_Manager.m_SharedComponentManager.IsEmpty();
            }

#if !NET_DOTS
            internal static string GetArchetypeDebugString(Archetype* a)
            {
                var buf = new System.Text.StringBuilder();
                buf.Append("(");

                for (var i = 0; i < a->TypesCount; i++)
                {
                    var componentTypeInArchetype = a->Types[i];
                    if (i > 0)
                        buf.Append(", ");
                    buf.Append(componentTypeInArchetype.ToString());
                }

                buf.Append(")");
                return buf.ToString();
            }
#endif

            public int EntityCount
            {
                get
                {
                    var allEntities = m_Manager.GetAllEntities();
                    var count = allEntities.Length;
                    allEntities.Dispose();
                    return count;
                }
            }

            internal Entity GetMetaChunkEntity(Entity entity)
            {
                return m_Manager.GetChunk(entity).m_Chunk->metaChunkEntity;
            }

            public void LogEntityInfo(Entity entity)
            {
                Unity.Debug.Log(GetEntityInfo(entity));
            }

            public string GetEntityInfo(Entity entity)
            {
                var archetype = m_Manager.Entities->GetArchetype(entity);
                #if !NET_DOTS
                    var str = new System.Text.StringBuilder();
                    str.Append(entity.ToString());
                    #if UNITY_EDITOR
                    {
                        var name = m_Manager.GetName(entity);
                        if(!string.IsNullOrEmpty(name))
                            str.Append($" (name '{name}')");
                    }
#endif
                    for (var i = 0; i < archetype->TypesCount; i++)
                    {
                        var componentTypeInArchetype = archetype->Types[i];
                        str.AppendFormat("  - {0}", componentTypeInArchetype.ToString());
                    }

                    return str.ToString();
                #else
                    // @TODO Tiny really needs a proper string/stringutils implementation
                    string str = $"Entity {entity.Index}.{entity.Version}";
                    for (var i = 0; i < archetype->TypesCount; i++)
                    {
                        var componentTypeInArchetype = archetype->Types[i];
                        str += "  - {0}" + componentTypeInArchetype.ToString();
                    }

                    return str;
                #endif
            }

#if !NET_DOTS
            public object GetComponentBoxed(Entity entity, ComponentType type)
            {
                m_Manager.Entities->AssertEntityHasComponent(entity, type);

                var typeInfo = TypeManager.GetTypeInfo(type.TypeIndex);
                if (typeInfo.Category == TypeManager.TypeCategory.ComponentData)
                {
                    var obj = Activator.CreateInstance(TypeManager.GetType(type.TypeIndex));
                    if (!typeInfo.IsZeroSized)
                    {
                        ulong handle;
                        var ptr = (byte*)UnsafeUtility.PinGCObjectAndGetAddress(obj, out handle);
                        ptr += TypeManager.ObjectOffset;
                        var src = m_Manager.Entities->GetComponentDataWithTypeRO(entity, type.TypeIndex);
                        UnsafeUtility.MemCpy(ptr, src, TypeManager.GetTypeInfo(type.TypeIndex).SizeInChunk);

                        UnsafeUtility.ReleaseGCObject(handle);
                    }
                    return obj;
                }
                else if (typeInfo.Category == TypeManager.TypeCategory.ISharedComponentData)
                {
                    return m_Manager.GetSharedComponentData(entity, type.TypeIndex);
                }
                else
                {
                    throw new System.NotImplementedException();
                }
            }
#else
            public object GetComponentBoxed(Entity entity, ComponentType type)
            {
                throw new System.NotImplementedException();
            }
#endif

            public void CheckInternalConsistency()
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS

                //@TODO: Validate from perspective of chunkquery...
                var entityCountEntityData = m_Manager.Entities->CheckInternalConsistency();
                var entityCountArchetypeManager = m_Manager.ArchetypeManager.CheckInternalConsistency(m_Manager.Entities);
                Assert.AreEqual(entityCountEntityData, entityCountArchetypeManager);

                Assert.IsTrue(m_Manager.m_SharedComponentManager.AllSharedComponentReferencesAreFromChunks(m_Manager.m_ArchetypeManager));
                m_Manager.m_SharedComponentManager.CheckRefcounts();
#endif
            }
        }

        internal EntityArchetype GetEntityOnlyArchetype()
        {
            return new EntityArchetype{Archetype = m_ArchetypeManager.GetEntityOnlyArchetype(m_GroupManager)};
        }

        // raw by-id access

        internal EntityArchetype CreateArchetypeRaw(int* typeIndices, int count)
        {
            // TODO fix this up
            ComponentType* ct = stackalloc ComponentType[count];
            for (int i = 0; i < count; ++i)
                ct[i] = ComponentType.FromTypeIndex(typeIndices[i]);
            return CreateArchetype(ct, count);
        }

        internal bool HasComponentRaw(Entity entity, int typeIndex)
        {
            return Entities->HasComponent(entity, typeIndex);
        }

        internal void AddComponentRaw(Entity entity, int typeIndex)
        {
            AddComponent(entity, ComponentType.FromTypeIndex(typeIndex));
        }

        internal void RemoveComponentRaw(Entity entity, int typeIndex)
        {
            RemoveComponent(entity, ComponentType.FromTypeIndex(typeIndex));
        }
    }
}
