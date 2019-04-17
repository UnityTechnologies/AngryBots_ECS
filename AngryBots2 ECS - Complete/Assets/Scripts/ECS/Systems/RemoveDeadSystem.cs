using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Experimental.PlayerLoop;

[UpdateAfter(typeof(CollisionSystem))]
public class RemoveDeadSystem : ComponentSystem
{
	EntityQuery enemyGroup;
	EntityQuery playerGroup;

	protected override void OnCreate()
	{
		//playerGroup = GetEntityQuery(typeof(Health), ComponentType.ReadOnly<Translation>(), ComponentType.ReadOnly<PlayerTag>());
		enemyGroup = GetEntityQuery(typeof(Health), ComponentType.ReadOnly<Translation>(), ComponentType.ReadOnly<EnemyTag>());
	}

	protected override void OnUpdate()
	{

		//Entities.ForEach((Entity e, Health playerHealth ) =>
		//{
		//	int toSpawnCount = spawner.Count;

		//	var spawnPositions = new NativeArray<float3>(toSpawnCount, Allocator.TempJob);
		//	GeneratePoints.RandomPointsInUnitSphere(spawnPositions);

		//	// Calling Instantiate once per spawned Entity is rather slow, and not recommended
		//	// This code is placeholder until we add the ability to bulk-instantiate many entities from an ECB
		//	var entities = new NativeArray<Entity>(toSpawnCount, Allocator.Temp);
		//	for (int i = 0; i < toSpawnCount; ++i)
		//	{
		//		entities[i] = PostUpdateCommands.Instantiate(spawner.Prefab);
		//	}

		//	for (int i = 0; i < toSpawnCount; i++)
		//	{
		//		PostUpdateCommands.SetComponent(entities[i], new LocalToWorld
		//		{
		//			Value = float4x4.TRS(
		//				localToWorld.Position + (spawnPositions[i] * spawner.Radius),
		//				quaternion.LookRotationSafe(spawnPositions[i], math.up()),
		//				new float3(1.0f, 1.0f, 1.0f))
		//		});
		//	}

		//	PostUpdateCommands.RemoveComponent<SpawnRandomInSphere>(e);

		//	spawnPositions.Dispose();
		//	entities.Dispose();
		//});

		//using (var playerEntityArray = playerGroup.ToEntityArray(Allocator.TempJob))
		//{
		//	foreach (var playerEntity in playerEntityArray)
		//	{
		//		var playerHealth = EntityManager.GetComponentData<Health>(playerEntity);
		//		var playerGameObject = EntityManager.GetComponentData<GameObjectComponent>(playerEntity);

		//		if (playerHealth.Value <= 0f)
		//			playerGameObject.GO.GetComponent<PlayerMovementAndLook>().CollidedWithEnemy();
		//	}
		//}

		using (var enemyEntityArray = enemyGroup.ToEntityArray(Allocator.TempJob))
		{
			foreach (var enemyEntity in enemyEntityArray)
			{
				var enemyHealth = EntityManager.GetComponentData<Health>(enemyEntity);
				var enemyTranslation = EntityManager.GetComponentData<Translation>(enemyEntity);

				if (enemyHealth.Value <= 0f)
				{
					Vector3 position = enemyTranslation.Value;
					PostUpdateCommands.DestroyEntity(enemyEntity);

					GameObject.Instantiate(Settings.main.bulletHitPrefab, position, Quaternion.identity);
				}
			}
		}
	}
}

//[UpdateBefore(typeof(RenderMeshSystem))]
//public class RemoveDeadSystem : ComponentSystem
//{
//	struct NPCGroup
//	{
//		public readonly int Length;
//		[ReadOnly] public EntityArray entity;
//		[ReadOnly] public ComponentDataArray<Translation> position;
//		[ReadOnly] public ComponentDataArray<Health> health;
//		[ReadOnly] public SubtractiveComponent<PlayerTag> tag;
//	}
//	[Inject] NPCGroup NPCs;

//	struct PlayerGroup
//	{
//		public readonly int Length;
//		public GameObjectArray playerObj;
//		[ReadOnly] public ComponentDataArray<Health> health;
//		[ReadOnly] public ComponentDataArray<PlayerTag> tag;
//	}
//	[Inject] PlayerGroup player;


//	protected override void OnUpdate()
//	{
//		if (player.Length > 0 && player.health[0].Value <= 0f)
//			player.playerObj[0].GetComponent<PlayerMovementAndLook>().CollidedWithEnemy();

//		for (int i = 0; i < NPCs.Length; i++)
//		{
//			if (NPCs.health[i].Value <= 0f)
//			{
//				Vector3 position = NPCs.position[i].Value;
//				PostUpdateCommands.DestroyEntity(NPCs.entity[i]);

//				GameObject.Instantiate(Settings.main.bulletHitPrefab, position, Quaternion.identity);
//			}
//		}
//	}
//}