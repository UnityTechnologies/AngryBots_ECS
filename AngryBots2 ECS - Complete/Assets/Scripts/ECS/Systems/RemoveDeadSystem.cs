using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

[UpdateBefore(typeof(MeshInstanceRenderer))]
public class RemoveDeadSystem : ComponentSystem
{
	struct NPCGroup
	{
		public readonly int Length;
		[ReadOnly] public EntityArray entity;
		[ReadOnly] public ComponentDataArray<Position> position;
		[ReadOnly] public ComponentDataArray<Health> health;
		[ReadOnly] public SubtractiveComponent<PlayerTag> tag;
	}
	[Inject] NPCGroup NPCs;

	struct PlayerGroup
	{
		public readonly int Length;
		public GameObjectArray playerObj;
		[ReadOnly] public ComponentDataArray<Health> health;
		[ReadOnly] public ComponentDataArray<PlayerTag> tag;
	}
	[Inject] PlayerGroup player;


	protected override void OnUpdate()
	{
		if (player.Length > 0 && player.health[0].Value <= 0f)
			player.playerObj[0].GetComponent<PlayerMovementAndLook>().CollidedWithEnemy();

		for (int i = 0; i < NPCs.Length; i++)
		{
			if (NPCs.health[i].Value <= 0f)
			{
				Vector3 position = NPCs.position[i].Value;
				PostUpdateCommands.DestroyEntity(NPCs.entity[i]);

				GameObject.Instantiate(Settings.main.bulletHitPrefab, position, Quaternion.identity);
			}
		}
	}
}

//==========ALTERNATE WAY OF REMOVING DEAD ENEMIES WITH A JOB===========
//public class RemoveDeadBarrier : BarrierSystem
//{
//}

//public class RemoveDeadSystem : JobComponentSystem
//{
//	struct Data
//	{
//		[ReadOnly] public EntityArray Entity;
//		[ReadOnly] public ComponentDataArray<Health> Health;
//		[ReadOnly] public SubtractiveComponent<PlayerTag> tag;
//	}

//	[Inject] private Data m_Data;
//	[Inject] private RemoveDeadBarrier m_RemoveDeadBarrier;

//	[BurstCompile]
//	struct RemoveReadJob : IJob
//	{
//		public bool playerDead;
//		[ReadOnly] public EntityArray Entity;
//		[ReadOnly] public ComponentDataArray<Health> Health;
//		public EntityCommandBuffer Commands;

//		public void Execute()
//		{
//			for (int i = 0; i < Entity.Length; ++i)
//			{
//				if (Health[i].Value <= 0.0f || playerDead)
//				{
//					Commands.DestroyEntity(Entity[i]);
//				}
//			}
//		}
//	}

//	protected override JobHandle OnUpdate(JobHandle inputDeps)
//	{
//		return new RemoveReadJob
//		{
//			playerDead = GameSettings.current.player == null,
//			Entity = m_Data.Entity,
//			Health = m_Data.Health,
//			Commands = m_RemoveDeadBarrier.CreateCommandBuffer(),
//		}.Schedule(inputDeps);
//	}
//}