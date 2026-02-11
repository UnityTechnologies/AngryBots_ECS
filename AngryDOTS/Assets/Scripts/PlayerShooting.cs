/* PLAYER SHOOTING
 * This script manages the process of shooting bullets. Most of the code is general
 * or used for GameObject workflows. The DOTS items to be aware of in this script are:
	* - The entity member: manager
	* - The initialization in the Start() method
	* - The entity instantiation in the SpawnBulletECS() and SpawnBulletSpreadECS() methods
 *
 * Note: This code is for presentation and learning purposes. We're handing off the spawning
 *	of entities to the Systems, but based on MonoBehaviour input.
 * 
 *  You can have all of the input detection code be systems-based, please refer to our ECS samples:
 *	https://github.com/Unity-Technologies/EntityComponentSystemSamples
 */

using UnityEngine;

public class PlayerShooting : MonoBehaviour
{
	[Header("General")]
	public Transform gunBarrel;
	public ParticleSystem shotVFX;
	public AudioSource shotAudio;
	
	[Header("Bullets")]
	public GameObject bulletPrefab;

	float timer;


	void Start()
	{
		// If not using ECS, no need to do anything here
		if (!Settings.IsUsingECSForBullets()) return;
	}
	
	void Update()
	{
		if(Settings.IsPlayerDead())
			return;
		
		timer += Time.deltaTime;

		if (Input.GetButton("Fire1") && timer >= Settings.GetFireRate())
		{
			Vector3 rotation = gunBarrel.rotation.eulerAngles;
			rotation.x = 0f;

			if (!Settings.IsUsingECSForBullets())
			{
				if (Settings.IsUsingSpreadShot())
					SpawnBulletSpread(rotation);
				else
					SpawnBullet(rotation);
			}

			timer = 0f;

			if (shotVFX)
				shotVFX.Play();

			if (shotAudio)
				shotAudio.Play();
		}
	}

	void SpawnBullet(Vector3 rotation)
	{
		Instantiate(bulletPrefab, gunBarrel.position, Quaternion.Euler(rotation));
	}

	void SpawnBulletSpread(Vector3 rotation)
	{
		int max = Settings.GetSpreadAmount() / 2;
		int min = -max;

		Vector3 tempRot = rotation;
		for (int x = min; x < max; x++)
		{
			tempRot.x = (rotation.x + 3 * x) % 360;

			for (int y = min; y < max; y++)
			{
				tempRot.y = (rotation.y + 3 * y) % 360;

				Instantiate(bulletPrefab, gunBarrel.position, Quaternion.Euler(tempRot));
			}
		}
	}
}