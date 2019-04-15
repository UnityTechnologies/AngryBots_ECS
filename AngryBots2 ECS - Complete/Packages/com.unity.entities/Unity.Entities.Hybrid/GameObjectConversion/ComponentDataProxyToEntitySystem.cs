using Unity.Entities;
using UnityEngine;

[DisableAutoCreation]
class ComponentDataProxyToEntitySystem : GameObjectConversionSystem
{
    protected override void OnUpdate()
    {
        Entities.ForEach((Transform transform) => 
        {            
            GameObjectEntity.CopyAllComponentsToEntity(transform.gameObject, DstEntityManager, GetPrimaryEntity(transform));
        });
    }
}
