using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Entities;
using Unity.Entities.Tests;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using static Unity.Entities.GameObjectConversionUtility;

namespace UnityEngine.Entities.Tests
{
    class GameObjectConversionTests : ECSTestsFixture
    {
        public static void ConvertSceneAndApplyDiff(Scene scene, World previousStateShadowWorld, World dstEntityWorld)
        {
            using (var cleanConvertedEntityWorld = new World("Clean Entity Conversion World"))
            {
                ConvertScene(scene, default(Unity.Entities.Hash128), cleanConvertedEntityWorld, ConversionFlags.AddEntityGUID);
                WorldDiffer.DiffAndApply(cleanConvertedEntityWorld, previousStateShadowWorld, dstEntityWorld);
            }
        }
        
        [Test]
        public void ConvertGameObject_HasOnlyTransform_ProducesEntityWithPositionAndRotation([Values]bool useDiffing)
        {
            // Prepare scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
            SceneManager.SetActiveScene(scene);
            
            var go = new GameObject("Test Conversion");
            go.transform.localPosition = new Vector3(1, 2, 3);
            
            // Convert
            if (useDiffing)
            {
                var shadowWorld = new World("Shadow");
                ConvertSceneAndApplyDiff(scene, shadowWorld, m_Manager.World);
                shadowWorld.Dispose();
            }
            else
            {
                ConvertScene(scene, default(Unity.Entities.Hash128), m_Manager.World);
            }
            
            // Check
            var entities = m_Manager.GetAllEntities();
            Assert.AreEqual(1, entities.Length);
            var entity = entities[0];

            Assert.AreEqual(useDiffing ? 4 : 3, m_Manager.GetComponentCount(entity));
            Assert.IsTrue(m_Manager.HasComponent<Translation>(entity));
            Assert.IsTrue(m_Manager.HasComponent<Rotation>(entity));
            if (useDiffing)
                Assert.IsTrue(m_Manager.HasComponent<EntityGuid>(entity));

            Assert.AreEqual(new float3(1, 2, 3), m_Manager.GetComponentData<Translation>(entity).Value);
            Assert.AreEqual(quaternion.identity, m_Manager.GetComponentData<Rotation>(entity).Value);
            var localToWorld = m_Manager.GetComponentData<LocalToWorld>(entity).Value;
            Assert.IsTrue(localToWorld.Equals(go.transform.localToWorldMatrix));
            
            // Unload
            EditorSceneManager.UnloadSceneAsync(scene);
        }
        
        [Test, Ignore("Disabled because when the package is published you get a ` Cancelling DisplayDialog: Opening scene in read-only package! It is not allowed to open a scene in a read-only package` error for this test")]
        public void ConversionIgnoresMissingMonoBehaviour()
        {
            TestTools.LogAssert.Expect(LogType.Warning, new Regex("missing"));
            var scene = EditorSceneManager.OpenScene("Packages/com.unity.entities/Unity.Entities.Hybrid.Tests/MissingMonoBehaviour.unity");
            var world = new World("Temp");
            ConvertScene(scene, default(Unity.Entities.Hash128), world);
            world.Dispose();
        }
        
        [Test]
        public void ConversionOfGameObject()
        {
            var gameObject = new GameObject();
            var entity = ConvertGameObjectHierarchy(gameObject, World);

            Assert.IsFalse(m_Manager.HasComponent<Prefab>(entity));
            Object.DestroyImmediate(gameObject);
        }

        [Test]
        public void ConversionOfPrefabIsEntityPrefab()
        {
            var path = "Assets/ConversionOfPrefabIsEntityPrefab.prefab";
            var gameObject = new GameObject();
            var prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, path);
            var entity = ConvertGameObjectHierarchy(prefab, World);

            Assert.IsTrue(m_Manager.HasComponent<Prefab>(entity));

            AssetDatabase.DeleteAsset(path);
            Object.DestroyImmediate(gameObject);
        }
        
        //@TODO: Test Prefabs
        //@TODO: Test GameObject -> Entity ID mapping

    }
}
