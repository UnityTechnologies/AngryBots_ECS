using Unity.Collections;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Experimental.PlayerLoop;

[UpdateAfter(typeof(Initialization))]
public class EnemySpawnSystem : ComponentSystem
{
	ComponentGroup spawnerGroup;

	protected override void OnCreateManager()
	{
		spawnerGroup = GetComponentGroup(typeof(Spawner));
	}

	protected override void OnUpdate()
	{
		if (Settings.main.player == null || !Settings.main.spawnEnemies)
			return;

		using (var spawnerEntityArray = spawnerGroup.ToEntityArray(Allocator.TempJob))
		{
			foreach (var spawnerEntity in spawnerEntityArray)
			{
				Spawner spawner = EntityManager.GetSharedComponentData<Spawner>(spawnerEntity);

				spawner.cooldown -= Time.deltaTime;

				if (spawner.cooldown <= 0f)
				{
					spawner.cooldown = Settings.main.enemySpawnRate;

					Entity enemyEntity = EntityManager.Instantiate(spawner.prefab);
					EntityManager.SetComponentData(enemyEntity, new Position { Value = Settings.GetPositionAroundPlayer() });
				}

				EntityManager.SetSharedComponentData(spawnerEntity, spawner);
			}
		}
	}
}

public static class EnemySpawnBootstrapper
{
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	public static void Initialize()
	{
		EntityManager manager = World.Active.GetOrCreateManager<EntityManager>();
		GameObject proto = Settings.main.enemyPrototype;

		//Setup spawner
		EntityArchetype spawnArch = manager.CreateArchetype(typeof(Spawner));

		Entity spawner = manager.CreateEntity(spawnArch);

		manager.SetSharedComponentData(spawner, new Spawner { cooldown = 0f,
														prefab = proto});
	}
}
