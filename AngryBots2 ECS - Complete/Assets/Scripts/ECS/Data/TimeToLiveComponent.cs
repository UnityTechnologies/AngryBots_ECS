using System;
using Unity.Entities;

[Serializable]
public struct TimeToLive : IComponentData
{
	public float Value;
}
