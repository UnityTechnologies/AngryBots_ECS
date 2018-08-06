using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateBefore(typeof(MoveForwardSystem))]
public class TurnTowardsPlayerSystem : ComponentSystem
{
	struct EnemyGroup
	{
		public ComponentDataArray<Rotation> rotation;
		[ReadOnly] public ComponentDataArray<Position> position;
		[ReadOnly] public ComponentDataArray<EnemyTag> tag;
	}
	[Inject]
	EnemyGroup enemies;


	protected override void OnUpdate()
	{
		if (Settings.main.player == null)
			return;

		float3 playerPos = Settings.main.player.position;
		for (int i = 0; i < enemies.position.Length; i++)
		{
			float3 heading = playerPos - enemies.position[i].Value;
			heading.y = 0f;
			enemies.rotation[i] = new Rotation(quaternion.lookRotation(heading, math.up()));
		}
	}
}
