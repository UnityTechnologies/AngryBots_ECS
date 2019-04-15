using System;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

#pragma warning disable 162

namespace Unity.Entities
{
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.GameObjectConversion)]
    class GameObjectConversionInitializationGroup : ComponentSystemGroup
    {
        
    }
    
    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.GameObjectConversion)]
    class GameObjectConversionGroup : ComponentSystemGroup
    {
        
    }

    [DisableAutoCreation]
    [WorldSystemFilter(WorldSystemFilterFlags.GameObjectConversion)]
    public class GameObjectAfterConversionGroup : ComponentSystemGroup
    {

    }

    public static class GameObjectConversionUtility
    {
        static ProfilerMarker m_ConvertScene = new ProfilerMarker("GameObjectConversionUtility.ConvertScene");
        static ProfilerMarker m_CreateConversionWorld = new ProfilerMarker("Create World & Systems");
        static ProfilerMarker m_DestroyConversionWorld = new ProfilerMarker("DestroyWorld");
        static ProfilerMarker m_CreateEntitiesForGameObjects = new ProfilerMarker("CreateEntitiesForGameObjects");
        static ProfilerMarker m_UpdateSystems = new ProfilerMarker("UpdateConversionSystems");
        static ProfilerMarker m_AddPrefabComponentDataTag = new ProfilerMarker("AddPrefabComponentDataTag");

        [Flags]
        public enum ConversionFlags : uint
        {
            None = 0,
            AddEntityGUID = 1 << 0,
            ForceStaticOptimization = 1 << 1,
            AssignName = 1 << 2,
        }

        unsafe public static EntityGuid GetEntityGuid(GameObject gameObject, int index)
        {
            return GameObjectConversionMappingSystem.GetEntityGuid(gameObject, index);
        }
    
        internal  static World CreateConversionWorld(World dstEntityWorld, Hash128 sceneGUID, ConversionFlags conversionFlags)
        {
            m_CreateConversionWorld.Begin();

            var gameObjectWorld = new World("GameObject World");
            gameObjectWorld.CreateSystem<GameObjectConversionMappingSystem>(dstEntityWorld, sceneGUID, conversionFlags);

            AddConversionSystems(gameObjectWorld);

            m_CreateConversionWorld.End();

            return gameObjectWorld;
        }
        
        
        internal static void Convert(World gameObjectWorld, World dstEntityWorld)
        {
            var mappingSystem = gameObjectWorld.GetExistingSystem<GameObjectConversionMappingSystem>();

            using (m_UpdateSystems.Auto())
            {
                // Convert all the data into dstEntityWorld
                gameObjectWorld.GetExistingSystem<GameObjectConversionInitializationGroup>().Update();
                gameObjectWorld.GetExistingSystem<GameObjectConversionGroup>().Update();
                gameObjectWorld.GetExistingSystem<GameObjectAfterConversionGroup>().Update();
            }

            using (m_AddPrefabComponentDataTag.Auto())
            {
                mappingSystem.AddPrefabComponentDataTag();    
            }
        }

        internal static Entity GameObjectToConvertedEntity(World gameObjectWorld, GameObject gameObject)
        {
            var mappingSystem = gameObjectWorld.GetExistingSystem<GameObjectConversionMappingSystem>();
            return mappingSystem.GetPrimaryEntity(gameObject);
        }


        public static Entity ConvertGameObjectHierarchy(GameObject root, World dstEntityWorld)
        {
            m_ConvertScene.Begin();
            
            Entity convertedEntity;
            using (var gameObjectWorld = CreateConversionWorld(dstEntityWorld, default(Hash128), 0))
            {
                var mappingSystem = gameObjectWorld.GetExistingSystem<GameObjectConversionMappingSystem>();

                using (m_CreateEntitiesForGameObjects.Auto())
                {
                    mappingSystem.AddGameObjectOrPrefabAsGroup(root);
                }

                Convert(gameObjectWorld, dstEntityWorld);

                convertedEntity = mappingSystem.GetPrimaryEntity(root);
                m_DestroyConversionWorld.Begin();
            }
            m_DestroyConversionWorld.End();
            
            m_ConvertScene.End();

            return convertedEntity;
        }
    
        public static void ConvertScene(Scene scene, Hash128 sceneHash, World dstEntityWorld, ConversionFlags conversionFlags = 0)
        {    
            m_ConvertScene.Begin();

            using (var gameObjectWorld = CreateConversionWorld(dstEntityWorld, sceneHash, conversionFlags))
            {                
                using (m_CreateEntitiesForGameObjects.Auto())
                {
                    GameObjectConversionMappingSystem.CreateEntitiesForGameObjects(scene, gameObjectWorld);
                }
                
                Convert(gameObjectWorld, dstEntityWorld);
                
                m_DestroyConversionWorld.Begin();
            }
            m_DestroyConversionWorld.End();
            
            m_ConvertScene.End();
        }
        
        static void AddConversionSystems(World gameObjectWorld)
        {
            var init = gameObjectWorld.GetOrCreateSystem<GameObjectConversionInitializationGroup>();
            var convert = gameObjectWorld.GetOrCreateSystem<GameObjectConversionGroup>();
            var afterConvert = gameObjectWorld.GetOrCreateSystem<GameObjectAfterConversionGroup>();

            // Ensure the following systems run first in this order...
            init.AddSystemToUpdateList(gameObjectWorld.GetOrCreateSystem<ConvertGameObjectToEntitySystemDeclarePrefabs>());
            init.AddSystemToUpdateList(gameObjectWorld.GetOrCreateSystem<ComponentDataProxyToEntitySystem>());

            var systems = DefaultWorldInitialization.GetAllSystems(WorldSystemFilterFlags.GameObjectConversion);
            foreach (var system in systems)
            {
                var attributes = system.GetCustomAttributes(typeof(UpdateInGroupAttribute), true);
                if (attributes.Length == 0)
                {
                    AddSystemAndLogException(gameObjectWorld, convert, system);
                }
                else
                {
                    foreach (var attribute in attributes)
                    {
                        var groupType = (attribute as UpdateInGroupAttribute)?.GroupType;

                        if (groupType == convert.GetType())
                        {
                            AddSystemAndLogException(gameObjectWorld, convert, system);
                        }
                        else if (groupType == afterConvert.GetType())
                        {
                            AddSystemAndLogException(gameObjectWorld, afterConvert, system);
                        }
                        else
                        {
                            Debug.LogWarning($"{system} has invalid UpdateInGroup[typeof({groupType}]");
                        }
                    }
                }
            }

            convert.SortSystemUpdateList();
            afterConvert.SortSystemUpdateList();
        }
    
        static void AddSystemAndLogException(World world, ComponentSystemGroup group, Type type)
        {
            try
            {
                group.AddSystemToUpdateList(world.GetOrCreateSystem(type) as ComponentSystemBase);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }    
}
