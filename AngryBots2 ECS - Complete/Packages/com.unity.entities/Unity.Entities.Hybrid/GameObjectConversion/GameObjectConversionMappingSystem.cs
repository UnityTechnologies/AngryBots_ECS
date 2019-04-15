#define DETAIL_MARKERS
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;
using Hash128 = Unity.Entities.Hash128;
using ConversionFlags = Unity.Entities.GameObjectConversionUtility.ConversionFlags;



public struct EntitiesEnumerator : IEnumerable<Entity>, IEnumerator<Entity>
{
    Entity[]                         m_Entities;
    int[]                            m_Next;
    int m_FirstIndex;
    int m_CurIndex;


    internal EntitiesEnumerator(Entity[] entities, int[] next, int index)
    {
        m_Entities = entities;
        m_Next = next;
        m_FirstIndex = index;
        m_CurIndex = -1;
    }

    public EntitiesEnumerator GetEnumerator()
    {
        return this;
    }

    IEnumerator<Entity> IEnumerable<Entity>.GetEnumerator()
    {
        return this;
    }
    IEnumerator IEnumerable.GetEnumerator()
    {
        return this;
    }
    
    
    public bool MoveNext()
    {
        if (m_CurIndex == -1)
            m_CurIndex = m_FirstIndex;
        else
            m_CurIndex = m_Next[m_CurIndex];

        return m_CurIndex != -1;
    }

    public void Reset()
    {
        m_CurIndex = -1;
    }

    public Entity Current
    {
        get
        {
            return m_Entities[m_CurIndex];
        }
    }
    
    object IEnumerator.Current => Current;

    public void Dispose()
    {
    }
}

[DisableAutoCreation]
class GameObjectConversionMappingSystem : ComponentSystem
{
    NativeHashMap<int, int>       m_GameObjectToEntity = new NativeHashMap<int, int>(100 * 1000, Allocator.Persistent);

    int                              m_EntitiesCount;
    Entity[]                         m_Entities;
    int[]                            m_Next;
    
    HashSet<GameObject>                   m_ReferencedPrefabs = new HashSet<GameObject>();
    HashSet<GameObject>                   m_LinkedEntityGroup = new HashSet<GameObject>();
    
    World                                 m_DstWorld;
    EntityManager                         m_DstManager;
    ConversionFlags                       m_ConversionFlags;
    Hash128                               m_SceneGUID;
    
    public World DstWorld { get { return m_DstWorld; } }
    public bool AddEntityGUID { get { return (m_ConversionFlags & ConversionFlags.AddEntityGUID) != 0; } }
    public bool ForceStaticOptimization { get { return (m_ConversionFlags & ConversionFlags.ForceStaticOptimization) != 0; } }
    public bool AssignName { get { return (m_ConversionFlags & ConversionFlags.AssignName) != 0; } }

    public GameObjectConversionMappingSystem(World DstWorld, Hash128 sceneGUID, ConversionFlags conversionFlags)
    {
        m_DstWorld = DstWorld;
        m_SceneGUID = sceneGUID;
        m_ConversionFlags = conversionFlags;
        m_DstManager = DstWorld.EntityManager;

        m_Entities = new Entity[128];
        m_Next = new int[128];
        m_EntitiesCount = 0;
    }

    protected override void OnDestroy()
    {
        m_GameObjectToEntity.Dispose();
    }   
    unsafe public static EntityGuid GetEntityGuid(GameObject go, int index)
    {
#if false
        var id = GlobalObjectId.GetGlobalObjectId(go);
        // For the time being use InstanceID until we support GlobalObjectID API
        //Debug.Log(id);
        var hash = Hash128.Compute($"{id}:{index}");
        
        EntityGuid entityGuid;
        Assert.AreEqual(sizeof(EntityGuid), sizeof(Hash128));
        UnsafeUtility.MemCpy(&entityGuid, &hash, sizeof(Hash128));
        return entityGuid;
#else
        EntityGuid entityGuid;
        entityGuid.a = (ulong)go.GetInstanceID();
        entityGuid.b = (ulong)index;
    
        return entityGuid;
#endif
    }

    #if DETAIL_MARKERS
    ProfilerMarker m_CreateEntity = new ProfilerMarker("GameObjectConversion.CreateEntity");
    ProfilerMarker m_GetPrimaryEntity = new ProfilerMarker("GameObjectConversion.GetPrimaryEntity");
    ProfilerMarker m_CreateAdditional = new ProfilerMarker("GameObjectConversionCreateAdditionalEntity");
    #endif

    Entity CreateEntity(GameObject go, int index)
    {
        #if DETAIL_MARKERS
        using (m_CreateEntity.Auto())
        #endif            
        {
            Entity entity;

            if (AddEntityGUID)
            {
                entity = m_DstManager.CreateEntity(ComponentType.ReadWrite<EntityGuid>());
                var entityGuid = GetEntityGuid(go, index);
                m_DstManager.SetComponentData(entity, entityGuid);
            }
            else
            {
                entity = m_DstManager.CreateEntity();
            }


            var section = go.GetComponentInParent<SceneSectionComponent>();
            if (m_SceneGUID != default(Hash128))
            {
                m_DstManager.AddSharedComponentData(entity, new SceneSection { SceneGUID = m_SceneGUID, Section = section != null ? section.SectionIndex : 0});
            }

            if (ForceStaticOptimization || go.GetComponentInParent<StaticOptimizeEntity>() != null)
                m_DstManager.AddComponentData(entity, new Static());

#if UNITY_EDITOR
            if (AssignName)
                m_DstManager.SetName(entity, go.name);
#endif


            return entity;
        }
    }

    public Entity GetPrimaryEntity(GameObject go)
    {
        #if DETAIL_MARKERS
        using (m_GetPrimaryEntity.Auto())
        #endif
        {
            if (go == null)
                return new Entity();

            var instanceID = go.GetInstanceID();
            int index;
            if (m_GameObjectToEntity.TryGetValue(instanceID, out index))
            {
                return m_Entities[index];
            }
            else
            {
                var entity = CreateEntity(go, 0);

                if (!m_GameObjectToEntity.TryAdd(instanceID, m_EntitiesCount))
                    throw  new InvalidOperationException();
                AddFirst(entity);

                return entity;
            }
        }
    }
    int Count(int index)
    {
        int count = 1;
        while ((index = m_Next[index]) != -1)
            count++;

        return count;
    }
    
    void AddFirst(Entity entity)
    {
        if (m_EntitiesCount == m_Entities.Length)
        {
            Array.Resize(ref m_Entities, m_EntitiesCount * 2);
            Array.Resize(ref m_Next, m_EntitiesCount * 2);
        }
            
        m_Entities[m_EntitiesCount] = entity;
        m_Next[m_EntitiesCount] = -1;
        m_EntitiesCount++;
    }
    
    void AddAdditional(int index, Entity entity)
    {
        if (m_EntitiesCount == m_Entities.Length)
        {
            Array.Resize(ref m_Entities, m_EntitiesCount * 2);
            Array.Resize(ref m_Next, m_EntitiesCount * 2);
        }

        int lastIndex = index;
        while ((index = m_Next[index]) != -1)
            lastIndex = index;

        m_Entities[m_EntitiesCount] = entity;
        m_Next[m_EntitiesCount] = -1;
        m_Next[lastIndex] = m_EntitiesCount;
        m_EntitiesCount++;
    }
    
    public Entity CreateAdditionalEntity(GameObject go)
    {
        #if DETAIL_MARKERS
        using (m_CreateAdditional.Auto())
        #endif
        {
            if (go == null)
                throw new System.ArgumentException("CreateAdditionalEntity must be called with a valid game object");

            var instanceID = go.GetInstanceID();
            int index;
            if (!m_GameObjectToEntity.TryGetValue(instanceID, out index))
                throw new System.ArgumentException("CreateAdditionalEntity can't be called before GetPrimaryEntity is called for that game object");

            int count = Count(index);
            var entity = CreateEntity(go, count);
            AddAdditional(index, entity);
        
            return entity;
        }
    }

    public EntitiesEnumerator GetEntities(GameObject go)
    {
        var instanceID = go != null ? go.GetInstanceID() : 0;
        int index;
        if (go != null)
        {
            if (m_GameObjectToEntity.TryGetValue(instanceID, out index))
                return new EntitiesEnumerator(m_Entities, m_Next, index);
        }

        return new EntitiesEnumerator(m_Entities, m_Next, -1);
    }

    public void AddGameObjectOrPrefabAsGroup(GameObject prefab)
    {
        if (prefab == null)
            return;
        if (m_ReferencedPrefabs.Contains(prefab))
            return;

        m_LinkedEntityGroup.Add(prefab);
        
        var isPrefab = !prefab.scene.IsValid();
        if (isPrefab)
            CreateEntitiesForGameObjectsRecurse(prefab.transform, EntityManager, m_ReferencedPrefabs);
        else
            CreateEntitiesForGameObjectsRecurse(prefab.transform, EntityManager, null);
    }

    public void AddReferencedPrefabAsGroup(GameObject prefab)
    {
        if (prefab == null)
            return;
        if (m_ReferencedPrefabs.Contains(prefab))
            return;

        var isPrefab = !prefab.scene.IsValid();

        if (!isPrefab)
            return;

        m_LinkedEntityGroup.Add(prefab);
        CreateEntitiesForGameObjectsRecurse(prefab.transform, EntityManager, m_ReferencedPrefabs);
    }
    
    internal void AddPrefabComponentDataTag()
    {
        // Add prefab tag to all entities that were converted from a prefab game object source
        foreach (var prefab in m_ReferencedPrefabs)
        {
            foreach(var e in GetEntities(prefab))
                m_DstManager.AddComponentData(e, new Prefab());
        }

        // Create LinkedEntityGroup for each root prefab entity
        // Instnatiate & Destroy will destroy the entity as a group.
        foreach (var i in m_LinkedEntityGroup)
        {
            var allChildren = i.GetComponentsInChildren<Transform>(true);

            var linkedRoot = GetPrimaryEntity(i);
            var buffer = m_DstManager.AddBuffer<LinkedEntityGroup>(linkedRoot);

            foreach (Transform t in allChildren)
            {
                foreach (var e in GetEntities(t.gameObject))
                    buffer.Add(e);                    
            }
        }
    }
    
    internal static void CreateEntitiesForGameObjectsRecurse(Transform transform, EntityManager gameObjectEntityManager, HashSet<GameObject> gameObjects)
    {
        GameObjectEntity.AddToEntityManager(gameObjectEntityManager, transform.gameObject);
        if (gameObjects != null)
            gameObjects.Add(transform.gameObject);

        int childCount = transform.childCount;
        for (int i = 0; i != childCount;i++)
            CreateEntitiesForGameObjectsRecurse(transform.GetChild(i), gameObjectEntityManager, gameObjects);
    }
    

    internal static void CreateEntitiesForGameObjects(Scene scene, World gameObjectWorld)
    {
        var entityManager = gameObjectWorld.EntityManager;
        var gameObjects = scene.GetRootGameObjects();

        foreach (var go in gameObjects)
            CreateEntitiesForGameObjectsRecurse(go.transform, entityManager, null);
    }
    
    protected override void OnUpdate()
    {
        
    }
}
