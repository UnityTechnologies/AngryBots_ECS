using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

public class ConfigAuthoring : MonoBehaviour
{
    public GameObject bulletPrefab;
    public GameObject enemyPrefab;

    class Baker : Baker<ConfigAuthoring>
    {
        public override void Bake(ConfigAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new Config
            {
                bulletPrefab = GetEntity(authoring.bulletPrefab, TransformUsageFlags.Dynamic),
                enemyPrefab = GetEntity(authoring.enemyPrefab, TransformUsageFlags.Dynamic)
            }) ;
        }
    }
}

public struct Config : IComponentData
{
    public Entity bulletPrefab;
    public Entity enemyPrefab;
}
