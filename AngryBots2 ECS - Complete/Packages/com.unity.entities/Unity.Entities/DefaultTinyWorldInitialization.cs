//#define WRITE_LOG

using System;
using UnityEngine;

namespace Unity.Entities
{
    public delegate void ConfigInit(World world);

    public static class DefaultTinyWorldInitialization
    {
        /// <summary>
        /// Initialize the DOTS-RT World with all the boilerplate that needs to be done.
        /// ComponentSystems will be created and sorted into the high level ComponentSystemGroups.
        /// </summary>
        /// <param name="worldName"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="Exception"></exception>
        public static World Initialize(string worldName)
        {
            World world = InitializeWorld(worldName);
            InitializeSystems(world);
            SortSystems(world);
            return world;
        }

        public static World InitializeWorld(string worldName)
        {
            var world = new World(worldName);
            World.Active = world;
            return world;
        }

        public static void InitializeSystems(World world)
        {
            var allSystemTypes = TypeManager.GetSystems();
            var allSystemNames = TypeManager.SystemNames;

            if (allSystemTypes.Length == 0)
            {
                throw new InvalidOperationException("DefaultTinyWorldInitialization: No Systems found.");
            }

            // Create top level presentation system and simulation systems.
            InitializationSystemGroup initializationSystemGroup = new InitializationSystemGroup();
            world.AddSystem(initializationSystemGroup);

            SimulationSystemGroup simulationSystemGroup = new SimulationSystemGroup();
            world.AddSystem(simulationSystemGroup);

            PresentationSystemGroup presentationSystemGroup = new PresentationSystemGroup();
            world.AddSystem(presentationSystemGroup);

            // Create the working set of systems.
            int nSystems = 0;
            Type[] systemTypes = new Type[allSystemTypes.Length];
            ComponentSystemBase[] systems = new ComponentSystemBase[allSystemTypes.Length];

#if WRITE_LOG
            Console.WriteLine("--- Adding systems:");
#endif

            for (int i = 0; i < allSystemTypes.Length; i++)
            {
                if (TypeManager.GetSystemAttributes(allSystemTypes[i], typeof(DisableAutoCreationAttribute)).Length > 0)
                    continue;
                if (allSystemTypes[i] == initializationSystemGroup.GetType() ||
                    allSystemTypes[i] == simulationSystemGroup.GetType() ||
                    allSystemTypes[i] == presentationSystemGroup.GetType())
                {
                    continue;
                }

                if (world.GetExistingSystem(allSystemTypes[i]) != null)
                    continue;
#if WRITE_LOG
                Console.WriteLine(allSystemNames[i]);
#endif
                systemTypes[nSystems] = allSystemTypes[i];
                systems[nSystems] = TypeManager.ConstructSystem(allSystemTypes[i]);
                world.AddSystem(systems[nSystems]);
                nSystems++;
            }
#if WRITE_LOG
            Console.WriteLine("--- Adding systems Done.");
#endif

            for (int i = 0; i < nSystems; ++i)
            {
                var sysType = systemTypes[i];
                var system = systems[i];

                var groups = TypeManager.GetSystemAttributes(sysType, typeof(UpdateInGroupAttribute));
                if (groups.Length == 0)
                {
                    simulationSystemGroup.AddSystemToUpdateList(system);
                }

                for (int g = 0; g < groups.Length; ++g)
                {
                    var groupType = groups[g] as UpdateInGroupAttribute;
                    var groupSystem = world.GetExistingSystem(groupType.GroupType) as ComponentSystemGroup;
                    if (groupSystem == null)
                        throw new Exception("DefaultTinyWorldInitialization failed to find existing SystemGroup.");

                    groupSystem.AddSystemToUpdateList(system);
                }
            }
        }

        public static void SortSystems(World world)
        {
            var initializationSystemGroup = world.GetExistingSystem<InitializationSystemGroup>();
            var simulationSystemGroup = world.GetExistingSystem<SimulationSystemGroup>();
            var presentationSystemGroup = world.GetExistingSystem<PresentationSystemGroup>();

            initializationSystemGroup.SortSystemUpdateList();
            simulationSystemGroup.SortSystemUpdateList();
            presentationSystemGroup.SortSystemUpdateList();

#if WRITE_LOG
#if UNITY_ZEROPLAYER
            Console.WriteLine("** Sorted: initializationSystemGroup **");
            initializationSystemGroup.RecursiveLogToConsole();
            Console.WriteLine("** Sorted: simulationSystemGroup **");
            simulationSystemGroup.RecursiveLogToConsole();
            Console.WriteLine("** Sorted: presentationSystemGroup **");
            presentationSystemGroup.RecursiveLogToConsole();
            Console.WriteLine("** Sorted done. **");
#endif
#endif
        }
    }
}
