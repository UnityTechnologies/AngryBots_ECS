using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateAfter(typeof(MoveForwardSystem))]
[UpdateBefore(typeof(BulletCullingSystem))]
public class CollisionSystem : JobComponentSystem
{
	struct PlayerGroup
	{
		public readonly int Length;
		public ComponentDataArray<Health> health;
		public GameObjectArray playerObj;
		public ComponentDataArray<PlayerTag> tag;
	}

	struct EnemyGroup
	{
		public readonly int Length;
		public ComponentDataArray<Health> health;
		public ComponentDataArray<Position> position;
		public ComponentDataArray<EnemyTag> tag;
	}

	struct BulletGroup
	{
		public ComponentDataArray<Bullet> bullet;
		public ComponentDataArray<Position> position;
	}

	[Inject] PlayerGroup player;
	[Inject] EnemyGroup enemies;
	[Inject] BulletGroup bullets;


	[BurstCompile]
	struct BulletsVersusEnemies : IJobParallelFor
	{
		public float radius;
		public ComponentDataArray<Health> enemyHealth;
		[ReadOnly] public ComponentDataArray<Position> enemyPos;
		[ReadOnly] public ComponentDataArray<Position> bulletPos;
		[NativeDisableParallelForRestriction]
		public ComponentDataArray<Bullet> bullets;


		public void Execute(int eI)
		{
			float damage = 0f;

			for (int bI = 0; bI < bullets.Length; bI++)
			{
				if (CheckCollision(enemyPos[eI].Value, bulletPos[bI].Value, radius))
				{
					bullets[bI] = new Bullet { TimeToLive = 0f };
					damage += 1;
				}
			}
			if (damage > 0)
			{
				Health thisHealth = enemyHealth[eI];
				thisHealth.Value -= damage;
				enemyHealth[eI] = thisHealth;
			}
		}
	}

	[BurstCompile]
	struct EnemiesVersusPlayer : IJob
	{
		public float radius;

		public float3 playerPos;
		public ComponentDataArray<Health> playerHealth;
		[ReadOnly] public ComponentDataArray<Position> enemyPos;


		public void Execute()
		{
			float damage = 0f;

			for (int eIndex = 0; eIndex < enemyPos.Length; eIndex++)
			{
				if (CheckCollision(playerPos, enemyPos[eIndex].Value, radius))
					damage += 1;
			}

			if (damage > 0)
			{
				playerHealth[0] = new Health { Value = 0f };
			}
		}
	}

	protected override JobHandle OnUpdate(JobHandle inputDeps)
	{
		float enemyRadius = Settings.main.enemyCollisionRadius;
		float playerRadius = Settings.main.playerCollisionRadius;

		BulletsVersusEnemies beJob = new BulletsVersusEnemies
		{
			radius = enemyRadius * enemyRadius,
			enemyHealth = enemies.health,
			enemyPos = enemies.position,
			bulletPos = bullets.position,
			bullets = bullets.bullet
		};

		JobHandle beHandle = beJob.Schedule(enemies.Length, 1, inputDeps);

		if (player.Length <= 0)
			return beHandle;


		EnemiesVersusPlayer epJob = new EnemiesVersusPlayer
		{
			radius = playerRadius * playerRadius,
			playerPos = player.playerObj[0].transform.position,
			playerHealth = player.health,
			enemyPos = enemies.position
		};

		return epJob.Schedule(beHandle);
	}

	static bool CheckCollision(float3 posA, float3 posB, float radiusSqr)
	{
		float3 delta = posA - posB;
		float distanceSquare = delta.x * delta.x + delta.z * delta.z;

		return distanceSquare <= radiusSqr;
	}
}
