using UnityEngine;

public class BulletImpactPool : MonoBehaviour
{
	static BulletImpactPool instance;

	[Header("Bullet Impact Info")]
	public GameObject bulletHitPrefab;
	public int impactPoolSize = 100;

	GameObject[] impactPool;
	int currentPoolIndex;

	void Awake()
	{
		if (instance != null && instance != this)
		{
			Destroy(gameObject);
			return;
		}
		else
			instance = this;

		impactPool = new GameObject[impactPoolSize];
		for (int i = 0; i < impactPoolSize; i++)
		{
			impactPool[i] = Instantiate(bulletHitPrefab, instance.transform) as GameObject;
			impactPool[i].SetActive(false);
		}
	}

	public static void PlayBulletImpact(Vector3 position)
	{
		if (++instance.currentPoolIndex >= instance.impactPool.Length)
			instance.currentPoolIndex = 0;

		instance.impactPool[instance.currentPoolIndex].SetActive(false);
		instance.impactPool[instance.currentPoolIndex].transform.position = position;
		instance.impactPool[instance.currentPoolIndex].SetActive(true);
	}
}
