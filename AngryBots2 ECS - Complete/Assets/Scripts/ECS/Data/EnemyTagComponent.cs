using System;
using Unity.Entities;

[Serializable]
public struct EnemyTag : IComponentData { }
public class EnemyTagComponent : ComponentDataWrapper<EnemyTag> { }
