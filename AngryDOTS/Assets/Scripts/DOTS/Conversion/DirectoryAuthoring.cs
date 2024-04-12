/* DIRECTORY AUTHORING
* This script handles converting the Directory game object into an entity.
* The Directory is simply that: a directory of prefabs that will need to
* be converted to entities and then spawned at runtime. There will only be
* one Directory so that it can be found as a singleton entity
*/

using Unity.Entities;
using UnityEngine;

// This script will go on the Directory GameOject in the sub-scene
public class DirectoryAuthoring : MonoBehaviour
{
    public GameObject bulletPrefab; // The GameObject prefab of the projectiles
    public GameObject enemyPrefab;  // The GameObject prefab of the enemies

    // This class, Baker, is embedded in the DirectoryAuthoring class directly (though
    // it doesn't have to be, this is just nice and clean). It manages the baking
    // process that converts this GameObject to an Entity
    class Baker : Baker<DirectoryAuthoring>
    {
        // The one method of this class. This is where the baking work is done
        public override void Bake(DirectoryAuthoring authoring)
        {
            // First we create an empty entity. The TransformUsageFlags.None
            // means that this is an entity that doesn't have / need a transform
            var entity = GetEntity(TransformUsageFlags.None);

            // We will add a new Directory data component (defined below) to this entity
            AddComponent(entity, new Directory
            {
                // Here we use GetEntity to "convert" (bake) the bullet and enemy prefabs and
                // store them as data on this entity. Note that "authoring" is how we access
                // items in this MonoBehaviour. Addionally, TransformUsageFlags.Dynamic means
                // that these entities will be able to both move around, and be rendered
                bulletPrefab = GetEntity(authoring.bulletPrefab, TransformUsageFlags.Dynamic),
                enemyPrefab = GetEntity(authoring.enemyPrefab, TransformUsageFlags.Dynamic)
            }) ;
        }
    }
}

// This component contains a two Entity variables which will contain the entity IDs
// for the bullet and enemy entities
public struct Directory : IComponentData
{
    public Entity bulletPrefab;
    public Entity enemyPrefab;
}
