using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Experimental.PlayerLoop;

[UpdateAfter(typeof(Initialization))]
public class EnemySpawnSystem : ComponentSystem
{
	struct SpawnerGroup
	{
		public ComponentDataArray<EnemySpawnCooldown> cooldown;
	}
	[Inject] SpawnerGroup spawner;


	protected override void OnUpdate()
	{
		if (Settings.main.player == null || !Settings.main.spawnEnemies)
			return;

		EnemySpawnCooldown cooldown = spawner.cooldown[0];
		cooldown.Value -= Time.deltaTime;		

		if (cooldown.Value <= 0f)
		{
			cooldown.Value = Settings.main.enemySpawnRate;

			Vector3 position = Settings.GetPositionAroundPlayer();

			PostUpdateCommands.CreateEntity(EnemySpawnBootstrapper.enemyArchetype);
			PostUpdateCommands.SetComponent(new Position { Value = position });
			PostUpdateCommands.SetComponent(new Health { Value = 1 });
			PostUpdateCommands.SetComponent(new MoveSpeed { speed = Settings.main.enemySpeed });
			PostUpdateCommands.SetSharedComponent(EnemySpawnBootstrapper.enemyRenderer);
		}
		spawner.cooldown[0] = cooldown;
	}
}

public static class EnemySpawnBootstrapper
{
	public static EntityArchetype enemyArchetype;
	public static MeshInstanceRenderer enemyRenderer;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	public static void Initialize()
	{
		EntityManager manager = World.Active.GetOrCreateManager<EntityManager>();

		//Set up enemy
		enemyArchetype = manager.CreateArchetype(typeof(EnemyTag),
												typeof(Health),
												typeof(Position),
												typeof(Rotation),
												typeof(MoveForward),
												typeof(MoveSpeed),
												typeof(MeshInstanceRenderer),
												typeof(TransformMatrix));

		GameObject proto = Settings.main.enemyPrototype;
		enemyRenderer = proto.GetComponent<MeshInstanceRendererComponent>().Value;

		//Setup spawner
		EntityArchetype spawnArch = manager.CreateArchetype(typeof(EnemySpawnCooldown));

		Entity spawner = manager.CreateEntity(spawnArch);
		manager.SetComponentData(spawner, new EnemySpawnCooldown { Value = 0f });
	}
}
