using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

[UpdateAfter(typeof(MoveForwardSystem))]
public class BulletCullingSystem : ComponentSystem
{
	struct BulletGroup
	{
		public readonly int Length;
		public EntityArray entity;
		public ComponentDataArray<Bullet> bullet;
	}
	[Inject] BulletGroup bullets;

	protected override void OnUpdate()
	{
		bool isPlayerDead = Settings.main.player == null;
		float dt = Time.deltaTime;

		for (int i = 0; i < bullets.Length; ++i)
		{
			Bullet b = bullets.bullet[i];
			b.TimeToLive -= dt;

			if (b.TimeToLive <= 0f || isPlayerDead)
				PostUpdateCommands.DestroyEntity(bullets.entity[i]);
	
			bullets.bullet[i] = b;
		}
	}
}

