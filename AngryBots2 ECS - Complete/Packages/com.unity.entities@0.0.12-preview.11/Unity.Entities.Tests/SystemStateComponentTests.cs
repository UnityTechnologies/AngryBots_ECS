using NUnit.Framework;
using Unity.Collections;
using System;
using NUnit.Framework.Interfaces;

namespace Unity.Entities.Tests
{
    [TestFixture]
    public class SystemStateComponentTests : ECSTestsFixture
    {
        [Test]
        public void SSC_DeleteWhenEmpty()
        {
            var entity = m_Manager.CreateEntity(
                typeof(EcsTestData),
                typeof(EcsTestSharedComp),
                typeof(EcsState1)
            );

            m_Manager.SetComponentData(entity, new EcsTestData(1));
            m_Manager.SetComponentData(entity, new EcsState1(2));
            m_Manager.SetSharedComponentData(entity, new EcsTestSharedComp(3));

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(),
                    None = Array.Empty<ComponentType>(),
                    All = new ComponentType[] {typeof(EcsTestData)}
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(1, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            m_Manager.DestroyEntity(entity);

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsTestData)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(0, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsState1)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(1, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            m_Manager.RemoveComponent<EcsState1>(entity);

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsState1)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(0, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            Assert.IsFalse(m_Manager.Exists(entity));
        }

        [Test]
        public void SSC_DeleteWhenEmptyArray()
        {
            var entities = new Entity[512];

            for (var i = 0; i < 512; i++)
            {
                var entity = m_Manager.CreateEntity(
                    typeof(EcsTestData),
                    typeof(EcsTestSharedComp),
                    typeof(EcsState1)
                );
                entities[i] = entity;

                m_Manager.SetComponentData(entity, new EcsTestData(i));
                m_Manager.SetComponentData(entity, new EcsState1(i));
                m_Manager.SetSharedComponentData(entity, new EcsTestSharedComp(i % 7));
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsTestData)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(512, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 512; i += 2)
            {
                var entity = entities[i];
                m_Manager.DestroyEntity(entity);
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsTestData)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = new ComponentType[] {typeof(EcsTestData)}, // none
                    All = new ComponentType[] {typeof(EcsState1)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 512; i += 2)
            {
                var entity = entities[i];
                m_Manager.RemoveComponent<EcsState1>(entity);
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), 
                    None = Array.Empty<ComponentType>(),
                    All = new ComponentType[] {typeof(EcsState1)}
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 512; i += 2)
            {
                var entity = entities[i];
                Assert.IsFalse(m_Manager.Exists(entity));
            }

            for (var i = 1; i < 512; i += 2)
            {
                var entity = entities[i];
                Assert.IsTrue(m_Manager.Exists(entity));
            }
        }
        
        [Test]
        public void SSC_DeleteWhenEmptyArray2()
        {
            var entities = new Entity[512];

            for (var i = 0; i < 512; i++)
            {
                var entity = m_Manager.CreateEntity(
                    typeof(EcsTestData),
                    typeof(EcsTestSharedComp),
                    typeof(EcsState1)
                );
                entities[i] = entity;

                m_Manager.SetComponentData(entity, new EcsTestData(i));
                m_Manager.SetComponentData(entity, new EcsState1(i));
                m_Manager.SetSharedComponentData(entity, new EcsTestSharedComp(i % 7));
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsTestData)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(512, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 256; i++)
            {
                var entity = entities[i];
                m_Manager.DestroyEntity(entity);
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsTestData)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // any
                    None = new ComponentType[] {typeof(EcsTestData)}, // none
                    All = new ComponentType[] {typeof(EcsState1)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 256; i++)
            {
                var entity = entities[i];
                m_Manager.RemoveComponent<EcsState1>(entity);
            }

            {
                var query = new EntityArchetypeQuery
                {
                    Any = Array.Empty<ComponentType>(), // none
                    None = Array.Empty<ComponentType>(), // none
                    All = new ComponentType[] {typeof(EcsState1)}, // all
                };
                var chunks = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob);
                Assert.AreEqual(256, ArchetypeChunkArray.CalculateEntityCount(chunks));
                chunks.Dispose();
            }

            for (var i = 0; i < 256; i++)
            {
                var entity = entities[i];
                Assert.IsFalse(m_Manager.Exists(entity));
            }

            for (var i = 256; i < 512; i++)
            {
                var entity = entities[i];
                Assert.IsTrue(m_Manager.Exists(entity));
            }
        }
    }
}
