using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

// Timing: Update this system before the group of systems that renders the geometry.
//  This helps us to allocate and set transform data of the entity before it's rendered,
//  to avoid having a 1-frame delay where you can see the entity at the origin

partial struct SpawnBulletSystem : ISystem
{
    float timer;
    
    // A query to find the directory data component
    EntityQuery directoryQuery;
    
    public void OnCreate(ref SystemState state)
    {
        // This line makes the system not update unless at least 1 entity in the world
        // exists that has the Directory component

        
        // build the query to retrieve data from the Directory component


        timer = 0f;
    }
    
    public void OnUpdate(ref SystemState state)
    {
        if(Settings.IsPlayerDead())
            return;
		
        timer += Time.deltaTime;
        
        // retrieve the data from the Directory

        
        // grab the bullet prefab entity reference

        
        // grab a reference to the entity manager. With it, we'll instantiate the new bullet(s)

        
        if (Settings.Instance.useECSforBullets && 
            Input.GetButton("Fire1") && 
            timer >= Settings.Instance.fireRate)
        {
            Vector3 rotation = Settings.PlayerGunBarrelRotationEuler;
            rotation.x = 0f;

            if (!Settings.Instance.spreadShot)
            {
                // Spawn single bullet
            }
            else
            {
                // Spawn bullet spread
            }
            
            timer = 0f;
        }
    }
    
    // This method spawns bullets as entities instead of GameObjects
    void SpawnBullet(
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
    
    void SpawnBulletSpread(
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
        
        // NativeArrays are thread-safe data containers. In DOTS, they are a great way to work
        // with a lot of entities at once. They must be cleaned up though. This code creates
        // a temporary NativeArray with a size equal to the number of bullets we want to spawn
        
        
        // By passing a NativeArray into the Instantiate() method of the EntityManager, many entities
        // are created at once and put into this NativeArray
        
        
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