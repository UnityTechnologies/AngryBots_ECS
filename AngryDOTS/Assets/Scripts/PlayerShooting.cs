using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

public class PlayerShooting : MonoBehaviour
{
	public bool useECS = false;
	public bool spreadShot = false;

	[Header("General")]
	public Transform gunBarrel;
	public ParticleSystem shotVFX;
	public AudioSource shotAudio;
	public float fireRate = .1f;
	public int spreadAmount = 20;

	[Header("Bullets")]
	public GameObject bulletPrefab;

	float timer;

	EntityManager manager;
	Entity bulletEntityPrefab;


	void Start()
	{
		if (!useECS) return;
		
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;
		EntityQuery query = new EntityQueryBuilder(Allocator.Temp).WithAll<Directory>().Build(manager);

		if (query.HasSingleton<Directory>())
			bulletEntityPrefab = query.GetSingleton<Directory>().bulletPrefab;
	}

	void Update()
	{
		timer += Time.deltaTime;

		if (Input.GetButton("Fire1") && timer >= fireRate)
		{
			Vector3 rotation = gunBarrel.rotation.eulerAngles;
			rotation.x = 0f;

			if (useECS)
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
		Entity bullet = manager.Instantiate(bulletEntityPrefab);
		LocalTransform t = new LocalTransform
		{
			Position = gunBarrel.position,
			Rotation = Quaternion.Euler(rotation),
			Scale = 1f
		};

		manager.SetComponentData(bullet, t);
	}

	void SpawnBulletSpreadECS(Vector3 rotation)
	{
		if (spreadAmount % 2 != 0) //No odd numbers to keep the spread even
			spreadAmount += 1;

		int max = spreadAmount / 2;
		int min = -max;
		int totalAmount = spreadAmount * spreadAmount;

		Vector3 tempRot = rotation;
		int index = 0;

		NativeArray<Entity> bullets = new NativeArray<Entity>(totalAmount, Allocator.TempJob);
		manager.Instantiate(bulletEntityPrefab, bullets);

		for (int x = min; x < max; x++)
		{
			tempRot.x = (rotation.x + 3 * x) % 360;

			for (int y = min; y < max; y++)
			{
				tempRot.y = (rotation.y + 3 * y) % 360;

				LocalTransform t = new LocalTransform
				{
					Position = gunBarrel.position,
					Rotation = Quaternion.Euler(tempRot),
					Scale = 1f
				};

				manager.SetComponentData(bullets[index], t);

				index++;
			}
		}
		bullets.Dispose();
	}
}

