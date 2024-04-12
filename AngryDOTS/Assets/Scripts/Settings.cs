/* SETTINGS
 * This script contains helper data and methods for the running of this project.
 * Since this code isn't related to DOTS learning, it has been kept minimal. Do 
 * not try to learn "best practices" from this code as it is intended to be as
 * simple and unobtrusive as possible
 */

using UnityEngine;

public class Settings : MonoBehaviour
{
	static Settings instance;

	[Header("Game Object References")]
	public Transform player;

	[Header("Collision Info")]
	public readonly static float PlayerCollisionRadius = .5f;
	public readonly static float EnemyCollisionRadius = .3f;

	public static Vector3 PlayerPosition
	{
		get { return instance.player.position; }
	}

	void Awake()
	{
		if (instance != null && instance != this)
			Destroy(gameObject);
		else
			instance = this;
	}

	public static Vector3 GetPositionAroundPlayer(float radius)
	{
		Vector3 playerPos = instance.player.position;

		float angle = UnityEngine.Random.Range(0f, 2 * Mathf.PI);
		float s = Mathf.Sin(angle);
		float c = Mathf.Cos(angle);
		
		return new Vector3(c * radius, 1.1f, s * radius) + playerPos;
	}

	public static void PlayerDied()
	{
		if (instance.player == null)
			return;

		instance.player = null;
	}

	public static bool IsPlayerDead()
	{
		return instance.player == null;
	}
}
