//using Unity.Entities;
using Unity.Entities;
using UnityEngine;

//[RequireComponent(typeof(Rigidbody))]
public class EnemyBehaviour : MonoBehaviour//, IConvertGameObjectToEntity
{
	[Header("Movement")]
	public float speed = 2f;

	[Header("Life Settings")]
	public float enemyHealth = 1f;

	Rigidbody rigidBody;


	void Start()
	{
		rigidBody = GetComponent<Rigidbody>();
	}

	void Update()
	{
		if (!Settings.IsPlayerDead())
		{
			Vector3 heading = Settings.PlayerPosition - transform.position;
			heading.y = 0f;
			transform.rotation = Quaternion.LookRotation(heading);
		}

		Vector3 movement = transform.forward * speed * Time.deltaTime;
		rigidBody.MovePosition(transform.position + movement);
	}

	//Enemy Collision
	void OnTriggerEnter(Collider theCollider)
	{
		if (!theCollider.CompareTag("Bullet"))
			return;

		enemyHealth--;

		if(enemyHealth <= 0)
		{
			Destroy(gameObject);
			BulletImpactPool.PlayBulletImpact(transform.position);
		}
	}

	public class EnemyBaker : Baker<EnemyBehaviour>
	{
		public override void Bake(EnemyBehaviour authoring)
		{
			var entity = GetEntity(TransformUsageFlags.Dynamic);
			AddComponent(entity, new EnemyTag { });
			AddComponent(entity, new MoveForward { });

			AddComponent(entity, new MoveSpeed { Value = authoring.speed });
			AddComponent(entity, new Health { Value = authoring.enemyHealth });
		}
	}

	//public void Convert(Entity entity, EntityManager manager, GameObjectConversionSystem conversionSystem)
	//{
	//	manager.AddComponent(entity, typeof(EnemyTag));
	//	manager.AddComponent(entity, typeof(MoveForward));

	//	MoveSpeed moveSpeed = new MoveSpeed { Value = speed };
	//	manager.AddComponentData(entity, moveSpeed);

	//	Health health = new Health { Value = enemyHealth };
	//	manager.AddComponentData(entity, health);
	//}
}
