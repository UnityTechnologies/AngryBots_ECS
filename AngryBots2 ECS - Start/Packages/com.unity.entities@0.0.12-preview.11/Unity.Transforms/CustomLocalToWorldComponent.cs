using System;
using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Transforms
{
    /// <summary>
    /// For user-managed transforms. When present, TransformSystem will ignore transform compoents
    /// like Rotation, Position, Scale.
    /// </summary>
    [Serializable]
    public struct CustomLocalToWorld : IComponentData
    {
        public float4x4 Value;
    }

    public class CustomLocalToWorldComponent : ComponentDataWrapper<CustomLocalToWorld>
    {
    }
}
