/* ENEMY SPAWNER
 * This script manages the process of spawning enemies. Most of the code is general
 * or used for GameObject workflows, and DOTS items are nearly identical to the
 * PlayerShooting script (instead of bullets, this script spawn enemies). The DOTS 
 * items to be aware of in this script are:
	* - The entity members: manager
	* - The initialization in the Start() method
	* - The entity instantiation in the Spawn() method
 */

using Unity.Entities;
using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
	[Header("Enemy Spawn Info")]
	public float enemySpawnRadius = 17f;
	public GameObject enemyPrefab;

	[Header("Enemy Spawn Timing")]
	[Range(1, 100)] public int spawnsPerInterval = 1;
	[Range(.1f, 2f)] public float spawnInterval = 1f;
	
	// Member to hold an EntityManager reference
	EntityManager manager;

	float cooldown;


	void Start()
	{
		cooldown = spawnInterval;

		// If not using ECS, no need to do anything here
		if (!Settings.IsUsingECSForEnemies())
			return;
		
		// Get a reference to an EntityManager which is how we will create and access entities
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;
	}
	
	void Update()
    {
		if (!Settings.IsSpawningEnemies() || Settings.IsPlayerDead())
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
			Vector3 newEnemyPosition = Settings.GetPositionAroundPlayer(enemySpawnRadius);

			if (!Settings.IsUsingECSForEnemies())
			{
				Instantiate(enemyPrefab, newEnemyPosition, Quaternion.identity);
			}
			else
			{
				// Use our EntityManager to instantiate a new entity and give it an enemy spawn request component
				var enemyRequestEntity = manager.CreateEntity();
				var spawnEnemyRequest = new SpawnEnemyRequest()
				{
					position = newEnemyPosition
				};
		
				// Set the component data we just created for the entity we just created
				manager.AddComponent<SpawnEnemyRequest>(enemyRequestEntity);
				manager.SetComponentData(enemyRequestEntity, spawnEnemyRequest);
			}
		}
	}
}