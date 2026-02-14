/* PLAYER SHOOTING
 * This script manages the process of shooting bullets as GameObjects.
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

	private float timer;
	
	void Update()
	{
		if(Settings.IsPlayerDead())
			return;
		
		timer += Time.deltaTime;

		if (Input.GetButton("Fire1") && timer >= Settings.Instance.fireRate)
		{
			Vector3 rotation = gunBarrel.rotation.eulerAngles;
			rotation.x = 0f;

			if (!Settings.Instance.useECSforBullets)
			{
				if (Settings.Instance.spreadShot)
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
		int max = Settings.Instance.spreadAmount / 2;
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