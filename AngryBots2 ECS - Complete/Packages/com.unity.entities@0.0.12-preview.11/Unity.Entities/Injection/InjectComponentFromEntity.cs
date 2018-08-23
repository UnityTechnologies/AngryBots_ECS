using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Entities
{
    internal struct InjectFromEntityData
    {
        private readonly InjectionData[] m_InjectComponentDataFromEntity;
        private readonly InjectionData[] m_InjectBufferDataFromEntity;

        public InjectFromEntityData(InjectionData[] componentDataFromEntity, InjectionData[] bufferDataFromEntity)
        {
            m_InjectComponentDataFromEntity = componentDataFromEntity;
            m_InjectBufferDataFromEntity = bufferDataFromEntity;
        }

        public static bool SupportsInjections(FieldInfo field)
        {
            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(ComponentDataFromEntity<>))
                return true;
            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(BufferDataFromEntity<>))
                return true;
            return false;
        }

        public static void CreateInjection(FieldInfo field, EntityManager entityManager,
            List<InjectionData> componentDataFromEntity, List<InjectionData> bufferDataFromEntity)
        {
            var isReadOnly = field.GetCustomAttributes(typeof(ReadOnlyAttribute), true).Length != 0;

            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(ComponentDataFromEntity<>))
            {
                var injection = new InjectionData(field, field.FieldType.GetGenericArguments()[0], isReadOnly);
                componentDataFromEntity.Add(injection);
            }
            else if (field.FieldType.IsGenericType &&
                     field.FieldType.GetGenericTypeDefinition() == typeof(BufferDataFromEntity<>))
            {
                var injection = new InjectionData(field, field.FieldType.GetGenericArguments()[0], isReadOnly);
                bufferDataFromEntity.Add(injection);
            }
            else
            {
                ComponentSystemInjection.ThrowUnsupportedInjectException(field);
            }
        }

        public unsafe void UpdateInjection(byte* pinnedSystemPtr, EntityManager entityManager)
        {
            for (var i = 0; i != m_InjectComponentDataFromEntity.Length; i++)
            {
                var array = entityManager.GetComponentDataFromEntity<ProxyComponentData>(
                    m_InjectComponentDataFromEntity[i].ComponentType.TypeIndex,
                    m_InjectComponentDataFromEntity[i].IsReadOnly);
                UnsafeUtility.CopyStructureToPtr(ref array,
                    pinnedSystemPtr + m_InjectComponentDataFromEntity[i].FieldOffset);
            }

            for (var i = 0; i != m_InjectBufferDataFromEntity.Length; i++)
            {
                var array = entityManager.GetBufferDataFromEntity<ProxyBufferElementData>(
                    m_InjectBufferDataFromEntity[i].ComponentType.TypeIndex,
                    m_InjectBufferDataFromEntity[i].IsReadOnly);
                UnsafeUtility.CopyStructureToPtr(ref array,
                    pinnedSystemPtr + m_InjectBufferDataFromEntity[i].FieldOffset);
            }
        }

        public void ExtractJobDependencyTypes(ComponentSystemBase system)
        {
            if (m_InjectComponentDataFromEntity != null)
                foreach (var injection in m_InjectComponentDataFromEntity)
                    system.AddReaderWriter(injection.ComponentType);

            if (m_InjectBufferDataFromEntity != null)
                foreach (var injection in m_InjectBufferDataFromEntity)
                    system.AddReaderWriter(injection.ComponentType);
        }
    }
}
