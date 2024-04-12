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

[BurstCompile]                              // Enable Burst compilation
[UpdateBefore(typeof(MoveForwardSystem))]   // Add a timing prerequisite to this system
partial struct TurnTowardsPlayerSystem : ISystem
{
	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		// Do not run this system if there are no enemy entities
		state.RequireForUpdate<EnemyTag>();
	}

	// NOTE: OnUpdate is NOT Burst compiled. This is because the method accesses data
	// in a managed object, Settings, which breaks the rules of Burst compilation.
	// We could re-write this code to get the data another way. For example, if you check
	// the PlayerController script you will see that the player's position is being written 
	// to an entity every frame. The code here is written like this purely as an example of
	// how to access managed data from a system. Furthermore, even though OnUpdate() won't 
	// Burst compile does not prevent the rest of the code from being Burst compiled. Even
	// the job scheduled from here can be Burst compiled. Sometimes this trade off is completely
	// acceptable 
	public void OnUpdate(ref SystemState state)
	{
		// Access this data prevents Burst compilation. This prevents jobs from being
		// scheduled if the player is dead
		if (Settings.IsPlayerDead())
			return;

		// Create a TurnTowardsTargetJob and pass it the player's position. 
		var TurnTowardsPlayerJob = new TurnTowardTargetJob
		{
			targetPosition = Settings.PlayerPosition // This code also prevents Burst
		};

		// Schedule this job as multi-threaded. Since we don't pass in a query, the
		// job itself will contain the query
		TurnTowardsPlayerJob.ScheduleParallel();
	}
}

[BurstCompile]
[WithAll(typeof(EnemyTag))]
// More information on IJobEntity can be found in the MoveForwardSystem.cs
// The query for this job is defined by the Execute() method. Since we only want this to
// process enemy entities, we specify the need for the EnemyTag as well (above). If we 
// wanted this job to be more generic, we would need to create a query in the System code
// above and pass it in when scheduling this job (you can see an example of that in the
// CollisionSytem.cs)
public partial struct TurnTowardTargetJob : IJobEntity
{
	// The position that the entities will turn towards
	public float3 targetPosition;

	// Execute will run once for each entity that matches the query
	void Execute(ref LocalTransform transform)
	{
		// Change the entity's rotation to point at the position
		float3 heading = targetPosition - transform.Position;
		heading.y = 0f;
		transform.Rotation = quaternion.LookRotation(heading, math.up());
	}
}
