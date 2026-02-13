using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// Timing: Update this system before the group of systems that renders the geometry.
//  This helps us to allocate and set transform data of the entity before it's rendered,
//  to avoid having a 1-frame delay where you can see the entity at the origin
[UpdateBefore(typeof(TransformSystemGroup))]
partial struct SpawnEnemySystem : ISystem
{
    float timer;
    
    // A query to find the directory data component
    EntityQuery directoryQuery;
    
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // These 2 lines makes the system not update unless at least 1 entity in the world
        // exists that has the Directory component, and also 1 with a SpawnEnemyRequest component
        state.RequireForUpdate<Directory>();
        
        directoryQuery = SystemAPI.QueryBuilder().WithAll<Directory>().Build();
        
        timer = 0f;
    }
    
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        if (!Settings.Instance.useECSforEnemies || 
            !Settings.Instance.spawnEnemies ||
            Settings.IsPlayerDead())
            return;
        
        Directory directory = directoryQuery.GetSingleton<Directory>();

        Entity enemyPrefab = directory.enemyPrefab;
        EntityManager manager = state.EntityManager;

        timer += SystemAPI.Time.DeltaTime;

        if (timer > Settings.Instance.enemySpawnInterval)
        {
            float3 newEnemyPosition = 
                Settings.GetPositionAroundPlayer(Settings.Instance.enemySpawnRadius);
            
            SpawnEnemy(
                ref manager,
                enemyPrefab,
                newEnemyPosition);
            
            timer = 0f;
        }
    }
    
    [BurstCompile]
    // This method spawns enemies as entities instead of GameObjects
    void SpawnEnemy(
        ref EntityManager manager, 
        Entity enemyPrefab, 
        float3 position)
    {
        // Use our EntityManager to instantiate a copy of the bullet entity
        Entity newEnemy = manager.Instantiate(enemyPrefab);

        // Create a new LocalTransform component and give it the values needed to
        // be positioned at the spawn point
        LocalTransform t = new LocalTransform()
        {
            Position = position,
            Rotation = Quaternion.identity,
            Scale = 1f
        };

        // Set the component data we just created for the entity we just created
        manager.SetComponentData(newEnemy, t);
    }
}