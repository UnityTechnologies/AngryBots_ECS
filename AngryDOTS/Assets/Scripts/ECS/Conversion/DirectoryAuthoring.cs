using Unity.Entities;
using UnityEngine;

public class DirectoryAuthoring : MonoBehaviour
{
    public GameObject bulletPrefab;
    public GameObject enemyPrefab;

    class Baker : Baker<DirectoryAuthoring>
    {
        public override void Bake(DirectoryAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new Directory
            {
                bulletPrefab = GetEntity(authoring.bulletPrefab, TransformUsageFlags.Dynamic),
                enemyPrefab = GetEntity(authoring.enemyPrefab, TransformUsageFlags.Dynamic)
            }) ;
        }
    }
}

public struct Directory : IComponentData
{
    public Entity bulletPrefab;
    public Entity enemyPrefab;
}
