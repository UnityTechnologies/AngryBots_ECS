using Unity.Entities;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class EnemyBehaviour : MonoBehaviour, IConvertGameObjectToEntity
{
	[Header("Movement")]
	public float speed = 2f;

	[Header("Life Settings")]
	public float enemyHealth = 1f;

	Rigidbody enemyRigidbody;


	void Start()
	{
		enemyRigidbody = GetComponent<Rigidbody>();
	}

	void Update()
	{
		if (Settings.main.player != null)
		{
			Vector3 heading = Settings.main.player.position - transform.position;
			heading.y = 0f;
			transform.rotation = Quaternion.LookRotation(heading);
		}

		Vector3 movement = transform.forward * speed * Time.deltaTime;
		enemyRigidbody.MovePosition(transform.position + movement);
	}

	void OnTriggerEnter(Collider theCollider)
	{
		if (theCollider.CompareTag("Player") || theCollider.CompareTag("Bullet"))
		{
			Destroy(gameObject);
			Instantiate(Settings.main.bulletHitPrefab, transform.position, Quaternion.identity);

			var playerMove = theCollider.GetComponent<PlayerMovementAndLook>();
			playerMove?.PlayerDied();
		}
	}

	public void Convert(Entity entity, EntityManager manager, GameObjectConversionSystem conversionSystem)
	{
		manager.AddComponent(entity, typeof(EnemyTag));
		manager.AddComponent(entity, typeof(MoveForward));

		MoveSpeed moveSpeed = new MoveSpeed { Value = speed };
		manager.AddComponentData(entity, moveSpeed);

		Health health = new Health { Value = enemyHealth };
		manager.AddComponentData(entity, health);
	}
}
