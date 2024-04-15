/* TURN TOWARDS PLAYER SYSTEM
 * This system finds any entity that has a LocalTransform and EnemyTag component 
 * and turns the entity to point its Z axis towards a target (in this case, the player). 
 * In this project, the one entity type it will affect are enemies
 */

using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]                              
// Timing goes here 
partial struct TurnTowardsPlayerSystem : ISystem
{
	public void OnCreate(ref SystemState state)
	{
	
	}

	public void OnUpdate(ref SystemState state)
	{

	}
}


