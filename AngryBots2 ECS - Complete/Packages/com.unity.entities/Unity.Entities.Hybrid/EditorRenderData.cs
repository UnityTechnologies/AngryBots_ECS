using UnityEngine;

namespace Unity.Entities
{
    public struct EditorRenderData : ISharedComponentData
    {
        public ulong      SceneCullingMask;
        public GameObject PickableObject;
    }
}