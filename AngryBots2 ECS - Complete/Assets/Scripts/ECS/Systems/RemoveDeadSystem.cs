using Unity.Entities;
using Unity.Transforms;
using UnityEngine.Experimental.PlayerLoop;

[UpdateBefore(typeof(Initialization))]
public class RemoveDeadSystem : ComponentSystem
{
	protected override void OnUpdate()
	{
		Entities.ForEach((Entity entity, ref Health health, ref Translation pos) =>
		{
			if (health.Value <= 0)
			{
				if (EntityManager.HasComponent(entity, typeof(PlayerTag)))
				{
					Settings.PlayerDied();
				}

				else if (EntityManager.HasComponent(entity, typeof(EnemyTag)))
				{
					PostUpdateCommands.DestroyEntity(entity);
					BulletImpactPool.PlayBulletImpact(pos.Value);
				}
			}
		});
	}
}