using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class PlayerController : MonoBehaviour
{
	[Header("Camera")]
	public Camera mainCamera;

	[Header("Movement")]
	public float speed = 4.5f;
	public LayerMask whatIsGround;

	[Header("Life Settings")]
	public float playerHealth = 1f;

	[Header("Animation")]
	public Animator playerAnimator;

	Rigidbody playerRigidbody;
	bool isDead;

	EntityManager manager;
	Entity playerDataEntity;

	void Awake()
	{
		playerRigidbody = GetComponent<Rigidbody>();
	}

	void Start()
	{
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;
		
		playerDataEntity = manager.CreateEntity();
		manager.AddComponent<PlayerTag>(playerDataEntity);

		LocalTransform t = new LocalTransform
		{
			Position = transform.position
		};
		manager.AddComponentData(playerDataEntity, t);

		Health h = new Health
		{
			Value = playerHealth
		};
		manager.AddComponentData(playerDataEntity, h);
	}

	void FixedUpdate()
	{
		if (isDead)
			return;

		//Arrow Key Input
		float h = Input.GetAxis("Horizontal");
		float v = Input.GetAxis("Vertical");

		Vector3 inputDirection = new Vector3(h, 0, v);

		//Camera Direction
		var cameraForward = mainCamera.transform.forward;
		var cameraRight = mainCamera.transform.right;

		cameraForward.y = 0f;
		cameraRight.y = 0f;

		Vector3 desiredDirection = cameraForward * inputDirection.z + cameraRight * inputDirection.x;
		
		MoveThePlayer(desiredDirection);
		TurnThePlayer();
		AnimateThePlayer(desiredDirection);
		UpdateEntity();
	}

	void MoveThePlayer(Vector3 desiredDirection)
	{
		Vector3 movement = new Vector3(desiredDirection.x, 0f, desiredDirection.z);
		movement = movement.normalized * speed * Time.deltaTime;

		playerRigidbody.MovePosition(transform.position + movement);
	}

	void TurnThePlayer()
	{
		Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
		RaycastHit hit;

		if (Physics.Raycast(ray, out hit, whatIsGround))
		{
			Vector3 playerToMouse = hit.point - transform.position;
			playerToMouse.y = 0f;
			playerToMouse.Normalize();

			Quaternion newRotation = Quaternion.LookRotation(playerToMouse);
			playerRigidbody.MoveRotation(newRotation);
		}
	}

	void AnimateThePlayer(Vector3 desiredDirection)
	{
		if(!playerAnimator)
			return;

		Vector3 movement = new Vector3(desiredDirection.x, 0f, desiredDirection.z);
		float forw = Vector3.Dot(movement, transform.forward);
		float stra = Vector3.Dot(movement, transform.right);

		playerAnimator.SetFloat("Forward", forw);
		playerAnimator.SetFloat("Strafe", stra);
	}

	//Player Collision
	void OnTriggerEnter(Collider theCollider)
	{
		if (!theCollider.CompareTag("Enemy"))
			return;

		playerHealth--;

		if(playerHealth <= 0)
		{
			PlayerDied();
		}
	}

	void UpdateEntity()
	{
		if (playerDataEntity == Entity.Null)
			return;

		Health health = manager.GetComponentData<Health>(playerDataEntity);

		if (health.Value > 0)
		{
			LocalTransform t = new LocalTransform
			{
				Position = transform.position
			};
			manager.SetComponentData(playerDataEntity, t);
		}
		else
		{
			PlayerDied();
		}

	}

	void PlayerDied()
	{
		if (isDead)
			return;

		isDead = true;

		playerAnimator.SetTrigger("Died");
		playerRigidbody.isKinematic = true;
		GetComponent<Collider>().enabled = false;

		Settings.PlayerDied();
	}
}
