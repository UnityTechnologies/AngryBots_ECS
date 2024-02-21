using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class PlayerDataAuthoring : MonoBehaviour
{
    class Baker : Baker<PlayerDataAuthoring>
    {
        public override void Bake(PlayerDataAuthoring authoring)
        {
            var playerObject = GameObject.FindGameObjectWithTag("Player");// FindObjectOfType<PlayerController>().playerHealth
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent(entity, new PlayerTag { });
            AddComponent(entity, new PlayerData
            {
                isAlive = true,
                position = playerObject.transform.position
            });
            
            AddComponent(entity, new Health
            {
                Value = playerObject.GetComponent<PlayerController>().playerHealth
            });
        }
    }
}
