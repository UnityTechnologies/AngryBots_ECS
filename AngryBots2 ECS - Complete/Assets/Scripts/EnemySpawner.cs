using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
	[Header("Enemy Spawn Info")]
	public bool spawnEnemies = true;
	public bool useECS = false;
	public float enemySpawnCount = 1f;
	public GameObject enemyPrefab;

	float spawnInterval = 1f;
	float cooldown;
	EntityManager manager;
	Entity enemyEntityPrefab;


	void Start()
	{
		if (useECS)
		{
			manager = World.Active.EntityManager;
			enemyEntityPrefab = GameObjectConversionUtility.ConvertGameObjectHierarchy(enemyPrefab, World.Active);
		}
	}

	void Update()
    {
		if (!spawnEnemies || Settings.main.player == null)
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
		for (int i = 0; i < enemySpawnCount; i++)
		{
			Vector3 pos = Settings.GetPositionAroundPlayer();

			if (!useECS)
			{
				Instantiate(enemyPrefab, pos, Quaternion.identity);
			}
			else
			{
				Entity enemy = manager.Instantiate(enemyEntityPrefab);
				manager.SetComponentData(enemy, new Translation { Value = pos });
			}
		}
	}
}
