using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

//[UpdateAfter(typeof(MoveForwardSystem))]
//[UpdateBefore(typeof(TimedDestroySystem))]
[BurstCompile]
partial struct CollisionSystem : ISystem
{
	EntityQuery enemyGroup;
	EntityQuery bulletGroup;
	EntityQuery playerGroup;

	[BurstCompile]
	public void OnCreate(ref SystemState state)
	{
		state.RequireForUpdate<EnemyTag>(); //if there are no enemies, this system doesn't need to run

		enemyGroup = SystemAPI.QueryBuilder().WithAll<Health, EnemyTag, LocalTransform>().Build();
		bulletGroup = SystemAPI.QueryBuilder().WithAll<TimeToLive, LocalTransform>().Build();
		playerGroup = SystemAPI.QueryBuilder().WithAll<Health, PlayerTag, LocalTransform>().Build();
	}

	[BurstCompile]
	public void OnUpdate(ref SystemState state)
	{
		float enemyRadius = Settings.EnemyCollisionRadius;
		float playerRadius = Settings.PlayerCollisionRadius;

		var healthType = SystemAPI.GetComponentTypeHandle<Health>();
		var translationType = SystemAPI.GetComponentTypeHandle<LocalTransform>(true);

		var jobEvB = new CollisionJob()
		{
			radius = enemyRadius * enemyRadius,
			healthTypeHandle = healthType,
			transformTypeHandle = translationType,
			transToTestAgainst = bulletGroup.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		};

		state.Dependency = jobEvB.Schedule(enemyGroup, state.Dependency);
		//JobHandle jobHandle = jobEvB.Schedule(enemyGroup, state.Dependency);
		//if (Settings.IsPlayerDead())
		//{
		//	state.Dependency = jobHandle;
		//	return;
		//}
		////state.Dependency = jobHandle;
		//var jobPvE = new CollisionJob()
		//{
		//	radius = playerRadius * playerRadius,
		//	healthTypeHandle = healthType,
		//	transformTypeHandle = translationType,
		//	transToTestAgainst = enemyGroup.ToComponentDataArray<LocalTransform>(Allocator.TempJob)
		//};

		//state.Dependency = jobPvE.Schedule(playerGroup, jobHandle);
	}

	//EntityQuery enemyGroup;
	//EntityQuery bulletGroup;
	//EntityQuery playerGroup;

	//protected override void OnCreate()
	//{
	//	playerGroup = GetEntityQuery(typeof(Health), ComponentType.ReadOnly<Translation>(), ComponentType.ReadOnly<PlayerTag>());
	//	enemyGroup = GetEntityQuery(typeof(Health), ComponentType.ReadOnly<Translation>(), ComponentType.ReadOnly<EnemyTag>());
	//	bulletGroup = GetEntityQuery(typeof(TimeToLive), ComponentType.ReadOnly<Translation>());
	//}

	//protected override JobHandle OnUpdate(JobHandle inputDependencies)
	//{
	//	var healthType = GetArchetypeChunkComponentType<Health>(false);
	//	var translationType = GetArchetypeChunkComponentType<Translation>(true);

	//	float enemyRadius = Settings.EnemyCollisionRadius;
	//	float playerRadius = Settings.PlayerCollisionRadius;

	//	var jobEvB = new CollisionJob()
	//	{
	//		radius = enemyRadius * enemyRadius,
	//		healthType = healthType,
	//		translationType = translationType,
	//		transToTestAgainst = bulletGroup.ToComponentDataArray<Translation>(Allocator.TempJob)
	//	};

	//	JobHandle jobHandle = jobEvB.Schedule(enemyGroup, inputDependencies);

	//	if (Settings.IsPlayerDead())
	//		return jobHandle;

	//	var jobPvE = new CollisionJob()
	//	{
	//		radius = playerRadius * playerRadius,
	//		healthType = healthType,
	//		translationType = translationType,
	//		transToTestAgainst = enemyGroup.ToComponentDataArray<Translation>(Allocator.TempJob)
	//	};

	//	return jobPvE.Schedule(playerGroup, jobHandle);
	//}
}

[BurstCompile]
struct CollisionJob : IJobChunk
{
	public float radius;

	public ComponentTypeHandle<Health> healthTypeHandle;
	[ReadOnly] public ComponentTypeHandle<LocalTransform> transformTypeHandle;

	[DeallocateOnJobCompletion]
	[ReadOnly] public NativeArray<LocalTransform> transToTestAgainst;

	public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
	{
		Assert.IsFalse(useEnabledMask);

		var chunkHealths = chunk.GetNativeArray(ref healthTypeHandle);
		var chunkTransforms = chunk.GetNativeArray(ref transformTypeHandle);

		for (int i = 0; i < chunk.Count; i++)
		{
			float damage = 0f;
			Health health = chunkHealths[i];
			float3 pos = chunkTransforms[i].Position;

			for (int j = 0; j < transToTestAgainst.Length; j++)
			{
				float3 pos2 = transToTestAgainst[j].Position;

				if (CheckCollision(pos, pos2, radius))
				{
					damage += 1;
				}
			}

			if (damage > 0)
			{
				health.Value -= damage;
				chunkHealths[i] = health;
			}
		}
	}

	bool CheckCollision(float3 posA, float3 posB, float radiusSqr)
	{
		float3 delta = posA - posB;
		float distanceSquare = delta.x * delta.x + delta.z * delta.z;

		return distanceSquare <= radiusSqr;
	}
}
