# AngryBots ECS
The DOTS project used for the presentation Converting Your Game to DOTS and contains an example of how DOTS could be used to replace a low performance process in your games (shooting many bullets at once, in this case). This is meant to provide a simple, targeted example. While it is true that there are other ways to optimize the game object version of this game, those same optimizations would apply to entities as well. As such, this represents a decent comparision of performance as well as example of how to do the conversion. A video presentation of this project can be seen [here](https://www.youtube.com/watch?v=BNMrevfB6Q0).

## The basics
This project uses a combination of game objects and entities together. In this case, the player (game objets) spawns bullets (entity). There is an Enemy Spawner (game object) that spawns enemies (entities).  

## Collision
Since the bullets and enemies are entities and the player is a game object, making them collide with the built in physics system won't work. As such, collision is handled by a system (CollisionSystem) that is very simplistic in nature. In this system general radius is used to calculate if bullets have collided with enemies or enemies with players. A more sophisticted collision algorithm, or even the new DOTS physics, could have been used, but this is meant to be a simple example and this poorly optimized algorithm helps demonstrate the performance capabilities of burst compiled code.  

## The systems
Several systems are used to replace the monobehaviour functionality of the bullets and enemies
* MoveForwardSystem - Moves any entity with the MoveForward component forward at some speed
* TurnTowardsPlayerSystem - Turns enemy entities to face the player's position
* CollisionSystem - Detects simple radius collision between bullets, enemies, and players
* RemoveDeadSystem - Destroys any entities that have a Healt of zero or less
* TimedDestroySystem - Destroys any entities that have run out of time to live (bullets)

## Other Information
There are other interactions in the code that may not be apparent when you first look at it, so they are called out here
* Bullet and Enemy prefabs are baked in the normal way (using a sub-scene and Authoring scripts) however enemy and bullet entities are spawned in the EnemySpawner and PlayerShooting monobehaviours (respectively)
* The PlayerController monobehaviour creates an entity to store the player's health and position. This is so the CollisionSystem can easily check for collisions between enemies and the player, and adjust the player's health accordingly
* The player's position is **also** stored in the Settings monobehaviour so the TurnTowardsPlayer system can access it. This is not normally something you should do, instead deciding on one way or the other. This project does both simply for demonstration purposes
