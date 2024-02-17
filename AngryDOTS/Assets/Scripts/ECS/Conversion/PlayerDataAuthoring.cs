using System.Collections;
using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

public class PlayerDataAuthoring : MonoBehaviour
{
    class Baker : Baker<PlayerDataAuthoring>
    {
        public override void Bake(PlayerDataAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new PlayerData
            {
                position = authoring.transform.position
            }) ;
        }
    }
}
