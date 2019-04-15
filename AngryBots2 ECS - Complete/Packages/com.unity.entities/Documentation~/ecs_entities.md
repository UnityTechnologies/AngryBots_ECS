---
uid: ecs-entities
---
# Entities
<!-- 
> Topics to add
> * Spawning Entities in Jobs -- iEntity Command Buffers
> * Transferring Entities between worlds: EM.MoveEntity
> * Coming soon: Entities with components in sub-worlds
-->

Entities are one of the three principle elements of an Entity Component System architecture. They represent the individual "things" in you game or program. An entity has neither behavior nor data; instead, it identifies which pieces of data belong together. [Systems](ecs_systems.md) provide the behavior. [Components](ecs_components.md) store the data.

An entity is essentially an ID. You can think of it as a super lightweight [GameObject](https://docs.unity3d.com/Manual/class-GameObject.html) that does not even have a name by default. entity ID's are stable. They are the only stable way to store a reference to another component or entity.

An [EntityManager](../api/Unity.Entities.EntityManager.html) manages all of the entities in a [World](../api/Unity.Entities.World.html). An EntityManager maintains the list of entities and organizes the data associated with an entity for optimal performance.

Although an entity does not have a type, groups of entities can be categorized by the types of the data components associated with them. As you create entities and add components to them, the EntityManager keeps track of the unique combinations of components on the existing entities. Such a unique combination is called an _Archetype_. The EntityManager creates an [EntityArchetype](../api/Unity.Entities.EntityArchetype.html) struct as you add components to an entity. You can use existing EntityArchetypes to create new entities conforming to that archetype. You can also create an EntityArchetype in advance and use that to create entities. 

## Creating Entities

The easiest way to create an entity is in the Unity Editor. You can set up both GameObjects placed in a scene and Prefabs to be converted into entities at runtime. For more dynamic parts of your game or program, you can create spawning systems that create multiple entities in a job. Finally, you can create entities one at a time using one of the [EntityManager.CreateEntity](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_CreateEntity) functions.

<!-- This section has been superseded with the conversion workflow
### Creating Entities from GameObjects

**Important:** This workflow, which is dependent on ComponentDataWrapper class is about to be replaced.

On any GameObject in a Scene, place a [GameObjectEntity Component](@Unity.Entities.GameObjectEntity). When this component is present, Unity creates an entity for the GameObject at run time and copies any components that implement ComponentDataWrapper to the entity (as ECS Components.

The Unity.Transform and Unity.HybridRender systems provide wrapped components that you can put on a GameObject. You can also create wrappers for your own components so that they can be used with your custom systems.

See [GameObjects to Entities](gp_prefabs.md) for more information.
-->

### Creating Entities with an EntityManager

Use one of the [EntityManager.CreateEntity](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_CreateEntity) functions to create an entity. The entity is created in the same World as the EntityManager.

You can create entities one-by-one in the following ways:

* Create an entity with components using an array of [ComponentType](../api/Unity.Entities.ComponentType.html) objects.
* Create an entity with components using an [EntityArchetype](../api/Unity.Entities.EntityArchetype.html).
* Copy an existing entity, including its current data, with [Instantiate](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_Instantiate_Unity_Entities_Entity_)
* Create an entity with no components and then add components to it. (You can add components immediately or as additional components are needed.)

You can create multiple entities at a time also:

* Fill a NativeArray with new entities with the same archetype using [CreateEntity](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_CreateEntity_Unity_Entities_EntityArchetype_NativeArray_Unity_Entities_Entity__).
* Fill a NativeArray with copies of an existing entity, including its current data, using [Instantiate](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_Instantiate_Unity_Entities_Entity_NativeArray_Unity_Entities_Entity__).
* Explicitly create Chunks populated with a specified number of entities with a given archetype with [CreateChunk](../api/Unity.Entities.EntityManager.html#Unity_Entities_EntityManager_CreateChunk_Unity_Entities_EntityArchetype_NativeArray_Unity_Entities_ArchetypeChunk__System_Int32_).
    
## Adding and Removing Components

After an entity has been created, you can add or remove components When you do this, the archetype of the affected entities change and the EntityManager must move altered data to a new Chunk of memory, as well as condense the component arrays in the original Chunks. 

Changes to an entity that cause structural changes — that is, adding or removing components changing the values of SharedComponentData, and destroying the entity — cannot be done inside a Job since these could invalidate the data that the Job is working on. Instead, you add the commands to make these types of changes to an [EntityCommandBuffer](../api/Unity.Entities.EntityCommandBuffer.html) and execute this command buffer after the Job is complete.  


The EntityManager provides functions for removing a component from a single entity as well as all of the entities in a NativeArray. See [Components](ecs_components.md) for more information.

## Iterating entities

Iterating over all entities that have a matching set of components, is at the center of the ECS architecture. See [Accessing entity Data](chunk_iteration.md).


