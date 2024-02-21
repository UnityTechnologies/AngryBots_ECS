using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;


[BurstCompile]
[RequireMatchingQueriesForUpdate]
public partial struct TimedDestroySystem : ISystem
{ 
		[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
		{
			foreach (var (timer, entity) in SystemAPI.Query<RefRW<TimeToLive>>().WithEntityAccess())
			{
				timer.ValueRW.Value -= Time.fixedDeltaTime;

				if (timer.ValueRW.Value < 0f)
				{
					commandBuffer.DestroyEntity(entity);
				}
			}

			commandBuffer.Playback(state.EntityManager);
		}
	}
}

[BurstCompile]
public partial struct TimedDestroyJob : IJobEntity //The query for IJobEntity is inferred by the Execute method
{
	public float dt;
	public EntityCommandBuffer.ParallelWriter ecb;

	void Execute(ref TimeToLive ttl, [ChunkIndexInQuery] int index, in Entity entity)
	{
		ttl.Value -= dt;
		if (ttl.Value < 0)
			ecb.DestroyEntity(index, entity);
	}
}