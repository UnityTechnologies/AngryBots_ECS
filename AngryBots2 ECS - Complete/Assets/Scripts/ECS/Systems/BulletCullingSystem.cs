using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;

[UpdateAfter(typeof(BulletCullingSystem))]
public class CullingBarrier : EntityCommandBufferSystem
{ }

[UpdateAfter(typeof(MoveForwardSystem))]
public class BulletCullingSystem : JobComponentSystem
{
	CullingBarrier barrier;

	protected override void OnCreateManager()
	{
		barrier = World.Active.GetOrCreateSystem<CullingBarrier>();
	}

	struct BulletCullingJob : IJobForEachWithEntity<TimeToLive>
	{
		public EntityCommandBuffer.Concurrent commands;
		public float dt;

		public void Execute(Entity entity, int jobIndex, ref TimeToLive timeToLive)
		{
			timeToLive.Value -= dt;
			if (timeToLive.Value <= 0f)
				commands.DestroyEntity(jobIndex, entity);
		}
	}

	protected override JobHandle OnUpdate(JobHandle inputDeps)
	{
		var job = new BulletCullingJob
		{
			commands = barrier.CreateCommandBuffer().ToConcurrent(),
			dt = Time.deltaTime
		};

		var handle = job.Schedule(this, inputDeps);
		barrier.AddJobHandleForProducer(handle);

		return handle;
	}
}

