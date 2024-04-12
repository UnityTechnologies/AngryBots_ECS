/* COLLISION SYSTEM
 * This system manages the collision between bullets, enemies, and the player in a
 * very simple manner
 * 
 * WARNING: This code is incredibly inefficient. This was intentional as I wanted to
 * demonstrate how even poorly written code is very performant with Burst. You 
 * should NOT use this manner of collision detection in an actual game. Instead, use
 * DOTS physics or another performant solution
 */

using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;


[BurstCompile]									// Enable Burst compilation
[UpdateAfter(typeof(TurnTowardsPlayerSystem))]	// Add a timing prerequisite to this system
partial struct CollisionSystem : ISystem
{
	// The three queries this system will be using (Enemies, Bullets, and Player)
	EntityQuery enemyQuery;
	EntityQuery bulletQuery;
	EntityQuery playerQuery;

	// These variables will contain the unique collision radii for enemies and the player
	float enemyCollisionRadius;
	float playerCollisionRadius;

	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		// If there are no enemies, this system doesn't need to run
		state.RequireForUpdate<EnemyTag>(); 

		// Build and save the queries we will be using
		enemyQuery = SystemAPI.QueryBuilder().WithAll<Health, EnemyTag, LocalTransform>().Build();
		bulletQuery = SystemAPI.QueryBuilder().WithAll<TimeToLive, LocalTransform>().Build();
		playerQuery = SystemAPI.QueryBuilder().WithAll<Health, PlayerTag, LocalTransform>().Build();

		// Grab the radii values from the Settings script
		enemyCollisionRadius = Settings.EnemyCollisionRadius;
		playerCollisionRadius = Settings.PlayerCollisionRadius;
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		// Create a new CollisionJob for Enemies vs Bullets
		var jobEvB = new CollisionJob()
		{
			// Pass in the radius, which is squared for this algorithm
			radius = enemyCollisionRadius * enemyCollisionRadius,

			// Pass in a NativeArray of all of the Bullet transforms
			transToTestAgainst = bulletQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		};

		// Schedule this as a multi-threaded job. We pass in the query we want this job to
		// use (in this case, all the enemies) and the state dependency so Unity can
		// help managing timing for us. We then save the return value to state.Dependency
		// to properly manage further dependency tracking (we will use this again below)
		state.Dependency = jobEvB.ScheduleParallel(enemyQuery, state.Dependency);

		// Create a new CollisionJob for Player vs Enemies
		var jobPvE = new CollisionJob()
		{
			// Pass in the radius, which is squared for this algorithm
			radius = playerCollisionRadius * playerCollisionRadius,

			// Pass in a NativeArray of all of the Enemy transforms
			transToTestAgainst = enemyQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		};

		// Schedule this as a multi-threaded job, this time making it run on the player (or
		// players if we had more than one). Remember, state.Dependency is now referring to
		// the job we scheduled right before this one
		state.Dependency = jobPvE.ScheduleParallel(playerQuery, state.Dependency);
	}
}

[BurstCompile]
// This job is an IJobEntity even though we don't actually need the entity itself for the work
// we're doing. Instead, we chose this job type because the syntax is simple and convenient
partial struct CollisionJob : IJobEntity 
{
	// Collision radius
	public float radius;

	// Native Array of transforms we will be testing against. We want this NativeArray to be cleaned 
	// up once it is done being used, so we mark it as DeallocateOnJobCompletion
	[DeallocateOnJobCompletion]
	[ReadOnly] public NativeArray<LocalTransform> transToTestAgainst;

	// IJobEntity is nice because we can specify which data in our query we want to access
	// directly in the parameter list of the Execute method. In this case, this job will access
	// the Health and LocalTransform components of an entity. Execute is called once for each
	// entity that matches the query we passed in when we scheduled the job
	public void Execute(ref Health health, in LocalTransform transform)
	{
		float damage = 0f;

		// Loop through all the transforms we want to test this entity against (remember, this
		// way of checking collisions is intentionally simple and inefficient)
		for (int i = 0; i < transToTestAgainst.Length; i++)
		{
			// If there is a collision, increase the damage of this entity
			if (CheckCollision(transform.Position, transToTestAgainst[i].Position, radius))
				damage += 1;
		}

		// If any damage was taken, reduce the entity's health by that amount. We will
		// let a difference system actually manage the results of an entity "dying"
		if (damage > 0)
			health.Value -= damage;
	}

	// Some boilerplate position checking that finds determines if two 2D circles overlap (in
	// this case, the radii around our entities)
	bool CheckCollision(float3 posA, float3 posB, float radiusSqr)
	{
		float3 delta = posA - posB;
		float distanceSquare = delta.x * delta.x + delta.z * delta.z;

		return distanceSquare <= radiusSqr;
	}
}