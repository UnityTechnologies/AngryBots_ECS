using Unity.Burst;
using Unity.Entities;
using static Unity.Entities.SystemAPI;
using Unity.Transforms;
using Unity.Collections;

partial struct RemoveDeadSystem : ISystem
{
	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		state.RequireForUpdate<Health>();
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		// We could use this code to do something when the player's health drops to 0. Since
		// the PlayerController script already checks the health and responds, however, we don't
		// need to. This code is left here in case you want to see it or do something custom

		/*foreach (var (health, entity) in Query<RefRO<Health>>().WithAll<PlayerTag>().WithEntityAccess())
		{
			if (health.ValueRO.Value <= 0)
			{
			}
		}*/

		using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
		{
			foreach (var (health, entity) in SystemAPI.Query<RefRO<Health>>().WithAll<EnemyTag>().WithEntityAccess())
			{
				if (health.ValueRO.Value <= 0f)
				{
					commandBuffer.DestroyEntity(entity);
				}
			}

			commandBuffer.Playback(state.EntityManager);
		}
	}
}