using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;


public class CollisionSystem
{
	struct PlayerGroup
	{
		public readonly int Length;
		public GameObjectArray playerObj;
		//public ComponentDataArray<> ;
		//public ComponentDataArray<> ;
	}

	struct EnemyGroup
	{
		public readonly int Length;
		public ComponentDataArray<Position> position;
		//public ComponentDataArray<> ;
		//public ComponentDataArray<> ;
	}

	struct BulletGroup
	{
		public ComponentDataArray<Position> position;
		//public ComponentDataArray<> ;
	}

	[Inject] PlayerGroup player;
	[Inject] EnemyGroup enemies;
	[Inject] BulletGroup bullets;


	static bool CheckCollision(float3 posA, float3 posB, float radiusSqr)
	{
		float3 delta = posA - posB;
		float distanceSquare = delta.x * delta.x + delta.z * delta.z;

		return distanceSquare <= radiusSqr;
	}
}
