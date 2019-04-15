using System;

namespace Unity.Entities
{
    public interface IComponentData
    {
    }

    public interface IBufferElementData
    {
    }

    [AttributeUsage(AttributeTargets.Struct)]
    public class InternalBufferCapacityAttribute : Attribute
    {
        public readonly int Capacity;

        public InternalBufferCapacityAttribute(int capacity)
        {
            Capacity = capacity;
        }
    }
    
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class MaximumChunkCapacityAttribute : Attribute
    {
        public readonly int Capacity;

        public MaximumChunkCapacityAttribute(int capacity)
        {
            Capacity = capacity;
        }
        
    }

    public interface ISharedComponentData
    {
    }

    public interface ISystemStateComponentData : IComponentData
    {
    }

    public interface ISystemStateBufferElementData : IBufferElementData
    {
    }

    public interface ISystemStateSharedComponentData : ISharedComponentData
    {
    }

    public struct Disabled : IComponentData
    {
    }
    
    public struct Prefab : IComponentData
    {
    }
    
    public struct LinkedEntityGroup : IBufferElementData
    {
        public Entity Value;
        
        public static implicit operator LinkedEntityGroup(Entity e)
        {
            return new LinkedEntityGroup {Value = e};
        }
    }
    
    [Serializable]
    public struct SceneTag : ISharedComponentData, IEquatable<SceneTag>
    {
        public Entity  SceneEntity;

        public override int GetHashCode()
        {
            return SceneEntity.GetHashCode();
        }

        public bool Equals(SceneTag other)
        {
            return SceneEntity == other.SceneEntity;
        }

        public override string ToString()
        {
            return $"SubSceneTag: {SceneEntity}";
        }
    }
}

