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
