using UnityEngine;

public class PlayerMovementAndLook : MonoBehaviour
{
	[Header("Camera")]
	public Camera mainCamera;

	[Header("Movement")]
	public Rigidbody playerRigidbody;
	public float speed = 4.5f;	

	[Header("Animation")]
	public Animator playerAnimator;

	Plane playerMovementPlane;
	RaycastHit floorRaycastHit;
	Vector3 playerToMouse;
	Vector3 inputDirection;
	Vector3 movement;
	bool isDead;

	void Awake()
	{
		CreatePlayerMovementPlane();
	}

	void CreatePlayerMovementPlane()
	{
		playerMovementPlane = new Plane (transform.up, transform.position + transform.up);
	}

	void FixedUpdate()
	{
		if (isDead)
			return;

		//Arrow Key Input
		float h = Input.GetAxis("Horizontal");
		float v = Input.GetAxis("Vertical");

		inputDirection = new Vector3(h, 0, v);

		//Camera Direction
		var cameraForward = mainCamera.transform.forward;
		var cameraRight = mainCamera.transform.right;

		cameraForward.y = 0f;
		cameraRight.y = 0f;

		//Try not to use var for roadshows or learning code
		Vector3 desiredDirection = cameraForward * inputDirection.z + cameraRight * inputDirection.x;
		
		//Why not just pass the vector instead of breaking it up only to remake it on the other side?
		MoveThePlayer(desiredDirection);
		TurnThePlayer();
		AnimateThePlayer(desiredDirection);
		
	}

	void MoveThePlayer(Vector3 desiredDirection)
	{
		movement.Set(desiredDirection.x, 0f, desiredDirection.z);

		movement = movement.normalized * speed * Time.deltaTime;

		playerRigidbody.MovePosition(transform.position + movement);

	}

	void TurnThePlayer()
	{
		Vector3 cursorScreenPosition = Input.mousePosition;

		Vector3 cursorWorldPosition = ScreenPointToWorldPointOnPlane(cursorScreenPosition, playerMovementPlane, mainCamera);

		playerToMouse = cursorWorldPosition - transform.position;

		playerToMouse.y = 0f;

		playerToMouse.Normalize();

		Quaternion newRotation = Quaternion.LookRotation(playerToMouse);

		playerRigidbody.MoveRotation(newRotation);

	}

	Vector3 PlaneRayIntersection(Plane plane, Ray ray)
	{
		float dist = 0.0f;
		plane.Raycast(ray, out dist);
		return ray.GetPoint(dist);
	}

	Vector3 ScreenPointToWorldPointOnPlane(Vector3 screenPoint, Plane plane, Camera camera)
	{	
		Ray ray = camera.ScreenPointToRay(screenPoint);
		return PlaneRayIntersection(plane, ray);
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

	public void PlayerDied()
	{
		if (isDead)
			return;

		isDead = true;

		playerAnimator.SetTrigger("Died");
	}
}
