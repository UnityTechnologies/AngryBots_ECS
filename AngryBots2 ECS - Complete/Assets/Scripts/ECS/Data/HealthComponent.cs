using System;
using Unity.Entities;

[Serializable]
public struct Health : IComponentData
{
	public float Value;
}

public class HealthComponent : ComponentDataWrapper<Health> { }
