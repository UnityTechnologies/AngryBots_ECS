using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Transforms
{
	public class MoveForwardSystem : JobComponentSystem
	{
		[BurstCompile]
		[RequireComponentTag(typeof(MoveForward))]
		struct MoveForwardRotation : IJobForEach<Translation, Rotation, MoveSpeed>
		{
			public float dt;

			public void Execute(ref Translation pos, [ReadOnly] ref Rotation rot, [ReadOnly] ref MoveSpeed speed)
			{
				pos.Value = pos.Value + (dt * speed.Value * math.forward(rot.Value));
			}
		}

		protected override JobHandle OnUpdate(JobHandle inputDeps)
		{
			var moveForwardRotationJob = new MoveForwardRotation
			{
				dt = Time.DeltaTime
			};

			return moveForwardRotationJob.Schedule(this, inputDeps);
		}
	}
}