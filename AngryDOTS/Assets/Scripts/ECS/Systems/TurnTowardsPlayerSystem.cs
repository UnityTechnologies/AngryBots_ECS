using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
[UpdateBefore(typeof(MoveForwardSystem))]
partial struct TurnTowardsPlayerSystem : ISystem
{
	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		state.RequireForUpdate<EnemyTag>();
	}

	// NOT burst compiled
	public void OnUpdate(ref SystemState state)
	{
		//NOTE: This prevents OnUpdate from being Burst
		//compiled, but not the job itself
		if (Settings.IsPlayerDead())
			return;

		var TurnTowardsPlayerJob = new TurnTowardTargetJob
		{
			targetPosition = Settings.PlayerPosition
		};

		TurnTowardsPlayerJob.ScheduleParallel();
	}
}

[BurstCompile]
[WithAll(typeof(EnemyTag))]
public partial struct TurnTowardTargetJob : IJobEntity
{
	public float3 targetPosition;

	void Execute(ref LocalTransform transform)
	{
		float3 heading = targetPosition - transform.Position;
		heading.y = 0f;
		transform.Rotation = quaternion.LookRotation(heading, math.up());
	}
}
