using System;
using Unity.Entities;

[Serializable]
public struct Bullet : IComponentData
{
	public float TimeToLive;
}
public class BulletComponent : ComponentDataWrapper<Bullet> { }
