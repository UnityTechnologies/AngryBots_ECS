using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

[BurstCompile]
[UpdateBefore(typeof(MoveForwardSystem))]
partial struct TurnTowardsPlayerSystem : ISystem
{
	Entity playerDataEntity;
	float3 tempPos;

	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		tempPos = Vector3.zero;
		EntityQuery query = new EntityQueryBuilder(Allocator.Temp).WithAll<PlayerData>().Build(state.EntityManager);
		if (query.HasSingleton<PlayerData>())
			playerDataEntity = query.GetSingletonEntity();

		state.RequireForUpdate<EnemyTag>();
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		//if (Settings.IsPlayerDead())
		//return;
		float3 pos = Vector3.zero;
		EntityQuery query = new EntityQueryBuilder(Allocator.Temp).WithAll<PlayerData>().Build(state.EntityManager);
		if (query.HasSingleton<PlayerData>())
			pos = query.GetSingleton<PlayerData>().position;

		var TurnTowardsPlayerJob = new TurnTowardsPlayerJob
		{
			playerPosition = pos
		};

		TurnTowardsPlayerJob.ScheduleParallel();
	}
}

[BurstCompile]
[WithAll(typeof(EnemyTag))]
public partial struct TurnTowardsPlayerJob : IJobEntity //How does this compare to the older IJobParellelFor?
{
	public float3 playerPosition;

	void Execute(ref LocalTransform transform)
	{
		float3 heading = playerPosition - transform.Position;
		heading.y = 0f;
		transform.Rotation = quaternion.LookRotation(heading, math.up());
	}
}

//[UpdateBefore(typeof(MoveForwardSystem))]
//public class TurnTowardsPlayerSystem //: JobComponentSystem
//{
	//[BurstCompile]
	//[RequireComponentTag(typeof(EnemyTag))]
	//struct TurnJob : IJobForEach<Translation, Rotation>
	//{
	//	public float3 playerPosition; 

	//	public void Execute([ReadOnly] ref Translation pos, ref Rotation rot)
	//	{
	//		float3 heading = playerPosition - pos.Value;
	//		heading.y = 0f;
	//		rot.Value = quaternion.LookRotation(heading, math.up());
	//	}
	//}

	//protected override JobHandle OnUpdate(JobHandle inputDeps)
	//{
	//	if (Settings.IsPlayerDead())
	//		return inputDeps;

	//	var job = new TurnJob
	//	{
	//		playerPosition = Settings.PlayerPosition
	//	};

	//	return job.Schedule(this, inputDeps);
	//}
//}

