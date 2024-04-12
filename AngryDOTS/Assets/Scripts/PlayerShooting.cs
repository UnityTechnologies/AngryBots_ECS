/* PLAYER SHOOTING
 * This script manages the process of shooting bullets. Most of the code is general
 * or used for GameObject workflows. The DOTS items to be aware of in this script are:
	* - The entity members: manager, and bulletEntityPrefab
	* - The initialization in the Start() method
	* - The entity instantiation in the SpawnBulletECS() and SpawnBulletSpreadECS() methods
 */

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

	EntityManager manager;		// Member to hold an EntityManager reference
	Entity bulletEntityPrefab;	// Member to hold the ID of the bullet entity


	void Start()
	{
		// If not using ECS, no need to do anything here
		if (!useECS) return;
		
		// Get a reference to an EntityManager which is how we will create and access entities
		manager = World.DefaultGameObjectInjectionWorld.EntityManager;

		// Create a query that will find the Directory entity. The Directory is created automatically
		// by the baking process of the Directory GameObject which you can find in the "Baker Sub Scene"
		// in the ECS Shooter scene
		EntityQuery query = new EntityQueryBuilder(Allocator.Temp).WithAll<Directory>().Build(manager);

		// If this query finds one and only one Directory, then grab the bullet entity and store it
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

	// This method spawns bullets as entities instead of GameObjects
	void SpawnBulletECS(Vector3 rotation)
	{
		// Use our EntityManager to instantiate a copy of the bullet entity
		Entity bullet = manager.Instantiate(bulletEntityPrefab);

		// Create a new LocalTransform component and give it the values needed to
		// be positioned at the barrel of the gun
		LocalTransform t = new LocalTransform
		{
			Position = gunBarrel.position,
			Rotation = Quaternion.Euler(rotation),
			Scale = 1f
		};

		// Set the component data we just created for the entity we just created
		manager.SetComponentData(bullet, t);
	}

	// This method spawns many bullets at a time as entities instead of GameObjects
	void SpawnBulletSpreadECS(Vector3 rotation)
	{
		// Most of this code is just boilerplate math to create a grid of rotations. Only the
		// relevant DOTS code is commented
		if (spreadAmount % 2 != 0) //No odd numbers to keep the spread even
			spreadAmount += 1;

		int max = spreadAmount / 2;
		int min = -max;
		int totalAmount = spreadAmount * spreadAmount;

		Vector3 tempRot = rotation;
		int index = 0;

		// NativeArrays are thread-safe data containers. In DOTS, they are a great way to work
		// with a lot of entities at once. They must be cleaned up though. This code creates
		// a temporary NativeArray with a size equal to the number of bullets we want to spawn
		NativeArray<Entity> bullets = new NativeArray<Entity>(totalAmount, Allocator.TempJob);

		// By passing a NativeArray into the Instantiate() method of the EntityManager, many entities
		// are created at once and put into this NativeArray
		manager.Instantiate(bulletEntityPrefab, bullets);

		for (int x = min; x < max; x++)
		{
			tempRot.x = (rotation.x + 3 * x) % 360;

			for (int y = min; y < max; y++)
			{
				tempRot.y = (rotation.y + 3 * y) % 360;


				// Create a new LocalTransform component and give it the values needed to
				// be positioned at the barrel of the gun
				LocalTransform t = new LocalTransform
				{
					Position = gunBarrel.position,
					Rotation = Quaternion.Euler(tempRot), // Use the temp rotation value needed to make the bullets spread out
					Scale = 1f
				};

				// Set the component data we just created for the entity we just created
				manager.SetComponentData(bullets[index], t);

				index++;
			}
		}
		// Be sure to Dispose of the NativeArray or else you'll have a memory leak
		bullets.Dispose();
	}
}

