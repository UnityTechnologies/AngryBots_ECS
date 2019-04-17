using Unity.Entities;
using UnityEngine;


public class HealthProxy : MonoBehaviour, IConvertGameObjectToEntity
{
	public float healthValue = 1f;


	public void Convert(Entity entity, EntityManager manager, GameObjectConversionSystem conversionSystem)
	{
		Health health = new Health { Value = healthValue };
		manager.AddComponentData(entity, health);
	}
}