using NUnit.Framework;
using Unity.Collections;

namespace Unity.Entities.Tests
{
    class IJobForEachCombinationsTests : ECSTestsFixture
    {
        struct Process1 : IJobForEach<EcsTestData>
        {
            public void Execute(ref EcsTestData value)
            {
                value.value++;
            }
        }

        struct Process2 : IJobForEach<EcsTestData, EcsTestData2>
        {
            public void Execute([ReadOnly]ref EcsTestData src, ref EcsTestData2 dst)
            {
                dst.value1 = src.value;
            }
        }

        struct Process3 : IJobForEach<EcsTestData, EcsTestData2, EcsTestData3>
        {
            public void Execute([ReadOnly]ref EcsTestData src, ref EcsTestData2 dst1, ref EcsTestData3 dst2)
            {
                dst1.value1 = dst2.value2 = src.value;
            }
        }
        
        struct Process4 : IJobForEach<EcsTestData, EcsTestData2, EcsTestData3, EcsTestData4>
        {
            public void Execute([ReadOnly]ref EcsTestData src, ref EcsTestData2 dst1, ref EcsTestData3 dst2, ref EcsTestData4 dst3)
            {
                dst1.value1 = dst2.value2 = dst3.value3 = src.value;
            }
        }
        
        struct Process1Entity : IJobForEachWithEntity<EcsTestData>
        {
            public void Execute(Entity entity, int index, ref EcsTestData value)
            {
                value.value += entity.Index + index;
            }
        }

        struct Process2Entity  : IJobForEachWithEntity<EcsTestData, EcsTestData2>
        {
            public void Execute(Entity entity, int index, [ReadOnly]ref EcsTestData src, ref EcsTestData2 dst)
            {
                dst.value1 = src.value + entity.Index + index;
            }
        }

        struct Process3Entity  : IJobForEachWithEntity<EcsTestData, EcsTestData2, EcsTestData3>
        {
            public void Execute(Entity entity, int index, [ReadOnly]ref EcsTestData src, ref EcsTestData2 dst1, ref EcsTestData3 dst2)
            {
                dst1.value1 = dst2.value2 = src.value + index + entity.Index;
            }
        }
        
        struct Process4Entity  : IJobForEachWithEntity<EcsTestData, EcsTestData2, EcsTestData3, EcsTestData4>
        {
            public void Execute(Entity entity, int index, [ReadOnly]ref EcsTestData src, ref EcsTestData2 dst1, ref EcsTestData3 dst2, ref EcsTestData4 dst3)
            {
                dst1.value1 = dst2.value2 = dst3.value3 = src.value + index + entity.Index;
            }
        }
        
        public enum ProcessMode
        {
            Single,
            Parallel,
            Run
        }

        void Schedule<T>(ProcessMode mode) where T : struct, JobForEachExtensions.IBaseJobForEach
        {
#if !UNITY_ZEROPLAYER
            if (mode == ProcessMode.Parallel)
                new T().Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                new T().Run(EmptySystem);
            else 
                new T().ScheduleSingle(EmptySystem).Complete();
#endif
        }

        NativeArray<Entity> PrepareData(int entityCount)
        {
            var archetype = m_Manager.CreateArchetype(typeof(EcsTestData), typeof(EcsTestData2), typeof(EcsTestData3), typeof(EcsTestData4));

            var entities = new NativeArray<Entity>(entityCount, Allocator.Temp);
            m_Manager.CreateEntity(archetype, entities);
            for (int i = 0;i<entities.Length;i++)
                m_Manager.SetComponentData(entities[i], new EcsTestData(i));

            return entities;
        }

        void CheckResultsAndDispose(NativeArray<Entity> entities, int processCount, bool withEntity)
        {
            m_Manager.CreateArchetype(typeof(EcsTestData), typeof(EcsTestData2), typeof(EcsTestData3), typeof(EcsTestData4));

            for (int i = 0; i < entities.Length; i++)
            {
                // These values should remain untouched...
                if (processCount >= 2)
                    Assert.AreEqual(0, m_Manager.GetComponentData<EcsTestData2>(entities[i]).value0);
                if (processCount >= 3)
                    Assert.AreEqual(0, m_Manager.GetComponentData<EcsTestData3>(entities[i]).value1);
                if (processCount >= 4)
                    Assert.AreEqual(0, m_Manager.GetComponentData<EcsTestData4>(entities[i]).value2);

                int expectedResult;
                if (withEntity)
                    expectedResult = i + entities[i].Index + i;
                else
                    expectedResult = i;
                
                if (processCount >= 2)
                    Assert.AreEqual(expectedResult, m_Manager.GetComponentData<EcsTestData2>(entities[i]).value1);
                if (processCount >= 3)
                    Assert.AreEqual(expectedResult, m_Manager.GetComponentData<EcsTestData3>(entities[i]).value2);
                if (processCount >= 4)
                    Assert.AreEqual(expectedResult, m_Manager.GetComponentData<EcsTestData4>(entities[i]).value3);
            }

            entities.Dispose();
        }

        
        [Test]
        public void JobProcessStress_1([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var archetype = m_Manager.CreateArchetype(typeof(EcsTestData));
            var entities = new NativeArray<Entity>(entityCount, Allocator.Temp);
            m_Manager.CreateEntity(archetype, entities);

            if (mode == ProcessMode.Parallel)
                (new Process1()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process1()).Run(EmptySystem);
            else
                (new Process1()).ScheduleSingle(EmptySystem).Complete();

            for (int i = 0; i < entities.Length; i++)
                Assert.AreEqual(1, m_Manager.GetComponentData<EcsTestData>(entities[i]).value);

            entities.Dispose();
        }

        [Test]
        public void JobProcessStress_1_WithEntity([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var archetype = m_Manager.CreateArchetype(typeof(EcsTestData));
            var entities = new NativeArray<Entity>(entityCount, Allocator.Temp);
            m_Manager.CreateEntity(archetype, entities);
            
            if (mode == ProcessMode.Parallel)
                (new Process1Entity()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process1Entity()).Run(EmptySystem);
            else
                (new Process1Entity()).ScheduleSingle(EmptySystem).Complete();

            for (int i = 0; i < entities.Length; i++)
                Assert.AreEqual(i + entities[i].Index, m_Manager.GetComponentData<EcsTestData>(entities[i]).value);

            entities.Dispose();
        }
        
        [Test]
        public void JobProcessStress_2([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process2()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process2()).Run(EmptySystem);
            else
                (new Process2()).ScheduleSingle(EmptySystem).Complete();
            CheckResultsAndDispose(entities, 2, false);
        }

        [Test]
        public void JobProcessStress_2_WithEntity([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process2Entity()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process2Entity()).Run(EmptySystem);
            else
                (new Process2Entity()).ScheduleSingle(EmptySystem).Complete();

            CheckResultsAndDispose(entities, 2, true);

        }
        
        [Test]
        public void JobProcessStress_3([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process3()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process3()).Run(EmptySystem);
            else
                (new Process3()).ScheduleSingle(EmptySystem).Complete();

            CheckResultsAndDispose(entities, 3, false);
        }
        
        [Test]
        public void JobProcessStress_3_WithEntity([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process3Entity()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process3Entity()).Run(EmptySystem);
            else
                (new Process3Entity()).ScheduleSingle(EmptySystem).Complete();

            CheckResultsAndDispose(entities, 3, true);
        }
        
        [Test]
        public void JobProcessStress_4([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process4()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process4()).Run(EmptySystem);
            else
                (new Process4()).ScheduleSingle(EmptySystem).Complete();

            CheckResultsAndDispose(entities, 4, false);
        }
        
        [Test]
        public void JobProcessStress_4_WithEntity([Values]ProcessMode mode, [Values(0, 1, 1000)]int entityCount)
        {
            var entities = PrepareData(entityCount);

            if (mode == ProcessMode.Parallel)
                (new Process4Entity()).Schedule(EmptySystem).Complete();
            else if (mode == ProcessMode.Run)
                (new Process4Entity()).Run(EmptySystem);
            else
                (new Process4Entity()).ScheduleSingle(EmptySystem).Complete();

            CheckResultsAndDispose(entities, 4, true);
        }
    }
}
