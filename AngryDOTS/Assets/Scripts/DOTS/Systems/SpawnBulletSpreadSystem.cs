using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

partial struct SpawnBulletSpreadSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // These 2 lines makes the system not update unless at least 1 entity in the world
        // exists that has the Directory component, and 1 with the SpawnBulletSpreadRequest component
        state.RequireForUpdate<Directory>();
        state.RequireForUpdate<SpawnBulletSpreadRequest>();
    }
    
    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var directoryQuery = SystemAPI.QueryBuilder().WithAll<Directory>().Build();
        var directory = directoryQuery.GetSingleton<Directory>();

        Entity bulletEntityPrefab = directory.bulletPrefab;
        EntityManager manager = state.EntityManager;
        
        using (var commandBuffer = new EntityCommandBuffer(Allocator.Temp))
        {
            foreach (var(spawnBulletRequest,entity) in
                     SystemAPI.Query<RefRO<SpawnBulletSpreadRequest>>()
                         .WithEntityAccess())
            {
                SpawnBulletSpread(                        
                    ref manager,
                    bulletEntityPrefab,
                    spawnBulletRequest.ValueRO.spreadAmount,
                    spawnBulletRequest.ValueRO.gunBarrelPosition,
                    spawnBulletRequest.ValueRO.playerRotation);
                
                commandBuffer.DestroyEntity(entity);
            }
            
            // After the foreach, playback the buffer, destroying the entities
            commandBuffer.Playback(state.EntityManager);
        }
    }

    private void SpawnBulletSpread(
        ref EntityManager manager, 
        Entity bulletEntityPrefab,
        int spreadAmount,
        float3 position,
        float3 rotation)
    {
        // Most of this code is just boilerplate math to create a grid of rotations. Only the
        // relevant DOTS code is commented
        if (spreadAmount % 2 != 0) //No odd numbers to keep the spread even
            spreadAmount += 1;
        
        int max = spreadAmount / 2;
        int min = -max;
        int totalAmount = spreadAmount * spreadAmount;

        Vector3 tempRot = rotation;
        int index = 0;
        
        
        // allocate array of bullets
        
        // instantiate bullets inside of array
        
        
        for (int x = min; x < max; x++)
        {
            tempRot.x = (rotation.x + 3 * x) % 360;

            for (int y = min; y < max; y++)
            {
                tempRot.y = (rotation.y + 3 * y) % 360;


                // Create a new LocalTransform component and give it the values needed to
                // be positioned at the barrel of the gun
                

                // Set the component data we just created for the entity we just created

                index++;
            }
        }
        
        // Be sure to Dispose of the NativeArray or else you'll have a memory leak
        
    }
}