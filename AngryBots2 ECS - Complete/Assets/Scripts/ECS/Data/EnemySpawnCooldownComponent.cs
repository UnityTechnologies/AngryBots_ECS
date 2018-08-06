using System;
using Unity.Entities;

[Serializable]
public struct EnemySpawnCooldown : IComponentData
{
	public float Value;
}
