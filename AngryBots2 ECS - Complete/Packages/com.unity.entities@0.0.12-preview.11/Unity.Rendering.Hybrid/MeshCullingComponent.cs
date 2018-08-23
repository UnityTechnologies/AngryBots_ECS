using Unity.Mathematics;
using Unity.Entities;

namespace Unity.Rendering
{
    public struct MeshCulledComponent : IComponentData
    {
    }

    public struct MeshCullingComponent : IComponentData
    {
        public float3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public float CullStatus;
    }
}
