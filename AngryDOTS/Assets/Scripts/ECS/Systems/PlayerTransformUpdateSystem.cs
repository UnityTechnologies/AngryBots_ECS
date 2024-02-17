using Unity.Entities;
using Unity.Transforms;

[RequireMatchingQueriesForUpdate]
[UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
public partial struct PlayerTransformUpdateSystem : ISystem
{
	public void OnUpdate(ref SystemState state)
	{
		if (Settings.IsPlayerDead())
			return;

		//foreach (var (timer, entity) in SystemAPI.Query<RefRW<TimeToLive>>().WithEntityAccess())
		//{
		//	timer.ValueRW.Value -= Time.fixedDeltaTime;

		//	if (timer.ValueRW.Value < 0f)
		//	{
		//		commandBuffer.DestroyEntity(entity);
		//	}
		//}

		//using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
		//{
		//	foreach (var (timer, entity) in SystemAPI.Query<RefRW<TimeToLive>>().WithEntityAccess())
		//	{
		//		timer.ValueRW.Value -= Time.fixedDeltaTime;

		//		if (timer.ValueRW.Value < 0f)
		//		{
		//			commandBuffer.DestroyEntity(entity);
		//		}
		//	}

		//	commandBuffer.Playback(state.EntityManager);
		//}
	}
}

//[UpdateBefore(typeof(CollisionSystem))]
//public class PlayerTransformUpdateSystem //: ComponentSystem
//{
	//protected override void OnUpdate()
	//{
	//	if (Settings.IsPlayerDead())
	//		return;

	//	Entities.WithAll<PlayerTag>().ForEach((ref Translation pos) =>
	//	{
	//		pos = new Translation { Value = Settings.PlayerPosition };
	//	});
	//}
//}