using System;
using Unity.Entities;
using UnityEngine;

[Serializable]
public struct Spawner : ISharedComponentData
{
	public float cooldown;
	public GameObject prefab;
}
