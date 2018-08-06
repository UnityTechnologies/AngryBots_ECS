using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerLookDirection : MonoBehaviour {

	[Header("Camera")]
	public Camera mainCamera;

	//Utilities
	private Rigidbody playerRigidbody;
	private float camRayLength = 100f;


	void Awake()
	{
		playerRigidbody = GetComponent<Rigidbody>();
	}

	void FixedUpdate()
	{
		Turning();
	}

	void Turning ()
	{
        	// Create a ray from the mouse cursor on screen in the direction of the camera.
    		Ray camRay = mainCamera.ScreenPointToRay (Input.mousePosition);

        	// Create a RaycastHit variable to store information about what was hit by the ray.
		RaycastHit floorHit;

		Debug.DrawRay(camRay.origin, camRay.direction * camRayLength, Color.green);

        	// Perform the raycast and if it hits something on the floor layer...
    		if(Physics.Raycast (camRay, out floorHit, camRayLength))
		{
        		// Create a vector from the player to the point on the floor the raycast from the mouse hit.
        		Vector3 playerToMouse = floorHit.point - transform.position;

        		// Ensure the vector is entirely along the floor plane.
    			playerToMouse.y = 0f;
			
			playerToMouse.Normalize();
			Debug.Log(playerToMouse);

			// Create a quaternion (rotation) based on looking down the vector from the player to the mouse.
			Quaternion newRotatation = Quaternion.LookRotation (playerToMouse);

			// Set the player's rotation to this new rotation.
			playerRigidbody.MoveRotation (newRotatation);
		}
	}
}
