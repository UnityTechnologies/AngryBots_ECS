using Unity.Entities;
using Unity.Mathematics;

// This component contains a single float Value which represents how
// much health an entity has
public struct Health : IComponentData
{
    public float Value;
}

// This component contains a single float Value which represents how
// fast an entity moves
public struct MoveSpeed : IComponentData
{
    public float Value;
}

// This component contains a single float Value which represents how
// long something lives before it is destroyed or further processed
public struct TimeToLive : IComponentData
{
    public float Value;
}

// This "tag" component contains no data and is instead simply
// used to identify entities as "enemies"
public struct EnemyTag : IComponentData { }

// This "tag" component contains no data and is instead simply
// used to identify entities as "players"
public struct PlayerTag : IComponentData { }

// This "tag" component contains no data and is instead simply
// used to identify entities that need to "move forward"
public struct MoveForward : IComponentData { }


// The following components are for spawning bullets and enemies...
public struct SpawnBulletRequest : IComponentData
{
    public float3 gunBarrelPosition;
    public quaternion playerRotation;
}

public struct SpawnBulletSpreadRequest : IComponentData
{
    public float3 gunBarrelPosition;
    public float3 playerRotation;
    public int spreadAmount;
}

public struct SpawnEnemyRequest : IComponentData
{
    public float3 position;
}