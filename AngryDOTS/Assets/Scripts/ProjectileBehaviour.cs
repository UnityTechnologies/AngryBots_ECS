/* PROJECTILE BEHAVIOUR
 * This script manages the behavior of projectile GameObjects. Since this code isn't 
 * related to DOTS learning, it has been kept minimal. Do not try to learn 
 * "best practices" from this code as it is intended to be as simple and unobtrusive 
 * as possible
 */

using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class ProjectileBehaviour : MonoBehaviour
{
	[Header("Movement")]
	public float speed = 50f;

	[Header("Life Settings")]
	public float lifeTime = 2f;

	Rigidbody projectileRigidbody;


	void Start()
	{
		projectileRigidbody = GetComponent<Rigidbody>();
		Invoke("RemoveProjectile", lifeTime);
	}

	void Update()
	{
		Vector3 movement = transform.forward * speed * Time.deltaTime;
		projectileRigidbody.MovePosition(transform.position + movement);
	}

	void OnTriggerEnter(Collider theCollider)
	{

		if (theCollider.CompareTag("Enemy") || theCollider.CompareTag("Environment"))
			RemoveProjectile();
	}

	void RemoveProjectile()
	{
		Destroy(gameObject);
	}
}
