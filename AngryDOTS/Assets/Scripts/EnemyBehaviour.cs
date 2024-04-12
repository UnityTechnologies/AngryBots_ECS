/* ENEMY BEHAVIOUR
 * This script manages the behavior of enemy GameObjects. Since this code isn't 
 * related to DOTS learning, it has been kept minimal. Do not try to learn 
 * "best practices" from this code as it is intended to be as simple and unobtrusive 
 * as possible
 */

using UnityEngine;

public class EnemyBehaviour : MonoBehaviour
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
}
