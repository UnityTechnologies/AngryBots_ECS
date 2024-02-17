using Unity.Burst;
using Unity.Entities;
using static Unity.Entities.SystemAPI;
using Unity.Transforms;

partial struct RemoveDeadSystem : ISystem
{
	public void OnCreate(ref SystemState state)
	{
		state.RequireForUpdate<Health>();
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		foreach (var (health, entity) in Query<RefRO<Health>>().WithAll<PlayerTag>().WithEntityAccess())
		{
			if (health.ValueRO.Value <= 0)
			{
				//Settings.PlayerDied();
			}

		}

		foreach (var (health, transform, entity) in Query<RefRO<Health>, RefRO<LocalTransform>>().WithAll<EnemyTag>().WithEntityAccess())
		{
			if (health.ValueRO.Value <= 0)
			{
				var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
					.CreateCommandBuffer(state.WorldUnmanaged);

				ecb.DestroyEntity(entity);
				//BulletImpactPool.PlayBulletImpact(transform.ValueRO.Position);
			}

		}
	}

	//protected override void OnUpdate()
	//{
	//	Entities.ForEach((Entity entity, ref Health health, ref Translation pos) =>
	//	{
	//		if (health.Value <= 0)
	//		{
	//			if (EntityManager.HasComponent(entity, typeof(PlayerTag)))
	//			{
	//				Settings.PlayerDied();
	//			}

	//			else if (EntityManager.HasComponent(entity, typeof(EnemyTag)))
	//			{
	//				PostUpdateCommands.DestroyEntity(entity);
	//				BulletImpactPool.PlayBulletImpact(pos.Value);
	//			}
	//		}
	//	});
	//}
}