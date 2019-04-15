using System;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

class TransformConversion : GameObjectConversionSystem
{
    protected override void OnUpdate()
    {
        Entities.ForEach((Transform transform) =>
        {
            var entity = GetPrimaryEntity(transform);

            DstEntityManager.AddComponentData(entity, new LocalToWorld { Value = transform.localToWorldMatrix});
            if (DstEntityManager.HasComponent<Static>(entity))
                return;
            
            //@TODO: This would be much better by knowing all participating game objects instead... 
            // We sometimes convert partial hierarchies.
            var convertToEntity = transform.GetComponent<ConvertToEntity>();
            var isPartialHierarchyRoot = transform.parent != null && convertToEntity != null && convertToEntity.ConversionMode == ConvertToEntity.Mode.ConvertAndDestroy;
            
            if (isPartialHierarchyRoot)
            {
                DstEntityManager.AddComponentData(entity, new Translation { Value = transform.position });
                DstEntityManager.AddComponentData(entity, new Rotation { Value = transform.rotation });
                if (transform.lossyScale!= Vector3.one)
                    DstEntityManager.AddComponentData(entity, new NonUniformScale{ Value = transform.lossyScale });
            }
            else
            {
                DstEntityManager.AddComponentData(entity, new Translation { Value = transform.localPosition });
                DstEntityManager.AddComponentData(entity, new Rotation { Value = transform.localRotation });
            
                if (transform.localScale != Vector3.one)
                    DstEntityManager.AddComponentData(entity, new NonUniformScale{ Value = transform.localScale });
            }



            if (transform.parent != null && !isPartialHierarchyRoot)
            {
                DstEntityManager.AddComponentData(entity, new Parent { Value = GetPrimaryEntity(transform.parent) });
                DstEntityManager.AddComponentData(entity, new LocalToParent());
            }
        });
    }
}
