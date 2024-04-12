/* REMOVE DEAD SYSTEM
 * This system finds any entity that has a Health component and checks to see if the entity is
 * "dead" (health <= 0). If so, this system destroys that entity using a CommandBuffer. In this 
 * project, the two entity types it will affect are players and enemies
 */

using Unity.Burst;
using Unity.Entities;
using Unity.Collections;

[BurstCompile]
partial struct RemoveDeadSystem : ISystem
{
	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		// If there are no entities with Health, don't run this system
		state.RequireForUpdate<Health>();
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		// We could use this code to do something when the player's health drops to 0. Since
		// the PlayerController script already checks the health and responds, however, we don't
		// need to. This code is left here in case you want to see it or do something custom

		/*foreach (var (health, entity) in Query<RefRO<Health>>().WithAll<PlayerTag>().WithEntityAccess())
		{
			if (health.ValueRO.Value <= 0)
			{
			}
		}*/


		// This code uses a CommandBuffer, which lets us queue up commands for playback later.
		// Note: CommandBuffers must be cleaned up to prevent memory leaks, so the "using" syntax
		// is used here to automatically manage that
		using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
		{
			// This foreach is where you can find the query of this system. In this case, we are
			// looking for Health and EnemyTag components (since the death of enemies is handled
			// differently than the dead of the player). Additionally, we are using the
			// "WithEntityAccess()" which also gives us the entities themselves
			foreach (var (health, entity) in SystemAPI.Query<RefRO<Health>>().WithAll<EnemyTag>().WithEntityAccess())
			{
				// Access the value of health (local name for the Health component). Note how this
				// syntax uses "ValueRO" instead of just "Value". This is needed inside a foreach to 
				// specify the type of access needed for the component data
				if (health.ValueRO.Value <= 0f)
				{
					// If the health is <= 0, queue the entity to be destroy
					commandBuffer.DestroyEntity(entity);
				}
			}

			// After the foreach, playback the buffer, destroying the entities
			commandBuffer.Playback(state.EntityManager);
		}
		// Once the "using" block closes, the CommandBuffer will be cleaned up
	}
}