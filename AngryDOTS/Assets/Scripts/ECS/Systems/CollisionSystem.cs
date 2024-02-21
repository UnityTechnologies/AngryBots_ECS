using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;


[BurstCompile]
[UpdateAfter(typeof(TurnTowardsPlayerSystem))]
partial struct CollisionSystem : ISystem
{
	EntityQuery enemyQuery;
	EntityQuery bulletQuery;
	EntityQuery playerQuery;

	float enemyCollisionRadius;
	float playerCollisionRadius;

	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		state.RequireForUpdate<EnemyTag>(); //if there are no enemies, this system doesn't need to run

		enemyQuery = SystemAPI.QueryBuilder().WithAll<Health, EnemyTag, LocalTransform>().Build();
		bulletQuery = SystemAPI.QueryBuilder().WithAll<TimeToLive, LocalTransform>().Build();
		playerQuery = SystemAPI.QueryBuilder().WithAll<Health, PlayerTag, LocalTransform>().Build();

		enemyCollisionRadius = Settings.EnemyCollisionRadius;
		playerCollisionRadius = Settings.PlayerCollisionRadius;
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		var jobEvB = new CollisionJob()
		{
			radius = enemyCollisionRadius * enemyCollisionRadius,
			transToTestAgainst = bulletQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		};

		state.Dependency = jobEvB.ScheduleParallel(enemyQuery, state.Dependency);

		var jobPvE = new CollisionJob()
		{
			radius = playerCollisionRadius * playerCollisionRadius,
			transToTestAgainst = enemyQuery.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		};

		state.Dependency = jobPvE.ScheduleParallel(playerQuery, state.Dependency);
	}
}

[BurstCompile]
partial struct CollisionJob : IJobEntity
{
	public float radius;

	[DeallocateOnJobCompletion]
	[ReadOnly] public NativeArray<LocalTransform> transToTestAgainst;

	public void Execute(ref Health health, in LocalTransform transform)
	{
		float damage = 0f;

		for (int i = 0; i < transToTestAgainst.Length; i++)
		{
			if (CheckCollision(transform.Position, transToTestAgainst[i].Position, radius))
				damage += 1;
		}

		if (damage > 0)
			health.Value -= damage;
	}

	bool CheckCollision(float3 posA, float3 posB, float radiusSqr)
	{
		float3 delta = posA - posB;
		float distanceSquare = delta.x * delta.x + delta.z * delta.z;

		return distanceSquare <= radiusSqr;
	}
}