/* ENEMY SPAWNER
 * This script manages the process of spawning enemies as GameObjects.
 */

using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
	[Header("Enemy Spawn Info")]
	public GameObject enemyPrefab;

	float cooldown;


	void Start()
	{
		cooldown = Settings.Instance.enemySpawnInterval;
	}
	
	void Update()
    {
		if (!Settings.Instance.spawnEnemies || Settings.IsPlayerDead())
			return;

		cooldown -= Time.deltaTime;

		if (cooldown <= 0f)
		{
			cooldown += Settings.Instance.enemySpawnInterval;
			Spawn();
		}
    }

	void Spawn()
	{
		for (int i = 0; i < Settings.Instance.enemySpawnsPerInterval; i++)
		{
			Vector3 newEnemyPosition = 
				Settings.GetPositionAroundPlayer(Settings.Instance.enemySpawnRadius);

			if (!Settings.Instance.useECSforEnemies)
			{
				Instantiate(enemyPrefab, newEnemyPosition, Quaternion.identity);
			}
		}
	}
}