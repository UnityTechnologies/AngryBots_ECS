# Using ComponentSystem

You can use a ComponentSystem to process your data. ComponentSystem methods run on the main thread and thus don’t take advantage of multiple CPU cores. Use ComponentSystems in the following circumstances:

* Debugging or exploratory development — sometimes it is easier to observe what is going on when the code is running on the main thread. You can, for example, log debug text and draw debug graphics.
* When the system needs to access or interface with other APIs that can only run on the main thread — this can help you gradually convert your game systems to ECS rather than having to rewrite everything from the start.
* The amount of work the system performs is less than the small overhead of creating and scheduling a Job.

__Important:__ Making structural changes forces the completion of all Jobs. This event is called a *sync point* and can lead to a drop in performance because the system cannot take advantage of all the available CPU cores while it waits for the sync point. In a ComponentSystem, you should use a post-update command buffer. The sync point still occurs, but all the structural changes happen in a batch, so it has a slightly lower impact. For maximum efficiency, use a JobComponentSystem and an entity command buffer. When creating a large number of entities, you can also use a separate World to create the entities and then transfer those entities to the main game world.

## Iterating with ForEach delegates

The ComponentSystem provides a Entities.ForEach function that simplifies the task of iterating over a set of entities. Call ForEach in the system’s OnUpdate() function passing in a lambda function that takes the relevant components as parameters and whose function body performs the necessary work.

The following example, from the HelloCube_01_ForEach sample, animates the rotation for any entities that have both a RotationQuaternion and a RotationSpeed component:

    public class RotationSpeedSystem : ComponentSystem
    {
       protected override void OnUpdate()
       {
           Entities.ForEach( (ref RotationSpeed rotationSpeed, ref RotationQuaternion rotation) =>
           {
               var deltaTime = Time.deltaTime;
               rotation.Value = math.mul(math.normalize(rotation.Value),
                   quaternion.AxisAngle(math.up(), rotationSpeed.RadiansPerSecond * deltaTime));
           });
       }

You can use ForEach lambda functions with up to six types of components.

If you need to make structural changes to existing entities, you can add an Entity component to your lambda function parameters and use it to add the commands to the ComponentSystem post-update command buffer. (If you were allowed to make structural changes inside the lambda function, you might change the layout of the data in the arrays you are iterating over, leading to bugs or other undefined behavior.) 

For example, if you wanted to remove the RotationSpeed component form any entities whose rotation speed is currently zero, you could alter your ForEach function as follows:

``` c#
Entities.ForEach( (Entity entity, ref RotationSpeed rotationSpeed, ref RotationQuaternion rotation) =>
{
   var __deltaTime __= Time.deltaTime;
   rotation.Value = math.mul(math.normalize(rotation.Value),
       quaternion.AxisAngle(math.up(), rotationSpeed.RadiansPerSecond * __deltaTime__));
  
   if(math.abs(rotationSpeed.RadiansPerSecond) <= float.Epsilon) //Speed effectively zero
       PostUpdateCommands.RemoveComponent(entity, typeof(RotationSpeed));               
});
```

The system executes the commands in the post-update buffer after the OnUpdate() function is finished.

