using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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


		//VERSION 2==============================================
		//var commandBuffer = SystemAPI
		//.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
		//.CreateCommandBuffer(state.WorldUnmanaged);

		//foreach (var (timer, entity) in SystemAPI.Query<RefRW<TimeToLive>>().WithEntityAccess())
		//{
		//	timer.ValueRW.Value -= Time.fixedDeltaTime;

		//	if (timer.ValueRO.Value < 0f)
		//	{
		//		commandBuffer.DestroyEntity(entity);
		//	}
		//}

		//commandBuffer.Playback(state.EntityManager);

		//}
		//VERSION 3 ================================================
		//var commandBuffer = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>().CreateCommandBuffer(state.WorldUnmanaged);

		//var timedDestroyJob = new TimedDestroyJob
		//{ 
		//	dt = SystemAPI.Time.DeltaTime,
		//	ecb = commandBuffer.AsParallelWriter()
		//};

		//timedDestroyJob.ScheduleParallel();
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