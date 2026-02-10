using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

partial struct SpawnBulletSystem : ISystem
{
    public void OnCreate(ref SystemState state)
    {
        // start logic goes here...
        
        
    }
    
    public void OnUpdate(ref SystemState state)
    {
        // update logic goes here...
        
        
    }
    
    // This method spawns bullets as entities instead of GameObjects
    private void SpawnBullet(
        ref EntityManager manager, 
        Entity bulletEntityPrefab, 
        float3 position,
        Quaternion rotation)
    {
        // Use our EntityManager to instantiate a copy of the bullet entity
        

        // Create a new LocalTransform component and give it the values needed to
        // be positioned at the barrel of the gun


        // Set the component data we just created for the entity we just created
        
    }
}