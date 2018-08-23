using NUnit.Framework;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Unity.Entities.Tests
{
    [TestFixture]
    public class TransformTests : ECSTestsFixture
    {
        [Test]
        public void TRS_ChildPosition()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var child = m_Manager.CreateEntity(typeof(Position));
            var attach = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            var childWorldPosition = m_Manager.GetComponentData<LocalToWorld>(child).Value.c3;
            Assert.AreEqual(expected:1.0f,actual:childWorldPosition.x,delta:0.01f);
            Assert.AreEqual(expected:2.0f,actual:childWorldPosition.y,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:childWorldPosition.z,delta:0.01f);
        }

        [Test]
        public void TRS_ParentAddedRemoved()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var child = m_Manager.CreateEntity(typeof(Position));
            var attach = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            Assert.IsTrue(m_Manager.HasComponent<Attached>(child));
            Assert.IsTrue(m_Manager.HasComponent<Parent>(child));
            Assert.IsFalse(m_Manager.Exists(attach));

            m_Manager.DestroyEntity(parent);
            m_Manager.DestroyEntity(child);

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            Assert.IsFalse(m_Manager.Exists(parent));
            Assert.IsFalse(m_Manager.Exists(child));
        }

        [Test]
        public void TRS_FreezeChild()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var child = m_Manager.CreateEntity(typeof(Position),typeof(Static));
            var attach = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(0.0f, 1.0f, 0.0f), math.up())});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            Assert.IsFalse(m_Manager.Exists(attach));
            Assert.IsTrue(m_Manager.HasComponent<Frozen>(child));

            var childWorldPosition = m_Manager.GetComponentData<LocalToWorld>(child).Value.c3;
            Assert.AreEqual(expected:1.0f,actual:childWorldPosition.x,delta:0.01f);
            Assert.AreEqual(expected:2.0f,actual:childWorldPosition.y,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:childWorldPosition.z,delta:0.01f);

            m_Manager.DestroyEntity(parent);
            m_Manager.DestroyEntity(child);

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            Assert.AreEqual(0, m_ManagerDebug.EntityCount);
        }

        [Test]
        public void TRS_ParentChangesChild()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var child = m_Manager.CreateEntity(typeof(Position));
            var attach = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(0.0f, 1.0f, 0.0f), math.up())});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            var childWorldPosition = m_Manager.GetComponentData<LocalToWorld>(child).Value.c3;
            Assert.AreEqual(expected:0.0f,actual:childWorldPosition.x,delta:0.01f);
            Assert.AreEqual(expected:3.0f,actual:childWorldPosition.y,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:childWorldPosition.z,delta:0.01f);
        }

        [Test]
        public void TRS_InnerDepth()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var parent2 = m_Manager.CreateEntity(typeof(Position));
            var child = m_Manager.CreateEntity(typeof(Position));
            var attach = m_Manager.CreateEntity(typeof(Attach));
            var attach2 = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(parent2, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});

            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = parent2});
            m_Manager.SetComponentData(attach2, new Attach {Parent = parent2, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            var parentDepth = m_Manager.GetSharedComponentData<Depth>(parent);
            var parent2Depth = m_Manager.GetSharedComponentData<Depth>(parent2);

            Assert.AreEqual(0,parentDepth.Value);
            Assert.AreEqual(1,parent2Depth.Value);

            var childWorldPosition = m_Manager.GetComponentData<LocalToWorld>(child).Value.c3;
            Assert.AreEqual(expected:2.0f,actual:childWorldPosition.x,delta:0.01f);
            Assert.AreEqual(expected:2.0f,actual:childWorldPosition.y,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:childWorldPosition.z,delta:0.01f);
        }

        [Test]
        public void TRS_LocalPositions()
        {
            var parent = m_Manager.CreateEntity(typeof(Position), typeof(Rotation));
            var parent2 = m_Manager.CreateEntity(typeof(Position));
            var child = m_Manager.CreateEntity(typeof(Position));
            var attach = m_Manager.CreateEntity(typeof(Attach));
            var attach2 = m_Manager.CreateEntity(typeof(Attach));

            m_Manager.SetComponentData(parent, new Position {Value = new float3(0, 2, 0)});
            m_Manager.SetComponentData(parent, new Rotation {Value = quaternion.lookRotation(new float3(1.0f, 0.0f, 0.0f), math.up())});
            m_Manager.SetComponentData(parent2, new Position {Value = new float3(0, 0, 1)});
            m_Manager.SetComponentData(child, new Position {Value = new float3(0, 0, 1)});

            m_Manager.SetComponentData(attach, new Attach {Parent = parent, Child = parent2});
            m_Manager.SetComponentData(attach2, new Attach {Parent = parent2, Child = child});

            World.GetOrCreateManager<EndFrameTransformSystem>().Update();

            var parent2LocalPosition = m_Manager.GetComponentData<LocalToParent>(parent2).Value.c3;
            Assert.AreEqual(expected:0.0f,actual:parent2LocalPosition.x,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:parent2LocalPosition.y,delta:0.01f);
            Assert.AreEqual(expected:1.0f,actual:parent2LocalPosition.z,delta:0.01f);

            var childLocalPosition = m_Manager.GetComponentData<LocalToParent>(child).Value.c3;
            Assert.AreEqual(expected:0.0f,actual:childLocalPosition.x,delta:0.01f);
            Assert.AreEqual(expected:0.0f,actual:childLocalPosition.y,delta:0.01f);
            Assert.AreEqual(expected:1.0f,actual:childLocalPosition.z,delta:0.01f);
        }
    }
}
