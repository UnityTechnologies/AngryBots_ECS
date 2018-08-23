using System;
using Unity.Entities;

[Serializable]
public struct MoveForward : IComponentData{}

public class MoveForwardComponent : ComponentDataWrapper<MoveForward> { }
