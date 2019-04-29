using Unity.Entities;
using UnityEngine;

public class MoveForwardConversion : MonoBehaviour, IConvertGameObjectToEntity
{
	public float speed = 50f;


	public void Convert(Entity entity, EntityManager manager, GameObjectConversionSystem conversionSystem)
	{
		manager.AddComponent(entity, typeof(MoveForward));

		MoveSpeed moveSpeed = new MoveSpeed { Value = speed };
		manager.AddComponentData(entity, moveSpeed);
	}
}