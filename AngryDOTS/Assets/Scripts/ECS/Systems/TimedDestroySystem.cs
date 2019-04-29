using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;


[UpdateAfter(typeof(MoveForwardSystem))]
public class TimedDestroySystem : JobComponentSystem
{
	EndSimulationEntityCommandBufferSystem buffer;

	protected override void OnCreateManager()
	{
		buffer = World.Active.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
	}

	struct CullingJob : IJobForEachWithEntity<TimeToLive>
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
		var job = new CullingJob
		{
			commands = buffer.CreateCommandBuffer().ToConcurrent(),
			dt = Time.deltaTime
		};

		var handle = job.Schedule(this, inputDeps);
		buffer.AddJobHandleForProducer(handle);

		return handle;
	}
}

