using Unity.Entities;
using UnityEngine;

public class ProjectileAuthoring : MonoBehaviour
{
	public class ProjectileBaker : Baker<ProjectileAuthoring>
	{
		public override void Bake(ProjectileAuthoring authoring)
		{
			var projectileBehavior = authoring.GetComponent<ProjectileBehaviour>();

			var entity = GetEntity(TransformUsageFlags.Dynamic);
			AddComponent(entity, new TimeToLive { Value = projectileBehavior.lifeTime });
			AddComponent(entity, new MoveSpeed { Value = projectileBehavior.speed });
			AddComponent(entity, new MoveForward { });
		}
	}
}
