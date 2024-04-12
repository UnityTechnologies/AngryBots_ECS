/* ENEMY SPAWNER
 * This script manages the process of spawning enemies. Most of the code is general
 * or used for GameObject workflows, and DOTS items are nearly identical to the
 * PlayerShooting script (instead of bullets, this script spawn enemies). The DOTS 
 * items to be aware of in this script are:
	* - The entity members: manager, and bulletEntityPrefab
	* - The initialization in the Start() method
	* - The entity instantiation in the Spawn() method
 */

using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
	[Header("Enemy Spawn Info")]
	public bool spawnEnemies = true;
	public bool useECS = false;
	public float enemySpawnRadius = 17f;
	public GameObject enemyPrefab;

	[Header("Enemy Spawn Timing")]
	[Range(1, 100)] public int spawnsPerInterval = 1;
	[Range(.1f, 2f)] public float spawnInterval = 1f;
	
	EntityManager manager;          // Member to hold an EntityManager reference
	Entity enemyEntityPrefab;       // Member to hold the ID of the enemy entity

	float cooldown;


	void Start()
	{
		cooldown = spawnInterval;

		// If not using ECS, no need to do anything here
		if (!useECS)
			return;

		// Get a reference to an EntityManager which is how we will create and access entities
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;

		// Create a query that will find the Directory entity. The Directory is created automatically
		// by the baking process of the Directory GameObject which you can find in the "Baker Sub Scene"
		// in the ECS Shooter scene
		EntityQuery query = new EntityQueryBuilder(Allocator.Temp).WithAll<Directory>().Build(manager);

		// If this query finds one and only one Directory, then grab the enemy entity and store it
		if (query.HasSingleton<Directory>())
			enemyEntityPrefab = query.GetSingleton<Directory>().enemyPrefab;
	}

	void Update()
    {
		if (!spawnEnemies || Settings.IsPlayerDead())
			return;

		cooldown -= Time.deltaTime;

		if (cooldown <= 0f)
		{
			cooldown += spawnInterval;
			Spawn();
		}
    }

	void Spawn()
	{
		for (int i = 0; i < spawnsPerInterval; i++)
		{
			Vector3 pos = Settings.GetPositionAroundPlayer(enemySpawnRadius);

			if (!useECS)
			{
				Instantiate(enemyPrefab, pos, Quaternion.identity);
			}
			else
			{
				// Use our EntityManager to instantiate a copy of the enemy entity
				Entity enemy = manager.Instantiate(enemyEntityPrefab);

				// Create a new LocalTransform component and give it the values needed to
				// be positioned at the spawn point
				LocalTransform t = new LocalTransform
				{
					Position = pos,
					Rotation = Quaternion.identity,
					Scale = 1f
				};

				// Set the component data we just created for the entity we just created
				manager.SetComponentData(enemy, t);
			}
		}
	}
}
