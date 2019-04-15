using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

[WorldSystemFilter(WorldSystemFilterFlags.GameObjectConversion)]
public abstract class GameObjectConversionSystem : ComponentSystem
{
    public World DstWorld;
    public EntityManager DstEntityManager;

    GameObjectConversionMappingSystem m_MappingSystem;

    protected override void OnCreate()
    {
        base.OnCreate();

        m_MappingSystem = World.GetOrCreateSystem<GameObjectConversionMappingSystem>();
        DstWorld = m_MappingSystem.DstWorld;
        DstEntityManager = DstWorld.EntityManager;
    }
    
    public Entity GetPrimaryEntity(Component component)
    {
        return m_MappingSystem.GetPrimaryEntity(component != null ? component.gameObject : null);
    }
    
    public Entity CreateAdditionalEntity(Component component)
    {
        return m_MappingSystem.CreateAdditionalEntity(component != null ? component.gameObject : null);
    }
    
    public EntitiesEnumerator GetEntities(Component component)
    {
        return m_MappingSystem.GetEntities(component != null ? component.gameObject : null);
    }
    
    public Entity GetPrimaryEntity(GameObject gameObject)
    {
        return m_MappingSystem.GetPrimaryEntity(gameObject);
    }
    
    public Entity CreateAdditionalEntity(GameObject gameObject)
    {
        return m_MappingSystem.CreateAdditionalEntity(gameObject);
    }
    
    public EntitiesEnumerator GetEntities(GameObject gameObject)
    {
        return m_MappingSystem.GetEntities(gameObject);
    }
    
    public void AddReferencedPrefab(GameObject gameObject)
    {
        m_MappingSystem.AddReferencedPrefabAsGroup(gameObject);
    }
}