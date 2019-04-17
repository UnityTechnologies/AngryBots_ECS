using System;
using Unity.Entities;
using UnityEngine;

[Serializable]
public struct GameObjectComponent : IComponentData
{
	public GameObject GO;
}