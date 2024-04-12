// ENEMY AUTHORING
// This script handles converting the Enemy prefab into an entity. The
// data components given to this entity will allow it to be used by
// the various systems that provide the functionality

using Unity.Entities;
using UnityEngine;

// This script will go on the Enemy prefab
public class EnemyAuthoring : MonoBehaviour
{
	// This class, Baker, is embedded in the EnemyAuthoring class directly (though
	// it doesn't have to be, this is just nice and clean). It manages the baking
	// process that converts this GameObject to an Entity
	public class EnemyBaker : Baker<EnemyAuthoring>
	{
		// The one method of this class. This is where the baking work is done
		public override void Bake(EnemyAuthoring authoring)
		{
			// First we access the EnemyBehaviour component of the Enemy prefab using
			// the authoring parameter. Note that we could put this Baker class directly
			// into the EnemyBehaviour script and then we would already have access to
			// its members. It is done this way just to keep the code clean
			var enemyBehaviour = authoring.GetComponent<EnemyBehaviour>();

			// Create a new entity. We use TransformUsageFlags.Dynamic because this
			// entity can both move and be rendered
			var entity = GetEntity(TransformUsageFlags.Dynamic);
			
			// Add the EnemyTag and MoveForwardTag data components to this entity. These
			// components are "tags" because they contain no data and are just used for
			// identification purposes
			AddComponent(entity, new EnemyTag { });
			AddComponent(entity, new MoveForward { });

			// Add the MoveSpeed and Health data components to the entity. These data
			// components get their values from the EnemyBehaviour component
			AddComponent(entity, new MoveSpeed { Value = enemyBehaviour.speed });
			AddComponent(entity, new Health { Value = enemyBehaviour.enemyHealth });
		}
	}
}