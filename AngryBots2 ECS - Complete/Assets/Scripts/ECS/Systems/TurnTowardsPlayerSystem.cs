using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

[UpdateBefore(typeof(MoveForwardSystem))]
public class TurnTowardsPlayerSystem : JobComponentSystem
{
	[BurstCompile]
	[RequireComponentTag(typeof(EnemyTag))]
	struct TurnTowardsPlayerJob : IJobProcessComponentData<Translation, Rotation>
	{
		public float3 playerPosition; 
		public float dt;

		public void Execute([ReadOnly] ref Translation pos, ref Rotation rot)
		{
			float3 heading = playerPosition - pos.Value;
			heading.y = 0f;
			rot.Value = quaternion.LookRotation(heading, math.up());
		}
	}

	protected override JobHandle OnUpdate(JobHandle inputDeps)
	{
		if (Settings.main.player == null)
			return inputDeps;
		
		var job = new TurnTowardsPlayerJob
		{
			playerPosition = Settings.main.player.position,
			dt = Time.deltaTime
		};

		return job.Schedule(this, inputDeps);
	}
}

