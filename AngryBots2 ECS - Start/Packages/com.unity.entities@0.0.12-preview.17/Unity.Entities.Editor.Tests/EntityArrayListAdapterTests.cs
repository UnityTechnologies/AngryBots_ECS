using NUnit.Framework;
using Unity.Collections;
using Unity.Entities.Tests;

namespace Unity.Entities.Editor.Tests
{
    public class EntityArrayListAdapterTests : ECSTestsFixture
    {
        [Test]
        public void EntityArrayListAdapter_SequentialAccessConsistent()
        {
            var archetype = m_Manager.CreateArchetype(new ComponentType[] {typeof(EcsTestData), typeof(EcsTestData2), typeof(EcsTestData3),
                typeof(EcsTestData4)});
            using (var entities = new NativeArray<Entity>(100000, Allocator.Temp))
            {
                m_Manager.CreateEntity(archetype, entities);
            }

            var query = new EntityArchetypeQuery()
            {
                Any = new ComponentType[0],
                All = new ComponentType[0],
                None = new ComponentType[0]
            };

            using (var chunkArray = m_Manager.CreateArchetypeChunkArray(query, Allocator.TempJob))
            {
                var adapter = new EntityArrayListAdapter();
                adapter.SetSource(chunkArray, m_Manager);
                var e1 = adapter[50001].id;
                var e2 = adapter[50002].id;
                var e3 = adapter[50003].id;
                var e2Again = adapter[50002].id;
                var e1Again = adapter[50001].id;
                Assert.AreNotEqual(e1, e2);
                Assert.AreNotEqual(e1, e2Again);
                Assert.AreNotEqual(e2, e1Again);
                Assert.AreEqual(e1, e1Again);
                Assert.AreEqual(e2, e2Again);
            }
            
        }
    }
}