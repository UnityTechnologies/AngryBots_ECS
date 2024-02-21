using Unity.Entities;
using UnityEngine;

public class EnemyAuthoring : MonoBehaviour
{
	public class EnemyBaker : Baker<EnemyAuthoring>
	{
		public override void Bake(EnemyAuthoring authoring)
		{
			var enemyBehaviour = authoring.GetComponent<EnemyBehaviour>();

			var entity = GetEntity(TransformUsageFlags.Dynamic);
			AddComponent(entity, new EnemyTag { });
			AddComponent(entity, new MoveForward { });

			AddComponent(entity, new MoveSpeed { Value = enemyBehaviour.speed });
			AddComponent(entity, new Health { Value = enemyBehaviour.enemyHealth });
		}
	}
}