/* PROJECTILE AUTHORING
* This script handles converting the Bullet prefab into an entity. The
* data components given to this entity will allow it to be used by
* the various systems that provide the functionality
*/
using Unity.Entities;
using UnityEngine;

// This script will go on the Bullet prefab
public class ProjectileAuthoring : MonoBehaviour
{
	// This class, Baker, is embedded in the ProjectileAuthoring class directly (though
	// it doesn't have to be, this is just nice and clean). It manages the baking
	// process that converts this GameObject to an Entity
	public class ProjectileBaker : Baker<ProjectileAuthoring>
	{
		// The one method of this class. This is where the baking work is done
		public override void Bake(ProjectileAuthoring authoring)
		{
			// First we access the ProjectileBehaviour component of the Bullet prefab using
			// the authoring parameter. Note that we could put this Baker class directly
			// into the ProjectileBehaviour script and then we would already have access to
			// its members. It is done this way just to keep the code clean
			var projectileBehavior = authoring.GetComponent<ProjectileBehaviour>();

			// Create a new entity. We use TransformUsageFlags.Dynamic because this
			// entity can both move and be rendered
			var entity = GetEntity(TransformUsageFlags.Dynamic);

			// Add the MoveForwardTag data components to this entity. These components
			// are "tags" because they contain no data and are just used for identification
			// purposes. Note that unlike enemies and players, the projectiles have no "tag"
			// that identifies them as a projectile. Instead, all projectiles have a 
			// TimeToLive component (added below) and systems use that to find projectiles.
			// If this project were expanded and more entities has TimeToLive components,
			// it would likely become needed to create a projectile tag 
			AddComponent(entity, new MoveForward { });

			// Add the MoveSpeed and TimeToLive data components to the entity. These data
			// components get their values from the ProjectileBehaviour component
			AddComponent(entity, new MoveSpeed { Value = projectileBehavior.speed });
			AddComponent(entity, new TimeToLive { Value = projectileBehavior.lifeTime });
		}
	}
}
