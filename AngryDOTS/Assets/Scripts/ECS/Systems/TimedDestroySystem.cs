using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;


[UpdateAfter(typeof(MoveForwardSystem))]
public class TimedDestroySystem : JobComponentSystem
{
	EndSimulationEntityCommandBufferSystem buffer;

	protected void OnCreateManager()
	{
		buffer = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<EndSimulationEntityCommandBufferSystem>();
	}

	//struct CullingJob : IJobForEachWithEntity<TimeToLive>
	//{
	//	public EntityCommandBuffer.Concurrent commands;
	//	public float dt;

	//	public void Execute(Entity entity, int jobIndex, ref TimeToLive timeToLive)
	//	{
	//		timeToLive.Value -= dt;
	//		if (timeToLive.Value <= 0f)
	//			commands.DestroyEntity(jobIndex, entity);
	//	}
	//}

	//protected override JobHandle OnUpdate(JobHandle inputDeps)
	//{
	//	var job = new CullingJob
	//	{
	//		commands = buffer.CreateCommandBuffer().ToConcurrent(),
	//		dt = UnityEngine.Time.deltaTime
	//	};

	//	var handle = job.Schedule(this, inputDeps);
	//	buffer.AddJobHandleForProducer(handle);

	//	return handle;
	//}

	protected override JobHandle OnUpdate(JobHandle inputDeps)
	{
		EntityCommandBuffer.Concurrent commands
			= buffer.CreateCommandBuffer().ToConcurrent();

		float dt = Time.DeltaTime;

		//entityInQueryIndex = JobIndex
		JobHandle handle = Entities
			.ForEach((Entity entity,
			int entityInQueryIndex,
			ref TimeToLive timeToLive) =>
			{
				timeToLive.Value -= dt;
				if (timeToLive.Value <= 0f)
					commands.DestroyEntity(entityInQueryIndex, entity);
			})
			.WithName("CullOldEntities")
			.Schedule(inputDeps);

		buffer.AddJobHandleForProducer(handle);
		return handle;
	}
}

