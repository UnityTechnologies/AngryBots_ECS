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

	public void OnUpdate(ref SystemState state)
	{
		if (Settings.IsPlayerDead())
			return;

		var TurnTowardsPlayerJob = new TurnTowardsPlayerJob
		{
			playerPosition = Settings.PlayerPosition
		};

		TurnTowardsPlayerJob.ScheduleParallel();
	}
}

[BurstCompile]
[WithAll(typeof(EnemyTag))]
public partial struct TurnTowardsPlayerJob : IJobEntity 
{
	public float3 playerPosition;

	void Execute(ref LocalTransform transform)
	{
		float3 heading = playerPosition - transform.Position;
		heading.y = 0f;
		transform.Rotation = quaternion.LookRotation(heading, math.up());
	}
}
