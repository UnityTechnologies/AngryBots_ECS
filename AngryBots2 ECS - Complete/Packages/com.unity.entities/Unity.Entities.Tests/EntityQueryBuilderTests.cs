using System;
// TEMPORARY HACK
//using JetBrains.Annotations;
using NUnit.Framework;

namespace Unity.Entities.Tests
{
    class EntityQueryBuilderTestFixture : ECSTestsFixture
    {
        [DisableAutoCreation]//, UsedImplicitly]
        protected class TestComponentSystem : ComponentSystem
            { protected override void OnUpdate() { } }

        protected static TestComponentSystem TestSystem => World.Active.GetOrCreateSystem<TestComponentSystem>();
    }

    class EntityQueryBuilderTests : EntityQueryBuilderTestFixture
    {
        [DisableAutoCreation]//, UsedImplicitly]
        class TestComponentSystem2 : ComponentSystem
            { protected override void OnUpdate() { } }

        static TestComponentSystem2 TestSystem2 => World.Active.GetOrCreateSystem<TestComponentSystem2>();

        [Test]
        public void WithGroup_WithNullGroup_Throws() =>
            Assert.Throws<ArgumentNullException>(() => TestSystem.Entities.With(null));

        [Test]
        public void WithGroup_WithExistingGroup_Throws()
        {
            var group0 = TestSystem.GetEntityQuery(ComponentType.ReadWrite<EcsTestData>());
            var group1 = TestSystem.GetEntityQuery(ComponentType.ReadOnly<EcsTestData>());

            var query = TestSystem.Entities.With(group0);

            Assert.Throws<InvalidOperationException>(() => query.With(group1));
        }

        [Test]
        public void WithGroup_WithExistingSpec_Throws()
        {
            var group = TestSystem.GetEntityQuery(ComponentType.ReadWrite<EcsTestData>());

            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.WithAny<EcsTestData>().With(group));
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.WithNone<EcsTestData>().With(group));
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.WithAll<EcsTestData>().With(group));
        }

        [Test]
        public void WithSpec_WithExistingGroup_Throws()
        {
            var group = TestSystem.GetEntityQuery(ComponentType.ReadWrite<EcsTestData>());

            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.With(group).WithAny<EcsTestData>());
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.With(group).WithNone<EcsTestData>());
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.With(group).WithAll<EcsTestData>());
        }

        [Test]
        public void Equals_WithMatchedButDifferentlyConstructedBuilders_ReturnsTrue()
        {
            var builder0 = TestSystem.Entities
                .WithAll<EcsTestTag>()
                .WithAny<EcsTestData, EcsTestData2>()
                .WithNone<EcsTestData3, EcsTestData4, EcsTestData5>();
            var builder1 = new EntityQueryBuilder(TestSystem)
                .WithNone<EcsTestData3>()
                .WithAll<EcsTestTag>()
                .WithAny<EcsTestData>()
                .WithNone<EcsTestData4, EcsTestData5>()
                .WithAny<EcsTestData2>();

            Assert.IsTrue(builder0.ShallowEquals(ref builder1));
        }

        [Test]
        public void Equals_WithSlightlyDifferentlyConstructedBuilders_ReturnsFalse()
        {
            var builder0 = TestSystem.Entities
                .WithAll<EcsTestTag>()
                .WithAny<EcsTestData, EcsTestData2>()
                .WithNone<EcsTestData3, EcsTestData4, EcsTestData5>();
            var builder1 = new EntityQueryBuilder(TestSystem)
                .WithAll<EcsTestTag>()
                .WithAny<EcsTestData>();

            Assert.IsFalse(builder0.ShallowEquals(ref builder1));
        }

        [Test]
        public void Equals_WithDifferentGroups_ReturnsFalse()
        {
            var group0 = TestSystem.GetEntityQuery(ComponentType.ReadWrite<EcsTestData>());
            var group1 = TestSystem.GetEntityQuery(ComponentType.ReadOnly<EcsTestData>());

            var builder0 = TestSystem.Entities.With(group0);
            var builder1 = TestSystem.Entities.With(group1);

            Assert.IsFalse(builder0.ShallowEquals(ref builder1));
        }

        [Test]
        public void ObjectGetHashCode_Throws()
        {
            // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.GetHashCode());
        }

        [Test]
        public void ObjectEquals_Throws()
        {
            // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
            Assert.Throws<InvalidOperationException>(() => TestSystem.Entities.Equals(null));
        }

        [Test]
        public void Equals_WithMismatchedSystems_Throws()
        {
            var builder0 = TestSystem.Entities;
            var builder1 = TestSystem2.Entities;

            Assert.Throws<InvalidOperationException>(() => builder0.ShallowEquals(ref builder1));
        }

        [Test]
        public void Equals_WithMismatchedBuilders_ReturnsFalse()
        {
            {
                var builder0 = TestSystem.Entities.WithAll<EcsTestData>();
                var builder1 = TestSystem.Entities.WithAll<EcsTestData2>();
                Assert.IsFalse(builder0.ShallowEquals(ref builder1));
            }

            {
                var builder0 = TestSystem.Entities.WithAny<EcsTestData3>();
                var builder1 = TestSystem.Entities.WithAny<EcsTestData3, EcsTestData4>();
                Assert.IsFalse(builder0.ShallowEquals(ref builder1));
            }

            {
                var builder0 = TestSystem.Entities.WithNone<EcsTestData3>();
                var builder1 = TestSystem.Entities.WithAll<EcsTestData3>();
                Assert.IsFalse(builder0.ShallowEquals(ref builder1));
            }

            {
                var group = TestSystem.GetEntityQuery(ComponentType.ReadWrite<EcsTestData>());
                var builder0 = TestSystem.Entities.With(group);
                var builder1 = TestSystem.Entities;
                Assert.IsFalse(builder0.ShallowEquals(ref builder1));
            }
        }

        [Test]
        public void ToEntityArchetypeQuery_WithFluentSpec_ReturnsQueryAsSpecified()
        {
            var eaq = TestSystem.Entities
                .WithAll<EcsTestTag>()
                .WithAny<EcsTestData, EcsTestData2>()
                .WithNone<EcsTestData3, EcsTestData4, EcsTestData5>()
                .ToEntityQueryDesc();

            CollectionAssert.AreEqual(
                new[] { ComponentType.ReadWrite<EcsTestTag>() },
                eaq.All);
            CollectionAssert.AreEqual(
                new[] { ComponentType.ReadWrite<EcsTestData>(), ComponentType.ReadWrite<EcsTestData2>() },
                eaq.Any);
            CollectionAssert.AreEqual(
                new[] { ComponentType.ReadWrite<EcsTestData3>(), ComponentType.ReadWrite<EcsTestData4>(), ComponentType.ReadWrite<EcsTestData5>() },
                eaq.None);
        }

        [Test]
        public void ToComponentGroup_OnceCached_StaysCached()
        {
            // this will cause the group to get cached in the query
            var query = TestSystem.Entities.WithAll<EcsTestTag>();
            query.ToEntityQuery();

            // this will throw because we're trying to modify the spec, yet we already have a group cached
            Assert.Throws<InvalidOperationException>(() => query.WithNone<EcsTestData>());
        }

        [Test]
        public void ForEach_WithReusedQueryButDifferentDelegateParams_Throws()
        {
            var entity = m_Manager.CreateEntity();
            m_Manager.AddComponentData(entity, new EcsTestData(0));
            m_Manager.AddComponentData(entity, new EcsTestData2(1));
            m_Manager.AddComponentData(entity, new EcsTestData3(2));

            var query = TestSystem.Entities.WithAll<EcsTestData, EcsTestData2>();
            var oldQuery = query;

            // validate that each runs with a different componentgroup (if the second shared the first, we'd get a null ref)

            query.ForEach((ref EcsTestData3 three) => { Assert.NotNull(three); });
            query.ForEach((ref EcsTestData4 four) => { Assert.NotNull(four); });

            // also validate that the query has not been altered by either ForEach

            Assert.IsTrue(oldQuery.ShallowEquals(ref query));

            var eaq = query.ToEntityQueryDesc();
            CollectionAssert.AreEqual(
                new[] { ComponentType.ReadWrite<EcsTestData>(), ComponentType.ReadWrite<EcsTestData2>() },
                eaq.All);
        }
    }
}
