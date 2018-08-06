using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class PlayerShooting : MonoBehaviour
{
	public bool UseECS = false;
	public bool spreadShot = false;

	[Header("General")]
	public Transform gunBarrel;
	public ParticleSystem shotVFX;
	public AudioSource shotAudio;
	public float fireRate = .1f;
	public int spreadAmount = 20;

	[Header("Bullets")]
	public GameObject bulletPrefab;
	public GameObject bulletPrefabECS;

	float timer;

	EntityManager manager;


	void Start()
	{
		if(UseECS)
			manager = World.Active.GetOrCreateManager<EntityManager>();
	}

	void Update()
	{
		timer += Time.deltaTime;

		if (Input.GetButton("Fire1") && timer >= fireRate)
		{
			Vector3 rotation = gunBarrel.rotation.eulerAngles;
			rotation.x = 0f;

			if (UseECS)
			{
				if (spreadShot)
					SpawnBulletSpreadECS(rotation);
				else
					SpawnBulletECS(rotation);
			}
			else
			{
				if (spreadShot)
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
		int max = spreadAmount / 2;
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

	void SpawnBulletECS(Vector3 rotation)
	{
		Entity bullet = manager.Instantiate(bulletPrefabECS);
		manager.SetComponentData(bullet, new Position { Value = gunBarrel.position });
		manager.SetComponentData(bullet, new Rotation { Value = Quaternion.Euler(rotation) });
	}

	void SpawnBulletSpreadECS(Vector3 rotation)
	{
		int max = spreadAmount / 2;
		int min = -max;
		int totalAmount = spreadAmount * spreadAmount;
		
		Vector3 tempRot = rotation;
		int index = 0;

		NativeArray<Entity> bullets = new NativeArray<Entity>(totalAmount, Allocator.Temp);
		manager.Instantiate(bulletPrefabECS, bullets);
		
		for (int x = min; x < max; x++)
		{
			tempRot.x = (rotation.x + 3 * x) % 360;

			for (int y = min; y < max; y++)
			{
				tempRot.y = (rotation.y + 3 * y) % 360;
				
				manager.SetComponentData(bullets[index], new Position { Value = gunBarrel.position });
				manager.SetComponentData(bullets[index], new Rotation { Value = Quaternion.Euler(tempRot) });
				index++;
			}
		}
		
		bullets.Dispose();
	}
}

