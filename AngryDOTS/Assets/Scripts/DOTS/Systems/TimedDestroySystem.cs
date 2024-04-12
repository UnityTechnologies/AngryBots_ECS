/* TIMED DESTROY SYSTEM
 * This system finds any entity that has a TimeToLive component. It reduces the Value of
 * the TimeToLive component, and then destroys any entities that are out of time. Since 
 * destroying entities causes structural changes to memory, they are tricky to do in a
 * multi-threaded job. As such, this code runs on the main thread and uses an 
 * EntityCommandBuffer to queue up all the Destroy commands. In this project, the one 
 * entity type it will affect are bullets
 */

using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;


[BurstCompile]                      // Enable Burst compilation
[RequireMatchingQueriesForUpdate]	// Don't let this system run if there are no bullets
public partial struct TimedDestroySystem : ISystem
{ 
	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		// This code uses a CommandBuffer, which lets us queue up commands for playback later.
		// Note: CommandBuffers must be cleaned up to prevent memory leaks, so the "using" syntax
		// is used here to automatically manage that
		using (var commandBuffer = new EntityCommandBuffer(Allocator.TempJob))
		{
			// This foreach is where you can find the query of this system. In this case, we are
			// looking for TimeToLive components. Additionally, we are using the "WithEntityAccess()"
			// which also gives us the entities themselves
			foreach (var (timer, entity) in SystemAPI.Query<RefRW<TimeToLive>>().WithEntityAccess())
			{
				// Access the value of timer (local name for the TimeToLive component). Note how this
				// syntax uses "ValueRW" instead of just "Value". This is needed inside a foreach to 
				// make changes to component data
				timer.ValueRW.Value -= Time.fixedDeltaTime;

				// If the TimeToLive value (read-only here) is less than 0...
				if (timer.ValueRO.Value < 0f)
				{
					// Queue the destruction of this entity into the command buffer
					commandBuffer.DestroyEntity(entity);
				}
			}

			// After the foreach, playback the buffer, destroying the entities
			commandBuffer.Playback(state.EntityManager);
		}
		// Once the "using" block closes, the CommandBuffer will be cleaned up
	}
}