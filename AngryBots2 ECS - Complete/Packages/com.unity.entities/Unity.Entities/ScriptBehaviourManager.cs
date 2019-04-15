using System;
#if UNITY_EDITOR
using UnityEngine.Profiling;

#endif

namespace Unity.Entities
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class DisableAutoCreationAttribute : Attribute
    {
    }

    [Flags]
    public enum WorldSystemFilterFlags
    {
        Default                  = 1 << 0,
        GameObjectConversion     = 1 << 1,
        EntitySceneOptimizations = 1 << 2,
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class WorldSystemFilterAttribute : Attribute
    {
        public WorldSystemFilterFlags FilterFlags;

        public WorldSystemFilterAttribute(WorldSystemFilterFlags flags)
        {
            FilterFlags = flags;
        }
    }
}
