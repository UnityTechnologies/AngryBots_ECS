/* PLAYER CONTROLLER
 * This script manages the process of the player's movement and health. Most of the code is general
 * or used for GameObject workflows. One interesting thing of note is that this script creates an
 * entity containing its health and position. These are then checked and updated every frame. This
 * is done so that the CollisionSystem can treat collisions in a generic way without having to have
 * a special job just for the player. The DOTS items to be aware of in this script are:
	* - The entity members: manager, and bulletEntityPrefab
	* - The initialization in the CreateEntity() method
	* - The entity updates in the UpdateEntity() method
 */

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

	bool useECS;
	EntityManager manager;      // Member to hold an EntityManager reference
	Entity playerDataEntity;    // Member to hold the ID of the player data entity

	void Awake()
	{
		playerRigidbody = GetComponent<Rigidbody>();
	}

	void Start()
	{
		// We only need to do the ECS steps if the player is using ECS for bullets and 
		// entities. This isn't a perfect way to set it up, but it works in this
		// simple example
		useECS = GetComponent<PlayerShooting>().useECS;
		
		CreateEntity();
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

	void CreateEntity()
	{
		// If not using ECS, no need to do anything here
		if (!useECS) 
			return;

		// Get a reference to an EntityManager which is how we will create and access entities
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;

		// Create an entity. We will use this as a singleton (one and only one) in order to access
		// the player's data in Update()
		playerDataEntity = manager.CreateEntity();

		// Give the entity the PlayerTag component
		manager.AddComponent<PlayerTag>(playerDataEntity);

		// Create a new LocalTransform component and give it the values needed to represent the
		// location of the Player GameObject
		LocalTransform t = new LocalTransform
		{
			Position = transform.position
		};

		// Set the component data we just created for the entity we just created
		manager.AddComponentData(playerDataEntity, t);

		// Create a new Health component and give it the value of the player's starting health
		Health h = new Health
		{
			Value = playerHealth
		};

		// Set the component data we just created for the entity we just created
		manager.AddComponentData(playerDataEntity, h);
	}

	void UpdateEntity()
	{
		// If not using ECS, no need to do anything here
		if (!useECS)
			return;

		// If there is no playerDataEntity for some reason, exit
		if (playerDataEntity == Entity.Null)
			return;

		// Use the player's data entity to find the player's Health data component
		Health health = manager.GetComponentData<Health>(playerDataEntity);

		// Check to see if the player is still alive. Since we will only either face GameObject
		// enemies or Entity enemies, there is no need to sync health both ways
		if (health.Value > 0)
		{
			// If the player is still alive, create a new LocalTransform at the player's position
			LocalTransform t = new LocalTransform
			{
				Position = transform.position
			};

			// Set the LocalTransform to the player's data entity
			manager.SetComponentData(playerDataEntity, t);
		}
		else
		{
			// Sad face
			PlayerDied();
		}

	}
}
