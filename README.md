# AngryBots ECS
The DOTS project used for the presentation Converting Your Game to DOTS and contains an example of how DOTS could be used to replace a low performance process in your games (shooting many bullets at once, in this case). This is meant to provide a simple, targeted example. A video presentation of this project can be seen [here](https://www.youtube.com/watch?v=BNMrevfB6Q0)

## The basics
This project uses a combination of game objects and entities together. In this case, the player (game objets) spawns bullets (entity). There is an Enemy Spawner (game object) that spawns enemies (entities). When enemies die, they spawn particle effects (game objects). 

## Collision
Since the bullets and enemies are entities and the player is a game object, making them collide with the built in physics system won't work. As such, collision is handled by a system (CollisionSystem) and is very simplistic in nature. In this system general radius is used to calculate if bullets have collided with enemies or enemies with players. A more sophisticted collision algorithm, or even the new DOTS physics, could have been used, but this is meant to be a simple example and really isn't necessary. 

## The systems
Several systems are used to replace the monobehaviour functionality of the bullets and enemies
* MoveForwardSystem - Moves any entity with the MoveForward component forward at some speed
* TurnTowardsPlayerSystem - Turns enemy entities to face the player's position
* CollisionSystem - Detects simple radius collision between bullets, enemies, and players
* RemoveDeadSystem - Destroys any entities that have a Healt of zero or less
* TimedDestroySystem - Destroys any entities that have run out of time to live (bullets)
* PlayerTransformUpdateSystem - Due to the hybrid nature of the player game object, this sytem is needed to keep the entities position in sync with the game object's position
