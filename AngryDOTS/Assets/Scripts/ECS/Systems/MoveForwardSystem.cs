using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
partial struct MoveForwardSystem : ISystem
{
    [BurstCompile]
    public void OnCreate(ref SystemState state)
    {
        state.RequireForUpdate<MoveSpeed>();
    }

    [BurstCompile]
    public void OnUpdate(ref SystemState state)
    {
        var MoveForwardJob = new MoveForwardJob
		{
            dt = SystemAPI.Time.DeltaTime
        };

		MoveForwardJob.ScheduleParallel();
    }
}

[BurstCompile]
[WithAll(typeof(MoveForward))]
public partial struct MoveForwardJob : IJobEntity //The query for IJobEntity is inferred by the Execute method
{
    public float dt;

    void Execute(in MoveSpeed speed, ref LocalTransform transform)
    {
        transform.Position = transform.Position + dt * speed.Value * math.forward(transform.Rotation);
    }
}