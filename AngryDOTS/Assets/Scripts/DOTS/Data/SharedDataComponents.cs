using Unity.Entities;

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