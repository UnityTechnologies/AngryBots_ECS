using System;
using Unity.Entities;

[Serializable]
public struct MoveSpeed : IComponentData
{
	public float Value;
}

public class MoveSpeedComponent : ComponentDataWrapper<MoveSpeed> { }
