using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

partial struct SpawnEnemySystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // These 2 lines makes the system not update unless at least 1 entity in the world
        // exists that has the Directory component, and also 1 with a SpawnEnemyRequest component
        state.RequireForUpdate<Directory>();
        state.RequireForUpdate<SpawnEnemyRequest>();
    }
    
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var directoryQuery = SystemAPI.QueryBuilder().WithAll<Directory>().Build();
        var directory = directoryQuery.GetSingleton<Directory>();

        Entity enemyPrefab = directory.enemyPrefab;
        EntityManager manager = state.EntityManager;

        // This code uses a CommandBuffer, which lets us queue up commands for playback later.
        // Note: CommandBuffers must be cleaned up to prevent memory leaks, so the "using" syntax
        // is used here to automatically manage that
        using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
        {
            foreach (var(spawnBulletRequest,entity) in
                     SystemAPI.Query<RefRO<SpawnEnemyRequest>>()
                         .WithEntityAccess())
            {
                SpawnEnemy(
                    ref manager,
                    enemyPrefab,
                    spawnBulletRequest.ValueRO.position);
                
                commandBuffer.DestroyEntity(entity);
            }
            
            // After the foreach, playback the buffer, destroying the entities
            commandBuffer.Playback(state.EntityManager);
        }
    }
    
    // This method spawns bullets as entities instead of GameObjects
    private void SpawnEnemy(
        ref EntityManager manager, 
        Entity enemyPrefab, 
        float3 position)
    {
        // Use our EntityManager to instantiate a copy of the bullet entity
        Entity enemy = manager.Instantiate(enemyPrefab);

        // Create a new LocalTransform component and give it the values needed to
        // be positioned at the spawn point
        LocalTransform t = new LocalTransform()
        {
            Position = position,
            Rotation = Quaternion.identity,
            Scale = 1f
        };

        // Set the component data we just created for the entity we just created
        manager.SetComponentData(enemy, t);
    }
}