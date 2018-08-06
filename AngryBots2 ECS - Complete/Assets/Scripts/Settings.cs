using Unity.Mathematics;
using UnityEngine;

public class Settings : MonoBehaviour
{
	public static Settings main;

	[Header("Game Object References")]
	public Transform player;
	public GameObject enemyPrototype;
	public GameObject bulletHitPrefab;
	
	[Header("Enemy Info")]
	public float enemySpeed = 2f;

	[Header ("Enemy Spawn Info")]
	public bool spawnEnemies = true;
	public float enemySpawnRate = 1f;
	public float enemySpawnRadius = 10f;

	[Header("Collision Info")]
	public float playerCollisionRadius = .5f;
	public float enemyCollisionRadius = .3f;


	void Awake()
	{
		if (main != null && main != this)
			Destroy(gameObject);
		else
			main = this;
	}

	public static Vector3 GetPositionAroundPlayer()
	{
		float radius = main.enemySpawnRadius;
		Vector3 playerPos = main.player.position;

		float angle = Random.Range(0f, 2 * Mathf.PI);
		float s = Mathf.Sin(angle);
		float c = Mathf.Cos(angle);
		
		return new Vector3(c * radius, 1f, s * radius) + playerPos;
	}
}
