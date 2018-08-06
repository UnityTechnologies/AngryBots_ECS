using System;
using Unity.Entities;

[Serializable]
public struct PlayerTag : IComponentData { }
public class PlayerTagComponent : ComponentDataWrapper<PlayerTag> { }