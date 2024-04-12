/* MOVE FORWARD SYSTEM
 * This simple system finds any entity that has a LocalTransform, MoveForward, and MoveSpeed
 * component and moves the entity forward. In this project, the two entity types it will 
 * affect are bullets and enemies
 */

using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]  // Enable Burst compilation
partial struct MoveForwardSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        // If there are no entities with MoveSpeed, this system doesn't need to run
        state.RequireForUpdate<MoveSpeed>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        // Create a MoveForwardJob and tell it the amount of time that has passed
        // since the last time this system updated
        var MoveForwardJob = new MoveForwardJob
		{
            dt = SystemAPI.Time.DeltaTime
        };

        // Schedule this job as multi-threaded. Since we don't pass in a query, the
        // job itself will contain the query
        MoveForwardJob.ScheduleParallel();
    }
}

[BurstCompile]
[WithAll(typeof(MoveForward))]
// One of the reasons IJobEntity is convenient is that it can infer the entity query for you
// based on the arguments of its Execute method. In this case, the job will work on entities
// that have a MoveSpeed and a LocalTransform component. Additionally, since this system should
// only work on entities that specifically need to move forward (as opposed to other types of
// movement), we specify that MoveForward is also required on the line above. We don't put that
// in the Execute method, however, since we don't need to access MoveFoward, we just need to make
// sure it is there. Think of MoveForward like a tag, since it doesn't actually contain any data
public partial struct MoveForwardJob : IJobEntity
{
    public float dt;    // The amount of time that has passed since the last update

    // Execute will run once for each entity that matches the query
    void Execute(in MoveSpeed speed, ref LocalTransform transform)
    {
        // Change the entities position in the forward direction based on its speed and time
        transform.Position = transform.Position + dt * speed.Value * math.forward(transform.Rotation);
    }
}