//using Unity.Burst;
//using Unity.Collections;
//using Unity.Entities;
//using Unity.Jobs;
//using Unity.Rendering;
//using Unity.Transforms;
//using UnityEngine;

//[UpdateBefore(typeof(RenderMeshSystem))]
//public class RemoveDeadSystem : ComponentSystem
//{
//	struct NPCGroup
//	{
//		public readonly int Length;
//		[ReadOnly] public EntityArray entity;
//		[ReadOnly] public ComponentDataArray<Translation> position;
//		[ReadOnly] public ComponentDataArray<Health> health;
//		[ReadOnly] public SubtractiveComponent<PlayerTag> tag;
//	}
//	[Inject] NPCGroup NPCs;

//	struct PlayerGroup
//	{
//		public readonly int Length;
//		public GameObjectArray playerObj;
//		[ReadOnly] public ComponentDataArray<Health> health;
//		[ReadOnly] public ComponentDataArray<PlayerTag> tag;
//	}
//	[Inject] PlayerGroup player;


//	protected override void OnUpdate()
//	{
//		if (player.Length > 0 && player.health[0].Value <= 0f)
//			player.playerObj[0].GetComponent<PlayerMovementAndLook>().CollidedWithEnemy();

//		for (int i = 0; i < NPCs.Length; i++)
//		{
//			if (NPCs.health[i].Value <= 0f)
//			{
//				Vector3 position = NPCs.position[i].Value;
//				PostUpdateCommands.DestroyEntity(NPCs.entity[i]);

//				GameObject.Instantiate(Settings.main.bulletHitPrefab, position, Quaternion.identity);
//			}
//		}
//	}
//}