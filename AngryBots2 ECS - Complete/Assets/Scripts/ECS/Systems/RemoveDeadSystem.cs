using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Experimental.PlayerLoop;

[UpdateBefore(typeof(Initialization))]
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
		Entities.ForEach((Entity entity, ref Health health, ref Translation pos) =>
		{
			if (EntityManager.HasComponent(entity, typeof(PlayerTag)))
			{
				Debug.Log(pos.Value);
			}

			if (health.Value <= 0)
			{
				if (EntityManager.HasComponent(entity, typeof(PlayerTag)))
				{
					Settings.main.PlayerDied();
				}

				else if (EntityManager.HasComponent(entity, typeof(EnemyTag)))
				{
					PostUpdateCommands.DestroyEntity(entity);
					GameObject.Instantiate(Settings.main.bulletHitPrefab, pos.Value, Quaternion.identity);
				}
			}

		});

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

		//using (var enemyEntityArray = enemyGroup.ToEntityArray(Allocator.TempJob))
		//{
		//	foreach (var enemyEntity in enemyEntityArray)
		//	{
		//		var enemyHealth = EntityManager.GetComponentData<Health>(enemyEntity);
		//		var enemyTranslation = EntityManager.GetComponentData<Translation>(enemyEntity);

		//		if (enemyHealth.Value <= 0f)
		//		{
		//			Vector3 position = enemyTranslation.Value;
		//			PostUpdateCommands.DestroyEntity(enemyEntity);

		//			GameObject.Instantiate(Settings.main.bulletHitPrefab, position, Quaternion.identity);
		//		}
		//	}
		//}
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